using System.Globalization;
using System.Text;
using IntuneLogMonitor.LogSimulator;

// Verwendung:
//   generate <Zielverzeichnis>                 statische Beispieldateien (alte + aktive Datei) erzeugen
//   replay <Pfad\AppWorkload.log> [--speed N]  Szenario live schreiben, inkl. echtem Rollover (Umbenennen + Neuanlage)
//   analyze <Datei1> [<Datei2> ...]            Logdateien (älteste zuerst) durch Parser und Zustandsmaschine schicken

if (args.Length < 2 || args[0] is not ("generate" or "replay" or "analyze"))
{
    Console.WriteLine("Verwendung:");
    Console.WriteLine("  generate <Zielverzeichnis>");
    Console.WriteLine("  replay <Pfad\\AppWorkload.log> [--speed N]   (Standard: N = 10, d. h. 10-fach beschleunigt)");
    Console.WriteLine("  analyze <AppWorkload-alt.log> <AppWorkload.log> ...   (älteste Datei zuerst)");
    return 1;
}

if (args[0] == "analyze")
    return LogAnalyzer.Run(args[1..]);

var lines = SampleScenario.Build();
var utf8 = new UTF8Encoding(false);

if (args[0] == "generate")
{
    string dir = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(dir);
    var baseTime = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Local);
    var rolloverTime = baseTime + SampleScenario.RolloverAt;
    string oldName = $"AppWorkload-{rolloverTime:yyyyMMdd-HHmmss}.log";

    var oldFile = new StringBuilder();
    var newFile = new StringBuilder();
    foreach (var line in lines)
        (line.Offset < SampleScenario.RolloverAt ? oldFile : newFile).Append(SampleScenario.Format(line, baseTime + line.Offset));

    File.WriteAllText(Path.Combine(dir, oldName), oldFile.ToString(), utf8);
    File.WriteAllText(Path.Combine(dir, "AppWorkload.log"), newFile.ToString(), utf8);
    Console.WriteLine($"Erzeugt: {Path.Combine(dir, oldName)} (vor dem Rollover)");
    Console.WriteLine($"Erzeugt: {Path.Combine(dir, "AppWorkload.log")} (aktive Datei nach dem Rollover)");
    return 0;
}

// replay
string path = Path.GetFullPath(args[1]);
double speed = 10;
int speedIdx = Array.IndexOf(args, "--speed");
if (speedIdx >= 0 && speedIdx + 1 < args.Length)
    speed = double.Parse(args[speedIdx + 1], CultureInfo.InvariantCulture);

Directory.CreateDirectory(Path.GetDirectoryName(path)!);
// Die Log-Zeitstempel laufen mit Szenario-Zeit (nicht beschleunigt), damit Raten und
// das 10-Minuten-Inaktivitätsfenster realistisch bleiben.
var start = DateTime.Now;
bool rolled = false;
var clock = System.Diagnostics.Stopwatch.StartNew();

Console.WriteLine($"Schreibe nach {path} (Faktor {speed}) – Strg+C bricht ab.");
foreach (var line in lines)
{
    var due = TimeSpan.FromTicks((long)(line.Offset.Ticks / speed));
    var wait = due - clock.Elapsed;
    if (wait > TimeSpan.Zero)
        Thread.Sleep(wait);

    if (!rolled && line.Offset >= SampleScenario.RolloverAt)
    {
        string rolledPath = Path.Combine(Path.GetDirectoryName(path)!,
            $"AppWorkload-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Move(path, rolledPath);
        Console.WriteLine($"Rollover: {Path.GetFileName(path)} → {Path.GetFileName(rolledPath)}");
        rolled = true;
    }

    // Wie die IME: Datei pro Schreibvorgang öffnen, anhängen, schließen – mit FileShare.ReadWrite | Delete.
    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
    {
        var bytes = utf8.GetBytes(SampleScenario.Format(line, start + line.Offset));
        fs.Write(bytes);
    }
    Console.WriteLine($"{line.Offset:mm\\:ss} {Shorten(line.Message)}");
}
Console.WriteLine("Fertig.");
return 0;

static string Shorten(string s)
{
    s = s.ReplaceLineEndings(" ");
    return s.Length <= 110 ? s : s[..107] + "...";
}
