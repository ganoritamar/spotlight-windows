using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spotlight.Indexing;
using Spotlight.Search;

namespace Spotlight.Services;

/// <summary>Opens results, or shows them in Explorer.</summary>
internal static class Launcher
{
    private const int ErrorCancelled = 1223; // e.g. the user said "No" to an admin prompt

    public static bool Open(SearchHit hit)
    {
        try
        {
            var info = hit.App != null
                ? new ProcessStartInfo(hit.App.LaunchTarget) { UseShellExecute = true }
                : new ProcessStartInfo(hit.Target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(hit.Target) ?? "",
                };
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"could not open {hit.Target}: {ex.Message}");
            return false;
        }
    }

    public static void Reveal(SearchHit hit)
    {
        try
        {
            string? path = hit.App != null ? ResolveAppLocation(hit.App) : hit.Target;
            if (path == null)
            {
                // Store apps have no folder you can browse; show the Applications folder instead.
                Process.Start(new ProcessStartInfo("shell:AppsFolder") { UseShellExecute = true })?.Dispose();
                return;
            }
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"could not reveal {hit.Target}: {ex.Message}");
        }
    }

    private static string? ResolveAppLocation(AppEntry app)
    {
        string id = app.AppId;
        if (Path.IsPathFullyQualified(id) && (File.Exists(id) || Directory.Exists(id))) return id;

        // "{known-folder-guid}\Vendor\app.exe", e.g. Program Files.
        if (id.StartsWith('{'))
        {
            int close = id.IndexOf('}');
            if (close > 0 && Guid.TryParse(id[..(close + 1)], out var folderId) && AppPaths.KnownFolder(folderId) is { } root)
            {
                string path = Path.Join(root, id[(close + 1)..].TrimStart('\\'));
                if (File.Exists(path)) return path;
            }
        }

        if (app.ShortcutPath != null && File.Exists(app.ShortcutPath))
        {
            string? target = ShortcutTarget(app.ShortcutPath);
            return target != null && File.Exists(target) ? target : app.ShortcutPath;
        }
        return null;
    }

    private static string? ShortcutTarget(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return null;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
            dynamic item = folder.ParseName(Path.GetFileName(lnkPath));
            return (string)item.GetLink.Path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
