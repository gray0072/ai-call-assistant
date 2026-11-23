using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundRecord;

enum TranscriptionMode { Off, AfterRecording, Live }

/// <summary>User choices made in the tray menu, stored in settings.json next to the exe.</summary>
sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly string DefaultRecordingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sound Records");

    public TranscriptionMode Transcription { get; set; } = TranscriptionMode.AfterRecording;

    /// <summary>Where recordings and transcripts go; null means <see cref="DefaultRecordingsFolder"/>.</summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>UI language code (ru, en, sv); null means the Windows display language.</summary>
    public string? Language { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings load failed: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings save failed: " + ex.Message);
        }
    }
}

static class AppLog
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "SoundRecord.log");
    static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break the app
        }
    }
}
