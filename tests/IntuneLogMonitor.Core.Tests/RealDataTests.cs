using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

/// <summary>
/// Regressionstests mit echten IME-Logs aus Real-Data-as-Template (Testgerät, 22.–26.10.2026).
/// Die Dateien werden nicht kopiert, sondern beim Build verlinkt; fehlen sie, werden die Tests übersprungen.
/// </summary>
public sealed class RealDataTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "realdata");
    private static readonly string[] Files =
    [
        Path.Combine(Dir, "AppWorkload-20261026-122516.log"),
        Path.Combine(Dir, "AppWorkload.log"),
    ];

    private static bool Available => Files.All(File.Exists);

    [Fact]
    public void AllRealDownloadsAreCompletedWithNames()
    {
        if (!Available) return;

        var result = LogFileAnalyzer.Analyze(Files);

        Assert.All(result.Entries, e => Assert.False(e.IsRaw));
        Assert.Equal(9, result.Downloads.Count);
        Assert.All(result.Downloads, d =>
        {
            Assert.Equal(DownloadStatus.Completed, d.Status);
            Assert.False(string.IsNullOrEmpty(d.AppName), $"Download #{d.Id} ohne Namen");
            Assert.NotNull(d.AppId);
            Assert.Equal(d.TotalBytes, d.CurrentBytes);
        });
    }

    [Fact]
    public void SmallPackagesWithoutZeroOfTotalLineComplete()
    {
        if (!Available) return;

        var d = LogFileAnalyzer.Analyze(Files).Downloads[0];

        Assert.Equal("a3e98c96-3774-4649-9e5e-232b6d40bd51", d.AppId);
        Assert.Equal("Win32 DB04_Supersedence-BreakTest_Paket A", d.AppName);
        Assert.Equal(1568, d.TotalBytes);
        Assert.Equal(DownloadStatus.Completed, d.Status);
        Assert.Equal(new DateTime(2026, 10, 22, 10, 0, 17).AddTicks(8611749), d.StartTime);
    }

    [Fact]
    public void LargePackagesHaveCorrectSizeAndDuration()
    {
        if (!Available) return;

        var downloads = LogFileAnalyzer.Analyze(Files).Downloads;
        var fg = downloads.Single(d => d.AppName == "DB03_TestPaket-Delivery Optimization Foreground_0.1");
        var bg = downloads.Single(d => d.AppName == "DB03_TestPaket-Delivery Optimization Background_0.1");

        Assert.Equal(366_113_136, fg.TotalBytes);
        Assert.Equal(366_113_104, bg.TotalBytes);
        Assert.InRange(fg.Duration!.Value.TotalSeconds, 169, 171);   // 12:23:11 → 12:26:01
        Assert.InRange(bg.Duration!.Value.TotalSeconds, 725, 727);   // 14:49:59 → 15:02:05
        Assert.NotNull(fg.BytesPerSecond);
    }

    [Fact]
    public async Task RealLogReplayedLiveWithRolloverCompletesAllDownloads()
    {
        if (!Available) return;

        // Live-Pfad: Datei 1 schreiben, Monitor starten, dann Rollover und Datei 2 stückweise anhängen.
        string dir = Directory.CreateTempSubdirectory("ilm-real-").FullName;
        try
        {
            string path = Path.Combine(dir, "AppWorkload.log");
            File.Copy(Files[0], path);
            var downloads = new Dictionary<int, DownloadSnapshot>();
            await using (var monitor = new AppWorkloadMonitor(new AppWorkloadMonitorOptions
            {
                LogFilePath = path, PollIntervalMilliseconds = 50,
                Tracking = new DownloadTrackingOptions { CheckInactivityBySystemClock = false },
            }))
            {
                monitor.Updated += u => { lock (downloads) foreach (var d in u.Downloads) downloads[d.Id] = d; };
                monitor.Start();
                await Task.Delay(800);

                File.Move(path, Path.Combine(dir, "AppWorkload-20261026-122516.log"));
                byte[] second = await File.ReadAllBytesAsync(Files[1]);
                for (int i = 0; i < second.Length; i += 64 * 1024)
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(second, i, Math.Min(64 * 1024, second.Length - i));
                }

                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(100);
                    lock (downloads)
                        if (downloads.Count == 9 && downloads.Values.All(d => d.Status == DownloadStatus.Completed))
                            break;
                }
            }

            lock (downloads)
            {
                Assert.Equal(9, downloads.Count);
                Assert.All(downloads.Values, d => Assert.Equal(DownloadStatus.Completed, d.Status));
                Assert.All(downloads.Values, d => Assert.False(string.IsNullOrEmpty(d.AppName)));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
