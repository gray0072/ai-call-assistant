using System.Threading.Channels;
using SherpaOnnx;

namespace SoundRecord;

/// <summary>
/// Draft transcript written while recording. The microphone and the speakers are handled separately:
/// microphone speech is labelled "Я", speaker output is split into speakers by voice prints.
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

    /// <summary>Per-source state; touched only by the recorder thread (and by CompleteAsync after it stops).</summary>
    sealed class SourceState
    {
        public readonly Downsampler Down = new();
        public readonly float[] Window = new float[SpeechEngine.VadWindow];
        public int WindowFill;
        public readonly List<float> Backlog = new(); // audio received before the models are available
        public VoiceActivityDetector? Vad;
    }

    readonly SpeechEngine _engine;
    readonly Action<string> _onError;
    readonly Dictionary<AudioSource, SourceState> _sources = new()
    {
        [AudioSource.Speakers] = new(),
        [AudioSource.Microphone] = new()
    };
    readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    readonly Task _worker;
    float[] _down = new float[16000];

    SpeakerEmbeddingExtractor? _extractor;
    readonly List<(string Name, float[] Print)> _speakers = new(); // normalized voice print per speaker
    string? _lastRemote;

    public string FilePath { get; }

    public LiveTranscriber(SpeechEngine engine, string transcriptPath, Action<string> onError)
    {
        _engine = engine;
        _onError = onError;
        FilePath = transcriptPath;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Called by the recorder with 48 kHz mono audio of one source.</summary>
    public void Feed(AudioSource source, float[] mono48k, int count)
    {
        if (_down.Length < count / 3 + 1)
            _down = new float[count / 3 + 1];

        var s = _sources[source];
        int n = s.Down.Process(mono48k, count, _down);
        for (int i = 0; i < n; i++)
        {
            s.Window[s.WindowFill++] = _down[i];
            if (s.WindowFill == s.Window.Length)
            {
                SubmitWindow(source, s);
                s.WindowFill = 0;
            }
        }
    }

    void SubmitWindow(AudioSource source, SourceState s)
    {
        if (s.Vad == null && _engine.ModelsReady)
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
            await _engine.EnsureModelsAsync();
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

    /// <summary>The recording did not start: stop the worker and remove the empty transcript.</summary>
    public void Abandon()
    {
        _jobs.Writer.TryComplete();
        _worker.ContinueWith(_ =>
        {
            try { File.Delete(FilePath); } catch { /* best effort */ }
        });
    }

    async Task WorkAsync()
    {
        // Whisper stays loaded for as long as the live session runs.
        using var lease = _engine.Use();
        using var writer = Transcript.Create(FilePath);
        try
        {
            await _engine.EnsureLoadedAsync();
            _extractor = SpeechEngine.CreateEmbeddingExtractor();
        }
        catch (Exception ex)
        {
            AppLog.Write("Live transcription unavailable: " + ex);
            _onError("Онлайн-распознавание недоступно: " + ex.GetBaseException().Message);
            writer.WriteLine("(распознавание недоступно: " + ex.GetBaseException().Message + ")");
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
                    _onError(ex.GetBaseException().Message);
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
