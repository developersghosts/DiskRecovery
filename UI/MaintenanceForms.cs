using DiskRecovery.Core;
using DiskRecovery.Core.Maintenance;

namespace DiskRecovery.UI;

/// <summary>أساس مشترك لنافذتي الإصلاح والفورمات: خيارات بالأعلى وسجل تنفيذ بالأسفل.</summary>
public abstract class MaintenanceFormBase : Form
{
    protected static readonly Color Accent = Color.FromArgb(28, 78, 128);
    protected static readonly Color Danger = Color.FromArgb(176, 32, 32);

    protected readonly PartitionInfo Partition;
    protected readonly Panel Options = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 10, 14, 4) };
    protected readonly TextBox Log = new();
    protected readonly Button BtnStart = new();
    protected readonly Button BtnClose = new();
    private CancellationTokenSource? _cts;
    private bool _allowCancel;

    /// <summary>صحيح لو تم تنفيذ عملية غيّرت البارتشن (لتحديث القائمة في النافذة الرئيسية).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Changed { get; protected set; }

    protected MaintenanceFormBase(PartitionInfo p, string title)
    {
        Partition = p;
        Text = title;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(720, 600);
        MinimumSize = new Size(600, 480);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(246, 248, 251);

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 54, BackColor = Accent, ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 14, 0),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Text = $"Disk {p.DiskNumber} — بارتشن {p.PartitionNumber}   |   {p.DriveLetter ?? "بدون حرف"}   |   " +
                   $"{p.DisplayName}   |   {p.FileSystem}   |   {Ui.Size(p.Length)}",
        };

        Log.Multiline = true;
        Log.ReadOnly = true;
        Log.ScrollBars = ScrollBars.Vertical;
        Log.Dock = DockStyle.Fill;
        Log.BackColor = Color.FromArgb(30, 34, 40);
        Log.ForeColor = Color.FromArgb(220, 230, 240);
        Log.Font = new Font("Segoe UI", 9.5f);
        var logBox = new GroupBox { Text = "  سجل التنفيذ  ", Dock = DockStyle.Fill, Padding = new Padding(10) };
        logBox.Controls.Add(Log);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 0) };
        StyleButton(BtnStart, "", true, 200);
        StyleButton(BtnClose, "إغلاق", false, 120);
        BtnStart.Click += async (_, _) => await StartAsync();
        BtnClose.Click += (_, _) =>
        {
            if (_cts != null && _allowCancel) _cts.Cancel();
            else if (_cts == null) Close();
        };
        buttons.Controls.Add(BtnStart);
        buttons.Controls.Add(BtnClose);

        Controls.Add(logBox);
        Controls.Add(buttons);
        Controls.Add(Options);
        Controls.Add(info);
        logBox.BringToFront();

        FormClosing += (_, e) =>
        {
            if (_cts == null) return;
            e.Cancel = true;
            if (_allowCancel) _cts.Cancel();
            else Msg("لا يمكن إغلاق النافذة أثناء التنفيذ — قطع العملية في المنتصف قد يتلف البارتشن.", MessageBoxIcon.Warning);
        };
    }

    /// <summary>يرجع null لو المستخدم ألغى، وإلا العملية المطلوب تنفيذها.</summary>
    protected abstract Func<Action<string>, CancellationToken, Task<int>>? Prepare(out bool allowCancel);
    protected virtual void OnFinished(int exitCode) { }
    protected virtual void RefreshStartState() => BtnStart.Enabled = true;

    private async Task StartAsync()
    {
        var op = Prepare(out _allowCancel);
        if (op == null) return;

        Options.Enabled = false;
        BtnStart.Enabled = false;
        BtnClose.Text = _allowCancel ? "إيقاف" : "إغلاق";
        BtnClose.Enabled = _allowCancel;
        UseWaitCursor = true;
        Log.Clear();
        _cts = new CancellationTokenSource();

        void Append(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Append(line))); return; }
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }

        try
        {
            int code = await op(Append, _cts.Token);
            Append(code == 0 ? "✔ انتهت العملية بنجاح." : $"✖ انتهت العملية برمز {code} — راجع السجل.");
            OnFinished(code);
        }
        catch (OperationCanceledException) { Append("تم الإيقاف."); }
        catch (Exception ex) { Append("✖ خطأ: " + ex.Message); }
        finally
        {
            _cts.Dispose(); _cts = null;
            UseWaitCursor = false;
            BtnClose.Text = "إغلاق";
            BtnClose.Enabled = true;
            Options.Enabled = true;
            RefreshStartState();
        }
    }

    protected DialogResult Msg(string text, MessageBoxIcon icon, MessageBoxButtons buttons = MessageBoxButtons.OK,
                               MessageBoxDefaultButton def = MessageBoxDefaultButton.Button1) =>
        MessageBox.Show(this, text, Text, buttons, icon, def, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    protected static void StyleButton(Button b, string text, bool primary, int width, Color? color = null)
    {
        var c = color ?? Accent;
        b.Text = text;
        b.Width = width;
        b.Height = 36;
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderColor = c;
        b.BackColor = primary ? c : Color.White;
        b.ForeColor = primary ? Color.White : c;
        if (primary) b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        b.Margin = new Padding(6, 3, 6, 3);
    }

    protected static Label Note(string text, Color color, bool bold = false) => new()
    {
        Text = text, ForeColor = color, AutoSize = true, MaximumSize = new Size(660, 0),
        Margin = new Padding(3, 4, 3, 8),
        Font = new Font("Segoe UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular),
    };
}

// ═══════════════════════════════ نافذة الإصلاح ═══════════════════════════════

public sealed class RepairForm : MaintenanceFormBase
{
    private readonly RadioButton _rScan = new() { Text = "فحص فقط بدون أي تعديل (آمن تماماً)", AutoSize = true, Checked = true };
    private readonly RadioButton _rFix = new() { Text = "فحص وإصلاح أخطاء نظام الملفات  (chkdsk /f)", AutoSize = true };
    private readonly RadioButton _rDeep = new() { Text = "إصلاح شامل + فحص القطاعات التالفة  (chkdsk /r) — بطيء جداً", AutoSize = true };
    private readonly int _deletedFiles;

    public RepairForm(PartitionInfo p, int deletedRecoverableFiles) : base(p, "إصلاح البارتشن")
    {
        _deletedFiles = deletedRecoverableFiles;
        StyleButton(BtnStart, "بدء", true, 200);

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(Note("اختر نوع العملية:", Accent, bold: true));
        flow.Controls.Add(_rScan);
        flow.Controls.Add(_rFix);
        flow.Controls.Add(_rDeep);
        flow.Controls.Add(Note(
            "• الإصلاح يكتب على البارتشن وقد يمسح سجلات الملفات المحذوفة، فاسترجع ملفاتك المهمة أولاً.\n" +
            "• أثناء الإصلاح يتم فصل البارتشن مؤقتاً، فاقفل أي ملفات مفتوحة منه.\n" +
            (VolumeTools.IsSystemVolume(p) ? "• هذا بارتشن الويندوز: الإصلاح سيتم تلقائياً عند إعادة تشغيل الجهاز.\n" : ""),
            Color.DimGray));
        if (_deletedFiles > 0)
            flow.Controls.Add(Note($"⚠ يوجد {_deletedFiles:N0} ملف محذوف قابل للاسترجاع على هذا البارتشن. استرجعها قبل الإصلاح!",
                                   Danger, bold: true));
        Options.Controls.Add(flow);
    }

    protected override Func<Action<string>, CancellationToken, Task<int>>? Prepare(out bool allowCancel)
    {
        var mode = _rScan.Checked ? RepairMode.ScanOnly : _rFix.Checked ? RepairMode.FixErrors : RepairMode.FixAndBadSectors;
        allowCancel = mode == RepairMode.ScanOnly;

        if (VolumeTools.GetProtectionReason(Partition, forFormat: false) is { } reason)
        {
            Msg(reason, MessageBoxIcon.Stop);
            return null;
        }
        if (mode != RepairMode.ScanOnly)
        {
            string warn = "سيتم تعديل البارتشن لإصلاح الأخطاء.\n\n";
            if (_deletedFiles > 0) warn += $"تنبيه: يوجد {_deletedFiles:N0} ملف محذوف قابل للاسترجاع وقد يضيع بعد الإصلاح.\n\n";
            warn += "هل تريد المتابعة؟";
            if (Msg(warn, MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return null;
            Changed = true;
        }
        return (log, ct) => VolumeTools.RepairAsync(Partition, mode, log, ct);
    }
}

// ═══════════════════════════════ نافذة الفورمات ═══════════════════════════════

public sealed class FormatForm : MaintenanceFormBase
{
    private const string ConfirmWord = "FORMAT";
    private readonly ComboBox _cboFs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly TextBox _txtLabel = new() { Width = 240, MaxLength = 32 };
    private readonly CheckBox _chkQuick = new() { Text = "فورمات سريع", Checked = true, AutoSize = true };
    private readonly CheckBox _chkUnderstand = new() { AutoSize = true };
    private readonly TextBox _txtConfirm = new() { Width = 140, CharacterCasing = CharacterCasing.Upper };
    private readonly int _deletedFiles;

    public FormatForm(PartitionInfo p, int deletedRecoverableFiles) : base(p, "فورمات البارتشن")
    {
        _deletedFiles = deletedRecoverableFiles;
        StyleButton(BtnStart, "فورمات الآن", true, 200, Danger);

        _cboFs.Items.AddRange(new object[] { "NTFS", "exFAT", "FAT32" });
        _cboFs.SelectedIndex = 0;
        _txtLabel.Text = p.Label;
        _chkUnderstand.Text = "أفهم أن كل البيانات على هذا البارتشن ستُمسح، وأن الملفات المحذوفة لن يمكن استرجاعها بعد ذلك";
        _chkUnderstand.MaximumSize = new Size(660, 0);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        grid.Controls.Add(new Label { Text = "نظام الملفات:", AutoSize = true, Anchor = AnchorStyles.Right }, 0, 0);
        grid.Controls.Add(_cboFs, 1, 0);
        grid.Controls.Add(new Label { Text = "اسم البارتشن:", AutoSize = true, Anchor = AnchorStyles.Right }, 0, 1);
        grid.Controls.Add(_txtLabel, 1, 1);
        grid.Controls.Add(new Label(), 0, 2);
        grid.Controls.Add(_chkQuick, 1, 2);

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(grid);
        flow.Controls.Add(Note(
            "• NTFS للهاردات الداخلية والخارجية الكبيرة — exFAT للفلاشات والهاردات اللي هتتنقل بين أجهزة — FAT32 حد أقصى 32GB.\n" +
            "• الفورمات الكامل (بدون علامة السريع) يكتب أصفار على البارتشن كله ويكشف القطاعات التالفة، لكنه بطيء جداً.",
            Color.DimGray));
        if (_deletedFiles > 0)
            flow.Controls.Add(Note($"⚠ يوجد {_deletedFiles:N0} ملف محذوف قابل للاسترجاع على هذا البارتشن. الفورمات سيضيعها!", Danger, bold: true));
        flow.Controls.Add(_chkUnderstand);
        var confirmRow = new FlowLayoutPanel { AutoSize = true };
        confirmRow.Controls.Add(new Label { Text = $"للتأكيد اكتب  {ConfirmWord}  هنا:", AutoSize = true, ForeColor = Danger, Margin = new Padding(3, 6, 3, 0) });
        confirmRow.Controls.Add(_txtConfirm);
        flow.Controls.Add(confirmRow);
        Options.Controls.Add(flow);

        _chkUnderstand.CheckedChanged += (_, _) => RefreshStartState();
        _txtConfirm.TextChanged += (_, _) => RefreshStartState();
        _cboFs.SelectedIndexChanged += (_, _) =>
        {
            if (_cboFs.Text == "FAT32" && Partition.Length > 32L * 1024 * 1024 * 1024)
                Msg("ويندوز لا يقبل فورمات FAT32 لبارتشن أكبر من 32GB. استخدم exFAT بدلاً منه.", MessageBoxIcon.Information);
        };
        Shown += (_, _) => RefreshStartState();
    }

    protected override Func<Action<string>, CancellationToken, Task<int>>? Prepare(out bool allowCancel)
    {
        allowCancel = false;
        if (VolumeTools.GetProtectionReason(Partition, forFormat: true) is { } reason)
        {
            Msg(reason, MessageBoxIcon.Stop);
            return null;
        }
        string label = new string(_txtLabel.Text.Where(c => !"\\/:*?\"<>|".Contains(c)).ToArray()).Trim();
        var opts = new FormatOptions(_cboFs.Text, label, _chkQuick.Checked);

        string target = Partition.DriveLetter ?? $"البارتشن رقم {Partition.PartitionNumber} على Disk {Partition.DiskNumber}";
        if (Msg($"تأكيد أخير:\n\nسيتم مسح كل محتويات {target} ({Ui.Size(Partition.Length)}) نهائياً.\n\nمتابعة؟",
                MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return null;

        Changed = true;
        return (log, ct) => VolumeTools.FormatAsync(Partition, opts, log, ct);
    }

    protected override void RefreshStartState() =>
        BtnStart.Enabled = _chkUnderstand.Checked && _txtConfirm.Text.Trim() == ConfirmWord;

    protected override void OnFinished(int exitCode)
    {
        _txtConfirm.Clear();
        _chkUnderstand.Checked = false;
    }
}

// ═══════════════════════════════ إدارة البارتشنات ═══════════════════════════════

public sealed class PartitionManagerForm : MaintenanceFormBase
{
    private readonly RadioButton _rAssign = new() { Text = "إعطاء حرف (لإظهار بارتشن مخفي)", AutoSize = true };
    private readonly ComboBox _cboLetter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly RadioButton _rRemove = new() { Text = "إزالة الحرف (إخفاء البارتشن)", AutoSize = true };
    private readonly RadioButton _rResize = new() { Text = "تغيير الحجم إلى (GB):", AutoSize = true };
    private readonly NumericUpDown _numSize = new() { Width = 120, DecimalPlaces = 2, Maximum = 1_000_000 };
    private readonly Label _lblRange = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "جاري حساب الحدود..." };
    private readonly RadioButton _rCreate = new() { Text = "إنشاء بارتشن جديد في المساحة غير المقسّمة على نفس القرص", AutoSize = true };
    private readonly ComboBox _cboFs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _txtLabel = new() { Width = 160, Text = "New Volume" };
    private readonly Label _lblFree = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly RadioButton _rDelete = new() { Text = "حذف البارتشن نهائياً", AutoSize = true, ForeColor = Color.FromArgb(176, 32, 32) };
    private readonly TextBox _txtConfirm = new() { Width = 120, CharacterCasing = CharacterCasing.Upper };
    private (long Min, long Max)? _range;
    private long _freeSpace;

    public PartitionManagerForm(PartitionInfo p) : base(p, "إدارة البارتشنات")
    {
        StyleButton(BtnStart, "تنفيذ", true, 200);
        _cboLetter.Items.Add("تلقائي");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'D'; c <= 'Z'; c++) if (!used.Contains(c)) _cboLetter.Items.Add(c.ToString());
        _cboLetter.SelectedIndex = 0;
        _cboFs.Items.AddRange(new object[] { "NTFS", "exFAT", "FAT32" });
        _cboFs.SelectedIndex = 0;

        FlowLayoutPanel Row(params Control[] c) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; f.Controls.AddRange(c); return f; }
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(Note("اختر العملية:", Accent, bold: true));
        flow.Controls.Add(Row(_rAssign, _cboLetter));
        flow.Controls.Add(_rRemove);
        flow.Controls.Add(Row(_rResize, _numSize, _lblRange));
        flow.Controls.Add(_rCreate);
        flow.Controls.Add(Row(new Label { Text = "      النظام:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, _cboFs,
                              new Label { Text = "الاسم:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, _txtLabel, _lblFree));
        flow.Controls.Add(Row(_rDelete, new Label { Text = "   للتأكيد اكتب DELETE:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, _txtConfirm));
        flow.Controls.Add(Note("• تصغير البارتشن ممكن يأخذ وقت لو عليه بيانات كثيرة.  • الحذف يمسح البارتشن من الجدول (البيانات يمكن استرجاعها بالبحث عن البارتشنات المفقودة لو لم يُكتب فوقها).", Color.DimGray));
        Options.Controls.Add(flow);

        bool hasLetter = p.DriveLetter != null;
        _rAssign.Enabled = _cboLetter.Enabled = !hasLetter;
        _rRemove.Enabled = hasLetter && !VolumeTools.IsSystemVolume(p);
        (hasLetter ? _rResize : _rAssign).Checked = true;
        Shown += async (_, _) => await LoadLimitsAsync();
    }

    private async Task LoadLimitsAsync()
    {
        try
        {
            _range = await PartitionManager.GetSupportedSizeAsync(Partition, CancellationToken.None);
            if (_range is { } r)
            {
                _numSize.Minimum = (decimal)Math.Round(r.Min / 1073741824.0, 2) + 0.01m;
                _numSize.Maximum = Math.Max(_numSize.Minimum, (decimal)Math.Round(r.Max / 1073741824.0, 2));
                _numSize.Value = Math.Clamp((decimal)Math.Round(Partition.Length / 1073741824.0, 2), _numSize.Minimum, _numSize.Maximum);
                _lblRange.Text = $"من {Ui.Size(r.Min)} إلى {Ui.Size(r.Max)}";
            }
            else { _lblRange.Text = "غير متاح لهذا البارتشن"; _rResize.Enabled = false; }
        }
        catch { _lblRange.Text = "غير متاح"; _rResize.Enabled = false; }
        try
        {
            _freeSpace = await PartitionManager.GetLargestFreeExtentAsync(Partition.DiskNumber, CancellationToken.None);
            _lblFree.Text = _freeSpace > 16 << 20 ? $"المساحة المتاحة: {Ui.Size(_freeSpace)}" : "لا توجد مساحة غير مقسّمة";
            _rCreate.Enabled = _freeSpace > 16 << 20;
        }
        catch { _rCreate.Enabled = false; }
    }

    protected override Func<Action<string>, CancellationToken, Task<int>>? Prepare(out bool allowCancel)
    {
        allowCancel = false;
        var p = Partition;
        if (_rAssign.Checked)
        {
            char? letter = _cboLetter.SelectedIndex > 0 ? _cboLetter.Text[0] : null;
            Changed = true;
            return (log, ct) => PartitionManager.AssignLetterAsync(p, letter, log, ct);
        }
        if (_rRemove.Checked)
        {
            if (Msg($"سيختفي البارتشن {p.DriveLetter} من «هذا الكمبيوتر» (البيانات تبقى كما هي). متابعة؟", MessageBoxIcon.Question, MessageBoxButtons.YesNo) != DialogResult.Yes) return null;
            Changed = true;
            return (log, ct) => PartitionManager.RemoveLetterAsync(p, log, ct);
        }
        if (_rResize.Checked)
        {
            if (VolumeTools.GetProtectionReason(p, forFormat: false) is { } why) { Msg(why, MessageBoxIcon.Stop); return null; }
            long size = (long)(_numSize.Value * 1073741824m);
            if (_range is { } r) size = Math.Clamp(size, r.Min, r.Max);
            if (Msg($"تغيير حجم البارتشن من {Ui.Size(p.Length)} إلى {Ui.Size(size)}؟\nيُنصح بأخذ نسخة احتياطية قبل تغيير الأحجام.", MessageBoxIcon.Question, MessageBoxButtons.YesNo) != DialogResult.Yes) return null;
            Changed = true;
            return (log, ct) => PartitionManager.ResizeAsync(p, size, log, ct);
        }
        if (_rCreate.Checked)
        {
            string fs = _cboFs.Text, label = _txtLabel.Text.Trim();
            Changed = true;
            return (log, ct) => PartitionManager.CreateAsync(p.DiskNumber, null, fs, label, log, ct);
        }
        if (_rDelete.Checked)
        {
            if (VolumeTools.GetProtectionReason(p, forFormat: true) is { } why) { Msg(why, MessageBoxIcon.Stop); return null; }
            if (_txtConfirm.Text.Trim() != "DELETE") { Msg("اكتب كلمة DELETE للتأكيد.", MessageBoxIcon.Information); return null; }
            if (Msg($"حذف البارتشن {p.DriveLetter ?? p.DisplayName} ({Ui.Size(p.Length)}) نهائياً؟", MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
            Changed = true;
            return (log, ct) => PartitionManager.DeleteAsync(p, log, ct);
        }
        return null;
    }
}

// ═══════════════════════════════ المسح الآمن ═══════════════════════════════

public sealed class WipeForm : MaintenanceFormBase
{
    private readonly RadioButton _rFree = new() { Text = "مسح المساحة الفارغة فقط (يمسح أثر كل الملفات المحذوفة — الملفات الموجودة لا تتأثر)", AutoSize = true, Checked = true };
    private readonly RadioButton _rFiles = new() { Text = "مسح ملفات أو مجلدات محددة نهائياً", AutoSize = true };
    private readonly ListBox _list = new() { Width = 520, Height = 90, HorizontalScrollbar = true };
    private readonly NumericUpDown _passes = new() { Minimum = 1, Maximum = 7, Value = 3, Width = 60 };
    private readonly RadioButton _rAll = new() { Text = "مسح البارتشن كله (كتابة أصفار على كل القطاعات)", AutoSize = true, ForeColor = Color.FromArgb(176, 32, 32) };
    private readonly TextBox _txtConfirm = new() { Width = 120, CharacterCasing = CharacterCasing.Upper };

    public WipeForm(PartitionInfo p) : base(p, "المسح الآمن")
    {
        StyleButton(BtnStart, "ابدأ المسح", true, 200, Color.FromArgb(176, 32, 32));
        FlowLayoutPanel Row(params Control[] c) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; f.Controls.AddRange(c); return f; }
        var addFiles = MakeButton("إضافة ملفات...", 130);
        var addFolder = MakeButton("إضافة مجلد...", 130);
        var clear = MakeButton("مسح القائمة", 110);
        addFiles.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Multiselect = true, Title = "اختر الملفات المراد مسحها نهائياً" };
            if (d.ShowDialog(this) == DialogResult.OK) { _list.Items.AddRange(d.FileNames); _rFiles.Checked = true; }
        };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "اختر المجلد المراد مسحه نهائياً بكل محتوياته", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) { _list.Items.Add(d.SelectedPath); _rFiles.Checked = true; }
        };
        clear.Click += (_, _) => _list.Items.Clear();

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(Note("المسح الآمن يجعل البيانات غير قابلة للاسترجاع بأي برنامج — استخدمه قبل بيع جهاز أو هارد.", Danger, bold: true));
        flow.Controls.Add(_rFree);
        flow.Controls.Add(_rFiles);
        flow.Controls.Add(Row(_list, new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Controls = { addFiles, addFolder, clear } }));
        flow.Controls.Add(Row(new Label { Text = "      عدد مرات الكتابة:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, _passes));
        flow.Controls.Add(Row(_rAll, new Label { Text = "   للتأكيد اكتب WIPE:", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, _txtConfirm));
        flow.Controls.Add(Note("• على SSD: مسح المساحة الفارغة أو البارتشن كله هو الأضمن، لأن الـ SSD قد يحتفظ بنسخ قديمة من الملفات في أماكن أخرى.", Color.DimGray));
        Options.Controls.Add(flow);
        _rFree.Enabled = p.DriveLetter != null;
        if (!_rFree.Enabled) _rFiles.Checked = true;
    }

    private static Button MakeButton(string text, int w)
    {
        var b = new Button();
        StyleButton(b, text, false, w);
        b.Height = 28;
        return b;
    }

    protected override Func<Action<string>, CancellationToken, Task<int>>? Prepare(out bool allowCancel)
    {
        allowCancel = true;
        var p = Partition;
        if (_rFree.Checked)
        {
            if (Msg($"سيتم الكتابة فوق كل المساحة الفارغة في {p.DriveLetter}.\nأي ملف محذوف عليه لن يمكن استرجاعه بعد ذلك. متابعة؟",
                    MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
            return (log, ct) => SecureWipe.WipeFreeSpaceAsync(p, log, ct);
        }
        if (_rFiles.Checked)
        {
            var paths = _list.Items.Cast<string>().ToList();
            if (paths.Count == 0) { Msg("أضف الملفات أو المجلدات أولاً.", MessageBoxIcon.Information); return null; }
            if (paths.Any(x => x.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                            || x.TrimEnd('\\').Length <= 3))
            { Msg("لا يمكن مسح ملفات الويندوز أو قرص كامل من هنا.", MessageBoxIcon.Stop); return null; }
            if (Msg($"سيتم مسح {paths.Count} عنصر نهائياً بدون إمكانية استرجاع. متابعة؟", MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
            int passes = (int)_passes.Value;
            return (log, ct) => Task.Run(() => { int n = SecureWipe.WipeFiles(paths, passes, log, ct); log($"تم مسح {n} ملف."); return 0; }, ct);
        }
        if (_rAll.Checked)
        {
            allowCancel = false;
            if (VolumeTools.GetProtectionReason(p, forFormat: true) is { } why) { Msg(why, MessageBoxIcon.Stop); return null; }
            if (_txtConfirm.Text.Trim() != "WIPE") { Msg("اكتب كلمة WIPE للتأكيد.", MessageBoxIcon.Information); return null; }
            if (Msg($"سيتم مسح كل البيانات على {p.DriveLetter ?? p.DisplayName} ({Ui.Size(p.Length)}) نهائياً وعمل فورمات كامل.\nمتابعة؟",
                    MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
            Changed = true;
            var opts = new FormatOptions(p.FileSystem is "NTFS" or "exFAT" or "FAT32" ? p.FileSystem : "NTFS", p.Label, QuickFormat: false);
            return (log, ct) => VolumeTools.FormatAsync(p, opts, log, ct);
        }
        return null;
    }
}
