using System.Windows;
using IntuneLogMonitor.Core;
using Microsoft.Extensions.Configuration;

namespace IntuneLogMonitor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppWorkloadMonitorOptions options;
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();
            options = configuration.GetSection(AppWorkloadMonitorOptions.SectionName).Get<AppWorkloadMonitorOptions>()
                      ?? new AppWorkloadMonitorOptions();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"appsettings.json konnte nicht gelesen werden:\n{ex.Message}", "Package Monitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Optional: --logfile <Pfad> überschreibt den konfigurierten Pfad (z. B. für den Simulator).
        int idx = Array.FindIndex(e.Args, a => a.Equals("--logfile", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < e.Args.Length)
            options.LogFilePath = e.Args[idx + 1];

        try
        {
            var window = new MainWindow(options);
            MainWindow = window;
            window.Show();
        }
        catch (ArgumentException ex)
        {
            // z. B. ungültiger regulärer Ausdruck in appsettings.json
            MessageBox.Show($"Ungültige Konfiguration:\n{ex.Message}", "Package Monitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
