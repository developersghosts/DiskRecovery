using System.Buffers.Binary;

namespace DiskRecovery.Core.Carving;

/// <summary>قراءة عشوائية سريعة من البارتشن مع كاش بسيط بالبلوكات (لتحليل بنية الملفات).</summary>
public sealed class ByteSource
{
    private const int BlockSize = 1 << 20;
    private const int MaxBlocks = 16;
    private readonly PartitionReader _reader;
    private readonly Dictionary<long, byte[]> _cache = new();
    private readonly Queue<long> _order = new();

    public long Length { get; }

    public ByteSource(PartitionReader reader, long length)
    {
        _reader = reader;
        Length = length;
    }

    private byte[] Block(long index)
    {
        if (_cache.TryGetValue(index, out var b)) return b;
        long off = index * BlockSize;
        int n = (int)Math.Min(BlockSize, Length - off);
        b = new byte[BlockSize];
        if (n > 0) _reader.Read(off, b, 0, n);
        if (_cache.Count >= MaxBlocks) _cache.Remove(_order.Dequeue());
        _cache[index] = b;
        _order.Enqueue(index);
        return b;
    }

    public int U8(long pos) => pos < 0 || pos >= Length ? -1 : Block(pos / BlockSize)[pos % BlockSize];

    public void Read(long pos, Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            long p = pos + done;
            if (p >= Length) { dst[done..].Clear(); return; }
            var b = Block(p / BlockSize);
            int inBlock = (int)(p % BlockSize);
            int n = Math.Min(dst.Length - done, BlockSize - inBlock);
            b.AsSpan(inBlock, n).CopyTo(dst[done..]);
            done += n;
        }
    }

    public byte[] Bytes(long pos, int count) { var b = new byte[count]; Read(pos, b); return b; }
    public uint U16BE(long p) { Span<byte> b = stackalloc byte[2]; Read(p, b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    public uint U16LE(long p) { Span<byte> b = stackalloc byte[2]; Read(p, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    public uint U32BE(long p) { Span<byte> b = stackalloc byte[4]; Read(p, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    public uint U32LE(long p) { Span<byte> b = stackalloc byte[4]; Read(p, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    public ulong U64LE(long p) { Span<byte> b = stackalloc byte[8]; Read(p, b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }
    public ulong U64BE(long p) { Span<byte> b = stackalloc byte[8]; Read(p, b); return BinaryPrimitives.ReadUInt64BigEndian(b); }

    /// <summary>يبحث عن نمط بايتات من موضع معين حتى حد أقصى. يرجع -1 لو لم يجده.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long from, long limit)
    {
        limit = Math.Min(limit, Length);
        long p = from;
        while (p < limit)
        {
            var b = Block(p / BlockSize);
            int inBlock = (int)(p % BlockSize);
            int avail = (int)Math.Min(BlockSize - inBlock, limit - p);
            int i = b.AsSpan(inBlock, avail).IndexOf(pattern);
            if (i >= 0) return p + i;
            // النمط قد يكون مقسوماً بين بلوكين
            if (pattern.Length > 1 && p + avail < limit)
            {
                long edge = p + avail - (pattern.Length - 1);
                var tmp = Bytes(edge, (pattern.Length - 1) * 2);
                int j = tmp.AsSpan().IndexOf(pattern);
                if (j >= 0) return edge + j;
            }
            p += avail;
        }
        return -1;
    }

    /// <summary>يبحث عن بايت واحد (أسرع).</summary>
    public long IndexOfByte(byte value, long from, long limit)
    {
        limit = Math.Min(limit, Length);
        long p = from;
        while (p < limit)
        {
            var b = Block(p / BlockSize);
            int inBlock = (int)(p % BlockSize);
            int avail = (int)Math.Min(BlockSize - inBlock, limit - p);
            int i = b.AsSpan(inBlock, avail).IndexOf(value);
            if (i >= 0) return p + i;
            p += avail;
        }
        return -1;
    }
}
