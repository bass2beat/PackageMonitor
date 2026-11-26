using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntuneLogMonitor.Core;

/// <summary>Ein Job aus Get-DeliveryOptimizationStatus.</summary>
public sealed record DoJob
{
    public string FileId { get; init; } = "";
    public long FileSize { get; init; }
    public long FileSizeInCache { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public double PercentPeerCaching { get; init; }
    public long BytesFromPeers { get; init; }
    public long BytesFromHttp { get; init; }
    public long BytesFromCacheServer { get; init; }
    public long BytesFromLanPeers { get; init; }
    public long BytesFromLinkLocalPeers { get; init; }
    public long BytesFromGroupPeers { get; init; }
    public long BytesFromInternetPeers { get; init; }
    public long BytesToLanPeers { get; init; }
    public long BytesToLinkLocalPeers { get; init; }
    public long BytesToGroupPeers { get; init; }
    public long BytesToInternetPeers { get; init; }
    public string Status { get; init; } = "";
    public string Priority { get; init; } = "";
    public string DownloadMode { get; init; } = "";
    public double DownloadDurationSeconds { get; init; }
    public int HttpConnectionCount { get; init; }
    public int CacheServerConnectionCount { get; init; }
    public int LanConnectionCount { get; init; }
    public int LinkLocalConnectionCount { get; init; }
    public int GroupConnectionCount { get; init; }
    public int InternetConnectionCount { get; init; }
    public int NumPeers { get; init; }
    public string PredefinedCallerApplication { get; init; } = "";

    public long BytesToPeers => BytesToLanPeers + BytesToLinkLocalPeers + BytesToGroupPeers + BytesToInternetPeers;
    public double? Percent => FileSize > 0 ? Math.Min(100, TotalBytesDownloaded * 100.0 / FileSize) : null;
    public double? PeerSharePercent => TotalBytesDownloaded > 0 ? BytesFromPeers * 100.0 / TotalBytesDownloaded : null;
}

/// <summary>Gesamtwerte aus Get-DeliveryOptimizationPerfSnap (Sicht des DO-Dienstes).</summary>
public sealed record DoPerfSnap
{
    public long CacheSizeBytes { get; init; }
    public int Files { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public long TotalBytesUploaded { get; init; }
    public int NumberOfPeers { get; init; }
    public int CacheHostConnections { get; init; }
    public int CdnConnections { get; init; }
    public int LanConnections { get; init; }
    public int LinkLocalConnections { get; init; }
    public int GroupConnections { get; init; }
    public int InternetConnections { get; init; }
    public long DownlinkUsageBps { get; init; }
    public long UplinkUsageBps { get; init; }
    public string DownloadMode { get; init; } = "";
}

/// <summary>Zusammenfassung über alle Jobs aus Get-DeliveryOptimizationStatus.</summary>
public sealed record DoSummary
{
    public int JobCount { get; init; }
    public int ActiveJobCount { get; init; }
    /// <summary>Summe NumPeers über alle Jobs = aktuell verbundene Peers.</summary>
    public int PeersConnected { get; init; }
    /// <summary>Summe NumPeers über aktiv ladende Jobs.</summary>
    public int PeersConnectedActiveJobs { get; init; }
    public int LanConnections { get; init; }
    public int LinkLocalConnections { get; init; }
    public int GroupConnections { get; init; }
    public int InternetConnections { get; init; }
    public int CacheServerConnections { get; init; }
    public int HttpConnections { get; init; }
    public long TotalBytesDownloaded { get; init; }
    public long BytesFromPeers { get; init; }
    public long BytesFromLanPeers { get; init; }
    public long BytesFromLinkLocalPeers { get; init; }
    public long BytesFromGroupPeers { get; init; }
    public long BytesFromInternetPeers { get; init; }
    /// <summary>HTTP/CDN-Bytes (enthält die Bytes vom Connected-Cache-Server).</summary>
    public long BytesFromHttp { get; init; }
    public long BytesFromCacheServer { get; init; }
    public long BytesToPeers { get; init; }
    public long FileSizeInCache { get; init; }

    public double? PeerSharePercent => TotalBytesDownloaded > 0 ? BytesFromPeers * 100.0 / TotalBytesDownloaded : null;

    /// <summary>Status-Werte, die einen laufenden Download bedeuten.</summary>
    public static bool IsActiveStatus(string status) =>
        status.Equals("Downloading", StringComparison.OrdinalIgnoreCase)
        || status.Equals("VerifyingFile", StringComparison.OrdinalIgnoreCase);

    public static DoSummary From(IReadOnlyList<DoJob> jobs) => new()
    {
        JobCount = jobs.Count,
        ActiveJobCount = jobs.Count(j => IsActiveStatus(j.Status)),
        PeersConnected = jobs.Sum(j => j.NumPeers),
        PeersConnectedActiveJobs = jobs.Where(j => IsActiveStatus(j.Status)).Sum(j => j.NumPeers),
        LanConnections = jobs.Sum(j => j.LanConnectionCount),
        LinkLocalConnections = jobs.Sum(j => j.LinkLocalConnectionCount),
        GroupConnections = jobs.Sum(j => j.GroupConnectionCount),
        InternetConnections = jobs.Sum(j => j.InternetConnectionCount),
        CacheServerConnections = jobs.Sum(j => j.CacheServerConnectionCount),
        HttpConnections = jobs.Sum(j => j.HttpConnectionCount),
        TotalBytesDownloaded = jobs.Sum(j => j.TotalBytesDownloaded),
        BytesFromPeers = jobs.Sum(j => j.BytesFromPeers),
        BytesFromLanPeers = jobs.Sum(j => j.BytesFromLanPeers),
        BytesFromLinkLocalPeers = jobs.Sum(j => j.BytesFromLinkLocalPeers),
        BytesFromGroupPeers = jobs.Sum(j => j.BytesFromGroupPeers),
        BytesFromInternetPeers = jobs.Sum(j => j.BytesFromInternetPeers),
        BytesFromHttp = jobs.Sum(j => j.BytesFromHttp),
        BytesFromCacheServer = jobs.Sum(j => j.BytesFromCacheServer),
        BytesToPeers = jobs.Sum(j => j.BytesToPeers),
        FileSizeInCache = jobs.Sum(j => j.FileSizeInCache),
    };
}

/// <summary>Größe eines Cache-Ordners.</summary>
/// <param name="Bytes">
/// Tatsächlich belegte Bytes. Bei Sparse-/komprimierten Dateien die belegten Bytes (GetCompressedFileSize):
/// DO legt Dateien sparse mit der Endgröße an und füllt sie stückweise – die Dateilänge ändert sich während
/// des Downloads nicht, die belegten Bytes schon.
/// </param>
/// <param name="LogicalBytes">Summe der Dateilängen (bei Sparse-Dateien die reservierte Endgröße).</param>
public sealed record FolderSize(
    string Name, string Path, bool Exists, long Bytes, long LogicalBytes, int Files, int SparseFiles,
    bool AccessDenied, int InaccessibleSubfolders, string? Error)
{
    public static FolderSize Denied(string name, string path) => new(name, path, true, 0, 0, 0, 0, true, 0, null);

    public string DisplayText =>
        AccessDenied ? "Access denied – run as administrator"
        : Error is not null ? $"Error: {Error}"
        : !Exists ? "Folder does not exist"
        : $"{Formatting.FormatBytes(Bytes)} ({Files:N0} files)"
          + (LogicalBytes > Bytes ? $" · reserved {Formatting.FormatBytes(LogicalBytes)}" : "")
          + (InaccessibleSubfolders > 0 ? $" – {InaccessibleSubfolders} subfolder(s) not readable, value incomplete" : "");
}

/// <summary>Ergebnis einer Abfrage.</summary>
public sealed record DeliveryOptimizationReport
{
    public DateTime CollectedAt { get; init; }
    public IReadOnlyList<DoJob> Jobs { get; init; } = [];
    public DoSummary Summary { get; init; } = new();
    public DoPerfSnap? PerfSnap { get; init; }
    public IReadOnlyList<FolderSize> Folders { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Parst die JSON-Ausgabe des Abfrageskripts.</summary>
public static class DeliveryOptimizationParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static (IReadOnlyList<DoJob> Jobs, DoPerfSnap? Perf, IReadOnlyList<string> Errors) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var jobs = new List<DoJob>();
        if (root.TryGetProperty("Jobs", out var jobsEl))
        {
            // PowerShell 5.1 serialisiert ein einzelnes Element u. U. als Objekt statt Array.
            if (jobsEl.ValueKind == JsonValueKind.Array)
                jobs.AddRange(jobsEl.EnumerateArray().Select(e => e.Deserialize<DoJob>(JsonOptions)!));
            else if (jobsEl.ValueKind == JsonValueKind.Object)
                jobs.Add(jobsEl.Deserialize<DoJob>(JsonOptions)!);
        }

        DoPerfSnap? perf = null;
        if (root.TryGetProperty("Perf", out var perfEl) && perfEl.ValueKind == JsonValueKind.Object)
            perf = perfEl.Deserialize<DoPerfSnap>(JsonOptions);

        var errors = new List<string>();
        if (root.TryGetProperty("Errors", out var errEl))
        {
            if (errEl.ValueKind == JsonValueKind.Array)
                errors.AddRange(errEl.EnumerateArray().Select(e => e.ToString()));
            else if (errEl.ValueKind == JsonValueKind.String)
                errors.Add(errEl.GetString()!);
        }

        return (jobs, perf, errors);
    }
}
