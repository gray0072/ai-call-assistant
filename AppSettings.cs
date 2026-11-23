using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCallAssistant;

enum TranscriptionMode { Off, AfterRecording, Live }

enum LlmProvider { LmStudio, OpenRouter }

/// <summary>User choices made in the tray menu and the windows, stored in settings.json next to the exe.</summary>
sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly string DefaultRecordingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI Call Assistant");

    public TranscriptionMode Transcription { get; set; } = TranscriptionMode.Live;

    /// <summary>Where recordings and transcripts go; null means <see cref="DefaultRecordingsFolder"/>.</summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>UI language code (en, ru, sv); null means the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>The user's own name, shown instead of "Me" once given.</summary>
    public string? MyName { get; set; }

    /// <summary>Project of the next call; null — none.</summary>
    public string? Project { get; set; }

    /// <summary>Known projects, most recently used first.</summary>
    public List<ProjectInfo> Projects { get; set; } = new();

    public AssistantSettings Assistant { get; set; } = new();

    public ProjectInfo? FindProject(string? name) =>
        name == null ? null : Projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Makes <paramref name="name"/> the current project (creating it if new) and moves it to the top.</summary>
    public ProjectInfo? UseProject(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var project = FindProject(name);
        if (name != null && project == null)
            project = new ProjectInfo { Name = name };
        if (project != null)
        {
            Projects.Remove(project);
            Projects.Insert(0, project);
        }
        Project = project?.Name;
        return project;
    }

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

/// <summary>A project groups calls: its recordings go to a subfolder, its notes give the assistant context.</summary>
sealed class ProjectInfo
{
    public string Name { get; set; } = "";

    /// <summary>Free text the assistant gets as background: goals, glossary, who is who.</summary>
    public string? Notes { get; set; }

    /// <summary>Names given to participants of this project's calls, offered when naming the next ones.</summary>
    public List<string> People { get; set; } = new();

    /// <summary>Folder name derived from the project name.</summary>
    public string FolderName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return safe.Length == 0 ? "_" : safe;
        }
    }
}

sealed class AssistantSettings
{
    /// <summary>The AI assistant reads the live transcript and writes summaries and suggestions.</summary>
    public bool Enabled { get; set; }

    public LlmProvider Provider { get; set; } = LlmProvider.LmStudio;

    public string LmStudioUrl { get; set; } = "http://localhost:1234";

    /// <summary>Last chosen model per provider.</summary>
    public string? LocalModel { get; set; } = "google/gemma-4-e4b";
    public string? CloudModel { get; set; }

    /// <summary>Context length (tokens) LM Studio loaded the local model with — limits how much transcript is sent.</summary>
    public int? LocalContext { get; set; }

    /// <summary>OpenRouter API key encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? OpenRouterKeyProtected { get; set; }

    /// <summary>How often the assistant looks at new speech during a call; 0 — only when asked.</summary>
    public int IntervalSeconds { get; set; } = 60;

    [JsonIgnore]
    public string? OpenRouterKey
    {
        get => Secret.Unprotect(OpenRouterKeyProtected);
        set => OpenRouterKeyProtected = Secret.Protect(value);
    }

    [JsonIgnore]
    public string? Model => Provider == LlmProvider.LmStudio ? LocalModel : CloudModel;

    public static readonly int[] Intervals = { 0, 30, 60, 120, 300 };
}

/// <summary>Secrets in settings.json are readable only by the same Windows user on the same machine.</summary>
static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiCallAssistant.settings");

    public static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            AppLog.Write("Cannot decrypt a stored secret: " + ex.Message);
            return null;
        }
    }
}

static class AppLog
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "AiCallAssistant.log");
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
