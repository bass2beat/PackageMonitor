using System.Threading.Channels;

namespace IntuneLogMonitor.Core;

/// <summary>Ein Bündel neuer Daten für die Oberfläche.</summary>
public sealed record MonitorUpdate(
    IReadOnlyList<LogEntry> Entries,
    IReadOnlyList<DownloadSnapshot> Downloads,
    IReadOnlyList<string> StatusMessages,
    int RolloverCount);

/// <summary>
/// Verbindet Tail-Reader, CMTrace-Parser und Download-Zustandsmaschine. Alle Verarbeitungsschritte
/// laufen nacheinander auf einem einzigen Hintergrund-Task (über einen Channel), damit Parser und
/// Tracker ohne Sperren auskommen und die Reihenfolge über Rollover hinweg erhalten bleibt.
/// <see cref="Updated"/> wird auf diesem Hintergrund-Task ausgelöst.
/// </summary>
public sealed class AppWorkloadMonitor : IAsyncDisposable
{
    private const int MaxEntriesPerUpdate = 20_000;

    private abstract record Message;
    private sealed record TextMessage(string Text) : Message;
    private sealed record SwitchMessage(FileSwitchInfo Info) : Message;
    private sealed record StatusMessage(string Text) : Message;
    private sealed record TickMessage : Message;

    private readonly AppWorkloadMonitorOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly Channel<Message> _channel = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CmTraceParser _parser = new();
    private readonly DownloadTracker _tracker;
    private readonly LogFileTailer _tailer;

    private CancellationTokenSource? _cts;
    private Task? _tailTask, _consumeTask, _tickTask;
    private long _sequence;
    private int _rollovers;

    public AppWorkloadMonitor(AppWorkloadMonitorOptions options, Func<DateTime>? clock = null)
    {
        _options = options;
        _clock = clock ?? (() => DateTime.Now);
        _tracker = new DownloadTracker(options.Tracking);
        _tailer = new LogFileTailer(options.LogFilePath,
            TimeSpan.FromMilliseconds(Math.Max(50, options.PollIntervalMilliseconds)),
            options.ReadExistingContent);
        _tailer.TextRead += t => _channel.Writer.TryWrite(new TextMessage(t));
        _tailer.FileSwitched += i => _channel.Writer.TryWrite(new SwitchMessage(i));
        _tailer.StatusChanged += s => _channel.Writer.TryWrite(new StatusMessage(s));
    }

    public string FilePath => _tailer.FilePath;

    public event Action<MonitorUpdate>? Updated;

    public void Start()
    {
        if (_cts is not null)
            throw new InvalidOperationException("Monitor is already running.");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _consumeTask = Task.Run(() => ConsumeAsync(ct), ct);
        _tailTask = Task.Run(() => _tailer.RunAsync(ct), ct);
        _tickTask = Task.Run(() => TickAsync(ct), ct);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.InactivityCheckIntervalSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                _channel.Writer.TryWrite(new TickMessage());
        }
        catch (OperationCanceledException) { }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var entries = new List<LogEntry>();
                var statuses = new List<string>();

                while (entries.Count < MaxEntriesPerUpdate && reader.TryRead(out var message))
                {
                    try
                    {
                        Handle(message, entries, statuses);
                    }
                    catch (Exception ex)
                    {
                        statuses.Add($"Verarbeitungsfehler: {ex.Message}");
                    }
                }

                var downloads = _tracker.TakeChanges();
                if (entries.Count > 0 || downloads.Count > 0 || statuses.Count > 0)
                    Updated?.Invoke(new MonitorUpdate(entries, downloads, statuses, _rollovers));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Handle(Message message, List<LogEntry> entries, List<string> statuses)
    {
        switch (message)
        {
            case TextMessage t:
                foreach (var e in _parser.Feed(t.Text))
                    Add(e, entries);
                break;

            case SwitchMessage s:
                // Unvollständigen Rest der alten Datei abschließen, dann Markierung einfügen.
                foreach (var e in _parser.Flush())
                    Add(e, entries);
                if (!s.Info.WasTruncation)
                    _rollovers++;
                Add(new LogEntry
                {
                    Timestamp = _clock(),
                    Component = "LogMonitor",
                    IsSynthetic = true,
                    Severity = LogSeverity.Warning,
                    Message = s.Info.WasTruncation
                        ? "──── File was truncated – reading from position 0 ────"
                        : $"──── Rollover: old file read to the end ({Formatting.FormatBytes(s.Info.OldFileBytesRead)})"
                          + (s.Info.RolledOverTo is null ? "" : $", old file: {Path.GetFileName(s.Info.RolledOverTo)}")
                          + " – continuing with the new file from position 0; running downloads are still tracked ────",
                }, entries);
                break;

            case StatusMessage st:
                statuses.Add(st.Text);
                break;

            case TickMessage:
                if (_options.Tracking.CheckInactivityBySystemClock)
                    _tracker.CheckInactivity(_clock());
                break;
        }
    }

    private void Add(LogEntry entry, List<LogEntry> entries)
    {
        entry.Sequence = ++_sequence;
        _tracker.Process(entry);
        entries.Add(entry);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;
        _cts.Cancel();
        foreach (var task in new[] { _tailTask, _consumeTask, _tickTask })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _tailer.Dispose();
    }
}
