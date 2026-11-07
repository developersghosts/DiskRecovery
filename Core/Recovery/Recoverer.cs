namespace DiskRecovery.Core.Recovery;

public sealed class RecoverProgress
{
    public int FilesDone;
    public int FilesTotal;
    public long BytesDone;
    public string Current = "";
}

public sealed class RecoverReport
{
    public int Recovered;
    public int Folders;
    public int Failed;
    public int Skipped;
    public int Verified;
    public int Suspicious;
    public long Bytes;
    public List<string> Errors { get; } = new();
    public List<string> SuspiciousFiles { get; } = new();
}

/// <summary>ينسخ بيانات الملفات من القرص إلى المكان الذي اختاره المستخدم (لا يكتب أبداً على البارتشن الأصلي).</summary>
public sealed class Recoverer
{
    private readonly ScanResult _scan;
    private readonly HashSet<long> _done = new();
    private readonly byte[] _buf = new byte[1024 * 1024];

    public bool VerifyAfterRecovery { get; set; } = true;

    public Recoverer(ScanResult scan) => _scan = scan;

    public RecoverReport Recover(IReadOnlyList<DeletedItem> selected, string destination, bool keepStructure,
                                 IProgress<RecoverProgress>? progress, CancellationToken ct)
    {
        var report = new RecoverReport();
        Directory.CreateDirectory(destination);

        // لو تم اختيار مجلد ومحتوياته معاً نتجاهل المحتويات (ستُسترجع مع المجلد)
        var selectedDirs = selected.Where(s => s.IsDirectory).Select(s => s.Id).ToHashSet();
        var roots = selected.Where(s => !HasSelectedAncestor(s, selectedDirs)).ToList();

        var prog = new RecoverProgress { FilesTotal = roots.Sum(CountTree) };
        foreach (var item in roots)
        {
            ct.ThrowIfCancellationRequested();
            string baseDir = destination;
            if (keepStructure)
            {
                foreach (var part in item.FolderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                    baseDir = Path.Combine(baseDir, Sanitize(part));
                Directory.CreateDirectory(baseDir);
            }
            RecoverItem(item, baseDir, report, prog, progress, ct, 0);
        }
        return report;
    }

    private bool HasSelectedAncestor(DeletedItem item, HashSet<long> selectedDirs)
    {
        var current = item;
        for (int i = 0; i < 256 && current.ParentIsDeletedFolder; i++)
        {
            if (selectedDirs.Contains(current.ParentId)) return true;
            if (!_scan.ById.TryGetValue(current.ParentId, out var p)) break;
            current = p;
        }
        return false;
    }

    private int CountTree(DeletedItem item)
    {
        if (!item.IsDirectory) return 1;
        int n = 0;
        var stack = new Stack<(DeletedItem, int)>();
        stack.Push((item, 0));
        while (stack.Count > 0)
        {
            var (d, depth) = stack.Pop();
            if (!d.IsDirectory) { n++; continue; }
            if (depth > 200 || !_scan.ChildrenOf.TryGetValue(d.Id, out var kids)) continue;
            foreach (var k in kids) stack.Push((k, depth + 1));
        }
        return n;
    }

    private void RecoverItem(DeletedItem item, string targetDir, RecoverReport report, RecoverProgress prog,
                             IProgress<RecoverProgress>? progress, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (!_done.Add(item.Id) || depth > 200) return;

        if (item.IsDirectory)
        {
            string dir = UniquePath(Path.Combine(targetDir, Sanitize(item.Name)), isDir: true);
            Directory.CreateDirectory(dir);
            report.Folders++;
            if (_scan.ChildrenOf.TryGetValue(item.Id, out var kids))
                foreach (var k in kids)
                    RecoverItem(k, dir, report, prog, progress, ct, depth + 1);
            TrySetTimes(dir, item, isDir: true);
            return;
        }

        prog.Current = item.FullPath;
        progress?.Report(prog);

        if (item.Chance == RecoveryChance.Unsupported || item.Data == null)
        {
            report.Skipped++;
            report.Errors.Add($"{item.FullPath}: {(item.Data == null ? "لا توجد بيانات" : item.Status)}");
            prog.FilesDone++;
            return;
        }

        string path = UniquePath(Path.Combine(targetDir, Sanitize(item.Name)), isDir: false);
        try
        {
            WriteFile(item.Data, path, prog, ct);
            TrySetTimes(path, item, isDir: false);
            report.Recovered++;
            report.Bytes += item.Data.Length;

            if (VerifyAfterRecovery)
            {
                var v = FileValidator.Check(path);
                if (v.State == ValidationState.Valid) report.Verified++;
                else if (v.State == ValidationState.Damaged)
                {
                    report.Suspicious++;
                    report.SuspiciousFiles.Add($"{item.FullPath}: {v.Reason}");
                }
            }
        }
        catch (OperationCanceledException) { TryDelete(path); throw; }
        catch (Exception ex)
        {
            report.Failed++;
            report.Errors.Add($"{item.FullPath}: {ex.Message}");
            TryDelete(path);
        }
        prog.FilesDone++;
        progress?.Report(prog);
    }

    private void WriteFile(FileData data, string path, RecoverProgress prog, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        for (long off = 0; off < data.Length; off += _buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(_buf.Length, data.Length - off);
            data.Read(_scan.Reader, off, _buf, 0, n);
            fs.Write(_buf, 0, n);
            prog.BytesDone += n;
        }
    }

    private static void TrySetTimes(string path, DeletedItem item, bool isDir)
    {
        try
        {
            if (isDir)
            {
                if (item.Created is { } c) Directory.SetCreationTime(path, c);
                if (item.Modified is { } m) Directory.SetLastWriteTime(path, m);
            }
            else
            {
                if (item.Created is { } c) File.SetCreationTime(path, c);
                if (item.Modified is { } m) File.SetLastWriteTime(path, m);
            }
        }
        catch { /* غير مهم */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string UniquePath(string path, bool isDir)
    {
        bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
        if (!Exists(path) || isDir) return path; // المجلدات بنفس الاسم تُدمج
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(p)) return p;
        }
    }

    private static readonly HashSet<char> Invalid =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();

    public static string Sanitize(string name)
    {
        var chars = name.Select(c => Invalid.Contains(c) || c < 32 ? '_' : c).ToArray();
        string s = new string(chars).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }
}
