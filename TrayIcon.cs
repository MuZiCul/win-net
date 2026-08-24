using System.Runtime.InteropServices;
using System.Text;

namespace WinNetFix;

/// <summary>
/// 纯 P/Invoke 系统托盘（不依赖 WinForms / System.Drawing），支持图标、气泡、原生菜单、消息循环。
/// 用于替代 NotifyIcon + ContextMenuStrip + Application.Run。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ---- 菜单命令 ID（TrayApp 使用）----
    public const int CmdAutoStart = 1;
    public const int CmdShowStatus = 2;
    public const int CmdUninstall = 3;
    public const int CmdExit = 4;
    public const int CmdAutoRepair = 5;
    public const int CmdAutoWifi = 6;
    public const int CmdRestartAdapter = 7;
    public const int CmdDisableEnable = 8;
    public const int CmdFixDns = 9;
    public const int CmdOpenLogDir = 10;
    public const int CmdGithubFix = 11;
    public const int CmdGithubRestore = 12;
    public const int CmdAbout = 13;

    // ---- Shell_NotifyIcon ----
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;

    // ---- 窗口消息 ----
    private const uint WM_TRAYICON = 0x8000; // WM_APP
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;

    // ---- 菜单 ----
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;

    // ---- 窗口类 ----
    private const string ClassName = "WinNetFixTrayWindow";
    private const uint WS_OVERLAPPED = 0x00000000;
    private const int CW_USEDEFAULT = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string szExeFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private readonly Logger _log;
    private readonly IntPtr _hIcon;
    private readonly IntPtr _hwnd;
    private readonly WndProcDelegate _wndProcDelegate; // 保持引用防止 GC 回收
    private bool _disposed;
    private string _tip = "WinNetFix 网络自愈";

    /// <summary>左键双击托盘图标（显示状态）。</summary>
    public event Action? Activated;

    /// <summary>右键菜单命令（TrayApp 处理）。</summary>
    public event Action<int>? Command;

    /// <summary>菜单弹出前查询"开机自启"勾选状态。</summary>
    public Func<bool>? AutoStartCheckedQuery;

    /// <summary>菜单弹出前查询"自动执行修复"勾选状态。</summary>
    public Func<bool>? AutoRepairCheckedQuery;

    /// <summary>菜单弹出前查询"自动连接 WiFi"勾选状态。</summary>
    public Func<bool>? AutoWifiCheckedQuery;

    /// <summary>创建托盘图标并加入系统托盘。</summary>
    public TrayIcon(Logger log)
    {
        _log = log;

        // 注册隐藏窗口类（接收托盘回调消息）
        _wndProcDelegate = WndProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProcDelegate,
            lpszClassName = ClassName,
            hInstance = GetModuleHandle(null),
        };
        if (RegisterClass(ref wc) == 0)
            throw new InvalidOperationException("RegisterClass 失败");
        _hwnd = CreateWindowEx(0, ClassName, "WinNetFixTray", WS_OVERLAPPED,
            0, 0, CW_USEDEFAULT, CW_USEDEFAULT, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        // 从自身 exe 提取图标（ApplicationIcon 已编译进 exe）
        _hIcon = LoadExeIcon();
        if (_hIcon == IntPtr.Zero)
            throw new InvalidOperationException("无法从 exe 提取图标");

        // 加入托盘
        var data = BuildData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
            throw new InvalidOperationException("Shell_NotifyIcon 添加失败");
    }

    /// <summary>运行消息循环（阻塞，直到 Exit() 或收到 WM_QUIT）。</summary>
    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>退出消息循环并移除托盘图标。</summary>
    public void Exit()
    {
        RemoveIcon();
        if (_hwnd != IntPtr.Zero)
        {
            PostQuitMessage(0);
            DestroyWindow(_hwnd);
        }
    }

    /// <summary>显示气泡通知。</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            var data = BuildData();
            data.uFlags = NIF_INFO;
            data.szInfo = text;
            data.szInfoTitle = title;
            data.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            _log.Debug($"[Tray] 气泡失败: {ex.Message}");
        }
    }

    /// <summary>更新悬浮提示文字。</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        var data = BuildData();
        data.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA BuildData()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = _tip,
        };
        return data;
    }

    /// <summary>从当前 exe 提取应用图标（ApplicationIcon 编译资源）。</summary>
    private IntPtr LoadExeIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            var n = ExtractIconEx(exe, 0, out var large, out var small, 1);
            if (n == 0) return IntPtr.Zero;
            if (small != IntPtr.Zero) DestroyIcon(small); // 只保留大图标
            return large;
        }
        catch (Exception ex)
        {
            _log.Debug($"[Tray] 提取图标失败: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                {
                    var l = (uint)(long)lParam;
                    if (l == WM_LBUTTONDBLCLK || l == WM_LBUTTONUP)
                    {
                        Activated?.Invoke();
                    }
                    else if (l == WM_RBUTTONUP)
                    {
                        ShowMenu();
                    }
                    return IntPtr.Zero;
                }
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>弹出原生右键菜单（TPM_RETURNCMD 直接返回选中的命令 ID）。</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            // 勾选态：开机自启 / 自动执行修复 / 自动连接 WiFi
            var flag = (AutoStartCheckedQuery?.Invoke() ?? false) ? MF_CHECKED : MF_UNCHECKED;
            AppendMenu(menu, MF_STRING | flag, new IntPtr(CmdAutoStart), "开机自启");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            flag = (AutoRepairCheckedQuery?.Invoke() ?? false) ? MF_CHECKED : MF_UNCHECKED;
            AppendMenu(menu, MF_STRING | flag, new IntPtr(CmdAutoRepair), "自动执行修复");
            flag = (AutoWifiCheckedQuery?.Invoke() ?? false) ? MF_CHECKED : MF_UNCHECKED;
            AppendMenu(menu, MF_STRING | flag, new IntPtr(CmdAutoWifi), "自动连接 WiFi");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, new IntPtr(CmdRestartAdapter), "重启网卡");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdDisableEnable), "禁用并恢复网卡");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdFixDns), "修复 DNS");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdOpenLogDir), "打开日志目录");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, new IntPtr(CmdGithubFix), "修复 GitHub 连接");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdGithubRestore), "还原 GitHub hosts");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, new IntPtr(CmdShowStatus), "显示状态");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdAbout), "关于");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdUninstall), "卸载");
            AppendMenu(menu, MF_STRING, new IntPtr(CmdExit), "退出");

            // TrackPopupMenu 需要窗口置前，否则菜单不响应键盘/不自动消失
            SetForegroundWindow(_hwnd);
            var pt = new POINT();
            GetCursorPos(out pt);
            var cmd = TrackPopupMenu(menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (cmd > 0)
                Command?.Invoke(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void RemoveIcon()
    {
        try
        {
            var data = BuildData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}

/// <summary>纯 P/Invoke 消息框（替代 WinForms MessageBox）。</summary>
public static class NativeBox
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const int IDOK = 1;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Info(string text, string caption = "WinNetFix")
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONINFORMATION);

    public static void Warning(string text, string caption = "WinNetFix")
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONWARNING);

    public static void Error(string text, string caption = "WinNetFix")
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR);

    public static bool YesNo(string text, string caption = "WinNetFix")
        => MessageBoxW(IntPtr.Zero, text, caption, MB_YESNO | MB_ICONQUESTION) == IDYES;
}
