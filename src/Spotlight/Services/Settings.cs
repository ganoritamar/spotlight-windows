using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spotlight.Services;

/// <summary>User settings, stored as readable JSON in %APPDATA%\Spotlight\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Global shortcut, e.g. "Alt+Space", "Ctrl+Space", "Ctrl+Shift+K".</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>Folders whose files are indexed (including everything inside them).</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Folder names that are skipped wherever they appear.</summary>
    public List<string> ExcludeFolderNames { get; set; } = new()
    {
        "node_modules", ".git", "bin", "obj", "__pycache__", ".vs", ".venv", "venv", "$RECYCLE.BIN",
    };

    /// <summary>Skip folders whose name starts with a dot (".cache", ".config", ...).</summary>
    public bool SkipDotFolders { get; set; } = true;

    /// <summary>How many results to show (3–15).</summary>
    public int MaxResults { get; set; } = 8;

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppPaths.ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "+" and Hebrew readable in the file
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
            else
            {
                var defaults = new Settings { Folders = DefaultFolders() };
                defaults.Save();
                return defaults;
            }
        }
        catch (Exception ex)
        {
            // Broken JSON: keep the user's file untouched and run with defaults.
            Log.Write("settings.json could not be read, using defaults: " + ex.Message);
        }

        return new Settings { Folders = DefaultFolders() };
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void Normalize()
    {
        MaxResults = Math.Clamp(MaxResults, 3, 15);
        ExcludeFolderNames ??= new List<string>();
        Folders = (Folders ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Environment.ExpandEnvironmentVariables(f.Trim()))
            .Select(f => f.StartsWith('~') ? AppPaths.UserProfile + f[1..] : f)
            .ToList();
        if (Folders.Count == 0) Folders = DefaultFolders();
    }

    public static List<string> DefaultFolders() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AppPaths.Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        }
        .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
