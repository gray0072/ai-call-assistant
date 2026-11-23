using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SoundRecord;

sealed class TrayApp : ApplicationContext
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly SpeechEngine _engine = new();
    readonly SynchronizationContext _ui;

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _error, _status, _start, _pause, _stop;
    readonly ToolStripMenuItem _modeLive, _modeAfter, _modeOff, _folderItem;
    readonly System.Windows.Forms.Timer _tick;
    readonly Dictionary<string, Icon> _icons = new();

    CallRecorder? _recorder;
    LiveTranscriber? _live;
    CallTracks? _tracks;
    string? _lastFile;

    (string Text, double? Fraction)? _engineStatus, _jobStatus;
    Task _queue = Task.CompletedTask; // transcriptions run one after another
    int _pending;
    int _animation;

    string? _errorText;
    int _errorCount;
    string? _lastBalloonKey;
    DateTime _lastBalloonAt;
    bool _balloonIsError;

    public TrayApp()
    {
        _error = new ToolStripMenuItem { Visible = false, ForeColor = Color.Firebrick };
        _error.Click += (_, _) => OpenLogAndClearError();
        _status = new ToolStripMenuItem { Enabled = false, Visible = false };
        _start = new ToolStripMenuItem("Начать запись", null, (_, _) => Start());
        _pause = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _stop = new ToolStripMenuItem("Стоп", null, (_, _) => Stop());

        _modeLive = new ToolStripMenuItem("Онлайн, во время записи", null, (_, _) => SetMode(TranscriptionMode.Live));
        _modeAfter = new ToolStripMenuItem("После записи", null, (_, _) => SetMode(TranscriptionMode.AfterRecording));
        _modeOff = new ToolStripMenuItem("Отключено", null, (_, _) => SetMode(TranscriptionMode.Off));
        var modeMenu = new ToolStripMenuItem("Распознавание речи");
        modeMenu.DropDownItems.AddRange(new ToolStripItem[] { _modeLive, _modeAfter, _modeOff });

        _folderItem = new ToolStripMenuItem("Папка для записей…", null, (_, _) => PickRecordingsFolder());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _error, _status,
            _start, _pause, _stop,
            new ToolStripSeparator(),
            modeMenu,
            new ToolStripMenuItem("Распознать файл…", null, (_, _) => PickFileToTranscribe()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Открыть папку", null, (_, _) => OpenFolder()),
            _folderItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => Exit())
        });

        // Creating the menu installs the WinForms synchronization context on this thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _engine.StatusChanged += (text, fraction) => OnUi(() =>
        {
            _engineStatus = text == null ? null : (text, fraction);
            UpdateUi();
        });

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonIsError) OpenLogAndClearError();
            else OpenFolder();
        };

        _tick = new System.Windows.Forms.Timer { Interval = 400 };
        _tick.Tick += (_, _) =>
        {
            _animation++;
            UpdateUi();
        };

        CallTracks.DeleteLeftovers();
        PrepareModels();
        UpdateUi();
    }

    void OnUi(Action action) => _ui.Post(_ => action(), null);

    string Folder => _settings.RecordingsFolder ?? AppSettings.DefaultRecordingsFolder;

    // ---------- recording ----------

    void Start()
    {
        if (_recorder != null)
            return;

        var file = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");
        var mode = _settings.Transcription;
        CallTracks? tracks = null;
        LiveTranscriber? live = null;
        try
        {
            if (mode != TranscriptionMode.Off)
                tracks = new CallTracks(Path.GetFileNameWithoutExtension(file));
            if (mode == TranscriptionMode.Live)
                live = new LiveTranscriber(_engine, Path.ChangeExtension(file, ".txt"),
                    message => OnUi(() => ReportError("Онлайн-распознавание", message)));

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
            ReportError("Не удалось начать запись", ex.Message);
            return;
        }

        _tracks = tracks;
        _live = live;

        if (_recorder.Warnings.Count > 0)
            ReportError("Запись начата частично", string.Join("\n", _recorder.Warnings));

        UpdateUi();
    }

    void OnRecorderError(string message, bool fatal)
    {
        ReportError("Запись", message);
        if (fatal)
            Stop();
    }

    void TogglePause()
    {
        if (_recorder == null)
            return;

        if (_recorder.IsPaused) _recorder.Resume();
        else _recorder.Pause();
        UpdateUi();
    }

    void Stop()
    {
        if (_recorder == null)
            return;

        var recorder = _recorder;
        var tracks = _tracks;
        var live = _live;
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
            ReportError("Запись", "Не удалось сохранить файл: " + ex.Message);
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
            ShowBalloon("Запись сохранена", Path.GetFileName(recorder.FilePath), ToolTipIcon.Info);

        if (live != null || (saved && _settings.Transcription == TranscriptionMode.AfterRecording))
            Enqueue(recorder.FilePath, tracks, live);
        else
            tracks?.Delete();

        UpdateUi();
    }

    // ---------- transcription ----------

    void SetMode(TranscriptionMode mode)
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
            t => OnUi(() => ReportError("Модели распознавания", t.Exception!.GetBaseException().Message)),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    void PickFileToTranscribe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Файл для распознавания",
            Filter = "Аудио|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|Все файлы|*.*",
            InitialDirectory = Directory.Exists(Folder) ? Folder : null
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            Enqueue(dialog.FileName, null, null);
    }

    void PickRecordingsFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Куда сохранять записи и расшифровки",
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
            var probe = Path.Combine(folder, ".soundrecord-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            ReportError("Папка для записей", $"Нельзя писать в {folder}: {ex.Message}");
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
    void Enqueue(string audioPath, CallTracks? tracks, LiveTranscriber? live)
    {
        var name = Path.GetFileName(audioPath);
        var previous = _queue;
        _pending++;
        UpdateUi();

        _queue = Task.Run(async () =>
        {
            try
            {
                if (live != null)
                {
                    SetJobStatus($"{name}: дописываю черновик", null);
                    await live.CompleteAsync();
                }

                await previous;
                var transcript = await FileTranscriber.RunAsync(_engine, audioPath,
                    (stage, fraction) => SetJobStatus($"{name}: {stage}", fraction), tracks);

                OnUi(() =>
                {
                    _lastFile = transcript;
                    ShowBalloon("Расшифровка готова", Path.GetFileName(transcript), ToolTipIcon.Info);
                });
            }
            catch (Exception ex)
            {
                AppLog.Write($"Transcription of {name} failed: {ex}");
                OnUi(() => ReportError("Распознавание " + name, ex.GetBaseException().Message));
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

    void OpenFolder()
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
            ReportError("Папка для записей", ex.Message);
        }
    }

    void Exit()
    {
        if (_pending > 0 && MessageBox.Show(
                "Расшифровка ещё не готова и будет прервана. Выйти?", "Sound Record",
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

        _tray.Visible = false;
        ExitThread();
    }

    void UpdateUi()
    {
        bool active = _recorder != null;
        bool paused = _recorder?.IsPaused == true;

        _start.Enabled = !active;
        _pause.Enabled = active;
        _pause.Text = paused ? "Продолжить" : "Пауза";
        _stop.Enabled = active;

        _modeLive.Checked = _settings.Transcription == TranscriptionMode.Live;
        _modeAfter.Checked = _settings.Transcription == TranscriptionMode.AfterRecording;
        _modeOff.Checked = _settings.Transcription == TranscriptionMode.Off;
        _folderItem.ToolTipText = "Сейчас: " + Folder + (active ? "\nНовая папка будет использована со следующей записи." : "");

        // What is being processed: model download/loading has priority over the job stage.
        var work = _engineStatus ?? _jobStatus ?? (_pending > 0 ? ("Распознавание в очереди", null) : null);
        string? workText = null;
        if (work is { } w)
        {
            workText = w.Fraction is { } f ? $"{w.Text} — {f:P0}" : w.Text;
            if (_pending > 1)
                workText += $" (ещё в очереди: {_pending - 1})";
        }
        _status.Visible = workText != null;
        _status.Text = workText ?? "";

        _error.Visible = _errorText != null;
        _error.Text = _errorText == null ? "" :
            "⚠ " + Shorten(_errorText, 70) + (_errorCount > 1 ? $" (ошибок: {_errorCount})" : "") + " — открыть лог";
        _error.ToolTipText = _errorText;

        string state;
        Color color;
        if (!active)
        {
            state = "ожидание";
            color = Color.FromArgb(140, 140, 140);
        }
        else
        {
            var d = _recorder!.Duration;
            state = $"{(paused ? "пауза" : "запись")} {(int)d.TotalHours:00}:{d:mm\\:ss}";
            color = paused ? Color.FromArgb(240, 170, 0) : Color.FromArgb(220, 40, 40);
        }

        _tray.Icon = GetIcon(color, work?.Fraction, work != null, _errorText != null);

        var tip = $"Sound Record — {state}";
        if (workText != null)
            tip += "\n" + workText;
        if (_errorText != null)
            tip += "\n⚠ ошибка — см. меню";
        _tray.Text = Shorten(tip, 127);

        // The timer drives the duration counter and the spinner.
        _tick.Enabled = active || work != null;
    }

    static string Shorten(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    // ---------- icon ----------

    Icon GetIcon(Color color, double? fraction, bool busy, bool error)
    {
        // Progress rounded to 5% so only a few dozen icons are ever drawn; unknown progress spins.
        string ring = !busy ? "-" : fraction is { } f ? $"p{(int)(f * 20)}" : $"s{_animation % 8}";
        var key = $"{color.ToArgb()}|{ring}|{error}";
        if (!_icons.TryGetValue(key, out var icon))
            _icons[key] = icon = DrawIcon(color, busy, fraction, _animation % 8, error);
        return icon;
    }

    static Icon DrawIcon(Color color, bool busy, double? fraction, int spinPhase, bool error)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(color);
            if (busy)
            {
                // Smaller dot inside a progress ring.
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
            _recorder?.Dispose();
            _tick.Dispose();
            _tray.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }
}
