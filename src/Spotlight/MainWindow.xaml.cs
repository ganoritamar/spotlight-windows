using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Spotlight.Interop;
using Spotlight.Search;
using Spotlight.Services;

namespace Spotlight;

public partial class MainWindow : Window
{
    private const int HotkeyId = 1;

    private readonly SearchEngine _engine;
    private readonly UsageTracker _usage;
    private readonly IconCache _icons;
    private readonly DispatcherTimer _trimTimer;
    private Settings _settings;
    private IntPtr _hwnd;
    private TrayIcon? _tray;
    private bool _hotkeyRegistered;
    private bool _shuttingDown;
    private List<ResultItem> _results = new();

    public event Action? RebuildRequested;
    public event Action? ExitRequested;

    public MainWindow(SearchEngine engine, UsageTracker usage, Settings settings)
    {
        InitializeComponent();
        _engine = engine;
        _usage = usage;
        _settings = settings;
        _icons = new IconCache(Dispatcher);

        // One-shot: runs once a few seconds after the bar hides, never periodically.
        _trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            App.TrimMemory();
        };

        Query.TextChanged += (_, _) => RunSearch();
        Query.PreviewKeyDown += OnQueryKeyDown;
        Results.PreviewMouseLeftButtonUp += OnResultClicked;
        Deactivated += (_, _) => HideLauncher();
        Closing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true; // Alt+F4 just hides it
            HideLauncher();
        };
    }

    /// <summary>Creates the (hidden) window handle, frosted backdrop, tray icon and hotkey.</summary>
    public void Initialize()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);
        ApplyBackdrop();

        _tray = new TrayIcon(_hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"), "Spotlight");
        _tray.Clicked += ShowLauncher;
        _tray.MenuProvider = BuildTrayMenu;
        RegisterHotkey();
    }

    public void ApplySettings(Settings settings)
    {
        _settings = settings;
        RegisterHotkey();
    }

    private void ApplyBackdrop()
    {
        // Without the system menu style, Windows doesn't draw minimize/close buttons on the frame.
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));

        int on = 1, corner = Native.DWMWCP_ROUND, backdrop = Native.DWMSBT_TRANSIENTWINDOW, border = 0x00484848;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // The frosted backdrop needs Windows 11 22H2 or newer. Older versions (incl. Windows 10)
        // would show black behind the see-through tint, so they get a solid dark background instead.
        bool frosted = Environment.OSVersion.Version.Build >= 22621
            && Environment.GetEnvironmentVariable("SPOTLIGHT_SOLID") != "1"
            && Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        if (!frosted)
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }
        Log.Write(frosted ? "frosted backdrop on" : "solid background (frosted backdrop not available)");
    }

    private void RegisterHotkey()
    {
        if (_hotkeyRegistered)
        {
            Native.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }

        string text = _settings.Hotkey;
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            _tray?.ShowBalloon("Spotlight", $"\"{text}\" isn't a valid shortcut in settings.json. Using Ctrl+Space.");
            text = "Ctrl+Space";
            Hotkey.TryParse(text, out hotkey);
        }

        _hotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.VirtualKey);
        if (!_hotkeyRegistered)
        {
            Log.Write($"hotkey {text} is taken by another app");
            _tray?.ShowBalloon("Spotlight", $"{text} is already used by another app. Pick a different shortcut in settings.json, or click the tray icon.");
        }
        else
        {
            Log.Write($"hotkey {text} registered");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleLauncher();
            handled = true;
        }
        else if (_tray != null && _tray.HandleMessage(msg, wParam, lParam))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu() => new[]
    {
        new TrayMenuItem($"Open Spotlight\t{_settings.Hotkey}", ShowLauncher, IsDefault: true),
        TrayMenuItem.Separator,
        new TrayMenuItem("Start with Windows", () => StartupManager.SetEnabled(!StartupManager.IsEnabled), Checked: StartupManager.IsEnabled),
        new TrayMenuItem("Edit settings…", OpenSettingsFile),
        new TrayMenuItem("Reload settings and rebuild index", () => RebuildRequested?.Invoke()),
        TrayMenuItem.Separator,
        new TrayMenuItem("Quit Spotlight", () => ExitRequested?.Invoke()),
    };

    private static void OpenSettingsFile()
    {
        try
        {
            if (!File.Exists(Settings.FilePath)) Settings.Load();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\""))?.Dispose();
        }
        catch (Exception ex)
        {
            // Notepad can be uninstalled on Windows 11; fall back to whatever opens .json files.
            Log.Write("could not open settings in Notepad: " + ex.Message);
            try { Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex2) { Log.Write("could not open settings: " + ex2.Message); }
        }
    }

    // ---- show / hide --------------------------------------------------------------------------

    private void ToggleLauncher()
    {
        if (IsVisible && IsActive) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        var sw = Stopwatch.StartNew();
        _trimTimer.Stop();

        if (!IsVisible)
        {
            PlaceOnMouseScreen();
            Show();
            PlaceOnMouseScreen(); // again, in case the window was resized for a screen with different scaling
        }

        Activate();
        Native.SetForegroundWindow(_hwnd);
        Query.Focus();
        Keyboard.Focus(Query);
        Query.SelectAll();
        _engine.Apps.RefreshIfStale(TimeSpan.FromMinutes(10));

        if (Log.Verbose)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Log.Write($"shown in {sw.Elapsed.TotalMilliseconds:F1} ms"));
        }
    }

    /// <summary>Centered horizontally, about a fifth of the way down (where Spotlight sits on a Mac),
    /// on whichever screen the mouse is on.</summary>
    private void PlaceOnMouseScreen()
    {
        Native.GetCursorPos(out var cursor);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST), ref info)) return;
        if (!Native.GetWindowRect(_hwnd, out var window)) return;

        var work = info.rcWork;
        int x = work.Left + (work.Right - work.Left - (window.Right - window.Left)) / 2;
        int y = work.Top + (work.Bottom - work.Top) / 5;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void HideLauncher()
    {
        if (!IsVisible) return;
        Hide();
        _trimTimer.Start();
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
        _tray?.Dispose();
        _icons.Dispose();
        Close();
    }

    // ---- search -------------------------------------------------------------------------------

    private void RunSearch()
    {
        string text = Query.Text;
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var sw = Stopwatch.StartNew();
        var hits = _engine.Search(text, _settings.MaxResults);
        double searchMs = sw.Elapsed.TotalMilliseconds;

        _results = hits.Select(h => new ResultItem(h)).ToList();
        Results.ItemsSource = _results;
        var visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = visibility;
        Divider.Visibility = visibility;
        if (_results.Count > 0) Results.SelectedIndex = 0;

        int iconSize = (int)Math.Round(28 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        foreach (var item in _results)
        {
            _icons.Request(item.Hit, iconSize, image => item.Icon = image);
        }

        if (Log.Verbose) Log.Write($"search \"{text}\": {hits.Count} results in {searchMs:F2} ms");
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher();
                break;
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Tab:
                MoveSelection(shift ? -1 : 1);
                break;
            case Key.Enter:
                OpenSelected(reveal: ctrl);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, _results.Count - 1);
    }

    private void OpenSelected(bool reveal)
    {
        if (Results.SelectedItem is not ResultItem item) return;
        var hit = item.Hit;
        if (reveal)
        {
            Launcher.Reveal(hit);
        }
        else if (Launcher.Open(hit))
        {
            _usage.RecordLaunch(FuzzyMatcher.NormalizeQuery(Query.Text), hit.UsageKey);
        }
        HideLauncher();
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
        {
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (element is ListBoxItem { DataContext: ResultItem item })
        {
            Results.SelectedItem = item;
            OpenSelected(reveal: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }
}
