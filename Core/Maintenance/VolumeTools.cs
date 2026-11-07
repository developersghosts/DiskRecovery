using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DiskRecovery.Core.Maintenance;

public enum RepairMode
{
    ScanOnly,        // chkdsk X:          — قراءة فقط، لا يغيّر شيئاً
    FixErrors,       // chkdsk X: /f /x    — يصلح أخطاء نظام الملفات
    FixAndBadSectors // chkdsk X: /r /x    — يصلح ويفحص القطاعات التالفة (بطيء)
}

public sealed record FormatOptions(string FileSystem, string Label, bool QuickFormat);

/// <summary>إصلاح وفورمات البارتشنات باستخدام أدوات ويندوز نفسها (chkdsk و Format-Volume).</summary>
public static class VolumeTools
{
    private static readonly HashSet<string> SystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EFI System", "Microsoft Reserved", "Windows Recovery", "Windows Recovery (مخفي)",
        "LDM Metadata", "LDM Data", "Storage Spaces", "GPT Protective", "Dynamic",
    };

    /// <summary>هل البارتشن ممنوع تعديله (ويندوز، الإقلاع، الاستعادة...)؟</summary>
    public static string? GetProtectionReason(PartitionInfo p, bool forFormat)
    {
        string sysRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        string appRoot = (Path.GetPathRoot(AppContext.BaseDirectory) ?? "").TrimEnd('\\');

        if (SystemTypes.Contains(p.TypeName))
            return $"هذا بارتشن نظام ({p.TypeName}) يحتاجه ويندوز للإقلاع أو الاستعادة. تعديله قد يمنع الجهاز من التشغيل.";

        if (forFormat)
        {
            if (p.DriveLetter != null && p.DriveLetter.Equals(sysRoot, StringComparison.OrdinalIgnoreCase))
                return "هذا بارتشن الويندوز نفسه ولا يمكن عمل فورمات له من داخل ويندوز.";
            if (p.DriveLetter != null && p.DriveLetter.Equals(appRoot, StringComparison.OrdinalIgnoreCase))
                return "البرنامج نفسه يعمل من هذا البارتشن. انقل البرنامج لمكان آخر ثم أعد المحاولة.";
            if (p.TypeName is "Linux" or "Linux Swap")
                return "هذا بارتشن لينكس. لا يمكن عمل فورمات له من هذا البرنامج.";
        }
        return null;
    }

    public static bool IsSystemVolume(PartitionInfo p)
    {
        string sysRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        return p.DriveLetter != null && p.DriveLetter.Equals(sysRoot, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── الإصلاح ─────────────

    public static async Task<int> RepairAsync(PartitionInfo p, RepairMode mode, Action<string> log, CancellationToken ct)
    {
        string? target = p.DriveLetter;
        if (target == null)
        {
            log("البارتشن مخفي — جاري تحديد مساره...");
            target = await GetVolumePathAsync(p, ct);
            if (target == null)
                throw new InvalidOperationException("تعذّر الوصول للبارتشن. غالباً لا يحتوي على نظام ملفات (يحتاج فورمات).");
        }

        // بارتشن الويندوز مستخدم دائماً، فيتم جدولة الإصلاح عند إعادة التشغيل
        if (IsSystemVolume(p) && mode != RepairMode.ScanOnly)
        {
            log($"بارتشن الويندوز ({target}) قيد الاستخدام، لذلك سيتم الإصلاح تلقائياً عند إعادة تشغيل الجهاز.");
            int code = await RunAsync("fsutil.exe", $"dirty set {target}", null, log, ct, Encoding.UTF8);
            if (code == 0) log("✔ تمت الجدولة. أعد تشغيل الجهاز ليبدأ الإصلاح قبل دخول ويندوز.");
            return code;
        }

        string args = mode switch
        {
            RepairMode.ScanOnly => $"{target}",
            RepairMode.FixErrors => $"{target} /f /x",
            _ => $"{target} /r /x",
        };
        log($"> chkdsk {args}");
        return await RunAsync("chkdsk.exe", args, null, log, ct, OemEncoding());
    }

    // ───────────── الفورمات ─────────────

    public static async Task<int> FormatAsync(PartitionInfo p, FormatOptions o, Action<string> log, CancellationToken ct)
    {
        string fs = o.FileSystem switch { "NTFS" or "exFAT" or "FAT32" => o.FileSystem, _ => "NTFS" };
        string label = o.Label.Replace("'", "''");
        string full = o.QuickFormat ? "" : " -Full";
        string script =
            "$ErrorActionPreference='Stop';" +
            $"$p = Get-Partition -DiskNumber {p.DiskNumber} | Where-Object {{ $_.Offset -eq {p.Offset} }};" +
            "if (-not $p) { throw 'لم يتم العثور على البارتشن' };" +
            $"$v = $p | Format-Volume -FileSystem {fs} -NewFileSystemLabel '{label}'{full} -Force -Confirm:$false;" +
            "'تم الفورمات: ' + $v.FileSystem + '  المساحة: ' + [math]::Round($v.Size/1GB,2) + ' GB'";

        log($"جاري الفورمات بنظام {fs}{(o.QuickFormat ? " (سريع)" : " (كامل - قد يستغرق وقتاً طويلاً)")}...");
        return await RunPowerShellAsync(script, log, ct);
    }

    private static async Task<string?> GetVolumePathAsync(PartitionInfo p, CancellationToken ct)
    {
        string? path = null;
        string script =
            $"$v = Get-Partition -DiskNumber {p.DiskNumber} | Where-Object {{ $_.Offset -eq {p.Offset} }} | Get-Volume;" +
            "if ($v -and $v.FileSystem) { $v.Path }";
        await RunPowerShellAsync(script, line =>
        {
            if (line.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)) path = line.Trim().TrimEnd('\\');
        }, ct);
        return path;
    }

    // ───────────── تشغيل الأوامر ─────────────

    internal static Task<int> RunPowerShellAsync(string script, Action<string> log, CancellationToken ct)
    {
        string full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                        null, log, ct, Encoding.UTF8);
    }

    internal static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }

    internal static async Task<int> RunAsync(string exe, string args, string? stdin, Action<string> log,
                                            CancellationToken ct, Encoding encoding)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(CleanLine(e.Data)); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("⚠ " + CleanLine(e.Data)); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (stdin != null) { await proc.StandardInput.WriteAsync(stdin); proc.StandardInput.Close(); }

        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            // نسمح بإيقاف الفحص فقط؛ الإصلاح والفورمات لا يجب قطعهما في المنتصف
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            throw;
        }
        return proc.ExitCode;
    }

    private static string CleanLine(string s)
    {
        // chkdsk يكتب نسبة التقدم مع \r في نفس السطر
        int i = s.LastIndexOf('\r');
        return (i >= 0 ? s[(i + 1)..] : s).TrimEnd();
    }
}
