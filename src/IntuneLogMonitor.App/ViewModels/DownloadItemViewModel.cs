using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.App.ViewModels;

/// <summary>Eine Zeile der Download-Liste.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadSnapshot _snapshot;

    public DownloadItemViewModel(DownloadSnapshot snapshot) => _snapshot = snapshot;

    public DownloadSnapshot Snapshot => _snapshot;

    public void Update(DownloadSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty); // alle Eigenschaften neu lesen
    }

    public int Id => _snapshot.Id;
    public DownloadStatus Status => _snapshot.Status;
    public string StatusText => _snapshot.StatusText;
    public string StartTimeText => _snapshot.StartTime?.ToString("dd.MM. HH:mm:ss") ?? "";
    public string AppId => _snapshot.AppId ?? "";
    public string AppName => _snapshot.AppName ?? "";

    public string TotalText => _snapshot.TotalBytes is { } t ? Formatting.FormatBytes(t) : "–";

    public string CurrentText => _snapshot.TotalBytes is null ? "–" : Formatting.FormatBytes(_snapshot.CurrentBytes);

    public double Percent => _snapshot.Percent ?? 0;
    public string PercentText => _snapshot.Percent is { } p ? $"{p:0.0} %" : "";
    public bool HasTotal => _snapshot.TotalBytes is not null;

    public string RateText => Formatting.FormatRate(_snapshot.BytesPerSecond);
    public string RemainingText => _snapshot.Status == DownloadStatus.Running ? Formatting.FormatDuration(_snapshot.Remaining) : "";
    public string DurationText => Formatting.FormatDuration(_snapshot.Duration);
    public string NotesText => string.Join(" · ", _snapshot.Notes);
    public bool HasNotes => _snapshot.Notes.Count > 0;

    public long StartSequence => _snapshot.StartSequence;

    /// <summary>Ende des Zeilenbereichs (Abschluss-/Abbruchzeile); offen = bis auf Weiteres.</summary>
    public long EndSequenceOrOpen => _snapshot.EndSequence ?? (_snapshot.Status.IsOpen() ? long.MaxValue : _snapshot.LastSequence);
}
