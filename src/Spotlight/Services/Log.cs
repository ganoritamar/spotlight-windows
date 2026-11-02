namespace Spotlight.Services;

/// <summary>A tiny append-only log in %LOCALAPPDATA%\Spotlight\spotlight.log. Writes only on events, never on a timer.</summary>
internal static class Log
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "spotlight.log");

    /// <summary>Set the environment variable SPOTLIGHT_LOG=1 to also log per-search and per-show timings.</summary>
    public static bool Verbose { get; } = Environment.GetEnvironmentVariable("SPOTLIGHT_LOG") == "1";

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > 256 * 1024) file.Delete();
        }
        catch { }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
