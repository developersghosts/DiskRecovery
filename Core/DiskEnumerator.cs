using System.Buffers.Binary;
using System.Text;
using DiskRecovery.Native;

namespace DiskRecovery.Core;

public sealed class PartitionInfo
{
    public int DiskNumber { get; init; }
    public string DiskName { get; init; } = "";
    public long DiskSize { get; init; }
    public int SectorSize { get; init; } = 512;
    public string Style { get; init; } = "";        // GPT / MBR
    public int PartitionNumber { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public string TypeName { get; init; } = "";
    public string GptName { get; init; } = "";
    public string? DriveLetter { get; set; }       // مثل "C:"  أو null لو مخفي
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "غير معروف";

    /// <summary>لو البارتشن داخل ملف نسخة قرص بدلاً من هارد فعلي.</summary>
    public string? ImagePath { get; init; }
    /// <summary>بارتشن مفقود تم العثور عليه بالبحث (غير موجود في جدول البارتشن).</summary>
    public bool IsLost { get; init; }

    public bool IsImage => ImagePath != null;
    public bool IsHidden => string.IsNullOrEmpty(DriveLetter);
    public bool IsNtfs => FileSystem == "NTFS";
    /// <summary>هل يمكن إصلاحه أو عمل فورمات له (هارد فعلي وموجود في جدول البارتشن)؟</summary>
    public bool CanModify => !IsImage && !IsLost;

    public string StateText => IsLost ? "مفقود" : IsImage ? "ملف نسخة" : IsHidden ? "مخفي" : "ظاهر";

    /// <summary>يفتح الجهاز المناسب للقراءة (الهارد أو ملف النسخة).</summary>
    public IBlockDevice OpenDevice() =>
        ImagePath != null ? new ImageFileDevice(ImagePath) : new PhysicalDiskDevice(DiskNumber, SectorSize);

    public string DisplayName =>
        !string.IsNullOrEmpty(Label) ? Label :
        !string.IsNullOrEmpty(GptName) ? GptName : TypeName;
}

public static class DiskEnumerator
{
    private static readonly Dictionary<Guid, string> GptTypes = new()
    {
        [new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")] = "EFI System",
        [new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae")] = "Microsoft Reserved",
        [new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")] = "Basic Data",
        [new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac")] = "Windows Recovery",
        [new Guid("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3")] = "LDM Metadata",
        [new Guid("af9b60a0-1431-4f62-bc68-3311714a69ad")] = "LDM Data",
        [new Guid("e75caf8f-f680-4cee-afa3-b001e56efc2d")] = "Storage Spaces",
        [new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4")] = "Linux",
        [new Guid("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f")] = "Linux Swap",
    };

    private static readonly Dictionary<byte, string> MbrTypes = new()
    {
        [0x01] = "FAT12", [0x04] = "FAT16", [0x06] = "FAT16", [0x07] = "NTFS/exFAT",
        [0x0B] = "FAT32", [0x0C] = "FAT32 LBA", [0x0E] = "FAT16 LBA",
        [0x11] = "مخفي FAT12", [0x14] = "مخفي FAT16", [0x17] = "مخفي NTFS",
        [0x1B] = "مخفي FAT32", [0x1C] = "مخفي FAT32", [0x27] = "Windows Recovery (مخفي)",
        [0x42] = "Dynamic", [0x82] = "Linux Swap", [0x83] = "Linux", [0xEE] = "GPT Protective",
    };

    public static string GptTypeName(Guid g) => GptTypes.TryGetValue(g, out var n) ? n : g.ToString();
    public static string MbrTypeName(byte t) => MbrTypes.TryGetValue(t, out var n) ? n : $"0x{t:X2}";

    public static List<PartitionInfo> GetAllPartitions()
    {
        var result = new List<PartitionInfo>();
        for (int disk = 0; disk < 64; disk++)
        {
            using var h = NativeMethods.OpenForRead($@"\\.\PhysicalDrive{disk}");
            if (h.IsInvalid) continue;

            int sectorSize = 512;
            long diskSize = 0;
            var geo = NativeMethods.IoctlQuery(h, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, 256);
            if (geo is { Length: >= 32 })
            {
                // DISK_GEOMETRY (24 bytes) ثم DiskSize
                sectorSize = BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20));
                diskSize = BinaryPrimitives.ReadInt64LittleEndian(geo.AsSpan(24));
                if (sectorSize <= 0) sectorSize = 512;
            }
            string diskName = GetDiskModel(h) ?? $"Disk {disk}";

            var layout = NativeMethods.IoctlQuery(h, NativeMethods.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, 16 * 1024);
            if (layout == null || layout.Length < 48) continue;

            int style = BinaryPrimitives.ReadInt32LittleEndian(layout);
            int count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
            const int headerSize = 48, entrySize = 144;

            for (int i = 0; i < count && headerSize + (i + 1) * entrySize <= layout.Length; i++)
            {
                var e = layout.AsSpan(headerSize + i * entrySize, entrySize);
                long start = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
                long len = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                int number = BinaryPrimitives.ReadInt32LittleEndian(e[24..]);
                if (len <= 0) continue;

                string typeName, gptName = "", styleName;
                if (style == 1) // GPT
                {
                    styleName = "GPT";
                    typeName = GptTypeName(new Guid(e.Slice(32, 16)));
                    gptName = Encoding.Unicode.GetString(e.Slice(72, 72)).TrimEnd('\0');
                }
                else if (style == 0) // MBR
                {
                    styleName = "MBR";
                    byte t = e[32];
                    if (t == 0x00 || t == 0x05 || t == 0x0F || t == 0x85) continue; // فارغ أو Extended
                    typeName = MbrTypeName(t);
                }
                else continue;

                result.Add(new PartitionInfo
                {
                    DiskNumber = disk, DiskName = diskName, DiskSize = diskSize, SectorSize = sectorSize,
                    Style = styleName, PartitionNumber = number, Offset = start, Length = len,
                    TypeName = typeName, GptName = gptName,
                });
            }
        }

        MapDriveLetters(result);
        foreach (var p in result) DetectFileSystem(p);
        return result;
    }

    private static string? GetDiskModel(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty(0), QueryType = Standard(0) }
        var query = new byte[12];
        var buf = NativeMethods.IoctlQuery(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, 1024, query);
        if (buf == null || buf.Length < 36) return null;
        int vendorOff = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12));
        int productOff = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(16));
        string s = (ReadAnsi(buf, vendorOff) + " " + ReadAnsi(buf, productOff)).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string ReadAnsi(byte[] b, int off)
    {
        if (off <= 0 || off >= b.Length) return "";
        int end = Array.IndexOf(b, (byte)0, off);
        if (end < 0) end = b.Length;
        return Encoding.ASCII.GetString(b, off, end - off).Trim();
    }

    private static void MapDriveLetters(List<PartitionInfo> parts)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            string letter = drive.Name.TrimEnd('\\');
            using var h = NativeMethods.OpenForRead($@"\\.\{letter}");
            if (h.IsInvalid) continue;
            var ext = NativeMethods.IoctlQuery(h, NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, 256);
            if (ext == null || ext.Length < 32) continue;
            int disk = BinaryPrimitives.ReadInt32LittleEndian(ext.AsSpan(8));
            long start = BinaryPrimitives.ReadInt64LittleEndian(ext.AsSpan(16));
            var p = parts.FirstOrDefault(x => x.DiskNumber == disk && x.Offset == start);
            if (p == null) continue;
            p.DriveLetter = letter;
            try
            {
                if (drive.IsReady) { p.Label = drive.VolumeLabel; p.FileSystem = drive.DriveFormat; }
            }
            catch { /* قرص غير جاهز */ }
        }
    }

    private static void DetectFileSystem(PartitionInfo p)
    {
        try
        {
            using var dev = new PhysicalDiskDevice(p.DiskNumber, p.SectorSize);
            var reader = new PartitionReader(dev, p.Offset, p.Length);
            var boot = reader.Read(0, 512);
            string fs = DetectFromBootSector(boot);
            if (fs != "") p.FileSystem = fs;
            else if (p.FileSystem == "غير معروف" && p.TypeName == "Microsoft Reserved") p.FileSystem = "—";
        }
        catch { /* بعض الأقسام لا تُقرأ */ }
    }

    public static string DetectFromBootSector(byte[] boot)
    {
        string Ascii(int off, int len) => Encoding.ASCII.GetString(boot, off, len);
        if (boot.Length < 512) return "";
        if (Ascii(3, 8) == "NTFS    ") return "NTFS";
        if (Ascii(3, 8) == "-FVE-FS-") return "BitLocker";
        if (Ascii(3, 8) == "EXFAT   ") return "exFAT";
        if (Ascii(0x52, 5) == "FAT32") return "FAT32";
        if (Ascii(0x36, 3) == "FAT") return "FAT";
        if (boot[3] == 'R' && boot[4] == 'e' && boot[5] == 'F' && boot[6] == 'S') return "ReFS";
        return "";
    }
}
