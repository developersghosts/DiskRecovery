using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core;

/// <summary>قراءة جدول البارتشن (MBR / GPT) مباشرة من القطاعات — يُستخدم مع ملفات نسخ الأقراص.</summary>
public static class PartitionTable
{
    public sealed record Entry(int Number, long Offset, long Length, string Style, string TypeName, string Name);

    public static List<Entry> Parse(IBlockDevice dev, long deviceLength)
    {
        var r = new PartitionReader(dev, 0, deviceLength);
        var mbr = r.Read(0, 512);
        var list = new List<Entry>();
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return list;

        // GPT (قطاع 512 أو 4096)
        foreach (int ss in new[] { 512, 4096 })
        {
            if (ss * 2 > deviceLength) continue;
            var hdr = r.Read(ss, 512);
            if (Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART") continue;
            long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(72));
            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(80)), 1024u);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(84));
            if (size < 128 || size > 1024) continue;
            var table = r.Read(entriesLba * ss, count * size);
            for (int i = 0; i < count; i++)
            {
                var e = table.AsSpan(i * size, size);
                var type = new Guid(e[..16]);
                if (type == Guid.Empty) continue;
                long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
                long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
                string name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
                if (first <= 0 || last < first) continue;
                list.Add(new Entry(i + 1, first * ss, (last - first + 1) * ss, "GPT", DiskEnumerator.GptTypeName(type), name));
            }
            return list;
        }

        // MBR + الأقسام المنطقية داخل Extended
        int number = 1;
        for (int i = 0; i < 4; i++)
        {
            var e = mbr.AsSpan(446 + i * 16, 16);
            byte type = e[4];
            long start = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * 512L;
            long len = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]) * 512L;
            if (type == 0 || len == 0) continue;
            if (type is 0x05 or 0x0F or 0x85)
            {
                long ebr = start;
                for (int guard = 0; guard < 128 && ebr > 0 && ebr < deviceLength; guard++)
                {
                    var b = r.Read(ebr, 512);
                    if (b[510] != 0x55 || b[511] != 0xAA) break;
                    var l = b.AsSpan(446, 16);
                    long ls = BinaryPrimitives.ReadUInt32LittleEndian(l[8..]) * 512L;
                    long ll = BinaryPrimitives.ReadUInt32LittleEndian(l[12..]) * 512L;
                    if (l[4] != 0 && ll > 0) list.Add(new Entry(4 + number++, ebr + ls, ll, "MBR", DiskEnumerator.MbrTypeName(l[4]), ""));
                    var n = b.AsSpan(462, 16);
                    long next = BinaryPrimitives.ReadUInt32LittleEndian(n[8..]) * 512L;
                    if (n[4] == 0 || next == 0) break;
                    ebr = start + next;
                }
                continue;
            }
            if (type == 0xEE) continue;
            list.Add(new Entry(i + 1, start, len, "MBR", DiskEnumerator.MbrTypeName(type), ""));
        }
        return list;
    }

    /// <summary>يفتح ملف نسخة (img/dd/raw): لو كان نسخة بارتشن واحد يرجع بارتشن واحد، ولو نسخة قرص كامل يقرأ جدوله.</summary>
    public static List<PartitionInfo> OpenImage(string path)
    {
        long len = new FileInfo(path).Length;
        using var dev = new ImageFileDevice(path);
        var r = new PartitionReader(dev, 0, len);
        string fs = DiskEnumerator.DetectFromBootSector(r.Read(0, 512));
        string name = Path.GetFileName(path);
        var result = new List<PartitionInfo>();
        if (fs != "")
        {
            result.Add(new PartitionInfo
            {
                DiskNumber = -1, ImagePath = path, DiskName = "ملف نسخة: " + name, DiskSize = len, Style = "صورة",
                PartitionNumber = 1, Offset = 0, Length = len, TypeName = "نسخة بارتشن", FileSystem = fs, Label = name,
            });
            return result;
        }
        foreach (var e in Parse(dev, len))
        {
            var p = new PartitionInfo
            {
                DiskNumber = -1, ImagePath = path, DiskName = "ملف نسخة: " + name, DiskSize = len, Style = e.Style,
                PartitionNumber = e.Number, Offset = e.Offset, Length = Math.Min(e.Length, len - e.Offset),
                TypeName = e.TypeName, GptName = e.Name,
            };
            try { var f = DiskEnumerator.DetectFromBootSector(r.Read(e.Offset, 512)); if (f != "") p.FileSystem = f; } catch { }
            result.Add(p);
        }
        if (result.Count == 0)
            result.Add(new PartitionInfo
            {
                DiskNumber = -1, ImagePath = path, DiskName = "ملف نسخة: " + name, DiskSize = len, Style = "صورة",
                PartitionNumber = 0, Offset = 0, Length = len, TypeName = "بيانات خام (بحث عميق فقط)", Label = name,
            });
        return result;
    }
}
