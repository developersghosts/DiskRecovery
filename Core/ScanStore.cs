using System.IO.Compression;
using System.Text.Json;

namespace DiskRecovery.Core;

/// <summary>حفظ نتيجة الفحص في ملف (.drscan) لاستكمال الاسترجاع لاحقاً بدون إعادة الفحص.</summary>
public static class ScanStore
{
    public const string Extension = ".drscan";

    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public DateTime Saved { get; set; }
        public int DiskNumber { get; set; }
        public string DiskName { get; set; } = "";
        public long Offset { get; set; }
        public long Length { get; set; }
        public int SectorSize { get; set; }
        public string? ImagePath { get; set; }
        public string FileSystem { get; set; } = "";
        public List<ItemDto> Items { get; set; } = new();
    }

    private sealed class ItemDto
    {
        public long Id { get; set; }
        public long P { get; set; }
        public string N { get; set; } = "";
        public string F { get; set; } = "";
        public bool D { get; set; }
        public long S { get; set; }
        public DateTime? M { get; set; }
        public DateTime? C { get; set; }
        public int Ch { get; set; }
        public string St { get; set; } = "";
        public string Src { get; set; } = "";
        public bool Pd { get; set; }
        public long? L { get; set; }
        public long? V { get; set; }
        public string? R { get; set; }
        public long[]? E { get; set; }   // أزواج (موضع، طول)
        public bool Est { get; set; }
    }

    public static void Save(string path, ScanResult scan, PartitionInfo p)
    {
        var snap = new Snapshot
        {
            Saved = DateTime.Now, DiskNumber = p.DiskNumber, DiskName = p.DiskName, Offset = p.Offset, Length = p.Length,
            SectorSize = p.SectorSize, ImagePath = p.ImagePath, FileSystem = scan.FileSystem,
            Items = scan.Items.Select(i => new ItemDto
            {
                Id = i.Id, P = i.ParentId, N = i.Name, F = i.FolderPath, D = i.IsDirectory, S = i.Size, M = i.Modified, C = i.Created,
                Ch = (int)i.Chance, St = i.Status, Src = i.Source, Pd = i.ParentIsDeletedFolder,
                L = i.Data?.Length, V = i.Data?.ValidLength, Est = i.Data?.SizeEstimated ?? false,
                R = i.Data?.Resident is { } r ? Convert.ToBase64String(r) : null,
                E = i.Data?.Extents.SelectMany(e => new[] { e.Offset, e.Length }).ToArray(),
            }).ToList(),
        };
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, snap);
    }

    public sealed record Loaded(PartitionInfo Partition, List<DeletedItem> Items, string FileSystem, DateTime Saved);

    /// <summary>يقرأ الملف المحفوظ ويطابقه مع البارتشن الحالي (لازم نفس القرص ونفس المكان).</summary>
    public static Loaded Load(string path, IEnumerable<PartitionInfo> current)
    {
        Snapshot snap;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            snap = JsonSerializer.Deserialize<Snapshot>(gz) ?? throw new InvalidDataException("ملف الفحص تالف.");

        PartitionInfo? p = snap.ImagePath != null
            ? (File.Exists(snap.ImagePath)
                ? PartitionTable.OpenImage(snap.ImagePath).FirstOrDefault(x => x.Offset == snap.Offset)
                : throw new FileNotFoundException("ملف النسخة غير موجود: " + snap.ImagePath))
            : current.FirstOrDefault(x => x.DiskNumber == snap.DiskNumber && x.Offset == snap.Offset && x.Length == snap.Length)
              ?? current.FirstOrDefault(x => x.Offset == snap.Offset && x.Length == snap.Length && x.DiskName == snap.DiskName);
        if (p == null)
            throw new InvalidOperationException($"البارتشن اللي اتعمل له الفحص غير موجود حالياً (Disk {snap.DiskNumber}). وصّل الهارد وحدّث القائمة ثم حاول مرة أخرى.");

        var items = snap.Items.Select(d => new DeletedItem
        {
            Id = d.Id, ParentId = d.P, Name = d.N, FolderPath = d.F, IsDirectory = d.D, Size = d.S, Modified = d.M, Created = d.C,
            Chance = (RecoveryChance)d.Ch, Status = d.St, Source = d.Src, ParentIsDeletedFolder = d.Pd,
            Data = d.L == null ? null : new FileData
            {
                Length = d.L.Value, ValidLength = d.V ?? d.L.Value, SizeEstimated = d.Est,
                Resident = d.R != null ? Convert.FromBase64String(d.R) : null,
                Extents = d.E == null ? new() : Enumerable.Range(0, d.E.Length / 2).Select(k => new Extent(d.E[k * 2], d.E[k * 2 + 1])).ToList(),
            },
        }).ToList();
        return new Loaded(p, items, snap.FileSystem, snap.Saved);
    }
}
