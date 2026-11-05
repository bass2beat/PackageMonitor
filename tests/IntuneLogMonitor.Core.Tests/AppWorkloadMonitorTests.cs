using System.Text;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

public sealed class AppWorkloadMonitorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ilm-mon-").FullName;

    private static string Line(string msg, int sec) =>
        $"<![LOG[{msg}]LOG]!><time=\"09:00:{sec:00}.0000000\" date=\"11-5-2026\" component=\"AppWorkload\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n";

    private static void Append(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task DownloadStateSurvivesRolloverInLivePipeline()
    {
        string path = Path.Combine(_dir, "AppWorkload.log");
        const string app = "3f1c6b0e-8a5d-4c2e-9b71-2d4e6a8c0f11";
        Append(path, Line($"[Win32App DO] app {app} (name = Demo) via DO, bytes 0/0 for user S-1", 0)
                     + Line("[Win32App DO] DO Job set priority", 1)
                     + Line("[Win32App DO] bytes 0/1000 for user S-1", 2)
                     + Line("[Win32App DO] bytes 400/1000 for user S-1", 4));

        var options = new AppWorkloadMonitorOptions { LogFilePath = path, PollIntervalMilliseconds = 50 };
        var entries = new List<LogEntry>();
        var downloads = new Dictionary<int, DownloadSnapshot>();
        var completed = new TaskCompletionSource();

        await using var monitor = new AppWorkloadMonitor(options);
        monitor.Updated += u =>
        {
            lock (entries)
            {
                entries.AddRange(u.Entries);
                foreach (var d in u.Downloads) downloads[d.Id] = d;
                if (downloads.Values.Any(d => d.Status == DownloadStatus.Completed)) completed.TrySetResult();
            }
        };
        monitor.Start();

        await Task.Delay(400);
        Append(path, Line("[Win32App DO] bytes 700/1000 for user S-1", 6)); // Rest der alten Datei
        File.Move(path, Path.Combine(_dir, "AppWorkload-20261105-090007.log"));
        Append(path, Line("[Win32App DO] bytes 1000/1000 for user S-1", 8)); // neue Datei

        var done = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(completed.Task, done);

        lock (entries)
        {
            var d = Assert.Single(downloads.Values);
            Assert.Equal(DownloadStatus.Completed, d.Status);
            Assert.Equal(TimeSpan.FromSeconds(8), d.Duration);
            Assert.Contains(entries, e => e.IsSynthetic && e.Message.Contains("Rollover"));
            // Reihenfolge: 700 (alte Datei) vor Rollover-Markierung vor 1000 (neue Datei)
            int i700 = entries.FindIndex(e => e.Message.Contains("700/1000"));
            int iMarker = entries.FindIndex(e => e.IsSynthetic);
            int i1000 = entries.FindIndex(e => e.Message.Contains("1000/1000"));
            Assert.True(i700 < iMarker && iMarker < i1000);
            Assert.Equal(Enumerable.Range(1, entries.Count).Select(i => (long)i), entries.Select(e => e.Sequence));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
