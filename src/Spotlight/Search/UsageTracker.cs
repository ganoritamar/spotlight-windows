using System.Text.Json;
using Spotlight.Services;

namespace Spotlight.Search;

/// <summary>
/// Remembers what you open (how often, how recently) and what you picked for a given query,
/// so those results rise to the top. Stored in %APPDATA%\Spotlight\usage.json. UI thread only.
/// </summary>
public sealed class UsageTracker
{
    private const int MaxItems = 2000;
    private const int MaxPicks = 1000;

    public sealed class Record
    {
        public int Count { get; set; }
        public long LastUsed { get; set; } // unix seconds
    }

    public sealed class Data
    {
        public Dictionary<string, Record> Items { get; set; } = new();
        public Dictionary<string, string> Picks { get; set; } = new();
    }

    private readonly Dictionary<string, Record> _items;
    private readonly Dictionary<string, string> _picks;
    private readonly HashSet<string> _fileNames; // last path segment of every file key, for a fast pre-check

    private static string FilePath => Path.Combine(AppPaths.ConfigDir, "usage.json");

    private UsageTracker(Data data)
    {
        _items = new Dictionary<string, Record>(data.Items, StringComparer.OrdinalIgnoreCase);
        _picks = new Dictionary<string, string>(data.Picks, StringComparer.OrdinalIgnoreCase);
        _fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _items.Keys) AddFileName(key);
    }

    public static UsageTracker Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                if (data != null) return new UsageTracker(data);
            }
        }
        catch (Exception ex)
        {
            Log.Write("usage.json could not be read: " + ex.Message);
        }
        return new UsageTracker(new Data());
    }

    private void AddFileName(string key)
    {
        if (!key.StartsWith("app:", StringComparison.Ordinal)) _fileNames.Add(Path.GetFileName(key));
    }

    /// <summary>True when some file with this name has usage history (lets search skip building paths).</summary>
    public bool HasFileName(string name) => _fileNames.Contains(name);

    /// <summary>0–300 points: more launches and more recent launches score higher (half-life two weeks).</summary>
    public double Boost(string key)
    {
        if (!_items.TryGetValue(key, out var record)) return 0;
        double days = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - record.LastUsed) / 86400.0);
        double frequency = Math.Log2(1 + record.Count) * 45;
        double recency = 90 * Math.Pow(0.5, days / 14);
        return Math.Min(300, frequency + recency);
    }

    public bool IsPick(string query, string key) =>
        _picks.TryGetValue(query, out var picked) && string.Equals(picked, key, StringComparison.OrdinalIgnoreCase);

    public void RecordLaunch(string query, string key)
    {
        if (!_items.TryGetValue(key, out var record))
        {
            record = new Record();
            _items[key] = record;
            AddFileName(key);
        }
        record.Count++;
        record.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (query.Length > 0) _picks[query] = key;

        Prune();
        SaveInBackground();
    }

    private void Prune()
    {
        if (_items.Count > MaxItems)
        {
            foreach (var key in _items.OrderBy(kv => kv.Value.LastUsed).Take(_items.Count - MaxItems).Select(kv => kv.Key).ToList())
            {
                _items.Remove(key);
            }
        }
        if (_picks.Count > MaxPicks)
        {
            foreach (var query in _picks.Keys.Where(q => !_items.ContainsKey(_picks[q])).ToList()) _picks.Remove(query);
            foreach (var query in _picks.Keys.Take(_picks.Count - MaxPicks).ToList()) _picks.Remove(query);
        }
    }

    private void SaveInBackground()
    {
        string json = JsonSerializer.Serialize(new Data { Items = _items, Picks = _picks });
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("usage.json could not be saved: " + ex.Message);
            }
        });
    }
}
