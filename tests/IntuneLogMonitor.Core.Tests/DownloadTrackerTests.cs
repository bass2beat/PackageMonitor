using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.Core.Tests;

public class DownloadTrackerTests
{
    private const string AppA = "3f1c6b0e-8a5d-4c2e-9b71-2d4e6a8c0f11";
    private const string AppB = "9b2e4d71-0c3a-4f5e-8d62-7a1b3c5e9f20";
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0);

    private readonly DownloadTracker _tracker = new(new DownloadTrackingOptions());
    private long _seq;

    private LogEntry Feed(double seconds, string message)
    {
        var e = new LogEntry { Timestamp = T0.AddSeconds(seconds), Message = message, Sequence = ++_seq };
        _tracker.Process(e);
        return e;
    }

    private const string UserGuid = "5a1e0c7d-0000-4000-8000-00000000d0e5";

    // Echtes IME-Format: "[StatusService] Downloading app (id = <GUID>, name <Name>) via DO, bytes c/t for user <GUID>"
    private static string Start(string? appId = null, string? name = null) => appId is null
        ? "[StatusService] Downloading app via DO, bytes 0/0 for user S-1-12-1-1"
        : $"[StatusService] Downloading app (id = {appId}, name {name ?? "Test App"}) via DO, bytes 0/0 for user {UserGuid}";

    /// <summary>Ohne appId: Zeile ohne GUID (Zuordnung nur über die Größe).</summary>
    private static string Bytes(long cur, long total, string? appId = null) => appId is null
        ? $"[DO] state: Transferring, bytes {cur}/{total} for user S-1-12-1-1"
        : $"[StatusService] Downloading app (id = {appId}, name Test App) via DO, bytes {cur}/{total} for user {UserGuid}";

    private void Chatter(double seconds, int count)
    {
        for (int i = 0; i < count; i++)
            Feed(seconds, "[StatusService] Sending download progress update from StatusServicePublisher.");
    }

    [Theory]
    [InlineData("(id = X, name Win32 DB04_Supersedence-BreakTest_Paket A) via DO", "Win32 DB04_Supersedence-BreakTest_Paket A")]
    [InlineData("(id = X, name Northwind Toolkit (x64)) via DO", "Northwind Toolkit (x64)")]
    [InlineData("(id = X, name = Contoso Agent) via DO", "Contoso Agent")]
    [InlineData("(id = X, Name: StoreApp_Company Portal) via WinGet", "StoreApp_Company Portal")]
    public void AppNameIsExtractedFromRealFormat(string fragment, string expected)
    {
        Feed(0, $"[StatusService] Downloading app {fragment.Replace("X", AppA)}, bytes 0/0 for user {UserGuid}".Replace("via WinGet,", "via DO,"));
        Assert.Equal(expected, Single().AppName);
    }

    [Fact]
    public void AppIdIsFirstGuidNotTheUserId()
    {
        Feed(0, Start(AppA, "X"));
        Assert.Equal(AppA, Single().AppId);
    }

    [Fact]
    public void SmallPackage_ZeroZeroThenDirectlyComplete_IsRecognized()
    {
        // Echter Ablauf kleiner Pakete: kein "0/N", nach "0/0" folgt direkt "N/N" (4. Folgezeile).
        Feed(0, Start(AppA, "Win32 Paket A"));
        Chatter(0, 3);
        var done = Feed(5, Bytes(1568, 1568, AppA));

        var d = Single();
        Assert.Equal(DownloadStatus.Completed, d.Status);
        Assert.Equal(1568, d.TotalBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), d.Duration);
        Assert.Equal(d.Id, done.DownloadId);
    }

    [Fact]
    public void NonZeroFirstLineWithoutMatchingAppIdStaysStrict()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(1568, 1568)); // keine App-ID → strenge Regel
        Assert.Equal(DownloadStatus.Started, Single().Status);
    }

    [Fact]
    public void WindowExpiredDownloadIsRescuedByAppId()
    {
        Feed(0, Start(AppA, "Paket A"));
        Chatter(1, 7); // parallele Downloads erzeugen viele Zwischenzeilen
        Assert.Equal(DownloadStatus.Incomplete, Single().Status);

        Feed(5, Bytes(0, 1000, AppA));
        var d = Single();
        Assert.Equal(DownloadStatus.Running, d.Status);
        Assert.Null(d.EndSequence);
        Assert.DoesNotContain(d.Notes, n => n.StartsWith("Keine Gesamtgröße"));
        Assert.Contains(d.Notes, n => n.Contains("über App-ID"));

        Feed(10, Bytes(1000, 1000, AppA));
        Assert.Equal(DownloadStatus.Completed, Single().Status);
    }

    [Fact]
    public void RescueByAppIdDoesNotReviveRestartedDownload()
    {
        Feed(0, Start(AppA));
        Chatter(1, 6);                    // #1 unvollständig (Fenster)
        Feed(2, Start(AppA));             // #2 neuer Versuch – #1 ist nicht mehr offen, bleibt unvollständig
        Feed(3, Bytes(500, 500, AppA));   // gehört dem jüngsten wartenden: #2

        Assert.Equal(DownloadStatus.Incomplete, Get(1).Status);
        Assert.Equal(DownloadStatus.Completed, Get(2).Status);
    }

    private DownloadSnapshot Single() => Assert.Single(_tracker.Downloads);
    private DownloadSnapshot Get(int id) => _tracker.Downloads.Single(d => d.Id == id);

    [Fact]
    public void Phase1_StartMarkerCreatesDownloadWithAppIdAndName()
    {
        var e = Feed(0, Start(AppA.ToUpperInvariant(), "Contoso Agent 4.2"));

        var d = Single();
        Assert.Equal(DownloadStatus.Started, d.Status);
        Assert.Equal("Gestartet", d.StatusText);
        Assert.Equal(AppA, d.AppId);
        Assert.Equal("Contoso Agent 4.2", d.AppName);
        Assert.Equal(T0, d.StartTime);
        Assert.Equal(d.Id, e.DownloadId);
    }

    [Fact]
    public void Phase2_TotalSizeInSecondLineSetsRunning()
    {
        Feed(0, Start(AppA));
        Feed(0.1, "[Win32App DO] DO Job set priority is DODownloadPriorityForeground");
        var sizeLine = Feed(0.2, Bytes(0, 52_428_800));

        var d = Single();
        Assert.Equal(DownloadStatus.Running, d.Status);
        Assert.Equal(52_428_800, d.TotalBytes);
        Assert.Equal(d.Id, sizeLine.DownloadId);
    }

    [Fact]
    public void Phase2_ExtraInsertedLinesWithinWindowAreTolerated()
    {
        Feed(0, Start(AppA));
        for (int i = 1; i <= 4; i++)
            Feed(i * 0.1, $"Füllzeile {i}");
        Feed(0.5, Bytes(0, 1000)); // 5. Folgezeile = noch im Fenster

        Assert.Equal(DownloadStatus.Running, Single().Status);
    }

    [Fact]
    public void Phase2_NoTotalWithinWindowMarksIncomplete()
    {
        Feed(0, Start(AppA));
        for (int i = 1; i <= 5; i++)
            Feed(i, $"Füllzeile {i}");

        var d = Single();
        Assert.Equal(DownloadStatus.Incomplete, d.Status);
        Assert.Contains(d.Notes, n => n.Contains("Gesamtgröße"));

        // Später auftauchende Größe wird nicht mehr zugeordnet.
        Feed(10, Bytes(0, 1000));
        Assert.Equal(DownloadStatus.Incomplete, Single().Status);
    }

    [Fact]
    public void Phase2_WindowSizeIsConfigurable()
    {
        var tracker = new DownloadTracker(new DownloadTrackingOptions { TotalSizeSearchWindow = 2 });
        long seq = 0;
        void F(string m) => tracker.Process(new LogEntry { Timestamp = T0, Message = m, Sequence = ++seq });
        F(Start(AppA));
        F("x");
        F("y");
        F(Bytes(0, 1000));
        Assert.Equal(DownloadStatus.Incomplete, tracker.Downloads.Single().Status);
    }

    [Fact]
    public void Phase3_ProgressUpdatesBytesPercentRateAndRemaining()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1_000_000));
        Feed(2, Bytes(100_000, 1_000_000));
        Feed(4, Bytes(300_000, 1_000_000)); // 200 000 B in 2 s = 100 000 B/s

        var d = Single();
        Assert.Equal(DownloadStatus.Running, d.Status);
        Assert.Equal(300_000, d.CurrentBytes);
        Assert.Equal(30.0, d.Percent!.Value, 3);
        Assert.Equal(100_000, d.BytesPerSecond!.Value, 3);
        Assert.Equal(TimeSpan.FromSeconds(7), d.Remaining);
    }

    [Fact]
    public void Phase3_RateUsesLastLineWithDifferentTimestamp()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1_000_000));
        Feed(2, Bytes(100_000, 1_000_000));
        Feed(2, Bytes(150_000, 1_000_000)); // gleicher Zeitstempel → Basis bleibt (t = 2 s, 100 000 B)
        Feed(3, Bytes(350_000, 1_000_000)); // (350 000 − 100 000) B / 1 s

        Assert.Equal(250_000, Single().BytesPerSecond!.Value, 3);
    }

    [Fact]
    public void Phase3_CurrentEqualsTotalCompletesWithDuration()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        Feed(5, Bytes(500, 1000));
        var last = Feed(30, Bytes(1000, 1000));

        var d = Single();
        Assert.Equal(DownloadStatus.Completed, d.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), d.Duration);
        Assert.Equal(T0.AddSeconds(30), d.EndTime);
        Assert.Equal(last.Sequence, d.EndSequence);
        Assert.Equal(100.0, d.Percent);
        Assert.Equal(TimeSpan.Zero, d.Remaining);
    }

    [Fact]
    public void Phase3_LinesWithDifferentTotalAreIgnored()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        var foreign = Feed(2, Bytes(999, 5000));

        Assert.Equal(0, Single().CurrentBytes);
        Assert.Null(foreign.DownloadId);
    }

    [Fact]
    public void StartLineItselfIsNotTreatedAsProgressOrCompletion()
    {
        Feed(0, Start(AppA)); // enthält "bytes 0/0"
        Assert.Equal(DownloadStatus.Started, Single().Status);
    }

    [Fact]
    public void Parallel_AssignmentBySizeWhenNoAppIdInLine()
    {
        Feed(0, Start(AppA));
        Feed(0.1, Bytes(0, 1000));
        Feed(0.5, Start(AppB));
        Feed(0.6, Bytes(0, 8000));

        Feed(1, Bytes(500, 1000));
        Feed(2, Bytes(4000, 8000));
        Feed(3, Bytes(1000, 1000));

        Assert.Equal(DownloadStatus.Completed, Get(1).Status);
        Assert.Equal(DownloadStatus.Running, Get(2).Status);
        Assert.Equal(4000, Get(2).CurrentBytes);
        Assert.Equal(8000, Get(2).TotalBytes);
    }

    [Fact]
    public void Parallel_AppIdInLineTakesPrecedenceOverSize()
    {
        // Gleiche Gesamtgröße, aber Zeilen tragen die App-ID.
        Feed(0, Start(AppA));
        Feed(0.1, Bytes(0, 1000, AppA));
        Feed(0.5, Start(AppB));
        Feed(0.6, Bytes(0, 1000, AppB));

        var line = Feed(1, Bytes(700, 1000, AppA));

        Assert.Equal(700, Get(1).CurrentBytes);
        Assert.Equal(0, Get(2).CurrentBytes);
        Assert.Equal(1, line.DownloadId);
        Assert.Equal(0, Get(1).AmbiguousAssignments);
    }

    [Fact]
    public void Parallel_TotalLineWithAppIdGoesToMatchingPendingDownload()
    {
        Feed(0, Start(AppA));
        Feed(0.1, Start(AppB));
        Feed(0.2, Bytes(0, 2000, AppB)); // gehört B, obwohl A älter ist
        Feed(0.3, Bytes(0, 1000));        // ohne ID → ältester wartender (A)

        Assert.Equal(1000, Get(1).TotalBytes);
        Assert.Equal(2000, Get(2).TotalBytes);
    }

    [Fact]
    public void Parallel_AmbiguousAssignmentGoesToYoungestAndIsNoted()
    {
        Feed(0, Start(AppA));
        Feed(0.1, Bytes(0, 1000));
        Feed(0.5, Start(AppB));
        Feed(0.6, Bytes(0, 1000));

        var line = Feed(1, Bytes(300, 1000));

        Assert.Equal(2, line.DownloadId);
        Assert.Equal(300, Get(2).CurrentBytes);
        Assert.Equal(0, Get(1).CurrentBytes);
        Assert.Equal(1, Get(2).AmbiguousAssignments);
        Assert.Contains(Get(2).Notes, n => n.Contains("nicht eindeutig"));
    }

    [Fact]
    public void NewStartForSameAppIdMarksOldAsRestarted()
    {
        Feed(0, Start(AppA, "Tailspin Reader"));
        Feed(1, Bytes(0, 1000));
        Feed(2, Bytes(400, 1000));
        var restart = Feed(60, Start(AppA, "Tailspin Reader"));

        var old = Get(1);
        Assert.Equal(DownloadStatus.Restarted, old.Status);
        Assert.Equal("Abgebrochen/Neu gestartet", old.StatusText);
        Assert.Equal(restart.Sequence, old.EndSequence);
        Assert.Equal(DownloadStatus.Started, Get(2).Status);

        // Folgezeilen gehen an den neuen Versuch, nicht an den abgebrochenen.
        Feed(61, Bytes(0, 1000));
        Feed(62, Bytes(1000, 1000));
        Assert.Equal(DownloadStatus.Completed, Get(2).Status);
        Assert.Equal(400, Get(1).CurrentBytes);
    }

    [Fact]
    public void StartForDifferentAppIdDoesNotRestart()
    {
        Feed(0, Start(AppA));
        Feed(1, Start(AppB));
        Assert.All(_tracker.Downloads, d => Assert.Equal(DownloadStatus.Started, d.Status));
    }

    [Fact]
    public void NoProgressForTimeoutMarksNoActivity_ByLogTime()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        Feed(2, Bytes(100, 1000));
        Feed(2 + 9 * 60, "irgendeine Zeile");
        Assert.Equal(DownloadStatus.Running, Single().Status);

        Feed(2 + 10 * 60 + 1, "irgendeine Zeile");
        Assert.Equal(DownloadStatus.NoActivity, Single().Status);
        Assert.Equal("Keine Aktivität", Single().StatusText);
    }

    [Fact]
    public void NoProgressForTimeoutMarksNoActivity_ByWallClock()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        _tracker.CheckInactivity(T0.AddMinutes(5));
        Assert.Equal(DownloadStatus.Running, Single().Status);
        _tracker.CheckInactivity(T0.AddMinutes(11));
        Assert.Equal(DownloadStatus.NoActivity, Single().Status);
    }

    [Fact]
    public void NoActivityDownloadResumesOnNewProgress()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        _tracker.CheckInactivity(T0.AddMinutes(20));
        Feed(20 * 60, Bytes(1000, 1000));

        Assert.Equal(DownloadStatus.Completed, Single().Status);
    }

    [Fact]
    public void InactivityTimeoutIsConfigurable()
    {
        var tracker = new DownloadTracker(new DownloadTrackingOptions { InactivityTimeoutMinutes = 1 });
        tracker.Process(new LogEntry { Sequence = 1, Timestamp = T0, Message = Start(AppA) });
        tracker.Process(new LogEntry { Sequence = 2, Timestamp = T0, Message = Bytes(0, 10) });
        tracker.CheckInactivity(T0.AddSeconds(61));
        Assert.Equal(DownloadStatus.NoActivity, tracker.Downloads.Single().Status);
    }

    [Fact]
    public void TakeChangesReturnsOnlyChangedDownloads()
    {
        Feed(0, Start(AppA));
        Feed(1, Bytes(0, 1000));
        Feed(2, Start(AppB));
        _ = _tracker.TakeChanges();

        Feed(3, Bytes(10, 1000));
        var changes = _tracker.TakeChanges();
        Assert.Equal(1, Assert.Single(changes).Id);
        Assert.Empty(_tracker.TakeChanges());
    }

    [Fact]
    public void CustomPatternsFromConfigurationAreUsed()
    {
        var tracker = new DownloadTracker(new DownloadTrackingOptions
        {
            StartMarkerPattern = @"^BEGIN DOWNLOAD",
            ProgressPattern = @"progress=(?<current>\d+)\sof\s(?<total>\d+)",
        });
        tracker.Process(new LogEntry { Sequence = 1, Timestamp = T0, Message = "BEGIN DOWNLOAD x" });
        tracker.Process(new LogEntry { Sequence = 2, Timestamp = T0.AddSeconds(1), Message = "progress=0 of 50" });
        tracker.Process(new LogEntry { Sequence = 3, Timestamp = T0.AddSeconds(2), Message = "progress=50 of 50" });
        Assert.Equal(DownloadStatus.Completed, tracker.Downloads.Single().Status);
    }

    [Fact]
    public void ProgressPatternWithoutRequiredGroupsIsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            new DownloadTracker(new DownloadTrackingOptions { ProgressPattern = @"bytes (\d+)/(\d+)" }));

    [Fact]
    public void SyntheticEntriesDoNotCountTowardsWindow()
    {
        Feed(0, Start(AppA));
        for (int i = 0; i < 10; i++)
            _tracker.Process(new LogEntry { Sequence = ++_seq, IsSynthetic = true, Message = "Rollover" });
        Feed(1, Bytes(0, 1000));
        Assert.Equal(DownloadStatus.Running, Single().Status);
    }
}
