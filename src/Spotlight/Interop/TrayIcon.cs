using System.Runtime.InteropServices;

namespace Spotlight.Interop;

internal sealed record TrayMenuItem(string? Text, Action? Action = null, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new((string?)null);
}

/// <summary>
/// A notification-area icon built directly on Shell_NotifyIcon, so we don't pull in WinForms.
/// Messages arrive through the owner window's WndProc and are forwarded to <see cref="HandleMessage"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly string _tooltip;
    private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    public event Action? Clicked;
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public TrayIcon(IntPtr hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _tooltip = tooltip;
        int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        _hIcon = Native.LoadImage(IntPtr.Zero, iconPath, Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);

        try
        {
            Native.SetPreferredAppMode(2); // force dark menus
            Native.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }

        Add();
    }

    private Native.NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var data = CreateData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        var data = CreateData();
        data.uFlags = Native.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == Native.WM_LBUTTONUP) Clicked?.Invoke();
            else if (evt == Native.WM_RBUTTONUP) ShowMenu();
            return true;
        }

        if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            Add(); // Explorer restarted: put the icon back.
            return true;
        }

        return false;
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;

        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Text == null)
                {
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
                    continue;
                }
                Native.AppendMenu(menu, Native.MF_STRING | (item.Checked ? Native.MF_CHECKED : 0), (nuint)(i + 1), item.Text);
                if (item.IsDefault) Native.SetMenuDefaultItem(menu, (uint)(i + 1), 0);
            }

            Native.GetCursorPos(out var pt);
            Native.SetForegroundWindow(_hwnd); // required so the menu closes when you click elsewhere
            int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            Native.PostMessage(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0) items[cmd - 1].Action?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        var data = CreateData();
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }
}
