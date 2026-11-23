using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundRecord;

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
    readonly LameMP3FileWriter _writer;
    readonly IWaveProvider _output;
    readonly Thread _pump;
    volatile bool _running = true;
    volatile bool _paused;

    public string FilePath { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsPaused => _paused;

    /// <summary>Recorded duration, pauses excluded.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _totalFrames) / SampleRate);
    long _totalFrames;

    public CallRecorder(string filePath)
    {
        FilePath = filePath;
        var warnings = new List<string>();

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true
        };

        TryAddSource(() => new WasapiLoopbackCapture(), "выход (динамики)", mixer, warnings);
        TryAddSource(() => new WasapiCapture(), "микрофон", mixer, warnings);

        if (_captures.Count == 0)
            throw new InvalidOperationException("Не найдено ни одного аудиоустройства.\n" + string.Join("\n", warnings));

        Warnings = warnings;
        _output = new SampleToWaveProvider16(mixer);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new LameMP3FileWriter(filePath, _output.WaveFormat, BitRateKbps);

        foreach (var c in _captures)
            c.StartRecording();

        _pump = new Thread(Pump) { IsBackground = true, Name = "Mp3 pump" };
        _pump.Start();
    }

    void TryAddSource(Func<WasapiCapture> factory, string name, MixingSampleProvider mixer, List<string> warnings)
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
            mixer.AddMixerInput(sp);

            _captures.Add(capture);
            _buffers.Add(buffer);
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
        var bytesPerFrame = _output.WaveFormat.BlockAlign;
        var buffer = new byte[SampleRate * bytesPerFrame]; // up to 1 s per iteration

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
            // Loopback capture delivers nothing during silence — the mixer fills the gaps with zeros.
            long target = (long)((clock.Elapsed - Latency).TotalSeconds * SampleRate);
            while (target - written > 0)
            {
                int frames = (int)Math.Min(target - written, SampleRate);
                int bytes = _output.Read(buffer, 0, frames * bytesPerFrame);
                _writer.Write(buffer, 0, bytes);
                written += frames;
                Interlocked.Add(ref _totalFrames, frames);
            }
        }

        // Captures are stopped by now: flush what is still buffered (the latency tail).
        if (!_paused)
        {
            int tail = (int)(Latency.TotalSeconds * SampleRate) * bytesPerFrame;
            int bytes = _output.Read(buffer, 0, Math.Min(tail, buffer.Length));
            _writer.Write(buffer, 0, bytes);
        }
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
