using System.Diagnostics;

namespace DiskRecovery.Core.Imaging;

public sealed class ImageProgress
{
    public long Done;
    public long Total;
    public long BadSectors;
    public double SpeedMBs;
    public TimeSpan Eta;
    public string Stage = "";
}

public sealed class ImageReport
{
    public long BytesCopied;
    public long BadSectors;
    public List<long> BadOffsets { get; } = new();
    public string? BadSectorLog;
}

/// <summary>
/// نسخة كاملة من هارد/بارتشن لملف (.img). مصمم للهاردات الضعيفة:
/// المرحلة الأولى تنسخ بسرعة وتتخطى المناطق التالفة، والثانية ترجع لها قطاع بقطاع.
/// القرص الأصلي يُقرأ فقط، والملف الناتج يمكن فحصه واسترجاع الملفات منه.
/// </summary>
public static class DiskImager
{
    private const int Block = 1 << 20;
    private const int Retries = 2;

    public static string ProgressFile(string dest) => dest + ".progress";

    /// <summary>موضع الاستكمال لو كانت هناك عملية سابقة لم تكتمل (0 لو لا يوجد).</summary>
    public static long GetResumeOffset(string dest, long length)
    {
        try
        {
            string pf = ProgressFile(dest);
            if (!File.Exists(pf) || !File.Exists(dest) || new FileInfo(dest).Length != length) return 0;
            return long.TryParse(File.ReadAllText(pf).Trim(), out long v) && v > 0 && v < length ? v : 0;
        }
        catch { return 0; }
    }

    public static ImageReport Create(IBlockDevice dev, long offset, long length, string dest, long resumeFrom,
                                     IProgress<ImageProgress>? progress, CancellationToken ct)
    {
        int ss = dev.SectorSize;
        var report = new ImageReport();
        var badBlocks = new List<long>();
        var prog = new ImageProgress { Total = length, Stage = "المرحلة 1: نسخ سريع" };
        var buf = new byte[Block];
        var zero = new byte[Block];
        var sw = Stopwatch.StartNew();
        long lastSave = 0, startDone = resumeFrom;

        using (var fs = new FileStream(dest, resumeFrom > 0 ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
        {
            if (resumeFrom == 0) fs.SetLength(length);

            for (long pos = resumeFrom; pos < length; pos += Block)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Block, length - pos);
                int aligned = (n + ss - 1) / ss * ss;
                bool ok;
                try { ok = dev.ReadAligned(offset + pos, buf, aligned) >= n; }
                catch { ok = false; }

                fs.Position = pos;
                if (ok) fs.Write(buf, 0, n);
                else { fs.Write(zero, 0, n); badBlocks.Add(pos); }

                prog.Done = pos + n;
                Update(prog, sw, startDone);
                prog.BadSectors = badBlocks.Count * (long)(Block / ss);
                progress?.Report(prog);

                if (prog.Done - lastSave >= 256L << 20)
                {
                    fs.Flush();
                    File.WriteAllText(ProgressFile(dest), prog.Done.ToString());
                    lastSave = prog.Done;
                }
            }

            // المرحلة 2: المناطق اللي فشلت نقرأها قطاع بقطاع ونحاول أكثر من مرة
            if (badBlocks.Count > 0)
            {
                prog.Stage = "المرحلة 2: محاولة قراءة المناطق التالفة قطاع بقطاع";
                var sector = new byte[ss];
                long totalRetry = badBlocks.Count * (long)Block, retried = 0;
                foreach (var b in badBlocks)
                {
                    int n = (int)Math.Min(Block, length - b);
                    for (int o = 0; o < n; o += ss)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool got = false;
                        for (int t = 0; t <= Retries && !got; t++)
                        {
                            try { got = dev.ReadAligned(offset + b + o, sector, ss) == ss; } catch { got = false; }
                        }
                        if (got) { fs.Position = b + o; fs.Write(sector, 0, Math.Min(ss, n - o)); }
                        else { report.BadSectors++; report.BadOffsets.Add(offset + b + o); }
                        retried += ss;
                        prog.Done = length - totalRetry + retried;
                        prog.BadSectors = report.BadSectors;
                        progress?.Report(prog);
                    }
                }
            }
            fs.Flush(true);
        }

        try { File.Delete(ProgressFile(dest)); } catch { }
        report.BytesCopied = length;
        if (report.BadOffsets.Count > 0)
        {
            report.BadSectorLog = dest + ".bad-sectors.txt";
            File.WriteAllLines(report.BadSectorLog,
                new[] { $"قطاعات تعذرت قراءتها: {report.BadSectors} (حجم القطاع {ss} بايت). تم وضع أصفار مكانها في ملف النسخة.", "الموضع على القرص (بايت):" }
                .Concat(report.BadOffsets.Select(o => o.ToString())));
        }
        return report;
    }

    private static void Update(ImageProgress p, Stopwatch sw, long startDone)
    {
        double secs = sw.Elapsed.TotalSeconds;
        if (secs < 0.5) return;
        p.SpeedMBs = (p.Done - startDone) / secs / (1 << 20);
        p.Eta = TimeSpan.FromSeconds((p.Total - p.Done) / Math.Max(1, p.SpeedMBs * (1 << 20)));
    }
}
