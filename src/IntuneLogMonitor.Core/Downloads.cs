namespace IntuneLogMonitor.Core;

public enum DownloadStatus
{
    /// <summary>Phase 1: Startmarker gesehen, Gesamtgröße noch unbekannt.</summary>
    Started,
    /// <summary>Phase 2/3: Gesamtgröße festgeschrieben, Fortschritt wird verfolgt.</summary>
    Running,
    Completed,
    /// <summary>Gesamtgröße wurde im Suchfenster nicht gefunden.</summary>
    Incomplete,
    /// <summary>Kein Fortschritt innerhalb des Inaktivitäts-Timeouts.</summary>
    NoActivity,
    /// <summary>Neuer Startmarker für dieselbe App-ID, während dieser Download noch offen war.</summary>
    Restarted,
}

public static class DownloadStatusExtensions
{
    public static string ToDisplayText(this DownloadStatus status) => status switch
    {
        DownloadStatus.Started => "Gestartet",
        DownloadStatus.Running => "Läuft",
        DownloadStatus.Completed => "Abgeschlossen",
        DownloadStatus.Incomplete => "Unvollständig",
        DownloadStatus.NoActivity => "Keine Aktivität",
        DownloadStatus.Restarted => "Abgebrochen/Neu gestartet",
        _ => status.ToString(),
    };

    /// <summary>Offen = kann noch Zeilen zugeordnet bekommen.</summary>
    public static bool IsOpen(this DownloadStatus status) =>
        status is DownloadStatus.Started or DownloadStatus.Running or DownloadStatus.NoActivity;
}

/// <summary>Unveränderlicher Zustand eines Downloads zur Weitergabe an die Oberfläche.</summary>
public sealed record DownloadSnapshot
{
    public required int Id { get; init; }
    public required DateTime? StartTime { get; init; }
    public required long StartSequence { get; init; }
    public long? EndSequence { get; init; }
    public long LastSequence { get; init; }
    public string? AppId { get; init; }
    public string? AppName { get; init; }
    public long? TotalBytes { get; init; }
    public long CurrentBytes { get; init; }
    public double? Percent { get; init; }
    public double? BytesPerSecond { get; init; }
    public TimeSpan? Remaining { get; init; }
    public DateTime? EndTime { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTime? LastActivity { get; init; }
    public required DownloadStatus Status { get; init; }
    public int AmbiguousAssignments { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string StatusText => Status.ToDisplayText();
}

/// <summary>Veränderlicher Arbeitszustand eines Downloads innerhalb des Trackers.</summary>
internal sealed class DownloadState
{
    public int Id;
    public DateTime? StartTime;
    public long StartSequence;
    public long? EndSequence;
    public long LastSequence;
    public string? AppId;
    public string? AppName;
    public long? TotalBytes;
    public long CurrentBytes;
    public double? BytesPerSecond;
    public DateTime? EndTime;
    public DownloadStatus Status;
    public int LinesSinceStart;
    /// <summary>Unvollständig nur wegen abgelaufenem Suchfenster – über die App-ID noch rettbar.</summary>
    public bool IncompleteByWindow;
    public DateTime? LastActivity;
    public long RateBaselineBytes;
    public DateTime? RateBaselineTime;
    public int AmbiguousAssignments;
    public readonly List<string> Notes = [];

    public DownloadSnapshot ToSnapshot()
    {
        double? percent = TotalBytes is > 0 ? Math.Min(100.0, CurrentBytes * 100.0 / TotalBytes.Value) : null;
        TimeSpan? remaining = null;
        if (Status == DownloadStatus.Completed)
            remaining = TimeSpan.Zero;
        else if (Status == DownloadStatus.Running && TotalBytes is > 0 && BytesPerSecond is > 0)
            remaining = TimeSpan.FromSeconds(Math.Max(0, TotalBytes.Value - CurrentBytes) / BytesPerSecond.Value);

        DateTime? reference = EndTime ?? LastActivity;
        TimeSpan? duration = StartTime is { } s && reference is { } r && r >= s ? r - s : null;

        var notes = new List<string>(Notes);
        if (AmbiguousAssignments > 0)
            notes.Add($"{AmbiguousAssignments} Fortschrittszeile(n) nicht eindeutig – dem jüngsten offenen Download mit passender Größe zugeordnet");

        return new DownloadSnapshot
        {
            Id = Id,
            StartTime = StartTime,
            StartSequence = StartSequence,
            EndSequence = EndSequence,
            LastSequence = LastSequence,
            AppId = AppId,
            AppName = AppName,
            TotalBytes = TotalBytes,
            CurrentBytes = CurrentBytes,
            Percent = percent,
            BytesPerSecond = Status is DownloadStatus.Running or DownloadStatus.Completed ? BytesPerSecond : null,
            Remaining = remaining,
            EndTime = EndTime,
            Duration = duration,
            LastActivity = LastActivity,
            Status = Status,
            AmbiguousAssignments = AmbiguousAssignments,
            Notes = notes,
        };
    }
}
