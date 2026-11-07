using DiskRecovery.Core.Fat;
using DiskRecovery.Core.Ntfs;

namespace DiskRecovery.Core;

/// <summary>يختار الفاحص المناسب حسب نظام ملفات البارتشن.</summary>
public static class QuickScan
{
    public static string DetectFileSystem(PartitionReader reader)
    {
        try { return DiskEnumerator.DetectFromBootSector(reader.Read(0, 512)); }
        catch { return ""; }
    }

    public static bool IsSupported(string fs) => fs is "NTFS" or "FAT32" or "FAT" or "exFAT";

    public static ScanResult Scan(PartitionReader reader, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string fs = DetectFileSystem(reader);
        return fs switch
        {
            "NTFS" => NtfsScanner.Scan(reader, progress, ct),
            "FAT32" or "FAT" => FatScanner.Scan(reader, progress, ct),
            "exFAT" => ExFatScanner.Scan(reader, progress, ct),
            "BitLocker" => throw new InvalidDataException("البارتشن مشفّر بـ BitLocker ولا يمكن قراءته مباشرة."),
            _ => throw new InvalidDataException("نظام الملفات غير معروف — استخدم «البحث العميق» للبحث عن الملفات بالتوقيع."),
        };
    }
}
