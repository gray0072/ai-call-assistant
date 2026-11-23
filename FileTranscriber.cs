using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRecord;

/// <summary>Transcribes a finished recording: diarization over the whole file, then Whisper per speaker turn.</summary>
static class FileTranscriber
{
    const double MaxGapSeconds = 1.5;   // join turns of the same speaker separated by a short pause
    const double MaxChunkSeconds = 45;
    const double MarginSeconds = 0.15;

    /// <returns>Path of the written .txt transcript.</returns>
    public static async Task<string> RunAsync(SpeechEngine engine, string audioPath, Action<string> status,
        CancellationToken ct = default)
    {
        var name = Path.GetFileName(audioPath);
        var outPath = Path.ChangeExtension(audioPath, ".txt");

        await engine.EnsureLoadedAsync();

        status($"{name}: чтение файла");
        var samples = await Task.Run(() => LoadMono16k(audioPath), ct);

        status($"{name}: разделение голосов");
        var segments = await Task.Run(() => engine.Diarize(samples,
            (done, total) => status($"{name}: разделение голосов {done * 100 / Math.Max(total, 1)}%")), ct);

        var turns = MergeTurns(segments.Select(s => (s.Start, s.End, s.Speaker)));
        var speakerNames = new Dictionary<int, string>();

        using var writer = Transcript.Create(outPath);
        for (int i = 0; i < turns.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status($"{name}: распознавание {i * 100 / turns.Count}%");

            var (start, end, speaker) = turns[i];
            int from = Math.Max(0, (int)((start - MarginSeconds) * SpeechEngine.SampleRate));
            int to = Math.Min(samples.Length, (int)((end + MarginSeconds) * SpeechEngine.SampleRate));
            if (to <= from)
                continue;

            var text = await engine.TranscribeAsync(samples[from..to], ct);
            if (text.Length == 0)
                continue;

            // Number speakers in order of their first words.
            if (!speakerNames.TryGetValue(speaker, out var label))
                speakerNames[speaker] = label = $"Спикер {speakerNames.Count + 1}";

            writer.WriteLine(Transcript.Line(TimeSpan.FromSeconds(start), label, text));
        }

        if (turns.Count == 0)
            writer.WriteLine("(речь не найдена)");

        return outPath;
    }

    static List<(float Start, float End, int Speaker)> MergeTurns(IEnumerable<(float Start, float End, int Speaker)> segments)
    {
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
            turns.Add(s);
        }
        return turns;
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
