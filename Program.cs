namespace NvidiaBuildApp;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        Application.ThreadException += (s, e) =>
        {
            try { MessageBox.Show(e.Exception.Message, "NVIDIA Build App - Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { MessageBox.Show(e.ExceptionObject?.ToString() ?? "?", "NVIDIA Build App - Erreur"); } catch { }
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Store.EnsureCreated();
        Application.Run(new MainForm());
    }
}


