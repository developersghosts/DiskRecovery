using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Ntfs;

/// <summary>جزء متصل من بيانات ملف على القرص. Lcn = -1 يعني منطقة فارغة (sparse).</summary>
public readonly record struct DataRun(long StartVcn, long Lcn, long Length)
{
    public bool IsSparse => Lcn < 0;
    public long EndVcn => StartVcn + Length;
}

/// <summary>بيانات سجل واحد من جدول MFT بعد تحليله.</summary>
public sealed class MftRecord
{
    public long RecordNumber;
    public ushort Sequence;
    public bool InUse;
    public bool IsDirectory;
    public long BaseRecord;            // ≠ 0 لو كان سجل امتداد

    public string? Name;
    public int NameSpace = -1;
    public long ParentRecord;
    public ushort ParentSequence;

    public DateTime? Created;
    public DateTime? Modified;

    public bool HasData;
    public bool DataResident;
    public byte[]? ResidentData;
    public List<DataRun>? Runs;
    public long DataSize;
    public long InitializedSize;
    public bool Compressed;
    public bool Encrypted;

    public bool HasAttributeList;
    public List<long>? DataExtensionRecords;   // سجلات أخرى تحمل بقية $DATA
}

internal static class MftParser
{
    public const uint AttrStandardInfo = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFFFFFF;

    /// <summary>يطبّق "Update Sequence" لإصلاح آخر بايتين في كل 512 بايت.</summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        if (usaOff < 0x28 || usaCount < 2 || usaOff + usaCount * 2 > rec.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOff..]);
        bool ok = true;
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            if (pos + 2 > rec.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[pos..]) != usn) ok = false;
            rec[pos] = rec[usaOff + i * 2];
            rec[pos + 1] = rec[usaOff + i * 2 + 1];
        }
        return ok;
    }

    public static bool IsFileRecord(ReadOnlySpan<byte> rec) =>
        rec.Length >= 48 && rec[0] == (byte)'F' && rec[1] == (byte)'I' && rec[2] == (byte)'L' && rec[3] == (byte)'E';

    /// <summary>يحلل سجل MFT (بعد تطبيق الـ fixup). يرجع null لو السجل فاضي أو تالف.</summary>
    public static MftRecord? Parse(byte[] buf, int offset, int size, long recordNumber)
    {
        var rec = buf.AsSpan(offset, size);
        if (!IsFileRecord(rec)) return null;

        var r = new MftRecord
        {
            RecordNumber = recordNumber,
            Sequence = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x10..]),
        };
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        r.InUse = (flags & 0x0001) != 0;
        r.IsDirectory = (flags & 0x0002) != 0;
        r.BaseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & 0x0000FFFFFFFFFFFF);

        int pos = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec[0x18..]), (uint)size);
        if (used <= 0) used = size;

        for (int guard = 0; guard < 256 && pos + 16 <= used; guard++)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[pos..]);
            if (type == AttrEnd) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(pos + 4)..]);
            if (len < 16 || pos + len > size) break;
            var a = rec.Slice(pos, len);
            ParseAttribute(r, a, type);
            pos += len;
        }
        return r;
    }

    private static void ParseAttribute(MftRecord r, ReadOnlySpan<byte> a, uint type)
    {
        bool nonResident = a[8] != 0;
        int nameLen = a[9];
        ushort aflags = BinaryPrimitives.ReadUInt16LittleEndian(a[0x0C..]);

        ReadOnlySpan<byte> value = default;
        if (!nonResident && a.Length >= 0x18)
        {
            int vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[0x10..]);
            int voff = BinaryPrimitives.ReadUInt16LittleEndian(a[0x14..]);
            if (voff + vlen <= a.Length && vlen >= 0) value = a.Slice(voff, vlen);
        }

        switch (type)
        {
            case AttrStandardInfo when value.Length >= 16:
                r.Created = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value));
                r.Modified = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[8..]));
                break;

            case AttrFileName when value.Length >= 0x42:
            {
                int ns = value[0x41];
                int chars = value[0x40];
                if (0x42 + chars * 2 > value.Length) break;
                // الأولوية: Win32 أو Win32&DOS ثم POSIX ثم الاسم القصير DOS
                int Rank(int n) => n switch { 1 or 3 => 3, 0 => 2, 2 => 1, _ => 0 };
                if (r.Name != null && Rank(ns) <= Rank(r.NameSpace)) break;
                ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
                r.ParentRecord = (long)(parent & 0x0000FFFFFFFFFFFF);
                r.ParentSequence = (ushort)(parent >> 48);
                r.Name = Encoding.Unicode.GetString(value.Slice(0x42, chars * 2));
                r.NameSpace = ns;
                if (r.Created == null) r.Created = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]));
                if (r.Modified == null) r.Modified = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[0x10..]));
                if (!r.HasData) r.DataSize = BinaryPrimitives.ReadInt64LittleEndian(value[0x30..]);
                break;
            }

            case AttrAttributeList:
                r.HasAttributeList = true;
                if (!nonResident) ParseAttributeList(r, value);
                break;

            case AttrData when nameLen == 0: // التدفق الأساسي فقط (بدون Alternate Data Streams)
                r.Compressed = (aflags & 0x0001) != 0;
                r.Encrypted = (aflags & 0x4000) != 0;
                if (!nonResident)
                {
                    r.HasData = true;
                    r.DataResident = true;
                    r.ResidentData = value.ToArray();
                    r.DataSize = value.Length;
                    r.InitializedSize = value.Length;
                }
                else if (a.Length >= 0x40)
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(a[0x10..]);
                    int runOff = BinaryPrimitives.ReadUInt16LittleEndian(a[0x20..]);
                    var runs = DecodeRuns(a[runOff..], startVcn);
                    r.Runs ??= new List<DataRun>();
                    r.Runs.AddRange(runs);
                    if (startVcn == 0)
                    {
                        r.HasData = true;
                        r.DataSize = BinaryPrimitives.ReadInt64LittleEndian(a[0x30..]);
                        r.InitializedSize = BinaryPrimitives.ReadInt64LittleEndian(a[0x38..]);
                    }
                }
                break;
        }
    }

    public static void ParseAttributeList(MftRecord r, ReadOnlySpan<byte> list)
    {
        int p = 0;
        while (p + 0x1A <= list.Length)
        {
            uint t = BinaryPrimitives.ReadUInt32LittleEndian(list[p..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(list[(p + 4)..]);
            if (len < 0x1A || p + len > list.Length) break;
            int nameLen = list[p + 6];
            long refRec = (long)(BinaryPrimitives.ReadUInt64LittleEndian(list[(p + 0x10)..]) & 0x0000FFFFFFFFFFFF);
            if (t == AttrData && nameLen == 0 && refRec != r.RecordNumber)
            {
                r.DataExtensionRecords ??= new List<long>();
                if (!r.DataExtensionRecords.Contains(refRec)) r.DataExtensionRecords.Add(refRec);
            }
            p += len;
        }
    }

    /// <summary>فك ترميز قائمة الـ Data Runs.</summary>
    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> data, long startVcn)
    {
        var runs = new List<DataRun>();
        int p = 0;
        long vcn = startVcn, lcn = 0;
        while (p < data.Length)
        {
            byte header = data[p++];
            if (header == 0) break;
            int lenBytes = header & 0x0F, offBytes = header >> 4;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > data.Length) break;

            long length = 0;
            for (int i = 0; i < lenBytes; i++) length |= (long)data[p + i] << (8 * i);
            p += lenBytes;

            if (offBytes == 0)
            {
                runs.Add(new DataRun(vcn, -1, length)); // sparse
            }
            else
            {
                long delta = 0;
                for (int i = 0; i < offBytes; i++) delta |= (long)data[p + i] << (8 * i);
                if ((data[p + offBytes - 1] & 0x80) != 0 && offBytes < 8) delta -= 1L << (8 * offBytes); // سالب
                p += offBytes;
                lcn += delta;
                runs.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
        return runs;
    }

    private static DateTime? FromFileTime(long ft)
    {
        if (ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc()) return null;
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { return null; }
    }
}
