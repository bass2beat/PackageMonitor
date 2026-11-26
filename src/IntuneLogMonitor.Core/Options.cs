namespace IntuneLogMonitor.Core;

/// <summary>
/// Konfiguration für Bereich C. Die Standardwerte sind die festen Einstellungen der App.
/// </summary>
public sealed class AppWorkloadMonitorOptions
{
    public const string SectionName = "AppWorkloadMonitor";

    /// <summary>Pfad der überwachten Logdatei.</summary>
    public string LogFilePath { get; set; } =
        @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log";

    /// <summary>Intervall der periodischen Prüfung (Größe, Erstellungszeit, File-ID) in Millisekunden.</summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>true: beim Start den vorhandenen Dateiinhalt einlesen; false: erst ab Dateiende verfolgen.</summary>
    public bool ReadExistingContent { get; set; } = true;

    /// <summary>Maximale Anzahl Logzeilen in der Anzeige; ältere Einträge werden verworfen.</summary>
    public int MaxLogEntries { get; set; } = 250_000;

    /// <summary>Intervall, in dem Downloads anhand der Uhrzeit auf Inaktivität geprüft werden (Sekunden).</summary>
    public int InactivityCheckIntervalSeconds { get; set; } = 15;

    public DownloadTrackingOptions Tracking { get; set; } = new();

    public DeliveryOptimizationOptions DeliveryOptimization { get; set; } = new();
}

/// <summary>Abfrage von Get-DeliveryOptimizationStatus/-PerfSnap und Messung der Cache-Ordner.</summary>
public sealed class DeliveryOptimizationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Abfrageintervall in Sekunden.</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>Messintervall der Cache-Ordner in Sekunden (unabhängig von der DO-Abfrage).</summary>
    public int CacheRefreshIntervalSeconds { get; set; } = 5;

    /// <summary>Maximale Laufzeit eines PowerShell-Aufrufs.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>PowerShell für die DO-Cmdlets (Windows PowerShell 5.1 enthält das Modul DeliveryOptimization).</summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    /// <summary>
    /// Zu messende Ordner. Umgebungsvariablen (z. B. %windir%) werden aufgelöst.
    /// Leer = <see cref="DefaultCacheFolders"/>.
    /// </summary>
    public List<CacheFolderOptions> CacheFolders { get; set; } = [];

    public static IReadOnlyList<CacheFolderOptions> DefaultCacheFolders { get; } =
    [
        new() { Name = "Delivery Optimization cache", Path = @"%windir%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" },
        new() { Name = "IME download (incoming)", Path = @"%ProgramFiles(x86)%\Microsoft Intune Management Extension\Content\Incoming" },
        new() { Name = "IME staging", Path = @"%ProgramFiles(x86)%\Microsoft Intune Management Extension\Content\Staging" },
        new() { Name = "IME package cache (IMECache)", Path = @"%windir%\IMECache" },
    ];

    public IReadOnlyList<CacheFolderOptions> EffectiveCacheFolders => CacheFolders.Count > 0 ? CacheFolders : DefaultCacheFolders;
}

public sealed class CacheFolderOptions
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
/// Muster und Grenzwerte der Download-Zustandsmaschine. Alle Muster sind reguläre Ausdrücke.
/// </summary>
public sealed class DownloadTrackingOptions
{
    /// <summary>Phase 1: Startmarker eines Downloads.</summary>
    public string StartMarkerPattern { get; set; } = "via DO, bytes 0/0 for user";

    /// <summary>Phase 2/3: Fortschritt. Benötigt die Gruppen "current" und "total".</summary>
    public string ProgressPattern { get; set; } = @"bytes\s+(?<current>\d+)/(?<total>\d+)";

    /// <summary>App-ID (GUID). Benötigt die Gruppe "appId".</summary>
    public string AppIdPattern { get; set; } =
        @"(?<appId>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})";

    /// <summary>Optionaler App-Name in der Startzeile. Benötigt die Gruppe "appName". Leer = deaktiviert.</summary>
    public string AppNamePattern { get; set; } = @"(?i)\bname\s*[=:]?\s*(?<appName>.+?)\)\s*via\b";

    /// <summary>Anzahl Folgezeilen nach der Startzeile, in denen die Gesamtgröße gesucht wird.</summary>
    public int TotalSizeSearchWindow { get; set; } = 5;

    /// <summary>Zeit ohne neue Fortschrittszeile, nach der ein Download als "Keine Aktivität" gilt.</summary>
    public double InactivityTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// true (Standard): Nur eine Zeile "bytes 0/&lt;Gesamt&gt;" legt in Phase 2 die Gesamtgröße fest.
    /// false: Jede Fortschrittszeile mit Gesamt &gt; 0 im Suchfenster zählt (sofern die Größe nicht schon
    /// zu einem laufenden Download gehört).
    /// </summary>
    public bool StrictTotalSizeDetection { get; set; } = true;

    /// <summary>Inaktivität anhand der Log-Zeit jeder neuen Zeile prüfen (wirkt auch beim Einlesen alter Logs).</summary>
    public bool CheckInactivityByLogTime { get; set; } = true;

    /// <summary>Inaktivität zusätzlich periodisch anhand der Systemuhr prüfen.</summary>
    public bool CheckInactivityBySystemClock { get; set; } = true;

    /// <summary>Ein Download mit "Keine Aktivität" wechselt bei neuem Fortschritt zurück auf "Läuft".</summary>
    public bool ResumeAfterNoActivity { get; set; } = true;

    /// <summary>
    /// Ein Download, der vor Phase 2 hängen bleibt (keine Gesamtgröße, keine Aktivität innerhalb des
    /// Timeouts), wird als "Unvollständig" markiert. false: er bleibt bis zum Ende des Suchfensters "Gestartet".
    /// </summary>
    public bool MarkStalledStartsIncomplete { get; set; } = true;

    public TimeSpan InactivityTimeout => TimeSpan.FromMinutes(InactivityTimeoutMinutes);
}
