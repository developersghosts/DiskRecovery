using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskRecovery.Core.Health;

public sealed class SpeedResult
{
    public double SeqReadStart, SeqReadMiddle, SeqReadEnd;  // MB/s
    public double RandomReadIops, RandomReadLatencyMs;
    public double? SeqWrite, SeqReadFile;                    // MB/s (لو تم اختبار الكتابة)
    public double? RandomWriteIops;
    public string Notes = "";
}

/// <summary>اختبار سرعة الهارد. القراءة من القرص مباشرة (آمنة)، والكتابة في ملف مؤقت يُحذف بعد الاختبار.</summary>
public static class SpeedTest
{
    private const int SeqBlock = 4 << 20;

    public static SpeedResult Run(IBlockDevice dev, long offset, long length, string? writeTestFolder,
                                  IProgress<string>? progress, CancellationToken ct)
    {
        var r = new SpeedResult();
        long sample = Math.Min(256L << 20, length / 4);

        progress?.Report("قراءة متتالية من بداية القرص...");
        r.SeqReadStart = Sequential(dev, offset, sample, ct);
        progress?.Report("قراءة متتالية من منتصف القرص...");
        r.SeqReadMiddle = Sequential(dev, offset + AlignDown(length / 2, dev.SectorSize), sample, ct);
        progress?.Report("قراءة متتالية من نهاية القرص...");
        r.SeqReadEnd = Sequential(dev, offset + AlignDown(length - sample, dev.SectorSize), sample, ct);

        progress?.Report("قراءة عشوائية 4K (10 ثواني)...");
        (r.RandomReadIops, r.RandomReadLatencyMs) = RandomRead(dev, offset, length, TimeSpan.FromSeconds(10), ct);

        if (writeTestFolder != null)
        {
            progress?.Report("اختبار الكتابة في ملف مؤقت...");
            try { WriteTest(writeTestFolder, r, ct); }
            catch (Exception ex) { r.Notes = "تعذر اختبار الكتابة: " + ex.Message; }
        }
        progress?.Report("تم");
        return r;
    }

    private static long AlignDown(long v, int a) => v / a * a;

    private static double Sequential(IBlockDevice dev, long start, long bytes, CancellationToken ct)
    {
        var buf = new byte[SeqBlock];
        var sw = Stopwatch.StartNew();
        long done = 0;
        while (done < bytes)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(SeqBlock, bytes - done);
            n = n / dev.SectorSize * dev.SectorSize;
            if (n <= 0) break;
            dev.ReadAligned(start + done, buf, n);
            done += n;
        }
        return done / Math.Max(0.001, sw.Elapsed.TotalSeconds) / (1 << 20);
    }

    private static (double Iops, double LatencyMs) RandomRead(IBlockDevice dev, long start, long length, TimeSpan duration, CancellationToken ct)
    {
        int size = Math.Max(4096, dev.SectorSize);
        var buf = new byte[size];
        var rnd = new Random(1234);
        long slots = Math.Max(1, length / size - 1);
        var sw = Stopwatch.StartNew();
        int ops = 0;
        while (sw.Elapsed < duration)
        {
            ct.ThrowIfCancellationRequested();
            long pos = start + rnd.NextInt64(slots) * size;
            dev.ReadAligned(pos, buf, size);
            ops++;
        }
        double secs = sw.Elapsed.TotalSeconds;
        return (ops / secs, secs * 1000 / Math.Max(1, ops));
    }

    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);

    /// <summary>كتابة وقراءة ملف مؤقت بدون كاش ويندوز للحصول على السرعة الحقيقية.</summary>
    private static unsafe void WriteTest(string folder, SpeedResult r, CancellationToken ct)
    {
        string path = Path.Combine(folder, $"__speedtest_{Guid.NewGuid():N}.tmp");
        long free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        long size = Math.Min(512L << 20, free / 4);
        size = size / SeqBlock * SeqBlock;
        if (size < SeqBlock) throw new IOException("المساحة الفارغة غير كافية");

        void* mem = NativeMemory.AlignedAlloc(SeqBlock, 4096);
        try
        {
            var span = new Span<byte>(mem, SeqBlock);
            Random.Shared.NextBytes(span);

            using (var h = CreateFile(path, 0x40000000 | 0x80000000, 0, IntPtr.Zero, 2 /*CREATE_ALWAYS*/,
                                      FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | 0x04000000 /*DELETE_ON_CLOSE*/, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new IOException("تعذر إنشاء ملف الاختبار");
                var sw = Stopwatch.StartNew();
                for (long o = 0; o < size; o += SeqBlock)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, span, o);
                }
                r.SeqWrite = size / sw.Elapsed.TotalSeconds / (1 << 20);

                sw.Restart();
                for (long o = 0; o < size; o += SeqBlock) RandomAccess.Read(h, span, o);
                r.SeqReadFile = size / sw.Elapsed.TotalSeconds / (1 << 20);

                // كتابة عشوائية 4K لمدة 5 ثواني
                var small = span[..4096];
                long slots = size / 4096;
                int ops = 0;
                sw.Restart();
                while (sw.Elapsed.TotalSeconds < 5)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, small, Random.Shared.NextInt64(slots) * 4096);
                    ops++;
                }
                r.RandomWriteIops = ops / sw.Elapsed.TotalSeconds;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
