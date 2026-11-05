using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

/// <summary>
/// End-to-End über die Beispieldateien in samples/ (erzeugt mit tools/IntuneLogMonitor.LogSimulator):
/// alte Datei (vor Rollover) und aktive Datei werden nacheinander durch Parser und Tracker geschickt.
/// </summary>
public sealed class SampleLogTests
{
    private static readonly string SampleDir = Path.Combine(AppContext.BaseDirectory, "samples");

    private static (List<LogEntry> Entries, IReadOnlyList<DownloadSnapshot> Downloads) ProcessSamples()
    {
        var oldFile = Directory.GetFiles(SampleDir, "AppWorkload-*.log").Single();
        var newFile = Path.Combine(SampleDir, "AppWorkload.log");

        var parser = new CmTraceParser();
        var tracker = new DownloadTracker(new DownloadTrackingOptions());
        var entries = new List<LogEntry>();
        long seq = 0;

        foreach (var file in new[] { oldFile, newFile })
        {
            // Text in kleinen Stücken zuführen, wie beim Tail-Lesen
            string text = File.ReadAllText(file);
            var parsed = new List<LogEntry>();
            for (int i = 0; i < text.Length; i += 777)
                parsed.AddRange(parser.Feed(text.Substring(i, Math.Min(777, text.Length - i))));
            parsed.AddRange(parser.Flush()); // Dateiende = Rollover
            foreach (var e in parsed)
            {
                e.Sequence = ++seq;
                tracker.Process(e);
                entries.Add(e);
            }
        }
        return (entries, tracker.Downloads);
    }

    [Fact]
    public void AllSampleLinesAreValidCmTrace()
    {
        var (entries, _) = ProcessSamples();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(e.IsRaw, $"Rohzeile: {e.Message}"));
        Assert.Contains(entries, e => e.Message.Contains('\n'));        // mehrzeiliger Eintrag
        Assert.Contains(entries, e => e.Severity == LogSeverity.Warning);
        Assert.Contains(entries, e => e.Severity == LogSeverity.Error);
    }

    [Fact]
    public void SampleScenarioProducesExpectedDownloadStates()
    {
        var (_, downloads) = ProcessSamples();

        Assert.Collection(downloads,
            d => { Assert.Equal("Contoso Agent 4.2", d.AppName); Assert.Equal(DownloadStatus.Completed, d.Status); Assert.Equal(52_428_800, d.TotalBytes); },
            d => { Assert.Equal("Fabrikam Office Suite", d.AppName); Assert.Equal(DownloadStatus.Completed, d.Status); Assert.Equal(1_288_490_189, d.TotalBytes); },
            d => { Assert.Equal("Northwind Toolkit (x64)", d.AppName); Assert.Equal(DownloadStatus.Completed, d.Status); Assert.Equal(734_003_200, d.TotalBytes); },
            d => { Assert.Equal("Adatum VPN Client", d.AppName); Assert.Equal(DownloadStatus.Incomplete, d.Status); Assert.Null(d.TotalBytes); },
            d => { Assert.Equal("Contoso Script Package", d.AppName); Assert.Equal(DownloadStatus.Completed, d.Status); Assert.Equal(1_568, d.TotalBytes); },
            d => { Assert.Equal("Tailspin Reader", d.AppName); Assert.Equal(DownloadStatus.Restarted, d.Status); },
            d => { Assert.Equal("Tailspin Reader", d.AppName); Assert.Equal(DownloadStatus.Completed, d.Status); },
            d => { Assert.Equal("Litware Sync", d.AppName); Assert.Equal(DownloadStatus.NoActivity, d.Status); Assert.Equal(25.0, d.Percent!.Value, 1); });

        Assert.All(downloads, d => Assert.Equal(0, d.AmbiguousAssignments));
    }

    [Fact]
    public void DownloadSpanningRolloverHasLinesInBothFiles()
    {
        var (entries, downloads) = ProcessSamples();
        var d3 = downloads.Single(d => d.AppName == "Northwind Toolkit (x64)");
        int oldFileCount = CountEntries(Directory.GetFiles(SampleDir, "AppWorkload-*.log").Single());

        var lines = entries.Where(e => e.DownloadId == d3.Id).ToList();
        Assert.Contains(lines, e => e.Sequence <= oldFileCount);
        Assert.Contains(lines, e => e.Sequence > oldFileCount);
        Assert.Equal(22, lines.Count); // Start + Größe + 20 Fortschrittszeilen
        Assert.Equal(TimeSpan.FromSeconds(105), d3.Duration);
    }

    private static int CountEntries(string file)
    {
        var p = new CmTraceParser();
        var list = p.Feed(File.ReadAllText(file));
        list.AddRange(p.Flush());
        return list.Count;
    }
}
