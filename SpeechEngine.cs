using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AiCallAssistant;

/// <summary>
/// Owns the speech models: Whisper (speech → text, GPU via Vulkan with CPU fallback)
/// and sherpa-onnx (voice activity, speaker diarization and speaker embeddings, CPU).
/// Whisper is loaded only while someone holds a lease (<see cref="Use"/>) and is unloaded
/// from video memory after <see cref="IdleTimeout"/> without leases.
/// </summary>
sealed class SpeechEngine : IDisposable
{
    public const int SampleRate = 16000;
    public const int VadWindow = 512;
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);
    const float DiarizationThreshold = 0.4f;
    static readonly string[] Languages = { "en", "ru", "sv" };
    static readonly int Threads = Math.Max(2, Environment.ProcessorCount / 2);

    // Phrases Whisper is known to invent on silence or noise (it was trained on subtitled videos).
    static readonly Regex Hallucination = new(
        @"субтитр|dimatorzok|продолжение следует|спасибо за просмотр|thanks? (you )?for watching|подписывайтесь на канал|textning|undertexter|tack för att (du|ni) (tittade|lyssnade)",
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
    WhisperProcessor? _processor;
    int _users;
    System.Threading.Timer? _idleTimer;

    /// <summary>Model download/loading progress (text, fraction 0..1 or null when unknown); text is null when done.</summary>
    public event Action<string?, double?>? StatusChanged;

    public bool ModelsReady => _models is { IsCompletedSuccessfully: true };

    // ---------- lifetime ----------

    /// <summary>Keeps Whisper loaded until the returned lease is disposed.</summary>
    public IDisposable Use()
    {
        lock (_sync)
        {
            _users++;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        return new Lease(this);
    }

    sealed class Lease(SpeechEngine engine) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                engine.Release();
        }
    }

    void Release()
    {
        lock (_sync)
        {
            if (--_users == 0)
                _idleTimer = new System.Threading.Timer(_ => Unload(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    void Unload()
    {
        WhisperProcessor? processor;
        WhisperFactory? factory;
        lock (_sync)
        {
            if (_users > 0)
                return;
            if (_loading is { IsCompleted: false })
            {
                // Still loading for a user who has already gone — try again later.
                _idleTimer = new System.Threading.Timer(_ => Unload(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
                return;
            }
            processor = _processor;
            factory = _factory;
            _processor = null;
            _factory = null;
            _loading = null;
        }

        if (processor == null)
            return;
        processor.Dispose();
        factory?.Dispose();
        AppLog.Write("Whisper unloaded (idle)");
    }

    public Task EnsureModelsAsync()
    {
        lock (_sync)
        {
            if (_models == null || _models.IsFaulted)
                _models = Task.Run(() => ModelStore.DownloadMissingAsync((s, f) => StatusChanged?.Invoke(s, f)));
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
            StatusChanged?.Invoke(Strings.Current.LoadingModel, null);

            var gpu = VulkanGpu.FindDiscrete();
            AppLog.Write(gpu is { } g ? $"Whisper GPU: #{g.Index} {g.Name}" : "Whisper: no discrete GPU, using CPU");
            RuntimeOptions.RuntimeLibraryOrder = gpu != null
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
            var factory = WhisperFactory.FromPath(ModelStore.Whisper, new WhisperFactoryOptions
            {
                UseGpu = gpu != null,
                GpuDevice = gpu?.Index ?? 0
            });
            var processor = factory.CreateBuilder()
                .WithLanguage("ru")
                .WithNoContext()
                .WithThreads(Threads)
                .Build();

            lock (_sync)
            {
                _factory = factory;
                _processor = processor;
            }
            AppLog.Write($"Whisper loaded, runtime: {RuntimeOptions.LoadedLibrary}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Speech engine load failed: " + ex);
            throw;
        }
        finally
        {
            StatusChanged?.Invoke(null, null);
        }
    }

    // ---------- recognition ----------

    /// <summary>Transcribes one utterance (16 kHz mono), picking English, Russian or Swedish for it. Hold a lease while calling.</summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        if (samples.Length < SampleRate * 0.3)
            return "";

        await EnsureLoadedAsync();
        var audio = Pad(samples);

        await _whisperGate.WaitAsync(ct);
        try
        {
            var processor = _processor ?? throw new InvalidOperationException(Strings.Current.ModelUnloaded);
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
    public static OfflineSpeakerDiarizationSegment[] Diarize(float[] samples, Action<int, int>? progress = null,
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

    /// <summary>Speech segments (in seconds) of a whole single-speaker track.</summary>
    public static List<(float Start, float End)> DetectSpeech(float[] samples)
    {
        var result = new List<(float, float)>();
        var vad = CreateVad();
        try
        {
            void Drain()
            {
                while (!vad.IsEmpty())
                {
                    var s = vad.Front();
                    result.Add(((float)s.Start / SampleRate, (float)(s.Start + s.Samples.Length) / SampleRate));
                    vad.Pop();
                }
            }

            var window = new float[VadWindow];
            for (int i = 0; i + VadWindow <= samples.Length; i += VadWindow)
            {
                Array.Copy(samples, i, window, 0, VadWindow);
                vad.AcceptWaveform(window);
                Drain();
            }
            vad.Flush();
            Drain();
        }
        finally
        {
            vad.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Voice print of one speaker over a whole track: the normalized, length-weighted average of the
    /// embeddings of their longest phrases. Null if no phrase is long enough.
    /// </summary>
    public static float[]? VoicePrintOf(float[] samples, IEnumerable<(float Start, float End)> segments)
    {
        var picked = segments
            .Where(s => s.End - s.Start >= 1.0f)
            .OrderByDescending(s => s.End - s.Start)
            .Take(30)
            .ToList();
        if (picked.Count == 0)
            return null;

        using var extractor = CreateEmbeddingExtractor();
        float[]? sum = null;
        foreach (var (start, end) in picked)
        {
            int from = Math.Max(0, (int)(start * SampleRate));
            int to = Math.Min(samples.Length, (int)(end * SampleRate));
            if (to - from < SampleRate)
                continue;

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, samples[from..to]);
            stream.InputFinished();
            if (!extractor.IsReady(stream))
                continue;
            var embedding = extractor.Compute(stream);
            VoicePrint.Normalize(embedding);

            float weight = Math.Min(end - start, 10f);
            sum ??= new float[embedding.Length];
            for (int i = 0; i < sum.Length; i++)
                sum[i] += embedding[i] * weight;
        }

        if (sum != null)
            VoicePrint.Normalize(sum);
        return sum;
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

    public void Dispose()
    {
        lock (_sync)
        {
            _idleTimer?.Dispose();
            _processor?.Dispose();
            _factory?.Dispose();
            _processor = null;
            _factory = null;
        }
    }
}

/// <summary>48 kHz → 16 kHz by averaging every three samples (good enough for speech models).</summary>
sealed class Downsampler
{
    float _acc;
    int _count;

    /// <returns>Number of samples written to <paramref name="output"/> (needs count / 3 + 1 room).</returns>
    public int Process(float[] input, int count, float[] output)
    {
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            _acc += input[i];
            if (++_count < 3)
                continue;
            output[written++] = _acc / 3;
            _acc = 0;
            _count = 0;
        }
        return written;
    }
}
