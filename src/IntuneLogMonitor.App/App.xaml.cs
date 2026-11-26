using System.Windows;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Alle Einstellungen (Logpfad, Muster, Timeouts, Cache-Ordner) sind fest im Code hinterlegt (siehe Options.cs).
        var options = new AppWorkloadMonitorOptions();

        // Optional: --logfile <Pfad> überschreibt den Logpfad (z. B. für den Simulator oder eine kopierte Datei).
        int idx = Array.FindIndex(e.Args, a => a.Equals("--logfile", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < e.Args.Length)
            options.LogFilePath = e.Args[idx + 1];

        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }
}
