using System.Text;

namespace DiskRecovery.Core.Carving;

/// <summary>نتيجة التعرف على ملف من توقيعه: الامتداد والحجم.</summary>
public readonly record struct CarvedFile(string Extension, long Length, bool Estimated);

/// <summary>
/// التعرف على الملفات من أول بايتات فيها وحساب حجمها الحقيقي من بنيتها الداخلية
/// (وليس بحجم ثابت) — عشان الملف يرجع كامل وبدون زيادة.
/// </summary>
public static class Signatures
{
    public const long MaxDefault = 2L << 30; // 2GB

    public static readonly string[] AllTypes =
        { "jpg", "png", "gif", "bmp", "pdf", "office", "ole", "mp4", "riff", "mp3", "7z", "rar", "dwg" };

    public static string TypeName(string t) => t switch
    {
        "jpg" => "صور JPG", "png" => "صور PNG", "gif" => "صور GIF", "bmp" => "صور BMP", "pdf" => "PDF",
        "office" => "Word/Excel/PowerPoint الحديثة و ZIP", "ole" => "Word/Excel/PowerPoint القديمة",
        "mp4" => "فيديو MP4 / MOV / صور HEIC", "riff" => "AVI / WAV / WEBP", "mp3" => "MP3",
        "7z" => "7z", "rar" => "RAR", "dwg" => "أوتوكاد DWG", _ => t,
    };

    /// <summary>فحص سريع على أول بايتات (head يكفي 16 بايت). يرجع نوع التوقيع أو null.</summary>
    public static string? Match(ReadOnlySpan<byte> h)
    {
        if (h.Length < 16) return null;
        switch (h[0])
        {
            case 0xFF when h[1] == 0xD8 && h[2] == 0xFF && h[3] >= 0xC0: return "jpg";
            case 0x89 when h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A: return "png";
            case (byte)'G' when h[1] == 'I' && h[2] == 'F' && h[3] == '8' && (h[4] == '7' || h[4] == '9') && h[5] == 'a': return "gif";
            case (byte)'B' when h[1] == 'M' && h[6] == 0 && h[7] == 0 && h[8] == 0 && h[9] == 0 && h[15] == 0 && h[14] is 12 or 40 or 52 or 56 or 108 or 124: return "bmp";
            case (byte)'%' when h[1] == 'P' && h[2] == 'D' && h[3] == 'F' && h[4] == '-': return "pdf";
            case 0x50 when h[1] == 0x4B && h[2] == 0x03 && h[3] == 0x04: return "office";
            case 0xD0 when h[1] == 0xCF && h[2] == 0x11 && h[3] == 0xE0 && h[4] == 0xA1 && h[5] == 0xB1 && h[6] == 0x1A && h[7] == 0xE1: return "ole";
            case (byte)'R' when h[1] == 'I' && h[2] == 'F' && h[3] == 'F': return "riff";
            case (byte)'I' when h[1] == 'D' && h[2] == '3' && h[3] is 2 or 3 or 4: return "mp3";
            case 0x37 when h[1] == 0x7A && h[2] == 0xBC && h[3] == 0xAF && h[4] == 0x27 && h[5] == 0x1C: return "7z";
            case (byte)'R' when h[1] == 'a' && h[2] == 'r' && h[3] == '!' && h[4] == 0x1A && h[5] == 0x07: return "rar";
            case (byte)'A' when h[1] == 'C' && h[2] == '1' && h[3] == '0' && char.IsAsciiDigit((char)h[4]) && char.IsAsciiDigit((char)h[5]): return "dwg";
        }
        if (h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p' && h[0] == 0 && h[1] == 0) return "mp4";
        return null;
    }

    /// <summary>يحسب حجم الملف الحقيقي. null لو البيانات مش ملف سليم.</summary>
    public static CarvedFile? Measure(string type, ByteSource s, long start, long max = MaxDefault)
    {
        long limit = Math.Min(s.Length, start + max);
        try
        {
            return type switch
            {
                "jpg" => Simple("jpg", Jpeg(s, start, limit)),
                "png" => Simple("png", Png(s, start, limit)),
                "gif" => Simple("gif", Gif(s, start, limit)),
                "bmp" => Simple("bmp", Bmp(s, start, limit)),
                "pdf" => Simple("pdf", Pdf(s, start, limit)),
                "office" => Zip(s, start, limit),
                "ole" => Ole(s, start, limit),
                "mp4" => IsoBmff(s, start, limit),
                "riff" => Riff(s, start, limit),
                "mp3" => Simple("mp3", Mp3(s, start, limit)),
                "7z" => Simple("7z", SevenZip(s, start, limit)),
                "rar" => Simple("rar", Rar(s, start, limit)),
                "dwg" => Dwg(s, start, limit),
                _ => null,
            };
        }
        catch { return null; }
    }

    private static CarvedFile? Simple(string ext, long len) => len > 0 ? new CarvedFile(ext, len, false) : null;

    // ───────────── JPG ─────────────
    internal static long Jpeg(ByteSource s, long start, long limit)
    {
        long p = start + 2;
        bool sawSos = false, sawFrame = false;
        while (p + 4 <= limit)
        {
            if (s.U8(p) != 0xFF) return -1;
            int m = s.U8(p + 1);
            while (m == 0xFF) { p++; m = s.U8(p + 1); }
            if (m == 0xD9) return sawSos && sawFrame ? p + 2 - start : -1;
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            if (m < 0xC0) return -1;
            uint len = s.U16BE(p + 2);
            if (len < 2) return -1;
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) sawFrame = true;
            p += 2 + len;
            if (m != 0xDA) continue;

            sawSos = true;
            // بيانات الصورة المضغوطة: نبحث عن أول FF متبوع بعلامة حقيقية
            while (true)
            {
                long q = s.IndexOfByte(0xFF, p, limit);
                if (q < 0) return -1;
                int b = s.U8(q + 1);
                if (b == 0x00 || (b >= 0xD0 && b <= 0xD7) || b == 0xFF) { p = q + 1; continue; }
                p = q;
                break;
            }
        }
        return -1;
    }

    // ───────────── PNG ─────────────
    private static long Png(ByteSource s, long start, long limit)
    {
        long p = start + 8;
        bool header = false;
        while (p + 12 <= limit)
        {
            uint len = s.U32BE(p);
            var t = s.Bytes(p + 4, 4);
            if (len > 0x7FFFFFFF || !t.All(c => c is >= (byte)'A' and <= (byte)'z')) return -1;
            string type = Encoding.ASCII.GetString(t);
            if (!header) { if (type != "IHDR") return -1; header = true; }
            p += 12 + len;
            if (type == "IEND") return p - start;
        }
        return -1;
    }

    // ───────────── GIF ─────────────
    private static long Gif(ByteSource s, long start, long limit)
    {
        long p = start + 6;
        int flags = s.U8(p + 4);
        p += 7;
        if ((flags & 0x80) != 0) p += 3L * (1 << ((flags & 7) + 1));
        long SkipSub(long q)
        {
            while (q < limit)
            {
                int n = s.U8(q);
                if (n < 0) return -1;
                q += 1 + n;
                if (n == 0) return q;
            }
            return -1;
        }
        while (p < limit)
        {
            int b = s.U8(p);
            if (b == 0x3B) return p + 1 - start;
            if (b == 0x21) { p = SkipSub(p + 2); if (p < 0) return -1; }
            else if (b == 0x2C)
            {
                int f = s.U8(p + 9);
                p += 10;
                if ((f & 0x80) != 0) p += 3L * (1 << ((f & 7) + 1));
                p = SkipSub(p + 1);
                if (p < 0) return -1;
            }
            else return -1;
        }
        return -1;
    }

    // ───────────── BMP ─────────────
    private static long Bmp(ByteSource s, long start, long limit)
    {
        uint size = s.U32LE(start + 2);
        uint dataOff = s.U32LE(start + 10);
        uint w = s.U32LE(start + 18), h = s.U32LE(start + 22);
        if (size < 60 || size > 300_000_000 || dataOff >= size || dataOff < 26 || w == 0 || w > 30000 || h == 0) return -1;
        return start + size <= limit ? size : -1;
    }

    // ───────────── PDF ─────────────
    private static readonly byte[] Eof = "%%EOF"u8.ToArray();
    private static long Pdf(ByteSource s, long start, long limit)
    {
        long end = -1, p = start;
        while (true)
        {
            long e = s.IndexOf(Eof, p, limit);
            if (e < 0) break;
            end = e + 5;
            // نتجاوز نهاية السطر
            while (end < limit && s.U8(end) is 0x0D or 0x0A or 0x20) end++;
            // تحديثات لاحقة على الملف (incremental update)؟
            var next = s.Bytes(end, 2048);
            string t = Encoding.Latin1.GetString(next);
            if (t.Contains(" obj") || t.Contains("xref") || t.Contains("startxref")) { p = end; continue; }
            break;
        }
        return end > 0 ? end - start : -1;
    }

    // ───────────── ZIP / DOCX / XLSX / PPTX ─────────────
    private static readonly byte[] Eocd = { 0x50, 0x4B, 0x05, 0x06 };
    private static CarvedFile? Zip(ByteSource s, long start, long limit)
    {
        long p = start;
        while (true)
        {
            long e = s.IndexOf(Eocd, p + 4, limit);
            if (e < 0) return null;
            long cdSize = s.U32LE(e + 12), cdOff = s.U32LE(e + 16);
            uint comment = s.U16LE(e + 20);
            if (cdOff + cdSize == e - start)
            {
                long len = e + 22 + comment - start;
                return new CarvedFile(ZipKind(s, start + cdOff, (int)Math.Min(cdSize, 4 << 20)), len, false);
            }
            p = e;
        }
    }

    private static string ZipKind(ByteSource s, long cd, int len)
    {
        string names = Encoding.Latin1.GetString(s.Bytes(cd, len));
        if (names.Contains("word/")) return "docx";
        if (names.Contains("xl/")) return "xlsx";
        if (names.Contains("ppt/")) return "pptx";
        if (names.Contains("AndroidManifest.xml")) return "apk";
        if (names.Contains("META-INF/MANIFEST.MF")) return "jar";
        if (names.Contains("content.xml") && names.Contains("mimetype")) return "odt";
        if (names.Contains("doc.kml")) return "kmz";
        return "zip";
    }

    // ───────────── Office القديم (OLE) ─────────────
    private static CarvedFile? Ole(ByteSource s, long start, long limit)
    {
        int shift = (int)s.U16LE(start + 30);
        if (shift is not (9 or 12)) return null;
        int ss = 1 << shift;
        int perSector = ss / 4;
        uint numFat = s.U32LE(start + 44);
        if (numFat == 0 || numFat > 100000) return null;
        long SectorPos(uint id) => start + ss + (long)id * ss;

        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < numFat; i++)
        {
            uint id = s.U32LE(start + 76 + i * 4);
            if (id >= 0xFFFFFFFA) break;
            fatSectors.Add(id);
        }
        uint difat = s.U32LE(start + 68);
        for (int guard = 0; fatSectors.Count < numFat && difat < 0xFFFFFFFA && guard < 10000; guard++)
        {
            for (int i = 0; i < perSector - 1 && fatSectors.Count < numFat; i++) fatSectors.Add(s.U32LE(SectorPos(difat) + i * 4));
            difat = s.U32LE(SectorPos(difat) + (perSector - 1) * 4);
        }

        long maxUsed = -1, index = 0;
        foreach (var fs in fatSectors)
        {
            if (SectorPos(fs) + ss > limit) return null;
            var b = s.Bytes(SectorPos(fs), ss);
            for (int i = 0; i < perSector; i++, index++)
                if (BitConverter.ToUInt32(b, i * 4) != 0xFFFFFFFF) maxUsed = index;
        }
        if (maxUsed < 0) return null;
        long len = (long)ss * (maxUsed + 2);
        if (start + len > limit) return null;

        // نوع الملف من أسماء الأقسام بداخله
        uint dir = s.U32LE(start + 48);
        string names = dir < 0xFFFFFFFA ? Encoding.Unicode.GetString(s.Bytes(SectorPos(dir), Math.Min(ss * 4, 8192))) : "";
        string ext = names.Contains("WordDocument") ? "doc"
                   : names.Contains("Workbook") || names.Contains("Book") ? "xls"
                   : names.Contains("PowerPoint Document") ? "ppt"
                   : names.Contains("__substg1.0") ? "msg"
                   : names.Contains("VisioDocument") ? "vsd"
                   : "doc";
        return new CarvedFile(ext, len, false);
    }

    // ───────────── MP4 / MOV / HEIC ─────────────
    private static readonly HashSet<string> Boxes = new()
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pnot", "pdin", "moof", "mfra",
        "sidx", "styp", "emsg", "prft", "udta", "junk", "PICT", "iinf", "iloc", "idat",
    };
    private static CarvedFile? IsoBmff(ByteSource s, long start, long limit)
    {
        string brand = Encoding.ASCII.GetString(s.Bytes(start + 8, 4));
        string ext = brand switch
        {
            "qt  " => "mov", "M4A " or "M4B " => "m4a", "M4V " => "m4v",
            "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" => "heic",
            "avif" => "avif", "crx " => "cr3",
            _ when brand.StartsWith("3g") => "3gp",
            _ => "mp4",
        };
        long p = start;
        bool moov = false, mdat = false;
        while (p + 8 <= limit)
        {
            long size = s.U32BE(p);
            string type = Encoding.ASCII.GetString(s.Bytes(p + 4, 4));
            if (!Boxes.Contains(type)) break;
            if (size == 1) size = (long)s.U64BE(p + 8);
            else if (size == 0) return null;
            if (size < 8 || p + size > limit) return null;
            if (type == "moov" || type == "meta") moov = true;
            if (type == "mdat") mdat = true;
            p += size;
        }
        if (!moov && !mdat) return null;
        return new CarvedFile(ext, p - start, false);
    }

    // ───────────── AVI / WAV / WEBP ─────────────
    private static CarvedFile? Riff(ByteSource s, long start, long limit)
    {
        long size = s.U32LE(start + 4) + 8L;
        string type = Encoding.ASCII.GetString(s.Bytes(start + 8, 4));
        string? ext = type switch { "AVI " => "avi", "WAVE" => "wav", "WEBP" => "webp", "RMID" => "mid", "CDXA" => "dat", _ => null };
        if (ext == null || size < 44 || start + size > limit) return null;
        return new CarvedFile(ext, size, false);
    }

    // ───────────── MP3 ─────────────
    private static readonly int[] BitratesV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private static readonly int[] BitratesV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    private static long Mp3(ByteSource s, long start, long limit)
    {
        var id3 = s.Bytes(start + 6, 4);
        long tag = ((id3[0] & 0x7F) << 21) | ((id3[1] & 0x7F) << 14) | ((id3[2] & 0x7F) << 7) | (id3[3] & 0x7F);
        long p = start + 10 + tag + ((s.U8(start + 5) & 0x10) != 0 ? 10 : 0);
        int frames = 0;
        while (p + 4 <= limit)
        {
            uint h = s.U32BE(p);
            if ((h & 0xFFE00000) != 0xFFE00000) break;
            int ver = (int)((h >> 19) & 3), layer = (int)((h >> 17) & 3), br = (int)((h >> 12) & 0xF), sr = (int)((h >> 10) & 3), pad = (int)((h >> 9) & 1);
            if (ver == 1 || layer != 1 || br == 0 || br == 15 || sr == 3) break; // نقبل Layer III فقط
            int[] srs = ver == 3 ? new[] { 44100, 48000, 32000 } : ver == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
            int bitrate = (ver == 3 ? BitratesV1L3[br] : BitratesV2L3[br]) * 1000;
            int len = (ver == 3 ? 144 : 72) * bitrate / srs[sr] + pad;
            if (len < 24) break;
            p += len;
            frames++;
        }
        if (frames < 10) return -1;
        if (p + 128 <= limit && s.U8(p) == 'T' && s.U8(p + 1) == 'A' && s.U8(p + 2) == 'G') p += 128;
        return p - start;
    }

    // ───────────── 7z ─────────────
    private static long SevenZip(ByteSource s, long start, long limit)
    {
        long off = (long)s.U64LE(start + 12), size = (long)s.U64LE(start + 20);
        if (off < 0 || size <= 0 || size > (1L << 30)) return -1;
        long len = 32 + off + size;
        return start + len <= limit ? len : -1;
    }

    // ───────────── RAR (4 و 5) ─────────────
    private static long Rar(ByteSource s, long start, long limit)
    {
        if (s.U8(start + 6) == 0x01 && s.U8(start + 7) == 0x00) return Rar5(s, start, limit);
        if (s.U8(start + 6) != 0x00) return -1;
        long p = start + 7;
        bool files = false;
        for (int guard = 0; guard < 1_000_000 && p + 7 <= limit; guard++)
        {
            int type = s.U8(p + 2);
            uint flags = s.U16LE(p + 3);
            uint hsize = s.U16LE(p + 5);
            // بعض الأرشيفات القديمة بدون كتلة نهاية: ننهي عند أول كتلة غير صالحة
            if (hsize < 7 || type < 0x72 || type > 0x7B) return files ? p - start : -1;
            if (type == 0x74) files = true;
            long add = (flags & 0x8000) != 0 || type == 0x74 ? s.U32LE(p + 7) : 0;
            if (type == 0x74 && (flags & 0x100) != 0) add |= (long)s.U32LE(p + 32) << 32;
            p += hsize + add;
            if (type == 0x7B) return p - start;
        }
        return -1;
    }

    private static long Rar5(ByteSource s, long start, long limit)
    {
        long p = start + 8;
        (long Value, long Next) VInt(long q)
        {
            long v = 0; int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                int b = s.U8(q++);
                if (b < 0) return (-1, q);
                v |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return (v, q);
                shift += 7;
            }
            return (-1, q);
        }
        for (int guard = 0; guard < 1_000_000 && p + 7 <= limit; guard++)
        {
            var (hsize, q) = VInt(p + 4);
            if (hsize <= 0 || hsize > 2 << 20) return -1;
            long headerEnd = q + hsize;
            var (type, q2) = VInt(q);
            var (flags, q3) = VInt(q2);
            long data = 0;
            if ((flags & 1) != 0) { var (_, q4) = VInt(q3); q3 = q4; }
            if ((flags & 2) != 0) data = VInt(q3).Value;
            if (type is < 1 or > 5 || data < 0) return -1;
            p = headerEnd + data;
            if (type == 5) return p - start;
        }
        return -1;
    }

    // ───────────── DWG (حجم تقديري) ─────────────
    private static CarvedFile? Dwg(ByteSource s, long start, long limit)
    {
        // DWG لا يحتوي على حجمه بشكل مباشر — نقرأ حتى بداية ملف آخر أو منطقة أصفار طويلة
        const int step = 4096;
        long p = start + step;
        var buf = new byte[step];
        long max = Math.Min(limit, start + (300L << 20));
        long lastData = p;
        while (p + step <= max)
        {
            s.Read(p, buf);
            if (Match(buf) != null) break;
            if (buf.All(b => b == 0)) { if (p - lastData > 64 * 1024) break; }
            else lastData = p + step;
            p += step;
        }
        long len = lastData - start;
        return len > 1024 ? new CarvedFile("dwg", len, true) : null;
    }
}
