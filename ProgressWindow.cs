using System.Runtime.InteropServices;
using System.Text;

namespace WinNetFix;

/// <summary>进度文本行颜色。</summary>
public enum ProgressColor
{
    Default = 0,
    Info = 1,
    Success = 2,
    Fail = 3,
}

/// <summary>
/// 纯 P/Invoke 实时进度窗口：只读 RichEdit 多行富文本，支持按行着色，用于实时显示修复过程。
/// 由主线程创建（消息循环复用托盘窗口的 GetMessage 循环）；后台线程通过 AppendLine 跨线程追加文本。
/// 关闭本窗口只销毁自身，不退出托盘消息循环。
/// </summary>
public sealed class ProgressWindow : IDisposable
{
    private const string ClassName = "WinNetFixProgressWindow";
    private const string EditClass = "RICHEDIT50W";

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_READONLY = 0x0800;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint WS_EX_CLIENTEDGE = 0x00000200;
    private const int SW_SHOW = 5;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_GETICON = 0x007F;
    private const uint EM_SETSEL = 0x00B1;
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint ICON_SMALL = 0;
    private const uint ICON_BIG = 1;

    // RichEdit 富文本：EM_SETCHARFORMAT（WM_USER+68=0x444）、SCF_SELECTION=1、CFM_COLOR=0x40000000
    private const uint EM_SETCHARFORMAT = 0x0444;
    private const uint SCF_SELECTION = 0x0001;
    private const uint CFM_COLOR = 0x40000000;

    // COLORREF 颜色（0x00BBGGRR）
    private const int ColorDefault = 0x00000000; // 黑
    private const int ColorInfo = 0x00FF0000;    // 蓝
    private const int ColorSuccess = 0x00008000; // 绿
    private const int ColorFail = 0x000000FF;    // 红

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

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHARFORMAT2W
    {
        public uint cbSize;
        public uint dwMask;
        public uint dwEffects;
        public int yHeight;
        public int yOffset;
        public int crTextColor;
        public byte bCharSet;
        public byte bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szFaceName;
        public ushort wWeight;
        public ushort sSpacing;
        public int crBackColor;
        public int lcid;
        public uint dwReserved;
        public short sStyle;
        public short wKerning;
        public byte bUnderlineType;
        public byte bAnimation;
        public byte bRevAuthor;
        public byte bUnderlineColor;
    }

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
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string szExeFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const int DEFAULT_GUI_FONT = 17;

    private readonly WndProcDelegate _wndProc; // 保持引用防止 GC 回收
    private IntPtr _hwnd;
    private IntPtr _hEdit;
    private IntPtr _hIcon;
    private bool _disposed;

    /// <summary>创建并显示进度窗口。</summary>
    public ProgressWindow(string title)
    {
        // RichEdit 控件位于 Msftedit.dll，必须先加载（否则类不可用）
        LoadLibrary("Msftedit.dll");

        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            lpszClassName = ClassName,
            hInstance = hInstance,
        };
        RegisterClass(ref wc); // 重复注册返回 0，可忽略（类已存在）

        _hwnd = CreateWindowEx(0, ClassName, title, WS_OVERLAPPEDWINDOW,
            120, 120, 560, 400, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建进度窗口失败");

        _hEdit = CreateWindowEx(WS_EX_CLIENTEDGE, EditClass, "",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | WS_VSCROLL | ES_AUTOVSCROLL,
            0, 0, 560, 400, _hwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        var font = GetStockObject(DEFAULT_GUI_FONT);
        if (font != IntPtr.Zero)
            SendMessage(_hEdit, WM_SETFONT, font, new IntPtr(1));

        // 窗口标题栏/任务栏显示彩色应用图标（从 exe 提取）
        _hIcon = LoadExeIcon();
        if (_hIcon != IntPtr.Zero)
        {
            SendMessage(_hwnd, WM_SETICON, new IntPtr(ICON_SMALL), _hIcon);
            SendMessage(_hwnd, WM_SETICON, new IntPtr(ICON_BIG), _hIcon);
        }

        ShowWindow(_hwnd, SW_SHOW);
    }

    /// <summary>从当前 exe 提取应用图标（ApplicationIcon 编译资源，彩色多尺寸）。</summary>
    private static IntPtr LoadExeIcon()
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
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>更新窗口标题（可跨线程调用，用于标注运行中/已完成）。窗口已关闭时忽略。</summary>
    public void SetTitle(string title)
    {
        if (_hwnd == IntPtr.Zero) return;
        try { SetWindowText(_hwnd, title); } catch { }
    }

    /// <summary>追加一行文本（默认颜色）。可跨线程调用，窗口已关闭时忽略。</summary>
    public void AppendLine(string text) => AppendLine(text, ProgressColor.Default);

    /// <summary>追加一行文本并指定颜色。可跨线程调用，窗口已关闭时忽略。</summary>
    public void AppendLine(string text, ProgressColor color)
    {
        if (_hEdit == IntPtr.Zero) return;
        try
        {
            // 光标移到末尾
            SendMessage(_hEdit, EM_SETSEL, new IntPtr(-1), new IntPtr(-1));
            // 设置当前插入点字符格式（颜色）
            var cf = new CHARFORMAT2W
            {
                cbSize = (uint)Marshal.SizeOf<CHARFORMAT2W>(),
                dwMask = CFM_COLOR,
                crTextColor = ColorToRef(color),
            };
            var pcf = Marshal.AllocHGlobal(Marshal.SizeOf<CHARFORMAT2W>());
            try
            {
                Marshal.StructureToPtr(cf, pcf, false);
                SendMessage(_hEdit, EM_SETCHARFORMAT, new IntPtr(SCF_SELECTION), pcf);
            }
            finally
            {
                Marshal.FreeHGlobal(pcf);
            }
            SendMessage(_hEdit, EM_REPLACESEL, IntPtr.Zero, SymbolFor(color) + text + "\r\n");
        }
        catch
        {
            // 窗口关闭中忽略
        }
    }

    /// <summary>按行颜色返回彩色符号前缀（继承当前行颜色）。</summary>
    private static string SymbolFor(ProgressColor c) => c switch
    {
        ProgressColor.Success => "✔ ",
        ProgressColor.Fail => "✘ ",
        ProgressColor.Info => "► ",
        _ => "",
    };

    /// <summary>ProgressColor → COLORREF（0x00BBGGRR）。</summary>
    private static int ColorToRef(ProgressColor c) => c switch
    {
        ProgressColor.Info => ColorInfo,
        ProgressColor.Success => ColorSuccess,
        ProgressColor.Fail => ColorFail,
        _ => ColorDefault,
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                // 只清理句柄；不 PostQuitMessage（进度窗口关闭不应退出托盘消息循环）
                _hwnd = IntPtr.Zero;
                _hEdit = IntPtr.Zero;
                // 用户手动关窗时立即释放图标（Dispose 有判零保护，不会重复释放）
                if (_hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }
                return IntPtr.Zero;
            case WM_GETICON:
                // 确保任务栏/缩略图也显示应用图标
                return _hIcon != IntPtr.Zero ? _hIcon : DefWindowProc(hWnd, msg, wParam, lParam);
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _hEdit = IntPtr.Zero;
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
