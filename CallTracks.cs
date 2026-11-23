using NAudio.Wave;

namespace SoundRecord;

/// <summary>
/// Microphone and speakers recorded as two separate 16 kHz mono WAV files in the temp folder,
/// so the transcript can tell "me" from the others. Deleted once the transcript is done.
/// </summary>
sealed class CallTracks
{
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "SoundRecord");

    readonly WaveFileWriter _mic, _speakers;
    readonly Downsampler _micDown = new(), _speakersDown = new();
    float[] _buffer = new float[16000];
    bool _closed;

    public string MicPath { get; }
    public string SpeakersPath { get; }

    public CallTracks(string name)
    {
        Directory.CreateDirectory(Dir);
        MicPath = Path.Combine(Dir, name + ".mic.wav");
        SpeakersPath = Path.Combine(Dir, name + ".speakers.wav");
        var format = new WaveFormat(SpeechEngine.SampleRate, 16, 1);
        _mic = new WaveFileWriter(MicPath, format);
        _speakers = new WaveFileWriter(SpeakersPath, format);
    }

    /// <summary>Called on the recorder thread with 48 kHz mono audio of one source.</summary>
    public void Feed(AudioSource source, float[] mono48k, int count)
    {
        if (_buffer.Length < count / 3 + 1)
            _buffer = new float[count / 3 + 1];

        var (writer, down) = source == AudioSource.Microphone ? (_mic, _micDown) : (_speakers, _speakersDown);
        int n = down.Process(mono48k, count, _buffer);
        writer.WriteSamples(_buffer, 0, n);
    }

    /// <summary>Finishes the WAV files; call after the recorder has stopped.</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _mic.Dispose();
        _speakers.Dispose();
    }

    public void Delete()
    {
        Close();
        TryDelete(MicPath);
        TryDelete(SpeakersPath);
    }

    /// <summary>Removes tracks left by a previous run that was closed before transcribing them.</summary>
    public static void DeleteLeftovers()
    {
        if (!Directory.Exists(Dir))
            return;
        foreach (var file in Directory.EnumerateFiles(Dir, "*.wav"))
            TryDelete(file);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { AppLog.Write($"Cannot delete {path}: {ex.Message}"); }
    }
}
