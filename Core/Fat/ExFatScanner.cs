using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Fat;

/// <summary>قراءة بارتشن exFAT (الفلاشات والكروت الكبيرة).</summary>
public sealed class ExFatVolume : IAllocationMap
{
    public PartitionReader Reader { get; }
    public int ClusterSize { get; }
    public long HeapOffset { get; }
    public uint ClusterCount { get; }
    public uint RootCluster { get; }
    private readonly long _fatOffset;
    private byte[]? _bitmap;
    private uint[]? _fat;

    public ExFatVolume(PartitionReader reader)
    {
        Reader = reader;
        var b = reader.Read(0, 512);
        if (Encoding.ASCII.GetString(b, 3, 8) != "EXFAT   ") throw new InvalidDataException("البارتشن ليس exFAT.");
        int bps = 1 << b[108];
        int spc = 1 << b[109];
        ClusterSize = bps * spc;
        _fatOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(80)) * bps;
        uint fatLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(84));
        HeapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(88)) * bps;
        ClusterCount = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(92));
        RootCluster = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(96));

        long fatBytes = Math.Min((long)fatLength * bps, (ClusterCount + 2L) * 4);
        var raw = new byte[fatBytes];
        for (long off = 0; off < fatBytes; off += 4 << 20)
            reader.Read(_fatOffset + off, raw, (int)off, (int)Math.Min(4 << 20, fatBytes - off));
        _fat = new uint[fatBytes / 4];
        for (int i = 0; i < _fat.Length; i++) _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));

        // خريطة الكلسترات المستخدمة موجودة كمدخل 0x81 داخل المجلد الرئيسي
        var root = ReadChain(RootCluster, 0, false);
        for (int o = 0; o + 32 <= root.Length; o += 32)
        {
            if (root[o] == 0) break;
            if (root[o] == 0x81)
            {
                uint bmCluster = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(o + 20));
                long bmLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(o + 24));
                if (bmLen > 0 && bmLen < 256L * 1024 * 1024) _bitmap = ReadChain(bmCluster, bmLen, false);
                break;
            }
        }
    }

    public bool IsValidCluster(uint c) => c >= 2 && c < ClusterCount + 2;
    public long DataEnd => HeapOffset + (long)ClusterCount * ClusterSize;
    public long ClusterOffset(uint c) => HeapOffset + (long)(c - 2) * ClusterSize;

    public bool? IsAllocated(long partitionOffset)
    {
        if (_bitmap == null) return null;
        if (partitionOffset < HeapOffset) return true;
        long idx = (partitionOffset - HeapOffset) / ClusterSize;
        if (idx >= ClusterCount || (idx >> 3) >= _bitmap.Length) return true;
        return (_bitmap[idx >> 3] & (1 << (int)(idx & 7))) != 0;
    }

    public List<uint> Chain(uint start, long maxClusters)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        uint c = start;
        while (IsValidCluster(c) && seen.Add(c) && list.Count < maxClusters)
        {
            list.Add(c);
            if (_fat == null || c >= _fat.Length) break;
            uint next = _fat[c];
            if (next >= 0xFFFFFFF7 || next < 2) break;
            c = next;
        }
        return list;
    }

    /// <summary>يقرأ بيانات تبدأ من كلستر: متصلة (NoFatChain) أو عبر سلسلة FAT.</summary>
    public byte[] ReadChain(uint start, long length, bool contiguous)
    {
        if (!IsValidCluster(start)) return Array.Empty<byte>();
        if (contiguous)
        {
            var b = new byte[Math.Min(length, 64L << 20)];
            Reader.Read(ClusterOffset(start), b, 0, b.Length);
            return b;
        }
        long max = length > 0 ? (length + ClusterSize - 1) / ClusterSize : 4096;
        var chain = Chain(start, max);
        var buf = new byte[chain.Count * (long)ClusterSize];
        for (int i = 0; i < chain.Count; i++) Reader.Read(ClusterOffset(chain[i]), buf, i * ClusterSize, ClusterSize);
        if (length > 0 && buf.Length > length) Array.Resize(ref buf, (int)length);
        return buf;
    }

    /// <summary>أماكن بيانات ملف. للملفات المحذوفة: لو سلسلة FAT لا تطابق الحجم نفترض أنه متصل.</summary>
    public FileData DataFor(uint start, long length, long validLength, bool noFatChain)
    {
        if (!noFatChain)
        {
            long need = (length + ClusterSize - 1) / ClusterSize;
            var chain = Chain(start, need);
            if (chain.Count == need)
            {
                var runs = new List<(long, long, long)>();
                long vcn = 0;
                foreach (var c in chain) runs.Add((vcn++, c - 2, 1));
                return FileData.FromClusterRuns(runs, ClusterSize, HeapOffset, length, validLength);
            }
        }
        return new FileData
        {
            Length = length,
            ValidLength = validLength > 0 && validLength < length ? validLength : length,
            Extents = new List<Extent> { new(ClusterOffset(start), length) },
        };
    }
}

public static class ExFatScanner
{
    private sealed record DirJob(byte[] Content, long Id, string Path, bool InsideDeleted, int Depth);

    public static ScanResult Scan(PartitionReader reader, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var vol = new ExFatVolume(reader);
        var items = new List<DeletedItem>();
        var prog = new ScanProgress { Stage = "قراءة المجلدات" };
        long nextId = 1;
        var visited = new HashSet<uint> { vol.RootCluster };
        var queue = new Queue<DirJob>();
        queue.Enqueue(new DirJob(vol.ReadChain(vol.RootCluster, 0, false), 0, "", false, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var job = queue.Dequeue();
            prog.Done++;
            foreach (var e in ParseDirectory(job.Content))
            {
                bool deleted = e.Deleted || job.InsideDeleted;
                if (e.IsDirectory)
                {
                    if (!vol.IsValidCluster(e.FirstCluster) || !visited.Add(e.FirstCluster) || job.Depth > 64) continue;
                    long id = deleted ? nextId++ : -nextId++;
                    var content = vol.ReadChain(e.FirstCluster, e.DataLength, e.NoFatChain || deleted && e.NoFatChain);
                    if (deleted && !e.NoFatChain && content.Length < e.DataLength)
                        content = vol.ReadChain(e.FirstCluster, e.DataLength, true);
                    if (deleted)
                        items.Add(new DeletedItem
                        {
                            Id = id, ParentId = job.InsideDeleted ? job.Id : -1, Name = e.Name,
                            FolderPath = job.Path.Length == 0 ? "\\" : job.Path, IsDirectory = true,
                            Modified = e.Modified, Created = e.Created, Source = "exFAT",
                            Chance = RecoveryChance.Good, Status = "مجلد", ParentIsDeletedFolder = job.InsideDeleted,
                        });
                    queue.Enqueue(new DirJob(content, id, job.Path + "\\" + e.Name, deleted, job.Depth + 1));
                    continue;
                }
                if (!deleted) continue;

                FileData? data = e.DataLength == 0 ? FileData.FromResident(Array.Empty<byte>())
                    : vol.IsValidCluster(e.FirstCluster) ? vol.DataFor(e.FirstCluster, e.DataLength, e.ValidLength, e.NoFatChain) : null;
                var item = new DeletedItem
                {
                    Id = nextId++, ParentId = job.InsideDeleted ? job.Id : -1, Name = e.Name,
                    FolderPath = job.Path.Length == 0 ? "\\" : job.Path, Size = e.DataLength,
                    Modified = e.Modified, Created = e.Created, Source = "exFAT", Data = data,
                    ParentIsDeletedFolder = job.InsideDeleted,
                };
                if (data == null) { item.Chance = RecoveryChance.Poor; item.Status = "مكان البيانات غير معروف"; }
                else if (e.DataLength == 0) { item.Chance = RecoveryChance.Good; item.Status = "ملف فارغ"; }
                else if (data.Extents.Count == 1) item.Data = item.EvaluateContiguous(vol, data, vol.DataEnd);
                else item.ApplyChance(vol.UsedFraction(data));
                items.Add(item);
                prog.Found = items.Count;
            }
            progress?.Report(prog);
        }
        prog.Stage = "تم";
        progress?.Report(prog);
        return new ScanResult { Reader = reader, FileSystem = "exFAT", Items = items, Allocation = vol };
    }

    internal sealed record Entry(string Name, bool Deleted, bool IsDirectory, uint FirstCluster, long DataLength,
                                 long ValidLength, bool NoFatChain, DateTime? Modified, DateTime? Created);

    internal static List<Entry> ParseDirectory(byte[] d)
    {
        var list = new List<Entry>();
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            byte type = d[o];
            if (type == 0x00) break;
            if (type != 0x85 && type != 0x05) continue;

            bool deleted = type == 0x05;
            int secondary = d[o + 1];
            if (secondary < 2 || secondary > 18 || o + 32 * (secondary + 1) > d.Length) continue;
            byte streamType = d[o + 32];
            if (streamType != (deleted ? 0x40 : 0xC0)) continue;

            ushort attrs = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 4));
            uint created = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 8));
            uint modified = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 12));

            var s = d.AsSpan(o + 32, 32);
            bool noFat = (s[1] & 0x02) != 0;
            int nameLen = s[3];
            long valid = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[8..]);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
            long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);

            var sb = new StringBuilder(nameLen);
            byte nameType = deleted ? (byte)0x41 : (byte)0xC1;
            for (int k = 2; k <= secondary && sb.Length < nameLen; k++)
            {
                int no = o + 32 * k;
                if (d[no] != nameType) break;
                for (int c = 0; c < 15 && sb.Length < nameLen; c++)
                    sb.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(no + 2 + c * 2)));
            }
            if (sb.Length == 0 || length < 0) continue;

            list.Add(new Entry(sb.ToString(), deleted, (attrs & 0x10) != 0, first, length, valid, noFat,
                               Timestamp(modified), Timestamp(created)));
            o += 32 * secondary;
        }
        return list;
    }

    private static DateTime? Timestamp(uint t) => FatScanner.DosTime((ushort)(t >> 16), (ushort)(t & 0xFFFF));
}
