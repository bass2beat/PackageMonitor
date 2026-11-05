using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

public class DeliveryOptimizationTests
{
    // Aufbau wie die Ausgabe des Abfrageskripts (Werte aus einem echten Windows-11-Client abgeleitet)
    private const string Json = """
        {"Jobs":[
          {"FileId":"96020d1b","FileSize":13887776,"FileSizeInCache":2353440,"TotalBytesDownloaded":2353440,"PercentPeerCaching":0,
           "BytesFromPeers":0,"BytesFromHttp":2353440,"BytesFromCacheServer":2353440,"BytesFromLanPeers":0,"BytesFromLinkLocalPeers":0,
           "BytesFromGroupPeers":0,"BytesFromInternetPeers":0,"BytesToLanPeers":0,"BytesToLinkLocalPeers":0,"BytesToGroupPeers":0,
           "BytesToInternetPeers":0,"Status":"Caching","Priority":"Background","DownloadMode":"Group","DownloadDurationSeconds":115.569,
           "HttpConnectionCount":5,"CacheServerConnectionCount":3,"LanConnectionCount":0,"LinkLocalConnectionCount":0,
           "GroupConnectionCount":0,"InternetConnectionCount":0,"NumPeers":0,"PredefinedCallerApplication":"WU Client Download"},
          {"FileId":"349513e4","FileSize":186839141,"FileSizeInCache":58912869,"TotalBytesDownloaded":100000000,"PercentPeerCaching":40,
           "BytesFromPeers":40000000,"BytesFromHttp":60000000,"BytesFromCacheServer":0,"BytesFromLanPeers":30000000,"BytesFromLinkLocalPeers":0,
           "BytesFromGroupPeers":10000000,"BytesFromInternetPeers":0,"BytesToLanPeers":5000000,"BytesToLinkLocalPeers":0,"BytesToGroupPeers":1000000,
           "BytesToInternetPeers":0,"Status":"Downloading","Priority":"Foreground","DownloadMode":"Group","DownloadDurationSeconds":101.9,
           "HttpConnectionCount":5,"CacheServerConnectionCount":0,"LanConnectionCount":2,"LinkLocalConnectionCount":0,
           "GroupConnectionCount":1,"InternetConnectionCount":0,"NumPeers":3,"PredefinedCallerApplication":"Intune"}
        ],
        "Perf":{"CacheSizeBytes":989773963,"Files":33,"TotalBytesDownloaded":989773963,"TotalBytesUploaded":0,"NumberOfPeers":96,
                "CacheHostConnections":29,"CdnConnections":71,"LanConnections":1,"LinkLocalConnections":0,"GroupConnections":1,
                "InternetConnections":0,"DownlinkUsageBps":0,"UplinkUsageBps":0,"DownloadMode":"Group"},
        "Errors":[]}
        """;

    [Fact]
    public void ParsesJobsPerfSnapAndErrors()
    {
        var (jobs, perf, errors) = DeliveryOptimizationParser.Parse(Json);

        Assert.Equal(2, jobs.Count);
        Assert.Equal("Caching", jobs[0].Status);
        Assert.Equal("WU Client Download", jobs[0].PredefinedCallerApplication);
        Assert.Equal(3, jobs[1].NumPeers);
        Assert.Equal(989_773_963, perf!.CacheSizeBytes);
        Assert.Equal(96, perf.NumberOfPeers);
        Assert.Empty(errors);
    }

    [Fact]
    public void SummaryAggregatesPeersAndBytes()
    {
        var (jobs, _, _) = DeliveryOptimizationParser.Parse(Json);
        var s = DoSummary.From(jobs);

        Assert.Equal(2, s.JobCount);
        Assert.Equal(1, s.ActiveJobCount);
        Assert.Equal(3, s.PeersConnected);
        Assert.Equal(3, s.PeersConnectedActiveJobs);
        Assert.Equal(2, s.LanConnections);
        Assert.Equal(1, s.GroupConnections);
        Assert.Equal(3, s.CacheServerConnections);
        Assert.Equal(10, s.HttpConnections);
        Assert.Equal(102_353_440, s.TotalBytesDownloaded);
        Assert.Equal(40_000_000, s.BytesFromPeers);
        Assert.Equal(30_000_000, s.BytesFromLanPeers);
        Assert.Equal(62_353_440, s.BytesFromHttp);
        Assert.Equal(2_353_440, s.BytesFromCacheServer);
        Assert.Equal(6_000_000, s.BytesToPeers);
        Assert.Equal(40_000_000 * 100.0 / 102_353_440, s.PeerSharePercent!.Value, 6);
    }

    [Fact]
    public void SingleJobSerializedAsObjectAndErrorsAreHandled()
    {
        // PowerShell 5.1 kann ein einzelnes Element als Objekt statt Array ausgeben.
        const string json = """{"Jobs":{"FileId":"a","FileSize":10,"TotalBytesDownloaded":5,"NumPeers":2,"Status":"Downloading"},"Perf":null,"Errors":"Get-DeliveryOptimizationPerfSnap: Zugriff verweigert"}""";
        var (jobs, perf, errors) = DeliveryOptimizationParser.Parse(json);

        Assert.Equal(2, Assert.Single(jobs).NumPeers);
        Assert.Null(perf);
        Assert.Contains("Zugriff verweigert", Assert.Single(errors));
    }

    [Fact]
    public void EmptyJobListGivesZeroSummary()
    {
        var (jobs, _, _) = DeliveryOptimizationParser.Parse("""{"Jobs":[],"Perf":null,"Errors":[]}""");
        var s = DoSummary.From(jobs);
        Assert.Equal(0, s.PeersConnected);
        Assert.Null(s.PeerSharePercent);
    }

    [Fact]
    public void FolderSizeSumsRecursivelyIncludingHiddenFiles()
    {
        string dir = Directory.CreateTempSubdirectory("ilm-size-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
            File.WriteAllBytes(Path.Combine(dir, "sub", "deep", "b.bin"), new byte[2500]);
            string hidden = Path.Combine(dir, "sub", "h.bin");
            File.WriteAllBytes(hidden, new byte[500]);
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var r = FolderSizeCalculator.Measure("Test", dir);

            Assert.True(r.Exists);
            Assert.False(r.AccessDenied);
            Assert.Equal(4000, r.Bytes);
            Assert.Equal(3, r.Files);
            Assert.Contains("Dateien", r.DisplayText);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingFolderIsReportedNotThrown()
    {
        var r = FolderSizeCalculator.Measure("X", @"%TEMP%\gibt-es-nicht-" + Guid.NewGuid());
        Assert.False(r.Exists);
        Assert.Equal("Ordner nicht vorhanden", r.DisplayText);
        Assert.DoesNotContain("%TEMP%", r.Path); // Umgebungsvariable aufgelöst
    }

    [Fact]
    public void AccessDeniedFolderShowsAdminHint()
    {
        var r = FolderSize.Denied("IMECache", @"C:\Windows\IMECache");
        Assert.Equal("Zugriff verweigert – als Administrator starten", r.DisplayText);
    }

    /// <summary>
    /// Wie Delivery Optimization: Datei als Sparse in Endgröße anlegen und stückweise füllen.
    /// Die Dateilänge bleibt konstant – gemessen werden muss die belegte Größe.
    /// </summary>
    [Fact]
    public void SparseFileGrowingDuringDownloadIsMeasuredByAllocatedSize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string dir = Directory.CreateTempSubdirectory("ilm-sparse-").FullName;
        try
        {
            string path = Path.Combine(dir, "content.bin");
            const long finalSize = 64L * 1024 * 1024;
            var chunk = new byte[4 * 1024 * 1024];
            Array.Fill(chunk, (byte)7);

            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.True(SparseFile.MarkSparse(fs), "FSCTL_SET_SPARSE fehlgeschlagen");
            fs.SetLength(finalSize);
            fs.Flush(true);

            var before = FolderSizeCalculator.Measure("DO", dir);
            Assert.Equal(finalSize, before.LogicalBytes);
            Assert.Equal(1, before.SparseFiles);
            Assert.True(before.Bytes < 1024 * 1024, $"vorher belegt: {before.Bytes}");

            for (int i = 0; i < 4; i++) fs.Write(chunk); // 16 MiB "heruntergeladen"
            fs.Flush(true);

            var during = FolderSizeCalculator.Measure("DO", dir);
            Assert.Equal(finalSize, during.LogicalBytes);                       // Länge unverändert
            Assert.InRange(during.Bytes, 16L * 1024 * 1024, 17L * 1024 * 1024); // belegte Größe gewachsen
            Assert.Contains("reserviert", during.DisplayText);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FolderMonitorRaisesFolderUpdatesIndependently()
    {
        string dir = Directory.CreateTempSubdirectory("ilm-fl-").FullName;
        try
        {
            var opts = new DeliveryOptimizationOptions
            {
                PowerShellPath = "does-not-exist.exe", // DO-Abfrage schlägt fehl – Ordner müssen trotzdem kommen
                CacheRefreshIntervalSeconds = 1,
                CacheFolders = [new CacheFolderOptions { Name = "T", Path = dir }],
            };
            int updates = 0;
            var monitor = new DeliveryOptimizationMonitor(opts);
            monitor.FoldersUpdated += (_, _) => Interlocked.Increment(ref updates);
            monitor.Start();
            File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[10]);
            await Task.Delay(2500);
            await monitor.DisposeAsync();
            Assert.True(updates >= 2, $"nur {updates} Ordner-Updates");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DefaultCacheFoldersUsedWhenNoneConfigured()
    {
        var o = new DeliveryOptimizationOptions();
        Assert.Equal(4, o.EffectiveCacheFolders.Count);
        Assert.Contains(o.EffectiveCacheFolders, f => f.Path.EndsWith(@"\IMECache"));
    }

    /// <summary>Echter Aufruf der DO-Cmdlets auf diesem Rechner (nur Windows).</summary>
    [Fact]
    public async Task CollectAsync_AgainstRealDeliveryOptimizationCmdlets()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var report = await DeliveryOptimizationMonitor.CollectAsync(new DeliveryOptimizationOptions(), CancellationToken.None);

        Assert.True(report.Errors.Count == 0, string.Join(" | ", report.Errors));
        Assert.NotNull(report.PerfSnap);
        Assert.Equal(report.Jobs.Count, report.Summary.JobCount);
        Assert.Equal(4, report.Folders.Count);
    }
}
