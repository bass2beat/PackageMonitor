# Package Monitor – Bereich C (AppWorkload-Downloads)

**Version 0.1** (05.11.2026)

WPF-Anwendung (.NET 10), die `AppWorkload.log` der Intune Management Extension live verfolgt und Downloads über eine Zustandsmaschine auswertet.

## Projekte

| Pfad | Inhalt |
|---|---|
| `src/IntuneLogMonitor.Core` | Tail-Reader mit Rollover-Erkennung, CMTrace-Parser, Download-Zustandsmaschine, Monitor-Pipeline |
| `src/IntuneLogMonitor.App` | WPF-Oberfläche (Tab „Bereich C“), `appsettings.json` |
| `tests/IntuneLogMonitor.Core.Tests` | xUnit-Tests (Parser, Zustandsmaschine, Tail/Rollover gegen echtes Dateisystem, End-to-End) |
| `tools/IntuneLogMonitor.LogSimulator` | Erzeugt die Beispiel-Logs und spielt das Szenario live ab (inkl. echtem Rollover) |
| `samples/` | Synthetische Beispiel-Logs: `AppWorkload-20261105-090221.log` (vor Rollover) + `AppWorkload.log` (aktiv) |

## Bauen, testen, starten

```bash
dotnet build IntuneLogMonitor.sln
```

```bash
dotnet test tests/IntuneLogMonitor.Core.Tests
```

```bash
dotnet run --project src/IntuneLogMonitor.App
```

Der Logpfad steht in `src/IntuneLogMonitor.App/appsettings.json` und lässt sich per `--logfile <Pfad>` überschreiben.

## Live-Demo mit Rollover

Terminal 1 – Szenario in ein Testverzeichnis schreiben (10-fach beschleunigt, Rollover nach ~14 s):

```bash
dotnet run --project tools/IntuneLogMonitor.LogSimulator -- replay C:\Temp\ImeDemo\AppWorkload.log --speed 10
```

Terminal 2 – Monitor auf dieselbe Datei richten:

```bash
dotnet run --project src/IntuneLogMonitor.App -- --logfile C:\Temp\ImeDemo\AppWorkload.log
```

Beispieldateien neu erzeugen: `dotnet run --project tools/IntuneLogMonitor.LogSimulator -- generate samples`

## Szenario der Beispiel-Logs

| # | App | Fall | Erwarteter Status |
|---|---|---|---|
| 1 | Contoso Agent 4.2 | Fortschritt mit App-ID | Abgeschlossen |
| 2 | Fabrikam Office Suite | parallel zu #1, Fortschritt ohne App-ID, Größe erst in 3. Folgezeile | Abgeschlossen |
| 3 | Northwind Toolkit | Rollover der Datei mitten im Download | Abgeschlossen |
| 4 | Adatum VPN Client | keine Gesamtgröße im Suchfenster | Unvollständig |
| 5 | Tailspin Reader | neuer Startmarker für dieselbe App-ID | Abgebrochen/Neu gestartet |
| 6 | Tailspin Reader | zweiter Versuch | Abgeschlossen |
| 7 | Litware Sync | bleibt bei 25 % stehen, > 10 min ohne Fortschritt | Keine Aktivität |

## Delivery Optimization & Cache-Größen

Oben in Bereich C (Standard: alle 30 s, Button „Aktualisieren“). Abgefragt wird über `powershell.exe`, weil das Modul DeliveryOptimization zu Windows PowerShell gehört:

- **Peers verbunden:** Summe `NumPeers` über alle Jobs aus `Get-DeliveryOptimizationStatus`, dazu die Verbindungen nach LAN, Gruppe, Internet und Link-Local sowie HTTP/CDN und Connected Cache.
- **Daten über DO:** heruntergeladen gesamt, davon von Peers (in %, aufgeteilt nach Peer-Art), von HTTP/CDN (davon Connected Cache) und an Peers hochgeladen. Eine Tabelle aller DO-Jobs lässt sich aufklappen.
- **DO-Dienst gesamt:** Werte aus `Get-DeliveryOptimizationPerfSnap`, darunter die Cache-Größe aus Sicht des DO-Dienstes.
- **Cache-Größen (gemessen auf dem Datenträger):** DO-Cache, IME `Content\Incoming`, IME `Content\Staging` und `C:\Windows\IMECache`. Die Ordner sind in `DeliveryOptimization:CacheFolders` konfigurierbar, Umgebungsvariablen sind erlaubt. `IMECache` lässt sich nur mit Administratorrechten lesen. Ohne diese Rechte zeigt die App „Zugriff verweigert – als Administrator starten“.

## Schaltbare Regeln der Zustandsmaschine (`Tracking`)

| Einstellung | Standard | Wirkung |
|---|---|---|
| `StrictTotalSizeDetection` | `true` | Nur `bytes 0/<Gesamt>` legt die Größe fest. `false` = jede Zeile mit Gesamt > 0 im Suchfenster zählt, solange die Größe nicht schon zu einem laufenden Download gehört. |
| `CheckInactivityByLogTime` | `true` | Prüft auf Inaktivität anhand der Log-Zeit jeder neuen Zeile. |
| `CheckInactivityBySystemClock` | `true` | Prüft zusätzlich periodisch anhand der Systemuhr. |
| `ResumeAfterNoActivity` | `true` | Neuer Fortschritt setzt einen Download mit „Keine Aktivität“ wieder auf „Läuft“. `false` = der Status bleibt endgültig. |
| `MarkStalledStartsIncomplete` | `true` | Ein Download ohne Gesamtgröße wird nach dem Timeout „Unvollständig“. `false` = er bleibt „Gestartet“, bis das Suchfenster abläuft. |

## Designentscheidungen

- **Rollover:** Das offene Lese-Handle folgt der umbenannten Datei. Rollover erkennt der Monitor über die File-ID (`GetFileInformationByHandle`). Die Erstellungszeit allein ist unzuverlässig, weil NTFS-„File System Tunneling“ sie bei schneller Neuanlage unter gleichem Namen vererbt. Ein FileSystemWatcher stößt die Prüfung sofort an, und die periodische Prüfung dient als Absicherung.
- **Threading:** Tailer → Channel → ein einziger Verarbeitungs-Task (Parser + Tracker) → gebündelte UI-Übernahme alle 200 ms. Parser und Tracker kommen dadurch ohne Locks aus, und die Zeilenreihenfolge bleibt über den Rollover hinweg erhalten.
- **Inaktivität:** Geprüft wird sowohl mit der Log-Zeit jeder Zeile (funktioniert auch beim Einlesen alter Logs) als auch periodisch mit der Uhrzeit. Kommt danach wieder Fortschritt, wechselt der Download zurück auf „Läuft“.
- **Phase 2** erwartet streng `bytes 0/<Gesamt>`. Eine Zeile mit einer App-ID, die einem *anderen* bekannten Download gehört, wird nicht fremd zugeordnet.
- **NuGet:** `nuget.config` im Repo-Stamm trägt nuget.org als Quelle ein, da auf dieser Maschine nur die VS-Offline-Quelle registriert war.
