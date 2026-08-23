using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace WinNetFix;

/// <summary>
/// 系统托盘：右下角图标 + 菜单（开机自启开关 / 卸载 / 退出）。
/// 开机自启开关勾选/取消时自动执行计划任务注册/移除。
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly Logger _log;
    private readonly string _exePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private StateMachine? _machine;

    public event Action? ExitRequested;

    public TrayApp(Config config, Logger log)
    {
        _log = log;
        _exePath = Environment.ProcessPath ?? "WinNetFix.exe";

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WinNetFix 网络自愈",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        _autoStartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = true,
            Checked = IsAutoStartRegistered(),
        };
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("卸载", null, (_, _) => Uninstall()));
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Exit()));

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowStatus();

        // 后台运行状态机
        _machine = new StateMachine(config, log);
        _workerTask = Task.Run(() => RunWorker(config));
    }

    /// <summary>常驻主线程：阻塞直到退出（由调用方 Application.Run 驱动，或本类阻塞）。</summary>
    public void Run()
    {
        // 用 Application.Run 提供消息泵（NotifyIcon 需要）
        Application.Run();
    }

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

    /// <summary>从内嵌 PNG 资源加载图标（缩放到 32x32 后经 GetHicon 转 Icon）。失败回退系统图标。</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var bmp = new System.Drawing.Bitmap(stream);
                    using var small = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(32, 32));
                    var hIcon = small.GetHicon();
                    try
                    {
                        return Icon.FromHandle(hIcon);
                    }
                    catch
                    {
                        // FromHandle 失败时释放 hIcon 避免泄漏
                        NativeMethods.DestroyIcon(hIcon);
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"图标加载失败: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
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
            if (_autoStartItem.Checked)
            {
                var ok = Program.InstallTaskInternal(_exePath, _log);
                _autoStartItem.Checked = ok;
                if (ok) _log.Info("[Tray] 已注册开机自启");
                else MessageBox.Show("注册开机自启失败（需要管理员权限）", "WinNetFix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                var ok = Program.UninstallTaskInternal(_log);
                _autoStartItem.Checked = !ok;
                if (ok) _log.Info("[Tray] 已移除开机自启");
                else MessageBox.Show("移除开机自启失败", "WinNetFix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Tray] 切换自启异常: {ex}");
            MessageBox.Show($"操作失败: {ex.Message}", "WinNetFix", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>卸载：移除自启 + 调用卸载程序（安装版 unins000.exe；便携版提示手动删除）。</summary>
    private void Uninstall()
    {
        if (MessageBox.Show("确定卸载 WinNetFix 吗？\n将移除开机自启并退出。", "卸载 WinNetFix",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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
            _icon.Visible = false;
            try { Process.Start(new ProcessStartInfo { FileName = unins, UseShellExecute = true }); } catch { }
            Exit();
        }
        else
        {
            // 便携版：仅移除自启 + 提示手动删除
            MessageBox.Show("已移除开机自启。\n便携版无需卸载程序，删除程序文件夹即可。", "WinNetFix",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                _icon.ShowBalloonTip(3000, "WinNetFix", "正在运行中...", ToolTipIcon.Info);
                return;
            }
            var r = _machine.LastProbe;
            var state = r.AllOk ? "正常" : r.DhcpFault ? "DHCP故障(169.254)" : r.LinkFault ? "链路故障" : r.DnsFault ? "DNS故障" : "异常";
            _icon.ShowBalloonTip(3000, "WinNetFix", $"状态: {state}\nIP: {r.AdapterIp ?? "-"}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Debug($"[Tray] 状态气泡失败: {ex.Message}");
        }
    }

    private void Exit()
    {
        _log.Info("[Tray] 用户选择退出");
        _cts.Cancel();
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
        ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _icon.Dispose();
        _cts.Dispose();
    }
}
