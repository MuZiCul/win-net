using System.Text.RegularExpressions;

namespace WinNetFix;

/// <summary>
/// WiFi 例行守护：每个监测周期调用，未连接（含 Software Off）则主动打开无线并连接已存 SSID；已连接则不触碰。
/// 连接失败进入冷却期，避免反复尝试。
/// </summary>
public sealed class WlanManager
{
    private readonly Logger _log;
    private readonly int _retryCooldownSec;
    private string _lastSeenSsid = "";
    private DateTime _lastAttemptTime = DateTime.MinValue;

    public WlanManager(Logger log, int retryCooldownSec = 60)
    {
        _log = log;
        _retryCooldownSec = retryCooldownSec;
    }

    /// <summary>WiFi 状态检查结果。</summary>
    public sealed record WifiStatus(bool Connected, bool SoftwareOff, bool HardwareOff, string Ssid, bool HasWlan);

    /// <summary>检查 WiFi 状态：已连接 / 软件关 / 硬件关 / 无网卡。</summary>
    public WifiStatus CheckWifi()
    {
        var r = ProcessRunner.RunNetsh("wlan show interfaces");
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
        {
            // netsh wlan 不可用 / 无无线服务
            return new WifiStatus(false, false, false, "", false);
        }

        var t = r.StdOut;
        if (t.Contains("There is no wireless interface", StringComparison.OrdinalIgnoreCase)
            || t.Contains("is not running", StringComparison.OrdinalIgnoreCase))
        {
            // 无无线接口或 wlansvc 服务未运行
            return new WifiStatus(false, false, false, "", false);
        }

        // 兼容中英文标签：中文系统 netsh 输出为 "状态 : 已连接" / "名称 : WLAN" 等
        bool connected = Regex.IsMatch(t, @"(?im)^\s*(State|状态)\s*:\s*(connected|已连接)");
        string ssid = "";
        var mSsid = Regex.Match(t, @"(?im)^\s*(SSID|SSID)\s*:\s*(.+)$");
        if (mSsid.Success) ssid = mSsid.Groups[2].Value.Trim();
        if (!string.IsNullOrEmpty(ssid)) _lastSeenSsid = ssid;

        // Radio status 解析（中英文）：
        // 英文: "Radio status : Hardware On\n Software Off"
        // 中文: "无线电状态 : 硬件 开\n 软件 关"
        bool hwOff = Regex.IsMatch(t, @"(?im)^\s*Radio status\s*:\s*Hardware Off")
                  || Regex.IsMatch(t, @"(?im)^\s*无线电状态\s*:\s*硬件\s*关")
                  || Regex.IsMatch(t, @"(?im)^\s*Hardware Off")
                  || Regex.IsMatch(t, @"(?im)^\s*硬件\s*关");
        bool swOff = Regex.IsMatch(t, @"(?im)^\s*(Radio status\s*:\s*)?Software Off")
                  || Regex.IsMatch(t, @"(?im)^\s*无线电状态\s*:\s*软件\s*关")
                  || Regex.IsMatch(t, @"(?im)^\s*Software Off")
                  || Regex.IsMatch(t, @"(?im)^\s*软件\s*关");

        // 明确记录 WiFi 开关状态（连接状态、硬件开关、软件开关）
        _log.Debug($"[WlanManager] WiFi 开关检测: connected={connected}, ssid={ssid ?? "-"}, swOff={swOff}, hwOff={hwOff}");
        return new WifiStatus(connected, swOff, hwOff, ssid ?? "", true);
    }

    /// <summary>
    /// 例行确保 WiFi 在线。已连接 → 跳过；未连接 → 打开无线开关并连接。
    /// 失败进入冷却期（_retryCooldownSec 秒内不再尝试）。返回当前是否在线。
    /// </summary>
    public bool EnsureWifiUp(string preferredSsid)
    {
        var st = CheckWifi();
        if (!st.HasWlan)
        {
            _log.Debug("[WlanManager] 无无线网卡/服务，跳过");
            return false;
        }

        // 明确记录 WiFi 开关状态（Info 级别，便于诊断）
        _log.Info($"[WlanManager] WiFi 开关状态: connected={st.Connected}, ssid={st.Ssid}, swOff={st.SoftwareOff}, hwOff={st.HardwareOff}");

        if (st.Connected)
        {
            _log.Info($"[WlanManager] WiFi 已连接({st.Ssid})，无需操作");
            return true;
        }

        // 冷却期内不再尝试
        if ((DateTime.Now - _lastAttemptTime).TotalSeconds < _retryCooldownSec)
        {
            _log.Debug("[WlanManager] WiFi 未连接，处于冷却期，跳过本次尝试");
            return false;
        }
        _lastAttemptTime = DateTime.Now;

        _log.Info($"[WlanManager] WiFi 未连接(swOff={st.SoftwareOff}, hwOff={st.HardwareOff})，主动打开并连接");

        // 1. 硬件关闭（飞行模式物理键）无法用软件开启，仅记录
        if (st.HardwareOff)
        {
            _log.Warn("[WlanManager] 无线硬件已关闭（飞行模式），无法自动打开，请手动开启");
            return false;
        }

        // 2. 软件关闭 → 打开无线
        var iface = GetWirelessInterfaceName();
        if (string.IsNullOrEmpty(iface))
        {
            _log.Warn("[WlanManager] 无法获取无线接口名");
            return false;
        }

        if (st.SoftwareOff)
        {
            _log.Info($"[WlanManager] 打开无线开关(radio): {iface}");

            // 2a. 优先用 WLAN API 直接打开 radio 软开关（netsh 无法控制 Software Off）
            var apiOk = WlanApi.SetRadioState(iface, on: true);
            if (!apiOk)
            {
                _log.Warn("[WlanManager] WLAN API 打开 radio 失败，尝试 netsh 回退");
            }
            Thread.Sleep(3000);

            // 2b. 若 API 失败，netsh 回退：enable 接口 + 开 autoconfig
            if (!apiOk)
            {
                var en1 = ProcessRunner.RunNetsh($"interface set interface name=\"{iface}\" admin=enable");
                if (!en1.Ok)
                {
                    if (IsInterfaceAdminEnabled(iface))
                        _log.Debug($"[WlanManager] 接口 {iface} 已是 Enabled（admin=enable 返回1属正常）");
                    else
                    {
                        _log.Warn($"[WlanManager] 启用接口失败(exit={en1.ExitCode}): {en1.StdErr}（可能需要管理员权限）");
                        return false;
                    }
                }
                Thread.Sleep(5000);

                var ac = ProcessRunner.RunNetsh($"wlan set autoconfig enabled=yes interface=\"{iface}\"");
                if (!ac.Ok)
                    _log.Warn($"[WlanManager] 开启 WLAN AutoConfig 失败(exit={ac.ExitCode}): {ac.StdErr}");
                Thread.Sleep(3000);
            }

            // 2c. 复检开关是否真的打开（Software Off 是否消除）
            var afterOpen = CheckWifi();
            _log.Info($"[WlanManager] 打开开关后复检: swOff={afterOpen.SoftwareOff}, hwOff={afterOpen.HardwareOff}, connected={afterOpen.Connected}");

            // 2d. 关键：开关打不开就不再尝试连接（radio 关着连不上，别白费功夫）
            if (afterOpen.SoftwareOff)
            {
                _log.Warn("[WlanManager] 无线软件开关无法自动打开（可能被驱动/组策略锁定），跳过连接。请手动打开 WiFi 开关");
                return false;
            }
        }
        else
        {
            // 未 Software Off 但 disconnected，仍确保接口 enable（忽略失败，接口可能已启用）
            ProcessRunner.RunNetsh($"interface set interface name=\"{iface}\" admin=enable");
        }

        // 3. 确定目标 SSID：preferredSsid → 最近连接 → 首个 profile
        var target = string.IsNullOrWhiteSpace(preferredSsid) ? _lastSeenSsid : preferredSsid;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = GetFirstProfile();
            if (!string.IsNullOrEmpty(target))
                _log.Info($"[WlanManager] 使用首个 profile 作为目标: {target}");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            _log.Warn("[WlanManager] 无可用 SSID/profile，无法连接 WiFi");
            return false;
        }

        // 4. 尝试连接（最多重试 2 次）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // 第 2 次重试前：disable→enable 重置 radio（首次已 enable 则跳过）
            if (attempt == 2)
            {
                _log.Info($"[WlanManager] 重试前重置无线接口: {iface}");
                ProcessRunner.RunNetsh($"interface set interface name=\"{iface}\" admin=disable");
                Thread.Sleep(3000);
                ProcessRunner.RunNetsh($"interface set interface name=\"{iface}\" admin=enable");
                Thread.Sleep(5000);
            }

            _log.Info($"[WlanManager] 尝试连接 WiFi: {target}（第{attempt}次）");
            var connect = ProcessRunner.RunNetsh($"wlan connect name=\"{target}\"");
            if (!connect.Ok)
                _log.Debug($"[WlanManager] connect 返回 exit={connect.ExitCode}: {connect.StdErr}");

            // 等待 10s 校验
            Thread.Sleep(10000);
            var after = CheckWifi();
            if (after.Connected)
            {
                _log.Info($"[WlanManager] WiFi 连接成功: {after.Ssid}");
                return true;
            }
        }

        _log.Warn($"[WlanManager] WiFi 连接失败（重试2次后仍断开）");
        return false;
    }

    /// <summary>检查接口 Admin State 是否为 Enabled（中英文兼容）。</summary>
    private bool IsInterfaceAdminEnabled(string iface)
    {
        var r = ProcessRunner.RunNetsh("interface show interface");
        if (!r.Ok) return false;

        // 英文表头: "Admin State    State    Type    Interface Name"
        // 中文表头: "管理员状态    状态     类型    接口名称"
        foreach (var line in r.StdOut.Split('\n'))
        {
            if (!line.Contains(iface, StringComparison.OrdinalIgnoreCase)) continue;
            return Regex.IsMatch(line, @"(?i)(Enabled|已启用|已启用)");
        }
        return false;
    }

    private string? GetWirelessInterfaceName()
    {
        // 无线关闭时 wlan show interfaces 可能不列出接口，回退到 netsh interface show interface
        var r = ProcessRunner.RunNetsh("wlan show interfaces");
        var m = Regex.Match(r.StdOut, @"(?im)^\s*(Name|名称)\s*:\s*(.+)$");
        if (m.Success) return m.Groups[2].Value.Trim();

        var r2 = ProcessRunner.RunNetsh("interface show interface");
        var m2 = Regex.Match(r2.StdOut, @"(?im)^\s*(WLAN|Wi-Fi|WiFi|无线局域网连接)\b");
        if (m2.Success) return m2.Value.Trim();

        return null;
    }

    private string GetFirstProfile()
    {
        var r = ProcessRunner.RunNetsh("wlan show profiles");
        var m = Regex.Match(r.StdOut, @"(?im)^\s*(All User Profile|所有用户配置文件)\s*:\s*(.+)$");
        return m.Success ? m.Groups[2].Value.Trim() : "";
    }
}
