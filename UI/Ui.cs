using DiskRecovery.Core;

namespace DiskRecovery.UI;

/// <summary>هدف لأدوات الهارد: قرص كامل أو بارتشن أو ملف نسخة.</summary>
public sealed class TargetItem
{
    public required string Text { get; init; }
    public int DiskNumber { get; init; } = -1;
    public string? ImagePath { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public int SectorSize { get; init; } = 512;
    public bool IsWholeDisk { get; init; }
    public PartitionInfo? Partition { get; init; }

    public IBlockDevice Open() =>
        ImagePath != null ? new ImageFileDevice(ImagePath) : new PhysicalDiskDevice(DiskNumber, SectorSize);

    public override string ToString() => Text;
}

/// <summary>تنسيقات وأدوات مشتركة للواجهة.</summary>
internal static class Ui
{
    public static readonly Color Accent = Color.FromArgb(28, 78, 128);
    public static readonly Color Danger = Color.FromArgb(176, 32, 32);
    public static readonly Color Good = Color.FromArgb(34, 139, 34);
    public static readonly Color Warn = Color.FromArgb(214, 120, 0);
    public static readonly Color Back = Color.FromArgb(246, 248, 251);

    public static Font F(float size = 10f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static void SetupForm(Form f, string title, int width, int height, bool dialog = true)
    {
        f.Text = title;
        f.RightToLeft = RightToLeft.Yes;
        f.RightToLeftLayout = true;
        f.Font = F();
        f.ClientSize = new Size(width, height);
        f.MinimumSize = new Size(Math.Min(width, 600), Math.Min(height, 420));
        f.BackColor = Back;
        f.StartPosition = dialog ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        if (dialog) { f.MinimizeBox = false; f.ShowInTaskbar = false; }
    }

    public static Label Header(string text, string? sub = null) => new()
    {
        Dock = DockStyle.Top, Height = sub == null ? 50 : 66, BackColor = Accent, ForeColor = Color.White,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 16, 0),
        Font = F(12.5f, FontStyle.Bold), Text = sub == null ? text : text + "\n" + sub,
    };

    public static Button Button(string text, bool primary, int width, Color? color = null)
    {
        var b = new Button();
        StyleButton(b, text, primary, width, color);
        return b;
    }

    public static void StyleButton(Button b, string text, bool primary, int width, Color? color = null)
    {
        var c = color ?? Accent;
        b.Text = text;
        b.Width = width;
        b.Height = 34;
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderColor = c;
        b.BackColor = primary ? c : Color.White;
        b.ForeColor = primary ? Color.White : c;
        b.Font = F(10f, primary ? FontStyle.Bold : FontStyle.Regular);
        b.Margin = new Padding(5, 3, 5, 3);
    }

    public static void StyleGrid(DataGridView g)
    {
        g.Dock = DockStyle.Fill;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 237, 244);
        g.ColumnHeadersDefaultCellStyle.Font = F(9.5f, FontStyle.Bold);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 32;
        g.RowTemplate.Height = 26;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 225, 247);
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.GridColor = Color.FromArgb(230, 230, 230);
        g.RightToLeft = RightToLeft.Yes;
    }

    public static void AddColumn(DataGridView g, string name, string header, float weight, bool sortable = false)
    {
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, FillWeight = weight,
            SortMode = sortable ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable,
        });
    }

    public static DialogResult Msg(IWin32Window? owner, string text, string title, MessageBoxIcon icon,
                                   MessageBoxButtons buttons = MessageBoxButtons.OK,
                                   MessageBoxDefaultButton def = MessageBoxDefaultButton.Button1) =>
        MessageBox.Show(owner, text, title, buttons, icon, def, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    public static string Size(long bytes)
    {
        string[] units = { "بايت", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} {units[0]}" : $"{v:0.##} {units[u]}";
    }

    /// <summary>قائمة الأهداف (أقراص كاملة و/أو بارتشنات) لأدوات الهارد.</summary>
    public static List<TargetItem> Targets(IEnumerable<PartitionInfo> parts, bool disks, bool partitions)
    {
        var list = new List<TargetItem>();
        foreach (var g in parts.GroupBy(p => (p.DiskNumber, p.ImagePath)).OrderBy(g => g.Key.DiskNumber))
        {
            var first = g.First();
            if (disks)
            {
                list.Add(first.ImagePath != null
                    ? new TargetItem
                    {
                        Text = $"📄 {first.DiskName}  ({Size(first.DiskSize)})", ImagePath = first.ImagePath,
                        Offset = 0, Length = first.DiskSize, IsWholeDisk = true,
                    }
                    : new TargetItem
                    {
                        Text = $"💽 Disk {first.DiskNumber}: {first.DiskName}  ({Size(first.DiskSize)}) — القرص كامل",
                        DiskNumber = first.DiskNumber, Offset = 0, Length = first.DiskSize, SectorSize = first.SectorSize, IsWholeDisk = true,
                    });
            }
            if (partitions)
                foreach (var p in g)
                    list.Add(new TargetItem
                    {
                        Text = $"      {(p.DriveLetter ?? "—")}  {p.DisplayName}  [{p.FileSystem}]  {Size(p.Length)}" + (p.IsLost ? "  (مفقود)" : ""),
                        DiskNumber = p.DiskNumber, ImagePath = p.ImagePath, Offset = p.Offset, Length = p.Length,
                        SectorSize = p.SectorSize, Partition = p,
                    });
        }
        return list;
    }

    public static TextBox LogBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(30, 34, 40), ForeColor = Color.FromArgb(220, 230, 240), Font = F(9.5f),
    };
}
