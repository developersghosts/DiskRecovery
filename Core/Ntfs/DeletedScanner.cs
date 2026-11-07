namespace DiskRecovery.Core.Ntfs;

/// <summary>فحص NTFS: يقرأ جدول MFT ويطلع كل الملفات والمجلدات المحذوفة بمساراتها الأصلية.</summary>
public static class NtfsScanner
{
    public const long RootRecord = 5;
    private const int FirstUserRecord = 24;

    internal sealed class Node
    {
        public string? Name;
        public long Parent;
        public ushort ParentSeq;
        public ushort Seq;
        public bool InUse;
        public bool IsDir;
    }

    public static ScanResult Scan(PartitionReader reader, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var vol = new NtfsVolume(reader);
        long total = vol.MftRecordCount;
        var nodes = new Dictionary<long, Node>();
        var deleted = new List<MftRecord>();
        var prog = new ScanProgress { Total = total, Stage = "قراءة جدول الملفات (MFT)" };

        int rs = vol.RecordSize;
        int perChunk = Math.Max(1, (4 * 1024 * 1024) / rs);
        var buf = new byte[perChunk * rs];

        for (long first = 0; first < total; first += perChunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(perChunk, total - first);
            vol.ReadStream(vol.MftRuns, first * rs, buf, 0, n * rs);

            for (int i = 0; i < n; i++)
            {
                long recNo = first + i;
                var r = ParseAt(buf, i * rs, rs, recNo);
                if (r == null) continue;
                nodes[recNo] = ToNode(r);
                if (!r.InUse && recNo >= FirstUserRecord)
                {
                    deleted.Add(r);
                    prog.Found = deleted.Count;
                }
            }
            prog.Done = first + n;
            progress?.Report(prog);
        }

        ResolveExtensions(vol, deleted, total);

        prog.Stage = "فحص حالة الملفات";
        progress?.Report(prog);
        try { vol.LoadBitmap(); } catch { /* نكمل بدون فحص الكتابة فوق الملفات */ }

        var items = BuildItems(vol.ToFileData, vol.HasBitmap ? vol : null, nodes, deleted, "", "MFT", idOffset: 0);
        prog.Stage = "تم";
        prog.Found = items.Count;
        progress?.Report(prog);
        return new ScanResult { Reader = reader, FileSystem = "NTFS", Items = items, Allocation = vol.HasBitmap ? vol : null };
    }

    internal static MftRecord? ParseAt(byte[] buf, int off, int rs, long recNo)
    {
        if (!MftParser.IsFileRecord(buf.AsSpan(off, rs))) return null;
        MftParser.ApplyFixup(buf.AsSpan(off, rs));
        MftRecord? r;
        try { r = MftParser.Parse(buf, off, rs, recNo); } catch { return null; }
        if (r == null || r.BaseRecord != 0 || r.Name == null) return null;
        return r;
    }

    internal static Node ToNode(MftRecord r) => new()
    {
        Name = r.Name, Parent = r.ParentRecord, ParentSeq = r.ParentSequence,
        Seq = r.Sequence, InUse = r.InUse, IsDir = r.IsDirectory,
    };

    /// <summary>استكمال بيانات الملفات المجزأة جداً التي توجد runs الخاصة بها في سجلات أخرى.</summary>
    internal static void ResolveExtensions(NtfsVolume vol, List<MftRecord> deleted, long total)
    {
        foreach (var r in deleted.Where(d => d.DataExtensionRecords != null))
        {
            foreach (var ext in r.DataExtensionRecords!)
            {
                if (ext <= 0 || ext >= total) continue;
                MftRecord? er;
                try { er = vol.ReadRecord(ext); } catch { continue; }
                if (er == null || er.BaseRecord != r.RecordNumber || er.Runs == null) continue;
                r.Runs ??= new List<DataRun>();
                r.Runs.AddRange(er.Runs);
                if (!r.HasData && er.HasData) { r.HasData = true; r.DataSize = er.DataSize; r.InitializedSize = er.InitializedSize; }
            }
            if (r.Runs != null) r.Runs = r.Runs.OrderBy(x => x.StartVcn).ToList();
        }
    }

    /// <summary>يبني عناصر القائمة مع المسارات الكاملة. rootPrefix يُضاف قبل كل مسار.</summary>
    internal static List<DeletedItem> BuildItems(Func<MftRecord, FileData> toData, IAllocationMap? map,
                                                 Dictionary<long, Node> nodes, List<MftRecord> deleted,
                                                 string rootPrefix, string source, long idOffset)
    {
        string orphan = rootPrefix + "\\$بدون_مسار";
        var pathCache = new Dictionary<long, string>();

        bool ParentMatches(long parent, ushort parentSeq)
        {
            if (!nodes.TryGetValue(parent, out var p) || !p.IsDir) return false;
            if (p.InUse) return p.Seq == parentSeq;
            // عند الحذف يزيد ويندوز رقم التسلسل بواحد
            return p.Seq == parentSeq || p.Seq == (ushort)(parentSeq + 1);
        }
        string DirPath(long dir, int depth)
        {
            if (dir == RootRecord) return rootPrefix;
            if (pathCache.TryGetValue(dir, out var cached)) return cached;
            var d = nodes[dir];
            string parent = depth > 200 || !ParentMatches(d.Parent, d.ParentSeq) ? orphan : DirPath(d.Parent, depth + 1);
            string path = parent + "\\" + d.Name;
            pathCache[dir] = path;
            return path;
        }

        var items = new List<DeletedItem>(deleted.Count);
        foreach (var r in deleted)
        {
            bool parentOk = ParentMatches(r.ParentRecord, r.ParentSequence);
            string folder = parentOk ? DirPath(r.ParentRecord, 0) : orphan;
            FileData? data = r.IsDirectory ? null : toData(r);
            var item = new DeletedItem
            {
                Id = idOffset + r.RecordNumber,
                ParentId = parentOk ? idOffset + r.ParentRecord : -1,
                Name = r.Name!,
                FolderPath = folder.Length == 0 ? "\\" : folder,
                IsDirectory = r.IsDirectory,
                Size = r.IsDirectory ? 0 : r.DataSize,
                Created = r.Created,
                Modified = r.Modified,
                Source = source,
                Data = data,
                ParentIsDeletedFolder = parentOk && nodes[r.ParentRecord] is { InUse: false },
            };
            Evaluate(map, item, r);
            items.Add(item);
        }
        return items;
    }

    private static void Evaluate(IAllocationMap? map, DeletedItem item, MftRecord r)
    {
        if (item.IsDirectory) { item.Chance = RecoveryChance.Good; item.Status = "مجلد"; return; }
        if (r.Encrypted) { item.Chance = RecoveryChance.Unsupported; item.Status = "مشفّر (EFS) - غير مدعوم"; return; }
        if (r.Compressed) { item.Chance = RecoveryChance.Unsupported; item.Status = "مضغوط (NTFS) - غير مدعوم"; return; }
        if (r.DataSize == 0) { item.Chance = RecoveryChance.Good; item.Status = "ملف فارغ"; return; }
        if (r.DataResident) { item.Chance = RecoveryChance.Good; item.Status = "ممتاز"; return; }
        if (r.Runs == null || r.Runs.Count == 0 || !r.HasData)
        {
            item.Chance = RecoveryChance.Poor; item.Status = "لا توجد معلومات عن مكان البيانات"; return;
        }
        item.ApplyChance(map.UsedFraction(item.Data!));
    }
}
