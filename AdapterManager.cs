using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace WinNetFix;

/// <summary>以太网适配器的识别与重启（禁用+启用）。仅处理物理有线网卡，排除虚拟/隧道/回环。</summary>
public static class AdapterManager
{
    /// <summary>
    /// 查找活动以太网适配器。
    /// </summary>
    /// <param name="match">"auto" 或名称正则。auto：取第一个 Up 的物理以太网；若均 Down 则取第一个物理以太网（供探测判定 Down 状态）。</param>
    public static NetworkInterface? FindEthernetAdapter(string match)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                         && !ProbeLayer.IsVirtual(ni.Description))
            .ToList();

        if (all.Count == 0) return null;

        if (!string.IsNullOrEmpty(match) && !match.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var rx = new Regex(match, RegexOptions.IgnoreCase);
            var matched = all.FirstOrDefault(ni => rx.IsMatch(ni.Name) || rx.IsMatch(ni.Description ?? ""));
            if (matched != null) return matched;
        }

        // 优先 Up，否则任意物理以太网
        return all.FirstOrDefault(ni => ni.OperationalStatus == OperationalStatus.Up) ?? all[0];
    }

    /// <summary>重启以太网适配器：禁用 → 等待 → 启用。成功返回 true。</summary>
    public static bool RestartAdapter(string adapterName, Logger log)
    {
        log.Info($"[AdapterManager] 重启以太网适配器: {adapterName}");

        // 主方案：PowerShell NetAdapter 模块
        var disableScript = $"Disable-NetAdapter -Name '{EscapePs(adapterName)}' -Confirm:$false";
        var disable = ProcessRunner.RunPowershell(disableScript);
        if (!disable.Ok)
        {
            log.Warn($"[AdapterManager] Disable-NetAdapter 失败({disable.ExitCode}): {disable.StdErr}");
            // 回退：netsh
            var netshDisable = ProcessRunner.RunNetsh($"interface set interface name=\"{adapterName}\" admin=disable");
            if (!netshDisable.Ok)
            {
                log.Error($"[AdapterManager] netsh 禁用失败({netshDisable.ExitCode}): {netshDisable.StdErr}");
                return false;
            }
        }

        Thread.Sleep(3000);

        // 启用
        var enableScript = $"Enable-NetAdapter -Name '{EscapePs(adapterName)}' -Confirm:$false";
        var enable = ProcessRunner.RunPowershell(enableScript);
        if (!enable.Ok)
        {
            log.Warn($"[AdapterManager] Enable-NetAdapter 失败({enable.ExitCode}): {enable.StdErr}");
            var netshEnable = ProcessRunner.RunNetsh($"interface set interface name=\"{adapterName}\" admin=enable");
            if (!netshEnable.Ok)
            {
                log.Error($"[AdapterManager] netsh 启用失败({netshEnable.ExitCode}): {netshEnable.StdErr}");
                return false;
            }
        }

        log.Info($"[AdapterManager] 以太网适配器已重启: {adapterName}");
        return true;
    }

    /// <summary>简单转义 PowerShell 单引号字符串。</summary>
    private static string EscapePs(string s) => s.Replace("'", "''");

    /// <summary>强制 DHCP 重新续租（ipconfig /release + /renew）。用于 169.254（DHCP 拿不到 IP）场景。</summary>
    public static bool RenewDhcp(string adapterName, Logger log)
    {
        log.Info($"[AdapterManager] 强制 DHCP 续租: {adapterName}");

        // release 后 renew（release 失败不影响 renew 尝试）
        var release = ProcessRunner.Run("ipconfig.exe", $"/release \"{adapterName}\"");
        if (!release.Ok)
            log.Debug($"[AdapterManager] ipconfig /release 返回 {release.ExitCode}: {release.StdErr}");

        Thread.Sleep(2000);

        var renew = ProcessRunner.Run("ipconfig.exe", $"/renew \"{adapterName}\"");
        if (!renew.Ok)
        {
            log.Warn($"[AdapterManager] ipconfig /renew 失败({renew.ExitCode}): {renew.StdErr}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 获取指定网卡的 PnP 设备 ID（InstanceId），并校验它是物理有线网卡（非无线/虚拟）。
    /// 通过 Get-NetAdapter 反查 PnPDeviceID + InterfaceDescription 双保险校验。
    /// 返回 null 表示不是有线网卡或获取失败。
    /// </summary>
    public static string? GetWiredPnpDeviceId(string adapterName, Logger log)
    {
        // 用 Get-NetAdapter 拿 PnPDeviceID 与 InterfaceDescription，同时校验
        var script = $"Get-NetAdapter -Name '{EscapePs(adapterName)}' -ErrorAction SilentlyContinue | " +
                     "Select-Object Name, InterfaceDescription, PnpDeviceID | ConvertTo-Json -Compress";
        var r = ProcessRunner.RunPowershell(script);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
        {
            log.Warn($"[AdapterManager] 获取网卡 PnP 信息失败({r.ExitCode}): {r.StdErr}");
            return null;
        }

        // 解析 JSON 输出。失败时直接返回 null（宁可不做 PnP 重启，也不冒"半解析+误匹配无线"的风险）
        string? desc = null, pnpId = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.StdOut);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                log.Warn($"[AdapterManager] PnP 信息不是 JSON 对象，跳过 PnP 重启（name={adapterName}）");
                return null;
            }
            if (doc.RootElement.TryGetProperty("InterfaceDescription", out var d)) desc = d.GetString();
            if (doc.RootElement.TryGetProperty("PnpDeviceID", out var p)) pnpId = p.GetString();
        }
        catch (Exception ex)
        {
            log.Warn($"[AdapterManager] PnP 信息 JSON 解析失败，跳过 PnP 重启（name={adapterName}）: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(pnpId) || string.IsNullOrWhiteSpace(desc))
        {
            log.Warn($"[AdapterManager] 无法获取网卡 PnPDeviceID（name={adapterName}, desc={desc}, pnp={pnpId}）");
            return null;
        }

        // 双保险：必须是物理有线（描述含 Ethernet/以太网，且不含 Wireless/WLAN/802.11）
        desc = desc.ToLowerInvariant();
        bool isWired = desc.Contains("ethernet") || desc.Contains("以太网");
        bool isWireless = desc.Contains("wireless") || desc.Contains("wlan")
                       || desc.Contains("802.11") || desc.Contains("wi-fi") || desc.Contains("wi-fi direct");
        if (!isWired || isWireless)
        {
            log.Warn($"[AdapterManager] PnP 设备不是物理有线网卡（{desc}），拒绝重启，防止误伤无线");
            return null;
        }

        log.Debug($"[AdapterManager] 有线网卡 PnPDeviceID: {pnpId} (desc={desc})");
        return pnpId;
    }

    /// <summary>
    /// 检查有线网卡"断开时进入睡眠/省电"是否开启（DeviceSleepOnDisconnect）。
    /// true=开启（危险，网线断开时网卡睡眠，插回可能不唤醒），false=已关闭。
    /// </summary>
    public static bool? IsPowerSavingEnabled(string adapterName, Logger log)
    {
        // 兼容新旧参数：优先读 DeviceSleepOnDisconnect（新），失败读 AllowComputerToTurnOffDevice（旧）
        var script = $@"
$pm = Get-NetAdapterPowerManagement -Name '{EscapePs(adapterName)}' -ErrorAction SilentlyContinue
if ($pm -ne $null) {{ if ($null -ne $pm.DeviceSleepOnDisconnect) {{ $pm.DeviceSleepOnDisconnect }} else {{ $pm.AllowComputerToTurnOffDevice }} }}";
        var r = ProcessRunner.RunPowershell(script);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
        {
            log.Debug($"[AdapterManager] 查询省电设置失败: {r.StdErr}");
            return null;
        }
        var text = r.StdOut.Trim();
        if (bool.TryParse(text, out var v)) return v;
        if (text == "1") return true;
        if (text == "0") return false;
        // PowerShell 枚举字符串
        if (text.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return false;
        // Unsupported/Unknown 视为无需处理
        log.Debug($"[AdapterManager] 省电设置解析: {text}");
        return null;
    }

    /// <summary>
    /// 关闭有线网卡省电挂起：DeviceSleepOnDisconnect（断开时睡眠）+ SelectiveSuspend（选择性挂起）。
    /// 防止"网线断开后网卡进入睡眠、插回不唤醒"导致必须重启电脑。成功返回 true。
    /// </summary>
    public static bool DisablePowerSaving(string adapterName, Logger log)
    {
        // 同时关闭两个省电开关（SelectiveSuspend 可能 Unsupported，不影响整体）
        var script = $@"
Set-NetAdapterPowerManagement -Name '{EscapePs(adapterName)}' -DeviceSleepOnDisconnect Disabled -ErrorAction SilentlyContinue
Set-NetAdapterPowerManagement -Name '{EscapePs(adapterName)}' -SelectiveSuspend Disabled -ErrorAction SilentlyContinue";
        var r = ProcessRunner.RunPowershell(script);
        if (!r.Ok)
        {
            log.Warn($"[AdapterManager] 关闭省电失败({r.ExitCode}): {r.StdErr}");
            return false;
        }
        log.Info($"[AdapterManager] 已关闭有线网卡省电挂起: {adapterName}");
        return true;
    }

    /// <summary>
    /// PnP 设备级重启（等效设备管理器"禁用/启用设备"，能重载驱动，解决"必须重启电脑"的网卡僵尸状态）。
    /// 主方案 pnputil /restart-device（Win10 1809+），回退 Disable/Enable-PnpDevice。
    /// 仅用于已知有线 PnPDeviceID。
    /// </summary>
    public static bool RestartAdapterPnp(string pnpDeviceId, Logger log)
    {
        log.Info($"[AdapterManager] PnP 设备级重启: {pnpDeviceId}");

        // 主方案：pnputil /restart-device（一条命令完成，Win10 1809+）
        var restart = ProcessRunner.Run("pnputil.exe", $"/restart-device \"{pnpDeviceId}\"", timeoutMs: 30000);
        if (restart.Ok)
        {
            log.Info($"[AdapterManager] pnputil 重启设备成功: {pnpDeviceId}");
            return true;
        }

        log.Warn($"[AdapterManager] pnputil 重启失败({restart.ExitCode}): {restart.StdErr}，回退 Disable/Enable-PnpDevice");

        // 回退：PowerShell 禁用+启用设备
        var disableScript = $"Disable-PnpDevice -InstanceId '{EscapePs(pnpDeviceId)}' -Confirm:$false -ErrorAction SilentlyContinue";
        var disable = ProcessRunner.RunPowershell(disableScript);
        if (!disable.Ok)
            log.Warn($"[AdapterManager] Disable-PnpDevice 返回非零({disable.ExitCode}): {disable.StdErr}");
        Thread.Sleep(3000);
        var enableScript = $"Enable-PnpDevice -InstanceId '{EscapePs(pnpDeviceId)}' -Confirm:$false -ErrorAction SilentlyContinue";
        var enable = ProcessRunner.RunPowershell(enableScript);

        if (!enable.Ok)
        {
            log.Error($"[AdapterManager] Enable-PnpDevice 失败({enable.ExitCode}): {enable.StdErr}");
            return false;
        }

        log.Info($"[AdapterManager] PnP 设备重启成功（PS 回退）: {pnpDeviceId}");
        return true;
    }
}
