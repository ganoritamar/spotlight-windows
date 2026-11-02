using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spotlight.Services;

namespace Spotlight.Indexing;

public sealed class AppEntry
{
    public AppEntry(string name, string appId, string? shortcutPath)
    {
        Name = name;
        AppId = appId;
        ShortcutPath = shortcutPath;
    }

    public string Name { get; }

    /// <summary>The shell:AppsFolder parsing name (AUMID, exe path, URL...), or the .lnk path for shortcut-only apps.</summary>
    public string AppId { get; }

    /// <summary>Matching Start Menu shortcut, used to reveal the app in Explorer.</summary>
    public string? ShortcutPath { get; }

    public bool IsShortcutOnly => ShortcutPath != null && AppId == ShortcutPath;

    /// <summary>What to hand to ShellExecute. shell:AppsFolder launches work for desktop and Store apps alike.</summary>
    public string LaunchTarget => IsShortcutOnly ? ShortcutPath! : @"shell:AppsFolder\" + AppId;

    public string UsageKey => "app:" + AppId;
}

/// <summary>
/// Installed apps: everything in shell:AppsFolder (desktop + Store/UWP apps, same list as Start's "All apps"),
/// matched against the Start Menu .lnk folders. Cached to disk; refreshed in the background.
/// </summary>
public sealed class AppIndex : IDisposable
{
    private const int CacheMagic = 0x31415053; // "SPA1"

    private sealed class Snapshot
    {
        public Snapshot(AppEntry[] apps)
        {
            Apps = apps;
            Names = new HashSet<string>(apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        }

        public AppEntry[] Apps { get; }
        public HashSet<string> Names { get; }
    }

    private volatile Snapshot _snapshot = new(Array.Empty<AppEntry>());
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounce;
    private int _refreshing;
    private long _lastRefresh = long.MinValue / 2;

    private static string CachePath => Path.Combine(AppPaths.DataDir, "apps.bin");

    public AppIndex()
    {
        _debounce = new Timer(_ => Refresh());
    }

    public AppEntry[] Apps => _snapshot.Apps;

    public bool HasAppNamed(string name) => _snapshot.Names.Contains(name);

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var reader = new BinaryReader(File.OpenRead(CachePath), Encoding.UTF8);
            if (reader.ReadInt32() != CacheMagic) return;
            int count = reader.ReadInt32();
            var apps = new AppEntry[count];
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                string id = reader.ReadString();
                string? lnk = reader.ReadBoolean() ? reader.ReadString() : null;
                apps[i] = new AppEntry(name, id, lnk);
            }
            _snapshot = new Snapshot(apps);
        }
        catch (Exception ex)
        {
            Log.Write("apps cache could not be read: " + ex.Message);
        }
    }

    private static void SaveCache(AppEntry[] apps)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string tmp = CachePath + ".tmp";
            using (var writer = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                writer.Write(CacheMagic);
                writer.Write(apps.Length);
                foreach (var app in apps)
                {
                    writer.Write(app.Name);
                    writer.Write(app.AppId);
                    writer.Write(app.ShortcutPath != null);
                    if (app.ShortcutPath != null) writer.Write(app.ShortcutPath);
                }
            }
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("apps cache could not be saved: " + ex.Message);
        }
    }

    /// <summary>Re-reads the app list on a background STA thread. Overlapping calls are ignored.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        var thread = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var apps = Enumerate();
                _snapshot = new Snapshot(apps);
                SaveCache(apps);
                Log.Write($"apps indexed: {apps.Length} in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Write("app indexing failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _lastRefresh, Environment.TickCount64);
                Volatile.Write(ref _refreshing, 0);
            }
        })
        {
            IsBackground = true,
            Name = "Spotlight apps",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void RefreshIfStale(TimeSpan maxAge)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastRefresh) > maxAge.TotalMilliseconds) Refresh();
    }

    /// <summary>Re-index shortly after anything changes in the Start Menu folders (installs/uninstalls).</summary>
    public void StartWatching()
    {
        foreach (var dir in StartMenuDirs())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler changed = (_, _) => _debounce.Change(3000, Timeout.Infinite);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, _) => _debounce.Change(3000, Timeout.Infinite);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Write($"cannot watch {dir}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> StartMenuDirs()
    {
        // Common first so the user's own shortcuts win on name clashes.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
    }

    private static bool IsJunk(string name) => name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ScanShortcuts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var dir in StartMenuDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", options))
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (!IsJunk(name)) map[name] = lnk;
            }
        }
        return map;
    }

    /// <summary>Must run on an STA thread (uses the Shell.Application COM object).</summary>
    private static AppEntry[] Enumerate()
    {
        var shortcuts = ScanShortcuts();
        var result = new List<AppEntry>(256);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || IsJunk(name)) continue;
                if (!seenIds.Add(id)) continue;
                seenNames.Add(name);
                shortcuts.TryGetValue(name, out var lnk);
                result.Add(new AppEntry(name, id, lnk));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        // Shortcuts that for some reason aren't in the apps list.
        foreach (var (name, lnk) in shortcuts)
        {
            if (seenNames.Add(name)) result.Add(new AppEntry(name, lnk, lnk));
        }

        return result.ToArray();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _debounce.Dispose();
    }
}
