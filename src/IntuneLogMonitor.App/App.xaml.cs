using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // English UI: numbers, sizes and dates always in English format ("1.20 GB"), regardless of the Windows locale.
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(english.IetfLanguageTag)));

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
