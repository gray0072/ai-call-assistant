using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRecord;

enum AudioSource { Speakers, Microphone }

/// <summary>
/// Records the default output device (loopback — the other side of the call)
/// mixed with the default microphone into a single MP3 file.
/// </summary>
sealed class CallRecorder : IDisposable
{
    const int SampleRate = 48000;
    const int Channels = 2;
    const int BitRateKbps = 96; // ~43 MB per hour
    static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(300);

    readonly List<WasapiCapture> _captures = new();
    readonly List<BufferedWaveProvider> _buffers = new();
    readonly List<(AudioSource Kind, ISampleProvider Provider)> _sources = new();
    readonly LameMP3FileWriter _writer;
    readonly Thread _pump;
    volatile bool _running = true;
    volatile bool _paused;
    volatile Action<AudioSource, float[], int>? _tap;
    long _totalFrames;

    public string FilePath { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsPaused => _paused;

    /// <summary>Recorded duration, pauses excluded.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _totalFrames) / SampleRate);

    /// <summary>
    /// Receives exactly what goes into the file, per source, as 48 kHz mono
    /// (the buffer is reused — copy what you need). Called on the recorder thread.
    /// </summary>
    public Action<AudioSource, float[], int>? Tap
    {
        get => _tap;
        set => _tap = value;
    }

    public CallRecorder(string filePath)
    {
        FilePath = filePath;
        var warnings = new List<string>();

        TryAddSource(() => new WasapiLoopbackCapture(), AudioSource.Speakers, "выход (динамики)", warnings);
        TryAddSource(() => new WasapiCapture(), AudioSource.Microphone, "микрофон", warnings);

        if (_captures.Count == 0)
            throw new InvalidOperationException("Не найдено ни одного аудиоустройства.\n" + string.Join("\n", warnings));

        Warnings = warnings;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new LameMP3FileWriter(filePath, new WaveFormat(SampleRate, 16, Channels), BitRateKbps);

        foreach (var c in _captures)
            c.StartRecording();

        _pump = new Thread(Pump) { IsBackground = true, Name = "Mp3 pump" };
        _pump.Start();
    }

    void TryAddSource(Func<WasapiCapture> factory, AudioSource kind, string name, List<string> warnings)
    {
        WasapiCapture? capture = null;
        try
        {
            capture = factory();
            var format = NormalizeFormat(capture.WaveFormat);
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            capture.DataAvailable += (_, e) =>
            {
                if (!_paused)
                    buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            ISampleProvider sp = ToStereo(buffer.ToSampleProvider());
            if (sp.WaveFormat.SampleRate != SampleRate)
                sp = new WdlResamplingSampleProvider(sp, SampleRate);

            _captures.Add(capture);
            _buffers.Add(buffer);
            _sources.Add((kind, sp));
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            warnings.Add($"Не удалось подключить {name}: {ex.Message}");
        }
    }

    /// <summary>WASAPI mix formats are usually WAVE_FORMAT_EXTENSIBLE; map them to plain PCM/float.</summary>
    static WaveFormat NormalizeFormat(WaveFormat f)
    {
        if (f is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)
                return WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels);
            if (ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM)
                return new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);
        }
        return f;
    }

    static ISampleProvider ToStereo(ISampleProvider src) => src.WaveFormat.Channels switch
    {
        1 => new MonoToStereoSampleProvider(src),
        2 => src,
        _ => new DownmixToStereo(src)
    };

    void Pump()
    {
        var clock = Stopwatch.StartNew();
        long written = 0;

        var mix = new float[SampleRate * Channels]; // up to 1 s per step
        var source = new float[mix.Length];
        var mono = new float[SampleRate];
        var pcm = new byte[mix.Length * 2];

        void WriteFrames(int frames)
        {
            int n = frames * Channels;
            Array.Clear(mix, 0, n);
            var tap = _tap;

            foreach (var (kind, provider) in _sources)
            {
                int read = provider.Read(source, 0, n);
                if (read < n)
                    Array.Clear(source, read, n - read);

                for (int i = 0; i < n; i++)
                    mix[i] += source[i];

                if (tap != null)
                {
                    for (int f = 0; f < frames; f++)
                        mono[f] = (source[2 * f] + source[2 * f + 1]) * 0.5f;
                    try { tap(kind, mono, frames); }
                    catch (Exception ex) { AppLog.Write("Audio tap failed: " + ex); }
                }
            }

            for (int i = 0; i < n; i++)
            {
                float v = Math.Clamp(mix[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(v * short.MaxValue));
            }
            _writer.Write(pcm, 0, n * 2);
            Interlocked.Add(ref _totalFrames, frames);
        }

        while (_running)
        {
            Thread.Sleep(50);

            if (_paused)
            {
                foreach (var b in _buffers)
                    b.ClearBuffer();
                clock.Restart();
                written = 0;
                continue;
            }

            // Pull audio at wall-clock pace, a bit behind real time so the buffers have data.
            // Loopback capture delivers nothing during silence — the buffers fill the gaps with zeros.
            long target = (long)((clock.Elapsed - Latency).TotalSeconds * SampleRate);
            while (target - written > 0)
            {
                int frames = (int)Math.Min(target - written, SampleRate);
                WriteFrames(frames);
                written += frames;
            }
        }

        // Captures are stopped by now: flush what is still buffered (the latency tail).
        if (!_paused)
            WriteFrames((int)(Latency.TotalSeconds * SampleRate));
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    public void Stop()
    {
        if (!_running)
            return;

        foreach (var c in _captures)
        {
            try { c.StopRecording(); } catch { /* device may be gone */ }
        }
        _running = false;
        _pump.Join();
        _writer.Flush();
    }

    public void Dispose()
    {
        Stop();
        _writer.Dispose();
        foreach (var c in _captures)
            c.Dispose();
    }

    /// <summary>Folds multichannel (e.g. 5.1) audio into stereo: front L/R plus center.</summary>
    sealed class DownmixToStereo : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly int _inCh;
        float[] _buf = Array.Empty<float>();

        public DownmixToStereo(ISampleProvider src)
        {
            _src = src;
            _inCh = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _inCh;
            if (_buf.Length < need)
                _buf = new float[need];

            int read = _src.Read(_buf, 0, need) / _inCh;
            bool hasCenter = _inCh >= 6;
            for (int i = 0; i < read; i++)
            {
                int s = i * _inCh;
                float c = hasCenter ? _buf[s + 2] * 0.707f : 0f;
                buffer[offset + i * 2] = _buf[s] + c;
                buffer[offset + i * 2 + 1] = _buf[s + 1] + c;
            }
            return read * 2;
        }
    }
}
