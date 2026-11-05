using System.Globalization;

namespace IntuneLogMonitor.LogSimulator;

public sealed record SimLine(TimeSpan Offset, string Component, int Type, int Thread, string Message);

/// <summary>
/// Synthetisches AppWorkload.log-Szenario im echten IME-Zeilenformat (siehe Real-Data-as-Template):
///   [StatusService] Downloading app (id = &lt;GUID&gt;, name &lt;Name&gt;) via DO, bytes &lt;aktuell&gt;/&lt;gesamt&gt; for user &lt;GUID&gt;
/// gefolgt von drei [StatusService]-Begleitzeilen je Fortschrittsmeldung.
///
///  D1 Contoso Agent        – 50 MB, abgeschlossen
///  D2 Fabrikam Office      – 1,2 GB, parallel zu D1 (Größenzeile erst nach vielen Zwischenzeilen)
///  D3 Northwind Toolkit    – 700 MB, Rollover der Datei während des Downloads, danach abgeschlossen
///  D4 Adatum VPN Client    – Start ohne Gesamtgröße → Unvollständig
///  D5 Contoso Script Pkg   – kleines Paket: nach "0/0" direkt "N/N" (keine Zeile "0/N") → Abgeschlossen
///  D6 Tailspin Reader      – nach 40 % neuer Startmarker für dieselbe App-ID → Abgebrochen/Neu gestartet
///  D7 Tailspin Reader      – zweiter Versuch, abgeschlossen
///  D8 Litware Sync         – bleibt bei 25 % stehen, > 10 min keine Fortschrittszeile → Keine Aktivität
/// </summary>
public static class SampleScenario
{
    public const string UserId = "5a1e0c7d-0000-4000-8000-00000000d0e5";
    public const string AppD1 = "3f1c6b0e-8a5d-4c2e-9b71-2d4e6a8c0f11";
    public const string AppD2 = "9b2e4d71-0c3a-4f5e-8d62-7a1b3c5e9f20";
    public const string AppD3 = "c47a1e92-5b6d-4e8f-a031-6f2d8b4c1e33";
    public const string AppD4 = "e8d3b5a1-2f4c-4a6e-9b70-1c3e5a7d9f44";
    public const string AppSmall = "a3e98c96-3774-4649-9e5e-232b6d40bd51";
    public const string AppD6 = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c55";
    public const string AppD8 = "7f6e5d4c-3b2a-4c1d-8e0f-9a8b7c6d5e66";

    public const long SizeD1 = 52_428_800;       // 50 MiB
    public const long SizeD2 = 1_288_490_189;    // ~1,2 GiB
    public const long SizeD3 = 734_003_200;      // 700 MiB
    public const long SizeSmall = 1_568;         // wie im echten Log
    public const long SizeD6 = 157_286_400;      // 150 MiB
    public const long SizeD8 = 314_572_800;      // 300 MiB

    /// <summary>Zeilen mit Offset &lt; diesem Wert landen in der alten (umbenannten) Datei.</summary>
    public static readonly TimeSpan RolloverAt = TimeSpan.FromSeconds(141);

    public static List<SimLine> Build()
    {
        var lines = new List<SimLine>();
        void Add(double sec, string msg, int type = 1, int thread = 14) =>
            lines.Add(new SimLine(TimeSpan.FromSeconds(sec), "AppWorkload", type, thread, msg));

        // Fortschrittsmeldung wie die IME: Statuszeile + drei Begleitzeilen mit gleichem Zeitstempel
        void Progress(double sec, string appId, string name, long cur, long total, int thread)
        {
            Add(sec, $"[StatusService] Downloading app (id = {appId}, name {name}) via DO, bytes {cur}/{total} for user {UserId}", thread: thread);
            Add(sec, "[StatusService] Sending download progress update from StatusServicePublisher.", thread: thread);
            Add(sec, "[StatusService] Received DownloadReport to send via Callback.", thread: thread);
            Add(sec, $"[StatusService] Sending a DownloadReport to user {UserId} for app {appId} via callback.", thread: thread);
        }

        void Done(double sec, string appId, long size, int thread)
        {
            Add(sec, "[Win32App] Starts verifying encrypted hash", thread: thread);
            Add(sec, "[Win32App] hmac validation is pass.", thread: thread);
            Add(sec, "[Win32App DO] DO download and decryption is successfully done", thread: thread);
            Add(sec, string.Create(CultureInfo.InvariantCulture, $"[Win32App] Downloaded file size {size:N2}"), thread: thread);
            Add(sec + 0.05, $"[Win32App][ReportingManager] Download state for app with id: {appId} has been updated. Report delta: {{\"EnforcementState\":{{\"OldValue\":\"InProgress\",\"NewValue\":\"InProgressDownloadCompleted\"}}}}", thread: thread);
            Add(sec + 0.05, "[Win32App][DownloadActionHandler] Handler completed with action status: Success.", thread: thread);
        }

        // --- Einleitung ---
        Add(0, "[Win32App][V3Processor] Processing 3 subgraphs.");
        Add(0.5, "[Win32App] Get policies = [{\"Id\":\"" + AppD1 + "\",\"Name\":\"Contoso Agent 4.2\",\"Version\":1},\r\n"
                 + " {\"Id\":\"" + AppD2 + "\",\"Name\":\"Fabrikam Office Suite\",\"Version\":3},\r\n"
                 + " {\"Id\":\"" + AppD3 + "\",\"Name\":\"Northwind Toolkit (x64)\",\"Version\":2}]");
        Add(1, "[Win32App][DownloadActionHandler] Handler invoked for 3 app downloads.");

        // --- D1: 50 MiB ---
        const string n1 = "Contoso Agent 4.2";
        Progress(5, AppD1, n1, 0, 0, 21);
        Progress(10, AppD1, n1, 0, SizeD1, 21);
        for (int i = 1; i <= 10; i++)
            Progress(10 + i * 5, AppD1, n1, SizeD1 * i / 10, SizeD1, 21);
        Done(60.1, AppD1, SizeD1, 21);

        // --- D2: 1,2 GiB, parallel; D1-Zeilen schieben sich zwischen Start und Größenzeile ---
        const string n2 = "Fabrikam Office Suite";
        Progress(8, AppD2, n2, 0, 0, 33);
        Add(8.2, "[Win32App DO] DO Job set priority is DODownloadPriorityForeground", thread: 33);
        Progress(13, AppD2, n2, 0, SizeD2, 33);
        const int d2Steps = 19;
        for (int i = 1; i <= d2Steps; i++)
        {
            Progress(13 + i * 4, AppD2, n2, SizeD2 * i / d2Steps, SizeD2, 33);
            if (i == 8)
                Add(13 + i * 4 + 1, "[Win32App DO] DO Job transient network error 0x80D02002, DO will retry automatically", type: 2, thread: 33);
        }
        Done(13 + d2Steps * 4 + 0.1, AppD2, SizeD2, 33);

        // --- D3: 700 MiB, Rollover während des Downloads (bei t = 141 s); Name mit Klammern ---
        const string n3 = "Northwind Toolkit (x64)";
        Progress(100, AppD3, n3, 0, 0, 40);
        Progress(105, AppD3, n3, 0, SizeD3, 40);
        const int d3Steps = 20;
        for (int i = 1; i <= d3Steps; i++)
            Progress(105 + i * 5, AppD3, n3, SizeD3 * i / d3Steps, SizeD3, 40);
        Done(205.1, AppD3, SizeD3, 40);

        // --- D4: Start ohne Gesamtgröße → Unvollständig ---
        Progress(210, AppD4, "Adatum VPN Client", 0, 0, 52);
        Add(211, "[Win32App DO] Waiting 43200 seconds for DO Job to complete", thread: 52);
        Add(212, "[Win32App DO] DO Job state polling ...", thread: 52);
        Add(225, $"[Win32App] Failed to download content for app {AppD4}. DO job error 0x80D03805 (no peers and HTTP source unreachable)", type: 3, thread: 52);

        // --- D5: kleines Paket – nach 0/0 direkt N/N (so im echten Log) ---
        const string n5 = "Contoso Script Package";
        Progress(230, AppSmall, n5, 0, 0, 74);
        Progress(235, AppSmall, n5, SizeSmall, SizeSmall, 74);
        Done(235.1, AppSmall, SizeSmall, 74);

        // --- D6/D7: Neustart für dieselbe App-ID ---
        const string n6 = "Tailspin Reader";
        Progress(240, AppD6, n6, 0, 0, 61);
        Progress(245, AppD6, n6, 0, SizeD6, 61);
        for (int i = 1; i <= 4; i++)
            Progress(245 + i * 5, AppD6, n6, SizeD6 * i / 10, SizeD6, 61);
        Add(290, "[Win32App DO] DO Job stalled, cancelling job and starting a new attempt", type: 2, thread: 61);
        Progress(300, AppD6, n6, 0, 0, 62);
        Progress(305, AppD6, n6, 0, SizeD6, 62);
        for (int i = 1; i <= 10; i++)
            Progress(305 + i * 3, AppD6, n6, SizeD6 * i / 10, SizeD6, 62);
        Done(335.1, AppD6, SizeD6, 62);

        // --- D8: bleibt stehen → Keine Aktivität ---
        const string n8 = "Litware Sync";
        Progress(360, AppD8, n8, 0, 0, 70);
        Progress(365, AppD8, n8, 0, SizeD8, 70);
        for (int i = 1; i <= 5; i++)
            Progress(365 + i * 5, AppD8, n8, SizeD8 * i / 20, SizeD8, 70);
        for (int m = 1; m <= 15; m++)
            Add(390 + m * 60, "[Win32App][V3Processor] Waiting for DO job, no state change", thread: 14);
        Add(1300, "[Win32App][V3Processor] Done processing 3 subgraphs.");

        return lines.OrderBy(l => l.Offset).ToList();
    }

    public static string Format(SimLine line, DateTime timestamp) =>
        string.Create(CultureInfo.InvariantCulture,
            $"<![LOG[{line.Message}]LOG]!><time=\"{timestamp:HH:mm:ss.fffffff}\" date=\"{timestamp.Month}-{timestamp.Day}-{timestamp.Year}\" component=\"{line.Component}\" context=\"\" type=\"{line.Type}\" thread=\"{line.Thread}\" file=\"\">\r\n");
}
