using System.Buffers.Binary;
using System.Text.Json;
using DiskRecovery.Core.Maintenance;
using DiskRecovery.Native;

namespace DiskRecovery.Core.Health;

public enum HealthVerdict { Good, Caution, Bad, Unknown }

public sealed class SmartAttribute
{
    public byte Id { get; init; }
    public string Name { get; init; } = "";
    public byte Current { get; init; }
    public byte Worst { get; init; }
    public byte Threshold { get; init; }
    public long Raw { get; init; }
    public bool Critical { get; init; }   // من الخصائص المهمة لعمر الهارد
    public bool Failing => Threshold > 0 && Current > 0 && Current <= Threshold;
    public string RawDisplay => Id is 194 or 190 ? $"{Raw & 0xFF} °C" : Raw.ToString("N0");
}

public sealed class DiskHealth
{
    public int DiskNumber { get; init; }
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Bus { get; set; } = "";
    public string Media { get; set; } = "";
    public long Size { get; set; }
    public string WindowsHealth { get; set; } = "";
    public int? Temperature { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public int? PercentUsed { get; set; }        // نسبة استهلاك عمر SSD
    public long? ReadErrors { get; set; }
    public long? MediaErrors { get; set; }
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public long? Uncorrectable { get; set; }
    public long? DataWrittenGB { get; set; }
    public List<SmartAttribute> Attributes { get; } = new();
    public HealthVerdict Verdict { get; set; } = HealthVerdict.Unknown;
    public List<string> Notes { get; } = new();
    public string Source { get; set; } = "";

    public bool IsSsd => Media.Contains("SSD", StringComparison.OrdinalIgnoreCase) || Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>قراءة حالة الهارد: S.M.A.R.T. للهاردات SATA، وسجل الصحة لـ NVMe، وعدادات ويندوز.</summary>
public static class DiskHealthReader
{
    private static readonly Dictionary<byte, (string Name, bool Critical)> Names = new()
    {
        [1] = ("أخطاء القراءة (Raw Read Error Rate)", false),
        [3] = ("زمن بدء الدوران (Spin-Up Time)", false),
        [4] = ("مرات التشغيل والإيقاف", false),
        [5] = ("قطاعات تالفة تم استبدالها (Reallocated)", true),
        [7] = ("أخطاء تحديد المكان (Seek Error)", false),
        [9] = ("ساعات التشغيل", false),
        [10] = ("إعادة محاولة الدوران (Spin Retry)", true),
        [12] = ("مرات تشغيل الجهاز", false),
        [170] = ("الكتل الاحتياطية المتاحة", true),
        [171] = ("أخطاء البرمجة (Program Fail)", true),
        [172] = ("أخطاء المسح (Erase Fail)", true),
        [173] = ("متوسط الاستهلاك (Wear Leveling)", false),
        [174] = ("انقطاع الكهرباء المفاجئ", false),
        [177] = ("استهلاك الخلايا (Wear Leveling Count)", true),
        [181] = ("أخطاء البرمجة", true),
        [182] = ("أخطاء المسح", true),
        [183] = ("كتل تالفة أثناء التشغيل", true),
        [184] = ("أخطاء نقل البيانات الداخلية (End-to-End)", true),
        [187] = ("أخطاء غير قابلة للتصحيح (Reported Uncorrectable)", true),
        [188] = ("انتهاء مهلة الأوامر (Command Timeout)", false),
        [189] = ("كتابة على ارتفاع غير طبيعي", false),
        [190] = ("درجة حرارة الهواء", false),
        [191] = ("أخطاء الصدمات (G-Sense)", false),
        [192] = ("مرات الإيقاف الطارئ", false),
        [193] = ("دورات تحميل الرأس (Load Cycle)", false),
        [194] = ("درجة الحرارة", false),
        [195] = ("أخطاء تم تصحيحها (ECC)", false),
        [196] = ("أحداث الاستبدال (Reallocation Events)", true),
        [197] = ("قطاعات معلّقة غير مستقرة (Pending)", true),
        [198] = ("قطاعات غير قابلة للإصلاح (Offline Uncorrectable)", true),
        [199] = ("أخطاء الكابل (UDMA CRC)", false),
        [200] = ("أخطاء الكتابة (Write Error Rate)", false),
        [202] = ("نسبة العمر المستهلك", false),
        [231] = ("العمر المتبقي لـ SSD", true),
        [232] = ("المساحة الاحتياطية المتبقية", true),
        [233] = ("استهلاك الذاكرة (Media Wearout)", true),
        [241] = ("إجمالي البيانات المكتوبة", false),
        [242] = ("إجمالي البيانات المقروءة", false),
    };

    public static async Task<List<DiskHealth>> ReadAllAsync(IEnumerable<int> diskNumbers, CancellationToken ct)
    {
        var result = diskNumbers.Distinct().Where(d => d >= 0).OrderBy(d => d)
                                .Select(d => new DiskHealth { DiskNumber = d }).ToList();

        // 1) معلومات ويندوز (تعمل مع معظم الأنواع بما فيها USB أحياناً)
        try { await ReadWindowsCountersAsync(result, ct); } catch { /* PowerShell غير متاح */ }

        // 2) S.M.A.R.T. مباشرة من الهارد
        foreach (var h in result)
        {
            ct.ThrowIfCancellationRequested();
            try { ReadNative(h); } catch { }
            Evaluate(h);
        }
        return result;
    }

    private static async Task ReadWindowsCountersAsync(List<DiskHealth> disks, CancellationToken ct)
    {
        const string script =
            "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
            "[pscustomobject]@{ Id=[string]$_.DeviceId; Model=$_.FriendlyName; Serial=$_.SerialNumber; Media=[string]$_.MediaType; " +
            "Bus=[string]$_.BusType; Health=[string]$_.HealthStatus; Size=[long]$_.Size; Temp=$r.Temperature; " +
            "Hours=$r.PowerOnHours; Wear=$r.Wear; ReadErr=$r.ReadErrorsTotal; StartStop=$r.StartStopCycleCount } } | ConvertTo-Json -Compress";
        var sb = new System.Text.StringBuilder();
        await VolumeTools.RunPowerShellAsync(script, l => sb.AppendLine(l), ct);
        string json = sb.ToString().Trim();
        if (json.Length == 0 || (json[0] != '[' && json[0] != '{')) return;

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
        foreach (var e in arr)
        {
            if (!int.TryParse(Str(e, "Id"), out int id)) continue;
            var h = disks.FirstOrDefault(d => d.DiskNumber == id);
            if (h == null) continue;
            h.Model = Str(e, "Model");
            h.Serial = Str(e, "Serial").Trim();
            h.Media = Str(e, "Media") switch { "SSD" => "SSD", "HDD" => "HDD", var m => m };
            h.Bus = Str(e, "Bus");
            h.WindowsHealth = Str(e, "Health");
            h.Size = Num(e, "Size") ?? 0;
            if (Num(e, "Temp") is > 0 and < 150 and var t) h.Temperature = (int)t;
            h.PowerOnHours = Num(e, "Hours") is > 0 and var hrs ? hrs : null;
            if (Num(e, "Wear") is >= 0 and <= 100 and var w && h.IsSsd) h.PercentUsed = (int)w;
            h.ReadErrors = Num(e, "ReadErr");
            h.PowerCycles = Num(e, "StartStop");
            h.Source = "Windows";
        }
    }

    private static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" :
        e.TryGetProperty(n, out v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";

    private static long? Num(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : null;

    // ───────────── القراءة المباشرة ─────────────

    private static void ReadNative(DiskHealth h)
    {
        using var handle = NativeMethods.OpenForQuery($@"\\.\PhysicalDrive{h.DiskNumber}");
        if (handle.IsInvalid) return;

        if (TryReadAta(handle, h.DiskNumber, out var values, out var thresholds))
        {
            foreach (var a in ParseAtaAttributes(values, thresholds)) h.Attributes.Add(a);
            ApplyAta(h);
            h.Source = h.Source.Length > 0 ? h.Source + " + S.M.A.R.T." : "S.M.A.R.T.";
            return;
        }
        if (TryReadNvme(handle, out var log))
        {
            ApplyNvme(h, log);
            h.Source = h.Source.Length > 0 ? h.Source + " + NVMe" : "NVMe";
            if (string.IsNullOrEmpty(h.Bus)) h.Bus = "NVMe";
        }
    }

    private static bool TryReadAta(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int disk, out byte[] values, out byte[]? thresholds)
    {
        values = Array.Empty<byte>(); thresholds = null;
        var v = SmartCommand(handle, disk, 0xD0);  // READ ATTRIBUTE VALUES
        if (v == null) return false;
        values = v;
        thresholds = SmartCommand(handle, disk, 0xD1); // READ THRESHOLDS
        return true;
    }

    private static byte[]? SmartCommand(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int disk, byte feature)
    {
        // SENDCMDINPARAMS (مضغوط بدون فراغات)
        var input = new byte[32 + 1];
        BinaryPrimitives.WriteInt32LittleEndian(input, 512);
        input[4] = feature;            // bFeaturesReg
        input[5] = 1;                  // bSectorCountReg
        input[6] = 1;                  // bSectorNumberReg
        input[7] = 0x4F;               // bCylLowReg
        input[8] = 0xC2;               // bCylHighReg
        input[9] = (byte)(0xA0 | ((disk & 1) << 4)); // bDriveHeadReg
        input[10] = 0xB0;              // bCommandReg = SMART
        input[12] = (byte)disk;        // bDriveNumber
        var output = new byte[16 + 512];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out int ret, IntPtr.Zero))
            return null;
        if (ret < 16 + 362) return null;
        var data = output.AsSpan(16, 512).ToArray();
        return data.All(b => b == 0) ? null : data;
    }

    private static bool TryReadNvme(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out byte[] log)
    {
        log = Array.Empty<byte>();
        // STORAGE_PROPERTY_QUERY (PropertyId=StorageDeviceProtocolSpecificProperty=50) + STORAGE_PROTOCOL_SPECIFIC_DATA + 512 bytes
        const int specSize = 40;
        var input = new byte[8 + specSize + 512];
        BinaryPrimitives.WriteInt32LittleEndian(input, 50);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4), 0);
        var spec = input.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(spec, 3);           // ProtocolTypeNvme
        BinaryPrimitives.WriteInt32LittleEndian(spec[4..], 2);      // NVMeDataTypeLogPage
        BinaryPrimitives.WriteInt32LittleEndian(spec[8..], 2);      // SMART / Health log
        BinaryPrimitives.WriteInt32LittleEndian(spec[12..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(spec[16..], specSize); // ProtocolDataOffset
        BinaryPrimitives.WriteInt32LittleEndian(spec[20..], 512);     // ProtocolDataLength
        var output = new byte[input.Length];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length, output, output.Length, out int ret, IntPtr.Zero))
            return false;
        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, ثم STORAGE_PROTOCOL_SPECIFIC_DATA
        int dataOff = 8 + BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + 16));
        if (dataOff <= 8 || dataOff + 512 > output.Length || ret < dataOff + 200) return false;
        log = output.AsSpan(dataOff, 512).ToArray();
        return !log.All(b => b == 0);
    }

    // ───────────── التحليل (قابل للاختبار) ─────────────

    public static List<SmartAttribute> ParseAtaAttributes(byte[] values, byte[]? thresholds)
    {
        var th = new Dictionary<byte, byte>();
        if (thresholds != null)
            for (int i = 0; i < 30; i++)
            {
                int o = 2 + i * 12;
                if (o + 12 > thresholds.Length) break;
                if (thresholds[o] != 0) th[thresholds[o]] = thresholds[o + 1];
            }

        var list = new List<SmartAttribute>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            if (o + 12 > values.Length) break;
            byte id = values[o];
            if (id == 0) continue;
            long raw = 0;
            for (int b = 0; b < 6; b++) raw |= (long)values[o + 5 + b] << (8 * b);
            var (name, critical) = Names.TryGetValue(id, out var n) ? n : ($"خاصية {id}", false);
            list.Add(new SmartAttribute
            {
                Id = id, Name = name, Critical = critical,
                Current = values[o + 3], Worst = values[o + 4],
                Threshold = th.TryGetValue(id, out var t) ? t : (byte)0,
                Raw = raw,
            });
        }
        return list;
    }

    private static void ApplyAta(DiskHealth h)
    {
        long? Raw(byte id) => h.Attributes.FirstOrDefault(a => a.Id == id)?.Raw;
        if (Raw(194) is { } t && (t & 0xFF) is > 0 and < 120) h.Temperature = (int)(t & 0xFF);
        else if (Raw(190) is { } t2 && (t2 & 0xFF) is > 0 and < 120) h.Temperature = (int)(t2 & 0xFF);
        if (Raw(9) is { } hrs) h.PowerOnHours = hrs & 0xFFFFFFFF;
        if (Raw(12) is { } pc) h.PowerCycles = pc;
        h.ReallocatedSectors = Raw(5);
        h.PendingSectors = Raw(197);
        h.Uncorrectable = Raw(198) ?? Raw(187);
        var life = h.Attributes.FirstOrDefault(a => a.Id is 231 or 233 or 177 or 202);
        if (life != null && life.Current is > 0 and <= 100) h.PercentUsed = 100 - life.Current;
        if (Raw(241) is { } lbas && lbas > 0) h.DataWrittenGB = lbas * 512 / 1_000_000_000;
    }

    public static void ApplyNvme(DiskHealth h, byte[] log)
    {
        byte critical = log[0];
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(1));
        if (kelvin > 200) h.Temperature = kelvin - 273;
        int spare = log[3], spareTh = log[4];
        h.PercentUsed = log[5];
        long Lo64(int off) => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(off)), long.MaxValue);
        h.DataWrittenGB = Lo64(48) * 512_000 / 1_000_000_000;
        h.PowerCycles = Lo64(112);
        h.PowerOnHours = Lo64(128);
        h.MediaErrors = Lo64(160);

        if (critical != 0)
        {
            if ((critical & 0x01) != 0) h.Notes.Add("المساحة الاحتياطية أقل من الحد الآمن");
            if ((critical & 0x02) != 0) h.Notes.Add("درجة الحرارة تعدّت الحد المسموح");
            if ((critical & 0x04) != 0) h.Notes.Add("تراجع في اعتمادية الذاكرة");
            if ((critical & 0x08) != 0) h.Notes.Add("الهارد تحوّل لوضع القراءة فقط");
        }
        h.Attributes.Add(new SmartAttribute { Id = 1, Name = "تحذيرات حرجة", Raw = critical, Critical = true });
        h.Attributes.Add(new SmartAttribute { Id = 2, Name = "درجة الحرارة", Raw = h.Temperature ?? 0 });
        h.Attributes.Add(new SmartAttribute { Id = 3, Name = "المساحة الاحتياطية المتاحة %", Raw = spare, Current = (byte)spare, Threshold = (byte)spareTh, Critical = true });
        h.Attributes.Add(new SmartAttribute { Id = 5, Name = "نسبة العمر المستهلك %", Raw = log[5], Critical = true });
        h.Attributes.Add(new SmartAttribute { Id = 12, Name = "مرات التشغيل", Raw = h.PowerCycles ?? 0 });
        h.Attributes.Add(new SmartAttribute { Id = 9, Name = "ساعات التشغيل", Raw = h.PowerOnHours ?? 0 });
        h.Attributes.Add(new SmartAttribute { Id = 13, Name = "مرات انقطاع الكهرباء المفاجئ", Raw = Lo64(144) });
        h.Attributes.Add(new SmartAttribute { Id = 14, Name = "أخطاء الذاكرة (Media Errors)", Raw = h.MediaErrors ?? 0, Critical = true });
        h.Attributes.Add(new SmartAttribute { Id = 15, Name = "إجمالي المكتوب (GB)", Raw = h.DataWrittenGB ?? 0 });
    }

    public static void Evaluate(DiskHealth h)
    {
        var v = HealthVerdict.Unknown;
        void Raise(HealthVerdict to) { if (v == HealthVerdict.Unknown || to > v) v = to; }

        if (h.Attributes.Count > 0 || h.Temperature != null || h.WindowsHealth.Length > 0) v = HealthVerdict.Good;

        switch (h.WindowsHealth)
        {
            case "Unhealthy": Raise(HealthVerdict.Bad); h.Notes.Add("ويندوز يعتبر الهارد غير سليم"); break;
            case "Warning": Raise(HealthVerdict.Caution); h.Notes.Add("ويندوز أصدر تحذيراً بخصوص الهارد"); break;
        }

        foreach (var a in h.Attributes.Where(a => a.Failing))
        {
            Raise(HealthVerdict.Bad);
            h.Notes.Add($"«{a.Name}» وصلت للحد الخطر");
        }

        if (h.ReallocatedSectors is > 0 and var re)
        {
            Raise(re > 100 ? HealthVerdict.Bad : HealthVerdict.Caution);
            h.Notes.Add($"يوجد {re:N0} قطاع تالف تم استبداله");
        }
        if (h.PendingSectors is > 0 and var pe)
        {
            Raise(HealthVerdict.Caution);
            h.Notes.Add($"يوجد {pe:N0} قطاع غير مستقر — انسخ بياناتك المهمة الآن");
        }
        if (h.Uncorrectable is > 0 and var un)
        {
            Raise(HealthVerdict.Caution);
            h.Notes.Add($"يوجد {un:N0} خطأ قراءة غير قابل للتصحيح");
        }
        if (h.MediaErrors is > 0 and var me)
        {
            Raise(HealthVerdict.Caution);
            h.Notes.Add($"يوجد {me:N0} خطأ في ذاكرة الـ SSD");
        }
        if (h.Notes.Any(n => n.Contains("القراءة فقط") || n.Contains("الاحتياطية أقل"))) Raise(HealthVerdict.Bad);
        if (h.PercentUsed is >= 100) { Raise(HealthVerdict.Bad); h.Notes.Add("انتهى العمر الافتراضي للـ SSD"); }
        else if (h.PercentUsed is >= 90) { Raise(HealthVerdict.Caution); h.Notes.Add($"تم استهلاك {h.PercentUsed}% من عمر الـ SSD"); }

        int hot = h.IsSsd ? 70 : 55;
        if (h.Temperature is { } t && t >= hot) { Raise(HealthVerdict.Caution); h.Notes.Add($"درجة الحرارة مرتفعة ({t}°C)"); }

        if (h.Attributes.FirstOrDefault(a => a.Id == 199) is { Raw: > 0 } crc)
            h.Notes.Add($"أخطاء في الكابل ({crc.Raw:N0}) — جرّب تغيير كابل SATA");

        if (v == HealthVerdict.Unknown)
            h.Notes.Add("الهارد لا يدعم قراءة S.M.A.R.T. (غالباً هارد خارجي USB أو كارت ميموري)");
        else if (v == HealthVerdict.Good && h.Notes.Count == 0)
            h.Notes.Add("لا توجد أي مشاكل");
        h.Verdict = v;
    }

    public static string VerdictText(HealthVerdict v) => v switch
    {
        HealthVerdict.Good => "سليم",
        HealthVerdict.Caution => "تحذير",
        HealthVerdict.Bad => "خطر",
        _ => "غير معروف",
    };
}
