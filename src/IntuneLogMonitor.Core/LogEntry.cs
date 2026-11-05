namespace IntuneLogMonitor.Core;

public enum LogSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Ein geparster Logeintrag (CMTrace) oder eine nicht interpretierbare Rohzeile.</summary>
public sealed class LogEntry
{
    /// <summary>Fortlaufende Nummer über alle Dateien (auch über Rollover) hinweg.</summary>
    public long Sequence { get; internal set; }

    public DateTime? Timestamp { get; init; }
    public string Component { get; init; } = "";
    public string Context { get; init; } = "";
    public LogSeverity Severity { get; init; } = LogSeverity.Info;
    public string Thread { get; init; } = "";
    public string File { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>true, wenn die Zeile nicht im CMTrace-Format vorlag.</summary>
    public bool IsRaw { get; init; }

    /// <summary>true für vom Monitor selbst erzeugte Hinweiszeilen (z. B. Rollover-Markierung).</summary>
    public bool IsSynthetic { get; init; }

    /// <summary>Download, dem diese Zeile zugeordnet wurde (Start-, Größen- oder Fortschrittszeile).</summary>
    public int? DownloadId { get; internal set; }

    /// <summary>Erste Zeile der Nachricht, für die einzeilige Tabellendarstellung.</summary>
    public string MessageFirstLine
    {
        get
        {
            int i = Message.IndexOfAny(['\r', '\n']);
            return i < 0 ? Message : Message[..i] + " …";
        }
    }

    public override string ToString() => $"#{Sequence} {Timestamp:HH:mm:ss.fff} [{Component}] {Message}";
}
