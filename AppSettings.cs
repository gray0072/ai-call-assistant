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

    public TranscriptionMode Transcription { get; set; } = TranscriptionMode.AfterRecording;

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
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "SoundRecord.log");
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
