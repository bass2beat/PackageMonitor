using System.Runtime.InteropServices;

namespace IntuneLogMonitor.Core;

/// <summary>
/// Misst Ordnergrößen rekursiv, ohne an einzelnen unlesbaren Unterordnern abzubrechen.
/// Sparse- und komprimierte Dateien werden mit ihrer tatsächlich belegten Größe gezählt
/// (Delivery Optimization legt Downloads als Sparse-Dateien in Endgröße an).
/// </summary>
public static class FolderSizeCalculator
{
    public static FolderSize Measure(string name, string path, CancellationToken ct = default)
    {
        string full = Environment.ExpandEnvironmentVariables(path);
        try
        {
            if (!Directory.Exists(full))
                return new FolderSize(name, full, false, 0, 0, 0, 0, false, 0, null);

            long bytes = 0, logical = 0;
            int files = 0, sparse = 0, inaccessible = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(full));
            bool root = true;
            var opts = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = false };

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var info in dir.EnumerateFileSystemInfos("*", opts))
                    {
                        if (info is FileInfo f)
                        {
                            files++;
                            logical += f.Length;
                            if ((f.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
                            {
                                sparse++;
                                bytes += AllocatedSize(f);
                            }
                            else
                            {
                                bytes += f.Length;
                            }
                        }
                        else if (info is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            stack.Push(d);
                        }
                    }
                }
                catch (UnauthorizedAccessException) when (root)
                {
                    return FolderSize.Denied(name, full);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                }
                root = false;
            }
            return new FolderSize(name, full, true, bytes, logical, files, sparse, false, inaccessible, null);
        }
        catch (UnauthorizedAccessException)
        {
            return FolderSize.Denied(name, full);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return new FolderSize(name, full, false, 0, 0, 0, 0, false, 0, ex.Message);
        }
    }

    /// <summary>Tatsächlich belegte Bytes einer Sparse-/komprimierten Datei; bei Fehler die Dateilänge.</summary>
    internal static long AllocatedSize(FileInfo file)
    {
        if (!OperatingSystem.IsWindows())
            return file.Length;
        uint low = GetCompressedFileSizeW(file.FullName, out uint high);
        if (low == uint.MaxValue)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 0)
                return file.Length; // z. B. Datei gerade gelöscht oder gesperrt
        }
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
