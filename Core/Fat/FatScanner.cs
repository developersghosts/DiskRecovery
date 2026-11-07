using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Fat;

/// <summary>قراءة بارتشن FAT12 / FAT16 / FAT32.</summary>
public sealed class FatVolume : IAllocationMap
{
    public PartitionReader Reader { get; }
    public int BytesPerSector { get; }
    public int ClusterSize { get; }
    public int FatBits { get; }
    public long DataStart { get; }
    public long RootDirOffset { get; }      // FAT12/16
    public int RootDirBytes { get; }        // FAT12/16
    public uint RootCluster { get; }        // FAT32
    public uint ClusterCount { get; }
    private readonly uint[] _fat;

    public FatVolume(PartitionReader reader)
    {
        Reader = reader;
        var b = reader.Read(0, 512);
        if (b[510] != 0x55 || b[511] != 0xAA) throw new InvalidDataException("قطاع الإقلاع ليس FAT.");
        BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(11));
        int spc = b[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14));
        int numFats = b[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(17));
        long totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(19));
        if (totalSectors == 0) totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
        long fatSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22));
        if (fatSize == 0) fatSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));

        if (BytesPerSector is not (512 or 1024 or 2048 or 4096) || spc == 0 || numFats == 0 || fatSize == 0)
            throw new InvalidDataException("قطاع الإقلاع FAT تالف.");

        ClusterSize = BytesPerSector * spc;
        RootDirBytes = rootEntries * 32;
        int rootSectors = (RootDirBytes + BytesPerSector - 1) / BytesPerSector;
        long fatStart = (long)reserved * BytesPerSector;
        RootDirOffset = fatStart + numFats * fatSize * BytesPerSector;
        long firstDataSector = reserved + numFats * fatSize + rootSectors;
        DataStart = firstDataSector * BytesPerSector;
        ClusterCount = (uint)((totalSectors - firstDataSector) / spc);
        FatBits = ClusterCount < 4085 ? 12 : ClusterCount < 65525 ? 16 : 32;
        RootCluster = FatBits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44)) : 0;

        // قراءة جدول FAT الأول
        long fatBytes = Math.Min(fatSize * BytesPerSector, FatBits switch { 12 => (ClusterCount + 2) * 3 / 2 + 2, 16 => (ClusterCount + 2) * 2L, _ => (ClusterCount + 2) * 4L });
        var raw = new byte[fatBytes];
        for (long off = 0; off < fatBytes; off += 4 << 20)
            reader.Read(fatStart + off, raw, (int)off, (int)Math.Min(4 << 20, fatBytes - off));
        _fat = new uint[ClusterCount + 2];
        for (uint c = 0; c < _fat.Length; c++)
        {
            switch (FatBits)
            {
                case 12:
                    int o = (int)(c * 3 / 2);
                    if (o + 1 >= raw.Length) break;
                    int v = raw[o] | (raw[o + 1] << 8);
                    _fat[c] = (uint)((c & 1) == 0 ? v & 0xFFF : v >> 4);
                    break;
                case 16:
                    if (c * 2 + 1 < raw.Length) _fat[c] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)(c * 2)));
                    break;
                default:
                    if (c * 4 + 3 < raw.Length) _fat[c] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)(c * 4))) & 0x0FFFFFFF;
                    break;
            }
        }
    }

    public string FsName => $"FAT{FatBits}";
    public long DataEnd => DataStart + (long)ClusterCount * ClusterSize;

    public bool IsValidCluster(uint c) => c >= 2 && c < ClusterCount + 2;
    public bool IsFree(uint c) => IsValidCluster(c) && _fat[c] == 0;
    public long ClusterOffset(uint c) => DataStart + (long)(c - 2) * ClusterSize;

    private bool IsEnd(uint v) => FatBits switch { 12 => v >= 0xFF8, 16 => v >= 0xFFF8, _ => v >= 0x0FFFFFF8 };

    public List<uint> Chain(uint start, int max = 1 << 22)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        uint c = start;
        while (IsValidCluster(c) && seen.Add(c) && list.Count < max)
        {
            list.Add(c);
            uint next = _fat[c];
            if (IsEnd(next) || next < 2) break;
            c = next;
        }
        return list;
    }

    public bool? IsAllocated(long partitionOffset)
    {
        if (partitionOffset < DataStart) return true;
        uint c = (uint)((partitionOffset - DataStart) / ClusterSize) + 2;
        return IsValidCluster(c) ? _fat[c] != 0 : true;
    }

    public byte[] ReadClusters(IReadOnlyList<uint> clusters)
    {
        var buf = new byte[clusters.Count * (long)ClusterSize];
        for (int i = 0; i < clusters.Count; i++) Reader.Read(ClusterOffset(clusters[i]), buf, i * ClusterSize, ClusterSize);
        return buf;
    }
}

/// <summary>يطلع الملفات المحذوفة من FAT: المداخل اللي أول حرف فيها 0xE5، مع استرجاع الاسم الطويل.</summary>
public static class FatScanner
{
    private sealed record DirJob(byte[] Content, long Id, string Path, bool InsideDeleted, int Depth);

    public static ScanResult Scan(PartitionReader reader, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var vol = new FatVolume(reader);
        var items = new List<DeletedItem>();
        var prog = new ScanProgress { Stage = "قراءة المجلدات", Total = 0 };
        long nextId = 1;
        var visited = new HashSet<uint>();

        byte[] root = vol.FatBits == 32
            ? vol.ReadClusters(vol.Chain(vol.RootCluster))
            : reader.Read(vol.RootDirOffset, vol.RootDirBytes);
        if (vol.FatBits == 32) visited.Add(vol.RootCluster);

        var queue = new Queue<DirJob>();
        queue.Enqueue(new DirJob(root, 0, "", false, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var job = queue.Dequeue();
            prog.Done++;
            foreach (var e in ParseDirectory(job.Content))
            {
                bool deleted = e.Deleted || job.InsideDeleted;
                uint start = e.StartCluster;
                if (!vol.IsValidCluster(start) && vol.IsValidCluster(start & 0xFFFF)) start &= 0xFFFF;

                if (e.IsDirectory)
                {
                    if (!vol.IsValidCluster(start) || !visited.Add(start) || job.Depth > 64) continue;
                    byte[]? content = deleted ? ReadDeletedDirectory(vol, start) : vol.ReadClusters(vol.Chain(start));
                    long id = deleted ? nextId++ : -(long)nextId++;
                    if (deleted)
                    {
                        items.Add(new DeletedItem
                        {
                            Id = id, ParentId = job.InsideDeleted ? job.Id : -1, Name = e.Name,
                            FolderPath = job.Path.Length == 0 ? "\\" : job.Path, IsDirectory = true,
                            Modified = e.Modified, Created = e.Created, Source = vol.FsName,
                            Chance = RecoveryChance.Good, Status = content == null ? "مجلد (محتواه غير متاح)" : "مجلد",
                            ParentIsDeletedFolder = job.InsideDeleted,
                        });
                    }
                    if (content != null) queue.Enqueue(new DirJob(content, id, job.Path + "\\" + e.Name, deleted, job.Depth + 1));
                    continue;
                }

                if (!deleted) continue;

                FileData? data = null;
                if (e.Size > 0 && vol.IsValidCluster(start))
                    data = FileData.Contiguous(vol.ClusterOffset(start), e.Size);
                else if (e.Size == 0)
                    data = FileData.FromResident(Array.Empty<byte>());

                var item = new DeletedItem
                {
                    Id = nextId++, ParentId = job.InsideDeleted ? job.Id : -1, Name = e.Name,
                    FolderPath = job.Path.Length == 0 ? "\\" : job.Path, Size = e.Size,
                    Modified = e.Modified, Created = e.Created, Source = vol.FsName, Data = data,
                    ParentIsDeletedFolder = job.InsideDeleted,
                };
                if (data == null) { item.Chance = RecoveryChance.Poor; item.Status = "مكان البيانات غير معروف"; }
                else if (e.Size == 0) { item.Chance = RecoveryChance.Good; item.Status = "ملف فارغ"; }
                else
                {
                    // FAT يمسح تسلسل الكلسترات عند الحذف، فنفترض أن الملف متصل ونتخطى الأجزاء المستخدمة لو وجدت
                    item.Data = item.EvaluateContiguous(vol, data, vol.DataEnd);
                    long clusters = (e.Size + vol.ClusterSize - 1) / vol.ClusterSize;
                    if (clusters > 1 && item.Status == "ممتاز") item.Status = "جيد (بافتراض أن الملف غير مجزأ)";
                }
                items.Add(item);
                prog.Found = items.Count;
            }
            progress?.Report(prog);
        }

        prog.Stage = "تم";
        progress?.Report(prog);
        return new ScanResult { Reader = reader, FileSystem = vol.FsName, Items = items, Allocation = vol };
    }

    /// <summary>المجلد المحذوف: سلسلة كلستراته مُسحت، فنقرأ كلسترات متتالية طالما تبدو كمجلد.</summary>
    private static byte[]? ReadDeletedDirectory(FatVolume vol, uint start)
    {
        var first = vol.ReadClusters(new[] { start });
        // أول مدخلين في أي مجلد هما "." و ".."
        if (!(first[0] == '.' && first[1] == ' ' && first[32] == '.' && first[33] == '.')) return null;
        var parts = new List<byte[]> { first };
        for (uint c = start + 1; vol.IsFree(c) && parts.Count < 256; c++)
        {
            var next = vol.ReadClusters(new[] { c });
            if (!LooksLikeDirectory(next)) break;
            parts.Add(next);
        }
        return parts.SelectMany(p => p).ToArray();
    }

    private static bool LooksLikeDirectory(byte[] b)
    {
        int valid = 0, total = 0;
        for (int o = 0; o + 32 <= b.Length; o += 32)
        {
            if (b[o] == 0) break;
            total++;
            byte attr = b[o + 11];
            if (attr == 0x0F || (attr & 0xC0) == 0) valid++;
        }
        return total > 0 && valid * 10 >= total * 9;
    }

    internal sealed record Entry(string Name, bool Deleted, bool IsDirectory, uint StartCluster, long Size, DateTime? Modified, DateTime? Created);

    internal static List<Entry> ParseDirectory(byte[] d)
    {
        var list = new List<Entry>();
        var lfn = new List<(string Text, byte Checksum)>();
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            var e = d.AsSpan(o, 32);
            byte first = e[0];
            if (first == 0x00) break;
            byte attr = e[11];

            if (attr == 0x0F)
            {
                lfn.Add((LfnChars(e), e[13]));
                continue;
            }
            if ((attr & 0x08) != 0 || first == 0x2E) { lfn.Clear(); continue; } // اسم البارتشن أو . و ..

            bool deleted = first == 0xE5;
            var sfn = e[..11].ToArray();
            if (first == 0x05) sfn[0] = 0xE5;

            string? longName = null;
            if (lfn.Count > 0)
            {
                byte cs = lfn[0].Checksum;
                if (lfn.All(x => x.Checksum == cs))
                {
                    if (deleted)
                    {
                        // أول حرف من الاسم القصير ضاع (اتكتب مكانه E5) — نجربه من الـ checksum
                        foreach (byte c in Candidates())
                        {
                            sfn[0] = c;
                            if (Checksum(sfn) == cs) { longName = Combine(lfn); break; }
                        }
                        if (longName == null) sfn[0] = (byte)'_';
                    }
                    else if (Checksum(sfn) == cs) longName = Combine(lfn);
                }
                lfn.Clear();
            }
            else if (deleted) sfn[0] = (byte)'_';

            string name = longName ?? ShortName(sfn, e[12]);
            if (string.IsNullOrWhiteSpace(name)) continue;

            uint hi = BinaryPrimitives.ReadUInt16LittleEndian(e[20..]);
            uint lo = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
            list.Add(new Entry(
                name, deleted, (attr & 0x10) != 0, (hi << 16) | lo,
                BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                DosTime(BinaryPrimitives.ReadUInt16LittleEndian(e[24..]), BinaryPrimitives.ReadUInt16LittleEndian(e[22..])),
                DosTime(BinaryPrimitives.ReadUInt16LittleEndian(e[16..]), BinaryPrimitives.ReadUInt16LittleEndian(e[14..]))));
        }
        return list;
    }

    // الـ checksum دالة واحد-لواحد في أول حرف، فيوجد حرف واحد فقط يطابق — نقبله لو كان حرفاً مسموحاً في الأسماء القصيرة
    private static IEnumerable<byte> Candidates()
    {
        for (int c = 0x20; c <= 0xFF; c++)
        {
            if (c is >= 'a' and <= 'z' || c == 0xE5 || "\"*+,./:;<=>?[\\]|".Contains((char)c)) continue;
            yield return (byte)c;
        }
    }

    private static string LfnChars(ReadOnlySpan<byte> e)
    {
        var sb = new StringBuilder(13);
        void Add(ReadOnlySpan<byte> s)
        {
            for (int i = 0; i + 1 < s.Length; i += 2)
            {
                char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
                if (c == '\0' || c == '￿') return;
                sb.Append(c);
            }
        }
        // نوقف عند أول نهاية في أي جزء
        var parts = new[] { (1, 10), (14, 12), (28, 4) };
        foreach (var (off, len) in parts)
        {
            int before = sb.Length;
            Add(e.Slice(off, len));
            if (sb.Length - before < len / 2) break;
        }
        return sb.ToString();
    }

    // مداخل LFN مخزنة بالعكس: آخر جزء من الاسم يأتي أولاً
    private static string Combine(List<(string Text, byte Checksum)> parts) =>
        string.Concat(Enumerable.Reverse(parts).Select(p => p.Text));

    private static byte Checksum(byte[] sfn)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + sfn[i]);
        return sum;
    }

    private static string ShortName(byte[] sfn, byte caseFlags)
    {
        string n = Encoding.Latin1.GetString(sfn, 0, 8).TrimEnd();
        string x = Encoding.Latin1.GetString(sfn, 8, 3).TrimEnd();
        if ((caseFlags & 0x08) != 0) n = n.ToLowerInvariant();
        if ((caseFlags & 0x10) != 0) x = x.ToLowerInvariant();
        return x.Length > 0 ? n + "." + x : n;
    }

    internal static DateTime? DosTime(ushort date, ushort time)
    {
        if (date == 0) return null;
        int y = 1980 + (date >> 9), m = (date >> 5) & 0xF, d = date & 0x1F;
        int h = time >> 11, mi = (time >> 5) & 0x3F, s = (time & 0x1F) * 2;
        try { return new DateTime(y, m, d, Math.Min(h, 23), Math.Min(mi, 59), Math.Min(s, 59), DateTimeKind.Local); }
        catch { return null; }
    }
}
