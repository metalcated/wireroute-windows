using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireRoute.App.Interop;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 37;
    private const int WindowProcedureIndex = -4;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconShowTip = 0x00000080;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const int LargeIconWidth = 11;
    private const int LargeIconHeight = 12;
    private const int SmallIconWidth = 49;
    private const int SmallIconHeight = 50;
    private const uint SetIconMessage = 0x0080;
    private const int SetSmallIcon = 0;
    private const int SetLargeIcon = 1;
    private const int LeftButtonUp = 0x0202;
    private const int LeftButtonDoubleClick = 0x0203;
    private const int RightButtonUp = 0x0205;
    private const int ContextMenu = 0x007B;
    private const int NotifyIconSelect = 0x0400;
    private const int NotifyIconKeySelect = 0x0401;
    private const uint MenuString = 0x00000000;
    private const uint MenuGray = 0x00000001;
    private const uint MenuChecked = 0x00000008;
    private const uint MenuPopup = 0x00000010;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackNoNotify = 0x0080;
    private const uint NullMessage = 0x0000;
    private const uint FirstCommandId = 100;
    private const int MaximumInlineTunnels = 10;

    private readonly nint windowHandle;
    private readonly Action activateWindow;
    private readonly Func<TrayMenuSnapshot> createMenuSnapshot;
    private readonly Action<TrayMenuAction> executeMenuAction;
    private readonly WindowProcedure windowProcedure;
    private readonly nint previousWindowProcedure;
    private readonly nint previousSmallIcon;
    private readonly nint previousLargeIcon;
    private readonly nint originalIconHandle;
    private readonly uint taskbarCreatedMessage;
    private nint iconHandle;
    private nint largeIconHandle;
    private bool isAdded;
    private bool isDisposed;
    private string appearanceStyle = string.Empty;
    private bool appearanceActive;
    private bool appearanceTransitioning;

    public TrayIcon(
        nint windowHandle,
        string iconPath,
        Action activateWindow,
        Func<TrayMenuSnapshot> createMenuSnapshot,
        Action<TrayMenuAction> executeMenuAction)
    {
        this.windowHandle = windowHandle;
        this.activateWindow = activateWindow;
        this.createMenuSnapshot = createMenuSnapshot;
        this.executeMenuAction = executeMenuAction;
        var iconWidth = GetSystemMetrics(SmallIconWidth);
        var iconHeight = GetSystemMetrics(SmallIconHeight);
        iconHandle = LoadImage(
            0,
            iconPath,
            ImageIcon,
            iconWidth,
            iconHeight,
            LoadFromFile);
        if (iconHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WireRoute could not load its notification icon.");
        }
        originalIconHandle = iconHandle;

        largeIconHandle = LoadImage(
            0,
            iconPath,
            ImageIcon,
            GetSystemMetrics(LargeIconWidth),
            GetSystemMetrics(LargeIconHeight),
            LoadFromFile);
        if (largeIconHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyIcon(iconHandle);
            iconHandle = 0;
            throw new Win32Exception(error, "WireRoute could not load its window icon.");
        }

        windowProcedure = ProcessWindowMessage;
        previousWindowProcedure = SetWindowLongPtr(
            windowHandle,
            WindowProcedureIndex,
            Marshal.GetFunctionPointerForDelegate(windowProcedure));
        if (previousWindowProcedure == 0)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyIcon(largeIconHandle);
            largeIconHandle = 0;
            DestroyIcon(iconHandle);
            iconHandle = 0;
            throw new Win32Exception(error, "WireRoute could not register its notification icon window.");
        }

        previousSmallIcon = SendMessage(windowHandle, SetIconMessage, SetSmallIcon, iconHandle);
        previousLargeIcon = SendMessage(windowHandle, SetIconMessage, SetLargeIcon, largeIconHandle);
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        try
        {
            AddIcon();
        }
        catch
        {
            SendMessage(windowHandle, SetIconMessage, SetSmallIcon, previousSmallIcon);
            SendMessage(windowHandle, SetIconMessage, SetLargeIcon, previousLargeIcon);
            SetWindowLongPtr(windowHandle, WindowProcedureIndex, previousWindowProcedure);
            DestroyIcon(largeIconHandle);
            largeIconHandle = 0;
            DestroyIcon(iconHandle);
            iconHandle = 0;
            throw;
        }
    }

    private void AddIcon()
    {
        var data = CreateIconData();
        if (!ShellNotifyIcon(NotifyIconAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WireRoute could not add its notification icon.");
        }

        isAdded = true;
        data.TimeoutOrVersion = NotifyIconVersion4;
        ShellNotifyIcon(NotifyIconSetVersion, ref data);
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = windowHandle,
        Id = IconId,
        Flags = NotifyIconMessage | NotifyIconIcon | NotifyIconTip | NotifyIconShowTip,
        CallbackMessage = CallbackMessage,
        IconHandle = iconHandle,
        Tip = "WireRoute",
    };

    public void SetAppearance(string style, bool active, bool transitioning)
    {
        if (isDisposed
            || appearanceStyle.Equals(style, StringComparison.OrdinalIgnoreCase)
            && appearanceActive == active
            && appearanceTransitioning == transitioning)
        {
            return;
        }

        var size = Math.Max(
            GetSystemMetrics(SmallIconWidth),
            GetSystemMetrics(SmallIconHeight));
        var replacement = WireRouteTrayIconRenderer.Create(style, size, active, transitioning);
        var previous = iconHandle;
        iconHandle = replacement;
        var data = CreateIconData();
        if (isAdded && !ShellNotifyIcon(NotifyIconModify, ref data))
        {
            iconHandle = previous;
            DestroyIcon(replacement);
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "WireRoute could not update its notification icon.");
        }

        appearanceStyle = style;
        appearanceActive = active;
        appearanceTransitioning = transitioning;
        if (previous != 0 && previous != originalIconHandle)
        {
            DestroyIcon(previous);
        }
    }

    private nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == taskbarCreatedMessage)
        {
            isAdded = false;
            try
            {
                AddIcon();
            }
            catch
            {
                // Explorer can still be rebuilding when TaskbarCreated is received.
            }

            return 0;
        }

        if (message == CallbackMessage)
        {
            var notification = unchecked((int)((long)lParam & 0xffff));
            if (notification is RightButtonUp or ContextMenu)
            {
                ShowContextMenu();
                return 0;
            }

            if (notification is LeftButtonUp
                or LeftButtonDoubleClick
                or NotifyIconSelect
                or NotifyIconKeySelect)
            {
                activateWindow();
                return 0;
            }
        }

        return CallWindowProc(previousWindowProcedure, window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var snapshot = createMenuSnapshot();
        var actions = new Dictionary<uint, TrayMenuAction>();
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        nint tunnelsMenu = 0;
        try
        {
            AppendMenu(menu, MenuString | MenuGray, 0, EscapeMenuText(snapshot.StatusText));
            if (snapshot.Tunnels.Count > 0)
            {
                AppendMenu(menu, MenuSeparator, 0, null);
                if (snapshot.Tunnels.Count > MaximumInlineTunnels)
                {
                    tunnelsMenu = CreatePopupMenu();
                    if (tunnelsMenu != 0)
                    {
                        AppendTunnels(tunnelsMenu, snapshot.Tunnels, actions);
                        AppendMenu(menu, MenuString | MenuPopup, unchecked((nuint)tunnelsMenu), "Tunnels");
                    }
                }
                else
                {
                    AppendTunnels(menu, snapshot.Tunnels, actions);
                }
            }

            AppendMenu(menu, MenuSeparator, 0, null);
            AppendAction(menu, actions, "Manage Tunnels", new TrayMenuAction(TrayMenuActionKind.ManageTunnels));
            AppendAction(menu, actions, "Import Tunnel(s) from File…", new TrayMenuAction(TrayMenuActionKind.ImportTunnels));
            AppendAction(menu, actions, "RouterOS Peer Manager…", new TrayMenuAction(TrayMenuActionKind.RouterOSPeerManager));
            AppendAction(menu, actions, "Settings…", new TrayMenuAction(TrayMenuActionKind.Settings));
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendAction(menu, actions, "About WireRoute", new TrayMenuAction(TrayMenuActionKind.About));
            AppendAction(menu, actions, "Quit WireRoute", new TrayMenuAction(TrayMenuActionKind.Quit));

            if (!GetCursorPos(out var point))
            {
                return;
            }

            SetForegroundWindow(windowHandle);
            var command = TrackPopupMenuEx(
                menu,
                TrackRightButton | TrackReturnCommand | TrackNoNotify,
                point.X,
                point.Y,
                windowHandle,
                0);
            PostMessage(windowHandle, NullMessage, 0, 0);
            if (command != 0 && actions.TryGetValue(command, out var action))
            {
                executeMenuAction(action);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void AppendTunnels(
        nint menu,
        IReadOnlyList<TrayTunnelMenuItem> tunnels,
        IDictionary<uint, TrayMenuAction> actions)
    {
        foreach (var tunnel in tunnels)
        {
            var flags = MenuString;
            if (!tunnel.IsEnabled)
            {
                flags |= MenuGray;
            }

            if (tunnel.IsChecked)
            {
                flags |= MenuChecked;
            }

            AppendAction(
                menu,
                actions,
                tunnel.DisplayName,
                new TrayMenuAction(TrayMenuActionKind.ToggleTunnel, tunnel.ManagerName),
                flags);
        }
    }

    private static void AppendAction(
        nint menu,
        IDictionary<uint, TrayMenuAction> actions,
        string title,
        TrayMenuAction action,
        uint flags = MenuString)
    {
        var commandId = FirstCommandId + (uint)actions.Count;
        actions.Add(commandId, action);
        AppendMenu(menu, flags, commandId, EscapeMenuText(title));
    }

    private static string EscapeMenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        if (isAdded)
        {
            var data = CreateIconData();
            ShellNotifyIcon(NotifyIconDelete, ref data);
            isAdded = false;
        }

        SendMessage(windowHandle, SetIconMessage, SetSmallIcon, previousSmallIcon);
        SendMessage(windowHandle, SetIconMessage, SetLargeIcon, previousLargeIcon);
        SetWindowLongPtr(windowHandle, WindowProcedureIndex, previousWindowProcedure);
        if (largeIconHandle != 0)
        {
            DestroyIcon(largeIconHandle);
            largeIconHandle = 0;
        }

        if (iconHandle != 0 && iconHandle != originalIconHandle)
        {
            DestroyIcon(iconHandle);
            iconHandle = 0;
        }
        if (originalIconHandle != 0)
        {
            DestroyIcon(originalIconHandle);
            iconHandle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(
        nint previousWindowProcedure,
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint window,
        nint parameters);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}

internal sealed record TrayMenuSnapshot(string StatusText, IReadOnlyList<TrayTunnelMenuItem> Tunnels);

internal sealed record TrayTunnelMenuItem(
    string ManagerName,
    string DisplayName,
    bool IsChecked,
    bool IsEnabled);

internal sealed record TrayMenuAction(TrayMenuActionKind Kind, string? ManagerName = null);

internal enum TrayMenuActionKind
{
    ToggleTunnel,
    ManageTunnels,
    ImportTunnels,
    RouterOSPeerManager,
    Settings,
    About,
    Quit,
}
