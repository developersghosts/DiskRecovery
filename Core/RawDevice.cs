using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskRecovery.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskRecovery.Core;

/// <summary>جهاز تخزين يُقرأ بالقطاعات (هارد فعلي أو ملف صورة).</summary>
public interface IBlockDevice : IDisposable
{
    int SectorSize { get; }
    /// <summary>يقرأ من إزاحة وطول مضاعفين لحجم القطاع.</summary>
    int ReadAligned(long offset, byte[] buffer, int count);
}

/// <summary>قراءة مباشرة من \\.\PhysicalDriveN (قراءة فقط، لا يكتب أبداً على القرص).</summary>
public sealed class PhysicalDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();
    public int SectorSize { get; }
    public int DiskNumber { get; }

    public PhysicalDiskDevice(int diskNumber, int sectorSize)
    {
        DiskNumber = diskNumber;
        SectorSize = sectorSize > 0 ? sectorSize : 512;
        _handle = NativeMethods.OpenForRead($@"\\.\PhysicalDrive{diskNumber}");
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"تعذّر فتح القرص {diskNumber}. شغّل البرنامج كمسؤول.");
    }

    public int ReadAligned(long offset, byte[] buffer, int count)
    {
        lock (_lock)
        {
            if (!NativeMethods.SetFilePointerEx(_handle, offset, out _, NativeMethods.FILE_BEGIN))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.ReadFile(_handle, buffer, count, out int read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return read;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>ملف صورة قرص (يُستخدم للاختبار أو لفحص نسخة .img).</summary>
public sealed class ImageFileDevice : IBlockDevice
{
    private readonly FileStream _fs;
    private readonly object _lock = new();
    public int SectorSize => 512;

    public ImageFileDevice(string path) =>
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);

    public int ReadAligned(long offset, byte[] buffer, int count)
    {
        lock (_lock)
        {
            _fs.Position = offset;
            int total = 0;
            while (total < count)
            {
                int n = _fs.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>
/// يقرأ أي عدد من البايتات من أي إزاحة داخل بارتشن،
/// ويتكفّل بمحاذاة القراءة على حدود القطاعات كما يشترط ويندوز للأقراص الخام.
/// </summary>
public sealed class PartitionReader
{
    private const int MaxChunk = 4 * 1024 * 1024;
    private readonly IBlockDevice _dev;
    private readonly long _base;
    private readonly long _length;
    private byte[] _scratch = Array.Empty<byte>();

    public PartitionReader(IBlockDevice device, long partitionOffset, long partitionLength)
    {
        _dev = device;
        _base = partitionOffset;
        _length = partitionLength;
    }

    public long Length => _length;

    public byte[] Read(long offset, int count)
    {
        var b = new byte[count];
        Read(offset, b, 0, count);
        return b;
    }

    public void Read(long offset, byte[] dst, int dstOffset, int count)
    {
        int ss = _dev.SectorSize;
        while (count > 0)
        {
            long abs = _base + offset;
            long alignedStart = abs / ss * ss;
            int lead = (int)(abs - alignedStart);
            int want = Math.Min(count, MaxChunk - ss);
            int alignedLen = (lead + want + ss - 1) / ss * ss;
            if (_scratch.Length < alignedLen) _scratch = new byte[Math.Max(alignedLen, 64 * 1024)];

            int got = _dev.ReadAligned(alignedStart, _scratch, alignedLen);
            int usable = Math.Min(want, got - lead);
            if (usable <= 0)
            {
                // نهاية القرص أو قطاع غير مقروء: نملأ بأصفار
                Array.Clear(dst, dstOffset, count);
                return;
            }
            Buffer.BlockCopy(_scratch, lead, dst, dstOffset, usable);
            offset += usable; dstOffset += usable; count -= usable;
        }
    }
}
