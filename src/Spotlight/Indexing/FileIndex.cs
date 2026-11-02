using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Spotlight.Interop;
using Spotlight.Services;

namespace Spotlight.Indexing;

public readonly record struct FileItem(string Name, bool IsDir);

/// <summary>One folder and the names directly inside it. Storing names per folder keeps memory small.</summary>
public sealed class DirNode
{
    public DirNode(string path) => Path = path;

    public string Path { get; }
    public List<FileItem> Items { get; } = new();
}

/// <summary>
/// File index for the user's chosen folders: loads the saved index instantly, rescans at low priority,
/// then keeps itself current with FileSystemWatcher. Nothing runs while nothing changes.
/// </summary>
public sealed class FileIndex : IDisposable
{
    private const int CacheMagic = 0x32495053; // "SPI2"
    private const int MaxDepth = 40;

    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly string[] SkippedExtensions = { ".tmp", ".crdownload", ".part", ".partial", ".download" };

    private readonly object _lock = new();      // guards _dirs, _scanning, _pending
    private readonly object _workLock = new();  // serializes background scans
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<(bool Created, string Path)> _pending = new();
    private readonly Timer _saveTimer;
    private readonly Timer _rescanTimer;

    private Dictionary<string, DirNode> _dirs = NewDirMap();
    private string[] _roots = Array.Empty<string>();
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private bool _skipDotFolders;
    private string _signature = "";
    private bool _scanning;
    private volatile bool _dirty;

    public event Action? ScanCompleted;

    private static string CachePath => Path.Combine(AppPaths.DataDir, "files.bin");

    public FileIndex(Settings settings)
    {
        _saveTimer = new Timer(_ => { if (_dirty) Save(); });
        _rescanTimer = new Timer(_ => Rescan());
        Configure(settings);
    }

    private static Dictionary<string, DirNode> NewDirMap(int capacity = 0) =>
        new(capacity, StringComparer.OrdinalIgnoreCase);

    private void Configure(Settings settings)
    {
        _roots = NormalizeRoots(settings.Folders);
        _excluded = new HashSet<string>(settings.ExcludeFolderNames, StringComparer.OrdinalIgnoreCase);
        _skipDotFolders = settings.SkipDotFolders;
        _signature = string.Join("|", _roots) + "#" + string.Join("|", _excluded.Order(StringComparer.OrdinalIgnoreCase)) + "#" + _skipDotFolders;
    }

    /// <summary>Full paths, without duplicates or folders nested inside another indexed folder.</summary>
    private static string[] NormalizeRoots(IEnumerable<string> folders)
    {
        var full = new List<string>();
        foreach (var folder in folders)
        {
            try { full.Add(Path.GetFullPath(folder).TrimEnd('\\')); }
            catch { }
        }
        full = full.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return full.Where(r => !full.Any(o => !ReferenceEquals(o, r) && r.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    /// <summary>Visits every folder under the index lock. Used by search (a few ms for ~50k items).</summary>
    public void Visit(Action<DirNode> visitor)
    {
        lock (_lock)
        {
            foreach (var node in _dirs.Values) visitor(node);
        }
    }

    public void Start() => RunInBackground(() =>
    {
        LoadCache();
        StartWatchers();
        ScanAll();
    });

    public void Rebuild(Settings settings) => RunInBackground(() =>
    {
        StopWatchers();
        Configure(settings);
        StartWatchers();
        ScanAll();
    });

    private void Rescan() => RunInBackground(ScanAll);

    // Background mode lowers CPU, disk and memory priority so scanning never makes the PC feel slow.
    private void RunInBackground(Action work)
    {
        var thread = new Thread(() =>
        {
            Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
                lock (_workLock) work();
            }
            catch (Exception ex)
            {
                Log.Write("file indexing failed: " + ex);
            }
            finally
            {
                Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_MODE_BACKGROUND_END);
            }
        })
        {
            IsBackground = true,
            Name = "Spotlight files",
        };
        thread.Start();
    }

    private bool SkipDir(string name) =>
        _excluded.Contains(name) || (_skipDotFolders && name.StartsWith('.'));

    private static bool SkipFile(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true; // Office lock files
        foreach (var ext in SkippedExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void ScanAll()
    {
        lock (_lock) _scanning = true;
        var sw = Stopwatch.StartNew();
        var dirs = NewDirMap(Math.Max(1024, _dirs.Count));

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            AddRootEntry(dirs, root);
            ScanTree(dirs, root);
        }

        int items = dirs.Values.Sum(d => d.Items.Count);
        List<(bool Created, string Path)> pending;
        lock (_lock)
        {
            _dirs = dirs;
            _scanning = false;
            pending = new List<(bool, string)>(_pending);
            _pending.Clear();
        }

        // Apply changes that happened while we were scanning.
        foreach (var (created, path) in pending)
        {
            if (created) OnCreated(path);
            else OnDeleted(path);
        }

        Log.Write($"files indexed: {items} items in {dirs.Count} folders, {sw.ElapsedMilliseconds} ms");
        Save();
        ScanCompleted?.Invoke();
    }

    /// <summary>Makes the indexed folders themselves searchable ("downloads" finds Downloads).</summary>
    private static void AddRootEntry(Dictionary<string, DirNode> dirs, string root)
    {
        string? parent = Path.GetDirectoryName(root);
        string name = Path.GetFileName(root);
        if (parent == null || name.Length == 0) return;
        var node = GetOrAdd(dirs, parent);
        if (!node.Items.Exists(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            node.Items.Add(new FileItem(name, true));
        }
    }

    private static DirNode GetOrAdd(Dictionary<string, DirNode> dirs, string path)
    {
        if (!dirs.TryGetValue(path, out var node))
        {
            node = new DirNode(path);
            dirs[path] = node;
        }
        return node;
    }

    private void ScanTree(Dictionary<string, DirNode> dirs, string start)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            var node = GetOrAdd(dirs, dir);
            try
            {
                var entries = new FileSystemEnumerable<(string Name, bool IsDir)>(
                    dir, static (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory), EnumOptions);
                foreach (var (name, isDir) in entries)
                {
                    if (isDir)
                    {
                        if (SkipDir(name)) continue;
                        node.Items.Add(new FileItem(name, true));
                        if (depth < MaxDepth) stack.Push((Path.Join(dir, name), depth + 1));
                    }
                    else if (!SkipFile(name))
                    {
                        node.Items.Add(new FileItem(name, false));
                    }
                }
            }
            catch (Exception)
            {
                // Folder vanished or is unreadable: keep what we have.
            }
            node.Items.TrimExcess();
        }
    }

    // ---- live updates -------------------------------------------------------------------------

    private void StartWatchers()
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Guard(() => OnCreated(e.FullPath));
                watcher.Deleted += (_, e) => Guard(() => OnDeleted(e.FullPath));
                watcher.Renamed += (_, e) => Guard(() =>
                {
                    OnDeleted(e.OldFullPath);
                    OnCreated(e.FullPath);
                });
                watcher.Error += (_, e) =>
                {
                    // Too many changes at once (buffer overflow): rescan shortly instead.
                    Log.Write("file watcher overflow, rescanning: " + e.GetException().Message);
                    _rescanTimer.Change(5000, Timeout.Infinite);
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Write($"cannot watch {root}: {ex.Message}");
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write("file change could not be applied: " + ex.Message); }
    }

    private void OnCreated(string path)
    {
        lock (_lock)
        {
            if (_scanning)
            {
                _pending.Add((true, path));
                return;
            }
        }

        string? parent = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (parent == null || name.Length == 0) return;

        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch { return; } // already gone

        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
        bool isDir = (attributes & FileAttributes.Directory) != 0;
        if (isDir ? SkipDir(name) : SkipFile(name)) return;

        // A folder moved in arrives as a single event, so scan its contents.
        Dictionary<string, DirNode>? subtree = null;
        if (isDir)
        {
            subtree = NewDirMap();
            ScanTree(subtree, path);
        }

        lock (_lock)
        {
            // No parent node means the parent is excluded (or outside the index): ignore.
            if (!_dirs.TryGetValue(parent, out var node)) return;
            int index = node.Items.FindIndex(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) node.Items.Add(new FileItem(name, isDir));
            else node.Items[index] = new FileItem(name, isDir);
            if (subtree != null)
            {
                foreach (var (key, value) in subtree) _dirs[key] = value;
            }
        }
        MarkDirty();
    }

    private void OnDeleted(string path)
    {
        lock (_lock)
        {
            if (_scanning)
            {
                _pending.Add((false, path));
                return;
            }

            string? parent = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (parent != null && _dirs.TryGetValue(parent, out var node))
            {
                node.Items.RemoveAll(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (_dirs.Remove(path))
            {
                string prefix = path + "\\";
                foreach (var key in _dirs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _dirs.Remove(key);
                }
            }
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(15_000, Timeout.Infinite); // one-shot, coalesces bursts of changes
    }

    // ---- persistence --------------------------------------------------------------------------

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var sw = Stopwatch.StartNew();
            using var reader = new BinaryReader(new BufferedStream(File.OpenRead(CachePath), 64 * 1024), Encoding.UTF8);
            if (reader.ReadInt32() != CacheMagic || reader.ReadString() != _signature) return;

            int dirCount = reader.ReadInt32();
            var dirs = NewDirMap(dirCount);
            for (int d = 0; d < dirCount; d++)
            {
                var node = new DirNode(reader.ReadString());
                int itemCount = reader.ReadInt32();
                node.Items.Capacity = itemCount;
                for (int i = 0; i < itemCount; i++)
                {
                    node.Items.Add(new FileItem(reader.ReadString(), reader.ReadBoolean()));
                }
                dirs[node.Path] = node;
            }

            lock (_lock) _dirs = dirs;
            Log.Write($"file index loaded from disk: {dirCount} folders in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Write("file index cache could not be read: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string tmp = CachePath + ".tmp";
            lock (_lock)
            {
                using var writer = new BinaryWriter(new BufferedStream(File.Create(tmp), 64 * 1024), Encoding.UTF8);
                writer.Write(CacheMagic);
                writer.Write(_signature);
                writer.Write(_dirs.Count);
                foreach (var node in _dirs.Values)
                {
                    writer.Write(node.Path);
                    writer.Write(node.Items.Count);
                    foreach (var item in node.Items)
                    {
                        writer.Write(item.Name);
                        writer.Write(item.IsDir);
                    }
                }
                _dirty = false;
            }
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("file index could not be saved: " + ex.Message);
        }
    }

    public void Dispose()
    {
        StopWatchers();
        _saveTimer.Dispose();
        _rescanTimer.Dispose();
        if (_dirty) Save();
    }
}
