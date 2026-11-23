using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace AiCallAssistant;

/// <summary>
/// The tray icon and the owner of everything that lives longer than a window: recording,
/// transcription queue, the current call session and its assistant.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly SpeechEngine _engine = new();
    readonly SynchronizationContext _ui;

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _error, _status, _show, _start, _pause, _stop;
    readonly ToolStripMenuItem _modeMenu, _modeLive, _modeAfter, _modeOff;
    readonly ToolStripMenuItem _transcribeItem, _aiEnabled, _aiSettings, _openFolderItem, _folderItem, _exitItem;
    readonly ToolStripMenuItem _languageMenu, _languageSystem;
    readonly List<(string Code, ToolStripMenuItem Item)> _languageItems = new();
    readonly System.Windows.Forms.Timer _tick;
    readonly Dictionary<string, Icon> _icons = new();

    CallRecorder? _recorder;
    LiveTranscriber? _live;
    CallTracks? _tracks;
    string? _lastFile;
    AssistantForm? _form;

    (string Text, double? Fraction)? _engineStatus, _jobStatus;
    Task _queue = Task.CompletedTask; // transcriptions run one after another
    int _pending;
    int _animation;

    string? _errorText;
    int _errorCount;
    string? _lastBalloonKey;
    DateTime _lastBalloonAt;
    bool _balloonIsError;

    readonly RegisteredWaitHandle _showWait;

    /// <param name="showSignal">Set by a second copy of the app asking this one to show its window.</param>
    public TrayApp(EventWaitHandle showSignal)
    {
        Strings.Use(_settings.Language);

        _error = new ToolStripMenuItem { Visible = false, ForeColor = Color.Firebrick };
        _error.Click += (_, _) => OpenLogAndClearError();
        _status = new ToolStripMenuItem { Enabled = false, Visible = false };
        _show = new ToolStripMenuItem("", null, (_, _) => ShowWindow());
        _show.Font = new Font(_show.Font, FontStyle.Bold);
        _start = new ToolStripMenuItem("", null, (_, _) => { Start(); ShowWindow(); });
        _pause = new ToolStripMenuItem("", null, (_, _) => TogglePause());
        _stop = new ToolStripMenuItem("", null, (_, _) => Stop());

        _modeLive = new ToolStripMenuItem("", null, (_, _) => SetMode(TranscriptionMode.Live));
        _modeAfter = new ToolStripMenuItem("", null, (_, _) => SetMode(TranscriptionMode.AfterRecording));
        _modeOff = new ToolStripMenuItem("", null, (_, _) => SetMode(TranscriptionMode.Off));
        _modeMenu = new ToolStripMenuItem();
        _modeMenu.DropDownItems.AddRange(new ToolStripItem[] { _modeLive, _modeAfter, _modeOff });

        _transcribeItem = new ToolStripMenuItem("", null, (_, _) => PickFileToTranscribe());
        _aiEnabled = new ToolStripMenuItem("", null, (_, _) => SetAssistantEnabled(!_settings.Assistant.Enabled));
        _aiSettings = new ToolStripMenuItem("", null, (_, _) => ShowAiSettings(null));
        _openFolderItem = new ToolStripMenuItem("", null, (_, _) => OpenFolder());
        _folderItem = new ToolStripMenuItem("", null, (_, _) => PickRecordingsFolder());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => Exit());

        _languageSystem = new ToolStripMenuItem("", null, (_, _) => SetLanguage(null));
        _languageMenu = new ToolStripMenuItem();
        _languageMenu.DropDownItems.Add(_languageSystem);
        _languageMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (code, name) in Strings.Languages)
        {
            // Language names are always shown in their own language.
            var item = new ToolStripMenuItem(name, null, (_, _) => SetLanguage(code));
            _languageItems.Add((code, item));
            _languageMenu.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _error, _status,
            _show,
            new ToolStripSeparator(),
            _start, _pause, _stop,
            new ToolStripSeparator(),
            _modeMenu,
            _transcribeItem,
            new ToolStripSeparator(),
            _aiEnabled,
            _aiSettings,
            new ToolStripSeparator(),
            _openFolderItem,
            _folderItem,
            _languageMenu,
            new ToolStripSeparator(),
            _exitItem
        });
        ApplyTexts();

        // Creating the menu installs the WinForms synchronization context on this thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _engine.StatusChanged += (text, fraction) => OnUi(() =>
        {
            _engineStatus = text == null ? null : (text, fraction);
            UpdateUi();
        });

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonIsError) OpenLogAndClearError();
            else OpenFolder();
        };

        _tick = new System.Windows.Forms.Timer { Interval = 400 };
        _tick.Tick += (_, _) =>
        {
            _animation++;
            if (_animation % 5 == 0)
                Assistant?.Tick();
            UpdateUi();
        };

        // The tray icon size follows the display scale.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => OnUi(ShowWindow), null, Timeout.Infinite, false);

        CallTracks.DeleteLeftovers();
        PrepareModels();
        UpdateUi();
        OnUi(ShowWindow);
    }

    void OnUi(Action action) => _ui.Post(_ => action(), null);

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        _icons.Clear();
        UpdateUi();
    });

    string Folder => _settings.RecordingsFolder ?? AppSettings.DefaultRecordingsFolder;

    static Strings T => Strings.Current;

    // ---------- state shown by the window ----------

    public AppSettings Settings => _settings;

    /// <summary>The call being recorded, or the last one recorded or transcribed.</summary>
    public CallSession? Session { get; private set; }

    public CallAssistant? Assistant { get; private set; }

    public bool IsRecording => _recorder != null;
    public bool IsPaused => _recorder?.IsPaused == true;
    public TimeSpan Duration => _recorder?.Duration ?? TimeSpan.Zero;

    /// <summary>What is being processed right now, if anything.</summary>
    public (string Text, double? Fraction)? Work { get; private set; }

    /// <summary>Recording state, progress or settings changed (UI thread).</summary>
    public event Action? StateChanged;

    /// <summary>Another call became the current one (UI thread).</summary>
    public event Action? SessionChanged;

    void ShowWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new AssistantForm(this);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    void SetLanguage(string? code)
    {
        _settings.Language = code;
        _settings.Save();
        Strings.Use(code);
        ApplyTexts();
        _form?.ApplyTexts();
        UpdateUi();
    }

    /// <summary>Texts that change only with the language; state-dependent ones are set in <see cref="UpdateUi"/>.</summary>
    void ApplyTexts()
    {
        _show.Text = T.ShowWindow;
        _start.Text = T.StartRecording;
        _stop.Text = T.Stop;
        _modeMenu.Text = T.Recognition;
        _modeLive.Text = T.ModeLive;
        _modeAfter.Text = T.ModeAfter;
        _modeOff.Text = T.ModeOff;
        _transcribeItem.Text = T.TranscribeFile;
        _aiEnabled.Text = T.AiAssistant;
        _aiSettings.Text = T.AiSettingsMenu;
        _openFolderItem.Text = T.OpenFolder;
        _folderItem.Text = T.RecordingsFolder;
        _languageMenu.Text = T.Language;
        _languageSystem.Text = T.LanguageSystem;
        _exitItem.Text = T.Exit;

        _languageSystem.Checked = _settings.Language == null;
        foreach (var (code, item) in _languageItems)
            item.Checked = _settings.Language == code;
    }

    // ---------- projects, names, assistant ----------

    /// <summary>Project of the current call and of the next recordings (null — none).</summary>
    public void SetProject(string? name)
    {
        var project = _settings.UseProject(name);
        _settings.Save();
        if (Session is { State: SessionState.Recording } session)
            session.Project = project?.Name;
        UpdateUi();
    }

    public void SetProjectNotes(string? notes)
    {
        if (_settings.FindProject(_settings.Project) is not { } project)
            return;
        project.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        _settings.Save();
    }

    /// <summary>Gives a voice of the current call a name and remembers it for the project.</summary>
    public void RenameSpeaker(string key, string? name)
    {
        var session = Session;
        if (session == null)
            return;

        try
        {
            session.Rename(key, name);
        }
        catch (Exception ex)
        {
            ReportError(T.ErrTranscriptFile, ex.Message);
        }

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (key == CallSession.MeKey)
        {
            _settings.MyName = name;
        }
        else if (name != null && _settings.FindProject(session.Project) is { } project &&
                 !project.People.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            project.People.Add(name);
        }
        _settings.Save();
    }

    public void SetAssistantEnabled(bool enabled)
    {
        if (enabled && _settings.Assistant.Model == null)
        {
            // Nothing to run it with yet: let the user pick a model first.
            ShowAiSettings(_form);
            if (_settings.Assistant.Model == null)
                return;
        }
        _settings.Assistant.Enabled = enabled;
        _settings.Save();
        UpdateUi();
    }

    public void SetAssistantInterval(int seconds)
    {
        _settings.Assistant.IntervalSeconds = seconds;
        _settings.Save();
        UpdateUi();
    }

    public void ShowAiSettings(IWin32Window? owner)
    {
        using var dialog = new AiSettingsForm(_settings);
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            _settings.Save();
            UpdateUi();
        }
    }

    void StartSession(CallSession session)
    {
        Assistant?.Dispose();
        Session = session;
        Assistant = new CallAssistant(session, _settings);
        Assistant.Failed += message => OnUi(() => ReportError(T.ErrAssistant, message));
        SessionChanged?.Invoke();
    }

    // ---------- recording ----------

    public void Start()
    {
        if (_recorder != null)
            return;

        var project = _settings.FindProject(_settings.Project);
        var folder = project == null ? Folder : Path.Combine(Folder, project.FolderName);
        var file = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");
        var mode = _settings.Transcription;
        var session = new CallSession(file, project?.Name, _settings.MyName, isRecording: true);
        CallTracks? tracks = null;
        LiveTranscriber? live = null;
        try
        {
            if (mode != TranscriptionMode.Off)
                tracks = new CallTracks(Path.GetFileNameWithoutExtension(file));
            if (mode == TranscriptionMode.Live)
                live = new LiveTranscriber(_engine, session, message => OnUi(() => ReportError(T.ErrLive, message)));

            Action<AudioSource, float[], int>? tap = tracks == null ? null : (source, audio, count) =>
            {
                tracks.Feed(source, audio, count);
                live?.Feed(source, audio, count);
            };

            _recorder = new CallRecorder(file, tap, (message, fatal) => OnUi(() => OnRecorderError(message, fatal)));
        }
        catch (Exception ex)
        {
            live?.Abandon();
            tracks?.Delete();
            ReportError(T.ErrStart, ex.Message);
            return;
        }

        _tracks = tracks;
        _live = live;
        StartSession(session);

        if (_recorder.Warnings.Count > 0)
            ReportError(T.WarnPartialStart, string.Join("\n", _recorder.Warnings));

        UpdateUi();
    }

    void OnRecorderError(string message, bool fatal)
    {
        ReportError(T.ErrRecording, message);
        if (fatal)
            Stop();
    }

    public void TogglePause()
    {
        if (_recorder == null)
            return;

        if (_recorder.IsPaused) _recorder.Resume();
        else _recorder.Pause();
        UpdateUi();
    }

    public void Stop()
    {
        if (_recorder == null)
            return;

        var recorder = _recorder;
        var tracks = _tracks;
        var live = _live;
        var session = Session!;
        _recorder = null;
        _tracks = null;
        _live = null;

        bool saved = true;
        try
        {
            recorder.Dispose();
        }
        catch (Exception ex)
        {
            saved = false;
            AppLog.Write("Saving recording failed: " + ex);
            ReportError(T.ErrRecording, T.ErrSave + ex.Message);
        }

        try
        {
            tracks?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("Saving tracks failed: " + ex);
            tracks?.Delete();
            tracks = null;
        }

        _lastFile = recorder.FilePath;
        if (saved)
            ShowBalloon(T.RecordingSaved, Path.GetFileName(recorder.FilePath), ToolTipIcon.Info);

        if (live != null || (saved && _settings.Transcription == TranscriptionMode.AfterRecording))
        {
            session.State = SessionState.Transcribing;
            Enqueue(session, tracks, live);
        }
        else
        {
            session.State = SessionState.Done;
            tracks?.Delete();
        }

        UpdateUi();
    }

    // ---------- transcription ----------

    public TranscriptionMode Mode => _settings.Transcription;

    public void SetMode(TranscriptionMode mode)
    {
        _settings.Transcription = mode;
        _settings.Save();
        PrepareModels();
        UpdateUi();
    }

    /// <summary>Downloads missing models in advance. Whisper itself is loaded only when needed.</summary>
    void PrepareModels()
    {
        if (_settings.Transcription == TranscriptionMode.Off)
            return;

        _engine.EnsureModelsAsync().ContinueWith(
            t => OnUi(() => ReportError(T.ErrModels, t.Exception!.GetBaseException().Message)),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void PickFileToTranscribe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = T.PickFileTitle,
            Filter = $"{T.AudioFiles}|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|{T.AllFiles}|*.*",
            InitialDirectory = Directory.Exists(Folder) ? Folder : null
        };
        if (dialog.ShowDialog(_form) != DialogResult.OK)
            return;

        var session = new CallSession(dialog.FileName, _settings.Project, _settings.MyName, isRecording: false);
        if (_recorder == null)
            StartSession(session); // shown in the window unless a call is being recorded
        Enqueue(session, null, null);
    }

    void PickRecordingsFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = T.PickFolderTitle,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(Folder) ? Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        var folder = dialog.SelectedPath;
        try
        {
            // Make sure we can actually write there before remembering it.
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".aicallassistant-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            ReportError(T.RecordingsFolder.TrimEnd('…'), T.ErrCannotWrite(folder, ex.Message));
            return;
        }

        _settings.RecordingsFolder = string.Equals(folder.TrimEnd('\\'), AppSettings.DefaultRecordingsFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : folder;
        _settings.Save();
        _lastFile = null;
        UpdateUi();
    }

    /// <summary>
    /// Queues an accurate transcription. For a live session its draft is finished first
    /// and then replaced by the accurate version.
    /// </summary>
    void Enqueue(CallSession session, CallTracks? tracks, LiveTranscriber? live)
    {
        var name = Path.GetFileName(session.AudioPath);
        var previous = _queue;
        _pending++;
        UpdateUi();

        _queue = Task.Run(async () =>
        {
            try
            {
                if (live != null)
                {
                    SetJobStatus($"{name}: {T.StageFinishingDraft}", null);
                    await live.CompleteAsync();
                }

                await previous;
                var lines = await FileTranscriber.RunAsync(_engine, session.AudioPath,
                    (stage, fraction) => SetJobStatus($"{name}: {stage}", fraction), tracks);
                session.AdoptFinal(lines);
                session.State = SessionState.Done;

                OnUi(() =>
                {
                    _lastFile = session.TranscriptPath;
                    ShowBalloon(T.TranscriptReady, Path.GetFileName(session.TranscriptPath), ToolTipIcon.Info);
                    if (_settings.Assistant.Enabled && Session == session)
                        Assistant?.Finish();
                });
            }
            catch (Exception ex)
            {
                session.State = SessionState.Failed;
                AppLog.Write($"Transcription of {name} failed: {ex}");
                OnUi(() => ReportError(T.ErrTranscription(name), ex.GetBaseException().Message));
            }
            finally
            {
                tracks?.Delete();
                OnUi(() =>
                {
                    _pending--;
                    _jobStatus = null;
                    UpdateUi();
                });
            }
        });
    }

    void SetJobStatus(string text, double? fraction) => OnUi(() =>
    {
        _jobStatus = (text, fraction);
        UpdateUi();
    });

    // ---------- errors ----------

    /// <summary>Shows an error until the user looks at it: balloon, red badge on the icon and a menu item.</summary>
    public void ReportError(string title, string message)
    {
        AppLog.Write($"ERROR {title}: {message}");
        _errorText = $"{title}: {message}";
        _errorCount++;

        // Don't flood with balloons when the same error repeats.
        var key = _errorText;
        if (key != _lastBalloonKey || DateTime.Now - _lastBalloonAt > TimeSpan.FromMinutes(1))
        {
            _lastBalloonKey = key;
            _lastBalloonAt = DateTime.Now;
            ShowBalloon(title, message, ToolTipIcon.Error);
        }
        UpdateUi();
    }

    /// <summary>Thread-safe <see cref="ReportError"/>.</summary>
    public void PostError(string title, string message) => OnUi(() => ReportError(title, message));

    void OpenLogAndClearError()
    {
        _errorText = null;
        _errorCount = 0;
        UpdateUi();
        if (File.Exists(AppLog.FilePath))
            Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true });
    }

    void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _balloonIsError = icon == ToolTipIcon.Error;
        _tray.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] + "…" : text, icon);
    }

    // ---------- misc ----------

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (_lastFile != null && File.Exists(_lastFile))
                Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
            else
                Process.Start("explorer.exe", $"\"{Folder}\"");
        }
        catch (Exception ex)
        {
            ReportError(T.RecordingsFolder.TrimEnd('…'), ex.Message);
        }
    }

    void Exit()
    {
        if (_pending > 0 && MessageBox.Show(
                T.ExitConfirm, AppInfo.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        if (_recorder != null)
        {
            try { _recorder.Dispose(); }
            catch (Exception ex) { AppLog.Write("Saving recording on exit failed: " + ex); }
            _recorder = null;
        }

        if (_live != null)
        {
            // Let the draft get its last phrases.
            var live = _live;
            _live = null;
            try { Task.Run(live.CompleteAsync).Wait(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { AppLog.Write("Live transcription on exit: " + ex.Message); }
        }
        _tracks?.Delete();
        Assistant?.Dispose();

        _form?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    void UpdateUi()
    {
        bool active = _recorder != null;
        bool paused = _recorder?.IsPaused == true;

        _start.Enabled = !active;
        _pause.Enabled = active;
        _pause.Text = paused ? T.Resume : T.Pause;
        _stop.Enabled = active;

        _modeLive.Checked = _settings.Transcription == TranscriptionMode.Live;
        _modeAfter.Checked = _settings.Transcription == TranscriptionMode.AfterRecording;
        _modeOff.Checked = _settings.Transcription == TranscriptionMode.Off;
        _aiEnabled.Checked = _settings.Assistant.Enabled;
        _folderItem.ToolTipText = T.CurrentFolder(Folder) + (active ? "\n" + T.FolderNextRecording : "");

        // What is being processed: model download/loading has priority over the job stage.
        var work = _engineStatus ?? _jobStatus ?? (_pending > 0 ? (T.Queued, null) : null);
        Work = work;
        string? workText = null;
        if (work is { } w)
        {
            workText = w.Fraction is { } f ? $"{w.Text} — {f:P0}" : w.Text;
            if (_pending > 1)
                workText += T.MoreQueued(_pending - 1);
        }
        _status.Visible = workText != null;
        _status.Text = workText ?? "";

        _error.Visible = _errorText != null;
        _error.Text = _errorText == null ? "" :
            "⚠ " + Shorten(_errorText, 70) + (_errorCount > 1 ? T.ErrorCount(_errorCount) : "") + T.OpenLog;
        _error.ToolTipText = _errorText;

        string state;
        Color? color = null;
        if (!active)
        {
            state = T.StateIdle;
            if (work != null)
                color = Color.FromArgb(140, 140, 140);
        }
        else
        {
            var d = _recorder!.Duration;
            state = $"{(paused ? T.StatePaused : T.StateRecording)} {Transcript.Time(d)}";
            color = paused ? Color.FromArgb(240, 170, 0) : Color.FromArgb(220, 40, 40);
        }

        _tray.Icon = GetIcon(color, work?.Fraction, work != null, _errorText != null);

        var tip = $"{AppInfo.Name} — {state}";
        if (workText != null)
            tip += "\n" + workText;
        if (_errorText != null)
            tip += "\n" + T.TipError;
        _tray.Text = Shorten(tip, 127);

        // The timer drives the duration counter, the spinner and the assistant schedule.
        _tick.Enabled = active || work != null;

        StateChanged?.Invoke();
    }

    static string Shorten(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    // ---------- icon ----------

    /// <param name="color">Dot color while recording, paused or busy; null — the app icon.</param>
    Icon GetIcon(Color? color, double? fraction, bool busy, bool error)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        // Progress rounded to 5% so only a few dozen icons are ever drawn; unknown progress spins.
        string ring = !busy ? "-" : fraction is { } f ? $"p{(int)(f * 20)}" : $"s{_animation % 8}";
        var key = $"{size}|{color?.ToArgb()}|{ring}|{error}";
        if (!_icons.TryGetValue(key, out var icon))
            _icons[key] = icon = DrawIcon(size, color, busy, fraction, _animation % 8, error);
        return icon;
    }

    static Icon DrawIcon(int size, Color? color, bool busy, double? fraction, int spinPhase, bool error)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            // Everything is drawn in 32×32 units and scaled to the size the taskbar wants at this DPI.
            g.ScaleTransform(size / 32f, size / 32f);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (color is not { } c)
            {
                AppIcon.Paint(g);
            }
            else if (busy)
            {
                // Smaller dot inside a progress ring.
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, 9, 9, 14, 14);
                using var track = new Pen(Color.FromArgb(110, 128, 128, 128), 5);
                g.DrawEllipse(track, 3.5f, 3.5f, 25, 25);
                using var progress = new Pen(Color.FromArgb(0, 120, 215), 5);
                if (fraction is { } f)
                {
                    if (f > 0)
                        g.DrawArc(progress, 3.5f, 3.5f, 25, 25, -90, (float)(360 * Math.Min(f, 1)));
                }
                else
                {
                    g.DrawArc(progress, 3.5f, 3.5f, 25, 25, -90 + spinPhase * 45, 100);
                }
            }
            else
            {
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, 3, 3, 26, 26);
                using var pen = new Pen(Color.White, 2.5f);
                g.DrawEllipse(pen, 3, 3, 26, 26);
            }

            if (error)
            {
                // Red badge with "!" in the bottom-right corner.
                using var badge = new SolidBrush(Color.FromArgb(220, 20, 20));
                using var border = new Pen(Color.White, 1.5f);
                g.FillEllipse(badge, 17, 17, 14, 14);
                g.DrawEllipse(border, 17, 17, 14, 14);
                using var white = new SolidBrush(Color.White);
                g.FillRectangle(white, 23, 19.5f, 2, 6);
                g.FillRectangle(white, 23, 26.5f, 2, 2);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _showWait.Unregister(null);
            _recorder?.Dispose();
            Assistant?.Dispose();
            _tick.Dispose();
            _tray.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }
}
