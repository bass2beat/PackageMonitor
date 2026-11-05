using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IntuneLogMonitor.Core;

/// <summary>
/// Identität einer Datei unabhängig von ihrem Namen. Unter Windows wird die File-ID
/// (Volume-Seriennummer + Dateiindex) verwendet. Die Erstellungszeit allein reicht nicht:
/// Durch NTFS-"File System Tunneling" erbt eine Datei, die innerhalb von 15 s nach dem
/// Umbenennen unter dem alten Namen neu angelegt wird, die Erstellungszeit der alten Datei.
/// Ohne File-ID (andere Plattformen) wird auf die Erstellungszeit zurückgegriffen.
/// </summary>
public readonly record struct FileIdentity(uint? VolumeSerial, ulong? FileIndex, DateTime CreationTimeUtc)
{
    public bool IsSameFileAs(FileIdentity other)
    {
        if (FileIndex.HasValue && other.FileIndex.HasValue)
            return FileIndex == other.FileIndex && VolumeSerial == other.VolumeSerial;
        return CreationTimeUtc == other.CreationTimeUtc;
    }

    public static FileIdentity FromHandle(SafeFileHandle handle)
    {
        DateTime created;
        try { created = File.GetCreationTimeUtc(handle); }
        catch (IOException) { created = DateTime.MinValue; }

        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(handle, out var info))
        {
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new FileIdentity(info.VolumeSerialNumber, index, created);
        }
        return new FileIdentity(null, null, created);
    }

    /// <summary>Ermittelt die Identität der Datei, die aktuell unter <paramref name="path"/> liegt; null, falls keine existiert.</summary>
    public static FileIdentity? FromPath(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return FromHandle(handle);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }          // z. B. gerade in Umbenennung
        catch (UnauthorizedAccessException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
