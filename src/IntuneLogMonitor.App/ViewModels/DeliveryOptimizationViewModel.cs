using System.Windows.Input;
using System.Windows.Threading;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App.ViewModels;

/// <summary>Zusammenfassung von Get-DeliveryOptimizationStatus und der Cache-Größen.</summary>
public sealed class DeliveryOptimizationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeliveryOptimizationMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly bool _enabled;
    private DeliveryOptimizationReport? _report;
    private bool _isRefreshing = true;

    public DeliveryOptimizationViewModel(DeliveryOptimizationOptions options)
    {
        _enabled = options.Enabled;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _monitor = new DeliveryOptimizationMonitor(options);
        _monitor.Updated += r => _dispatcher.BeginInvoke(() => Apply(r));
        _monitor.FoldersUpdated += (f, at) => _dispatcher.BeginInvoke(() => ApplyFolders(f, at));
        RefreshCommand = new RelayCommand(Refresh, () => _enabled && !_isRefreshing);
    }

    public ICommand RefreshCommand { get; }

    public bool IsEnabled => _enabled;

    public void Start() => _monitor.Start();

    private void Refresh()
    {
        IsRefreshing = true;
        _monitor.RefreshNow();
    }

    private void Apply(DeliveryOptimizationReport report)
    {
        _report = report;
        IsRefreshing = false;
        OnPropertyChanged(string.Empty);
    }

    private IReadOnlyList<FolderSize> _folders = [];
    private DateTime? _foldersAt;

    private void ApplyFolders(IReadOnlyList<FolderSize> folders, DateTime at)
    {
        _folders = folders;
        _foldersAt = at;
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(FoldersTotalText));
        OnPropertyChanged(nameof(FoldersStateText));
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetField(ref _isRefreshing, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    private DoSummary S => _report?.Summary ?? new DoSummary();
    private static string B(long bytes) => Formatting.FormatBytes(bytes);

    public bool HasReport => _report is not null;

    public string StateText =>
        !_enabled ? "Abfrage deaktiviert (appsettings.json)"
        : _isRefreshing ? "Wird abgefragt …"
        : _report is null ? ""
        : $"Stand {_report.CollectedAt:HH:mm:ss}";

    // --- Peers ---
    public string PeersConnectedText => _report is null ? "–" : $"{S.PeersConnected}";
    public string PeersDetailText => _report is null ? "" : $"(davon bei aktiv ladenden Jobs: {S.PeersConnectedActiveJobs})";
    public string PeerConnectionsText => _report is null ? "–"
        : $"LAN {S.LanConnections} · Gruppe {S.GroupConnections} · Internet {S.InternetConnections} · Link-Local {S.LinkLocalConnections}";
    public string SourceConnectionsText => _report is null ? "–"
        : $"HTTP/CDN {S.HttpConnections} · Connected Cache {S.CacheServerConnections}";
    public string JobsText => _report is null ? "–" : $"{S.JobCount} (aktiv ladend: {S.ActiveJobCount})";

    // --- Datenmengen ---
    public string TotalDownloadedText => _report is null ? "–" : B(S.TotalBytesDownloaded);
    public string FromPeersText => _report is null ? "–"
        : $"{B(S.BytesFromPeers)}{(S.PeerSharePercent is { } p ? $" ({p:0.0} %)" : "")}";
    public string FromPeersDetailText => _report is null ? ""
        : $"LAN {B(S.BytesFromLanPeers)} · Gruppe {B(S.BytesFromGroupPeers)} · Internet {B(S.BytesFromInternetPeers)} · Link-Local {B(S.BytesFromLinkLocalPeers)}";
    public string FromHttpText => _report is null ? "–"
        : $"{B(S.BytesFromHttp)} (davon Connected Cache {B(S.BytesFromCacheServer)})";
    public string UploadedText => _report is null ? "–" : B(S.BytesToPeers);

    // --- DO-Dienst gesamt (PerfSnap) ---
    public string PerfSnapText => _report?.PerfSnap is not { } p ? "–"
        : $"Cache laut DO {B(p.CacheSizeBytes)} ({p.Files} Dateien) · {B(p.TotalBytesDownloaded)} geladen · {B(p.TotalBytesUploaded)} hochgeladen · Peers {p.NumberOfPeers} · Modus {p.DownloadMode}";

    /// <summary>Cache-Ordner, gemessen im eigenen (kürzeren) Intervall.</summary>
    public IReadOnlyList<FolderSize> Folders => _folders;
    public long FoldersTotalBytes => _folders.Sum(f => f.Bytes);
    public string FoldersTotalText => _foldersAt is null ? "" : $"Summe belegt (lesbare Ordner): {B(FoldersTotalBytes)}";
    public string FoldersStateText => _foldersAt is { } at ? $"Stand {at:HH:mm:ss}" : "Wird gemessen …";

    public IReadOnlyList<DoJob> Jobs => _report?.Jobs ?? [];
    public string JobsHeader => $"DO-Jobs ({Jobs.Count})";

    public string ErrorText => _report is null ? "" : string.Join(Environment.NewLine, _report.Errors);
    public bool HasErrors => _report is { Errors.Count: > 0 };

    public ValueTask DisposeAsync() => _monitor.DisposeAsync();
}
