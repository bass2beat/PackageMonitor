using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App.ViewModels;

/// <summary>
/// ViewModel für Bereich C. Updates des Monitors kommen auf einem Hintergrundthread an, werden
/// gepuffert und von einem DispatcherTimer gebündelt auf dem UI-Thread übernommen.
/// </summary>
public sealed class AppWorkloadViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppWorkloadMonitor _monitor;
    private readonly int _maxEntries;
    private readonly ConcurrentQueue<MonitorUpdate> _pending = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Dictionary<int, DownloadItemViewModel> _downloadsById = [];

    private DownloadItemViewModel? _selectedDownload;
    private LogEntry? _selectedEntry;
    private bool _showContextRange;
    private bool _autoScroll = true;
    private string _statusText = "Starte …";
    private int _rolloverCount;
    private long _totalEntriesRead;

    public AppWorkloadViewModel(AppWorkloadMonitorOptions options)
    {
        _maxEntries = Math.Max(1000, options.MaxLogEntries);
        _monitor = new AppWorkloadMonitor(options);
        _monitor.Updated += u => _pending.Enqueue(u);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => ApplyPendingUpdates();

        ClearFilterCommand = new RelayCommand(() => SelectedDownload = null, () => SelectedDownload is not null);
        DeliveryOptimization = new DeliveryOptimizationViewModel(options.DeliveryOptimization);
    }

    /// <summary>Zusammenfassung Get-DeliveryOptimizationStatus und Cache-Größen.</summary>
    public DeliveryOptimizationViewModel DeliveryOptimization { get; }

    public string FilePath => _monitor.FilePath;

    public BulkObservableCollection<LogEntry> AllEntries { get; } = [];
    public BulkObservableCollection<LogEntry> FilteredEntries { get; } = [];
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    /// <summary>Quelle der Logtabelle: alle Zeilen oder nur die des ausgewählten Downloads.</summary>
    public IEnumerable VisibleEntries => _selectedDownload is null ? AllEntries : FilteredEntries;

    public ICommand ClearFilterCommand { get; }

    /// <summary>Wird ausgelöst, nachdem neue Zeilen in die sichtbare Liste übernommen wurden.</summary>
    public event Action? VisibleEntriesAppended;

    public DownloadItemViewModel? SelectedDownload
    {
        get => _selectedDownload;
        set
        {
            if (!SetField(ref _selectedDownload, value))
                return;
            RebuildFilter();
            OnPropertyChanged(nameof(VisibleEntries));
            OnPropertyChanged(nameof(FilterText));
            OnPropertyChanged(nameof(IsFiltered));
        }
    }

    public bool IsFiltered => _selectedDownload is not null;

    public string FilterText => _selectedDownload is { } d
        ? $"Gefiltert auf Download #{d.Id} {(string.IsNullOrEmpty(d.AppName) ? d.AppId : d.AppName)} – {FilteredEntries.Count:N0} Zeile(n)"
        : "Alle Zeilen";

    /// <summary>true: alle Zeilen zwischen Start- und Abschlusszeile zeigen, nicht nur die zugeordneten.</summary>
    public bool ShowContextRange
    {
        get => _showContextRange;
        set
        {
            if (SetField(ref _showContextRange, value))
            {
                RebuildFilter();
                OnPropertyChanged(nameof(FilterText));
            }
        }
    }

    public LogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
                OnPropertyChanged(nameof(SelectedEntryDetails));
        }
    }

    public string SelectedEntryDetails => _selectedEntry is not { } e
        ? ""
        : $"#{e.Sequence}  {e.Timestamp:dd.MM.yyyy HH:mm:ss.fff}  Komponente: {e.Component}  Typ: {e.Severity}  Thread: {e.Thread}"
          + (string.IsNullOrEmpty(e.File) ? "" : $"  Datei: {e.File}")
          + (e.DownloadId is { } id ? $"  → Download #{id}" : "")
          + Environment.NewLine + e.Message;

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetField(ref _autoScroll, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public int RolloverCount
    {
        get => _rolloverCount;
        private set => SetField(ref _rolloverCount, value);
    }

    public string CountersText =>
        $"{AllEntries.Count:N0} Zeilen angezeigt ({_totalEntriesRead:N0} gelesen) · {Downloads.Count} Downloads · " +
        $"{Downloads.Count(d => d.Status is DownloadStatus.Running or DownloadStatus.Started)} aktiv · " +
        $"{Downloads.Count(d => d.Status == DownloadStatus.Completed)} abgeschlossen · Rollover: {RolloverCount}";

    public void Start()
    {
        _uiTimer.Start();
        _monitor.Start();
        DeliveryOptimization.Start();
    }

    private void ApplyPendingUpdates()
    {
        if (_pending.IsEmpty)
            return;

        var newEntries = new List<LogEntry>();
        bool any = false;
        while (_pending.TryDequeue(out var update))
        {
            any = true;
            // Downloads zuerst, damit Filterbereiche (Endzeile) für die neuen Zeilen bereits stimmen.
            foreach (var snapshot in update.Downloads)
            {
                if (_downloadsById.TryGetValue(snapshot.Id, out var vm))
                {
                    vm.Update(snapshot);
                }
                else
                {
                    vm = new DownloadItemViewModel(snapshot);
                    _downloadsById[snapshot.Id] = vm;
                    Downloads.Insert(0, vm); // neueste oben
                }
            }
            newEntries.AddRange(update.Entries);
            if (update.StatusMessages.Count > 0)
                StatusText = update.StatusMessages[^1];
            RolloverCount = update.RolloverCount;
        }
        if (!any)
            return;

        _totalEntriesRead += newEntries.Count;
        AllEntries.AddRange(newEntries);
        TrimEntries();

        if (_selectedDownload is not null)
        {
            var matching = newEntries.Where(MatchesFilter).ToList();
            FilteredEntries.AddRange(matching);
            if (matching.Count > 0)
                OnPropertyChanged(nameof(FilterText));
        }

        OnPropertyChanged(nameof(CountersText));
        if (newEntries.Count > 0)
            VisibleEntriesAppended?.Invoke();
    }

    private void TrimEntries()
    {
        int excess = AllEntries.Count - _maxEntries;
        if (excess <= 0)
            return;
        // In Blöcken kürzen (10 %), damit nicht bei jedem Update ein Reset nötig ist.
        int remove = excess + _maxEntries / 10;
        AllEntries.RemoveFirst(remove);
        if (AllEntries.Count > 0 && FilteredEntries.Count > 0)
        {
            long firstSeq = AllEntries[0].Sequence;
            int filteredRemove = FilteredEntries.TakeWhile(e => e.Sequence < firstSeq).Count();
            FilteredEntries.RemoveFirst(filteredRemove);
        }
    }

    private bool MatchesFilter(LogEntry e)
    {
        var d = _selectedDownload;
        if (d is null)
            return true;
        if (_showContextRange)
            return e.Sequence >= d.StartSequence && e.Sequence <= d.EndSequenceOrOpen;
        return e.DownloadId == d.Id;
    }

    private void RebuildFilter()
    {
        if (_selectedDownload is null)
        {
            FilteredEntries.ReplaceAll([]);
            return;
        }
        FilteredEntries.ReplaceAll(AllEntries.Where(MatchesFilter));
    }

    /// <summary>Stoppt den UI-Timer; muss auf dem UI-Thread aufgerufen werden.</summary>
    public void StopUi() => _uiTimer.Stop();

    /// <summary>Beendet die Hintergrund-Monitore; darf auf einem beliebigen Thread laufen.</summary>
    public async ValueTask DisposeAsync()
    {
        await _monitor.DisposeAsync().ConfigureAwait(false);
        await DeliveryOptimization.DisposeAsync().ConfigureAwait(false);
    }
}
