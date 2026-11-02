using Spotlight.Indexing;
using Spotlight.Services;

namespace Spotlight.Search;

public enum ResultKind { App, Folder, File }

public sealed class SearchHit
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string KindLabel { get; init; }
    public required ResultKind Kind { get; init; }

    /// <summary>Full path for files and folders; the app id for apps.</summary>
    public required string Target { get; init; }

    public required string UsageKey { get; init; }
    public AppEntry? App { get; init; }
    public double Score { get; init; }

    /// <summary>Shell parsing name used to fetch the icon.</summary>
    public string IconSource => App switch
    {
        null => Target,
        { IsShortcutOnly: true } app => app.ShortcutPath!,
        var app => @"shell:AppsFolder\" + app.AppId,
    };
}

/// <summary>Scores every app and indexed file against the query and keeps the best few.</summary>
public sealed class SearchEngine
{
    private const double AppBonus = 200;    // apps are what you usually want
    private const double FolderBonus = 15;
    private const double NoisePenalty = 150; // program internals nobody opens by hand
    private const double PickBonus = 300;   // you chose this result for this exact query before

    private static readonly HashSet<string> NoiseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".sys", ".dat", ".ini", ".log", ".bak", ".cache", ".pak", ".bin", ".db", ".pdb",
        ".manifest", ".cfg", ".lock", ".old", ".tlog", ".pyc", ".mui", ".cat",
    };

    private readonly AppIndex _apps;
    private readonly FileIndex _files;
    private readonly UsageTracker _usage;

    public SearchEngine(AppIndex apps, FileIndex files, UsageTracker usage)
    {
        _apps = apps;
        _files = files;
        _usage = usage;
    }

    public AppIndex Apps => _apps;

    private readonly record struct Candidate(AppEntry? App, DirNode? Node, string Name, bool IsDir);

    public List<SearchHit> Search(string query, int max)
    {
        string q = FuzzyMatcher.NormalizeQuery(query);
        var hits = new List<SearchHit>();
        if (q.Length == 0 || max <= 0) return hits;

        var top = new TopResults<Candidate>(max);

        foreach (var app in _apps.Apps)
        {
            int s = FuzzyMatcher.Score(q, app.Name);
            if (s < 0) continue;
            top.Offer(s + AppBonus + UsageBonus(q, app.UsageKey), new Candidate(app, null, app.Name, false));
        }

        _files.Visit(node =>
        {
            foreach (var item in node.Items)
            {
                int s = FuzzyMatcher.Score(q, item.Name);
                if (s < 0) continue;

                // A Desktop shortcut to an app we already list would just be a duplicate.
                if (!item.IsDir && item.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    && _apps.HasAppNamed(ShortcutBaseName(item.Name))) continue;

                double score = s;
                if (item.IsDir) score += FolderBonus;
                else if (NoiseExtensions.Contains(Path.GetExtension(item.Name))) score -= NoisePenalty;
                if (_usage.HasFileName(item.Name)) score += UsageBonus(q, Path.Join(node.Path, item.Name));
                top.Offer(score, new Candidate(null, node, item.Name, item.IsDir));
            }
        });

        for (int i = 0; i < top.Count; i++)
        {
            var (score, c) = top[i];
            hits.Add(ToHit(c, score));
        }
        return hits;
    }

    /// <summary>"Google Chrome (2).lnk" / "Google Chrome - Shortcut.lnk" → "Google Chrome".</summary>
    public static string ShortcutBaseName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (name.EndsWith(" - Shortcut", StringComparison.OrdinalIgnoreCase)) name = name[..^11];
        if (name.EndsWith(')'))
        {
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && int.TryParse(name.AsSpan(open + 2, name.Length - open - 3), out _)) name = name[..open];
        }
        return name;
    }

    private double UsageBonus(string query, string key) =>
        _usage.Boost(key) + (_usage.IsPick(query, key) ? PickBonus : 0);

    private static SearchHit ToHit(Candidate c, double score)
    {
        if (c.App is { } app)
        {
            return new SearchHit
            {
                Title = app.Name,
                Subtitle = "Application",
                KindLabel = "",
                Kind = ResultKind.App,
                Target = app.AppId,
                UsageKey = app.UsageKey,
                App = app,
                Score = score,
            };
        }

        string path = Path.Join(c.Node!.Path, c.Name);
        string ext = c.IsDir ? "" : Path.GetExtension(c.Name);
        bool hideExtension = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
        return new SearchHit
        {
            Title = hideExtension ? Path.GetFileNameWithoutExtension(c.Name) : c.Name,
            Subtitle = FriendlyFolder(c.Node.Path),
            KindLabel = c.IsDir ? "Folder" : ext.Length > 1 ? ext[1..].ToUpperInvariant() : "File",
            Kind = c.IsDir ? ResultKind.Folder : ResultKind.File,
            Target = path,
            UsageKey = path,
            Score = score,
        };
    }

    /// <summary>"C:\Users\me\Documents\Work" → "Documents › Work".</summary>
    public static string FriendlyFolder(string folder)
    {
        string profile = AppPaths.UserProfile;
        if (folder.Equals(profile, StringComparison.OrdinalIgnoreCase)) return "Home";
        if (folder.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return folder[(profile.Length + 1)..].Replace("\\", " › ");
        }
        return folder;
    }
}

/// <summary>Keeps the N highest-scoring items without sorting everything.</summary>
internal sealed class TopResults<T>
{
    private readonly double[] _scores;
    private readonly T[] _items;

    public TopResults(int capacity)
    {
        _scores = new double[capacity];
        _items = new T[capacity];
    }

    public int Count { get; private set; }

    public (double Score, T Item) this[int index] => (_scores[index], _items[index]);

    public void Offer(double score, T item)
    {
        if (Count == _scores.Length && score <= _scores[Count - 1]) return;
        int i = Count < _scores.Length ? Count++ : Count - 1;
        while (i > 0 && _scores[i - 1] < score)
        {
            _scores[i] = _scores[i - 1];
            _items[i] = _items[i - 1];
            i--;
        }
        _scores[i] = score;
        _items[i] = item;
    }
}
