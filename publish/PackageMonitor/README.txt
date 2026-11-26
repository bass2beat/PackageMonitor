PackageMonitor by DPS
=====================

Start:  Double-click PackageMonitor.exe.
        A single file, nothing to install (.NET is included).
        Requires Windows 10/11 x64.

Update from an older version:
  Delete the old folder and use only the new PackageMonitor.exe.
  An appsettings.json is no longer needed and is ignored.

Administrator rights:
  PackageMonitor can be started with or without administrator rights.
  Without them everything works except the size of C:\Windows\IMECache
  (shown as "Access denied – run as administrator" or as
  "subfolder(s) not readable, value incomplete").
  For that value: right-click the exe -> "Run as administrator".

Cache sizes:
  Measured every 5 seconds; they show the bytes really used on disk.
  Delivery Optimization creates downloads as sparse files at their final size;
  "reserved" is that final size.
  "IME staging" is only filled briefly while decrypting/unpacking.

Settings:
  All paths, patterns and times are built into the app:
    Log:     C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log
    Caches:  DO cache, IME Content\Incoming, IME Content\Staging, C:\Windows\IMECache

Monitor a different log file (e.g. a copied file):
  PackageMonitor.exe --logfile "D:\Logs\AppWorkload.log"

Source code and downloads: https://github.com/bass2beat/PackageMonitor
