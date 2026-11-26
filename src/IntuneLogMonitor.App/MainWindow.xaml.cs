using System.ComponentModel;
using System.Windows;
using IntuneLogMonitor.App.ViewModels;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App;

public partial class MainWindow : Window
{
    private readonly AppWorkloadViewModel _appWorkload;

    public MainWindow(AppWorkloadMonitorOptions options)
    {
        InitializeComponent();
        Title = WindowTitle;
        _appWorkload = new AppWorkloadViewModel(options);
        AppWorkloadView.DataContext = _appWorkload;
        Loaded += (_, _) => _appWorkload.Start();
    }

    /// <summary>"PackageMonitor v0.2.1 by DPS" – the version comes from &lt;Version&gt; in the project file (patch number only if not 0).</summary>
    public static string WindowTitle
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            string number = v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
            return $"PackageMonitor v{number} by DPS";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Monitore sauber beenden (Datei-Handle, Watcher, laufender PowerShell-Prozess).
        // Auf dem Threadpool, damit kein await auf den hier blockierten UI-Thread zurückwill.
        _appWorkload.StopUi();
        Task.Run(async () => await _appWorkload.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
    }
}
