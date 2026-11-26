using System.Globalization;
using System.Text.RegularExpressions;

namespace IntuneLogMonitor.Core;

/// <summary>
/// Zustandsmaschine zur Verfolgung von IME-Downloads in drei Phasen:
/// <list type="number">
/// <item>Start: Zeile mit Startmarker → neuer Download, Status "Gestartet" (App-ID/-Name werden übernommen).</item>
/// <item>Gesamtgröße: In den nächsten N Zeilen (Suchfenster) eine Zeile "bytes 0/&lt;Gesamt&gt;" mit Gesamt &gt; 0 →
///   Gesamtgröße festschreiben, Status "Läuft". Ohne Treffer im Fenster → "Unvollständig".</item>
/// <item>Fortschritt: Zeilen "bytes &lt;aktuell&gt;/&lt;gesamt&gt;", deren Gesamtwert exakt der festgeschriebenen
///   Größe entspricht, aktualisieren Fortschritt, Rate und Restzeit. aktuell == gesamt → "Abgeschlossen".</item>
/// </list>
/// Mehrere Downloads können parallel laufen. Zuordnung primär über die App-ID in der Zeile, sekundär über
/// die Gesamtgröße; bei Mehrdeutigkeit gewinnt der jüngste offene Download (wird vermerkt).
/// Der Tracker ist nicht threadsicher und muss von einem einzigen Thread bedient werden.
/// </summary>
public sealed class DownloadTracker
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly DownloadTrackingOptions _options;
    private readonly Regex _startRegex;
    private readonly Regex _progressRegex;
    private readonly Regex _appIdRegex;
    private readonly Regex? _appNameRegex;

    private readonly List<DownloadState> _downloads = [];
    private readonly HashSet<DownloadState> _changed = [];
    private int _nextId = 1;

    public DownloadTracker(DownloadTrackingOptions options)
    {
        _options = options;
        const RegexOptions opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        _startRegex = new Regex(options.StartMarkerPattern, opts, RegexTimeout);
        _progressRegex = new Regex(options.ProgressPattern, opts, RegexTimeout);
        _appIdRegex = new Regex(options.AppIdPattern, opts, RegexTimeout);
        _appNameRegex = string.IsNullOrWhiteSpace(options.AppNamePattern)
            ? null
            : new Regex(options.AppNamePattern, opts, RegexTimeout);

        if (_progressRegex.GroupNumberFromName("current") < 0 || _progressRegex.GroupNumberFromName("total") < 0)
            throw new ArgumentException("ProgressPattern must contain the named groups 'current' and 'total'.", nameof(options));
    }

    public IReadOnlyList<DownloadSnapshot> Downloads => _downloads.Select(d => d.ToSnapshot()).ToList();

    /// <summary>Liefert alle seit dem letzten Aufruf geänderten Downloads und setzt die Änderungsliste zurück.</summary>
    public List<DownloadSnapshot> TakeChanges()
    {
        var list = _changed.OrderBy(d => d.Id).Select(d => d.ToSnapshot()).ToList();
        _changed.Clear();
        return list;
    }

    /// <summary>Verarbeitet einen Logeintrag. Synthetische Einträge werden ignoriert.</summary>
    public void Process(LogEntry entry)
    {
        if (entry.IsSynthetic)
            return;

        if (_options.CheckInactivityByLogTime && entry.Timestamp is { } ts)
            CheckInactivity(ts);

        string message = entry.Message;

        // Phase 1: Start
        if (SafeIsMatch(_startRegex, message))
        {
            HandleStart(entry);
            return;
        }

        // Suchfenster der wartenden Downloads weiterzählen
        foreach (var d in _downloads)
            if (d.Status == DownloadStatus.Started)
                d.LinesSinceStart++;

        var match = SafeMatch(_progressRegex, message);
        if (match is { Success: true }
            && long.TryParse(match.Groups["current"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long current)
            && long.TryParse(match.Groups["total"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long total)
            && total > 0)
        {
            string? lineAppId = ExtractAppId(message);
            // Phase 2:
            //  - "bytes 0/N" gilt immer als Größenzeile.
            //  - Trägt die Zeile die App-ID eines Downloads, der noch auf seine Größe wartet, gilt sie
            //    ebenfalls als Größenzeile – auch wenn aktuell > 0 ist. Kleine Pakete schreiben in echten
            //    IME-Logs nach "0/0" direkt "N/N" ohne eine Zeile "0/N".
            //  - Locker (StrictTotalSizeDetection = false): jede Zeile mit N > 0, solange N nicht schon
            //    die Größe eines laufenden Downloads ist (dann ist es eine Fortschrittszeile).
            bool totalCandidate = current == 0
                || (lineAppId is not null && _downloads.Any(d => SameAppId(d, lineAppId) && IsAwaitingTotal(d)))
                || (!_options.StrictTotalSizeDetection && !_downloads.Any(d => d.TotalBytes == total && IsProgressTarget(d.Status)));
            bool handled = totalCandidate && TryAssignTotal(entry, total, lineAppId, current);
            if (!handled)
                TryAssignProgress(entry, current, total, lineAppId);
        }

        // Wartende Downloads ohne Gesamtgröße nach Ablauf des Suchfensters als unvollständig markieren
        foreach (var d in _downloads)
        {
            if (d.Status == DownloadStatus.Started && d.LinesSinceStart >= _options.TotalSizeSearchWindow)
            {
                d.Status = DownloadStatus.Incomplete;
                d.IncompleteByWindow = true;
                d.EndTime = entry.Timestamp;
                d.EndSequence = entry.Sequence;
                d.Notes.Add(WindowExpiredNote);
                _changed.Add(d);
            }
        }
    }

    /// <summary>
    /// Markiert offene Downloads ohne Aktivität seit dem Timeout. Wird mit der Log-Zeit jeder Zeile
    /// und periodisch mit der aktuellen Uhrzeit aufgerufen.
    /// </summary>
    public void CheckInactivity(DateTime now)
    {
        var timeout = _options.InactivityTimeout;
        foreach (var d in _downloads)
        {
            if (d.LastActivity is not { } last || now - last <= timeout)
                continue;

            if (d.Status == DownloadStatus.Running)
            {
                d.Status = DownloadStatus.NoActivity;
                d.Notes.Add($"Keine Fortschrittszeile seit {last:HH:mm:ss} (> {timeout.TotalMinutes:0.#} min)");
                _changed.Add(d);
            }
            else if (d.Status == DownloadStatus.Started && _options.MarkStalledStartsIncomplete)
            {
                d.Status = DownloadStatus.Incomplete;
                d.Notes.Add($"Total size not determined, no activity since {last:HH:mm:ss}");
                _changed.Add(d);
            }
        }
    }

    private void HandleStart(LogEntry entry)
    {
        string? appId = ExtractAppId(entry.Message);
        string? appName = ExtractAppName(entry.Message);

        if (appId is not null)
        {
            foreach (var old in _downloads)
            {
                if (old.Status.IsOpen() && string.Equals(old.AppId, appId, StringComparison.OrdinalIgnoreCase))
                {
                    old.Status = DownloadStatus.Restarted;
                    old.EndTime = entry.Timestamp;
                    old.EndSequence = entry.Sequence;
                    old.Notes.Add($"New start marker for the same app ID in line #{entry.Sequence}");
                    _changed.Add(old);
                }
            }
        }

        var d = new DownloadState
        {
            Id = _nextId++,
            StartTime = entry.Timestamp,
            StartSequence = entry.Sequence,
            LastSequence = entry.Sequence,
            AppId = appId,
            AppName = appName,
            Status = DownloadStatus.Started,
            LastActivity = entry.Timestamp,
        };
        _downloads.Add(d);
        entry.DownloadId = d.Id;
        _changed.Add(d);
    }

    /// <summary>Phase 2: Gesamtgröße einem wartenden Download zuordnen.</summary>
    private string WindowExpiredNote => $"No total size found in the next {_options.TotalSizeSearchWindow} lines";

    /// <summary>Wartet auf die Gesamtgröße: Phase 1, oder nur wegen des Suchfensters unvollständig (über App-ID rettbar).</summary>
    private static bool IsAwaitingTotal(DownloadState d) =>
        d.Status == DownloadStatus.Started || (d.Status == DownloadStatus.Incomplete && d.IncompleteByWindow);

    private bool TryAssignTotal(LogEntry entry, long total, string? lineAppId, long current)
    {
        DownloadState? target = null;

        // 1. App-ID der Zeile passt zu einem wartenden Download (jüngster zuerst) – auch nach Ablauf des
        //    Suchfensters, z. B. wenn parallele Downloads viele Zwischenzeilen erzeugen.
        if (lineAppId is not null)
            target = _downloads.LastOrDefault(d => SameAppId(d, lineAppId) && IsAwaitingTotal(d));

        // 2. Sonst ältester wartender Download im Suchfenster – aber nur, wenn die App-ID der Zeile nicht
        //    zu einem anderen bekannten Download gehört (keine oder unbekannte ID, z. B. eine DO-Job-ID).
        if (target is null && (lineAppId is null || !_downloads.Any(d => SameAppId(d, lineAppId))))
            target = _downloads.FirstOrDefault(d => d.Status == DownloadStatus.Started
                                                    && d.LinesSinceStart <= _options.TotalSizeSearchWindow);

        if (target is null)
            return false;

        if (target.Status == DownloadStatus.Incomplete)
        {
            // Rettung über die App-ID: Status zurücknehmen
            target.IncompleteByWindow = false;
            target.EndTime = null;
            target.EndSequence = null;
            target.Notes.Remove(WindowExpiredNote);
            target.Notes.Add($"Total size found only after the search window (matched by app ID, line #{entry.Sequence})");
        }

        target.TotalBytes = total;
        target.CurrentBytes = 0;
        target.Status = DownloadStatus.Running;
        target.LastActivity = entry.Timestamp ?? target.LastActivity;
        target.RateBaselineBytes = current;
        target.RateBaselineTime = entry.Timestamp;
        target.LastSequence = entry.Sequence;
        entry.DownloadId = target.Id;
        _changed.Add(target);

        // Nur im lockeren Modus möglich: Größenzeile trägt bereits Fortschritt (ggf. sogar Abschluss).
        if (current > 0)
            ApplyProgress(target, entry, current);
        return true;
    }

    /// <summary>Downloads, denen Fortschrittszeilen zugeordnet werden dürfen.</summary>
    private bool IsProgressTarget(DownloadStatus status) =>
        status == DownloadStatus.Running || (status == DownloadStatus.NoActivity && _options.ResumeAfterNoActivity);

    /// <summary>Phase 3: Fortschrittszeile einem laufenden Download mit gleicher Gesamtgröße zuordnen.</summary>
    private void TryAssignProgress(LogEntry entry, long current, long total, string? lineAppId)
    {
        var candidates = _downloads
            .Where(d => d.TotalBytes == total && IsProgressTarget(d.Status))
            .ToList();
        if (candidates.Count == 0)
            return;

        DownloadState? target = null;
        if (lineAppId is not null)
            target = candidates.FirstOrDefault(d => string.Equals(d.AppId, lineAppId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            if (candidates.Count == 1)
            {
                target = candidates[0];
            }
            else
            {
                // Nicht eindeutig: jüngster offener Download mit passender Gesamtgröße.
                target = candidates.MaxBy(d => d.StartSequence)!;
                target.AmbiguousAssignments++;
            }
        }

        ApplyProgress(target, entry, current);
    }

    private void ApplyProgress(DownloadState d, LogEntry entry, long current)
    {
        long total = d.TotalBytes!.Value;
        if (d.Status == DownloadStatus.NoActivity)
        {
            d.Status = DownloadStatus.Running;
            d.Notes.Add($"Activity resumed from line #{entry.Sequence}");
        }

        if (entry.Timestamp is { } t)
        {
            if (d.RateBaselineTime is { } bt)
            {
                double seconds = (t - bt).TotalSeconds;
                if (seconds > 0 && current >= d.RateBaselineBytes)
                {
                    d.BytesPerSecond = (current - d.RateBaselineBytes) / seconds;
                    d.RateBaselineBytes = current;
                    d.RateBaselineTime = t;
                }
                else if (current < d.RateBaselineBytes)
                {
                    // Zähler zurückgesetzt (z. B. Wiederaufnahme) – neue Basis.
                    d.RateBaselineBytes = current;
                    d.RateBaselineTime = t;
                }
                // seconds == 0: Basis beibehalten, damit die nächste Zeile die Differenz über beide misst.
            }
            else
            {
                d.RateBaselineBytes = current;
                d.RateBaselineTime = t;
            }
            d.LastActivity = t;
        }

        d.CurrentBytes = current;
        d.LastSequence = entry.Sequence;
        entry.DownloadId = d.Id;

        if (current == total)
        {
            d.Status = DownloadStatus.Completed;
            d.EndTime = entry.Timestamp;
            d.EndSequence = entry.Sequence;
        }
        _changed.Add(d);
    }

    private static bool SameAppId(DownloadState d, string appId) =>
        string.Equals(d.AppId, appId, StringComparison.OrdinalIgnoreCase);

    private string? ExtractAppId(string message)
    {
        var m = SafeMatch(_appIdRegex, message);
        if (m is not { Success: true }) return null;
        var g = m.Groups["appId"];
        return (g.Success ? g.Value : m.Value).ToLowerInvariant();
    }

    private string? ExtractAppName(string message)
    {
        if (_appNameRegex is null) return null;
        var m = SafeMatch(_appNameRegex, message);
        if (m is not { Success: true }) return null;
        var g = m.Groups["appName"];
        string value = (g.Success ? g.Value : m.Value).Trim().Trim('"', '\'');
        return value.Length == 0 ? null : value;
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Match? SafeMatch(Regex regex, string input)
    {
        try { return regex.Match(input); }
        catch (RegexMatchTimeoutException) { return null; }
    }
}
