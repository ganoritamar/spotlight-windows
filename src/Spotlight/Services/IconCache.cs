using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Threading;
using Spotlight.Interop;
using Spotlight.Search;

namespace Spotlight.Services;

/// <summary>
/// Loads icons only for the results on screen, on one background STA thread, and keeps a small cache.
/// Files share one icon per extension (except .exe/.lnk/... which have their own). Call from the UI thread.
/// </summary>
internal sealed class IconCache : IDisposable
{
    private const int MaxEntries = 256;

    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".appref-ms", ".msc", ".cpl", ".scr",
    };

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ImageSource?> _cache = new();
    private readonly Dictionary<string, List<Action<ImageSource?>>> _waiting = new();
    private readonly BlockingCollection<(string Key, string Source, int Size)> _queue = new();
    private Thread? _worker;

    public IconCache(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Request(SearchHit hit, int size, Action<ImageSource?> apply)
    {
        string key = KeyFor(hit) + "|" + size;
        if (_cache.TryGetValue(key, out var cached))
        {
            apply(cached);
            return;
        }
        if (_waiting.TryGetValue(key, out var callbacks))
        {
            callbacks.Add(apply);
            return;
        }

        _waiting[key] = new List<Action<ImageSource?>> { apply };
        EnsureWorker();
        _queue.Add((key, hit.IconSource, size));
    }

    private static string KeyFor(SearchHit hit)
    {
        if (hit.Kind != ResultKind.File) return hit.IconSource;
        string ext = Path.GetExtension(hit.Target);
        return ext.Length == 0 || PerFileExtensions.Contains(ext) ? hit.Target : "ext:" + ext.ToLowerInvariant();
    }

    private void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new Thread(Work) { IsBackground = true, Name = "Spotlight icons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private void Work()
    {
        foreach (var (key, source, size) in _queue.GetConsumingEnumerable())
        {
            ImageSource? image = null;
            try { image = ShellIcons.Load(source, size); }
            catch { }
            _dispatcher.BeginInvoke(() => Complete(key, image));
        }
    }

    private void Complete(string key, ImageSource? image)
    {
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[key] = image;
        if (_waiting.Remove(key, out var callbacks))
        {
            foreach (var apply in callbacks) apply(image);
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
