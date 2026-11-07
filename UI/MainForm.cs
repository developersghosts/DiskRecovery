using System.Diagnostics;
using DiskRecovery.Core;
using DiskRecovery.Core.Carving;
using DiskRecovery.Core.Maintenance;
using DiskRecovery.Core.Preview;
using DiskRecovery.Core.Recovery;

namespace DiskRecovery.UI;

public sealed class MainForm : Form
{
    private static readonly Color HiddenRow = Color.FromArgb(255, 248, 225);
    private static readonly Color LostRow = Color.FromArgb(232, 244, 255);
    private static readonly Color PartialRow = Color.FromArgb(255, 236, 214);
    private static readonly Color PoorRow = Color.FromArgb(240, 240, 240);

    // البارتشنات
    private readonly DataGridView _gridParts = new();
    private readonly Button _btnQuick = Ui.Button("فحص سريع", true, 130);
    private readonly Button _btnDeep = Ui.Button("بحث عميق", true, 130, Color.FromArgb(90, 60, 150));
    private readonly Button _btnRefresh = Ui.Button("تحديث", false, 90);
    private readonly Button _btnRepair = Ui.Button("إصلاح", false, 90);
    private readonly Button _btnFormat = Ui.Button("فورمات", false, 90, Ui.Danger);

    // الملفات
    private readonly SplitContainer _split = new();
    private readonly SplitContainer _splitPreview = new();
    private readonly TreeView _tree = new();
    private readonly DataGridView _gridFiles = new();
    private readonly TextBox _txtFilter = new();
    private readonly ComboBox _cboType = new();
    private readonly CheckBox _chkHideBad = new();
    private readonly DateTimePicker _dtFrom = new();
    private readonly DateTimePicker _dtTo = new();
    private readonly NumericUpDown _numMinSize = new();
    private readonly CheckBox _chkPreview = new();
    private readonly Label _lblCounts = new();

    // المعاينة
    private readonly PictureBox _picPreview = new();
    private readonly TextBox _txtPreview = new();
    private readonly Label _lblPreviewInfo = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 300 };
    private int _previewVersion;

    // الاسترجاع
    private readonly TextBox _txtDest = new();
    private readonly Button _btnBrowse = Ui.Button("اختيار...", false, 95);
    private readonly CheckBox _chkKeepPaths = new() { Text = "الحفاظ على المسارات", Checked = true, AutoSize = true };
    private readonly CheckBox _chkVerify = new() { Text = "التأكد من سلامة الملفات", Checked = true, AutoSize = true };
    private readonly Button _btnRecover = Ui.Button("استرجاع المحدد", true, 170);

    // الحالة
    private readonly ToolStripProgressBar _progress = new() { Size = new Size(240, 18) };
    private readonly ToolStripStatusLabel _lblStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "جاهز" };
    private readonly ToolStripButton _btnCancel = new() { Text = "إيقاف", Enabled = false };
    private readonly MenuStrip _menu = new();

    private List<PartitionInfo> _partitions = new();
    private readonly List<PartitionInfo> _extraPartitions = new(); // ملفات نسخ وبارتشنات مفقودة
    private PartitionInfo? _scannedPartition;
    private IBlockDevice? _device;
    private ScanResult? _scan;
    private List<DeletedItem> _all = new();
    private List<DeletedItem> _view = new();
    private Func<DeletedItem, bool>? _treeFilter;
    private CancellationTokenSource? _cts;
    private int _sortColumn = -1;
    private bool _sortAsc = true;

    public MainForm()
    {
        Ui.SetupForm(this, "استرجاع الملفات المحذوفة وصيانة الهارد", 1340, 860, dialog: false);
        MinimumSize = new Size(1050, 700);
        BuildMenu();
        BuildLayout();
        Shown += async (_, _) => await LoadPartitionsAsync();
        FormClosing += (_, _) => { _cts?.Cancel(); _device?.Dispose(); };
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await UpdatePreviewAsync(); };
    }

    public static string FormatSize(long bytes) => Ui.Size(bytes);

    // ═════════════════════════════ القوائم ═════════════════════════════

    private void BuildMenu()
    {
        _menu.RightToLeft = RightToLeft.Yes;
        _menu.Font = Ui.F(10f);
        var file = new ToolStripMenuItem("ملف");
        file.DropDownItems.Add("فتح ملف نسخة قرص (img)...", null, (_, _) => OpenImageFile());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("حفظ نتيجة الفحص...", null, (_, _) => SaveScan());
        file.DropDownItems.Add("فتح نتيجة فحص محفوظة...", null, async (_, _) => await LoadScanAsync());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("خروج", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("أدوات الهارد");
        tools.DropDownItems.Add("صحة الهارد (S.M.A.R.T.)", null, (_, _) => ShowTool(new HealthForm(_partitions)));
        tools.DropDownItems.Add("فحص سطح القرص (القطاعات التالفة)", null, (_, _) => ShowTool(new SurfaceForm(AllPartitions())));
        tools.DropDownItems.Add("اختبار السرعة", null, (_, _) => ShowTool(new SpeedForm(AllPartitions())));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("عمل نسخة كاملة من القرص (Image)...", null, (_, _) => ShowImaging());
        tools.DropDownItems.Add("البحث عن بارتشنات مفقودة...", null, (_, _) => ShowLostPartitions());
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("إدارة البارتشنات...", null, async (_, _) => await ShowPartitionManagerAsync());
        tools.DropDownItems.Add("المسح الآمن...", null, async (_, _) => await ShowWipeAsync());

        var help = new ToolStripMenuItem("مساعدة");
        help.DropDownItems.Add("نصائح لزيادة فرص الاسترجاع", null, (_, _) => Ui.Msg(this,
            "• توقف عن استخدام الهارد أو الفلاشة فوراً بعد الحذف.\n" +
            "• لا تحفظ الملفات المسترجعة على نفس البارتشن.\n" +
            "• ابدأ بالفحص السريع، ولو الملفات مش موجودة استخدم البحث العميق.\n" +
            "• بعد الفورمات السريع: البحث العميق يرجع الأسماء والمجلدات من جدول MFT القديم.\n" +
            "• لو الهارد بيصدر صوت أو بطيء جداً: اعمل نسخة كاملة (Image) أولاً واسترجع من النسخة.\n" +
            "• راجع «صحة الهارد» قبل أي إصلاح — الإصلاح على هارد بيموت ممكن يقضي عليه.",
            "نصائح", MessageBoxIcon.Information));

        _menu.Items.AddRange(new ToolStripItem[] { file, tools, help });
        MainMenuStrip = _menu;
    }

    private void ShowTool(Form f)
    {
        using (f) f.ShowDialog(this);
    }

    // ═════════════════════════════ التصميم ═════════════════════════════

    private void BuildLayout()
    {
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        main.Controls.Add(BuildPartitionsGroup(), 0, 0);
        main.Controls.Add(BuildFilesGroup(), 0, 1);
        main.Controls.Add(BuildRecoverGroup(), 0, 2);

        var status = new StatusStrip { RightToLeft = RightToLeft.Yes, SizingGrip = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        status.Items.AddRange(new ToolStripItem[] { _lblStatus, _progress, _btnCancel });

        Controls.Add(main);
        Controls.Add(_menu);
        Controls.Add(status);
        main.BringToFront();
    }

    private GroupBox BuildPartitionsGroup()
    {
        var g = new GroupBox { Text = "  1) البارتشنات  ", Dock = DockStyle.Fill, Padding = new Padding(6) };
        Ui.StyleGrid(_gridParts);
        _gridParts.MultiSelect = false;
        _gridParts.ReadOnly = true;
        Ui.AddColumn(_gridParts, "disk", "القرص", 22);
        Ui.AddColumn(_gridParts, "num", "رقم", 5);
        Ui.AddColumn(_gridParts, "letter", "الحرف", 6);
        Ui.AddColumn(_gridParts, "name", "الاسم", 17);
        Ui.AddColumn(_gridParts, "fs", "النظام", 9);
        Ui.AddColumn(_gridParts, "size", "الحجم", 9);
        Ui.AddColumn(_gridParts, "type", "النوع", 15);
        Ui.AddColumn(_gridParts, "state", "الحالة", 9);
        _gridParts.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ScanAsync(deep: false); };
        _gridParts.SelectionChanged += (_, _) => UpdatePartitionButtons();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 4, 0, 0), WrapContents = false };
        _btnQuick.Click += async (_, _) => await ScanAsync(deep: false);
        _btnDeep.Click += async (_, _) => await ScanAsync(deep: true);
        _btnRefresh.Click += async (_, _) => await LoadPartitionsAsync();
        _btnRepair.Click += async (_, _) => await RunMaintenanceAsync(format: false);
        _btnFormat.Click += async (_, _) => await RunMaintenanceAsync(format: true);
        bar.Controls.AddRange(new Control[] { _btnQuick, _btnDeep, _btnRefresh, _btnRepair, _btnFormat });
        bar.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(14, 10, 0, 0),
            Text = "أصفر = مخفي   •   أزرق = مفقود أو ملف نسخة   •   الفحص السريع للمحذوف حديثاً، والبحث العميق بعد الفورمات أو لو الملفات مش ظاهرة",
        });

        g.Controls.Add(_gridParts);
        g.Controls.Add(bar);
        _gridParts.BringToFront();
        return g;
    }

    private GroupBox BuildFilesGroup()
    {
        var g = new GroupBox { Text = "  2) الملفات المحذوفة  ", Dock = DockStyle.Fill, Padding = new Padding(6) };

        // ── شريط الفلاتر ──
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, AutoScroll = false };
        Control L(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(3, 10, 2, 0) };
        _txtFilter.Width = 170; _txtFilter.Margin = new Padding(2, 6, 8, 0);
        _txtFilter.PlaceholderText = "اسم أو امتداد...";
        _cboType.DropDownStyle = ComboBoxStyle.DropDownList; _cboType.Width = 105; _cboType.Margin = new Padding(2, 6, 8, 0);
        _cboType.Items.AddRange(new object[] { "الكل", "ملفات فقط", "مجلدات فقط" }); _cboType.SelectedIndex = 0;
        foreach (var dt in new[] { _dtFrom, _dtTo })
        {
            dt.Format = DateTimePickerFormat.Short; dt.ShowCheckBox = true; dt.Checked = false; dt.Width = 130;
            dt.Margin = new Padding(2, 6, 8, 0); dt.RightToLeft = RightToLeft.No;
        }
        _dtFrom.Value = DateTime.Today.AddDays(-30);
        _numMinSize.DecimalPlaces = 1; _numMinSize.Maximum = 100000; _numMinSize.Width = 65; _numMinSize.Margin = new Padding(2, 7, 2, 0);
        _chkHideBad.Text = "إخفاء التالف"; _chkHideBad.AutoSize = true; _chkHideBad.Margin = new Padding(8, 9, 8, 0);
        _chkPreview.Text = "معاينة"; _chkPreview.AutoSize = true; _chkPreview.Checked = true; _chkPreview.Margin = new Padding(8, 9, 8, 0);
        _lblCounts.AutoSize = true; _lblCounts.ForeColor = Ui.Accent; _lblCounts.Margin = new Padding(10, 10, 0, 0);
        var btnAll = Ui.Button("تحديد الكل", false, 95); btnAll.Height = 30;
        var btnNone = Ui.Button("إلغاء", false, 65); btnNone.Height = 30;
        btnAll.Click += (_, _) => SetChecked(_view, true);
        btnNone.Click += (_, _) => SetChecked(_all, false);

        bar.Controls.AddRange(new Control[]
        {
            L("بحث:"), _txtFilter, _cboType, L("من:"), _dtFrom, L("إلى:"), _dtTo, L("أكبر من (MB):"), _numMinSize,
            _chkHideBad, _chkPreview, btnAll, btnNone, _lblCounts,
        });
        _txtFilter.TextChanged += (_, _) => ApplyFilter();
        _cboType.SelectedIndexChanged += (_, _) => ApplyFilter();
        _chkHideBad.CheckedChanged += (_, _) => ApplyFilter();
        _dtFrom.ValueChanged += (_, _) => ApplyFilter();
        _dtTo.ValueChanged += (_, _) => ApplyFilter();
        _numMinSize.ValueChanged += (_, _) => ApplyFilter();
        _chkPreview.CheckedChanged += (_, _) => { _splitPreview.Panel2Collapsed = !_chkPreview.Checked; SchedulePreview(); };

        // ── الشجرة ──
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.RightToLeft = RightToLeft.Yes;
        _tree.RightToLeftLayout = true;
        _tree.Font = Ui.F(9.5f);
        _tree.AfterSelect += (_, e) => { _treeFilter = e.Node?.Tag as Func<DeletedItem, bool>; ApplyFilter(); };

        // ── جدول الملفات ──
        Ui.StyleGrid(_gridFiles);
        _gridFiles.VirtualMode = true;
        _gridFiles.ReadOnly = false;
        _gridFiles.MultiSelect = true;
        _gridFiles.Columns.Add(new DataGridViewCheckBoxColumn { Name = "chk", HeaderText = "✓", FillWeight = 4, SortMode = DataGridViewColumnSortMode.NotSortable });
        Ui.AddColumn(_gridFiles, "name", "الاسم", 22, true);
        Ui.AddColumn(_gridFiles, "path", "المسار الأصلي", 28, true);
        Ui.AddColumn(_gridFiles, "type", "النوع", 7, true);
        Ui.AddColumn(_gridFiles, "size", "الحجم", 9, true);
        Ui.AddColumn(_gridFiles, "date", "التعديل", 12, true);
        Ui.AddColumn(_gridFiles, "status", "حالة الاسترجاع", 15, true);
        Ui.AddColumn(_gridFiles, "src", "المصدر", 7, true);
        foreach (DataGridViewColumn c in _gridFiles.Columns) c.ReadOnly = c.Name != "chk";
        _gridFiles.CellValueNeeded += GridFiles_CellValueNeeded;
        _gridFiles.CellValuePushed += GridFiles_CellValuePushed;
        _gridFiles.CellFormatting += GridFiles_CellFormatting;
        _gridFiles.ColumnHeaderMouseClick += GridFiles_HeaderClick;
        _gridFiles.SelectionChanged += (_, _) => SchedulePreview();
        _gridFiles.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_gridFiles.IsCurrentCellDirty) _gridFiles.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _gridFiles.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space) return;
            var rows = SelectedViewItems();
            if (rows.Count == 0) return;
            SetChecked(rows, !rows[0].Checked);
            e.Handled = true;
        };
        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add("تحديد الصفوف المختارة", null, (_, _) => SetChecked(SelectedViewItems(), true));
        menu.Items.Add("إلغاء تحديد الصفوف المختارة", null, (_, _) => SetChecked(SelectedViewItems(), false));
        menu.Items.Add("تحديد كل المعروض بحالة ممتازة", null, (_, _) => SetChecked(_view.Where(i => i.Chance == RecoveryChance.Good), true));
        _gridFiles.ContextMenuStrip = menu;

        // ── المعاينة ──
        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6) };
        _lblPreviewInfo.Dock = DockStyle.Top; _lblPreviewInfo.Height = 52; _lblPreviewInfo.ForeColor = Ui.Accent;
        _lblPreviewInfo.Text = "اختر ملفاً لمعاينته";
        _picPreview.Dock = DockStyle.Fill; _picPreview.SizeMode = PictureBoxSizeMode.Zoom; _picPreview.BackColor = Color.FromArgb(245, 245, 245);
        _txtPreview.Dock = DockStyle.Fill; _txtPreview.Multiline = true; _txtPreview.ReadOnly = true;
        _txtPreview.ScrollBars = ScrollBars.Both; _txtPreview.WordWrap = false; _txtPreview.Visible = false; _txtPreview.BackColor = Color.White;
        previewPanel.Controls.Add(_picPreview);
        previewPanel.Controls.Add(_txtPreview);
        previewPanel.Controls.Add(_lblPreviewInfo);

        _splitPreview.Dock = DockStyle.Fill;
        _splitPreview.Panel1.Controls.Add(_gridFiles);
        _splitPreview.Panel2.Controls.Add(previewPanel);
        _split.Dock = DockStyle.Fill;
        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_splitPreview);
        HandleCreated += (_, _) =>
        {
            _split.SplitterDistance = 240;
            _splitPreview.SplitterDistance = Math.Max(300, _splitPreview.Width - 330);
        };

        g.Controls.Add(_split);
        g.Controls.Add(bar);
        _split.BringToFront();
        return g;
    }

    private GroupBox BuildRecoverGroup()
    {
        var g = new GroupBox { Text = "  3) الاسترجاع  ", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "حفظ في:", AutoSize = true, Anchor = AnchorStyles.Right }, 0, 0);
        _txtDest.Dock = DockStyle.Fill; _txtDest.Margin = new Padding(3, 9, 3, 0);
        t.Controls.Add(_txtDest, 1, 0);
        _btnBrowse.Click += (_, _) => BrowseDestination();
        t.Controls.Add(_btnBrowse, 2, 0);
        _chkKeepPaths.Anchor = AnchorStyles.None; _chkVerify.Anchor = AnchorStyles.None;
        t.Controls.Add(_chkKeepPaths, 3, 0);
        t.Controls.Add(_chkVerify, 4, 0);
        _btnRecover.Click += async (_, _) => await RecoverAsync();
        t.Controls.Add(_btnRecover, 5, 0);
        g.Controls.Add(t);
        return g;
    }

    // ═════════════════════════════ البارتشنات ═════════════════════════════

    private List<PartitionInfo> AllPartitions() => _partitions.Concat(_extraPartitions).ToList();

    private async Task LoadPartitionsAsync()
    {
        if (_cts != null) return;
        SetBusy(true, "جاري قراءة الأقراص...");
        try
        {
            _partitions = await Task.Run(() => DiskEnumerator.GetAllPartitions());
            FillPartitionsGrid();
            int hidden = _partitions.Count(p => p.IsHidden);
            SetStatus($"تم العثور على {_partitions.Count} بارتشن ({hidden} مخفي)");
            if (_partitions.Count == 0)
                Ui.Msg(this, "لم يتم العثور على أي بارتشن. تأكد من تشغيل البرنامج كمسؤول (Run as administrator).", Text, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { Ui.Msg(this, "خطأ أثناء قراءة الأقراص:\n" + ex.Message, Text, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private void FillPartitionsGrid()
    {
        _gridParts.Rows.Clear();
        foreach (var p in AllPartitions())
        {
            string disk = p.IsImage ? p.DiskName : $"Disk {p.DiskNumber}  ({p.DiskName}, {p.Style})";
            int i = _gridParts.Rows.Add(disk, p.PartitionNumber, p.DriveLetter ?? "—", p.DisplayName, p.FileSystem,
                                        Ui.Size(p.Length), p.TypeName, p.StateText);
            var row = _gridParts.Rows[i];
            row.Tag = p;
            if (p.IsLost || p.IsImage) row.DefaultCellStyle.BackColor = LostRow;
            else if (p.IsHidden) row.DefaultCellStyle.BackColor = HiddenRow;
        }
        UpdatePartitionButtons();
    }

    private PartitionInfo? SelectedPartition =>
        _gridParts.SelectedRows.Count > 0 ? _gridParts.SelectedRows[0].Tag as PartitionInfo : null;

    private void UpdatePartitionButtons()
    {
        var p = SelectedPartition;
        bool idle = _cts == null;
        _btnRepair.Enabled = _btnFormat.Enabled = idle && p is { CanModify: true };
    }

    private void OpenImageFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "اختر ملف نسخة قرص أو بارتشن",
            Filter = "ملفات النسخ|*.img;*.dd;*.raw;*.bin;*.iso;*.001|كل الملفات|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _extraPartitions.RemoveAll(x => x.ImagePath == dlg.FileName);
            var parts = PartitionTable.OpenImage(dlg.FileName);
            _extraPartitions.AddRange(parts);
            FillPartitionsGrid();
            SelectPartition(parts[0]);
            SetStatus($"تم فتح ملف النسخة: {parts.Count} بارتشن");
        }
        catch (Exception ex) { Ui.Msg(this, "تعذر فتح الملف:\n" + ex.Message, Text, MessageBoxIcon.Error); }
    }

    private void SelectPartition(PartitionInfo p)
    {
        foreach (DataGridViewRow r in _gridParts.Rows)
            if (r.Tag == p) { r.Selected = true; _gridParts.FirstDisplayedScrollingRowIndex = r.Index; break; }
    }

    // ═════════════════════════════ الفحص ═════════════════════════════

    private async Task ScanAsync(bool deep)
    {
        if (_cts != null) return;
        var p = SelectedPartition;
        if (p == null) { Ui.Msg(this, "اختر بارتشن من القائمة أولاً.", Text, MessageBoxIcon.Information); return; }

        if (p.FileSystem == "BitLocker")
        {
            Ui.Msg(this, "هذا البارتشن مشفّر بـ BitLocker والبيانات الخام عليه مشفرة.\nافتح القفل من ويندوز أولاً، أو استخدم الملفات من داخل ويندوز.", Text, MessageBoxIcon.Information);
            return;
        }
        if (!deep && !QuickScan.IsSupported(p.FileSystem))
        {
            if (Ui.Msg(this, $"نظام الملفات «{p.FileSystem}» غير مدعوم في الفحص السريع.\nهل تريد عمل بحث عميق (بالتوقيع) بدلاً منه؟",
                       Text, MessageBoxIcon.Question, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            deep = true;
        }

        DeepScanOptions? deepOpts = null;
        if (deep)
        {
            using var dlg = new DeepScanForm(p);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            deepOpts = dlg.Options;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true, deep ? "جاري البحث العميق..." : "جاري الفحص...");
        _all = new(); _view = new(); _scan = null; RefreshGrid(); _tree.Nodes.Clear();

        var progress = new Progress<ScanProgress>(sp =>
        {
            _progress.Value = (int)Math.Clamp(sp.Total > 0 ? sp.Done * 100 / sp.Total : 0, 0, 100);
            SetStatus(sp.Total > 0 && deep
                ? $"{sp.Stage}  —  {Ui.Size(sp.Done)} / {Ui.Size(sp.Total)}  —  تم العثور على {sp.Found:N0}"
                : $"{sp.Stage}  —  تم العثور على {sp.Found:N0}");
        });

        try
        {
            _device?.Dispose();
            _device = p.OpenDevice();
            var reader = new PartitionReader(_device, p.Offset, p.Length);
            var token = _cts.Token;
            _scan = await Task.Run(() =>
            {
                ScanResult? quick = null;
                if (QuickScan.IsSupported(QuickScan.DetectFileSystem(reader)))
                {
                    try { quick = QuickScan.Scan(reader, deep ? null : progress, token); }
                    catch (OperationCanceledException) { throw; }
                    catch when (deep) { quick = null; }
                }
                if (!deep) return quick ?? throw new InvalidDataException("تعذر قراءة نظام الملفات.");
                return DeepScanner.Scan(reader, quick, deepOpts!, progress, token);
            }, token);

            _scannedPartition = p;
            _all = _scan.Items;
            _sortColumn = -1;
            BuildTree();
            ApplyFilter();
            int good = _all.Count(i => !i.IsDirectory && i.Chance == RecoveryChance.Good);
            SetStatus($"انتهى الفحص: {_all.Count:N0} عنصر، منها {good:N0} ملف حالته ممتازة");
            if (_all.Count == 0 && !deep)
                Ui.Msg(this, "لم يتم العثور على ملفات محذوفة بالفحص السريع.\nجرّب «البحث العميق» — بيلاقي الملفات حتى بعد الفورمات.", Text, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { SetStatus("تم الإيقاف"); }
        catch (Exception ex) { Ui.Msg(this, "فشل الفحص:\n" + ex.Message, Text, MessageBoxIcon.Error); SetStatus("فشل الفحص"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            SetBusy(false);
        }
    }

    // ═════════════════════════════ الشجرة والفلترة ═════════════════════════════

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var all = _tree.Nodes.Add($"كل النتائج ({_all.Count:N0})");
        all.Tag = null;

        var byType = _tree.Nodes.Add("حسب النوع");
        foreach (var grp in _all.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            var cat = grp.Key;
            var n = byType.Nodes.Add($"{FileCategories.ArabicName(cat)} ({grp.Count():N0})");
            n.Tag = (Func<DeletedItem, bool>)(i => i.Category == cat);
        }

        var byFolder = _tree.Nodes.Add("حسب المجلد");
        var map = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _all)
        {
            string path = "";
            foreach (var part in i.FolderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "\\" + part;
                counts[path] = counts.GetValueOrDefault(path) + 1;
            }
        }
        foreach (var path in counts.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase))
        {
            int cut = path.LastIndexOf('\\');
            string parent = cut > 0 ? path[..cut] : "";
            var parentNode = parent.Length > 0 && map.TryGetValue(parent, out var pn) ? pn : byFolder;
            string folder = path;
            var node = parentNode.Nodes.Add($"{path[(cut + 1)..]} ({counts[path]:N0})");
            node.Tag = (Func<DeletedItem, bool>)(i => i.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)
                                                    || i.FolderPath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));
            map[path] = node;
            if (map.Count > 50000) break;
        }
        byType.Expand();
        byFolder.Expand();
        _tree.EndUpdate();
        _treeFilter = null;
    }

    private void ApplyFilter()
    {
        string q = _txtFilter.Text.Trim();
        int type = _cboType.SelectedIndex;
        IEnumerable<DeletedItem> v = _all;
        if (_treeFilter != null) v = v.Where(_treeFilter);
        if (q.Length > 0) v = v.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (type == 1) v = v.Where(i => !i.IsDirectory);
        if (type == 2) v = v.Where(i => i.IsDirectory);
        if (_chkHideBad.Checked) v = v.Where(i => i.Chance is RecoveryChance.Good or RecoveryChance.Partial);
        if (_dtFrom.Checked) { var from = _dtFrom.Value.Date; v = v.Where(i => i.Modified is { } m && m >= from); }
        if (_dtTo.Checked) { var to = _dtTo.Value.Date.AddDays(1); v = v.Where(i => i.Modified is { } m && m < to); }
        if (_numMinSize.Value > 0) { long min = (long)(_numMinSize.Value * 1024 * 1024); v = v.Where(i => i.IsDirectory || i.Size >= min); }
        _view = v.ToList();
        if (_sortColumn >= 0) SortView();
        RefreshGrid();
    }

    private void RefreshGrid()
    {
        _gridFiles.RowCount = 0;
        _gridFiles.RowCount = _view.Count;
        _gridFiles.Invalidate();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var sel = _all.Where(i => i.Checked).ToList();
        _lblCounts.Text = $"المعروض: {_view.Count:N0}  |  المحدد: {sel.Count:N0} ({Ui.Size(sel.Sum(i => i.Size))})";
    }

    private void SetChecked(IEnumerable<DeletedItem> items, bool value)
    {
        foreach (var i in items) i.Checked = value;
        _gridFiles.Invalidate();
        UpdateCounts();
    }

    private List<DeletedItem> SelectedViewItems() =>
        _gridFiles.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.Index >= 0 && r.Index < _view.Count)
            .Select(r => _view[r.Index]).ToList();

    private void GridFiles_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
        var it = _view[e.RowIndex];
        e.Value = _gridFiles.Columns[e.ColumnIndex].Name switch
        {
            "chk" => it.Checked,
            "name" => (it.IsDirectory ? "📁 " : "") + it.Name,
            "path" => it.FolderPath,
            "type" => it.IsDirectory ? "مجلد" : (it.Extension.Length == 0 ? "ملف" : it.Extension.ToUpperInvariant()),
            "size" => it.IsDirectory ? "" : Ui.Size(it.Size),
            "date" => it.Modified?.ToString("yyyy/MM/dd HH:mm") ?? "",
            "status" => it.Status,
            "src" => it.Source,
            _ => null,
        };
    }

    private void GridFiles_CellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count || _gridFiles.Columns[e.ColumnIndex].Name != "chk") return;
        _view[e.RowIndex].Checked = e.Value is true;
        UpdateCounts();
    }

    private void GridFiles_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count || e.CellStyle == null) return;
        var it = _view[e.RowIndex];
        e.CellStyle.BackColor = it.Chance switch
        {
            RecoveryChance.Partial => PartialRow,
            RecoveryChance.Poor or RecoveryChance.Unsupported => PoorRow,
            _ => Color.White,
        };
        if (it.Chance is RecoveryChance.Poor or RecoveryChance.Unsupported) e.CellStyle.ForeColor = Color.Gray;
        if (_gridFiles.Columns[e.ColumnIndex].Name == "status" && it.Chance == RecoveryChance.Good && !it.IsDirectory)
            e.CellStyle.ForeColor = Ui.Good;
    }

    private void GridFiles_HeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        var col = _gridFiles.Columns[e.ColumnIndex];
        if (col.SortMode == DataGridViewColumnSortMode.NotSortable) return;
        _sortAsc = _sortColumn != e.ColumnIndex || !_sortAsc;
        _sortColumn = e.ColumnIndex;
        foreach (DataGridViewColumn c in _gridFiles.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        col.HeaderCell.SortGlyphDirection = _sortAsc ? SortOrder.Ascending : SortOrder.Descending;
        SortView();
        RefreshGrid();
    }

    private void SortView()
    {
        string name = _gridFiles.Columns[_sortColumn].Name;
        Comparison<DeletedItem> cmp = name switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            "path" => (a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.CurrentCultureIgnoreCase),
            "type" => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.Ordinal),
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "date" => (a, b) => Nullable.Compare(a.Modified, b.Modified),
            "status" => (a, b) => a.Chance.CompareTo(b.Chance),
            "src" => (a, b) => string.Compare(a.Source, b.Source, StringComparison.Ordinal),
            _ => (_, _) => 0,
        };
        _view.Sort(_sortAsc ? cmp : (a, b) => cmp(b, a));
    }

    // ═════════════════════════════ المعاينة ═════════════════════════════

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        if (_chkPreview.Checked) _previewTimer.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        var scan = _scan;
        var item = _gridFiles.CurrentRow is { Index: >= 0 } r && r.Index < _view.Count ? _view[r.Index] : null;
        if (scan == null || item == null || _cts != null) return;
        int version = ++_previewVersion;
        _lblPreviewInfo.Text = "جاري التحميل...";
        PreviewData data;
        try { data = await Task.Run(() => FilePreview.Build(scan, item)); }
        catch (Exception ex) { data = new PreviewData { Kind = PreviewKind.None, Info = "تعذرت المعاينة: " + ex.Message }; }
        if (version != _previewVersion || IsDisposed) return;

        var old = _picPreview.Image;
        _picPreview.Image = null;
        old?.Dispose();
        _lblPreviewInfo.Text = $"{item.Name}\n{data.Info}";

        if (data.Kind == PreviewKind.Image && data.ImageBytes != null)
        {
            try
            {
                using var ms = new MemoryStream(data.ImageBytes);
                using var img = Image.FromStream(ms);
                _picPreview.Image = new Bitmap(img);
                _lblPreviewInfo.Text += $"   ({img.Width}×{img.Height})";
                _picPreview.Visible = true; _txtPreview.Visible = false;
            }
            catch
            {
                _lblPreviewInfo.Text += "\n⚠ الصورة لا تُفتح — غالباً تالفة";
                _txtPreview.Text = FilePreview.Hex(data.ImageBytes, 4096);
                _picPreview.Visible = false; _txtPreview.Visible = true;
            }
        }
        else
        {
            _txtPreview.Font = data.Kind == PreviewKind.Hex ? new Font("Consolas", 9f) : Ui.F(9.5f);
            _txtPreview.RightToLeft = data.Kind == PreviewKind.Hex ? RightToLeft.No : RightToLeft.Yes;
            _txtPreview.Text = data.Text;
            _picPreview.Visible = false; _txtPreview.Visible = true;
        }
    }

    // ═════════════════════════════ الاسترجاع ═════════════════════════════

    private void BrowseDestination()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "اختر المجلد الذي ستُحفظ فيه الملفات المسترجعة", UseDescriptionForTitle = true, ShowNewFolderButton = true,
        };
        if (Directory.Exists(_txtDest.Text)) dlg.InitialDirectory = _txtDest.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtDest.Text = dlg.SelectedPath;
    }

    private async Task RecoverAsync()
    {
        if (_cts != null) return;
        if (_scan == null || _scannedPartition == null) { Ui.Msg(this, "قم بفحص بارتشن أولاً.", Text, MessageBoxIcon.Information); return; }
        var selected = _all.Where(i => i.Checked).ToList();
        if (selected.Count == 0) { Ui.Msg(this, "حدد الملفات أو المجلدات المراد استرجاعها (بعلامة ✓).", Text, MessageBoxIcon.Information); return; }

        string dest = _txtDest.Text.Trim();
        if (dest.Length == 0) { BrowseDestination(); dest = _txtDest.Text.Trim(); }
        if (dest.Length == 0) return;
        if (!Path.IsPathRooted(dest)) { Ui.Msg(this, "اكتب مساراً كاملاً للمجلد، مثل D:\\Recovered", Text, MessageBoxIcon.Warning); return; }

        string destRoot = Path.GetPathRoot(Path.GetFullPath(dest))?.TrimEnd('\\') ?? "";
        if (_scannedPartition.DriveLetter != null && destRoot.Equals(_scannedPartition.DriveLetter, StringComparison.OrdinalIgnoreCase))
        {
            if (Ui.Msg(this, $"أنت تحفظ على نفس البارتشن ({destRoot}) الذي تسترجع منه!\n\n" +
                             "هذا قد يكتب فوق الملفات المحذوفة ويتلفها نهائياً.\nيُنصح باختيار بارتشن آخر أو فلاشة/هارد خارجي.\n\nهل تريد المتابعة رغم ذلك؟",
                       "تحذير", MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        try
        {
            long need = selected.Sum(i => i.Size);
            long free = new DriveInfo(destRoot + "\\").AvailableFreeSpace;
            if (need > free && Ui.Msg(this, $"المساحة الفارغة في {destRoot} ({Ui.Size(free)}) أقل من حجم الملفات المحددة ({Ui.Size(need)}).\nمتابعة؟",
                                     Text, MessageBoxIcon.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        catch { /* مسار شبكة */ }

        _cts = new CancellationTokenSource();
        SetBusy(true, "جاري الاسترجاع...");
        bool keep = _chkKeepPaths.Checked, verify = _chkVerify.Checked;
        var scan = _scan;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<RecoverProgress>(rp =>
        {
            _progress.Value = rp.FilesTotal == 0 ? 0 : Math.Clamp(rp.FilesDone * 100 / rp.FilesTotal, 0, 100);
            double mbs = rp.BytesDone / Math.Max(0.1, sw.Elapsed.TotalSeconds) / (1 << 20);
            SetStatus($"استرجاع {rp.FilesDone:N0} / {rp.FilesTotal:N0}  ({mbs:0.#} MB/s):  {rp.Current}");
        });

        try
        {
            var token = _cts.Token;
            var report = await Task.Run(() => new Recoverer(scan) { VerifyAfterRecovery = verify }.Recover(selected, dest, keep, progress, token), token);

            string msg = $"تم استرجاع {report.Recovered:N0} ملف ({Ui.Size(report.Bytes)})";
            if (report.Folders > 0) msg += $" و {report.Folders:N0} مجلد";
            msg += ".";
            if (verify && report.Recovered > 0)
                msg += $"\n\nالتحقق: {report.Verified:N0} ملف بنيته سليمة" + (report.Suspicious > 0 ? $"، و {report.Suspicious:N0} ملف قد يكون تالفاً" : "");
            if (report.Failed > 0) msg += $"\nفشل: {report.Failed:N0}";
            if (report.Skipped > 0) msg += $"\nتم تخطي (غير مدعوم): {report.Skipped:N0}";
            var issues = report.SuspiciousFiles.Concat(report.Errors).Take(8).ToList();
            if (issues.Count > 0) msg += "\n\nملاحظات:\n" + string.Join("\n", issues);
            msg += "\n\nهل تريد فتح المجلد؟";
            SetStatus($"تم الاسترجاع: {report.Recovered:N0} ملف");

            if (Ui.Msg(this, msg, "انتهى الاسترجاع", MessageBoxIcon.Information, MessageBoxButtons.YesNo) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) { SetStatus("تم إيقاف الاسترجاع"); }
        catch (Exception ex) { Ui.Msg(this, "فشل الاسترجاع:\n" + ex.Message, Text, MessageBoxIcon.Error); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            SetBusy(false);
        }
    }

    // ═════════════════════════════ حفظ وفتح الفحص ═════════════════════════════

    private void SaveScan()
    {
        if (_scan == null || _scannedPartition == null) { Ui.Msg(this, "لا توجد نتيجة فحص لحفظها.", Text, MessageBoxIcon.Information); return; }
        using var dlg = new SaveFileDialog
        {
            Filter = "نتيجة فحص|*" + ScanStore.Extension,
            FileName = $"فحص {_scannedPartition.DriveLetter?.TrimEnd(':') ?? "Disk" + _scannedPartition.DiskNumber} {DateTime.Now:yyyy-MM-dd HH-mm}",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { ScanStore.Save(dlg.FileName, _scan, _scannedPartition); SetStatus("تم حفظ نتيجة الفحص"); }
        catch (Exception ex) { Ui.Msg(this, "تعذر الحفظ:\n" + ex.Message, Text, MessageBoxIcon.Error); }
    }

    private async Task LoadScanAsync()
    {
        if (_cts != null) return;
        using var dlg = new OpenFileDialog { Filter = "نتيجة فحص|*" + ScanStore.Extension };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true, "جاري فتح نتيجة الفحص...");
        try
        {
            var all = AllPartitions();
            var loaded = await Task.Run(() => ScanStore.Load(dlg.FileName, all));
            if (loaded.Partition.IsImage && !_extraPartitions.Any(x => x.ImagePath == loaded.Partition.ImagePath && x.Offset == loaded.Partition.Offset))
            {
                _extraPartitions.Add(loaded.Partition);
                FillPartitionsGrid();
            }
            _device?.Dispose();
            _device = loaded.Partition.OpenDevice();
            _scan = new ScanResult
            {
                Reader = new PartitionReader(_device, loaded.Partition.Offset, loaded.Partition.Length),
                FileSystem = loaded.FileSystem, Items = loaded.Items,
            };
            _scannedPartition = loaded.Partition;
            _all = _scan.Items;
            BuildTree();
            ApplyFilter();
            SetStatus($"تم فتح فحص بتاريخ {loaded.Saved:yyyy/MM/dd HH:mm} — {_all.Count:N0} عنصر");
        }
        catch (Exception ex) { Ui.Msg(this, ex.Message, Text, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    // ═════════════════════════════ الأدوات ═════════════════════════════

    private void ShowImaging()
    {
        using var f = new ImagingForm(AllPartitions(), SelectedPartition);
        f.ShowDialog(this);
        if (f.CreatedImage != null &&
            Ui.Msg(this, "تم عمل النسخة. هل تريد فتحها الآن لاسترجاع الملفات منها؟", Text, MessageBoxIcon.Question, MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            var parts = PartitionTable.OpenImage(f.CreatedImage);
            _extraPartitions.AddRange(parts);
            FillPartitionsGrid();
            SelectPartition(parts[0]);
        }
    }

    private void ShowLostPartitions()
    {
        using var f = new LostPartitionsForm(AllPartitions());
        f.ShowDialog(this);
        if (f.Added.Count == 0) return;
        _extraPartitions.AddRange(f.Added);
        FillPartitionsGrid();
        SelectPartition(f.Added[0]);
        SetStatus($"تمت إضافة {f.Added.Count} بارتشن مفقود للقائمة — اختره واعمل له فحص");
    }

    private async Task ShowPartitionManagerAsync()
    {
        var p = SelectedPartition;
        if (p == null || !p.CanModify) { Ui.Msg(this, "اختر بارتشن من الهارد (وليس ملف نسخة أو بارتشن مفقود).", Text, MessageBoxIcon.Information); return; }
        using var f = new PartitionManagerForm(p);
        f.ShowDialog(this);
        if (f.Changed) await AfterPartitionChangedAsync(p);
    }

    private async Task ShowWipeAsync()
    {
        var p = SelectedPartition;
        if (p == null || !p.CanModify) { Ui.Msg(this, "اختر بارتشن من الهارد أولاً.", Text, MessageBoxIcon.Information); return; }
        using var f = new WipeForm(p);
        f.ShowDialog(this);
        if (f.Changed) await AfterPartitionChangedAsync(p);
    }

    private async Task RunMaintenanceAsync(bool format)
    {
        if (_cts != null) return;
        var p = SelectedPartition;
        if (p == null) { Ui.Msg(this, "اختر بارتشن من القائمة أولاً.", Text, MessageBoxIcon.Information); return; }
        if (!p.CanModify) { Ui.Msg(this, "لا يمكن إصلاح أو فورمات ملف نسخة أو بارتشن مفقود.", Text, MessageBoxIcon.Information); return; }
        if (VolumeTools.GetProtectionReason(p, format) is { } reason) { Ui.Msg(this, reason, Text, MessageBoxIcon.Stop); return; }

        bool scannedThis = _scannedPartition != null && SamePartition(_scannedPartition, p);
        int recoverable = scannedThis ? _all.Count(i => !i.IsDirectory && i.Chance is RecoveryChance.Good or RecoveryChance.Partial) : 0;
        if (!scannedThis && QuickScan.IsSupported(p.FileSystem) &&
            Ui.Msg(this, "لم يتم فحص هذا البارتشن للملفات المحذوفة بعد.\n" + (format ? "الفورمات" : "الإصلاح") +
                         " قد يضيّع أي ملفات محذوفة عليه نهائياً.\n\nهل تريد المتابعة بدون فحص؟  (اختر «لا» للفحص أولاً)",
                   Text, MessageBoxIcon.Question, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        MaintenanceFormBase dlg = format ? new FormatForm(p, recoverable) : new RepairForm(p, recoverable);
        using (dlg) dlg.ShowDialog(this);
        if (dlg.Changed) await AfterPartitionChangedAsync(p);
    }

    private async Task AfterPartitionChangedAsync(PartitionInfo p)
    {
        if (_scannedPartition != null && SamePartition(_scannedPartition, p))
        {
            _scan = null; _scannedPartition = null;
            _all = new(); _view = new(); RefreshGrid(); _tree.Nodes.Clear();
        }
        await LoadPartitionsAsync();
    }

    private static bool SamePartition(PartitionInfo a, PartitionInfo b) =>
        a.DiskNumber == b.DiskNumber && a.Offset == b.Offset && a.ImagePath == b.ImagePath;

    // ═════════════════════════════ أدوات ═════════════════════════════

    private void SetBusy(bool busy, string? status = null)
    {
        _btnQuick.Enabled = _btnDeep.Enabled = _btnRefresh.Enabled = _btnRecover.Enabled = _btnBrowse.Enabled = !busy;
        _gridParts.Enabled = !busy;
        _menu.Enabled = !busy;
        _btnCancel.Enabled = busy && _cts != null;
        _progress.Style = busy && _cts == null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!busy) _progress.Value = 0;
        UseWaitCursor = busy && _cts == null;
        UpdatePartitionButtons();
        if (status != null) SetStatus(status);
    }

    private void SetStatus(string s) => _lblStatus.Text = s;
}
