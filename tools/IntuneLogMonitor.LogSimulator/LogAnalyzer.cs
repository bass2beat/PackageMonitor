using IntuneLogMonitor.Core;

namespace IntuneLogMonitor.LogSimulator;

/// <summary>Konsolenausgabe von <see cref="LogFileAnalyzer"/> (Befehl "analyze").</summary>
public static class LogAnalyzer
{
    public static int Run(string[] files)
    {
        var result = LogFileAnalyzer.Analyze(files);

        foreach (var (file, count) in result.Files)
            Console.WriteLine($"{Path.GetFileName(file)}: {count:N0} Einträge");
        Console.WriteLine($"Gesamt {result.Entries.Count:N0} Einträge, davon {result.Entries.Count(e => e.IsRaw):N0} nicht im CMTrace-Format");
        Console.WriteLine();
        Console.WriteLine($"{"#",3} {"Start",-19} {"App-ID",-8} {"App-Name",-50} {"Gesamt",10} {"Stand",10} {"Dauer",7}  Status");
        foreach (var d in result.Downloads)
        {
            string id = d.AppId is null ? "-" : d.AppId[..8];
            Console.WriteLine($"{d.Id,3} {d.StartTime:dd.MM.yyyy HH:mm:ss} {id,-8} {Trim(d.AppName ?? "-", 50),-50} " +
                              $"{(d.TotalBytes is { } t ? Formatting.FormatBytes(t) : "-"),10} {Formatting.FormatBytes(d.CurrentBytes),10} {Formatting.FormatDuration(d.Duration),7}  {d.StatusText}");
            foreach (var n in d.Notes)
                Console.WriteLine($"{"",4}↳ {n}");
        }
        return 0;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
