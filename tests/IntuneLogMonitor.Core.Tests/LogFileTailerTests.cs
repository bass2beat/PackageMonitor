using System.Text;
using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

/// <summary>Integrationstests gegen das echte Dateisystem (temporäres Verzeichnis).</summary>
public sealed class LogFileTailerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ilm-tail-").FullName;
    private readonly string _path;
    private readonly StringBuilder _text = new();
    private readonly List<FileSwitchInfo> _switches = [];

    public LogFileTailerTests() => _path = Path.Combine(_dir, "AppWorkload.log");

    private LogFileTailer CreateTailer(bool readExisting = true)
    {
        var t = new LogFileTailer(_path, TimeSpan.FromMilliseconds(50), readExisting);
        t.TextRead += s => _text.Append(s);
        t.FileSwitched += i => { _switches.Add(i); _text.Append("|SWITCH|"); };
        return t;
    }

    /// <summary>Schreibt wie die IME: öffnen, anhängen, schließen.</summary>
    private void Append(string text, string? path = null)
    {
        using var fs = new FileStream(path ?? _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void ReadsExistingAndAppendedContentOnlyOnce()
    {
        Append("eins\n");
        using var t = CreateTailer();
        t.Poll();
        Append("zwei\n");
        t.Poll();
        t.Poll();

        Assert.Equal("eins\nzwei\n", _text.ToString());
    }

    [Fact]
    public void DoesNotLockTheFile_WriterAndRenameStillWork()
    {
        Append("a\n");
        using var t = CreateTailer();
        t.Poll();
        Assert.True(t.IsOpen);

        // Schreiben mit FileShare.Read (strenger Schreiber) ist trotz offenem Lese-Handle möglich.
        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete))
            fs.Write("b\n"u8);

        // Umbenennen der Datei ist möglich, obwohl unser Handle offen ist.
        File.Move(_path, Path.Combine(_dir, "AppWorkload-rolled.log"));
    }

    [Fact]
    public void StartsAtEndWhenExistingContentShouldBeSkipped()
    {
        Append("alt\n");
        using var t = CreateTailer(readExisting: false);
        t.Poll();
        Append("neu\n");
        t.Poll();
        Assert.Equal("neu\n", _text.ToString());
    }

    [Fact]
    public void WaitsForFileThatDoesNotExistYet()
    {
        using var t = CreateTailer();
        t.Poll();
        Assert.False(t.IsOpen);
        Append("da\n");
        t.Poll();
        Assert.Equal("da\n", _text.ToString());
    }

    [Fact]
    public void Rollover_ReadsRestOfOldFileThenNewFileFromStart()
    {
        Append("alt-1\n");
        using var t = CreateTailer();
        t.Poll();

        // Nach dem letzten Lesen noch in die alte Datei geschrieben ...
        Append("alt-2\n");
        // ... dann Rollover: Umbenennen und Neuanlage unter dem ursprünglichen Namen.
        string rolled = Path.Combine(_dir, "AppWorkload-20261105-090221.log");
        File.Move(_path, rolled);
        Append("neu-1\n");

        t.Poll();
        Append("neu-2\n");
        t.Poll();

        Assert.Equal("alt-1\nalt-2\n|SWITCH|neu-1\nneu-2\n", _text.ToString());
        var sw = Assert.Single(_switches);
        Assert.False(sw.WasTruncation);
        Assert.Equal(12, sw.OldFileBytesRead); // Länge von "alt-1\nalt-2\n"
    }

    [Fact]
    public void Rollover_GapBetweenRenameAndCreateIsHandled()
    {
        Append("alt\n");
        using var t = CreateTailer();
        t.Poll();
        File.Move(_path, Path.Combine(_dir, "AppWorkload-1.log"));

        t.Poll(); // keine Datei unter dem Pfad – kein Wechsel, kein Fehler
        Assert.Empty(_switches);

        Append("neu\n");
        t.Poll();
        Assert.Equal("alt\n|SWITCH|neu\n", _text.ToString());
    }

    [Fact]
    public void Rollover_DetectedEvenIfNewFileIsLargerThanOldPosition()
    {
        // Größenvergleich allein würde das nicht erkennen – File-ID/Erstellungszeit schon.
        Append("x\n");
        using var t = CreateTailer();
        t.Poll();
        File.Move(_path, Path.Combine(_dir, "AppWorkload-2.log"));
        Append("eine deutlich längere neue Datei\n");
        t.Poll();

        Assert.Single(_switches);
        Assert.Equal("x\n|SWITCH|eine deutlich längere neue Datei\n", _text.ToString());
    }

    [Fact]
    public void MultipleRolloversInARow()
    {
        Append("1\n");
        using var t = CreateTailer();
        t.Poll();
        for (int i = 2; i <= 4; i++)
        {
            File.Move(_path, Path.Combine(_dir, $"AppWorkload-r{i}.log"));
            Append($"{i}\n");
            t.Poll();
        }
        Assert.Equal("1\n|SWITCH|2\n|SWITCH|3\n|SWITCH|4\n", _text.ToString());
    }

    [Fact]
    public void TruncationOfSameFileRestartsAtZero()
    {
        Append("eine lange erste Zeile\n");
        using var t = CreateTailer();
        t.Poll();

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.SetLength(0);
            fs.Write("kurz\n"u8);
        }
        t.Poll();

        Assert.True(Assert.Single(_switches).WasTruncation);
        Assert.EndsWith("|SWITCH|kurz\n", _text.ToString());
    }

    [Fact]
    public void MultiByteCharacterSplitAcrossWritesIsDecodedCorrectly()
    {
        byte[] umlaut = Encoding.UTF8.GetBytes("ä"); // 2 Bytes
        using var t = CreateTailer();
        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.Write([(byte)'a', (byte)'b', (byte)'c', umlaut[0]]);
        t.Poll();
        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.Write([umlaut[1], (byte)'\n']);
        t.Poll();

        Assert.Equal("abcä\n", _text.ToString());
    }

    [Theory]
    [InlineData("utf8bom")]
    [InlineData("utf16")]
    public void DetectsByteOrderMark(string kind)
    {
        Encoding enc = kind == "utf16" ? Encoding.Unicode : new UTF8Encoding(true);
        File.WriteAllText(_path, "Größe\n", enc);
        using var t = CreateTailer();
        t.Poll();
        Assert.Equal("Größe\n", _text.ToString());
    }

    [Fact]
    public async Task RunAsync_DetectsRolloverViaWatcherAndPolling()
    {
        Append("vorher\n");
        using var t = new LogFileTailer(_path, TimeSpan.FromMilliseconds(100));
        var received = new StringBuilder();
        var gotNew = new TaskCompletionSource();
        t.TextRead += s =>
        {
            lock (received)
            {
                received.Append(s);
                if (received.ToString().Contains("nachher")) gotNew.TrySetResult();
            }
        };
        using var cts = new CancellationTokenSource();
        var run = t.RunAsync(cts.Token);

        await Task.Delay(300);
        Append("rest-alt\n");
        File.Move(_path, Path.Combine(_dir, "AppWorkload-live.log"));
        Append("nachher\n");

        var finished = await Task.WhenAny(gotNew.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await cts.CancelAsync();
        await run;

        Assert.Same(gotNew.Task, finished);
        lock (received)
            Assert.Equal("vorher\nrest-alt\nnachher\n", received.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
