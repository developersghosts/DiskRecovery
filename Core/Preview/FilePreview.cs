using System.Text;
using System.Text.RegularExpressions;

namespace DiskRecovery.Core.Preview;

public enum PreviewKind { None, Image, Text, Hex, Folder }

public sealed class PreviewData
{
    public PreviewKind Kind { get; init; }
    public byte[]? ImageBytes { get; init; }
    public string Text { get; init; } = "";
    public string Info { get; init; } = "";
}

/// <summary>يجهّز معاينة الملف المحذوف قبل استرجاعه (صورة / نص / عرض سداسي).</summary>
public static class FilePreview
{
    private const int MaxImageBytes = 40 * 1024 * 1024;
    private const int MaxTextBytes = 64 * 1024;
    private const int HexBytes = 4096;

    public static PreviewData Build(ScanResult scan, DeletedItem item)
    {
        if (item.IsDirectory)
        {
            int kids = scan.ChildrenOf.TryGetValue(item.Id, out var k) ? k.Count : 0;
            return new PreviewData { Kind = PreviewKind.Folder, Info = $"مجلد محذوف يحتوي على {kids:N0} عنصر محذوف" };
        }
        if (item.Data == null || item.Data.Length == 0)
            return new PreviewData { Kind = PreviewKind.None, Info = "لا توجد بيانات للمعاينة" };

        string ext = item.Extension;
        var data = item.Data;

        if (FileCategories.IsImageExtension(ext) && data.Length <= MaxImageBytes)
        {
            var bytes = data.ReadHead(scan.Reader, (int)data.Length);
            return new PreviewData { Kind = PreviewKind.Image, ImageBytes = bytes, Info = DescribeHeader(bytes, ext) };
        }

        var head = data.ReadHead(scan.Reader, Math.Max(MaxTextBytes, HexBytes));
        if (head.All(b => b == 0))
            return new PreviewData { Kind = PreviewKind.Hex, Text = Hex(head, HexBytes), Info = "⚠ البيانات كلها أصفار — غالباً تمت الكتابة فوق الملف" };

        if (ext == "pdf")
            return new PreviewData { Kind = PreviewKind.Text, Text = PdfSummary(scan, item), Info = DescribeHeader(head, ext) };

        if (FileCategories.IsTextExtension(ext) || LooksLikeText(head))
            return new PreviewData { Kind = PreviewKind.Text, Text = DecodeText(head), Info = DescribeHeader(head, ext) };

        return new PreviewData { Kind = PreviewKind.Hex, Text = Hex(head, HexBytes), Info = DescribeHeader(head, ext) };
    }

    /// <summary>يتأكد إن أول بايتات الملف تطابق نوعه المتوقع.</summary>
    public static string DescribeHeader(byte[] b, string ext)
    {
        string? detected = Detect(b);
        if (detected == null) return "لم يتم التعرف على نوع الملف من محتواه";
        bool match = ext switch
        {
            "jpg" or "jpeg" => detected == "JPG",
            "docx" or "xlsx" or "pptx" or "zip" or "xlsm" => detected == "ZIP / Office",
            "doc" or "xls" or "ppt" or "msg" => detected == "Office قديم (OLE)",
            "mp4" or "mov" or "m4a" or "3gp" or "heic" => detected == "فيديو MP4/MOV",
            "txt" or "csv" or "log" or "xml" or "html" or "json" => detected == "نص",
            _ => detected.Equals(ext, StringComparison.OrdinalIgnoreCase),
        };
        return match ? $"✔ المحتوى يطابق نوع الملف ({detected})" : $"المحتوى من نوع: {detected}";
    }

    public static string? Detect(byte[] b)
    {
        bool S(params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);
        if (S(0xFF, 0xD8, 0xFF)) return "JPG";
        if (S(0x89, 0x50, 0x4E, 0x47)) return "PNG";
        if (S((byte)'G', (byte)'I', (byte)'F', (byte)'8')) return "GIF";
        if (S((byte)'%', (byte)'P', (byte)'D', (byte)'F')) return "PDF";
        if (S(0x50, 0x4B, 0x03, 0x04)) return "ZIP / Office";
        if (S(0xD0, 0xCF, 0x11, 0xE0)) return "Office قديم (OLE)";
        if (S((byte)'R', (byte)'a', (byte)'r', (byte)'!')) return "RAR";
        if (S(0x37, 0x7A, 0xBC, 0xAF)) return "7Z";
        if (S((byte)'A', (byte)'C', (byte)'1', (byte)'0')) return "DWG";
        if (S((byte)'B', (byte)'M')) return "BMP";
        if (S((byte)'R', (byte)'I', (byte)'F', (byte)'F')) return "RIFF (AVI/WAV)";
        if (S((byte)'I', (byte)'D', (byte)'3')) return "MP3";
        if (b.Length >= 8 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return "فيديو MP4/MOV";
        if (LooksLikeText(b)) return "نص";
        return null;
    }

    private static bool LooksLikeText(byte[] b)
    {
        int n = Math.Min(b.Length, 4096), bad = 0, zero = 0;
        if (n == 0) return false;
        if (n >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return true;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 0) zero++;
            else if (c < 9 || (c > 13 && c < 32)) bad++;
        }
        return zero == 0 && bad * 50 < n;
    }

    private static string DecodeText(byte[] b)
    {
        int end = b.Length;
        while (end > 0 && b[end - 1] == 0) end--;
        if (end >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, end - 2);
        if (end >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, end - 2);
        var utf8 = new UTF8Encoding(false, true);
        try { return utf8.GetString(b, 0, end); }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(1256).GetString(b, 0, end); } // عربي ويندوز
            catch { return Encoding.UTF8.GetString(b, 0, end); }
        }
    }

    private static string PdfSummary(ScanResult scan, DeletedItem item)
    {
        var data = item.Data!;
        int n = (int)Math.Min(data.Length, 8 * 1024 * 1024);
        var bytes = data.ReadHead(scan.Reader, n);
        string s = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder();
        var ver = Regex.Match(s, @"%PDF-(\d\.\d)");
        sb.AppendLine("ملف PDF" + (ver.Success ? " إصدار " + ver.Groups[1].Value : ""));
        int pages = Regex.Matches(s, @"/Type\s*/Page[^s]").Count;
        if (pages > 0) sb.AppendLine($"عدد الصفحات (تقريبي): {pages}" + (n < data.Length ? "+" : ""));
        foreach (var key in new[] { "Title", "Author", "Creator", "Producer", "CreationDate" })
        {
            var m = Regex.Match(s, "/" + key + @"\s*\(([^)]{1,200})\)");
            if (m.Success) sb.AppendLine($"{key}: {m.Groups[1].Value}");
        }
        sb.AppendLine(s.Contains("%%EOF") ? "✔ نهاية الملف موجودة" : "⚠ نهاية الملف غير موجودة في الجزء المقروء");
        return sb.ToString();
    }

    public static string Hex(byte[] b, int max)
    {
        int n = Math.Min(b.Length, max);
        var sb = new StringBuilder(n * 4);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
                sb.Append(i + j < n ? b[i + j].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < n; j++)
            {
                byte c = b[i + j];
                sb.Append(c is >= 32 and < 127 ? (char)c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
