using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Ntfs;

/// <summary>قراءة بارتشن NTFS مباشرة من القطاعات (بدون الاعتماد على ويندوز).</summary>
public sealed class NtfsVolume : IAllocationMap
{
    public PartitionReader Reader { get; }
    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public int RecordSize { get; }
    public long TotalClusters { get; }
    public long MftLcn { get; }
    public List<DataRun> MftRuns { get; private set; } = new();
    public long MftRecordCount { get; private set; }

    private byte[]? _bitmap;

    public NtfsVolume(PartitionReader reader)
    {
        Reader = reader;
        var boot = reader.Read(0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new InvalidDataException("البارتشن ليس بنظام NTFS أو مشفّر (BitLocker).");

        BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int spc = boot[0x0D];
        int sectorsPerCluster = spc <= 0x80 ? spc : 1 << (256 - spc);
        ClusterSize = BytesPerSector * sectorsPerCluster;
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x28));
        TotalClusters = totalSectors / Math.Max(1, sectorsPerCluster);
        MftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte cpr = (sbyte)boot[0x40];
        RecordSize = cpr > 0 ? cpr * ClusterSize : 1 << -cpr;

        if (BytesPerSector < 256 || ClusterSize <= 0 || RecordSize < 256 || RecordSize > 65536)
            throw new InvalidDataException("قطاع الإقلاع (Boot Sector) تالف.");

        LoadMftLayout();
    }

    private void LoadMftLayout()
    {
        var buf = Reader.Read(MftLcn * ClusterSize, RecordSize);
        MftParser.ApplyFixup(buf);
        var rec0 = MftParser.Parse(buf, 0, RecordSize, 0)
                   ?? throw new InvalidDataException("تعذّر قراءة جدول الملفات $MFT.");
        if (rec0.Runs == null || rec0.Runs.Count == 0)
            throw new InvalidDataException("جدول $MFT لا يحتوي على بيانات.");

        MftRuns = rec0.Runs;
        long size = rec0.DataSize;

        // لو جدول MFT نفسه مجزأ جداً فبقية أجزائه في سجلات امتداد
        if (rec0.DataExtensionRecords != null)
        {
            foreach (var ext in rec0.DataExtensionRecords)
            {
                var er = ReadRecord(ext);
                if (er?.Runs != null) MftRuns.AddRange(er.Runs);
            }
            MftRuns = MftRuns.OrderBy(r => r.StartVcn).ToList();
        }
        MftRecordCount = size / RecordSize;
    }

    /// <summary>يقرأ سجل MFT بالرقم.</summary>
    public MftRecord? ReadRecord(long number)
    {
        var buf = new byte[RecordSize];
        ReadStream(MftRuns, number * RecordSize, buf, 0, RecordSize);
        if (!MftParser.IsFileRecord(buf)) return null;
        MftParser.ApplyFixup(buf);
        return MftParser.Parse(buf, 0, RecordSize, number);
    }

    /// <summary>يقرأ من ملف غير مقيم (non-resident) بمعلومية قائمة الـ runs.</summary>
    public void ReadStream(IReadOnlyList<DataRun> runs, long streamOffset, byte[] dst, int dstOffset, int count)
    {
        while (count > 0)
        {
            long vcn = streamOffset / ClusterSize;
            int inCluster = (int)(streamOffset % ClusterSize);
            DataRun? run = null;
            foreach (var r in runs)
                if (vcn >= r.StartVcn && vcn < r.EndVcn) { run = r; break; }

            long runBytesLeft = run is { } rr
                ? (rr.EndVcn - vcn) * ClusterSize - inCluster
                : ClusterSize - inCluster;
            int n = (int)Math.Min(count, runBytesLeft);

            if (run is not { } x || x.IsSparse)
                Array.Clear(dst, dstOffset, n);
            else
                Reader.Read((x.Lcn + (vcn - x.StartVcn)) * ClusterSize + inCluster, dst, dstOffset, n);

            streamOffset += n; dstOffset += n; count -= n;
        }
    }

    /// <summary>يحمّل خريطة الكلسترات المستخدمة ($Bitmap) لمعرفة هل الملف المحذوف تمت الكتابة فوقه.</summary>
    public void LoadBitmap()
    {
        var rec = ReadRecord(6);
        if (rec?.Runs == null || rec.DataSize <= 0 || rec.DataSize > 512L * 1024 * 1024) return;
        var bm = new byte[rec.DataSize];
        const int chunk = 4 * 1024 * 1024;
        for (long off = 0; off < bm.Length; off += chunk)
        {
            int n = (int)Math.Min(chunk, bm.Length - off);
            ReadStream(rec.Runs, off, bm, (int)off, n);
        }
        _bitmap = bm;
    }

    public bool HasBitmap => _bitmap != null;

    public bool? IsAllocated(long partitionOffset)
    {
        if (_bitmap == null) return null;
        long lcn = partitionOffset / ClusterSize;
        long bi = lcn >> 3;
        if (bi < 0 || bi >= _bitmap.Length) return true;
        return (_bitmap[bi] & (1 << (int)(lcn & 7))) != 0;
    }

    /// <summary>هل هذا الموضع داخل جدول MFT الحالي؟</summary>
    public bool IsInsideMft(long partitionOffset)
    {
        long lcn = partitionOffset / ClusterSize;
        foreach (var r in MftRuns)
            if (!r.IsSparse && lcn >= r.Lcn && lcn < r.Lcn + r.Length) return true;
        return false;
    }

    public FileData ToFileData(MftRecord r)
    {
        if (r.DataResident) return FileData.FromResident(r.ResidentData ?? Array.Empty<byte>());
        return FileData.FromClusterRuns(
            (r.Runs ?? new List<DataRun>()).Select(x => (x.StartVcn, x.Lcn, x.Length)),
            ClusterSize, 0, r.DataSize, r.InitializedSize);
    }

    /// <summary>عدد الكلسترات من قائمة runs التي أصبحت مستخدمة الآن من ملفات أخرى.</summary>
    public long CountUsedClusters(IEnumerable<DataRun> runs, long maxClusters)
    {
        if (_bitmap == null) return 0;
        long used = 0, seen = 0;
        foreach (var r in runs)
        {
            if (r.IsSparse) { seen += r.Length; continue; }
            for (long c = 0; c < r.Length && seen < maxClusters; c++, seen++)
            {
                long lcn = r.Lcn + c;
                long bi = lcn >> 3;
                if (bi < 0 || bi >= _bitmap.Length) { used++; continue; }
                if ((_bitmap[bi] & (1 << (int)(lcn & 7))) != 0) used++;
            }
            if (seen >= maxClusters) break;
        }
        return used;
    }
}
