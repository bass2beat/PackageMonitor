# PackageMonitor

**Watch Intune app downloads live on a Windows client.** (An app coded with AI)

PackageMonitor follows what the Intune Management Extension (IME) is downloading right now: which app, how far along, how fast, and where the data comes from. All of this is in the IME log files, but hard to read there.

## Download

Get the latest `PackageMonitor.exe` from **[Releases](https://github.com/bass2beat/PackageMonitor/releases)**.

- One single file. Unzip it and start it, no installation needed (.NET is included).
- Runs on Windows 10 and Windows 11 (x64).

## What it does

### App downloads
PackageMonitor reads `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log` continuously and shows every app download in a table:

- start time, app name and app ID
- total size, bytes downloaded so far and a progress bar
- download rate, remaining time and duration
- status: running, completed, incomplete, no activity (no progress for 10 minutes), or cancelled/restarted

Several downloads running at the same time are tracked separately. When the IME starts a new log file (log rollover), monitoring continues without losing a download. Click a download to see only the log lines that belong to it.

### Log viewer
All log lines are shown in a fast, scrollable table with time, component, type and message. Warnings and errors are highlighted in colour.

### Delivery Optimization
A summary of `Get-DeliveryOptimizationStatus`, refreshed every 30 seconds:

- **Peers:** how many peers are currently connected, and over which connection type (LAN, group, internet, Connected Cache, HTTP/CDN).
- **Data:** how much was downloaded through Delivery Optimization, how much of it came from peers, and how much was uploaded to other peers.

### Cache sizes
Refreshed every 5 seconds, so you can watch them grow during a download:

| Cache | Folder |
|---|---|
| Delivery Optimization cache | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache` |
| IME download (incoming) | `C:\Program Files (x86)\Microsoft Intune Management Extension\Content\Incoming` |
| IME staging | `C:\Program Files (x86)\Microsoft Intune Management Extension\Content\Staging` |
| IME package cache | `C:\Windows\IMECache` |

The sizes show the space really used on disk. Delivery Optimization creates each download file at its final size right away and then fills it, so the file size alone would not change during a download. The final size is shown separately as "reserviert" (reserved).

## With or without admin rights

PackageMonitor can be started **with or without** administrator rights.

| | Normal start | Run as administrator |
|---|---|---|
| App downloads and log viewer | ✔ | ✔ |
| Delivery Optimization summary | ✔ | ✔ |
| Cache sizes: DO cache, IME incoming, IME staging | ✔ | ✔ |
| Cache size: `C:\Windows\IMECache` | – shows "Zugriff verweigert – als Administrator starten" (access denied) | ✔ |

To start it with admin rights: right-click `PackageMonitor.exe` → **Run as administrator**.

## Good to know

- The user interface is in German.
- PackageMonitor only reads. It does not change, delete or restart anything on the client.
- To view a log file copied from another device:

  ```bash
  PackageMonitor.exe --logfile "D:\Logs\AppWorkload.log"
  ```

## For developers

WPF application on .NET 10.

| Folder | Content |
|---|---|
| `src/IntuneLogMonitor.Core` | Log reader with rollover detection, CMTrace parser, download tracking, Delivery Optimization queries |
| `src/IntuneLogMonitor.App` | WPF user interface |
| `tests/IntuneLogMonitor.Core.Tests` | xUnit tests |
| `tools/IntuneLogMonitor.LogSimulator` | Creates sample logs and replays them live, including a log rollover |
| `samples/` | Synthetic sample logs |

Build, test and run:

```bash
dotnet build IntuneLogMonitor.sln
```

```bash
dotnet test tests/IntuneLogMonitor.Core.Tests
```

```bash
dotnet run --project src/IntuneLogMonitor.App
```

Live demo with a simulated download session (10× speed). Run the simulator in one terminal:

```bash
dotnet run --project tools/IntuneLogMonitor.LogSimulator -- replay C:\Temp\ImeDemo\AppWorkload.log --speed 10
```

and the app in a second one:

```bash
dotnet run --project src/IntuneLogMonitor.App -- --logfile C:\Temp\ImeDemo\AppWorkload.log
```

All settings (log path, search patterns, timeouts, cache folders) are defaults in `src/IntuneLogMonitor.Core/Options.cs`.

`RealDataTests` check the analysis against real IME logs in a local folder `Real-Data-as-Template`. Those logs contain device, user and tenant data and are therefore not part of this repository. Without the folder, these tests pass without checking anything.

## License

MIT – see [LICENSE](LICENSE).
