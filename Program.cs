using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WinNetFix;

internal static class Program
{
    private const string MutexName = @"Global\WinNetFix_SingleInstance";
    public const string TaskName = "WinNetFix";
    private static Logger? _log;

    /// <summary>日志目录：固定为 exe 所在目录下的 logs（不支持自定义）。</summary>
    public static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>是否以管理员权限运行。</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 注册代码页 provider：支持 GBK/936（hosts 中文注释、netsh 中文输出解码）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0");
            return 0;
        }

        // 解析 --config 路径
        string? configPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
            {
                configPath = args[i + 1];
                break;
            }
        }
        configPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinNetFix", "config.json");

        try
        {
            var config = Config.Load(configPath);
            _log = new Logger(
                Program.LogDir,
                config.Log.RetentionDays,
                Logger.ParseLevel(config.Log.Level));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"配置加载失败: {ex.Message}");
            return 2;
        }

        var first = args.Length > 0 ? args[0] : "--run";
        switch (first)
        {
            case "--install":
            case "--uninstall":
            case "--once":
            case "--run":
            case "--wifi-on":
            case "--wifi-off":
            case "--github-fix":
            case "--github-restore":
            default:
                // 需要管理员权限的模式：非管理员时自动 UAC 提权重启
                if (!IsAdministrator() && TryElevateSelf(args))
                    return 0; // 提权成功，旧进程退出
                WarnIfNotAdmin(); // 提权被拒/不可行，回退普通权限运行
                break;
            case "--status":
                // 只读探测，无需提权
                break;
        }

        switch (first)
        {
            case "--install":
                return InstallTask();
            case "--uninstall":
                return UninstallTask();
            case "--status":
                return ShowStatus(configPath);
            case "--once":
                return RunOnce(configPath);
            case "--wifi-on":
                return SetWifiRadio(on: true);
            case "--wifi-off":
                return SetWifiRadio(on: false);
            case "--github-fix":
                return GithubFix();
            case "--github-restore":
                return GithubRestore();
            case "--run":
            default:
                return RunBackground(configPath);
        }
    }

    /// <summary>修复 GitHub 连接：先诊断（输出污染判定），再 DoH 解析真实 IP 写入 hosts。</summary>
    private static int GithubFix()
    {
        _log!.Info("[Github] 开始修复 GitHub 连接");
        Console.WriteLine("正在诊断 GitHub 连接...");

        // 先诊断：TCP 可达性 + DNS 污染比对
        var diag = GithubHosts.Diagnose(_log!).GetAwaiter().GetResult();
        Console.WriteLine($"  TCP443(本机IP): {(diag.GithubReachable ? "可达" : "不可达")}");
        Console.WriteLine($"  本机解析: {string.Join(", ", diag.LocalIps)}");
        Console.WriteLine($"  DoH真值: {string.Join(", ", diag.TrueIps)}");
        Console.WriteLine($"  判定: {(diag.DnsPolluted ? "DNS 已污染 → 需要修复" : "未检测到污染 → 尝试修复")}");

        Console.WriteLine("正在修复（DoH 解析真实 IP → TCP443 测速 → 写 hosts）...");
        var (ok, msg) = GithubHosts.Fix(_log!).GetAwaiter().GetResult();
        Console.WriteLine(ok ? "OK" : "失败");
        Console.WriteLine(msg);
        _log.Info($"[Github] 修复结果: {(ok ? "OK" : "失败")} {msg}");
        return ok ? 0 : 1;
    }

    /// <summary>还原 hosts：移除 WinNetFix GitHub 修复条目。</summary>
    private static int GithubRestore()
    {
        _log!.Info("[Github] 开始还原 hosts");
        var (ok, msg) = GithubHosts.Restore(_log!);
        Console.WriteLine(msg);
        return ok ? 0 : 1;
    }

    /// <summary>手动打开/关闭 WiFi 软件开关（供测试与后续功能预留）。</summary>
    private static int SetWifiRadio(bool on)
    {
        var iface = GetWirelessInterfaceName();
        if (string.IsNullOrEmpty(iface))
        {
            Console.WriteLine("未找到无线网卡接口名");
            return 1;
        }

        var ok = WlanApi.SetRadioState(iface, on);
        Console.WriteLine($"{(on ? "打开" : "关闭")} WiFi 开关({iface}): {(ok ? "成功" : "失败")}");
        _log!.Info($"[Manual] {(on ? "打开" : "关闭")} WiFi 开关({iface}): {(ok ? "成功" : "失败")}");
        return ok ? 0 : 1;
    }

    /// <summary>获取无线接口名（netsh wlan show interfaces 的 Name 字段）。</summary>
    private static string? GetWirelessInterfaceName()
    {
        var r = ProcessRunner.RunNetsh("wlan show interfaces");
        var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"(?im)^\s*(Name|名称)\s*:\s*(.+)$");
        return m.Success ? m.Groups[2].Value.Trim() : null;
    }

    /// <summary>非管理员时记 Warn 并提示（重启网卡/开 WiFi 等操作会失败）。</summary>
    private static void WarnIfNotAdmin()
    {
        if (IsAdministrator()) return;
        const string msg = "当前非管理员运行：重启网卡、打开 WiFi 等修复操作将因权限不足失败。请以管理员身份运行（建议用 --install 注册计划任务或右键管理员运行）。";
        _log!.Warn(msg);
        Console.WriteLine(msg);
    }

    /// <summary>
    /// 尝试以管理员身份重新启动当前进程（触发 UAC 弹窗）。
    /// 返回 true 表示已成功拉起提权进程，当前进程应退出。
    /// </summary>
    private static bool TryElevateSelf(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            // 若当前是 dotnet 宿主（开发调试 dotnet run），提权会拉起 dotnet 而非本程序，禁用自动提权
            var fileName = Path.GetFileName(exe);
            if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return false;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas", // 触发 UAC 提权
            };
            if (args.Length > 0)
                psi.Arguments = string.Join(' ', args.Select(a => $"\"{a}\""));

            using var p = System.Diagnostics.Process.Start(psi);
            _log!.Info("已触发 UAC 提权，正在等待用户确认，本进程退出");
            return p != null;
        }
        catch (Exception ex)
        {
            // UAC 被用户拒绝或不可用：回退普通权限
            _log!.Warn($"UAC 提权未成功（{ex.Message}），以普通权限继续");
            return false;
        }
    }

    /// <summary>常驻运行：隐藏窗口，单实例互斥，循环调度状态机。</summary>
    private static int RunBackground(string configPath)
    {
        // 单实例
        var createdNew = false;
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            _log!.Info("已有实例在运行，退出（E002）");
            return 0;
        }

        // 隐藏控制台窗口（后台模式），改用系统托盘
        HideConsoleWindow();

        var config = Config.Load(configPath);
        _log!.Info($"=== WinNetFix 启动（托盘模式，interval={config.Probe.IntervalSec}s, failThreshold={config.Probe.FailThreshold}）===");

        try
        {
            // 托盘图标 + 后台状态机线程；Application.Run 阻塞直到托盘"退出"
            using var tray = new TrayApp(config, _log!);
            tray.Run();
            return 0;
        }
        catch (Exception ex)
        {
            _log.Error($"[Program] 托盘模式异常: {ex}");
            return 1;
        }
    }

    /// <summary>执行一次完整探测+修复流程后退出（调试/排障用）。</summary>
    private static int RunOnce(string configPath)
    {
        var config = Config.Load(configPath);
        var machine = new StateMachine(config, _log!, verbose: true);
        machine.Tick();
        Console.WriteLine($"\n最终状态: {machine.State}");
        Console.WriteLine($"上次修复: {machine.LastFixTime:yyyy-MM-dd HH:mm:ss}");
        return 0;
    }

    /// <summary>打印当前状态与最近探测信息。</summary>
    private static int ShowStatus(string configPath)
    {
        var config = Config.Load(configPath);
        var probe = new ProbeLayer(config);
        var r = probe.Probe();
        Console.WriteLine($"适配器 Up: {r.AdapterUp}  名称: {r.AdapterName ?? "-"}  网关: {r.GatewayIp ?? "-"}  本机IP: {r.AdapterIp ?? "-"}");
        Console.WriteLine($"网关ICMP: {(r.GatewayOk ? "OK" : "FAIL")}  外网ICMP: {(r.PublicOk ? "OK" : "FAIL")}");
        Console.WriteLine($"DNS解析: {(r.DnsOk ? "OK" : "FAIL")}  应用HTTP: {(r.AppOk ? "OK" : "FAIL")}");
        Console.WriteLine($"判定: {(r.AllOk ? "正常" : r.LinkFault ? "链路层故障" : r.DnsFault ? "DNS层故障" : r.AppFault ? "应用层故障(非本工具可修)" : "未知")}");

        // 省电状态（需管理员；非管理员查询会失败）
        if (r.AdapterName != null)
        {
            var enabled = AdapterManager.IsPowerSavingEnabled(r.AdapterName, _log!);
            Console.WriteLine(enabled == null
                ? "网卡省电挂起: 未知（查询需管理员权限，请用 --run 或管理员运行查看）"
                : enabled == true
                    ? "网卡省电挂起: 开启（危险，工具启动时会自动关闭）"
                    : "网卡省电挂起: 已关闭（安全）");
        }
        return 0;
    }

    /// <summary>注册开机自启计划任务（登录时运行，最高权限）。</summary>
    private static int InstallTask()
        => InstallTaskInternal(Environment.ProcessPath ?? "", _log!) ? 0 : 1;

    /// <summary>注册开机自启计划任务（供命令行与托盘复用）。成功返回 true。</summary>
    public static bool InstallTaskInternal(string exe, Logger log)
    {
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("无法获取当前程序路径");
        log.Info($"[Install] 注册计划任务 {TaskName}: {exe} --run");

        var tr = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --run\" /SC ONLOGON /RL HIGHEST /F";
        var r = ProcessRunner.Run("schtasks.exe", tr);
        if (!r.Ok)
        {
            log.Error($"[Install] 注册失败({r.ExitCode}): {r.StdErr}");
            return false;
        }

        Console.WriteLine("已注册开机自启计划任务 WinNetFix（登录时运行，最高权限）");
        log.Info("[Install] 注册成功");
        return true;
    }

    private static int UninstallTask()
        => UninstallTaskInternal(_log!) ? 0 : 1;

    /// <summary>注销开机自启计划任务（供命令行与托盘复用）。成功返回 true。</summary>
    public static bool UninstallTaskInternal(Logger log)
    {
        log.Info($"[Uninstall] 注销计划任务 {TaskName}");
        var r = ProcessRunner.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        if (!r.Ok)
        {
            log.Error($"[Uninstall] 注销失败({r.ExitCode}): {r.StdErr}");
            return false;
        }
        Console.WriteLine("已注销计划任务 WinNetFix");
        log.Info("[Uninstall] 注销成功");
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    private static void HideConsoleWindow()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }
        catch
        {
            // 隐藏失败不影响运行
        }
    }
}
