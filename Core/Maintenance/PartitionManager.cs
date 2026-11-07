using System.Text;

namespace DiskRecovery.Core.Maintenance;

/// <summary>إدارة البارتشنات عبر أوامر ويندوز (Storage cmdlets): حرف، تكبير/تصغير، إنشاء، حذف.</summary>
public static class PartitionManager
{
    private static string Select(PartitionInfo p) =>
        $"$p = Get-Partition -DiskNumber {p.DiskNumber} | Where-Object {{ $_.Offset -eq {p.Offset} }};" +
        "if (-not $p) { throw 'لم يتم العثور على البارتشن' };";

    private static Task<int> Run(string script, Action<string> log, CancellationToken ct) =>
        VolumeTools.RunPowerShellAsync("$ErrorActionPreference='Stop';" + script, log, ct);

    /// <summary>يعطي البارتشن حرفاً (حرف محدد أو أول حرف متاح).</summary>
    public static Task<int> AssignLetterAsync(PartitionInfo p, char? letter, Action<string> log, CancellationToken ct)
    {
        string cmd = letter is { } l
            ? $"$p | Set-Partition -NewDriveLetter {char.ToUpperInvariant(l)};"
            : "$p | Add-PartitionAccessPath -AssignDriveLetter;";
        return Run(Select(p) + cmd + "'الحرف الجديد: ' + (Get-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber).DriveLetter + ':'", log, ct);
    }

    public static Task<int> RemoveLetterAsync(PartitionInfo p, Action<string> log, CancellationToken ct)
    {
        if (p.DriveLetter == null) throw new InvalidOperationException("البارتشن ليس له حرف.");
        string letter = p.DriveLetter.TrimEnd(':');
        return Run(Select(p) + $"$p | Remove-PartitionAccessPath -AccessPath '{letter}:\\';'تم إخفاء البارتشن'", log, ct);
    }

    /// <summary>أصغر وأكبر حجم مسموح للبارتشن.</summary>
    public static async Task<(long Min, long Max)?> GetSupportedSizeAsync(PartitionInfo p, CancellationToken ct)
    {
        long? min = null, max = null;
        await Run(Select(p) + "$s = $p | Get-PartitionSupportedSize; 'MIN=' + $s.SizeMin; 'MAX=' + $s.SizeMax", line =>
        {
            if (line.StartsWith("MIN=") && long.TryParse(line[4..], out long a)) min = a;
            if (line.StartsWith("MAX=") && long.TryParse(line[4..], out long b)) max = b;
        }, ct);
        return min != null && max != null ? (min.Value, max.Value) : null;
    }

    public static Task<int> ResizeAsync(PartitionInfo p, long newSize, Action<string> log, CancellationToken ct) =>
        Run(Select(p) + $"$p | Resize-Partition -Size {newSize};'الحجم الجديد: ' + [math]::Round({newSize}/1GB,2) + ' GB'", log, ct);

    public static Task<int> DeleteAsync(PartitionInfo p, Action<string> log, CancellationToken ct) =>
        Run(Select(p) + "$p | Remove-Partition -Confirm:$false;'تم حذف البارتشن'", log, ct);

    /// <summary>أكبر مساحة غير مقسّمة على القرص.</summary>
    public static async Task<long> GetLargestFreeExtentAsync(int disk, CancellationToken ct)
    {
        long v = 0;
        await Run($"'FREE=' + (Get-Disk -Number {disk}).LargestFreeExtent", l =>
        {
            if (l.StartsWith("FREE=")) long.TryParse(l[5..], out v);
        }, ct);
        return v;
    }

    /// <summary>ينشئ بارتشن جديد في المساحة الفارغة ويعمل له فورمات ويعطيه حرفاً.</summary>
    public static Task<int> CreateAsync(int disk, long? size, string fs, string label, Action<string> log, CancellationToken ct)
    {
        string sizeArg = size is { } s ? $"-Size {s}" : "-UseMaximumSize";
        string safeLabel = label.Replace("'", "''");
        string fsName = fs is "NTFS" or "exFAT" or "FAT32" ? fs : "NTFS";
        return Run($"$p = New-Partition -DiskNumber {disk} {sizeArg} -AssignDriveLetter;" +
                   $"$v = $p | Format-Volume -FileSystem {fsName} -NewFileSystemLabel '{safeLabel}' -Confirm:$false;" +
                   "'تم إنشاء البارتشن: ' + $p.DriveLetter + ':  ' + [math]::Round($p.Size/1GB,2) + ' GB'", log, ct);
    }
}

/// <summary>المسح الآمن: بيانات لا يمكن استرجاعها بأي برنامج.</summary>
public static class SecureWipe
{
    /// <summary>يكتب فوق المساحة الفارغة كلها (يمسح أثر الملفات المحذوفة فقط ويترك الملفات الموجودة).</summary>
    public static Task<int> WipeFreeSpaceAsync(PartitionInfo p, Action<string> log, CancellationToken ct)
    {
        if (p.DriveLetter == null) throw new InvalidOperationException("المسح الآمن للمساحة الفارغة يحتاج بارتشن له حرف.");
        log("جاري الكتابة فوق المساحة الفارغة (3 مرات: أصفار ثم FF ثم بيانات عشوائية). قد يستغرق وقتاً طويلاً...");
        return VolumeTools.RunAsync("cipher.exe", $"/w:{p.DriveLetter}\\", null, log, ct, VolumeTools.OemEncoding());
    }

    /// <summary>يمسح ملفات محددة نهائياً: يكتب فوق محتواها ثم يغير اسمها ثم يحذفها.</summary>
    public static int WipeFiles(IEnumerable<string> paths, int passes, Action<string> log, CancellationToken ct)
    {
        int done = 0;
        var buf = new byte[1 << 20];
        foreach (var path in paths.SelectMany(Expand))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.IsReadOnly) info.IsReadOnly = false;
                long len = info.Length;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                {
                    for (int pass = 0; pass < passes; pass++)
                    {
                        fs.Position = 0;
                        for (long o = 0; o < len; o += buf.Length)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (pass == passes - 1) Random.Shared.NextBytes(buf);
                            else Array.Fill(buf, pass % 2 == 0 ? (byte)0x00 : (byte)0xFF);
                            fs.Write(buf, 0, (int)Math.Min(buf.Length, len - o));
                        }
                        fs.Flush(true);
                    }
                    fs.SetLength(0);
                }
                // تغيير الاسم لإخفاء الاسم الأصلي من سجلات نظام الملفات
                string renamed = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N")[..12]);
                File.Move(path, renamed);
                File.SetCreationTime(renamed, new DateTime(2000, 1, 1));
                File.SetLastWriteTime(renamed, new DateTime(2000, 1, 1));
                File.Delete(renamed);
                done++;
                log($"✔ {path}");
            }
            catch (Exception ex) { log($"✖ {path}: {ex.Message}"); }
        }
        foreach (var dir in paths.Where(Directory.Exists).OrderByDescending(d => d.Length))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        return done;
    }

    private static IEnumerable<string> Expand(string p)
    {
        if (File.Exists(p)) return new[] { p };
        if (Directory.Exists(p)) return Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories);
        return Array.Empty<string>();
    }
}
