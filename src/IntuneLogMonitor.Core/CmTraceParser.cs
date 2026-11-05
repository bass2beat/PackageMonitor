using System.Globalization;

namespace IntuneLogMonitor.Core;

/// <summary>
/// Inkrementeller Parser für das CMTrace-Format:
/// <c>&lt;![LOG[Nachricht]LOG]!&gt;&lt;time="..." date="..." component="..." context="" type="1" thread="12" file="..."&gt;</c>
/// Text kann in beliebigen Stücken zugeführt werden; Einträge dürfen sich über mehrere Zeilen
/// und über Lesegrenzen erstrecken. Unvollständige Einträge werden gepuffert, bis sie vollständig sind.
/// </summary>
public sealed class CmTraceParser
{
    private const string MessageStart = "<![LOG[";
    private const string MessageEnd = "]LOG]!>";

    /// <summary>Schutz gegen unendlich wachsende Puffer bei defekten Dateien.</summary>
    private const int MaxPendingChars = 4 * 1024 * 1024;

    private string _pending = "";

    /// <summary>Führt neuen Text zu und liefert alle dadurch vollständig gewordenen Einträge.</summary>
    public List<LogEntry> Feed(string text)
    {
        var result = new List<LogEntry>();
        if (text.Length == 0)
            return result;

        string s = _pending.Length == 0 ? text : _pending + text;
        int pos = 0;

        while (pos < s.Length)
        {
            int start = s.IndexOf(MessageStart, pos, StringComparison.Ordinal);
            if (start < 0)
            {
                // Kein weiterer Eintrag: nur vollständige Rohzeilen ausgeben. Der Rest könnte
                // der Anfang eines Markers ("<![LO") sein und bleibt gepuffert.
                int lastNl = s.LastIndexOf('\n');
                if (lastNl >= pos)
                {
                    EmitRawLines(s.AsSpan(pos, lastNl + 1 - pos), result);
                    pos = lastNl + 1;
                }
                break;
            }

            if (start > pos)
            {
                EmitRawLines(s.AsSpan(pos, start - pos), result);
                pos = start;
            }

            int msgEnd = s.IndexOf(MessageEnd, start + MessageStart.Length, StringComparison.Ordinal);
            if (msgEnd < 0)
                break; // Nachricht noch nicht vollständig

            int attrOpen = msgEnd + MessageEnd.Length;
            while (attrOpen < s.Length && s[attrOpen] is ' ' or '\t')
                attrOpen++;
            if (attrOpen >= s.Length)
                break;

            if (s[attrOpen] != '<')
            {
                // Eintrag ohne Attributteil – Nachricht trotzdem übernehmen.
                result.Add(new LogEntry { Message = s[(start + MessageStart.Length)..msgEnd] });
                pos = attrOpen;
                continue;
            }

            int attrClose = FindClosingBracket(s, attrOpen + 1);
            if (attrClose < 0)
                break; // Attributteil noch nicht vollständig

            string message = s[(start + MessageStart.Length)..msgEnd];
            string attributes = s[(attrOpen + 1)..attrClose];
            result.Add(CreateEntry(message, attributes));

            pos = attrClose + 1;
            // Zeilenende des Eintrags überspringen
            if (pos < s.Length && s[pos] == '\r') pos++;
            if (pos < s.Length && s[pos] == '\n') pos++;
        }

        _pending = pos >= s.Length ? "" : s[pos..];

        if (_pending.Length > MaxPendingChars)
            result.AddRange(Flush());

        return result;
    }

    /// <summary>
    /// Gibt einen gepufferten Rest als Rohzeile(n) aus, z. B. wenn eine Datei endet (Rollover).
    /// </summary>
    public List<LogEntry> Flush()
    {
        var result = new List<LogEntry>();
        if (_pending.Length > 0)
        {
            EmitRawLines(_pending.AsSpan(), result);
            _pending = "";
        }
        return result;
    }

    /// <summary>Parst genau einen vollständigen Eintrag (für Tests und Einzelzeilen).</summary>
    public static LogEntry? ParseSingle(string text)
    {
        var parser = new CmTraceParser();
        var entries = parser.Feed(text);
        entries.AddRange(parser.Flush());
        return entries.FirstOrDefault(e => !e.IsRaw) ?? entries.FirstOrDefault();
    }

    /// <summary>Sucht das schließende '&gt;' des Attributteils; '&gt;' innerhalb von Anführungszeichen zählt nicht.</summary>
    private static int FindClosingBracket(string s, int from)
    {
        bool inQuotes = false;
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '>' && !inQuotes) return i;
        }
        return -1;
    }

    private static void EmitRawLines(ReadOnlySpan<char> text, List<LogEntry> result)
    {
        foreach (var range in text.Split('\n'))
        {
            var line = text[range].TrimEnd('\r');
            if (line.IsWhiteSpace())
                continue;
            result.Add(new LogEntry { Message = line.ToString(), IsRaw = true, Component = "" });
        }
    }

    private static LogEntry CreateEntry(string message, string attributes)
    {
        string? time = null, date = null, component = null, context = null, type = null, thread = null, file = null;

        int i = 0;
        while (i < attributes.Length)
        {
            int eq = attributes.IndexOf('=', i);
            if (eq < 0) break;
            string name = attributes[i..eq].Trim();
            int q1 = attributes.IndexOf('"', eq);
            if (q1 < 0) break;
            int q2 = attributes.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            string value = attributes[(q1 + 1)..q2];
            switch (name)
            {
                case "time": time = value; break;
                case "date": date = value; break;
                case "component": component = value; break;
                case "context": context = value; break;
                case "type": type = value; break;
                case "thread": thread = value; break;
                case "file": file = value; break;
            }
            i = q2 + 1;
        }

        return new LogEntry
        {
            Message = message,
            Timestamp = ParseTimestamp(date, time),
            Component = component ?? "",
            Context = context ?? "",
            Severity = type switch
            {
                "2" => LogSeverity.Warning,
                "3" => LogSeverity.Error,
                _ => LogSeverity.Info,
            },
            Thread = thread ?? "",
            File = file ?? "",
        };
    }

    /// <summary>
    /// Parst date="M-d-yyyy" und time="HH:mm:ss.fffffff" (optional mit Zeitzonen-Bias "+60"/"-120").
    /// Der Bias wird ignoriert; die Zeit wird als lokale Zeit interpretiert, wie CMTrace es anzeigt.
    /// </summary>
    internal static DateTime? ParseTimestamp(string? date, string? time)
    {
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
            return null;

        var dateParts = date.Split('-', '/', '.');
        if (dateParts.Length != 3
            || !int.TryParse(dateParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(dateParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(dateParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
            return null;

        // Bias abtrennen: Vorzeichen nach dem Sekundenanteil
        string t = time;
        int biasIdx = t.IndexOfAny(['+', '-'], Math.Min(8, t.Length));
        if (biasIdx > 0) t = t[..biasIdx];

        var timeParts = t.Split(':');
        if (timeParts.Length != 3
            || !int.TryParse(timeParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(timeParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minute))
            return null;

        string secPart = timeParts[2];
        string fraction = "";
        int dot = secPart.IndexOf('.');
        if (dot >= 0)
        {
            fraction = secPart[(dot + 1)..];
            secPart = secPart[..dot];
        }
        if (!int.TryParse(secPart, NumberStyles.None, CultureInfo.InvariantCulture, out int second))
            return null;

        long ticks = 0;
        if (fraction.Length > 0)
        {
            // Bruchteil auf 7 Stellen (100-ns-Ticks) normieren
            string f = fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0');
            if (!long.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                return null;
        }

        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local).AddTicks(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
