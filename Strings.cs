using System.Globalization;

namespace SoundRecord;

/// <summary>
/// All user-visible text in Russian, English and Swedish. <c>required</c> makes the compiler
/// check that every language defines every string.
/// </summary>
sealed class Strings
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("ru", "Русский"),
        ("sv", "Svenska")
    };

    // Lazy on purpose: static fields initialize in text order, and Ru/En/Sv are declared below.
    static Strings? _current;

    /// <summary>Current UI language; switched from the UI thread, read from anywhere.</summary>
    public static Strings Current => _current ??= For(null);

    /// <summary>null = Windows display language (English if it is none of the supported ones).</summary>
    public static void Use(string? code) => _current = For(code);

    static Strings For(string? code) => (code ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) switch
    {
        "ru" => Ru,
        "sv" => Sv,
        _ => En
    };

    // ---------- menu ----------
    public required string StartRecording { get; init; }
    public required string Pause { get; init; }
    public required string Resume { get; init; }
    public required string Stop { get; init; }
    public required string Recognition { get; init; }
    public required string ModeLive { get; init; }
    public required string ModeAfter { get; init; }
    public required string ModeOff { get; init; }
    public required string TranscribeFile { get; init; }
    public required string OpenFolder { get; init; }
    public required string RecordingsFolder { get; init; }
    public required string Language { get; init; }
    public required string LanguageSystem { get; init; }
    public required string Exit { get; init; }
    public required Func<string, string> CurrentFolder { get; init; }
    public required string FolderNextRecording { get; init; }
    public required string Queued { get; init; }
    public required Func<int, string> MoreQueued { get; init; }
    public required Func<int, string> ErrorCount { get; init; }
    public required string OpenLog { get; init; }

    // ---------- tray tooltip ----------
    public required string StateIdle { get; init; }
    public required string StateRecording { get; init; }
    public required string StatePaused { get; init; }
    public required string TipError { get; init; }

    // ---------- notifications and dialogs ----------
    public required string RecordingSaved { get; init; }
    public required string TranscriptReady { get; init; }
    public required string ErrRecording { get; init; }
    public required string ErrStart { get; init; }
    public required string WarnPartialStart { get; init; }
    public required string ErrSave { get; init; }
    public required string ErrLive { get; init; }
    public required string ErrModels { get; init; }
    public required Func<string, string> ErrTranscription { get; init; }
    public required string ErrInternal { get; init; }
    public required Func<string, string, string> ErrCannotWrite { get; init; }
    public required Func<string, string, string> Fatal { get; init; }
    public required string ExitConfirm { get; init; }
    public required string PickFileTitle { get; init; }
    public required string AudioFiles { get; init; }
    public required string AllFiles { get; init; }
    public required string PickFolderTitle { get; init; }

    // ---------- recorder ----------
    public required string SourceSpeakers { get; init; }
    public required string SourceMic { get; init; }
    public required string NoDevices { get; init; }
    public required Func<string, string, string> SourceStopped { get; init; }
    public required Func<string, string, string> SourceFailed { get; init; }
    public required string TapFailed { get; init; }
    public required string RecordingStopped { get; init; }

    // ---------- transcription ----------
    public required string StageLoadingModel { get; init; }
    public required string StageReading { get; init; }
    public required string StageMicSpeech { get; init; }
    public required string StageVoices { get; init; }
    public required string StageRecognizing { get; init; }
    public required string StageDone { get; init; }
    public required string StageFinishingDraft { get; init; }
    public required string LoadingModel { get; init; }
    public required string ModelUnloaded { get; init; }
    public required Func<string, int, long, string> Downloading { get; init; }
    public required Func<string, long, string> DownloadingNoTotal { get; init; }
    public required string LiveUnavailable { get; init; }

    // ---------- transcript text ----------
    public required string Me { get; init; }
    public required Func<int, string> Speaker { get; init; }
    public required string OtherParty { get; init; }
    public required string NoSpeech { get; init; }
    public required Func<string, string> DraftUnavailable { get; init; }

    static readonly Strings Ru = new()
    {
        StartRecording = "Начать запись",
        Pause = "Пауза",
        Resume = "Продолжить",
        Stop = "Стоп",
        Recognition = "Распознавание речи",
        ModeLive = "Онлайн, во время записи",
        ModeAfter = "После записи",
        ModeOff = "Отключено",
        TranscribeFile = "Распознать файл…",
        OpenFolder = "Открыть папку",
        RecordingsFolder = "Папка для записей…",
        Language = "Язык (Language)",
        LanguageSystem = "Как в системе",
        Exit = "Выход",
        CurrentFolder = f => "Сейчас: " + f,
        FolderNextRecording = "Новая папка будет использована со следующей записи.",
        Queued = "Распознавание в очереди",
        MoreQueued = n => $" (ещё в очереди: {n})",
        ErrorCount = n => $" (ошибок: {n})",
        OpenLog = " — открыть лог",

        StateIdle = "ожидание",
        StateRecording = "запись",
        StatePaused = "пауза",
        TipError = "⚠ ошибка — см. меню",

        RecordingSaved = "Запись сохранена",
        TranscriptReady = "Расшифровка готова",
        ErrRecording = "Запись",
        ErrStart = "Не удалось начать запись",
        WarnPartialStart = "Запись начата частично",
        ErrSave = "Не удалось сохранить файл: ",
        ErrLive = "Онлайн-распознавание",
        ErrModels = "Модели распознавания",
        ErrTranscription = name => "Распознавание " + name,
        ErrInternal = "Внутренняя ошибка",
        ErrCannotWrite = (folder, msg) => $"Нельзя писать в {folder}: {msg}",
        Fatal = (msg, log) => $"Sound Record аварийно завершается:\n\n{msg}\n\nПодробности в {log}",
        ExitConfirm = "Расшифровка ещё не готова и будет прервана. Выйти?",
        PickFileTitle = "Файл для распознавания",
        AudioFiles = "Аудио",
        AllFiles = "Все файлы",
        PickFolderTitle = "Куда сохранять записи и расшифровки",

        SourceSpeakers = "выход (динамики)",
        SourceMic = "микрофон",
        NoDevices = "Не найдено ни одного аудиоустройства.",
        SourceStopped = (name, msg) => $"{name} перестал записываться: {msg}",
        SourceFailed = (name, msg) => $"Не удалось подключить {name}: {msg}",
        TapFailed = "Звук для распознавания больше не передаётся: ",
        RecordingStopped = "Запись остановлена: ",

        StageLoadingModel = "загрузка модели",
        StageReading = "чтение записи",
        StageMicSpeech = "поиск речи на микрофоне",
        StageVoices = "разделение голосов",
        StageRecognizing = "распознавание",
        StageDone = "готово",
        StageFinishingDraft = "дописываю черновик",
        LoadingModel = "Загрузка модели распознавания…",
        ModelUnloaded = "Модель распознавания выгружена.",
        Downloading = (title, pct, mb) => $"Скачивание {title}: {pct}% из {mb} МБ",
        DownloadingNoTotal = (title, mb) => $"Скачивание {title}: {mb} МБ",
        LiveUnavailable = "Онлайн-распознавание недоступно: ",

        Me = "Я",
        Speaker = n => $"Спикер {n}",
        OtherParty = "Собеседник",
        NoSpeech = "(речь не найдена)",
        DraftUnavailable = msg => $"(распознавание недоступно: {msg})"
    };

    static readonly Strings En = new()
    {
        StartRecording = "Start recording",
        Pause = "Pause",
        Resume = "Resume",
        Stop = "Stop",
        Recognition = "Speech recognition",
        ModeLive = "Live, while recording",
        ModeAfter = "After recording",
        ModeOff = "Off",
        TranscribeFile = "Transcribe file…",
        OpenFolder = "Open folder",
        RecordingsFolder = "Recordings folder…",
        Language = "Language",
        LanguageSystem = "System default",
        Exit = "Exit",
        CurrentFolder = f => "Current: " + f,
        FolderNextRecording = "A new folder takes effect from the next recording.",
        Queued = "Transcription queued",
        MoreQueued = n => $" ({n} more queued)",
        ErrorCount = n => $" (errors: {n})",
        OpenLog = " — open log",

        StateIdle = "idle",
        StateRecording = "recording",
        StatePaused = "paused",
        TipError = "⚠ error — see menu",

        RecordingSaved = "Recording saved",
        TranscriptReady = "Transcript ready",
        ErrRecording = "Recording",
        ErrStart = "Could not start recording",
        WarnPartialStart = "Recording started partially",
        ErrSave = "Could not save the file: ",
        ErrLive = "Live transcription",
        ErrModels = "Speech models",
        ErrTranscription = name => "Transcription of " + name,
        ErrInternal = "Internal error",
        ErrCannotWrite = (folder, msg) => $"Cannot write to {folder}: {msg}",
        Fatal = (msg, log) => $"Sound Record is crashing:\n\n{msg}\n\nDetails in {log}",
        ExitConfirm = "A transcript is not finished yet and will be cancelled. Exit anyway?",
        PickFileTitle = "File to transcribe",
        AudioFiles = "Audio",
        AllFiles = "All files",
        PickFolderTitle = "Where to save recordings and transcripts",

        SourceSpeakers = "output (speakers)",
        SourceMic = "microphone",
        NoDevices = "No audio devices found.",
        SourceStopped = (name, msg) => $"The {name} stopped recording: {msg}",
        SourceFailed = (name, msg) => $"Could not open the {name}: {msg}",
        TapFailed = "Audio no longer reaches transcription: ",
        RecordingStopped = "Recording stopped: ",

        StageLoadingModel = "loading model",
        StageReading = "reading the recording",
        StageMicSpeech = "finding speech on the microphone",
        StageVoices = "separating voices",
        StageRecognizing = "transcribing",
        StageDone = "done",
        StageFinishingDraft = "finishing the draft",
        LoadingModel = "Loading speech model…",
        ModelUnloaded = "The speech model was unloaded.",
        Downloading = (title, pct, mb) => $"Downloading {title}: {pct}% of {mb} MB",
        DownloadingNoTotal = (title, mb) => $"Downloading {title}: {mb} MB",
        LiveUnavailable = "Live transcription unavailable: ",

        Me = "Me",
        Speaker = n => $"Speaker {n}",
        OtherParty = "Other party",
        NoSpeech = "(no speech found)",
        DraftUnavailable = msg => $"(transcription unavailable: {msg})"
    };

    static readonly Strings Sv = new()
    {
        StartRecording = "Starta inspelning",
        Pause = "Pausa",
        Resume = "Fortsätt",
        Stop = "Stoppa",
        Recognition = "Taligenkänning",
        ModeLive = "Direkt, under inspelningen",
        ModeAfter = "Efter inspelningen",
        ModeOff = "Av",
        TranscribeFile = "Transkribera fil…",
        OpenFolder = "Öppna mappen",
        RecordingsFolder = "Mapp för inspelningar…",
        Language = "Språk (Language)",
        LanguageSystem = "Som i systemet",
        Exit = "Avsluta",
        CurrentFolder = f => "Nu: " + f,
        FolderNextRecording = "En ny mapp gäller från nästa inspelning.",
        Queued = "Transkribering i kö",
        MoreQueued = n => $" ({n} till i kö)",
        ErrorCount = n => $" (fel: {n})",
        OpenLog = " — öppna loggen",

        StateIdle = "väntar",
        StateRecording = "spelar in",
        StatePaused = "pausad",
        TipError = "⚠ fel — se menyn",

        RecordingSaved = "Inspelningen har sparats",
        TranscriptReady = "Transkriptionen är klar",
        ErrRecording = "Inspelning",
        ErrStart = "Kunde inte starta inspelningen",
        WarnPartialStart = "Inspelningen startade delvis",
        ErrSave = "Kunde inte spara filen: ",
        ErrLive = "Direkttranskribering",
        ErrModels = "Taligenkänningsmodeller",
        ErrTranscription = name => "Transkribering av " + name,
        ErrInternal = "Internt fel",
        ErrCannotWrite = (folder, msg) => $"Kan inte skriva till {folder}: {msg}",
        Fatal = (msg, log) => $"Sound Record kraschar:\n\n{msg}\n\nDetaljer finns i {log}",
        ExitConfirm = "En transkription är inte klar och kommer att avbrytas. Avsluta ändå?",
        PickFileTitle = "Fil att transkribera",
        AudioFiles = "Ljud",
        AllFiles = "Alla filer",
        PickFolderTitle = "Var inspelningar och transkriptioner ska sparas",

        SourceSpeakers = "utgången (högtalarna)",
        SourceMic = "mikrofonen",
        NoDevices = "Inga ljudenheter hittades.",
        SourceStopped = (name, msg) => $"Inspelningen från {name} avbröts: {msg}",
        SourceFailed = (name, msg) => $"Kunde inte ansluta {name}: {msg}",
        TapFailed = "Ljudet når inte längre transkriberingen: ",
        RecordingStopped = "Inspelningen stoppades: ",

        StageLoadingModel = "laddar modellen",
        StageReading = "läser inspelningen",
        StageMicSpeech = "söker tal i mikrofonen",
        StageVoices = "skiljer rösterna åt",
        StageRecognizing = "transkriberar",
        StageDone = "klar",
        StageFinishingDraft = "slutför utkastet",
        LoadingModel = "Laddar taligenkänningsmodellen…",
        ModelUnloaded = "Taligenkänningsmodellen har tagits bort ur minnet.",
        Downloading = (title, pct, mb) => $"Laddar ner {title}: {pct} % av {mb} MB",
        DownloadingNoTotal = (title, mb) => $"Laddar ner {title}: {mb} MB",
        LiveUnavailable = "Direkttranskribering är inte tillgänglig: ",

        Me = "Jag",
        Speaker = n => $"Talare {n}",
        OtherParty = "Motpart",
        NoSpeech = "(inget tal hittades)",
        DraftUnavailable = msg => $"(transkribering inte tillgänglig: {msg})"
    };
}
