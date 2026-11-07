using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Carving;

public sealed record FoundPartition(long Offset, long Length, string FileSystem, string Evidence);

/// <summary>
/// البحث عن بارتشنات اتمسحت من جدول البارتشن: يدور في المساحة غير المقسّمة على قطاعات إقلاع
/// NTFS / FAT32 / exFAT (والنسخة الاحتياطية لقطاع إقلاع NTFS في آخر البارتشن).
/// </summary>
public static class PartitionFinder
{
    public static List<FoundPartition> Find(IBlockDevice disk, long diskSize, IEnumerable<(long Offset, long Length)> existing,
                                            bool deep, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var used = existing.OrderBy(e => e.Offset).ToList();
        var gaps = new List<(long Start, long End)>();
        long cursor = 0;
        foreach (var (o, l) in used)
        {
            if (o > cursor) gaps.Add((cursor, o));
            cursor = Math.Max(cursor, o + l);
        }
        if (cursor < diskSize) gaps.Add((cursor, diskSize));
        gaps = gaps.Where(g => g.End - g.Start >= 1 << 20).ToList();

        var reader = new PartitionReader(disk, 0, diskSize);
        var found = new List<FoundPartition>();
        var prog = new ScanProgress { Total = gaps.Sum(g => g.End - g.Start), Stage = "البحث عن بارتشنات مفقودة" };

        bool HasMft(long partStart, byte[] boot)
        {
            int bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
            int spc = boot[13];
            long cs = (long)bps * (spc <= 0x80 ? spc : 1 << (256 - spc));
            long mft = partStart + BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30)) * cs;
            if (partStart < 0 || mft + 4 > diskSize) return false;
            var h = reader.Read(mft, 4);
            return h[0] == 'F' && h[1] == 'I' && h[2] == 'L' && h[3] == 'E';
        }

        void Consider(long pos, byte[] sector)
        {
            var c = Check(sector, pos);
            if (c == null) return;
            var fp = c;
            if (fp.FileSystem == "NTFS")
            {
                // قد يكون هذا القطاع هو النسخة الاحتياطية الموجودة في آخر البارتشن
                long total = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(0x28));
                int bps = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(11));
                long backupStart = pos - total * bps;
                if (HasMft(pos, sector)) fp = fp with { Evidence = "قطاع الإقلاع + جدول MFT" };
                else if (HasMft(backupStart, sector)) fp = new FoundPartition(backupStart, fp.Length, "NTFS", "النسخة الاحتياطية لقطاع الإقلاع + جدول MFT");
                else fp = fp with { Evidence = "قطاع الإقلاع فقط" };
            }
            else
            {
                // FAT32 يحتفظ بنسخة احتياطية من قطاع الإقلاع عند القطاع 6، و exFAT عند القطاع 12
                int bps = fp.FileSystem == "exFAT" ? 1 << sector[108] : BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(11));
                int backupAt = fp.FileSystem == "exFAT" ? 12 : BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x32));
                long primary = pos - (long)backupAt * bps;
                if (backupAt is > 0 and < 64 && primary >= 0)
                {
                    var prim = reader.Read(primary, 512);
                    if (prim.AsSpan(0, 90).SequenceEqual(sector.AsSpan(0, 90))) fp = fp with { Offset = primary };
                }
            }
            if (fp.Offset < 0 || fp.Offset + Math.Min(fp.Length, 1 << 20) > diskSize) return;
            if (found.Any(x => x.Offset == fp.Offset)) return;
            if (used.Any(u => fp.Offset >= u.Offset && fp.Offset < u.Offset + u.Length)) return; // بارتشن موجود فعلاً
            found.Add(fp);
        }

        foreach (var (start, end) in gaps)
        {
            ct.ThrowIfCancellationRequested();
            if (!deep)
            {
                // البارتشنات عادةً تبدأ عند بداية المساحة الفارغة أو على حدود 1MB أو القطاع 63
                var spots = new SortedSet<long> { start, AlignUp(start, 1 << 20), AlignUp(start, 63 * 512), end - 512 };
                for (long p = AlignUp(start, 1 << 20); p < Math.Min(end, start + (64L << 20)); p += 1 << 20) spots.Add(p);
                foreach (var p in spots.Where(p => p >= start && p + 512 <= end))
                    Consider(p, reader.Read(p, 512));
                prog.Done += end - start;
                progress?.Report(prog);
                continue;
            }

            const int chunk = 4 << 20;
            var buf = new byte[chunk];
            for (long p = start; p < end; p += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunk, end - p);
                reader.Read(p, buf, 0, n);
                for (int o = 0; o + 512 <= n; o += 512)
                {
                    if (buf[o + 510] != 0x55 || buf[o + 511] != 0xAA) continue;
                    Consider(p + o, buf.AsSpan(o, 512).ToArray());
                }
                prog.Done += n;
                prog.Found = found.Count;
                progress?.Report(prog);
            }
        }
        return found;
    }

    private static long AlignUp(long v, long a) => (v + a - 1) / a * a;

    /// <summary>يتحقق إن القطاع قطاع إقلاع صالح ويرجع مكان وحجم البارتشن.</summary>
    public static FoundPartition? Check(byte[] b, long pos)
    {
        if (b.Length < 512 || b[510] != 0x55 || b[511] != 0xAA) return null;
        string oem = Encoding.ASCII.GetString(b, 3, 8);
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(11));

        if (oem == "NTFS    ")
        {
            int spc = b[13];
            long total = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x28));
            long mft = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x30));
            if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || total <= 0 || mft <= 0) return null;
            int sectorsPerCluster = spc <= 0x80 ? spc : 1 << (256 - spc);
            if (mft * sectorsPerCluster >= total) return null;
            return new FoundPartition(pos, (total + 1) * bps, "NTFS", "قطاع الإقلاع");
        }
        if (oem == "EXFAT   ")
        {
            int shift = b[108];
            long volLen = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(72));
            if (shift is < 9 or > 12 || volLen <= 0) return null;
            return new FoundPartition(pos, volLen << shift, "exFAT", "قطاع الإقلاع");
        }
        if (Encoding.ASCII.GetString(b, 0x52, 5) == "FAT32")
        {
            int spc = b[13], reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14)), fats = b[16];
            long total = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
            uint fatSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
            if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0 || reserved == 0 || fats is < 1 or > 2 || fatSize == 0 || total == 0)
                return null;
            return new FoundPartition(pos, total * bps, "FAT32", "قطاع الإقلاع");
        }
        return null;
    }
}
