using DiskRecovery.Core;
using DiskRecovery.Core.Carving;
using DiskRecovery.Core.Imaging;

namespace DiskRecovery.UI;

// ═══════════════════════════ خيارات البحث العميق ═══════════════════════════

public sealed class DeepScanForm : Form
{
    private readonly CheckedListBox _types = new();
    private readonly CheckBox _chkFree = new() { Text = "البحث في المساحة الفارغة فقط (أسرع — لا يكرر الملفات الموجودة)", AutoSize = true, Checked = true };
    private readonly CheckBox _chkMft = new() { Text = "البحث عن سجلات الملفات القديمة (يرجّع الأسماء والمجلدات بعد الفورمات السريع)", AutoSize = true, Checked = true };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DeepScanOptions Options { get; private set; } = new();

    public DeepScanForm(PartitionInfo p)
    {
        Ui.SetupForm(this, "البحث العميق", 640, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 10, 16, 0), AutoScroll = true };
        body.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 10),
            Text = $"هيمر البرنامج على كل قطاعات «{p.DisplayName}» ({Ui.Size(p.Length)}) ويتعرف على الملفات من محتواها. " +
                   "بيلاقي الملفات حتى بعد الفورمات أو لو نظام الملفات بايظ، لكن أسماء الملفات بتضيع (إلا لو لقى سجلاتها القديمة). " +
                   "الوقت المتوقع تقريباً: " + EstimateTime(p.Length),
        });
        body.Controls.Add(new Label { Text = "أنواع الملفات:", AutoSize = true, Font = Ui.F(10f, FontStyle.Bold) });
        _types.CheckOnClick = true;
        _types.Width = 590; _types.Height = 250;
        _types.RightToLeft = RightToLeft.Yes;
        foreach (var t in Signatures.AllTypes) _types.Items.Add(new TypeItem(t), true);
        body.Controls.Add(_types);
        var all = new FlowLayoutPanel { AutoSize = true };
        var bAll = Ui.Button("الكل", false, 80); bAll.Height = 28;
        var bNone = Ui.Button("لا شيء", false, 80); bNone.Height = 28;
        bAll.Click += (_, _) => { for (int i = 0; i < _types.Items.Count; i++) _types.SetItemChecked(i, true); };
        bNone.Click += (_, _) => { for (int i = 0; i < _types.Items.Count; i++) _types.SetItemChecked(i, false); };
        all.Controls.Add(bAll); all.Controls.Add(bNone);
        body.Controls.Add(all);
        _chkFree.Enabled = QuickScan.IsSupported(p.FileSystem);
        if (!_chkFree.Enabled) _chkFree.Checked = false;
        _chkMft.Enabled = p.FileSystem is "NTFS" or "غير معروف" or "";
        if (!_chkMft.Enabled) _chkMft.Checked = false;
        body.Controls.Add(_chkFree);
        body.Controls.Add(_chkMft);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        var ok = Ui.Button("ابدأ البحث", true, 160, Color.FromArgb(90, 60, 150));
        var cancel = Ui.Button("إلغاء", false, 100);
        ok.Click += (_, _) =>
        {
            var set = _types.CheckedItems.Cast<TypeItem>().Select(t => t.Key).ToHashSet();
            if (set.Count == 0 && !_chkMft.Checked) { Ui.Msg(this, "اختر نوع ملف واحد على الأقل.", Text, MessageBoxIcon.Information); return; }
            Options = new DeepScanOptions { Types = set, FreeSpaceOnly = _chkFree.Checked, OldMftRecords = _chkMft.Checked };
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("البحث العميق بالتوقيع"));
        body.BringToFront();
    }

    private static string EstimateTime(long bytes)
    {
        double minutesHdd = bytes / (120.0 * (1 << 20)) / 60, minutesSsd = bytes / (450.0 * (1 << 20)) / 60;
        string F(double m) => m < 1 ? "أقل من دقيقة" : m < 90 ? $"{m:0} دقيقة" : $"{m / 60:0.#} ساعة";
        return $"{F(minutesSsd)} على SSD، و {F(minutesHdd)} على هارد عادي.";
    }

    private sealed record TypeItem(string Key)
    {
        public override string ToString() => Signatures.TypeName(Key);
    }
}

// ═══════════════════════════ نسخة كاملة من القرص ═══════════════════════════

public sealed class ImagingForm : Form
{
    private readonly ComboBox _cboSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
    private readonly TextBox _txtDest = new() { Width = 450 };
    private readonly ProgressBar _bar = new() { Width = 590, Height = 22 };
    private readonly Label _lblStatus = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly Button _btnStart = Ui.Button("ابدأ النسخ", true, 150);
    private readonly Button _btnClose = Ui.Button("إغلاق", false, 100);
    private CancellationTokenSource? _cts;
    private readonly List<PartitionInfo> _parts;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? CreatedImage { get; private set; }

    public ImagingForm(List<PartitionInfo> parts, PartitionInfo? selected)
    {
        _parts = parts;
        Ui.SetupForm(this, "نسخة كاملة من القرص", 640, 470);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 10, 16, 0) };
        body.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 10),
            Text = "مفيدة جداً لو الهارد ضعيف أو بيصدر صوت: البرنامج يقرأه مرة واحدة فقط ويتخطى الأماكن التالفة ثم يرجع لها في الآخر. " +
                   "بعدها تسترجع ملفاتك من النسخة براحتك بدون إجهاد الهارد. القرص الأصلي يُقرأ فقط.",
        });
        body.Controls.Add(new Label { Text = "المصدر:", AutoSize = true, Font = Ui.F(10f, FontStyle.Bold) });
        var targets = Ui.Targets(parts.Where(p => !p.IsImage), disks: true, partitions: true);
        _cboSource.Items.AddRange(targets.ToArray<object>());
        var pre = targets.FirstOrDefault(t => selected != null && t.Partition == selected) ?? targets.FirstOrDefault();
        if (pre != null) _cboSource.SelectedItem = pre;
        body.Controls.Add(_cboSource);

        body.Controls.Add(new Label { Text = "حفظ النسخة في:", AutoSize = true, Font = Ui.F(10f, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var browse = Ui.Button("اختيار...", false, 100); browse.Height = 30;
        browse.Click += (_, _) => Browse();
        row.Controls.Add(_txtDest); row.Controls.Add(browse);
        body.Controls.Add(row);
        body.Controls.Add(new Label { Height = 10 });
        body.Controls.Add(_bar);
        body.Controls.Add(_lblStatus);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnClose.Click += (_, _) => { if (_cts != null) _cts.Cancel(); else Close(); };
        buttons.Controls.Add(_btnStart); buttons.Controls.Add(_btnClose);

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("نسخة كاملة (Image)"));
        body.BringToFront();
        FormClosing += (_, e) => { if (_cts != null) { e.Cancel = true; _cts.Cancel(); } };
    }

    private void Browse()
    {
        using var dlg = new SaveFileDialog { Filter = "ملف نسخة|*.img", FileName = $"نسخة {DateTime.Now:yyyy-MM-dd}.img", OverwritePrompt = false };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtDest.Text = dlg.FileName;
    }

    private async Task StartAsync()
    {
        if (_cboSource.SelectedItem is not TargetItem src) return;
        string dest = _txtDest.Text.Trim();
        if (dest.Length == 0) { Browse(); dest = _txtDest.Text.Trim(); }
        if (dest.Length == 0) return;

        // لا نحفظ النسخة على نفس القرص المصدر
        string root = (Path.GetPathRoot(Path.GetFullPath(dest)) ?? "").TrimEnd('\\');
        bool onSource = src.IsWholeDisk
            ? _parts.Any(p => p.DiskNumber == src.DiskNumber && !p.IsImage && root.Equals(p.DriveLetter, StringComparison.OrdinalIgnoreCase))
            : root.Equals(src.Partition?.DriveLetter, StringComparison.OrdinalIgnoreCase);
        if (onSource) { Ui.Msg(this, "لا يمكن حفظ النسخة على نفس القرص أو البارتشن المصدر — اختر هارد آخر.", Text, MessageBoxIcon.Stop); return; }
        try
        {
            long free = new DriveInfo(root + "\\").AvailableFreeSpace;
            if (free < src.Length) { Ui.Msg(this, $"المساحة الفارغة ({Ui.Size(free)}) أقل من حجم النسخة ({Ui.Size(src.Length)}).", Text, MessageBoxIcon.Stop); return; }
        }
        catch { }

        long resume = DiskImager.GetResumeOffset(dest, src.Length);
        if (resume > 0)
        {
            var r = Ui.Msg(this, $"يوجد نسخ سابق لم يكتمل ({Ui.Size(resume)} من {Ui.Size(src.Length)}).\nهل تريد الاستكمال من حيث توقف؟",
                           Text, MessageBoxIcon.Question, MessageBoxButtons.YesNoCancel);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.No) resume = 0;
        }
        else if (File.Exists(dest) &&
                 Ui.Msg(this, "الملف موجود. استبداله؟", Text, MessageBoxIcon.Question, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        _btnStart.Enabled = false; _cboSource.Enabled = false; _txtDest.Enabled = false;
        _btnClose.Text = "إيقاف";
        var progress = new Progress<ImageProgress>(p =>
        {
            _bar.Value = (int)Math.Clamp(p.Done * 100 / Math.Max(1, p.Total), 0, 100);
            _lblStatus.Text = $"{p.Stage}\n{Ui.Size(p.Done)} / {Ui.Size(p.Total)}   —   {p.SpeedMBs:0.#} MB/s   —   متبقي {p.Eta:hh\\:mm\\:ss}" +
                              (p.BadSectors > 0 ? $"\n⚠ مناطق تالفة: {p.BadSectors:N0} قطاع" : "");
        });
        try
        {
            var token = _cts.Token;
            var report = await Task.Run(() =>
            {
                using var dev = src.Open();
                return DiskImager.Create(dev, src.Offset, src.Length, dest, resume, progress, token);
            }, token);
            CreatedImage = dest;
            _lblStatus.Text = report.BadSectors == 0
                ? "✔ تمت النسخة بنجاح بدون أي قطاعات تالفة."
                : $"✔ تمت النسخة. تعذرت قراءة {report.BadSectors:N0} قطاع (تم وضع أصفار مكانها).\nالتفاصيل في: {report.BadSectorLog}";
            _bar.Value = 100;
        }
        catch (OperationCanceledException) { _lblStatus.Text = "تم الإيقاف. تقدر تستكمل لاحقاً من نفس المكان."; }
        catch (Exception ex) { _lblStatus.Text = "✖ " + ex.Message; }
        finally
        {
            _cts.Dispose(); _cts = null;
            _btnStart.Enabled = true; _cboSource.Enabled = true; _txtDest.Enabled = true;
            _btnClose.Text = "إغلاق";
        }
    }
}

// ═══════════════════════════ البارتشنات المفقودة ═══════════════════════════

public sealed class LostPartitionsForm : Form
{
    private readonly ComboBox _cboDisk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
    private readonly RadioButton _rQuick = new() { Text = "بحث سريع (الأماكن المعتادة لبداية البارتشنات — ثواني)", AutoSize = true, Checked = true };
    private readonly RadioButton _rDeep = new() { Text = "بحث شامل (كل قطاعات المساحة غير المقسّمة — قد يستغرق ساعات)", AutoSize = true };
    private readonly DataGridView _grid = new();
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Bottom, Height = 18 };
    private readonly Button _btnFind = Ui.Button("ابدأ البحث", true, 150);
    private readonly Button _btnAdd = Ui.Button("إضافة المحدد للقائمة", false, 190);
    private readonly List<PartitionInfo> _parts;
    private CancellationTokenSource? _cts;
    private List<FoundPartition> _found = new();
    public List<PartitionInfo> Added { get; } = new();

    public LostPartitionsForm(List<PartitionInfo> parts)
    {
        _parts = parts;
        Ui.SetupForm(this, "البحث عن بارتشنات مفقودة", 720, 560);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 170, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 8, 16, 0) };
        top.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(670, 0), ForeColor = Color.DimGray,
            Text = "لو بارتشن اختفى أو اتمسح من جدول البارتشن، بياناته غالباً لسه موجودة. البرنامج يدور على قطاع الإقلاع بتاعه في المساحة غير المقسّمة. " +
                   "البارتشن اللي يتلاقى يتضاف للقائمة وتقدر تفحصه وتسترجع ملفاته مباشرة — بدون أي تعديل على الهارد.",
        });
        foreach (var t in Ui.Targets(parts.Where(p => !p.IsLost), disks: true, partitions: false)) _cboDisk.Items.Add(t);
        if (_cboDisk.Items.Count > 0) _cboDisk.SelectedIndex = 0;
        top.Controls.Add(_cboDisk);
        top.Controls.Add(_rQuick);
        top.Controls.Add(_rDeep);

        Ui.StyleGrid(_grid);
        _grid.ReadOnly = true;
        Ui.AddColumn(_grid, "fs", "النظام", 10);
        Ui.AddColumn(_grid, "off", "البداية", 15);
        Ui.AddColumn(_grid, "size", "الحجم", 12);
        Ui.AddColumn(_grid, "ev", "الدليل", 30);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        var close = Ui.Button("إغلاق", false, 100);
        _btnFind.Click += async (_, _) => await FindAsync();
        _btnAdd.Click += (_, _) => AddSelected();
        close.Click += (_, _) => { if (_cts != null) _cts.Cancel(); else Close(); };
        _btnAdd.Enabled = false;
        buttons.Controls.Add(_btnFind); buttons.Controls.Add(_btnAdd); buttons.Controls.Add(close);

        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 4) };
        mid.Controls.Add(_grid);
        mid.Controls.Add(_bar);
        Controls.Add(mid);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(Ui.Header("البحث عن بارتشنات مفقودة"));
        mid.BringToFront();
        FormClosing += (_, e) => { if (_cts != null) { e.Cancel = true; _cts.Cancel(); } };
    }

    private async Task FindAsync()
    {
        if (_cboDisk.SelectedItem is not TargetItem disk) return;
        bool deep = _rDeep.Checked;
        var existing = _parts.Where(p => p.DiskNumber == disk.DiskNumber && p.ImagePath == disk.ImagePath && !p.IsLost)
                             .Select(p => (p.Offset, p.Length)).ToList();
        _cts = new CancellationTokenSource();
        _btnFind.Enabled = false;
        _grid.Rows.Clear();
        var progress = new Progress<ScanProgress>(p => _bar.Value = (int)Math.Clamp(p.Total > 0 ? p.Done * 100 / p.Total : 0, 0, 100));
        try
        {
            var token = _cts.Token;
            _found = await Task.Run(() =>
            {
                using var dev = disk.Open();
                return PartitionFinder.Find(dev, disk.Length, existing, deep, progress, token);
            }, token);
            foreach (var f in _found) _grid.Rows.Add(f.FileSystem, Ui.Size(f.Offset), Ui.Size(f.Length), f.Evidence);
            _btnAdd.Enabled = _found.Count > 0;
            if (_found.Count == 0)
                Ui.Msg(this, deep ? "لم يتم العثور على بارتشنات مفقودة." : "لم يتم العثور على شيء. جرّب البحث الشامل.", Text, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.Msg(this, ex.Message, Text, MessageBoxIcon.Error); }
        finally { _cts.Dispose(); _cts = null; _btnFind.Enabled = true; _bar.Value = 0; }
    }

    private void AddSelected()
    {
        if (_cboDisk.SelectedItem is not TargetItem disk) return;
        var rows = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _found.Count).ToList();
        if (rows.Count == 0) rows = Enumerable.Range(0, _found.Count).ToList();
        var first = _parts.FirstOrDefault(p => p.DiskNumber == disk.DiskNumber && p.ImagePath == disk.ImagePath);
        foreach (var i in rows)
        {
            var f = _found[i];
            Added.Add(new PartitionInfo
            {
                DiskNumber = disk.DiskNumber, ImagePath = disk.ImagePath, DiskName = first?.DiskName ?? disk.Text,
                DiskSize = disk.Length, SectorSize = disk.SectorSize, Style = "—", PartitionNumber = 0,
                Offset = f.Offset, Length = Math.Min(f.Length, disk.Length - f.Offset), TypeName = "بارتشن مفقود",
                FileSystem = f.FileSystem, IsLost = true, Label = $"مفقود ({f.FileSystem})",
            });
        }
        DialogResult = DialogResult.OK;
    }
}
