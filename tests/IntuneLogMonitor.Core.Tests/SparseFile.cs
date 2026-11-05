using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IntuneLogMonitor.Core.Tests;

/// <summary>Testhilfe: Datei als Sparse markieren (FSCTL_SET_SPARSE), wie Delivery Optimization es tut.</summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    public static bool MarkSparse(FileStream fs) =>
        DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
