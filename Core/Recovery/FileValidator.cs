using System.Buffers.Binary;
using System.Text;

namespace DiskRecovery.Core.Recovery;

public enum ValidationState { Valid, Damaged, Unknown }

public readonly record struct ValidationResult(ValidationState State, string Reason)
{
    public static ValidationResult Ok => new(ValidationState.Valid, "سليم");
    public static ValidationResult Unknown => new(ValidationState.Unknown, "");
    public static ValidationResult Bad(string why) => new(ValidationState.Damaged, why);
}

/// <summary>يتأكد بعد الاسترجاع إن الملف بنيته سليمة (بداية ونهاية صحيحة حسب نوعه).</summary>
public static class FileValidator
{
    public static ValidationResult Check(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long len = fs.Length;
            if (len == 0) return ValidationResult.Unknown;
            int headLen = (int)Math.Min(65536, len);
            var head = new byte[headLen];
            fs.ReadExactly(head);
            int tailLen = (int)Math.Min(65536 + 22, len);
            var tail = new byte[tailLen];
            fs.Position = len - tailLen;
            fs.ReadExactly(tail);
            return Check(Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), head, tail, len, fs);
        }
        catch (Exception ex) { return ValidationResult.Bad(ex.Message); }
    }

    public static ValidationResult Check(string ext, byte[] head, byte[] tail, long length, Stream? full = null)
    {
        if (IsAllZero(head) && IsAllZero(tail)) return ValidationResult.Bad("محتواه أصفار فقط");

        switch (ext)
        {
            case "jpg" or "jpeg":
                if (!StartsWith(head, 0xFF, 0xD8, 0xFF)) return ValidationResult.Bad("بداية JPG غير صحيحة");
                return IndexOfLast(tail, new byte[] { 0xFF, 0xD9 }, 4096) >= 0 ? ValidationResult.Ok : ValidationResult.Bad("الصورة ناقصة (نهاية JPG غير موجودة)");
            case "png":
                if (!StartsWith(head, 0x89, 0x50, 0x4E, 0x47)) return ValidationResult.Bad("بداية PNG غير صحيحة");
                return IndexOfLast(tail, "IEND"u8.ToArray(), 64) >= 0 ? ValidationResult.Ok : ValidationResult.Bad("الصورة ناقصة");
            case "gif":
                if (!StartsWith(head, "GIF8"u8.ToArray())) return ValidationResult.Bad("بداية GIF غير صحيحة");
                return tail[^1] == 0x3B || IndexOfLast(tail, new byte[] { 0x3B }, 512) >= 0 ? ValidationResult.Ok : ValidationResult.Bad("الصورة ناقصة");
            case "bmp":
                if (!StartsWith(head, "BM"u8.ToArray()) || head.Length < 6) return ValidationResult.Bad("بداية BMP غير صحيحة");
                return BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(2)) <= length ? ValidationResult.Ok : ValidationResult.Bad("الصورة ناقصة");
            case "pdf":
                if (IndexOf(head, "%PDF"u8.ToArray(), 1024) < 0) return ValidationResult.Bad("بداية PDF غير صحيحة");
                return IndexOfLast(tail, "%%EOF"u8.ToArray(), 4096) >= 0 ? ValidationResult.Ok : ValidationResult.Bad("الملف ناقص (لا توجد نهاية PDF)");
            case "zip" or "docx" or "xlsx" or "xlsm" or "pptx" or "odt" or "ods" or "odp" or "jar" or "apk":
                if (!StartsWith(head, 0x50, 0x4B, 0x03, 0x04)) return ValidationResult.Bad("بداية الملف غير صحيحة");
                return IndexOfLast(tail, new byte[] { 0x50, 0x4B, 0x05, 0x06 }, tail.Length) >= 0 ? ValidationResult.Ok : ValidationResult.Bad("الملف ناقص (فهرس ZIP مفقود)");
            case "doc" or "xls" or "ppt" or "msg":
                return StartsWith(head, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1) ? ValidationResult.Ok : ValidationResult.Bad("بداية ملف Office غير صحيحة");
            case "7z":
                return StartsWith(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C) ? ValidationResult.Ok : ValidationResult.Bad("بداية 7z غير صحيحة");
            case "rar":
                return StartsWith(head, "Rar!"u8.ToArray()) ? ValidationResult.Ok : ValidationResult.Bad("بداية RAR غير صحيحة");
            case "dwg":
                return StartsWith(head, "AC10"u8.ToArray()) ? ValidationResult.Ok : ValidationResult.Bad("بداية DWG غير صحيحة");
            case "mp4" or "mov" or "m4a" or "m4v" or "3gp" or "heic":
                return CheckIsoBmff(full, length);
            case "avi" or "wav" or "webp":
                if (!StartsWith(head, "RIFF"u8.ToArray()) || head.Length < 8) return ValidationResult.Bad("بداية الملف غير صحيحة");
                return BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L <= length ? ValidationResult.Ok : ValidationResult.Bad("الملف ناقص");
            default:
                return ValidationResult.Unknown;
        }
    }

    private static ValidationResult CheckIsoBmff(Stream? s, long length)
    {
        if (s == null) return ValidationResult.Unknown;
        long pos = 0;
        var h = new byte[16];
        bool moov = false;
        while (pos + 8 <= length)
        {
            s.Position = pos;
            if (s.Read(h, 0, 16) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
            else if (size == 0) size = length - pos;
            if (size < 8) return ValidationResult.Bad("بنية الفيديو تالفة");
            if (type == "moov") moov = true;
            pos += size;
        }
        if (pos > length) return ValidationResult.Bad("الفيديو ناقص");
        return moov ? ValidationResult.Ok : ValidationResult.Bad("فهرس الفيديو (moov) مفقود");
    }

    private static bool IsAllZero(byte[] b)
    {
        foreach (var x in b) if (x != 0) return false;
        return true;
    }

    private static bool StartsWith(byte[] b, params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);

    private static int IndexOf(byte[] b, byte[] sig, int within) =>
        b.AsSpan(0, Math.Min(within, b.Length)).IndexOf(sig);

    private static int IndexOfLast(byte[] b, byte[] sig, int lastBytes)
    {
        int start = Math.Max(0, b.Length - lastBytes);
        int i = b.AsSpan(start).LastIndexOf(sig);
        return i < 0 ? -1 : start + i;
    }
}
