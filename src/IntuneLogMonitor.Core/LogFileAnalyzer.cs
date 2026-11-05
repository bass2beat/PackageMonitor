namespace IntuneLogMonitor.Core;

/// <summary>
/// Wertet vorhandene Logdateien offline aus – älteste Datei zuerst, wie nach Rollovern – mit
/// demselben Parser und derselben Zustandsmaschine wie die Live-Überwachung.
/// </summary>
public static class LogFileAnalyzer
{
    public sealed record Result(IReadOnlyList<LogEntry> Entries, IReadOnlyList<DownloadSnapshot> Downloads, IReadOnlyList<(string File, int Entries)> Files);

    public static Result Analyze(IEnumerable<string> files, DownloadTrackingOptions? options = null)
    {
        var parser = new CmTraceParser();
        var tracker = new DownloadTracker(options ?? new DownloadTrackingOptions());
        var all = new List<LogEntry>();
        var perFile = new List<(string, int)>();
        long seq = 0;

        foreach (var file in files)
        {
            string text;
            using (var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                text = reader.ReadToEnd();

            var entries = parser.Feed(text);
            entries.AddRange(parser.Flush()); // Dateiende = Rollover
            foreach (var e in entries)
            {
                e.Sequence = ++seq;
                tracker.Process(e);
            }
            all.AddRange(entries);
            perFile.Add((file, entries.Count));
        }
        return new Result(all, tracker.Downloads, perFile);
    }
}
