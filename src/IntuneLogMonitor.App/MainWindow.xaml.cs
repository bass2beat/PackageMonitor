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
        _appWorkload = new AppWorkloadViewModel(options);
        AppWorkloadView.DataContext = _appWorkload;
        Loaded += (_, _) => _appWorkload.Start();
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
