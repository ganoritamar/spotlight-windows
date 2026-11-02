using Microsoft.Win32;

namespace Spotlight.Services;

/// <summary>Start-with-Windows via the per-user Run key (no admin needed).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Spotlight";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>If the Spotlight folder was moved since Start with Windows was turned on, follow it.</summary>
    public static void FixPathIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string current) return;
        int end = current.IndexOf('"', 1);
        string oldExe = current.StartsWith('"') && end > 0 ? current[1..end] : current;
        if (!File.Exists(oldExe)) key.SetValue(ValueName, Command);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
