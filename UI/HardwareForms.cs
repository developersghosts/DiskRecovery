using DiskRecovery.Core;
using DiskRecovery.Core.Health;

namespace DiskRecovery.UI;

// ═══════════════════════════ صحة الهارد ═══════════════════════════

public sealed class HealthForm : Form
{
    private readonly DataGridView _disks = new();
    private readonly DataGridView _attrs = new();
    private readonly Label _verdict = new() { Dock = DockStyle.Top, Height = 46, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.F(15f, FontStyle.Bold), ForeColor = Color.White };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 110, Padding = new Padding(8), Font = Ui.F(10f) };
    private readonly List<int> _diskNumbers;
    private List<DiskHealth> _results = new();

    public HealthForm(List<PartitionInfo> parts)
    {
        _diskNumbers = parts.Where(p => !p.IsImage && p.DiskNumber >= 0).Select(p => p.DiskNumber).Distinct().ToList();
        Ui.SetupForm(this, "صحة الهارد", 1050, 680);

        Ui.StyleGrid(_disks);
        _disks.ReadOnly = true; _disks.MultiSelect = false;
        Ui.AddColumn(_disks, "disk", "القرص", 8);
        Ui.AddColumn(_disks, "model", "الموديل", 30);
        Ui.AddColumn(_disks, "type", "النوع", 12);
        Ui.AddColumn(_disks, "verdict", "الحالة", 10);
        Ui.AddColumn(_disks, "temp", "الحرارة", 9);
        Ui.AddColumn(_disks, "hours", "ساعات التشغيل", 12);
        _disks.SelectionChanged += (_, _) => ShowDetails();

        Ui.StyleGrid(_attrs);
        _attrs.ReadOnly = true;
        Ui.AddColumn(_attrs, "id", "ID", 5);
        Ui.AddColumn(_attrs, "name", "الخاصية", 40);
        Ui.AddColumn(_attrs, "cur", "الحالية", 8);
        Ui.AddColumn(_attrs, "worst", "الأسوأ", 8);
        Ui.AddColumn(_attrs, "th", "الحد", 8);
        Ui.AddColumn(_attrs, "raw", "القيمة الخام", 14);

        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };
        details.Controls.Add(_attrs);
        details.Controls.Add(_summary);
        details.Controls.Add(_verdict);
        _attrs.BringToFront();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_disks);
        split.Panel1.Padding = new Padding(10, 6, 10, 0);
        split.Panel2.Controls.Add(details);
        HandleCreated += (_, _) => split.SplitterDistance = 160;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        var refresh = Ui.Button("تحديث", true, 120);
        var close = Ui.Button("إغلاق", false, 100);
        refresh.Click += async (_, _) => await LoadAsync();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(refresh); buttons.Controls.Add(close);

        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("صحة الهارد (S.M.A.R.T.)", "قراءة فقط — لا يتم تعديل أي شيء على الهارد"));
        split.BringToFront();
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        UseWaitCursor = true;
        _summary.Text = "جاري القراءة...";
        try
        {
            _results = await DiskHealthReader.ReadAllAsync(_diskNumbers, CancellationToken.None);
            _disks.Rows.Clear();
            foreach (var h in _results)
            {
                int i = _disks.Rows.Add($"Disk {h.DiskNumber}", h.Model.Length > 0 ? h.Model : "—",
                    $"{(h.Media.Length > 0 ? h.Media : "?")} {h.Bus}", DiskHealthReader.VerdictText(h.Verdict),
                    h.Temperature is { } t ? $"{t} °C" : "—", h.PowerOnHours?.ToString("N0") ?? "—");
                _disks.Rows[i].Cells["verdict"].Style.ForeColor = Color.White;
                _disks.Rows[i].Cells["verdict"].Style.BackColor = VerdictColor(h.Verdict);
            }
            if (_results.Count == 0) _summary.Text = "لا توجد أقراص.";
            ShowDetails();
        }
        catch (Exception ex) { _summary.Text = "تعذرت القراءة: " + ex.Message; }
        finally { UseWaitCursor = false; }
    }

    private static Color VerdictColor(HealthVerdict v) => v switch
    {
        HealthVerdict.Good => Ui.Good, HealthVerdict.Caution => Ui.Warn, HealthVerdict.Bad => Ui.Danger, _ => Color.Gray,
    };

    private void ShowDetails()
    {
        if (_disks.SelectedRows.Count == 0 || _disks.SelectedRows[0].Index >= _results.Count) return;
        var h = _results[_disks.SelectedRows[0].Index];
        _verdict.BackColor = VerdictColor(h.Verdict);
        _verdict.Text = h.Verdict switch
        {
            HealthVerdict.Good => "✔ الهارد سليم",
            HealthVerdict.Caution => "⚠ تحذير — راقب الهارد وخد نسخة احتياطية",
            HealthVerdict.Bad => "✖ خطر — انقل بياناتك فوراً",
            _ => "حالة غير معروفة",
        };
        var lines = new List<string>
        {
            $"{h.Model}   •   {Ui.Size(h.Size)}   •   {h.Media} {h.Bus}" + (h.Serial.Length > 0 ? $"   •   S/N: {h.Serial}" : ""),
            string.Join("   •   ", new[]
            {
                h.Temperature is { } t ? $"الحرارة: {t}°C" : null,
                h.PowerOnHours is { } hr ? $"التشغيل: {hr:N0} ساعة (~{hr / 24.0 / 365:0.#} سنة)" : null,
                h.PercentUsed is { } pu ? $"عمر SSD المستهلك: {pu}%" : null,
                h.DataWrittenGB is { } w ? $"المكتوب: {w:N0} GB" : null,
            }.Where(x => x != null)),
        };
        lines.AddRange(h.Notes.Select(n => "• " + n));
        if (h.Verdict == HealthVerdict.Bad || h.Verdict == HealthVerdict.Caution)
            lines.Add("• لا تعمل إصلاح (chkdsk) على هارد بيموت — اعمل نسخة كاملة (Image) الأول من قائمة الأدوات.");
        _summary.Text = string.Join("\n", lines);

        _attrs.Rows.Clear();
        foreach (var a in h.Attributes)
        {
            int i = _attrs.Rows.Add(a.Id, a.Name, a.Current == 0 ? "" : a.Current, a.Worst == 0 ? "" : a.Worst, a.Threshold == 0 ? "" : a.Threshold, a.RawDisplay);
            if (a.Failing) _attrs.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 210, 210);
            else if (a.Critical && a.Raw > 0 && a.Id is 5 or 10 or 183 or 184 or 187 or 196 or 197 or 198 or 14)
                _attrs.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 236, 200);
        }
    }
}

// ═══════════════════════════ فحص سطح القرص ═══════════════════════════

internal sealed class SurfaceMap : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public SurfaceResult? Result { get; set; }
    public SurfaceMap() { DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true; }

    public static Color ColorOf(BlockState s) => s switch
    {
        BlockState.Fast => Color.FromArgb(76, 175, 80),
        BlockState.Normal => Color.FromArgb(139, 195, 74),
        BlockState.Slow => Color.FromArgb(255, 193, 7),
        BlockState.VerySlow => Color.FromArgb(255, 112, 67),
        BlockState.Bad => Color.FromArgb(198, 40, 40),
        _ => Color.FromArgb(225, 228, 232),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var r = Result;
        const int cell = 9, gap = 1;
        int cols = Math.Max(1, (Width - 4) / (cell + gap));
        int rows = Math.Max(1, (Height - 4) / (cell + gap));
        int cells = cols * rows;
        long blocks = r?.Blocks.LongLength ?? 0;
        long per = blocks == 0 ? 1 : Math.Max(1, (blocks + cells - 1) / cells);
        int used = blocks == 0 ? cells : (int)Math.Min(cells, (blocks + per - 1) / per);
        var brushes = Enum.GetValues<BlockState>().ToDictionary(s => s, s => new SolidBrush(ColorOf(s)));
        try
        {
            for (int i = 0; i < used; i++)
            {
                var worst = BlockState.NotScanned;
                if (r != null)
                {
                    long a = i * per, b = Math.Min(blocks, a + per);
                    for (long k = a; k < b; k++) if (r.Blocks[k] > worst) worst = r.Blocks[k];
                }
                int x = 2 + i % cols * (cell + gap); // النافذة معكوسة (RTL) فيظهر الرسم من اليمين
                int y = 2 + i / cols * (cell + gap);
                e.Graphics.FillRectangle(brushes[worst], x, y, cell, cell);
            }
        }
        finally { foreach (var b in brushes.Values) b.Dispose(); }
    }
}

public sealed class SurfaceForm : Form
{
    private readonly ComboBox _cbo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620 };
    private readonly SurfaceMap _map = new() { Dock = DockStyle.Fill };
    private readonly Label _stats = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(6), Font = Ui.F(10f) };
    private readonly Button _btn = Ui.Button("ابدأ الفحص", true, 150);
    private CancellationTokenSource? _cts;

    public SurfaceForm(List<PartitionInfo> parts)
    {
        Ui.SetupForm(this, "فحص سطح القرص", 900, 680);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14, 6, 14, 0), WrapContents = false };
        top.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(850, 0), ForeColor = Color.DimGray,
            Text = "يقرأ كل منطقة في القرص ويقيس سرعة قراءتها. المناطق البطيئة جداً غالباً قطاعات ضعيفة على وشك التلف، والحمراء قطاعات تالفة. قراءة فقط — آمن تماماً.",
        });
        foreach (var t in Ui.Targets(parts, disks: true, partitions: true)) _cbo.Items.Add(t);
        if (_cbo.Items.Count > 0) _cbo.SelectedIndex = 0;
        top.Controls.Add(_cbo);

        var legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(10, 4, 10, 0) };
        foreach (var (s, name) in new[] { (BlockState.Fast, "سريع"), (BlockState.Normal, "عادي"), (BlockState.Slow, "بطيء"), (BlockState.VerySlow, "بطيء جداً (ضعيف)"), (BlockState.Bad, "تالف"), (BlockState.NotScanned, "لم يُفحص") })
        {
            legend.Controls.Add(new Panel { Width = 14, Height = 14, BackColor = SurfaceMap.ColorOf(s), Margin = new Padding(4, 5, 2, 0) });
            legend.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 3, 10, 0) });
        }

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4) };
        body.Controls.Add(_map);
        body.Controls.Add(legend);
        body.Controls.Add(_stats);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        var close = Ui.Button("إغلاق", false, 100);
        _btn.Click += async (_, _) => { if (_cts != null) _cts.Cancel(); else await StartAsync(); };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(_btn); buttons.Controls.Add(close);

        Controls.Add(body);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("فحص سطح القرص"));
        body.BringToFront();
        FormClosing += (_, e) => { if (_cts != null) { e.Cancel = true; _cts.Cancel(); } };
    }

    private async Task StartAsync()
    {
        if (_cbo.SelectedItem is not TargetItem t) return;
        var result = SurfaceScanner.CreateResult(t.Length);
        _map.Result = result;
        _map.Invalidate();
        _cts = new CancellationTokenSource();
        _btn.Text = "إيقاف";
        _cbo.Enabled = false;
        var progress = new Progress<SurfaceResult>(r => { _map.Invalidate(); ShowStats(r, t.Length); });
        try
        {
            var token = _cts.Token;
            await Task.Run(() =>
            {
                using var dev = t.Open();
                SurfaceScanner.Scan(dev, t.Offset, result, progress, token);
            }, token);
            ShowStats(result, t.Length);
            Ui.Msg(this, result.BadSectors == 0 && result.Count(BlockState.VerySlow) == 0
                    ? "✔ انتهى الفحص: لا توجد قطاعات تالفة أو ضعيفة."
                    : $"انتهى الفحص:\n• قطاعات تالفة: {result.BadSectors:N0}\n• مناطق ضعيفة (بطيئة جداً): {result.Count(BlockState.VerySlow):N0} ميجا\n\n" +
                      "يُنصح بأخذ نسخة من بياناتك المهمة واستبدال الهارد قريباً.",
                   Text, result.BadSectors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Msg(this, ex.Message, Text, MessageBoxIcon.Error); }
        finally { _cts.Dispose(); _cts = null; _btn.Text = "ابدأ الفحص"; _cbo.Enabled = true; }
    }

    private void ShowStats(SurfaceResult r, long total)
    {
        double pct = 100.0 * r.Scanned / Math.Max(1, total);
        var remaining = TimeSpan.FromSeconds((total - r.Scanned) / Math.Max(1, r.AvgSpeedMBs * (1 << 20)));
        _stats.Text = $"تم فحص {Ui.Size(r.Scanned)} من {Ui.Size(total)} ({pct:0.0}%)   —   متوسط السرعة {r.AvgSpeedMBs:0} MB/s   —   متبقي {remaining:hh\\:mm\\:ss}\n" +
                      $"سريع: {r.Count(BlockState.Fast) + r.Count(BlockState.Normal):N0}   •   بطيء: {r.Count(BlockState.Slow):N0}   •   " +
                      $"ضعيف: {r.Count(BlockState.VerySlow):N0}   •   تالف: {r.Count(BlockState.Bad):N0} منطقة ({r.BadSectors:N0} قطاع)";
    }
}

// ═══════════════════════════ اختبار السرعة ═══════════════════════════

public sealed class SpeedForm : Form
{
    private readonly ComboBox _cbo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 600 };
    private readonly CheckBox _chkWrite = new() { Text = "اختبار الكتابة أيضاً (بملف مؤقت 512MB يُحذف بعد الاختبار)", AutoSize = true, Checked = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(620, 0), Font = Ui.F(11f), Margin = new Padding(3, 14, 3, 3) };
    private readonly Button _btn = Ui.Button("ابدأ الاختبار", true, 150);
    private CancellationTokenSource? _cts;

    public SpeedForm(List<PartitionInfo> parts)
    {
        Ui.SetupForm(this, "اختبار السرعة", 680, 520);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 10, 16, 0) };
        foreach (var t in Ui.Targets(parts.Where(p => !p.IsImage), disks: true, partitions: true)) _cbo.Items.Add(t);
        if (_cbo.Items.Count > 0) _cbo.SelectedIndex = 0;
        _cbo.SelectedIndexChanged += (_, _) => UpdateWrite();
        body.Controls.Add(_cbo);
        body.Controls.Add(_chkWrite);
        body.Controls.Add(_result);
        UpdateWrite();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        var close = Ui.Button("إغلاق", false, 100);
        _btn.Click += async (_, _) => { if (_cts != null) _cts.Cancel(); else await RunAsync(); };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(_btn); buttons.Controls.Add(close);
        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("اختبار سرعة الهارد"));
        body.BringToFront();
        FormClosing += (_, e) => { if (_cts != null) { e.Cancel = true; _cts.Cancel(); } };
    }

    private void UpdateWrite()
    {
        var t = _cbo.SelectedItem as TargetItem;
        _chkWrite.Enabled = t?.Partition?.DriveLetter != null;
        if (!_chkWrite.Enabled) _chkWrite.Checked = false;
    }

    private async Task RunAsync()
    {
        if (_cbo.SelectedItem is not TargetItem t) return;
        string? folder = _chkWrite.Checked && t.Partition?.DriveLetter is { } l ? l + "\\" : null;
        _cts = new CancellationTokenSource();
        _btn.Text = "إيقاف";
        var progress = new Progress<string>(s => _result.Text = s);
        try
        {
            var token = _cts.Token;
            var r = await Task.Run(() =>
            {
                using var dev = t.Open();
                return SpeedTest.Run(dev, t.Offset, t.Length, folder, progress, token);
            }, token);
            string kind = r.RandomReadIops > 2000 ? "SSD" : "هارد عادي (HDD)";
            _result.Text =
                $"القراءة المتتالية:\n   بداية القرص: {r.SeqReadStart:0} MB/s   •   المنتصف: {r.SeqReadMiddle:0} MB/s   •   النهاية: {r.SeqReadEnd:0} MB/s\n\n" +
                $"القراءة العشوائية (4K): {r.RandomReadIops:N0} عملية/ثانية   •   زمن الاستجابة {r.RandomReadLatencyMs:0.00} ms\n" +
                (r.SeqWrite is { } w ? $"\nالكتابة المتتالية: {w:0} MB/s   •   قراءة الملف: {r.SeqReadFile:0} MB/s\nالكتابة العشوائية (4K): {r.RandomWriteIops:N0} عملية/ثانية\n" : "") +
                (r.Notes.Length > 0 ? "\n" + r.Notes + "\n" : "") +
                $"\nالتقييم: الأداء يشبه {kind}." +
                (r.SeqReadEnd < r.SeqReadStart * 0.3 && kind != "SSD" ? "\n⚠ القراءة في نهاية القرص أبطأ بكثير من المعتاد." : "");
        }
        catch (OperationCanceledException) { _result.Text = "تم الإيقاف."; }
        catch (Exception ex) { _result.Text = "✖ " + ex.Message; }
        finally { _cts.Dispose(); _cts = null; _btn.Text = "ابدأ الاختبار"; }
    }
}
