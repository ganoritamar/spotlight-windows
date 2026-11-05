using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Spotlight.Interop;
using Spotlight.Services;

namespace Spotlight;

/// <summary>The settings window opened from the tray. Created only while open, so it costs nothing when closed.</summary>
public partial class SettingsWindow : Window
{
    private const int TestHotkeyId = 0x5150;
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly Settings _original;
    private readonly Func<(int Files, int Apps, bool Scanning)> _status;
    private readonly ObservableCollection<string> _folders;
    private readonly Brush _normalBorder;
    private readonly Brush _normalHint;
    private string _hotkey;
    private bool _capturing;
    private IntPtr _hook;
    private Native.LowLevelKeyboardProc? _hookProc; // kept in a field so it isn't garbage-collected while hooked

    /// <summary>Raised with the new settings after they were saved to disk.</summary>
    public event Action<Settings>? Saved;
    public event Action? RebuildRequested;
    /// <summary>True while the user is pressing a new shortcut, so the current one doesn't open the search bar.</summary>
    public event Action<bool>? CapturingHotkey;

    public SettingsWindow(Settings settings, Func<(int Files, int Apps, bool Scanning)> status)
    {
        InitializeComponent();
        _original = settings;
        _status = status;
        _hotkey = settings.Hotkey;
        _folders = new ObservableCollection<string>(settings.Folders);
        _normalBorder = HotkeyBox.BorderBrush;
        _normalHint = HotkeyHint.Foreground;

        HotkeyText.Text = _hotkey;
        FolderList.ItemsSource = _folders;
        ResultsSlider.Value = settings.MaxResults;
        StartupBox.IsChecked = StartupManager.IsEnabled;
        FullscreenBox.IsChecked = settings.IgnoreHotkeyInFullscreen;
        RefreshStatus();

        HotkeyBox.MouseLeftButtonUp += (_, _) => StartCapture();
        HotkeyBox.LostKeyboardFocus += (_, _) => StopCapture();
        PreviewKeyDown += OnPreviewKeyDown;
        FolderList.SelectionChanged += (_, _) => UpdateRemoveButton();
        AddFolderButton.Click += (_, _) => AddFolder();
        RemoveFolderButton.Click += (_, _) => RemoveFolder();
        RebuildButton.Click += (_, _) => Rebuild();
        AdvancedLink.Click += (_, _) => MainWindow.OpenSettingsFile();
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();
        SourceInitialized += (_, _) =>
        {
            int on = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
        Closed += (_, _) => StopCapture();
    }

    public void RefreshStatus()
    {
        var (files, apps, scanning) = _status();
        StatusText.Text = scanning ? "Indexing…" : $"{files:N0} files and folders · {apps:N0} apps";
        RebuildButton.IsEnabled = !scanning;
    }

    // ---- shortcut -----------------------------------------------------------------------------

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        CapturingHotkey?.Invoke(true);
        Keyboard.Focus(HotkeyBox);
        HotkeyBox.BorderBrush = (Brush)FindResource("Accent");
        HotkeyText.Text = "Press keys…";
        SetHint("Press the new shortcut, for example Ctrl+Space or Alt+S. Esc cancels.", warning: false);

        _hookProc ??= OnLowLevelKey;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("keyboard hook failed; shortcuts owned by other apps can't be detected");
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        HotkeyBox.BorderBrush = _normalBorder;
        HotkeyText.Text = _hotkey;
        CapturingHotkey?.Invoke(false);
    }

    // Keys pressed while recording are swallowed here (except Ctrl/Alt/Shift/Win themselves),
    // so the new shortcut neither types anything nor triggers the app that already owns it.
    private IntPtr OnLowLevelKey(int code, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        if (code >= 0 && _capturing && (message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vk);
            if (!IsModifierKey(key))
            {
                var modifiers = CurrentModifiers();
                Dispatcher.BeginInvoke(() => OnCapturedKey(key, modifiers));
                return 1;
            }
            // The key being pressed isn't counted as "down" yet at this point, so add it.
            var withThis = CurrentModifiers() | key switch
            {
                Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
                Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
                Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
                _ => ModifierKeys.Windows,
            };
            Dispatcher.BeginInvoke(() => OnCapturedKey(key, withThis));
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsDown(Native.VK_CONTROL)) modifiers |= ModifierKeys.Control;
        if (IsDown(Native.VK_MENU)) modifiers |= ModifierKeys.Alt;
        if (IsDown(Native.VK_SHIFT)) modifiers |= ModifierKeys.Shift;
        if (IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    // Fallback if the hook couldn't be installed: normal key presses still work for free shortcuts.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        if (_hook == IntPtr.Zero) OnCapturedKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }

    private void OnCapturedKey(Key key, ModifierKeys modifiers)
    {
        if (!_capturing) return;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopCapture();
            SetHint("Click the box, then press the keys you want to open Spotlight with.", warning: false);
            return;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (IsModifierKey(key))
        {
            HotkeyText.Text = string.Join("+", parts) + "+…";
            return;
        }
        if (parts.Count == 0)
        {
            SetHint("A shortcut needs Ctrl, Alt, Shift or Win plus another key.", warning: true);
            return;
        }

        parts.Add(key.ToString());
        string candidate = string.Join("+", parts);
        if (!Hotkey.TryParse(candidate, out var hotkey))
        {
            SetHint($"{candidate} can't be used as a shortcut. Try another.", warning: true);
            return;
        }
        if (IsTakenByAnotherApp(hotkey))
        {
            SetHint($"Another app already uses {candidate}. Try another.", warning: true);
            return;
        }

        _hotkey = candidate;
        StopCapture();
        SetHint(candidate == _original.Hotkey
            ? "Click the box, then press the keys you want to open Spotlight with."
            : $"Spotlight will open with {candidate} after you click Save.", warning: false);
    }

    /// <summary>Tries the shortcut for a moment. Spotlight's own shortcut is paused while capturing, so it doesn't count.</summary>
    private bool IsTakenByAnotherApp(Hotkey hotkey)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Native.RegisterHotKey(hwnd, TestHotkeyId, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.VirtualKey)) return true;
        Native.UnregisterHotKey(hwnd, TestHotkeyId);
        return false;
    }

    private void SetHint(string text, bool warning)
    {
        HotkeyHint.Text = text;
        HotkeyHint.Foreground = warning ? WarningBrush : _normalHint;
    }

    // ---- folders ------------------------------------------------------------------------------

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for Spotlight to search", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var folder in dialog.FolderNames)
        {
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder);
        }
        UpdateRemoveButton();
    }

    private void RemoveFolder()
    {
        foreach (var folder in FolderList.SelectedItems.Cast<string>().ToList()) _folders.Remove(folder);
        UpdateRemoveButton();
    }

    // At least one folder has to stay; with none, Spotlight would fall back to the defaults anyway.
    private void UpdateRemoveButton() =>
        RemoveFolderButton.IsEnabled = FolderList.SelectedItems.Count > 0 && FolderList.SelectedItems.Count < _folders.Count;

    // ---- index / save -------------------------------------------------------------------------

    private void Rebuild()
    {
        RebuildRequested?.Invoke();
        StatusText.Text = "Indexing…";
        RebuildButton.IsEnabled = false;
    }

    private void Save()
    {
        var settings = new Settings
        {
            Hotkey = _hotkey,
            IgnoreHotkeyInFullscreen = FullscreenBox.IsChecked == true,
            Folders = _folders.ToList(),
            ExcludeFolderNames = _original.ExcludeFolderNames,
            SkipDotFolders = _original.SkipDotFolders,
            MaxResults = (int)ResultsSlider.Value,
        };

        try
        {
            settings.Save();
            if (StartupBox.IsChecked == true != StartupManager.IsEnabled) StartupManager.SetEnabled(StartupBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            Log.Write("could not save settings: " + ex.Message);
            MessageBox.Show(this, "Spotlight couldn't save the settings:\n" + ex.Message, "Spotlight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Saved?.Invoke(settings);
        Close();
    }
}
