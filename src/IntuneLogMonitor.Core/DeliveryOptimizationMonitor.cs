using System.Diagnostics;
using System.Text;

namespace IntuneLogMonitor.Core;

/// <summary>
/// Fragt periodisch Get-DeliveryOptimizationStatus und Get-DeliveryOptimizationPerfSnap ab
/// (über Windows PowerShell, da das Modul DeliveryOptimization dort enthalten ist) und misst die
/// Cache-Ordner. <see cref="Updated"/> wird auf einem Hintergrundthread ausgelöst.
/// </summary>
public sealed class DeliveryOptimizationMonitor : IAsyncDisposable
{
    /// <summary>
    /// Liefert genau ein JSON-Objekt { Jobs: [...], Perf: {...}, Errors: [...] }.
    /// Enums und TimeSpan werden in Text bzw. Sekunden umgewandelt, damit die JSON-Ausgabe stabil ist.
    /// </summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $result = [ordered]@{ Jobs = @(); Perf = $null; Errors = @() }
        try {
            $result.Jobs = @(Get-DeliveryOptimizationStatus | ForEach-Object {
                [ordered]@{
                    FileId = "$($_.FileId)"
                    FileSize = [long]$_.FileSize
                    FileSizeInCache = [long]$_.FileSizeInCache
                    TotalBytesDownloaded = [long]$_.TotalBytesDownloaded
                    PercentPeerCaching = [double]$_.PercentPeerCaching
                    BytesFromPeers = [long]$_.BytesFromPeers
                    BytesFromHttp = [long]$_.BytesFromHttp
                    BytesFromCacheServer = [long]$_.BytesFromCacheServer
                    BytesFromLanPeers = [long]$_.BytesFromLanPeers
                    BytesFromLinkLocalPeers = [long]$_.BytesFromLinkLocalPeers
                    BytesFromGroupPeers = [long]$_.BytesFromGroupPeers
                    BytesFromInternetPeers = [long]$_.BytesFromInternetPeers
                    BytesToLanPeers = [long]$_.BytesToLanPeers
                    BytesToLinkLocalPeers = [long]$_.BytesToLinkLocalPeers
                    BytesToGroupPeers = [long]$_.BytesToGroupPeers
                    BytesToInternetPeers = [long]$_.BytesToInternetPeers
                    Status = "$($_.Status)"
                    Priority = "$($_.Priority)"
                    DownloadMode = "$($_.DownloadMode)"
                    DownloadDurationSeconds = [double]$_.DownloadDuration.TotalSeconds
                    HttpConnectionCount = [int]$_.HttpConnectionCount
                    CacheServerConnectionCount = [int]$_.CacheServerConnectionCount
                    LanConnectionCount = [int]$_.LanConnectionCount
                    LinkLocalConnectionCount = [int]$_.LinkLocalConnectionCount
                    GroupConnectionCount = [int]$_.GroupConnectionCount
                    InternetConnectionCount = [int]$_.InternetConnectionCount
                    NumPeers = [int]$_.NumPeers
                    PredefinedCallerApplication = "$($_.PredefinedCallerApplication)"
                }
            })
        } catch { $result.Errors += "Get-DeliveryOptimizationStatus: $($_.Exception.Message)" }
        try {
            $p = Get-DeliveryOptimizationPerfSnap
            $result.Perf = [ordered]@{
                CacheSizeBytes = [long]$p.CacheSizeBytes
                Files = [int]$p.Files
                TotalBytesDownloaded = [long]$p.TotalBytesDownloaded
                TotalBytesUploaded = [long]$p.TotalBytesUploaded
                NumberOfPeers = [int]$p.NumberOfPeers
                CacheHostConnections = [int]$p.CacheHostConnections
                CdnConnections = [int]$p.CdnConnections
                LanConnections = [int]$p.LanConnections
                LinkLocalConnections = [int]$p.LinkLocalConnections
                GroupConnections = [int]$p.GroupConnections
                InternetConnections = [int]$p.InternetConnections
                DownlinkUsageBps = [long]$p.DownlinkUsageBps
                UplinkUsageBps = [long]$p.UplinkUsageBps
                DownloadMode = "$($p.DownloadMode)"
            }
        } catch { $result.Errors += "Get-DeliveryOptimizationPerfSnap: $($_.Exception.Message)" }
        ConvertTo-Json -InputObject $result -Depth 4 -Compress
        """;

    private readonly DeliveryOptimizationOptions _options;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _folderRefreshSignal = new(0, 1);
    private Task? _loop, _folderLoop;

    public DeliveryOptimizationMonitor(DeliveryOptimizationOptions options) => _options = options;

    /// <summary>Ergebnis der DO-Abfrage (PowerShell), Intervall <see cref="DeliveryOptimizationOptions.RefreshIntervalSeconds"/>.</summary>
    public event Action<DeliveryOptimizationReport>? Updated;

    /// <summary>Cache-Ordnergrößen, eigenes, kürzeres Intervall <see cref="DeliveryOptimizationOptions.CacheRefreshIntervalSeconds"/>.</summary>
    public event Action<IReadOnlyList<FolderSize>, DateTime>? FoldersUpdated;

    public void Start()
    {
        if (!_options.Enabled || _cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _folderLoop = Task.Run(() => FolderLoopAsync(_cts.Token));
    }

    /// <summary>Löst sofort eine neue DO-Abfrage und Ordnermessung aus.</summary>
    public void RefreshNow()
    {
        try { _refreshSignal.Release(); } catch (SemaphoreFullException) { }
        try { _folderRefreshSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.RefreshIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            var report = await CollectAsync(_options, ct, includeFolders: false).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                break;
            Updated?.Invoke(report);
            try { await _refreshSignal.WaitAsync(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FolderLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.CacheRefreshIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var folders = MeasureFolders(_options, ct);
                FoldersUpdated?.Invoke(folders, DateTime.Now);
            }
            catch (OperationCanceledException) { break; }
            try { await _folderRefreshSignal.WaitAsync(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public static List<FolderSize> MeasureFolders(DeliveryOptimizationOptions options, CancellationToken ct = default) =>
        options.EffectiveCacheFolders.Select(f => FolderSizeCalculator.Measure(f.Name, f.Path, ct)).ToList();

    public static async Task<DeliveryOptimizationReport> CollectAsync(DeliveryOptimizationOptions options, CancellationToken ct, bool includeFolders = true)
    {
        var errors = new List<string>();
        IReadOnlyList<DoJob> jobs = [];
        DoPerfSnap? perf = null;

        // Ordner parallel zur PowerShell-Abfrage messen
        var foldersTask = includeFolders
            ? Task.Run(() => MeasureFolders(options, ct), ct)
            : Task.FromResult(new List<FolderSize>());

        try
        {
            string json = await RunPowerShellAsync(options, ct).ConfigureAwait(false);
            var parsed = DeliveryOptimizationParser.Parse(json);
            jobs = parsed.Jobs;
            perf = parsed.Perf;
            errors.AddRange(parsed.Errors);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"DO query failed: {ex.Message}");
        }

        List<FolderSize> folders;
        try { folders = await foldersTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            folders = [];
            errors.Add($"Ordnermessung fehlgeschlagen: {ex.Message}");
        }

        return new DeliveryOptimizationReport
        {
            CollectedAt = DateTime.Now,
            Jobs = jobs,
            Summary = DoSummary.From(jobs),
            PerfSnap = perf,
            Folders = folders,
            Errors = errors,
        };
    }

    private static async Task<string> RunPowerShellAsync(DeliveryOptimizationOptions options, CancellationToken ct)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        var psi = new ProcessStartInfo(options.PowerShellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.CommandTimeoutSeconds)));

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"PowerShell did not respond within {options.CommandTimeoutSeconds} s.");
        }

        string output = (await stdout.ConfigureAwait(false)).Trim();
        string error = (await stderr.ConfigureAwait(false)).Trim();
        // Erste Zeile, die wie JSON aussieht (falls Profile/Warnungen etwas davor schreiben)
        int start = output.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException(string.IsNullOrEmpty(error) ? "No output from PowerShell." : error);
        return output[start..];
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            foreach (var loop in new[] { _loop, _folderLoop })
            {
                if (loop is null) continue;
                try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
        _refreshSignal.Dispose();
        _folderRefreshSignal.Dispose();
    }
}
