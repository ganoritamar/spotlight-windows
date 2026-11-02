using Spotlight.Interop;

namespace Spotlight.Services;

internal static class AppPaths
{
    /// <summary>Settings and usage history (roams with the user profile).</summary>
    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotlight");

    /// <summary>Index caches and the log (machine-local, safe to delete).</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight");

    public static string UserProfile { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads =>
        Native.SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var path) == 0
            ? path
            : Path.Combine(UserProfile, "Downloads");

    public static string? KnownFolder(Guid id) =>
        Native.SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) == 0 ? path : null;
}
