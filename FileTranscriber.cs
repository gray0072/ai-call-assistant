using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRecord;

/// <summary>
/// Accurate transcription of a finished recording. With separate tracks the microphone becomes "Я"
/// and only the speakers track is split into voices; a plain audio file is split into voices as a whole.
/// </summary>
static class FileTranscriber
{
    const double MaxGapSeconds = 1.5;   // join turns of the same speaker separated by a short pause
    const double MaxChunkSeconds = 45;
    const double MarginSeconds = 0.15;
    const string Me = "Я";

    // Share of the progress bar taken by the preparation (reading, voice separation) — the rest is recognition.
    const double PrepareShare = 0.25;

    sealed record Utterance(float Start, float End, string Speaker, float[] Source);

    /// <param name="progress">Stage text and overall fraction 0..1 (null while unknown).</param>
    /// <returns>Path of the written .txt transcript.</returns>
    public static async Task<string> RunAsync(SpeechEngine engine, string audioPath,
        Action<string, double?> progress, CallTracks? tracks = null, CancellationToken ct = default)
    {
        using var lease = engine.Use();
        var outPath = Path.ChangeExtension(audioPath, ".txt");
        var tempPath = outPath + ".tmp";

        progress("загрузка модели", null);
        await engine.EnsureLoadedAsync();

        progress("чтение записи", 0);
        var utterances = tracks != null && File.Exists(tracks.MicPath) && File.Exists(tracks.SpeakersPath)
            ? await Task.Run(() => FromTracks(tracks, progress), ct)
            : await Task.Run(() => FromMix(audioPath, progress), ct);

        double total = Math.Max(1, utterances.Sum(u => u.End - u.Start));
        double done = 0;
        var speakerNames = new Dictionary<string, string>();

        try
        {
            await WriteAsync();
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }

        File.Move(tempPath, outPath, overwrite: true);
        progress("готово", 1);
        return outPath;

        async Task WriteAsync()
        {
            using var writer = Transcript.Create(tempPath);
            foreach (var u in utterances)
            {
                ct.ThrowIfCancellationRequested();
                progress("распознавание", PrepareShare + (1 - PrepareShare) * done / total);
                done += u.End - u.Start;

                int from = Math.Max(0, (int)((u.Start - MarginSeconds) * SpeechEngine.SampleRate));
                int to = Math.Min(u.Source.Length, (int)((u.End + MarginSeconds) * SpeechEngine.SampleRate));
                if (to <= from)
                    continue;

                var text = await engine.TranscribeAsync(u.Source[from..to], ct);
                if (text.Length == 0)
                    continue;

                // Other people are numbered in order of their first words.
                if (!speakerNames.TryGetValue(u.Speaker, out var label))
                    speakerNames[u.Speaker] = label = u.Speaker == Me ? Me : $"Спикер {speakerNames.Count(p => p.Key != Me) + 1}";

                writer.WriteLine(Transcript.Line(TimeSpan.FromSeconds(u.Start), label, text));
            }

            if (speakerNames.Count == 0)
                writer.WriteLine("(речь не найдена)");
        }
    }

    static List<Utterance> FromTracks(CallTracks tracks, Action<string, double?> progress)
    {
        var mic = LoadMono16k(tracks.MicPath);
        var speakers = LoadMono16k(tracks.SpeakersPath);

        progress("поиск речи на микрофоне", 0.02);
        var result = SpeechEngine.DetectSpeech(mic)
            .Select(s => new Utterance(s.Start, s.End, Me, mic))
            .ToList();

        result.AddRange(SeparateVoices(speakers, progress));
        return result.OrderBy(u => u.Start).ToList();
    }

    static List<Utterance> FromMix(string audioPath, Action<string, double?> progress) =>
        SeparateVoices(LoadMono16k(audioPath), progress);

    static List<Utterance> SeparateVoices(float[] samples, Action<string, double?> progress)
    {
        if (samples.Length < SpeechEngine.SampleRate)
            return new();

        progress("разделение голосов", 0.05);
        var segments = SpeechEngine.Diarize(samples,
            (done, total) => progress("разделение голосов", 0.05 + (PrepareShare - 0.05) * done / Math.Max(total, 1)));

        var turns = new List<(float Start, float End, int Speaker)>();
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (turns.Count > 0)
            {
                var last = turns[^1];
                if (last.Speaker == s.Speaker && s.Start - last.End < MaxGapSeconds && s.End - last.Start < MaxChunkSeconds)
                {
                    turns[^1] = (last.Start, Math.Max(last.End, s.End), last.Speaker);
                    continue;
                }
            }
            turns.Add((s.Start, s.End, s.Speaker));
        }

        return turns.Select(t => new Utterance(t.Start, t.End, "#" + t.Speaker, samples)).ToList();
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
