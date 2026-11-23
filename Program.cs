namespace AiCallAssistant;

static class AppInfo
{
    public const string Name = "AI Call Assistant";
}

static class Program
{
    [STAThread]
    static void Main()
    {
        // Starting the app again (e.g. from the desktop shortcut) brings up the running one's window.
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AiCallAssistant.Show");
        using var mutex = new Mutex(true, @"Local\AiCallAssistant.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            showSignal.Set();
            return;
        }

        // Per-monitor DPI awareness, visual styles and the default font come from the .csproj.
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // The process is about to die: at least say why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Write("FATAL: " + e.ExceptionObject);
            MessageBox.Show(Strings.Current.Fatal((e.ExceptionObject as Exception)?.Message ?? "", AppLog.FilePath),
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        var app = new TrayApp(showSignal);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Write("UI error: " + e.Exception);
            app.ReportError(Strings.Current.ErrInternal, e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Background error: " + e.Exception);
            app.PostError(Strings.Current.ErrInternal, e.Exception.GetBaseException().Message);
            e.SetObserved();
        };

        Application.Run(app);
    }
}
