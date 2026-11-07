namespace DiskRecovery.Core;

/// <summary>جزء متصل من بيانات الملف داخل البارتشن. Offset = -1 يعني منطقة أصفار.</summary>
public readonly record struct Extent(long Offset, long Length);

/// <summary>مكان بيانات ملف على القرص — موحّد لكل الأنظمة (NTFS / FAT / exFAT / البحث بالتوقيع).</summary>
public sealed class FileData
{
    public long Length { get; init; }
    /// <summary>البايتات بعد هذا الحد تُعتبر أصفاراً (Initialized size في NTFS).</summary>
    public long ValidLength { get; init; }
    public byte[]? Resident { get; init; }
    public List<Extent> Extents { get; init; } = new();
    public bool SizeEstimated { get; init; }

    private long[]? _starts;

    /// <summary>أول بايت حقيقي للملف داخل البارتشن (أو -1).</summary>
    public long FirstOffset => Extents.Count > 0 && Extents[0].Offset >= 0 ? Extents[0].Offset : -1;

    public static FileData Contiguous(long offset, long length, bool estimated = false) => new()
    {
        Length = length, ValidLength = length, SizeEstimated = estimated,
        Extents = new List<Extent> { new(offset, length) },
    };

    public static FileData FromResident(byte[] data) => new()
    {
        Length = data.Length, ValidLength = data.Length, Resident = data,
    };

    /// <summary>يحوّل قائمة الكلسترات (VCN/LCN) لقائمة مواضع بالبايت.</summary>
    public static FileData FromClusterRuns(IEnumerable<(long StartVcn, long Lcn, long Count)> runs, long clusterSize,
                                           long dataAreaOffset, long length, long validLength)
    {
        var list = new List<Extent>();
        long vcn = 0;
        foreach (var r in runs.OrderBy(r => r.StartVcn))
        {
            if (r.StartVcn > vcn) list.Add(new Extent(-1, (r.StartVcn - vcn) * clusterSize)); // فجوة
            if (r.StartVcn < vcn) continue; // تداخل غير منطقي
            list.Add(new Extent(r.Lcn < 0 ? -1 : dataAreaOffset + r.Lcn * clusterSize, r.Count * clusterSize));
            vcn = r.StartVcn + r.Count;
        }
        return new FileData
        {
            Length = length,
            ValidLength = validLength > 0 && validLength < length ? validLength : length,
            Extents = list,
        };
    }

    /// <summary>عدد البايتات المعروف مكانها فعلاً على القرص.</summary>
    public long MappedLength => Extents.Where(e => e.Offset >= 0).Sum(e => e.Length);

    public void Read(PartitionReader reader, long offset, byte[] buffer, int bufOffset, int count)
    {
        if (Resident != null)
        {
            int avail = (int)Math.Max(0, Math.Min(count, Resident.Length - offset));
            if (avail > 0) Buffer.BlockCopy(Resident, (int)offset, buffer, bufOffset, avail);
            if (avail < count) Array.Clear(buffer, bufOffset + avail, count - avail);
            return;
        }

        if (_starts == null)
        {
            var s = new long[Extents.Count];
            long acc = 0;
            for (int i = 0; i < s.Length; i++) { s[i] = acc; acc += Extents[i].Length; }
            _starts = s;
        }

        while (count > 0)
        {
            if (offset >= ValidLength)
            {
                Array.Clear(buffer, bufOffset, count);
                return;
            }
            int idx = Array.BinarySearch(_starts, offset);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0 || idx >= Extents.Count)
            {
                Array.Clear(buffer, bufOffset, count);
                return;
            }
            var ext = Extents[idx];
            long inExt = offset - _starts[idx];
            long left = ext.Length - inExt;
            if (left <= 0) { Array.Clear(buffer, bufOffset, count); return; }
            int n = (int)Math.Min(Math.Min(count, left), ValidLength - offset);

            if (ext.Offset < 0) Array.Clear(buffer, bufOffset, n);
            else reader.Read(ext.Offset + inExt, buffer, bufOffset, n);

            offset += n; bufOffset += n; count -= n;
        }
    }

    public byte[] ReadHead(PartitionReader reader, int maxBytes)
    {
        int n = (int)Math.Min(maxBytes, Length);
        var b = new byte[Math.Max(0, n)];
        if (n > 0) Read(reader, 0, b, 0, n);
        return b;
    }
}

public enum RecoveryChance { Good, Partial, Poor, Unsupported }

/// <summary>ملف أو مجلد محذوف (أو تم العثور عليه بالتوقيع) يظهر في القائمة.</summary>
public sealed class DeletedItem
{
    public long Id { get; init; }
    public long ParentId { get; init; } = -1;
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "\\";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public DateTime? Created { get; init; }
    public RecoveryChance Chance { get; set; }
    public string Status { get; set; } = "";
    public bool Checked { get; set; }
    /// <summary>MFT / FAT / exFAT / MFT قديم / توقيع</summary>
    public string Source { get; init; } = "";
    public bool ParentIsDeletedFolder { get; init; }
    public FileData? Data { get; set; }

    public string FullPath => FolderPath.TrimEnd('\\') + "\\" + Name;
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public FileCategory Category => IsDirectory ? FileCategory.Folder : FileCategories.Of(Extension);
}

/// <summary>خريطة الكلسترات المستخدمة في البارتشن (لمعرفة هل تمت الكتابة فوق ملف محذوف).</summary>
public interface IAllocationMap
{
    int ClusterSize { get; }
    /// <summary>هل البايت عند هذا الموضع (داخل البارتشن) داخل كلستر مستخدم حالياً؟ null = غير معروف.</summary>
    bool? IsAllocated(long partitionOffset);
}

public static class AllocationMapExtensions
{
    /// <summary>نسبة البايتات المستخدمة الآن من أماكن الملف (0..1). null لو غير معروف.</summary>
    public static double? UsedFraction(this IAllocationMap? map, FileData data)
    {
        if (map == null || data.Resident != null) return data.Resident != null ? 0 : null;
        long total = 0, used = 0;
        long cs = map.ClusterSize;
        foreach (var e in data.Extents)
        {
            if (e.Offset < 0) continue;
            long len = Math.Min(e.Length, Math.Max(0, data.Length - total));
            for (long o = 0; o < len; o += cs)
            {
                var a = map.IsAllocated(e.Offset + o);
                if (a == null) return null;
                if (a.Value) used++;
            }
            total += e.Length;
            if (total >= data.Length) break;
        }
        long clusters = Math.Max(1, (Math.Min(total, data.Length) + cs - 1) / cs);
        return Math.Min(1.0, (double)used / clusters);
    }

    /// <summary>
    /// لملفات FAT/exFAT اللي ضاعت سلسلة كلستراتها: يمشي من أول الملف ويأخذ الكلسترات الفارغة فقط،
    /// متخطياً الكلسترات المستخدمة لملفات أخرى (الملف غالباً كان مجزأ حولها).
    /// </summary>
    public static FileData? SkipAllocatedWalk(this IAllocationMap map, long start, long length, long validLength, long limit, out int skipped)
    {
        skipped = 0;
        long cs = map.ClusterSize;
        long need = (length + cs - 1) / cs;
        var exts = new List<Extent>();
        long got = 0;
        for (long pos = start; got < need && pos + cs <= limit; pos += cs)
        {
            var a = map.IsAllocated(pos);
            if (a == null) return null;
            if (a.Value) { skipped++; if (skipped > need * 4 + 64) return null; continue; }
            if (exts.Count > 0 && exts[^1].Offset + exts[^1].Length == pos)
                exts[^1] = new Extent(exts[^1].Offset, exts[^1].Length + cs);
            else exts.Add(new Extent(pos, cs));
            got++;
        }
        if (got < need) return null;
        return new FileData
        {
            Length = length,
            ValidLength = validLength > 0 && validLength < length ? validLength : length,
            Extents = exts,
        };
    }

    /// <summary>يطبّق تقييم الحالة، ولو الملف متصل ومكتوب فوق جزء منه يحاول تخطي الأجزاء المستخدمة.</summary>
    public static FileData EvaluateContiguous(this DeletedItem item, IAllocationMap map, FileData data, long limit)
    {
        var used = map.UsedFraction(data);
        if (used is > 0 && data.Extents.Count == 1 && map.IsAllocated(data.Extents[0].Offset) == false)
        {
            var walked = map.SkipAllocatedWalk(data.Extents[0].Offset, data.Length, data.ValidLength, limit, out int skipped);
            if (walked != null && skipped > 0)
            {
                item.Chance = RecoveryChance.Good;
                item.Status = "جيد (ملف مجزأ — تم تجميع أجزائه)";
                return walked;
            }
        }
        item.ApplyChance(used);
        return data;
    }

    public static void ApplyChance(this DeletedItem item, double? usedFraction)
    {
        if (usedFraction == null) { item.Chance = RecoveryChance.Good; item.Status = "غير محدد"; return; }
        double pct = usedFraction.Value * 100;
        if (pct <= 0) { item.Chance = RecoveryChance.Good; item.Status = "ممتاز"; }
        else if (pct >= 99) { item.Chance = RecoveryChance.Poor; item.Status = "تمت الكتابة فوقه"; }
        else { item.Chance = RecoveryChance.Partial; item.Status = $"تالف جزئياً ({pct:0}% مكتوب فوقه)"; }
    }
}

public sealed class ScanResult
{
    public required PartitionReader Reader { get; init; }
    public required string FileSystem { get; init; }
    public List<DeletedItem> Items { get; init; } = new();
    public IAllocationMap? Allocation { get; init; }

    private Dictionary<long, List<DeletedItem>>? _children;
    private Dictionary<long, DeletedItem>? _byId;

    /// <summary>العناصر المحذوفة مجمّعة حسب المجلد الأب (لاسترجاع مجلد بمحتوياته).</summary>
    public Dictionary<long, List<DeletedItem>> ChildrenOf => _children ??= BuildChildren();
    public Dictionary<long, DeletedItem> ById => _byId ??= Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());

    public void InvalidateIndexes() { _children = null; _byId = null; }

    private Dictionary<long, List<DeletedItem>> BuildChildren()
    {
        var d = new Dictionary<long, List<DeletedItem>>();
        foreach (var i in Items)
        {
            if (i.ParentId < 0) continue;
            if (!d.TryGetValue(i.ParentId, out var l)) d[i.ParentId] = l = new();
            l.Add(i);
        }
        return d;
    }
}

public sealed class ScanProgress
{
    public long Done;
    public long Total;
    public int Found;
    public string Stage = "";
}

public enum FileCategory { Folder, Image, Document, Spreadsheet, Presentation, Pdf, Video, Audio, Archive, Cad, Text, Other }

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = new()
    {
        ["jpg"] = FileCategory.Image, ["jpeg"] = FileCategory.Image, ["png"] = FileCategory.Image, ["gif"] = FileCategory.Image,
        ["bmp"] = FileCategory.Image, ["tif"] = FileCategory.Image, ["tiff"] = FileCategory.Image, ["webp"] = FileCategory.Image,
        ["heic"] = FileCategory.Image, ["raw"] = FileCategory.Image, ["cr2"] = FileCategory.Image, ["nef"] = FileCategory.Image,
        ["psd"] = FileCategory.Image, ["ico"] = FileCategory.Image, ["svg"] = FileCategory.Image,
        ["doc"] = FileCategory.Document, ["docx"] = FileCategory.Document, ["rtf"] = FileCategory.Document, ["odt"] = FileCategory.Document,
        ["xls"] = FileCategory.Spreadsheet, ["xlsx"] = FileCategory.Spreadsheet, ["xlsm"] = FileCategory.Spreadsheet,
        ["csv"] = FileCategory.Spreadsheet, ["ods"] = FileCategory.Spreadsheet,
        ["ppt"] = FileCategory.Presentation, ["pptx"] = FileCategory.Presentation, ["odp"] = FileCategory.Presentation,
        ["pdf"] = FileCategory.Pdf,
        ["mp4"] = FileCategory.Video, ["mov"] = FileCategory.Video, ["avi"] = FileCategory.Video, ["mkv"] = FileCategory.Video,
        ["wmv"] = FileCategory.Video, ["3gp"] = FileCategory.Video, ["m4v"] = FileCategory.Video, ["flv"] = FileCategory.Video,
        ["mp3"] = FileCategory.Audio, ["wav"] = FileCategory.Audio, ["m4a"] = FileCategory.Audio, ["aac"] = FileCategory.Audio,
        ["flac"] = FileCategory.Audio, ["ogg"] = FileCategory.Audio, ["wma"] = FileCategory.Audio, ["amr"] = FileCategory.Audio,
        ["zip"] = FileCategory.Archive, ["rar"] = FileCategory.Archive, ["7z"] = FileCategory.Archive, ["gz"] = FileCategory.Archive,
        ["tar"] = FileCategory.Archive, ["iso"] = FileCategory.Archive,
        ["dwg"] = FileCategory.Cad, ["dxf"] = FileCategory.Cad, ["dwf"] = FileCategory.Cad, ["skp"] = FileCategory.Cad,
        ["rvt"] = FileCategory.Cad, ["max"] = FileCategory.Cad, ["3ds"] = FileCategory.Cad, ["bak"] = FileCategory.Cad,
        ["txt"] = FileCategory.Text, ["log"] = FileCategory.Text, ["xml"] = FileCategory.Text, ["json"] = FileCategory.Text,
        ["html"] = FileCategory.Text, ["htm"] = FileCategory.Text, ["ini"] = FileCategory.Text, ["md"] = FileCategory.Text,
        ["cs"] = FileCategory.Text, ["sql"] = FileCategory.Text, ["bat"] = FileCategory.Text,
    };

    public static FileCategory Of(string ext) => Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static string ArabicName(FileCategory c) => c switch
    {
        FileCategory.Folder => "مجلدات", FileCategory.Image => "صور", FileCategory.Document => "مستندات Word",
        FileCategory.Spreadsheet => "جداول Excel", FileCategory.Presentation => "عروض", FileCategory.Pdf => "PDF",
        FileCategory.Video => "فيديو", FileCategory.Audio => "صوت", FileCategory.Archive => "ملفات مضغوطة",
        FileCategory.Cad => "رسومات أوتوكاد/تصميم", FileCategory.Text => "ملفات نصية", _ => "أخرى",
    };

    public static bool IsTextExtension(string ext) => Of(ext) == FileCategory.Text || ext is "csv";
    public static bool IsImageExtension(string ext) => ext is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff" or "ico";
}
