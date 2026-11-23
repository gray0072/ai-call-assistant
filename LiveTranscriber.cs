using System.Threading.Channels;
using SherpaOnnx;

namespace SoundRecord;

/// <summary>
/// Transcribes while recording. The microphone and the speakers are handled separately:
/// microphone speech is labelled "Я", speaker output is split into speakers by voice embeddings.
/// Every recognized phrase is appended to the transcript file right away.
/// </summary>
sealed class LiveTranscriber
{
    // Cosine similarity of voice prints. Measured on a compressed call recording:
    // same speaker 0.77–0.95, different speakers 0.50–0.91 (median 0.70).
    // Each speaker keeps the voice print of their first long phrase: averaging in later phrases
    // let other voices leak in and the print drifted until it matched everyone.
    const float SpeakerThreshold = 0.78f;
    const string Me = "Я";

    sealed record Job(AudioSource Source, long StartSample, float[] Samples);

    /// <summary>Per-source state; touched only by the recorder pump thread (and by CompleteAsync after it stops).</summary>
    sealed class SourceState
    {
        public float Acc;
        public int AccCount;
        public readonly float[] Window = new float[SpeechEngine.VadWindow];
        public int WindowFill;
        public readonly List<float> Backlog = new(); // audio received before the VAD model is available
        public VoiceActivityDetector? Vad;
    }

    readonly SpeechEngine _engine;
    readonly string _path;
    readonly Dictionary<AudioSource, SourceState> _sources = new()
    {
        [AudioSource.Speakers] = new(),
        [AudioSource.Microphone] = new()
    };
    readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    readonly Task _worker;

    SpeakerEmbeddingExtractor? _extractor;
    readonly List<(string Name, float[] Print)> _speakers = new(); // normalized voice print per speaker
    string? _lastRemote;

    public string FilePath => _path;

    public LiveTranscriber(SpeechEngine engine, string transcriptPath)
    {
        _engine = engine;
        _path = transcriptPath;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Called by the recorder with 48 kHz mono audio of one source.</summary>
    public void Feed(AudioSource source, float[] mono48k, int count)
    {
        var s = _sources[source];
        for (int i = 0; i < count; i++)
        {
            // 48 kHz → 16 kHz: average every three samples.
            s.Acc += mono48k[i];
            if (++s.AccCount < 3)
                continue;
            s.Window[s.WindowFill++] = s.Acc / 3;
            s.Acc = 0;
            s.AccCount = 0;
            if (s.WindowFill == s.Window.Length)
            {
                SubmitWindow(source, s);
                s.WindowFill = 0;
            }
        }
    }

    void SubmitWindow(AudioSource source, SourceState s)
    {
        if (s.Vad == null && _engine.IsLoaded)
            StartVad(source, s);

        if (s.Vad == null)
        {
            s.Backlog.AddRange(s.Window);
            return;
        }

        s.Vad.AcceptWaveform(s.Window);
        Drain(source, s);
    }

    void StartVad(AudioSource source, SourceState s)
    {
        s.Vad = SpeechEngine.CreateVad();
        var window = new float[SpeechEngine.VadWindow];
        for (int i = 0; i + window.Length <= s.Backlog.Count; i += window.Length)
        {
            s.Backlog.CopyTo(i, window, 0, window.Length);
            s.Vad.AcceptWaveform(window);
            Drain(source, s);
        }
        s.Backlog.Clear();
        s.Backlog.TrimExcess();
    }

    void Drain(AudioSource source, SourceState s)
    {
        while (!s.Vad!.IsEmpty())
        {
            var segment = s.Vad.Front();
            _jobs.Writer.TryWrite(new Job(source, segment.Start, segment.Samples));
            s.Vad.Pop();
        }
    }

    /// <summary>Call after the recorder has stopped: finishes pending phrases and closes the file.</summary>
    public async Task CompleteAsync()
    {
        try
        {
            await _engine.EnsureLoadedAsync();
            foreach (var (source, s) in _sources)
            {
                if (s.Vad == null)
                    StartVad(source, s);
                if (s.WindowFill > 0)
                {
                    Array.Clear(s.Window, s.WindowFill, s.Window.Length - s.WindowFill);
                    s.Vad!.AcceptWaveform(s.Window);
                }
                s.Vad!.Flush();
                Drain(source, s);
            }
        }
        finally
        {
            _jobs.Writer.TryComplete();
            await _worker;
            foreach (var s in _sources.Values)
                s.Vad?.Dispose();
        }
    }

    async Task WorkAsync()
    {
        using var writer = Transcript.Create(_path);
        try
        {
            await _engine.EnsureLoadedAsync();
            _extractor = SpeechEngine.CreateEmbeddingExtractor();
        }
        catch (Exception ex)
        {
            AppLog.Write("Live transcription unavailable: " + ex.Message);
            writer.WriteLine("(распознавание недоступно: " + ex.Message + ")");
            await foreach (var _ in _jobs.Reader.ReadAllAsync()) { } // let the recorder keep going
            return;
        }

        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync())
            {
                try
                {
                    var text = await _engine.TranscribeAsync(job.Samples);
                    if (text.Length == 0)
                        continue;

                    var speaker = job.Source == AudioSource.Microphone ? Me : IdentifySpeaker(job.Samples);
                    var at = TimeSpan.FromSeconds((double)job.StartSample / SpeechEngine.SampleRate);
                    writer.WriteLine(Transcript.Line(at, speaker, text));
                }
                catch (Exception ex)
                {
                    AppLog.Write("Live transcription error: " + ex);
                }
            }
        }
        finally
        {
            _extractor.Dispose();
        }
    }

    string IdentifySpeaker(float[] samples)
    {
        float[]? embedding = null;
        if (samples.Length >= SpeechEngine.SampleRate)
        {
            using var stream = _extractor!.CreateStream();
            stream.AcceptWaveform(SpeechEngine.SampleRate, samples);
            stream.InputFinished();
            if (_extractor.IsReady(stream))
                embedding = _extractor.Compute(stream);
        }

        bool reliable = samples.Length >= 2 * SpeechEngine.SampleRate;
        if (embedding == null)
            return _lastRemote ?? "Собеседник";

        Normalize(embedding);
        int best = -1;
        float bestScore = -1;
        for (int i = 0; i < _speakers.Count; i++)
        {
            float score = Dot(embedding, _speakers[i].Print);
            if (score > bestScore)
                (best, bestScore) = (i, score);
        }

        string name;
        if (best >= 0 && bestScore >= SpeakerThreshold)
        {
            name = _speakers[best].Name;
        }
        else if (!reliable && _lastRemote != null)
        {
            // A short phrase gives a poor voice print: rather keep the previous speaker than invent a new one.
            name = _lastRemote;
        }
        else
        {
            name = $"Спикер {_speakers.Count + 1}";
            _speakers.Add((name, embedding));
        }

        _lastRemote = name;
        return name;
    }

    static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (int i = 0; i < v.Length; i++)
                v[i] /= norm;
    }

    /// <summary>Cosine similarity of two normalized vectors.</summary>
    static float Dot(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }
}
