using System.Globalization;

namespace AiCallAssistant;

/// <summary>
/// All user-visible text in English, Russian and Swedish. <c>required</c> makes the compiler
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

    // Lazy on purpose: static fields initialize in text order, and En/Ru/Sv are declared below.
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

    static string Money(decimal value) => "$" + value.ToString(value < 1 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    // ---------- menu ----------
    public required string ShowWindow { get; init; }
    public required string StartRecording { get; init; }
    public required string Pause { get; init; }
    public required string Resume { get; init; }
    public required string Stop { get; init; }
    public required string Recognition { get; init; }
    public required string ModeLive { get; init; }
    public required string ModeAfter { get; init; }
    public required string ModeOff { get; init; }
    public required string TranscribeFile { get; init; }
    public required string AiAssistant { get; init; }
    public required string AiSettingsMenu { get; init; }
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
    public required string ErrTranscriptFile { get; init; }
    public required string ErrAssistant { get; init; }
    public required string ErrMoveFiles { get; init; }
    public required string ErrInternal { get; init; }
    public required Func<string, string, string> ErrCannotWrite { get; init; }
    public required Func<string, string, string> Fatal { get; init; }
    public required string ExitConfirm { get; init; }
    public required string PickFileTitle { get; init; }
    public required string AudioFiles { get; init; }
    public required string AllFiles { get; init; }
    public required string PickFolderTitle { get; init; }
    public required string Cancel { get; init; }
    public required string Close { get; init; }

    // ---------- main window ----------
    public required string Project { get; init; }
    public required string ProjectNotes { get; init; }
    public required Func<string, string> ProjectNotesTitle { get; init; }
    public required string ProjectNotesHint { get; init; }
    public required string ProjectFolder { get; init; }
    public required string Participants { get; init; }
    public required string ParticipantsHint { get; init; }
    public required string ParticipantsEmpty { get; init; }
    public required string ColumnVoice { get; init; }
    public required string ColumnName { get; init; }
    public required string ColumnPhrases { get; init; }
    public required string KnownVoices { get; init; }
    public required Func<float, string> RecognizedByVoice { get; init; }
    public required string TranscriptTitle { get; init; }
    public required string NoCallYet { get; init; }
    public required string DraftNote { get; init; }
    public required string LiveOffHint { get; init; }
    public required string EnableLive { get; init; }

    // ---------- known voices ----------
    public required string VoicesTitle { get; init; }
    public required string VoicesHint { get; init; }
    public required string VoicesCalls { get; init; }
    public required string VoicesLastHeard { get; init; }
    public required string VoicesProjects { get; init; }
    public required string VoicesForget { get; init; }
    public required Func<string, string> VoicesForgetConfirm { get; init; }

    // ---------- assistant panel ----------
    public required string AiSettingsButton { get; init; }
    public required string AiNoModelChosen { get; init; }
    public required string AiRuns { get; init; }
    public required Func<int, string> AiInterval { get; init; }
    public required string AiNow { get; init; }
    public required string AiAsk { get; init; }
    public required string AiQuestionHint { get; init; }
    public required Func<decimal, string> AiCost { get; init; }
    public required string AiWaiting { get; init; }
    public required string AiOffHint { get; init; }
    public required string AiThinking { get; init; }
    public required string AiNotesTitle { get; init; }
    public required string AiFinalTitle { get; init; }
    public required string AiYouAsked { get; init; }
    public required string AiCancelled { get; init; }
    public required string AiEmptyAnswer { get; init; }
    public required string AiNoModel { get; init; }
    public required string AiNoKey { get; init; }

    // ---------- what the model is asked to write ----------
    /// <summary>Name of this language in English, for the prompt ("Write in …").</summary>
    public required string AiLanguageName { get; init; }
    public required string AiHeadSummary { get; init; }
    public required string AiHeadSuggestions { get; init; }
    public required string AiHeadCheck { get; init; }
    public required string AiHeadDecisions { get; init; }
    public required string AiHeadActions { get; init; }
    public required string AiHeadOpen { get; init; }

    // ---------- AI settings ----------
    public required string AiSettingsTitle { get; init; }
    public required string AiSource { get; init; }
    public required string AiLocal { get; init; }
    public required string AiCloud { get; init; }
    public required string AiLmStudioUrl { get; init; }
    public required string AiApiKey { get; init; }
    public required string AiCheckKey { get; init; }
    public required string AiGetKey { get; init; }
    public required string AiEnterKey { get; init; }
    public required string AiCheckingKey { get; init; }
    public required Func<decimal, decimal?, string> AiKeyOk { get; init; }
    public required Func<string, string> AiKeyFailed { get; init; }
    public required string AiModel { get; init; }
    public required string AiRefresh { get; init; }
    public required string AiLoadingModels { get; init; }
    public required string AiNoLocalModels { get; init; }
    public required string AiNoCloudModels { get; init; }
    public required Func<string, string> AiLmStudioUnavailable { get; init; }
    public required string AiLocalLoadedInfo { get; init; }
    public required string AiLocalJitInfo { get; init; }
    public required string AiLoaded { get; init; }
    public required Func<int, string> AiContext { get; init; }
    public required string AiFree { get; init; }
    public required string AiPerMillion { get; init; }

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

    static readonly Strings En = new()
    {
        ShowWindow = "Open assistant window",
        StartRecording = "Start recording",
        Pause = "Pause",
        Resume = "Resume",
        Stop = "Stop",
        Recognition = "Speech recognition",
        ModeLive = "Live, while recording",
        ModeAfter = "After recording",
        ModeOff = "Off",
        TranscribeFile = "Transcribe file…",
        AiAssistant = "AI assistant",
        AiSettingsMenu = "AI assistant settings…",
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
        ErrTranscriptFile = "Transcript file",
        ErrAssistant = "AI assistant",
        ErrMoveFiles = "Moving the call to the project folder",
        ErrInternal = "Internal error",
        ErrCannotWrite = (folder, msg) => $"Cannot write to {folder}: {msg}",
        Fatal = (msg, log) => $"{AppInfo.Name} is crashing:\n\n{msg}\n\nDetails in {log}",
        ExitConfirm = "A transcript is not finished yet and will be cancelled. Exit anyway?",
        PickFileTitle = "File to transcribe",
        AudioFiles = "Audio",
        AllFiles = "All files",
        PickFolderTitle = "Where to save recordings and transcripts",
        Cancel = "Cancel",
        Close = "Close",

        Project = "Project:",
        ProjectNotes = "Notes…",
        ProjectNotesTitle = name => $"Notes for “{name}”",
        ProjectNotesHint = "Background the assistant gets with every request: goals, terms, who is who.",
        ProjectFolder = "Folder",
        Participants = "Participants",
        ParticipantsHint = "Type a name next to a voice — the transcript and the assistant will use it, and the voice will be recognized in the next calls.",
        ParticipantsEmpty = "Voices appear here as people speak.",
        ColumnVoice = "Voice",
        ColumnName = "Name",
        ColumnPhrases = "Phrases",
        KnownVoices = "Known voices…",
        RecognizedByVoice = s => $"Recognized by voice ({s:P0}). Type the name to confirm it, or another one to correct it.",
        TranscriptTitle = "Transcript",
        NoCallYet = "No call yet. Start recording, or transcribe a file from the tray menu.",
        DraftNote = "live draft — replaced by an accurate transcript after the call",
        LiveOffHint = "Live speech recognition is off: the transcript appears after the call.",
        EnableLive = "Turn on live recognition",

        VoicesTitle = "Known voices",

        VoicesHint = "Voices of the people you have named. In the next calls they are named automatically. Voice prints are stored only on this computer (voices.json next to the app).",

        VoicesCalls = "Calls",

        VoicesLastHeard = "Last heard",
        VoicesProjects = "Projects",

        VoicesForget = "Forget",

        VoicesForgetConfirm = names => $"Forget the voice of {names}? They will no longer be recognized.",


        AiSettingsButton = "Settings…",
        AiNoModelChosen = "no model chosen",
        AiRuns = "Update:",
        AiInterval = s => s switch
        {
            0 => "only on request",
            < 60 => $"every {s} s",
            _ => $"every {s / 60} min"
        },
        AiNow = "Update now",
        AiAsk = "Ask",
        AiQuestionHint = "Ask the assistant about the call…",
        AiCost = c => "spent " + Money(c),
        AiWaiting = "The assistant will write a summary and suggestions as the call goes on.",
        AiOffHint = "Turn on the AI assistant to get a summary, suggestions and fact checks during the call.",
        AiThinking = "thinking…",
        AiNotesTitle = "Notes",
        AiFinalTitle = "Summary of the call",
        AiYouAsked = "You: ",
        AiCancelled = "cancelled",
        AiEmptyAnswer = "The model returned an empty answer.",
        AiNoModel = "No model is chosen — open the AI assistant settings.",
        AiNoKey = "No OpenRouter API key — open the AI assistant settings.",

        AiLanguageName = "English",
        AiHeadSummary = "Summary",
        AiHeadSuggestions = "Suggestions",
        AiHeadCheck = "Worth checking",
        AiHeadDecisions = "Decisions",
        AiHeadActions = "Action items",
        AiHeadOpen = "Open questions",

        AiSettingsTitle = "AI assistant settings",
        AiSource = "Model runs:",
        AiLocal = "locally in LM Studio",
        AiCloud = "in the cloud (OpenRouter)",
        AiLmStudioUrl = "LM Studio address:",
        AiApiKey = "OpenRouter API key:",
        AiCheckKey = "Check",
        AiGetKey = "Get a key at openrouter.ai",
        AiEnterKey = "Enter a key to see the models and their prices.",
        AiCheckingKey = "Checking the key…",
        AiKeyOk = (usage, limit) => $"The key works. Spent {Money(usage)}" + (limit is { } l ? $" of {Money(l)}." : "."),
        AiKeyFailed = msg => "The key does not work: " + msg,
        AiModel = "Model:",
        AiRefresh = "Refresh",
        AiLoadingModels = "Loading the list of models…",
        AiNoLocalModels = "LM Studio has no chat models. Download one in LM Studio, e.g. google/gemma-4-e4b.",
        AiNoCloudModels = "OpenRouter returned no models.",
        AiLmStudioUnavailable = msg => "LM Studio is not reachable: " + msg + ". Start LM Studio and turn on its local server (Developer tab).",
        AiLocalLoadedInfo = "Loaded in LM Studio. Transcripts never leave this computer.",
        AiLocalJitInfo = "LM Studio loads it on the first request (takes a while). Transcripts never leave this computer.",
        AiLoaded = "loaded",
        AiContext = n => $"context {n:N0}",
        AiFree = "free",
        AiPerMillion = "per 1M tokens in / out",

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
        NoSpeech = "(no speech found)"
    };

    static readonly Strings Ru = new()
    {
        ShowWindow = "Открыть окно ассистента",
        StartRecording = "Начать запись",
        Pause = "Пауза",
        Resume = "Продолжить",
        Stop = "Стоп",
        Recognition = "Распознавание речи",
        ModeLive = "Онлайн, во время записи",
        ModeAfter = "После записи",
        ModeOff = "Отключено",
        TranscribeFile = "Распознать файл…",
        AiAssistant = "ИИ-ассистент",
        AiSettingsMenu = "Настройки ИИ-ассистента…",
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
        ErrTranscriptFile = "Файл расшифровки",
        ErrAssistant = "ИИ-ассистент",
        ErrMoveFiles = "Перенос звонка в папку проекта",
        ErrInternal = "Внутренняя ошибка",
        ErrCannotWrite = (folder, msg) => $"Нельзя писать в {folder}: {msg}",
        Fatal = (msg, log) => $"{AppInfo.Name} аварийно завершается:\n\n{msg}\n\nПодробности в {log}",
        ExitConfirm = "Расшифровка ещё не готова и будет прервана. Выйти?",
        PickFileTitle = "Файл для распознавания",
        AudioFiles = "Аудио",
        AllFiles = "Все файлы",
        PickFolderTitle = "Куда сохранять записи и расшифровки",
        Cancel = "Отмена",
        Close = "Закрыть",

        Project = "Проект:",
        ProjectNotes = "Заметки…",
        ProjectNotesTitle = name => $"Заметки к «{name}»",
        ProjectNotesHint = "Контекст, который ассистент получает с каждым запросом: цели, термины, кто есть кто.",
        ProjectFolder = "Папка",
        Participants = "Участники",
        ParticipantsHint = "Впишите имя рядом с голосом — его будут использовать расшифровка и ассистент, а голос будет узнаваться в следующих звонках.",
        ParticipantsEmpty = "Голоса появятся здесь, когда люди начнут говорить.",
        ColumnVoice = "Голос",
        ColumnName = "Имя",
        ColumnPhrases = "Фраз",
        KnownVoices = "Знакомые голоса…",
        RecognizedByVoice = s => $"Узнан по голосу ({s:P0}). Впишите имя, чтобы подтвердить, или другое, чтобы исправить.",
        TranscriptTitle = "Расшифровка",
        NoCallYet = "Звонка ещё не было. Начните запись или распознайте файл из меню в трее.",
        DraftNote = "черновик онлайн — после звонка заменяется точной расшифровкой",
        LiveOffHint = "Онлайн-распознавание выключено: расшифровка появится после звонка.",
        EnableLive = "Включить онлайн-распознавание",

        VoicesTitle = "Знакомые голоса",

        VoicesHint = "Голоса людей, которым вы дали имена. В следующих звонках они подписываются автоматически. Слепки голосов хранятся только на этом компьютере (voices.json рядом с приложением).",

        VoicesCalls = "Звонков",

        VoicesLastHeard = "Последний раз",
        VoicesProjects = "Проекты",

        VoicesForget = "Забыть",

        VoicesForgetConfirm = names => $"Забыть голос: {names}? Его больше не будут узнавать.",


        AiSettingsButton = "Настройки…",
        AiNoModelChosen = "модель не выбрана",
        AiRuns = "Обновлять:",
        AiInterval = s => s switch
        {
            0 => "только по запросу",
            < 60 => $"каждые {s} с",
            _ => $"каждые {s / 60} мин"
        },
        AiNow = "Обновить сейчас",
        AiAsk = "Спросить",
        AiQuestionHint = "Спросите ассистента о звонке…",
        AiCost = c => "потрачено " + Money(c),
        AiWaiting = "Ассистент будет писать саммари и подсказки по ходу звонка.",
        AiOffHint = "Включите ИИ-ассистента, чтобы получать саммари, подсказки и проверку фактов во время звонка.",
        AiThinking = "думает…",
        AiNotesTitle = "Заметки",
        AiFinalTitle = "Итоги звонка",
        AiYouAsked = "Вы: ",
        AiCancelled = "отменено",
        AiEmptyAnswer = "Модель вернула пустой ответ.",
        AiNoModel = "Модель не выбрана — откройте настройки ИИ-ассистента.",
        AiNoKey = "Нет API-ключа OpenRouter — откройте настройки ИИ-ассистента.",

        AiLanguageName = "Russian",
        AiHeadSummary = "Саммари",
        AiHeadSuggestions = "Подсказки",
        AiHeadCheck = "Стоит проверить",
        AiHeadDecisions = "Решения",
        AiHeadActions = "Задачи",
        AiHeadOpen = "Открытые вопросы",

        AiSettingsTitle = "Настройки ИИ-ассистента",
        AiSource = "Модель работает:",
        AiLocal = "локально в LM Studio",
        AiCloud = "в облаке (OpenRouter)",
        AiLmStudioUrl = "Адрес LM Studio:",
        AiApiKey = "API-ключ OpenRouter:",
        AiCheckKey = "Проверить",
        AiGetKey = "Получить ключ на openrouter.ai",
        AiEnterKey = "Введите ключ, чтобы увидеть модели и цены.",
        AiCheckingKey = "Проверяю ключ…",
        AiKeyOk = (usage, limit) => $"Ключ работает. Потрачено {Money(usage)}" + (limit is { } l ? $" из {Money(l)}." : "."),
        AiKeyFailed = msg => "Ключ не работает: " + msg,
        AiModel = "Модель:",
        AiRefresh = "Обновить",
        AiLoadingModels = "Загружаю список моделей…",
        AiNoLocalModels = "В LM Studio нет моделей для чата. Скачайте модель в LM Studio, например google/gemma-4-e4b.",
        AiNoCloudModels = "OpenRouter не вернул ни одной модели.",
        AiLmStudioUnavailable = msg => "LM Studio недоступна: " + msg + ". Запустите LM Studio и включите локальный сервер (вкладка Developer).",
        AiLocalLoadedInfo = "Загружена в LM Studio. Расшифровки не покидают этот компьютер.",
        AiLocalJitInfo = "LM Studio загрузит её при первом запросе (это займёт время). Расшифровки не покидают этот компьютер.",
        AiLoaded = "загружена",
        AiContext = n => $"контекст {n:N0}",
        AiFree = "бесплатно",
        AiPerMillion = "за 1M токенов вход / выход",

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
        NoSpeech = "(речь не найдена)"
    };

    static readonly Strings Sv = new()
    {
        ShowWindow = "Öppna assistentfönstret",
        StartRecording = "Starta inspelning",
        Pause = "Pausa",
        Resume = "Fortsätt",
        Stop = "Stoppa",
        Recognition = "Taligenkänning",
        ModeLive = "Direkt, under inspelningen",
        ModeAfter = "Efter inspelningen",
        ModeOff = "Av",
        TranscribeFile = "Transkribera fil…",
        AiAssistant = "AI-assistent",
        AiSettingsMenu = "Inställningar för AI-assistenten…",
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
        ErrTranscriptFile = "Transkriptionsfil",
        ErrAssistant = "AI-assistent",
        ErrMoveFiles = "Flytt av samtalet till projektmappen",
        ErrInternal = "Internt fel",
        ErrCannotWrite = (folder, msg) => $"Kan inte skriva till {folder}: {msg}",
        Fatal = (msg, log) => $"{AppInfo.Name} kraschar:\n\n{msg}\n\nDetaljer finns i {log}",
        ExitConfirm = "En transkription är inte klar och kommer att avbrytas. Avsluta ändå?",
        PickFileTitle = "Fil att transkribera",
        AudioFiles = "Ljud",
        AllFiles = "Alla filer",
        PickFolderTitle = "Var inspelningar och transkriptioner ska sparas",
        Cancel = "Avbryt",
        Close = "Stäng",

        Project = "Projekt:",
        ProjectNotes = "Anteckningar…",
        ProjectNotesTitle = name => $"Anteckningar för ”{name}”",
        ProjectNotesHint = "Bakgrund som assistenten får med varje förfrågan: mål, begrepp, vem som är vem.",
        ProjectFolder = "Mapp",
        Participants = "Deltagare",
        ParticipantsHint = "Skriv ett namn bredvid en röst — transkriptionen och assistenten använder det, och rösten känns igen i kommande samtal.",
        ParticipantsEmpty = "Rösterna visas här när folk börjar prata.",
        ColumnVoice = "Röst",
        ColumnName = "Namn",
        ColumnPhrases = "Fraser",
        KnownVoices = "Kända röster…",
        RecognizedByVoice = s => $"Igenkänd på rösten ({s:P0}). Skriv namnet för att bekräfta det, eller ett annat för att rätta.",
        TranscriptTitle = "Transkription",
        NoCallYet = "Inget samtal än. Starta en inspelning eller transkribera en fil från menyn i systemfältet.",
        DraftNote = "direktutkast — ersätts av en noggrann transkription efter samtalet",
        LiveOffHint = "Direkt taligenkänning är av: transkriptionen kommer efter samtalet.",
        EnableLive = "Slå på direkt taligenkänning",

        VoicesTitle = "Kända röster",

        VoicesHint = "Röster för de personer du har namngett. I kommande samtal får de namn automatiskt. Röstavtrycken sparas bara på den här datorn (voices.json bredvid appen).",

        VoicesCalls = "Samtal",

        VoicesLastHeard = "Senast hörd",
        VoicesProjects = "Projekt",

        VoicesForget = "Glöm",

        VoicesForgetConfirm = names => $"Glömma rösten för {names}? Den känns inte längre igen.",


        AiSettingsButton = "Inställningar…",
        AiNoModelChosen = "ingen modell vald",
        AiRuns = "Uppdatera:",
        AiInterval = s => s switch
        {
            0 => "bara på begäran",
            < 60 => $"var {s}:e sekund",
            1 * 60 => "varje minut",
            _ => $"var {s / 60}:e minut"
        },
        AiNow = "Uppdatera nu",
        AiAsk = "Fråga",
        AiQuestionHint = "Fråga assistenten om samtalet…",
        AiCost = c => "kostat " + Money(c),
        AiWaiting = "Assistenten skriver en sammanfattning och förslag medan samtalet pågår.",
        AiOffHint = "Slå på AI-assistenten för att få sammanfattning, förslag och faktakoll under samtalet.",
        AiThinking = "tänker…",
        AiNotesTitle = "Anteckningar",
        AiFinalTitle = "Sammanfattning av samtalet",
        AiYouAsked = "Du: ",
        AiCancelled = "avbrutet",
        AiEmptyAnswer = "Modellen returnerade ett tomt svar.",
        AiNoModel = "Ingen modell är vald — öppna inställningarna för AI-assistenten.",
        AiNoKey = "Ingen API-nyckel för OpenRouter — öppna inställningarna för AI-assistenten.",

        AiLanguageName = "Swedish",
        AiHeadSummary = "Sammanfattning",
        AiHeadSuggestions = "Förslag",
        AiHeadCheck = "Värt att kontrollera",
        AiHeadDecisions = "Beslut",
        AiHeadActions = "Att göra",
        AiHeadOpen = "Öppna frågor",

        AiSettingsTitle = "Inställningar för AI-assistenten",
        AiSource = "Modellen körs:",
        AiLocal = "lokalt i LM Studio",
        AiCloud = "i molnet (OpenRouter)",
        AiLmStudioUrl = "Adress till LM Studio:",
        AiApiKey = "API-nyckel för OpenRouter:",
        AiCheckKey = "Kontrollera",
        AiGetKey = "Skaffa en nyckel på openrouter.ai",
        AiEnterKey = "Ange en nyckel för att se modellerna och deras priser.",
        AiCheckingKey = "Kontrollerar nyckeln…",
        AiKeyOk = (usage, limit) => $"Nyckeln fungerar. Förbrukat {Money(usage)}" + (limit is { } l ? $" av {Money(l)}." : "."),
        AiKeyFailed = msg => "Nyckeln fungerar inte: " + msg,
        AiModel = "Modell:",
        AiRefresh = "Uppdatera",
        AiLoadingModels = "Hämtar listan över modeller…",
        AiNoLocalModels = "LM Studio har inga chattmodeller. Ladda ner en i LM Studio, t.ex. google/gemma-4-e4b.",
        AiNoCloudModels = "OpenRouter returnerade inga modeller.",
        AiLmStudioUnavailable = msg => "LM Studio går inte att nå: " + msg + ". Starta LM Studio och slå på den lokala servern (fliken Developer).",
        AiLocalLoadedInfo = "Inläst i LM Studio. Transkriptioner lämnar aldrig den här datorn.",
        AiLocalJitInfo = "LM Studio läser in den vid första förfrågan (tar en stund). Transkriptioner lämnar aldrig den här datorn.",
        AiLoaded = "inläst",
        AiContext = n => $"kontext {n:N0}",
        AiFree = "gratis",
        AiPerMillion = "per 1M tokens in / ut",

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
        NoSpeech = "(inget tal hittades)"
    };
}
