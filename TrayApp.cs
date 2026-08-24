using System.Diagnostics;

namespace WinNetFix;

/// <summary>
/// 系统托盘：右下角图标 + 原生菜单（开机自启开关 / 显示状态 / 卸载 / 退出）。
/// 基于纯 P/Invoke 的 TrayIcon，不依赖 WinForms。
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly Logger _log;
    private readonly string _exePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private StateMachine? _machine;
    private bool _exitStarted;
    private ProgressWindow? _githubWin; // 持有引用防 GC，窗口由用户手动关闭

    public event Action? ExitRequested;

    public TrayApp(Config config, Logger log)
    {
        _log = log;
        _exePath = Environment.ProcessPath ?? "WinNetFix.exe";

        _tray = new TrayIcon(log)
        {
            AutoStartCheckedQuery = IsAutoStartRegistered,
            AutoRepairCheckedQuery = () => _machine?.IsAutoRepairEnabled ?? true,
            AutoWifiCheckedQuery = () => _machine?.IsAutoConnectWifi ?? true,
        };
        _tray.Activated += ShowStatus;
        _tray.Command += OnMenuCommand;

        // 后台运行状态机
        _machine = new StateMachine(config, log);
        _workerTask = Task.Run(() => RunWorker(config));
    }

    /// <summary>常驻：阻塞在消息循环直到退出。</summary>
    public void Run() => _tray.Run();

    private void RunWorker(Config config)
    {
        _log.Info("=== WinNetFix 启动（托盘模式）===");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                _machine!.Tick();
                Thread.Sleep(TimeSpan.FromSeconds(config.Probe.IntervalSec));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[TrayApp] 状态机线程异常: {ex}");
        }
    }

    private void OnMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case TrayIcon.CmdAutoStart:
                ToggleAutoStart();
                break;
            case TrayIcon.CmdAutoRepair:
                ToggleAutoRepair();
                break;
            case TrayIcon.CmdAutoWifi:
                ToggleAutoWifi();
                break;
            case TrayIcon.CmdRestartAdapter:
                RunManualAction("重启网卡", p => { var ok = _machine!.ManualRestartAdapter(p); return (ok, ok ? "网卡已重启" : "重启失败，详见日志"); });
                break;
            case TrayIcon.CmdDisableEnable:
                if (NativeBox.YesNo("确定禁用并恢复有线网卡吗？\n将执行设备级禁用→启用（重载驱动），期间会短暂断网。", "WinNetFix"))
                    RunManualAction("禁用并恢复网卡", p => { var ok = _machine!.ManualDisableEnableAdapter(p); return (ok, ok ? "网卡已禁用并恢复" : "操作失败，详见日志"); });
                break;
            case TrayIcon.CmdFixDns:
                RunManualAction("修复 DNS", p => { var ok = _machine!.ManualFixDns(p); return (ok, ok ? "DNS 已恢复正常" : "DNS 修复未完全恢复，详见日志"); });
                break;
            case TrayIcon.CmdOpenLogDir:
                OpenLogDir();
                break;
            case TrayIcon.CmdGithubFix:
                RunGithubFix();
                break;
            case TrayIcon.CmdGithubRestore:
                RunManualAction("还原 GitHub hosts", p => GithubHosts.Restore(_log, p));
                break;
            case TrayIcon.CmdShowStatus:
                ShowStatus();
                break;
            case TrayIcon.CmdAbout:
                OpenAbout();
                break;
            case TrayIcon.CmdUninstall:
                Uninstall();
                break;
            case TrayIcon.CmdExit:
                Exit();
                break;
        }
    }

    /// <summary>切换"自动执行修复"开关（只写状态机标志，不持久化）。</summary>
    private void ToggleAutoRepair()
    {
        if (_machine == null) return;
        _machine.IsAutoRepairEnabled = !_machine.IsAutoRepairEnabled;
        _log.Info($"[Tray] 自动执行修复开关 → {(_machine.IsAutoRepairEnabled ? "开" : "关")}");
    }

    /// <summary>切换"自动连接 WiFi"开关（只写状态机标志，不持久化）。</summary>
    private void ToggleAutoWifi()
    {
        if (_machine == null) return;
        _machine.IsAutoConnectWifi = !_machine.IsAutoConnectWifi;
        _log.Info($"[Tray] 自动连接 WiFi 开关 → {(_machine.IsAutoConnectWifi ? "开" : "关")}");
    }

    /// <summary>
    /// 后台执行手动操作：弹出实时进度窗口显示过程，完成后弹详情框 + 气泡。
    /// 进度回调（文本+颜色）写入窗口；操作可返回详情消息。
    /// </summary>
    private void RunManualAction(string label, Func<Action<string, ProgressColor>?, (bool Ok, string Msg)> action)
    {
        _tray.ShowBalloon("WinNetFix", $"正在{label}…");
        _githubWin?.Dispose(); // 关闭上一个遗留窗口
        var win = new ProgressWindow($"WinNetFix - {label}");
        _githubWin = win; // 持有引用防 GC；窗口由用户手动关闭
        win.AppendLine($"开始{label}…");
        Task.Run(() =>
        {
            try
            {
                var (ok, msg) = action((t, c) => win.AppendLine(t, c));
                NativeBox.Info(msg, ok ? $"{label}完成" : $"{label}失败");
                _tray.ShowBalloon("WinNetFix", $"{label}: {(ok ? "完成" : "失败")}");
            }
            catch (Exception ex)
            {
                _log.Error($"[Tray] {label}异常: {ex}");
                win.AppendLine($"异常: {ex.Message}");
                NativeBox.Error($"{label}异常: {ex.Message}");
            }
        });
    }

    /// <summary>修复 GitHub 连接：弹出实时进度窗口显示全过程，完成后弹详情框。</summary>
    private void RunGithubFix()
    {
        _tray.ShowBalloon("WinNetFix", "正在修复 GitHub 连接…");
        _githubWin?.Dispose(); // 关闭上一个遗留窗口
        var win = new ProgressWindow("WinNetFix - 修复 GitHub 连接");
        _githubWin = win; // 持有引用防 GC；窗口由用户手动关闭
        Task.Run(async () =>
        {
            try
            {
                var (ok, msg) = await GithubHosts.Fix(_log, (t, c) => win.AppendLine(t, c));
                NativeBox.Info(msg, ok ? "GitHub 修复完成" : "GitHub 修复失败");
                _tray.ShowBalloon("WinNetFix", $"修复 GitHub 连接: {(ok ? "完成" : "失败")}");
            }
            catch (Exception ex)
            {
                _log.Error($"[Tray] 修复 GitHub 连接异常: {ex}");
                win.AppendLine($"异常: {ex.Message}");
                NativeBox.Error($"修复 GitHub 连接异常: {ex.Message}");
            }
        });
    }

    /// <summary>用默认浏览器打开项目主页。</summary>
    private void OpenAbout()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/MuZiCul/win-net",
                UseShellExecute = true,
            });
            _log.Info("[Tray] 打开关于页面");
        }
        catch (Exception ex)
        {
            _log.Error($"[Tray] 打开关于页面失败: {ex.Message}");
            NativeBox.Error($"打开关于页面失败: {ex.Message}");
        }
    }

    /// <summary>用资源管理器打开日志目录（exe 目录/logs）。</summary>
    private void OpenLogDir()
    {
        try
        {
            Directory.CreateDirectory(Program.LogDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Program.LogDir}\"",
                UseShellExecute = true,
            });
            _log.Info($"[Tray] 打开日志目录: {Program.LogDir}");
        }
        catch (Exception ex)
        {
            _log.Error($"[Tray] 打开日志目录失败: {ex.Message}");
            NativeBox.Error($"打开日志目录失败: {ex.Message}");
        }
    }

    /// <summary>开机自启是否已注册。</summary>
    private bool IsAutoStartRegistered()
    {
        var r = ProcessRunner.Run("schtasks.exe", $"/Query /TN \"{Program.TaskName}\"", timeoutMs: 10000);
        return r.Ok;
    }

    /// <summary>切换开机自启（勾选=注册，取消=移除）。</summary>
    private void ToggleAutoStart()
    {
        try
        {
            var wantOn = !IsAutoStartRegistered();
            if (wantOn)
            {
                var ok = Program.InstallTaskInternal(_exePath, _log);
                if (ok) _log.Info("[Tray] 已注册开机自启");
                else NativeBox.Warning("注册开机自启失败（需要管理员权限）");
            }
            else
            {
                var ok = Program.UninstallTaskInternal(_log);
                if (ok) _log.Info("[Tray] 已移除开机自启");
                else NativeBox.Warning("移除开机自启失败");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Tray] 切换自启异常: {ex}");
            NativeBox.Error($"操作失败: {ex.Message}");
        }
    }

    /// <summary>卸载：移除自启 + 调用卸载程序（安装版 unins000.exe；便携版提示手动删除）。</summary>
    private void Uninstall()
    {
        if (!NativeBox.YesNo("确定卸载 WinNetFix 吗？\n将移除开机自启并退出。", "卸载 WinNetFix"))
            return;

        _log.Info("[Tray] 用户选择卸载");
        // 移除自启
        try { Program.UninstallTaskInternal(_log); } catch { }

        // 查找卸载程序（安装版由 Inno Setup 生成）
        var dir = Path.GetDirectoryName(_exePath);
        var unins = dir != null ? Path.Combine(dir, "unins000.exe") : null;
        if (unins != null && File.Exists(unins))
        {
            _log.Info($"[Tray] 启动卸载程序: {unins}");
            _tray.Exit();
            try { Process.Start(new ProcessStartInfo { FileName = unins, UseShellExecute = true }); } catch { }
            Exit();
        }
        else
        {
            // 便携版：仅移除自启 + 提示手动删除
            NativeBox.Info("已移除开机自启。\n便携版无需卸载程序，删除程序文件夹即可。");
            Exit();
        }
    }

    /// <summary>显示当前网络状态（托盘气泡）。</summary>
    private void ShowStatus()
    {
        try
        {
            if (_machine?.LastProbe == null)
            {
                _tray.ShowBalloon("WinNetFix", "正在运行中...");
                return;
            }
            var r = _machine.LastProbe;
            var state = r.AllOk ? "正常" : r.DhcpFault ? "DHCP故障(169.254)" : r.LinkFault ? "链路故障" : r.DnsFault ? "DNS故障" : "异常";
            _tray.ShowBalloon("WinNetFix", $"状态: {state}\nIP: {r.AdapterIp ?? "-"}");
        }
        catch (Exception ex)
        {
            _log.Debug($"[Tray] 状态气泡失败: {ex.Message}");
        }
    }

    private void Exit()
    {
        if (_exitStarted) return;
        _exitStarted = true;
        _log.Info("[Tray] 用户选择退出");
        _cts.Cancel();
        _tray.Exit();
        ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _tray.Dispose();
        _cts.Dispose();
    }
}
