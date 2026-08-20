namespace WinNetFix;

/// <summary>DNS 修复：flushdns 刷新缓存；必要时切换以太网 IPv4 到公共 DNS（不改回）。</summary>
public static class DnsManager
{
    /// <summary>刷新 DNS 缓存。成功返回 true。</summary>
    public static bool FlushDns(Logger log)
    {
        log.Info("[DnsManager] 执行 ipconfig /flushdns");
        var r = ProcessRunner.Run("ipconfig.exe", "/flushdns");
        if (!r.Ok)
        {
            log.Warn($"[DnsManager] flushdns 失败({r.ExitCode}): {r.StdErr}");
            return false;
        }
        return true;
    }

    /// <summary>切换以太网适配器 IPv4 DNS 为公共 DNS（主+备）。成功返回 true。</summary>
    public static bool SetPublicDns(string adapterName, string[] dnsList, Logger log)
    {
        if (dnsList == null || dnsList.Length == 0) return false;
        var primary = dnsList[0];

        log.Info($"[DnsManager] 切换以太网({adapterName}) DNS → {string.Join(", ", dnsList)}（不改回）");

        // 记录原配置（日志留痕，不用于还原）
        var before = ProcessRunner.RunNetsh($"interface ip show dns name=\"{adapterName}\"");
        log.Debug($"[DnsManager] 原 DNS 配置: {before.StdOut}");

        var set = ProcessRunner.RunNetsh($"interface ip set dns name=\"{adapterName}\" static {primary}");
        if (!set.Ok)
        {
            log.Error($"[DnsManager] 设置主 DNS 失败({set.ExitCode}): {set.StdErr}");
            return false;
        }

        // 备用 DNS（index=2）
        if (dnsList.Length > 1)
        {
            for (int i = 1; i < dnsList.Length; i++)
            {
                var add = ProcessRunner.RunNetsh($"interface ip add dns name=\"{adapterName}\" {dnsList[i]} index={i + 1}");
                if (!add.Ok)
                    log.Warn($"[DnsManager] 添加备用 DNS {dnsList[i]} 失败({add.ExitCode}): {add.StdErr}");
            }
        }

        return true;
    }
}
