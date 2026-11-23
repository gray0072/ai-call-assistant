namespace SoundRecord;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\SoundRecord.SingleInstance", out bool isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // The process is about to die: at least say why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Write("FATAL: " + e.ExceptionObject);
            MessageBox.Show(Strings.Current.Fatal((e.ExceptionObject as Exception)?.Message ?? "", AppLog.FilePath),
                "Sound Record", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        var app = new TrayApp();
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
