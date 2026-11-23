using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AiCallAssistant;

/// <summary>
/// Accurate transcription of a finished recording. With separate tracks the microphone becomes "me"
/// and only the speakers track is split into voices; a plain audio file is split into voices as a whole.
/// </summary>
/// <summary>An accurate transcript: phrases and a voice print per speaker key.</summary>
sealed record TranscriptResult(List<Utterance> Lines, Dictionary<string, float[]> Prints);

static class FileTranscriber
{
    const double MaxGapSeconds = 1.5;   // join turns of the same speaker separated by a short pause
    const double MaxChunkSeconds = 45;
    const double MarginSeconds = 0.15;
    const string Me = "@me"; // speaker key of the microphone track

    // Share of the progress bar taken by the preparation (reading, voice separation) — the rest is recognition.
    const double PrepareShare = 0.25;

    sealed record Turn(float Start, float End, string Speaker, float[] Source); // a voice turn before recognition

    /// <summary>Voice prints by speaker key of the turns ("@me", "#k" of the diarization).</summary>
    sealed class Prints : Dictionary<string, float[]>;

    /// <param name="progress">Stage text and overall fraction 0..1 (null while unknown).</param>
    /// <returns>
    /// Phrases in time order, speakers "@me" and "#N" numbered in order of their first words,
    /// and the voice print of each speaker that has one.
    /// </returns>
    public static async Task<TranscriptResult> RunAsync(SpeechEngine engine, string audioPath,
        Action<string, double?> progress, CallTracks? tracks = null, CancellationToken ct = default)
    {
        using var lease = engine.Use();
        var s = Strings.Current;

        progress(s.StageLoadingModel, null);
        await engine.EnsureLoadedAsync();

        progress(s.StageReading, 0);
        var prints = new Prints();
        var utterances = tracks != null && File.Exists(tracks.MicPath) && File.Exists(tracks.SpeakersPath)
            ? await Task.Run(() => FromTracks(tracks, progress, s, prints), ct)
            : await Task.Run(() => FromMix(audioPath, progress, s, prints), ct);

        double total = Math.Max(1, utterances.Sum(u => u.End - u.Start));
        double done = 0;
        var keys = new Dictionary<string, string>();
        var result = new List<Utterance>();

        foreach (var u in utterances)
        {
            ct.ThrowIfCancellationRequested();
            progress(s.StageRecognizing, PrepareShare + (1 - PrepareShare) * done / total);
            done += u.End - u.Start;

            int from = Math.Max(0, (int)((u.Start - MarginSeconds) * SpeechEngine.SampleRate));
            int to = Math.Min(u.Source.Length, (int)((u.End + MarginSeconds) * SpeechEngine.SampleRate));
            if (to <= from)
                continue;

            var text = await engine.TranscribeAsync(u.Source[from..to], ct);
            if (text.Length == 0)
                continue;

            // Other people are numbered in order of their first words.
            if (!keys.TryGetValue(u.Speaker, out var key))
                keys[u.Speaker] = key = u.Speaker == Me ? CallSession.MeKey : "#" + (keys.Count(p => p.Key != Me) + 1);

            result.Add(new(TimeSpan.FromSeconds(u.Start), TimeSpan.FromSeconds(u.End), key, text));
        }

        progress(s.StageDone, 1);
        var resultPrints = new Dictionary<string, float[]>();
        foreach (var (turnKey, key) in keys)
            if (prints.TryGetValue(turnKey, out var print))
                resultPrints[key] = print;
        return new TranscriptResult(result, resultPrints);
    }

    static List<Turn> FromTracks(CallTracks tracks, Action<string, double?> progress, Strings s, Prints prints)
    {
        var mic = LoadMono16k(tracks.MicPath);
        var speakers = LoadMono16k(tracks.SpeakersPath);

        progress(s.StageMicSpeech, 0.02);
        var speech = SpeechEngine.DetectSpeech(mic);
        var result = speech.Select(x => new Turn(x.Start, x.End, Me, mic)).ToList();
        if (SpeechEngine.VoicePrintOf(mic, speech) is { } myPrint)
            prints[Me] = myPrint;

        result.AddRange(SeparateVoices(speakers, progress, s, prints));
        return result.OrderBy(u => u.Start).ToList();
    }

    static List<Turn> FromMix(string audioPath, Action<string, double?> progress, Strings s, Prints prints) =>
        SeparateVoices(LoadMono16k(audioPath), progress, s, prints);

    static List<Turn> SeparateVoices(float[] samples, Action<string, double?> progress, Strings s, Prints prints)
    {
        if (samples.Length < SpeechEngine.SampleRate)
            return new();

        progress(s.StageVoices, 0.05);
        var segments = SpeechEngine.Diarize(samples,
            (done, total) => progress(s.StageVoices, 0.05 + (PrepareShare - 0.05) * done / Math.Max(total, 1)));

        var turns = new List<(float Start, float End, int Speaker)>();
        foreach (var seg in segments.OrderBy(x => x.Start))
        {
            if (turns.Count > 0)
            {
                var last = turns[^1];
                if (last.Speaker == seg.Speaker && seg.Start - last.End < MaxGapSeconds && seg.End - last.Start < MaxChunkSeconds)
                {
                    turns[^1] = (last.Start, Math.Max(last.End, seg.End), last.Speaker);
                    continue;
                }
            }
            turns.Add((seg.Start, seg.End, seg.Speaker));
        }

        // A print per voice from phrases nobody else talks over; if there are none, from all its phrases.
        foreach (var voice in segments.GroupBy(x => x.Speaker))
        {
            var clean = voice
                .Where(a => !segments.Any(b => b.Speaker != a.Speaker && b.Start < a.End && a.Start < b.End))
                .Select(a => (a.Start, a.End));
            if ((SpeechEngine.VoicePrintOf(samples, clean) ?? SpeechEngine.VoicePrintOf(samples, voice.Select(a => (a.Start, a.End)))) is { } print)
                prints["#" + voice.Key] = print;
        }

        return turns.Select(t => new Turn(t.Start, t.End, "#" + t.Speaker, samples)).ToList();
    }

    public static float[] LoadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader.WaveFormat.Channels switch
        {
            1 => reader,
            2 => new StereoToMonoSampleProvider(reader),
            _ => new AverageChannels(reader)
        };
        if (sp.WaveFormat.SampleRate != SpeechEngine.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SpeechEngine.SampleRate);

        var result = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * SpeechEngine.SampleRate + 16000));
        var buffer = new float[SpeechEngine.SampleRate * 10];
        int read;
        while ((read = sp.Read(buffer, 0, buffer.Length)) > 0)
            result.AddRange(buffer.AsSpan(0, read));
        return result.ToArray();
    }

    sealed class AverageChannels(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _buffer = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _channels;
            if (_buffer.Length < need)
                _buffer = new float[need];
            int frames = source.Read(_buffer, 0, need) / _channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++)
                    sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }
}
