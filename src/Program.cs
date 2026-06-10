using System.Windows.Forms;

namespace WinEML;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Handle association registration without spinning up the UI.
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--register":
                    FileAssociation.Register();
                    return;
                case "--unregister":
                    FileAssociation.Unregister();
                    return;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);

        Telemetry.Mark("main");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string? path = args.Length > 0 ? args[0] : null;
        try
        {
            Application.Run(new MainForm(path));
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "WinEML", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"=== {DateTime.Now:o} ===\n{ex}\n\n");
        }
        catch { /* nothing we can do */ }
    }
}
