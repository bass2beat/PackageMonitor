using System.Text;

namespace IntuneLogMonitor.Core;

/// <summary>Information über einen erkannten Dateiwechsel (Rollover oder Kürzung).</summary>
/// <param name="OldFileBytesRead">Bis zum Wechsel insgesamt aus der alten Datei gelesene Bytes (= deren Länge bei Rollover).</param>
public sealed record FileSwitchInfo(string Path, string? RolledOverTo, long OldFileBytesRead, bool WasTruncation);

/// <summary>
/// Liest eine Logdatei wie "tail -f", ohne sie zu sperren (FileShare.ReadWrite | FileShare.Delete).
///
/// Rollover-Erkennung: Die IME benennt die aktive Datei um und legt sie unter dem ursprünglichen
/// Namen neu an. Unser offenes Handle folgt dabei der umbenannten Datei. Ein FileSystemWatcher
/// (Renamed/Created/Deleted/Changed) löst sofort eine Prüfung aus; zusätzlich wird periodisch
/// geprüft, ob unter dem Pfad noch dieselbe Datei liegt (File-ID bzw. Erstellungszeit) und ob die
/// Datei kürzer als die Leseposition ist (Kürzung). Bei einem Wechsel wird zuerst der Rest der
/// alten Datei über das noch offene Handle gelesen, danach geht es in der neuen Datei ab Position 0 weiter.
///
/// Alle Ereignisse werden auf dem Lesethread ausgelöst, und zwar in Dateireihenfolge.
/// </summary>
public sealed class LogFileTailer : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly bool _readExisting;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly byte[] _buffer = new byte[64 * 1024];

    private FileSystemWatcher? _watcher;
    private FileStream? _stream;
    private FileIdentity _identity;
    private long _position;
    private Decoder? _decoder;
    private bool _firstOpen = true;
    private volatile string? _lastRenameTarget;
    private string? _lastStatus;

    public LogFileTailer(string path, TimeSpan pollInterval, bool readExistingContent = true)
    {
        _path = Path.GetFullPath(path);
        _pollInterval = pollInterval;
        _readExisting = readExistingContent;
    }

    public string FilePath => _path;

    /// <summary>Neu gelesener, dekodierter Text.</summary>
    public event Action<string>? TextRead;

    /// <summary>Dateiwechsel: der Rest der alten Datei wurde bereits über <see cref="TextRead"/> geliefert.</summary>
    public event Action<FileSwitchInfo>? FileSwitched;

    public event Action<string>? StatusChanged;

    /// <summary>Aktuelle Leseposition in Bytes (für Diagnose/Tests).</summary>
    public long Position => _position;

    public bool IsOpen => _stream is not null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            EnsureWatcher();
            try
            {
                Poll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportStatus($"Lesefehler: {ex.Message}");
                CloseStream();
            }

            try
            {
                await _signal.WaitAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Ein Prüf- und Leseschritt. Wird von <see cref="RunAsync"/> aufgerufen; Tests rufen ihn direkt auf.
    /// </summary>
    public void Poll()
    {
        if (_stream is null)
        {
            if (!TryOpen(isSwitch: false))
                return;
        }

        ReadAvailable(final: false);

        var current = FileIdentity.FromPath(_path);
        if (current is null)
        {
            // Datei wurde umbenannt, neue noch nicht angelegt: weiter aus dem alten Handle lesen.
            return;
        }

        if (!current.Value.IsSameFileAs(_identity))
        {
            SwitchToNewFile(truncation: false);
            return;
        }

        long length = _stream!.Length;
        if (length < _position)
        {
            // Gleiche Datei, aber kürzer als unsere Position: wurde gekürzt/überschrieben.
            SwitchToNewFile(truncation: true);
        }
    }

    private void SwitchToNewFile(bool truncation)
    {
        if (!truncation)
        {
            // Erst den verbleibenden Rest der alten Datei über das noch offene Handle lesen.
            ReadAvailable(final: true);
        }
        long oldFileBytes = _position;
        FlushDecoder();
        string? renamedTo = _lastRenameTarget;
        _lastRenameTarget = null;
        CloseStream();

        FileSwitched?.Invoke(new FileSwitchInfo(_path, truncation ? null : renamedTo, oldFileBytes, truncation));
        ReportStatus(truncation
            ? "File was truncated – reading from the beginning."
            : $"Rollover detected{(renamedTo is null ? "" : $" (old file: {Path.GetFileName(renamedTo)})")} – reading the new file from position 0.");

        if (TryOpen(isSwitch: true))
            ReadAvailable(final: false);
    }

    private bool TryOpen(bool isSwitch)
    {
        try
        {
            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            _stream = stream;
            _identity = FileIdentity.FromHandle(stream.SafeFileHandle);
            _decoder = null;
            _position = 0;

            if (_firstOpen && !isSwitch && !_readExisting)
            {
                // Nur neu angehängte Inhalte verarbeiten. Ab Dateiende beginnen (Kodierung trotzdem erkennen).
                DetectEncoding(final: true);
                _position = Math.Max(_position, stream.Length);
            }
            _firstOpen = false;
            ReportStatus($"Monitoring {_path}");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            ReportStatus($"File does not exist – waiting for {_path}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportStatus($"File cannot be opened: {ex.Message}");
            return false;
        }
    }

    /// <summary>Liest alles von der aktuellen Position bis zum Dateiende.</summary>
    private void ReadAvailable(bool final)
    {
        var stream = _stream;
        if (stream is null)
            return;

        if (_decoder is null && !DetectEncoding(final))
            return;

        var sb = new StringBuilder();
        while (true)
        {
            stream.Seek(_position, SeekOrigin.Begin);
            int n = stream.Read(_buffer, 0, _buffer.Length);
            if (n <= 0)
                break;
            _position += n;
            var chars = new char[_decoder!.GetCharCount(_buffer, 0, n, flush: false)];
            int c = _decoder.GetChars(_buffer, 0, n, chars, 0, flush: false);
            sb.Append(chars, 0, c);

            // Große Bestände in Stücken weiterreichen, damit Parser und UI früh Daten sehen.
            if (sb.Length >= 512 * 1024)
            {
                TextRead?.Invoke(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            TextRead?.Invoke(sb.ToString());
    }

    /// <summary>Erkennt die Kodierung anhand des BOM (UTF-8, UTF-16 LE/BE); Standard ist UTF-8.</summary>
    private bool DetectEncoding(bool final)
    {
        var stream = _stream!;
        Span<byte> head = stackalloc byte[3];
        stream.Seek(0, SeekOrigin.Begin);
        int n = stream.Read(head);
        head = head[..n];

        // Nur warten, wenn die vorhandenen Bytes der Anfang eines BOM sein könnten.
        bool couldBeBomPrefix = n == 0
            || (n < 3 && head.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }.AsSpan(0, n)))
            || (n == 1 && head[0] is 0xFF or 0xFE);
        if (couldBeBomPrefix && !final)
            return false;

        Encoding encoding;
        int bom;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { encoding = new UTF8Encoding(false); bom = 3; }
        else if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) { encoding = Encoding.Unicode; bom = 2; }
        else if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; bom = 2; }
        else { encoding = new UTF8Encoding(false); bom = 0; }

        _decoder = encoding.GetDecoder();
        if (_position < bom)
            _position = bom;
        return true;
    }

    private void FlushDecoder()
    {
        if (_decoder is null)
            return;
        var chars = new char[16];
        int c = _decoder.GetChars([], 0, 0, chars, 0, flush: true);
        if (c > 0)
            TextRead?.Invoke(new string(chars, 0, c));
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
        _decoder = null;
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null)
            return;
        string? dir = Path.GetDirectoryName(_path);
        if (dir is null || !Directory.Exists(dir))
            return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            w.Renamed += (_, e) =>
            {
                if (PathEquals(e.OldFullPath, _path))
                    _lastRenameTarget = e.FullPath;
                if (PathEquals(e.OldFullPath, _path) || PathEquals(e.FullPath, _path))
                    Signal();
            };
            w.Created += (_, e) => { if (PathEquals(e.FullPath, _path)) Signal(); };
            w.Deleted += (_, e) => { if (PathEquals(e.FullPath, _path)) Signal(); };
            w.Changed += (_, e) => { if (PathEquals(e.FullPath, _path)) Signal(); };
            w.Error += (_, e) => { ReportStatus($"FileSystemWatcher error: {e.GetException().Message} – periodic check continues."); Signal(); };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ReportStatus($"FileSystemWatcher not available ({ex.Message}) – periodic check only.");
        }
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* bereits signalisiert */ }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);

    private void ReportStatus(string status)
    {
        if (status == _lastStatus)
            return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        CloseStream();
        _signal.Dispose();
    }
}
