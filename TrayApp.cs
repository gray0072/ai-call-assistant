using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SoundRecord;

sealed class TrayApp : ApplicationContext
{
    readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sound Records");

    readonly AppSettings _settings = AppSettings.Load();
    readonly SpeechEngine _engine = new();
    readonly SynchronizationContext _ui;

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _status, _start, _pause, _stop;
    readonly ToolStripMenuItem _modeLive, _modeAfter, _modeOff;
    readonly System.Windows.Forms.Timer _tick;
    readonly Icon _iconIdle, _iconRec, _iconPaused;

    CallRecorder? _recorder;
    LiveTranscriber? _live;
    string? _lastFile;
    string? _engineStatus, _jobStatus;
    Task _transcriptions = Task.CompletedTask; // file transcriptions run one after another

    public TrayApp()
    {
        _iconIdle = MakeIcon(Color.FromArgb(140, 140, 140));
        _iconRec = MakeIcon(Color.FromArgb(220, 40, 40));
        _iconPaused = MakeIcon(Color.FromArgb(240, 170, 0));

        _status = new ToolStripMenuItem { Enabled = false, Visible = false };
        _start = new ToolStripMenuItem("Начать запись", null, (_, _) => Start());
        _pause = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _stop = new ToolStripMenuItem("Стоп", null, (_, _) => Stop());

        _modeLive = new ToolStripMenuItem("Онлайн, во время записи", null, (_, _) => SetMode(TranscriptionMode.Live));
        _modeAfter = new ToolStripMenuItem("После записи", null, (_, _) => SetMode(TranscriptionMode.AfterRecording));
        _modeOff = new ToolStripMenuItem("Отключено", null, (_, _) => SetMode(TranscriptionMode.Off));
        var modeMenu = new ToolStripMenuItem("Распознавание речи");
        modeMenu.DropDownItems.AddRange(new ToolStripItem[] { _modeLive, _modeAfter, _modeOff });

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _status,
            _start, _pause, _stop,
            new ToolStripSeparator(),
            modeMenu,
            new ToolStripMenuItem("Распознать файл…", null, (_, _) => PickFileToTranscribe()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Открыть папку", null, (_, _) => OpenFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => Exit())
        });

        // Creating the menu installs the WinForms synchronization context on this thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _engine.StatusChanged += s => _ui.Post(_ => { _engineStatus = s; UpdateUi(); }, null);

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) => OpenFolder();

        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += (_, _) => UpdateUi();

        PrepareEngine();
        UpdateUi();
    }

    // ---------- recording ----------

    void Start()
    {
        if (_recorder != null)
            return;

        var file = Path.Combine(_folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");
        try
        {
            _recorder = new CallRecorder(file);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Не удалось начать запись", ex.Message, ToolTipIcon.Error);
            return;
        }

        if (_settings.Transcription == TranscriptionMode.Live)
        {
            _live = new LiveTranscriber(_engine, Path.ChangeExtension(file, ".txt"));
            _recorder.Tap = _live.Feed;
        }

        if (_recorder.Warnings.Count > 0)
            _tray.ShowBalloonTip(5000, "Запись начата частично", string.Join("\n", _recorder.Warnings), ToolTipIcon.Warning);

        _tick.Start();
        UpdateUi();
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

        _tick.Stop();
        var file = _recorder.FilePath;
        _recorder.Dispose();
        _recorder = null;
        _lastFile = file;

        var live = _live;
        _live = null;

        _tray.ShowBalloonTip(3000, "Запись сохранена", Path.GetFileName(file), ToolTipIcon.Info);
        UpdateUi();

        if (live != null)
            Track(Path.GetFileName(file), () => FinishLive(live));
        else if (_settings.Transcription == TranscriptionMode.AfterRecording)
            EnqueueFile(file);
    }

    async Task<string> FinishLive(LiveTranscriber live)
    {
        SetJobStatus("Дописываю расшифровку…");
        await live.CompleteAsync();
        return live.FilePath;
    }

    // ---------- transcription ----------

    void SetMode(TranscriptionMode mode)
    {
        _settings.Transcription = mode;
        _settings.Save();
        PrepareEngine();
        UpdateUi();
    }

    /// <summary>Downloads the models in advance; for live mode also loads Whisper so the first phrase is not delayed.</summary>
    void PrepareEngine()
    {
        var mode = _settings.Transcription;
        if (mode == TranscriptionMode.Off)
            return;

        var task = mode == TranscriptionMode.Live ? _engine.EnsureLoadedAsync() : _engine.EnsureModelsAsync();
        task.ContinueWith(t => _ui.Post(_ =>
            _tray.ShowBalloonTip(5000, "Модели распознавания недоступны",
                t.Exception!.GetBaseException().Message, ToolTipIcon.Error), null),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    void PickFileToTranscribe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Файл для распознавания",
            Filter = "Аудио|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|Все файлы|*.*",
            InitialDirectory = Directory.Exists(_folder) ? _folder : null
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            EnqueueFile(dialog.FileName);
    }

    void EnqueueFile(string audioPath)
    {
        var name = Path.GetFileName(audioPath);
        var previous = _transcriptions;
        _transcriptions = Track(name, async () =>
        {
            await previous;
            return await FileTranscriber.RunAsync(_engine, audioPath, SetJobStatus);
        });
    }

    /// <summary>Runs a transcription in the background and reports its result in the tray.</summary>
    Task Track(string name, Func<Task<string>> work) => Task.Run(async () =>
    {
        try
        {
            var transcript = await work();
            _ui.Post(_ =>
            {
                _lastFile = transcript;
                _tray.ShowBalloonTip(3000, "Расшифровка готова", Path.GetFileName(transcript), ToolTipIcon.Info);
            }, null);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Transcription of {name} failed: {ex}");
            _ui.Post(_ => _tray.ShowBalloonTip(5000, "Ошибка распознавания",
                $"{name}: {ex.GetBaseException().Message}", ToolTipIcon.Error), null);
        }
        finally
        {
            SetJobStatus(null);
        }
    });

    void SetJobStatus(string? status) => _ui.Post(_ => { _jobStatus = status; UpdateUi(); }, null);

    // ---------- misc ----------

    void OpenFolder()
    {
        Directory.CreateDirectory(_folder);
        if (_lastFile != null && File.Exists(_lastFile))
            Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        else
            Process.Start("explorer.exe", $"\"{_folder}\"");
    }

    void Exit()
    {
        if (_recorder != null)
        {
            _tick.Stop();
            _recorder.Dispose();
            _recorder = null;
        }

        if (_live != null)
        {
            // Give the last phrases a chance to reach the transcript.
            var live = _live;
            _live = null;
            try { Task.Run(live.CompleteAsync).Wait(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { AppLog.Write("Live transcription on exit: " + ex.Message); }
        }

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

        var status = _engineStatus ?? _jobStatus;
        _status.Visible = status != null;
        _status.Text = status ?? "";

        string state;
        if (!active)
        {
            _tray.Icon = _iconIdle;
            state = "ожидание";
        }
        else
        {
            var d = _recorder!.Duration;
            _tray.Icon = paused ? _iconPaused : _iconRec;
            state = $"{(paused ? "пауза" : "запись")} {(int)d.TotalHours:00}:{d:mm\\:ss}";
        }

        var text = $"Sound Record — {state}" + (status != null ? "\n" + status : "");
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.White, 2.5f);
            g.DrawEllipse(pen, 3, 3, 26, 26);
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
