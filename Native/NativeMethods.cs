using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskRecovery.Native;

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_BEGIN = 0;

    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    public static SafeFileHandle OpenForRead(string path) =>
        CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>فتح القرص بصلاحية قراءة/كتابة — مطلوب لبعض أوامر الاستعلام (S.M.A.R.T.) فقط، لا يُكتب به أي شيء.</summary>
    public static SafeFileHandle OpenForQuery(string path) =>
        CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>ينفّذ IOCTL ويكبّر المخزن تلقائياً لو كان صغيراً.</summary>
    public static byte[]? IoctlQuery(SafeFileHandle h, uint code, int initialSize = 4096, byte[]? input = null)
    {
        int size = initialSize;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var buf = new byte[size];
            if (DeviceIoControl(h, code, input, input?.Length ?? 0, buf, buf.Length, out int ret, IntPtr.Zero))
            {
                if (ret < buf.Length) Array.Resize(ref buf, ret);
                return buf;
            }
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_INSUFFICIENT_BUFFER && err != ERROR_MORE_DATA) return null;
            size *= 2;
        }
        return null;
    }
}
