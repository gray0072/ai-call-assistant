using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SoundRecord;

/// <summary>
/// Owns the speech models: Whisper (speech → text, GPU via Vulkan with CPU fallback)
/// and sherpa-onnx (voice activity, speaker diarization and speaker embeddings, CPU).
/// </summary>
sealed class SpeechEngine : IDisposable
{
    public const int SampleRate = 16000;
    const float DiarizationThreshold = 0.4f;
    static readonly string[] Languages = { "ru", "en" };
    static readonly int Threads = Math.Max(2, Environment.ProcessorCount / 2);

    // Phrases Whisper is known to invent on silence or noise (it was trained on subtitled videos).
    static readonly Regex Hallucination = new(
        @"субтитр|dimatorzok|продолжение следует|спасибо за просмотр|thanks? (you )?for watching|подписывайтесь на канал",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly IDisposable WhisperLog = Whisper.net.Logger.LogProvider.AddLogger((level, message) =>
    {
        if (level is Whisper.net.Logger.WhisperLogLevel.Error or Whisper.net.Logger.WhisperLogLevel.Warning)
            AppLog.Write($"whisper {level}: {message?.TrimEnd()}");
    });

    readonly object _sync = new();
    readonly SemaphoreSlim _whisperGate = new(1, 1);
    Task? _models, _loading;
    WhisperFactory? _factory;
    volatile WhisperProcessor? _processor;

    /// <summary>Progress of model download/loading; null when idle.</summary>
    public event Action<string?>? StatusChanged;

    public bool IsLoaded => _processor != null;

    public Task EnsureModelsAsync()
    {
        lock (_sync)
        {
            if (_models == null || _models.IsFaulted)
                _models = Task.Run(() => ModelStore.DownloadMissingAsync(s => StatusChanged?.Invoke(s)));
            return _models;
        }
    }

    public Task EnsureLoadedAsync()
    {
        lock (_sync)
        {
            if (_loading == null || _loading.IsFaulted)
                _loading = Task.Run(LoadAsync);
            return _loading;
        }
    }

    async Task LoadAsync()
    {
        try
        {
            await EnsureModelsAsync();
            StatusChanged?.Invoke("Загрузка модели распознавания…");

            var gpu = VulkanGpu.FindDiscrete();
            AppLog.Write(gpu is { } g ? $"Whisper GPU: #{g.Index} {g.Name}" : "Whisper: no discrete GPU, using CPU");
            RuntimeOptions.RuntimeLibraryOrder = gpu != null
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
            _factory = WhisperFactory.FromPath(ModelStore.Whisper, new WhisperFactoryOptions
            {
                UseGpu = gpu != null,
                GpuDevice = gpu?.Index ?? 0
            });
            _processor = _factory.CreateBuilder()
                .WithLanguage("ru")
                .WithNoContext()
                .WithThreads(Threads)
                .Build();

            AppLog.Write($"Whisper loaded, runtime: {RuntimeOptions.LoadedLibrary}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech engine load failed: " + ex);
            throw;
        }
        finally
        {
            StatusChanged?.Invoke(null);
        }
    }

    /// <summary>Transcribes one utterance (16 kHz mono), picking Russian or English for it.</summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        if (samples.Length < SampleRate * 0.3)
            return "";

        await EnsureLoadedAsync();
        var audio = Pad(samples);

        await _whisperGate.WaitAsync(ct);
        try
        {
            var processor = _processor!;
            var (language, _) = processor.DetectLanguageWithProbability(
                audio.AsSpan(0, Math.Min(audio.Length, 30 * SampleRate)), Languages);
            processor.ChangeLanguage(string.IsNullOrEmpty(language) ? "ru" : language);

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, ct))
            {
                var t = segment.Text.Trim();
                if (t.Length == 0 || Hallucination.IsMatch(t) || !t.Any(char.IsLetterOrDigit))
                    continue;
                if (text.Length > 0)
                    text.Append(' ');
                text.Append(t);
            }
            return text.ToString();
        }
        finally
        {
            _whisperGate.Release();
        }
    }

    /// <summary>Whisper ignores input shorter than 1 s; give it some silence around the speech.</summary>
    static float[] Pad(float[] samples)
    {
        int edge = SampleRate / 4;
        int length = Math.Max(samples.Length + 2 * edge, (int)(SampleRate * 1.2));
        var result = new float[length];
        Array.Copy(samples, 0, result, edge, samples.Length);
        return result;
    }

    /// <summary>Who spoke when, over a whole recording (16 kHz mono). Speakers are numbered from 0.</summary>
    public OfflineSpeakerDiarizationSegment[] Diarize(float[] samples, Action<int, int>? progress = null,
        float threshold = DiarizationThreshold)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = ModelStore.Segmentation;
        config.Segmentation.NumThreads = Threads;
        config.Segmentation.Provider = "cpu";
        config.Embedding.Model = ModelStore.Embedding;
        config.Embedding.NumThreads = Threads;
        config.Embedding.Provider = "cpu";
        config.Clustering.NumClusters = -1; // unknown number of speakers
        config.Clustering.Threshold = threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;

        var diarization = new OfflineSpeakerDiarization(config);
        try
        {
            OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
            {
                progress?.Invoke(done, total);
                return 0;
            };
            var result = diarization.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }
        finally
        {
            diarization.Dispose();
        }
    }

    public static SpeakerEmbeddingExtractor CreateEmbeddingExtractor()
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = ModelStore.Embedding;
        config.NumThreads = 2;
        config.Provider = "cpu";
        return new SpeakerEmbeddingExtractor(config);
    }

    public static VoiceActivityDetector CreateVad()
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = ModelStore.Vad;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.3f; // short: in calls people answer each other almost without pauses
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 20f;
        config.SileroVad.WindowSize = VadWindow;
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        return new VoiceActivityDetector(config, 60);
    }

    public const int VadWindow = 512;

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
    }
}

static class Transcript
{
    public static StreamWriter Create(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };

    public static string Line(TimeSpan at, string speaker, string text) =>
        $"[{(int)at.TotalHours:00}:{at:mm\\:ss}] {speaker}: {text}";
}
