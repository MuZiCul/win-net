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
}
