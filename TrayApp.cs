using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SoundRecord;

sealed class TrayApp : ApplicationContext
{
    readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sound Records");

    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _start, _pause, _stop;
    readonly System.Windows.Forms.Timer _tick;
    readonly Icon _iconIdle, _iconRec, _iconPaused;

    CallRecorder? _recorder;
    string? _lastFile;

    public TrayApp()
    {
        _iconIdle = MakeIcon(Color.FromArgb(140, 140, 140));
        _iconRec = MakeIcon(Color.FromArgb(220, 40, 40));
        _iconPaused = MakeIcon(Color.FromArgb(240, 170, 0));

        _start = new ToolStripMenuItem("Начать запись", null, (_, _) => Start());
        _pause = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _stop = new ToolStripMenuItem("Стоп", null, (_, _) => Stop());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _start, _pause, _stop,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Открыть папку", null, (_, _) => OpenFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => Exit())
        });

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenFolder();
        _tray.BalloonTipClicked += (_, _) => OpenFolder();

        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += (_, _) => UpdateUi();

        UpdateUi();
    }

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

        _tray.ShowBalloonTip(3000, "Запись сохранена", Path.GetFileName(file), ToolTipIcon.Info);
        UpdateUi();
    }

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

        if (!active)
        {
            _tray.Icon = _iconIdle;
            _tray.Text = "Sound Record — ожидание";
        }
        else
        {
            var d = _recorder!.Duration;
            _tray.Icon = paused ? _iconPaused : _iconRec;
            _tray.Text = $"Sound Record — {(paused ? "пауза" : "запись")} {(int)d.TotalHours:00}:{d:mm\\:ss}";
        }
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
        }
        base.Dispose(disposing);
    }
}
