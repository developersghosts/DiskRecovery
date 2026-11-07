using System.Diagnostics;

namespace DiskRecovery.Core.Health;

public enum BlockState : byte { NotScanned = 0, Fast = 1, Normal = 2, Slow = 3, VerySlow = 4, Bad = 5 }

public sealed class SurfaceResult
{
    public int BlockSize { get; init; }
    public long Length { get; init; }
    public BlockState[] Blocks { get; init; } = Array.Empty<BlockState>();
    public long BadSectors;
    public List<long> BadOffsets { get; } = new();
    public long Scanned;
    public double AvgSpeedMBs;

    public long Count(BlockState s) => Blocks.LongCount(b => b == s);
}

/// <summary>فحص سطح القرص: يقرأ كل منطقة ويقيس زمن القراءة ليكتشف القطاعات الضعيفة والتالفة. قراءة فقط.</summary>
public static class SurfaceScanner
{
    public const int BlockSize = 1 << 20;

    public static SurfaceResult CreateResult(long length) => new()
    {
        BlockSize = BlockSize, Length = length,
        Blocks = new BlockState[(length + BlockSize - 1) / BlockSize],
    };

    /// <summary>الحدود الزمنية بالمللي ثانية لقراءة 1MB.</summary>
    public static BlockState Classify(double ms) => ms switch
    {
        < 20 => BlockState.Fast,
        < 80 => BlockState.Normal,
        < 300 => BlockState.Slow,
        _ => BlockState.VerySlow,
    };

    public static void Scan(IBlockDevice dev, long offset, SurfaceResult result, IProgress<SurfaceResult>? progress, CancellationToken ct)
    {
        int ss = dev.SectorSize;
        var buf = new byte[BlockSize];
        var total = Stopwatch.StartNew();
        var sw = new Stopwatch();
        var lastReport = Stopwatch.StartNew();

        for (long i = 0; i < result.Blocks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            long pos = i * BlockSize;
            int n = (int)Math.Min(BlockSize, result.Length - pos);
            int aligned = (n + ss - 1) / ss * ss;
            sw.Restart();
            bool ok;
            try { ok = dev.ReadAligned(offset + pos, buf, aligned) >= n; } catch { ok = false; }
            sw.Stop();

            if (ok) result.Blocks[i] = Classify(sw.Elapsed.TotalMilliseconds * BlockSize / Math.Max(n, 1));
            else
            {
                result.Blocks[i] = BlockState.Bad;
                // نحدد القطاعات التالفة بالضبط داخل هذه المنطقة
                var sector = new byte[ss];
                for (int o = 0; o < n; o += ss)
                {
                    bool good;
                    try { good = dev.ReadAligned(offset + pos + o, sector, ss) == ss; } catch { good = false; }
                    if (!good) { result.BadSectors++; if (result.BadOffsets.Count < 100000) result.BadOffsets.Add(offset + pos + o); }
                }
            }
            result.Scanned = pos + n;
            result.AvgSpeedMBs = result.Scanned / Math.Max(0.001, total.Elapsed.TotalSeconds) / (1 << 20);
            if (lastReport.ElapsedMilliseconds > 200) { progress?.Report(result); lastReport.Restart(); }
        }
        progress?.Report(result);
    }
}
