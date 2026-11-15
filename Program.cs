using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NvidiaBuildApp;

static class Program
{
    static Mutex? _instanceMutex;

    [STAThread]
    static void Main()
    {
        // une seule instance : evite deux ecrivains sur les memes fichiers JSON
        _instanceMutex = new Mutex(true, @"Local\NvidiaBuildApp.SingleInstance", out bool created);
        if (!created)
        {
            ActivateExisting();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (s, e) =>
        {
            try { MessageBox.Show(e.Exception.Message, "NVIDIA Build App", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { MessageBox.Show(e.ExceptionObject?.ToString() ?? "?", "NVIDIA Build App"); } catch { }
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            Store.EnsureCreated();
            Application.Run(new MainForm());
        }
        finally
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
        }
    }

    static void ActivateExisting()
    {
        try
        {
            var other = Process.GetProcessesByName("NvidiaBuildApp")
                .FirstOrDefault(p => p.Id != Environment.ProcessId);
            if (other?.MainWindowHandle is IntPtr h && h != IntPtr.Zero)
            {
                ShowWindow(h, 9);   // SW_RESTORE
                SetForegroundWindow(h);
                return;
            }
        }
        catch { }
        try { MessageBox.Show(Loc.S("alreadyRunning"), "NVIDIA Build App", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
