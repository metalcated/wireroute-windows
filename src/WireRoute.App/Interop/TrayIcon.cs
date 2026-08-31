using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireRoute.App.Interop;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 37;
    private const int WindowProcedureIndex = -4;
    private const uint NotifyIconAdd = 0x00000000;
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
    private const int NotifyIconSelect = 0x0400;
    private const int NotifyIconKeySelect = 0x0401;

    private readonly nint windowHandle;
    private readonly Action activateWindow;
    private readonly WindowProcedure windowProcedure;
    private readonly nint previousWindowProcedure;
    private readonly nint previousSmallIcon;
    private readonly nint previousLargeIcon;
    private readonly uint taskbarCreatedMessage;
    private nint iconHandle;
    private nint largeIconHandle;
    private bool isAdded;
    private bool isDisposed;

    public TrayIcon(nint windowHandle, string iconPath, Action activateWindow)
    {
        this.windowHandle = windowHandle;
        this.activateWindow = activateWindow;
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
            if (notification is LeftButtonUp
                or LeftButtonDoubleClick
                or RightButtonUp
                or NotifyIconSelect
                or NotifyIconKeySelect)
            {
                activateWindow();
                return 0;
            }
        }

        return CallWindowProc(previousWindowProcedure, window, message, wParam, lParam);
    }

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

        if (iconHandle != 0)
        {
            DestroyIcon(iconHandle);
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
}
