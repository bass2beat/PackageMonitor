using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

/// <summary>Schaltbare Regeln der Zustandsmaschine (appsettings.json → Tracking).</summary>
public class TrackerSwitchTests
{
    private const string App = "3f1c6b0e-8a5d-4c2e-9b71-2d4e6a8c0f11";
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0);

    private sealed class Harness(DownloadTrackingOptions options)
    {
        public readonly DownloadTracker Tracker = new(options);
        private long _seq;
        public void Feed(double sec, string msg) =>
            Tracker.Process(new LogEntry { Sequence = ++_seq, Timestamp = T0.AddSeconds(sec), Message = msg });
        public DownloadSnapshot Single => Tracker.Downloads.Single();
    }

    private static string Start() => $"app {App} via DO, bytes 0/0 for user S-1";
    private static string Bytes(long c, long t) => $"state: Transferring, bytes {c}/{t} for user S-1";

    [Fact]
    public void Strict_IgnoresNonZeroCurrentForTotal()
    {
        var h = new Harness(new DownloadTrackingOptions());
        h.Feed(0, Start());
        h.Feed(1, Bytes(100, 1000));
        Assert.Equal(DownloadStatus.Started, h.Single.Status);
    }

    [Fact]
    public void Relaxed_AcceptsNonZeroCurrentAndAppliesProgress()
    {
        var h = new Harness(new DownloadTrackingOptions { StrictTotalSizeDetection = false });
        h.Feed(0, Start());
        h.Feed(1, Bytes(100, 1000));
        h.Feed(3, Bytes(500, 1000));

        var d = h.Single;
        Assert.Equal(DownloadStatus.Running, d.Status);
        Assert.Equal(1000, d.TotalBytes);
        Assert.Equal(500, d.CurrentBytes);
        Assert.Equal(200, d.BytesPerSecond!.Value, 3); // (500 − 100) / 2 s, nicht ab 0
    }

    [Fact]
    public void Relaxed_SizeLineCanCompleteImmediately()
    {
        var h = new Harness(new DownloadTrackingOptions { StrictTotalSizeDetection = false });
        h.Feed(0, Start());
        h.Feed(1, Bytes(1000, 1000));
        Assert.Equal(DownloadStatus.Completed, h.Single.Status);
    }

    [Fact]
    public void Relaxed_ProgressOfRunningDownloadIsNotStolenByPendingOne()
    {
        var h = new Harness(new DownloadTrackingOptions { StrictTotalSizeDetection = false });
        h.Feed(0, "x via DO, bytes 0/0 for user S-1");
        h.Feed(1, Bytes(0, 1000));
        h.Feed(2, "y via DO, bytes 0/0 for user S-1");   // zweiter, wartender Download
        h.Feed(3, Bytes(300, 1000));                      // gehört dem laufenden

        var downloads = h.Tracker.Downloads;
        Assert.Equal(300, downloads[0].CurrentBytes);
        Assert.Equal(DownloadStatus.Started, downloads[1].Status);
    }

    [Fact]
    public void LogTimeCheckCanBeDisabled()
    {
        var h = new Harness(new DownloadTrackingOptions { CheckInactivityByLogTime = false });
        h.Feed(0, Start());
        h.Feed(1, Bytes(0, 1000));
        h.Feed(3600, "viel später");
        Assert.Equal(DownloadStatus.Running, h.Single.Status);

        h.Tracker.CheckInactivity(T0.AddHours(2)); // Systemuhr-Prüfung wirkt weiterhin
        Assert.Equal(DownloadStatus.NoActivity, h.Single.Status);
    }

    [Fact]
    public async Task SystemClockCheckCanBeDisabled()
    {
        string dir = Directory.CreateTempSubdirectory("ilm-clock-").FullName;
        try
        {
            string path = Path.Combine(dir, "AppWorkload.log");
            await File.WriteAllTextAsync(path,
                "<![LOG[app via DO, bytes 0/0 for user S-1]LOG]!><time=\"09:00:00.0\" date=\"11-5-2026\" component=\"A\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n" +
                "<![LOG[bytes 0/1000 for user S-1]LOG]!><time=\"09:00:01.0\" date=\"11-5-2026\" component=\"A\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n");

            DownloadStatus? Run(bool clockCheck)
            {
                var opts = new AppWorkloadMonitorOptions
                {
                    LogFilePath = path, PollIntervalMilliseconds = 50, InactivityCheckIntervalSeconds = 1,
                    Tracking = new DownloadTrackingOptions { CheckInactivityBySystemClock = clockCheck },
                };
                DownloadStatus? last = null;
                var monitor = new AppWorkloadMonitor(opts, clock: () => T0.AddHours(1));
                monitor.Updated += u => { foreach (var d in u.Downloads) last = d.Status; };
                monitor.Start();
                Thread.Sleep(1800);
                monitor.DisposeAsync().AsTask().Wait();
                return last;
            }

            Assert.Equal(DownloadStatus.NoActivity, Run(clockCheck: true));
            Assert.Equal(DownloadStatus.Running, Run(clockCheck: false));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void NoResume_NoActivityStaysFinal()
    {
        var h = new Harness(new DownloadTrackingOptions { ResumeAfterNoActivity = false });
        h.Feed(0, Start());
        h.Feed(1, Bytes(0, 1000));
        h.Tracker.CheckInactivity(T0.AddMinutes(30));
        h.Feed(1900, Bytes(1000, 1000));
        Assert.Equal(DownloadStatus.NoActivity, h.Single.Status);
        Assert.Equal(0, h.Single.CurrentBytes);
    }

    [Fact]
    public void StalledStart_RemainsStartedWhenRuleDisabled()
    {
        var h = new Harness(new DownloadTrackingOptions { MarkStalledStartsIncomplete = false });
        h.Feed(0, Start());
        h.Tracker.CheckInactivity(T0.AddMinutes(30));
        Assert.Equal(DownloadStatus.Started, h.Single.Status);
    }

    [Fact]
    public void StalledStart_IncompleteByDefault()
    {
        var h = new Harness(new DownloadTrackingOptions());
        h.Feed(0, Start());
        h.Tracker.CheckInactivity(T0.AddMinutes(30));
        Assert.Equal(DownloadStatus.Incomplete, h.Single.Status);
    }
}
