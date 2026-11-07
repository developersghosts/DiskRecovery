using System.Buffers.Binary;
using DiskRecovery.Core.Ntfs;

namespace DiskRecovery.Core.Carving;

public sealed class DeepScanOptions
{
    /// <summary>أنواع الملفات المطلوب البحث عنها (من Signatures.AllTypes).</summary>
    public HashSet<string> Types { get; init; } = new(Signatures.AllTypes);
    /// <summary>البحث في المساحة الفارغة فقط (أسرع ولا يكرر الملفات الموجودة).</summary>
    public bool FreeSpaceOnly { get; init; } = true;
    /// <summary>البحث عن سجلات MFT قديمة (استرجاع بعد الفورمات السريع).</summary>
    public bool OldMftRecords { get; init; } = true;
}

/// <summary>
/// البحث العميق: يمر على البارتشن كله قطاع بقطاع ويتعرف على الملفات من توقيعها،
/// ويجمع سجلات MFT القديمة (بعد الفورمات السريع) ليعيد بناء الأسماء والمجلدات.
/// </summary>
public static class DeepScanner
{
    public const string CarvedRoot = "\\$بحث_عميق";
    public const string OldMftRoot = "\\$بعد_الفورمات";
    private const long CarvedIdBase = 1L << 50;
    private const long OldMftIdBase = 1L << 40;
    private const int Chunk = 4 << 20;
    private const int MaxFiles = 2_000_000;

    public static ScanResult Scan(PartitionReader reader, ScanResult? quick, DeepScanOptions opt,
                                  IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long length = reader.Length;
        var map = quick?.Allocation;
        bool freeOnly = opt.FreeSpaceOnly && map != null;

        NtfsVolume? ntfs = null;
        try { if (QuickScan.DetectFileSystem(reader) == "NTFS") ntfs = new NtfsVolume(reader); } catch { }

        int align = ntfs?.ClusterSize ?? 512;
        int recordSize = ntfs?.RecordSize ?? 1024;
        long clusterForOldMft = ntfs?.ClusterSize ?? 4096;

        var known = new HashSet<long>(quick?.Items.Where(i => i.Data != null).Select(i => i.Data!.FirstOffset).Where(o => o >= 0) ?? Enumerable.Empty<long>());
        var carved = new List<DeletedItem>();
        var oldRecords = new Dictionary<long, MftRecord>();
        var counters = new Dictionary<string, int>();
        var src = new ByteSource(reader, length);
        var prog = new ScanProgress { Total = length, Stage = "البحث العميق في كل قطاعات البارتشن" };

        var buf = new byte[Chunk + 64];
        long nextAllowed = 0;
        var swatch = System.Diagnostics.Stopwatch.StartNew();

        for (long chunkStart = 0; chunkStart < length; chunkStart += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Chunk + 64, length - chunkStart);
            reader.Read(chunkStart, buf, 0, n);
            int scanEnd = (int)Math.Min(Chunk, length - chunkStart);

            for (int off = 0; off < scanEnd; off += align)
            {
                long pos = chunkStart + off;
                if (freeOnly && map!.IsAllocated(pos) == true) continue;

                // سجلات MFT قديمة (كل 1024 بايت داخل الكلستر)
                if (opt.OldMftRecords)
                {
                    for (int r = 0; r < align && off + r + recordSize <= n; r += recordSize)
                    {
                        if (buf[off + r] != 'F' || buf[off + r + 1] != 'I' || buf[off + r + 2] != 'L' || buf[off + r + 3] != 'E') continue;
                        if (ntfs != null && ntfs.IsInsideMft(pos + r)) continue;
                        TryCollectMftRecord(buf, off + r, recordSize, oldRecords);
                    }
                }

                if (pos < nextAllowed || off + 16 > n) continue;
                string? type = Signatures.Match(buf.AsSpan(off, 16));
                if (type == null || !opt.Types.Contains(type) || known.Contains(pos)) continue;

                var found = Signatures.Measure(type, src, pos);
                if (found is not { } f || f.Length <= 0) continue;

                counters[f.Extension] = counters.GetValueOrDefault(f.Extension) + 1;
                var data = FileData.Contiguous(pos, f.Length, f.Estimated);
                var item = new DeletedItem
                {
                    Id = CarvedIdBase + carved.Count,
                    Name = $"{f.Extension.ToUpperInvariant()}_{counters[f.Extension]:D5}.{f.Extension}",
                    FolderPath = CarvedRoot + "\\" + f.Extension.ToUpperInvariant(),
                    Size = f.Length,
                    Source = "توقيع",
                    Data = data,
                };
                if (map != null && !freeOnly) item.ApplyChance(map.UsedFraction(data));
                else { item.Chance = RecoveryChance.Good; item.Status = "تم العثور عليه بالتوقيع"; }
                if (f.Estimated) item.Status = "حجم تقديري";
                carved.Add(item);

                if (!f.Estimated) nextAllowed = pos + f.Length;
                if (carved.Count >= MaxFiles) break;
            }

            prog.Done = Math.Min(length, chunkStart + Chunk);
            prog.Found = carved.Count + oldRecords.Count;
            double secs = swatch.Elapsed.TotalSeconds;
            if (secs > 1)
            {
                double speed = prog.Done / secs;
                var eta = TimeSpan.FromSeconds((length - prog.Done) / Math.Max(1, speed));
                prog.Stage = $"البحث العميق — {speed / (1 << 20):0} MB/s — متبقي {eta:hh\\:mm\\:ss}";
            }
            progress?.Report(prog);
            if (carved.Count >= MaxFiles) break;
        }

        var items = new List<DeletedItem>();
        if (quick != null) items.AddRange(quick.Items);
        items.AddRange(BuildOldMftItems(oldRecords, clusterForOldMft, map, ntfs));
        items.AddRange(carved);

        prog.Stage = "تم";
        prog.Found = items.Count;
        progress?.Report(prog);
        return new ScanResult { Reader = reader, FileSystem = quick?.FileSystem ?? "?", Items = items, Allocation = map };
    }

    private static void TryCollectMftRecord(byte[] buf, int off, int size, Dictionary<long, MftRecord> into)
    {
        var span = buf.AsSpan(off, size);
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        uint allocated = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
        if (usaOff is < 0x28 or > 0x38 || allocated != size) return;
        long recNo = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
        if (recNo < 24 || into.ContainsKey(recNo)) return;

        var copy = span.ToArray();
        MftRecord? r;
        try
        {
            MftParser.ApplyFixup(copy);
            r = MftParser.Parse(copy, 0, size, recNo);
        }
        catch { return; }
        if (r == null || r.BaseRecord != 0 || r.Name == null) return;
        into[recNo] = r;
    }

    private static List<DeletedItem> BuildOldMftItems(Dictionary<long, MftRecord> records, long clusterSize,
                                                     IAllocationMap? map, NtfsVolume? ntfs)
    {
        if (records.Count == 0) return new();
        var nodes = records.ToDictionary(kv => kv.Key, kv => NtfsScanner.ToNode(kv.Value));
        // المجلد الرئيسي (5) غير موجود ضمن السجلات القديمة عادةً — نضيفه كجذر
        if (!nodes.ContainsKey(NtfsScanner.RootRecord))
            nodes[NtfsScanner.RootRecord] = new NtfsScanner.Node { Name = "", IsDir = true, InUse = false, Seq = 5 };

        // كل السجلات القديمة تعتبر "مفقودة" بعد الفورمات، سواء كانت مستخدمة أو محذوفة وقتها
        var list = records.Values.Where(r => !(r.ParentRecord == NtfsScanner.RootRecord && r.Name!.StartsWith('$'))).ToList();
        FileData ToData(MftRecord r) => r.DataResident
            ? FileData.FromResident(r.ResidentData ?? Array.Empty<byte>())
            : FileData.FromClusterRuns((r.Runs ?? new List<DataRun>()).Select(x => (x.StartVcn, x.Lcn, x.Length)),
                                       clusterSize, 0, r.DataSize, r.InitializedSize);

        var root = nodes[NtfsScanner.RootRecord];
        bool rootWasInUse = root.InUse;
        root.InUse = true; // نسمح بمطابقة الأبناء مع الجذر مباشرة
        var items = NtfsScanner.BuildItems(ToData, map, nodes, list, OldMftRoot, "MFT قديم", OldMftIdBase);
        root.InUse = rootWasInUse;
        return items;
    }

}
