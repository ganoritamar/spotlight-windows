using System.Runtime;
using System.Windows;
using Spotlight.Indexing;
using Spotlight.Interop;
using Spotlight.Search;
using Spotlight.Services;

namespace Spotlight;

public partial class App : Application
{
    private const string InstanceName = @"Local\Spotlight.Launcher";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _quitWait;

    private MainWindow? _window;
    private AppIndex? _apps;
    private FileIndex? _files;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool quit = e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));
        bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // Only one copy runs. Starting it again opens the search bar (or quits it with --quit).
        _instanceMutex = new Mutex(true, InstanceName + ".Instance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");
        if (!isFirst || quit)
        {
            if (!isFirst)
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                (quit ? _quitSignal : _showSignal).Set();
            }
            Shutdown();
            return;
        }

        Log.Init();
        Log.Write($"started (pid {Environment.ProcessId})");
        DispatcherUnhandledException += (_, args) =>
        {
            // Log and keep running rather than vanishing from the tray.
            Log.Write("unexpected error: " + args.Exception);
            args.Handled = true;
        };

        try { StartupManager.FixPathIfMoved(); }
        catch (Exception ex) { Log.Write("could not check the startup entry: " + ex.Message); }

        var settings = Settings.Load();
        var usage = UsageTracker.Load();
        _apps = new AppIndex();
        _apps.LoadCache();
        _files = new FileIndex(settings);
        var engine = new SearchEngine(_apps, _files, usage);

        _window = new MainWindow(engine, usage, settings);
        _window.RebuildRequested += Rebuild;
        _window.ExitRequested += ExitApp;
        _window.Initialize();

        _apps.Refresh();
        _apps.StartWatching();
        _files.ScanCompleted += () => Dispatcher.BeginInvoke(() => { if (!_window.IsVisible) TrimMemory(); });
        _files.Start();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => _window.ShowLauncher()), null, Timeout.Infinite, false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => Dispatcher.BeginInvoke(ExitApp), null, Timeout.Infinite, false);

        if (!background) _window.ShowLauncher();
    }

    private void Rebuild()
    {
        var settings = Settings.Load();
        _window?.ApplySettings(settings);
        _apps?.Refresh();
        _files?.Rebuild(settings);
    }

    private void ExitApp()
    {
        Log.Write("exiting");
        _showWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _window?.Shutdown();
        _apps?.Dispose();
        _files?.Dispose();
        Shutdown();
    }

    /// <summary>Hands unused memory back to Windows. Called a few seconds after the bar hides.</summary>
    public static void TrimMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }
}
