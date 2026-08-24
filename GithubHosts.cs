using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WinNetFix;

/// <summary>
/// GitHub 连接修复：诊断 DNS 污染，通过 DoH 获取真实 IP 写入 hosts 绕过污染。
/// 核心能力：DoH 多源解析、TCP 443 测速、hosts 标记段写入/备份/还原。
/// </summary>
public static class GithubHosts
{
    // ---- 需要修复的核心域名（覆盖网页/API/下载/素材）----
    private static readonly string[] Domains =
    {
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "codeload.github.com",
        "github.githubassets.com",
        "avatars.githubusercontent.com",
        "user-images.githubusercontent.com",
        "objects.githubusercontent.com",
        "github.global.ssl.fastly.net",
    };

    // ---- DoH 源（多源取交集/兜底，任一可达即可）----
    private static readonly string[] DohEndpoints =
    {
        "https://dns.alidns.com/resolve?name={0}&type=A",
        "https://223.5.5.5/resolve?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A",
    };

    private const string BeginTag = "# === WinNetFix GitHub Fix BEGIN ===";
    private const string EndTag = "# === WinNetFix GitHub Fix END ===";

    /// <summary>hosts 文件路径（%SystemRoot%\System32\drivers\etc\hosts）。</summary>
    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");

    /// <summary>首次写入前的原始 hosts 备份。</summary>
    public static string BackupPath => HostsPath + ".winnetfix.bak";

    /// <summary>诊断结果。</summary>
    public sealed record DiagnoseResult(
        bool GithubReachable,   // github.com TCP 443 是否可达
        bool DnsPolluted,       // 本机 DNS 解析 IP 与 DoH 真值是否不一致（污染）
        string Detail,
        IReadOnlyList<string> LocalIps,  // 本机 DNS 解析结果
        IReadOnlyList<string> TrueIps);  // DoH 解析结果（可信）

    // ==================== 诊断 ====================

    /// <summary>诊断 GitHub 连通性：TCP 可达性 + DNS 污染比对。</summary>
    public static async Task<DiagnoseResult> Diagnose(Logger log)
    {
        var local = ResolveLocal("github.com");
        var (trueIps, viaDoh) = await ResolveDoh("github.com", log);
        var reachable = await Tcp443Reachable(local.FirstOrDefault(), 5000);

        var polluted = viaDoh && trueIps.Count > 0
                       && local.Count > 0
                       && !local.Intersect(trueIps, StringComparer.OrdinalIgnoreCase).Any();

        var detail = $"TCP443(本机IP): {(reachable ? "可达" : "不可达")}, 本机解析: {Join(local)}, DoH真值: {Join(trueIps)}";
        if (viaDoh && trueIps.Count == 0)
            detail += "（DoH 源均不可达，无法判定污染）";

        log.Info($"[GithubHosts] 诊断: {detail}, polluted={polluted}");
        return new DiagnoseResult(reachable, polluted, detail, local, trueIps);
    }

    // ==================== 修复 ====================

    /// <summary>
    /// 修复：并行 DoH 解析各核心域名 → TCP 443 测速选最优 IP → 写入 hosts。返回成功与消息。
    /// <paramref name="progress"/> 每解析/测速完一个域名回调一次实时进度（文本+颜色，可空）。
    /// </summary>
    public static async Task<(bool Ok, string Msg)> Fix(Logger log, Action<string, ProgressColor>? progress = null)
    {
        progress?.Invoke($"开始修复，共 {Domains.Length} 个域名（并行解析+测速）…", ProgressColor.Info);
        var tasks = Domains.Select(d => ResolveOne(d, log, progress)).ToArray();
        var results = await Task.WhenAll(tasks);

        var entries = results.Where(r => r.Ip != null)
                             .Select(r => (r.Domain, Ip: r.Ip!, Ms: r.Ms))
                             .OrderBy(e => e.Domain, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        var failed = results.Where(r => r.Failed != null)
                            .Select(r => r.Failed!)
                            .ToList();

        if (entries.Count == 0)
        {
            progress?.Invoke("全部域名无可达 IP，未修改 hosts", ProgressColor.Fail);
            return (false, "所有 GitHub 域名均无法获得可达 IP，未修改 hosts");
        }

        // 写入 hosts
        progress?.Invoke($"[写入] 共 {entries.Count} 个域名 → hosts", ProgressColor.Info);
        var write = WriteHosts(entries, log);
        if (!write.Ok)
        {
            progress?.Invoke($"写入失败：{write.Msg}", ProgressColor.Fail);
            return (false, write.Msg);
        }

        // 清除 DNS 客户端缓存，确保 hosts 立即生效
        progress?.Invoke("刷新 DNS 缓存（flushdns）…", ProgressColor.Info);
        DnsManager.FlushDns(log);

        var msg = $"已写入 {entries.Count} 个域名到 hosts：\n" +
                  string.Join("\n", entries.Select(e => $"  {e.Ip}  {e.Domain}"));
        if (failed.Count > 0)
            msg += $"\n跳过(无可用IP): {string.Join(", ", failed)}";
        progress?.Invoke("完成", ProgressColor.Success);
        return (true, msg);
    }

    /// <summary>还原：删除 WinNetFix 标记段（不影响用户其他 hosts 内容）。</summary>
    public static (bool Ok, string Msg) Restore(Logger log, Action<string, ProgressColor>? progress = null)
    {
        try
        {
            if (!File.Exists(HostsPath))
            {
                log.Info("[GithubHosts] hosts 不存在，无需还原");
                progress?.Invoke("hosts 文件不存在，无需还原", ProgressColor.Default);
                return (true, "hosts 文件不存在，无需还原");
            }

            progress?.Invoke("读取 hosts…", ProgressColor.Info);
            var (lines, enc) = ReadHostsWithEncoding();
            var inBlock = false;
            var foundBegin = false;   // 存在 BeginTag（即使无 EndTag 也要清理）
            var foundEnd = false;
            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (line.Trim().Equals(BeginTag, StringComparison.Ordinal)) { inBlock = true; foundBegin = true; continue; }
                if (line.Trim().Equals(EndTag, StringComparison.Ordinal)) { inBlock = false; foundEnd = true; continue; }
                if (!inBlock) kept.Add(line);
            }

            if (!foundBegin && !foundEnd)
            {
                log.Info("[GithubHosts] hosts 中没有 WinNetFix 标记段");
                progress?.Invoke("hosts 中没有 WinNetFix 修复条目，无需还原", ProgressColor.Default);
                return (true, "hosts 中没有 WinNetFix 修复条目，无需还原");
            }

            progress?.Invoke("移除 WinNetFix GitHub 条目…", ProgressColor.Info);
            File.WriteAllLines(HostsPath, kept, enc);
            var restored = foundBegin ? "已还原 hosts（移除 WinNetFix 修复条目）" : "已清理 hosts 中残留的 WinNetFix 标记";
            log.Info($"[GithubHosts] {restored} (begin={foundBegin}, end={foundEnd})");
            progress?.Invoke("还原完成", ProgressColor.Success);
            return (true, restored);
        }
        catch (Exception ex)
        {
            log.Error($"[GithubHosts] 还原失败: {ex.Message}");
            progress?.Invoke($"还原失败: {ex.Message}", ProgressColor.Fail);
            return (false, $"还原失败: {ex.Message}");
        }
    }

    // ==================== hosts 写入 ====================

    /// <summary>
    /// 检测 hosts 文件编码：UTF-8 BOM → UTF8；无 BOM 且可严格 UTF-8 解码 → UTF8(无BOM)；
    /// 否则回退系统 ANSI（中文系统 GBK/936）。保证中文注释不被破坏。
    /// </summary>
    private static Encoding DetectHostsEncoding()
    {
        try
        {
            var bytes = File.ReadAllBytes(HostsPath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(true); // 有 BOM 的 UTF-8

            // 无 BOM：尝试严格 UTF-8 解码，遇非法字节说明是 ANSI
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch
        {
            // ANSI/GBK（CodePages provider 已注册）
            return Encoding.GetEncoding(0);
        }
    }

    /// <summary>读取 hosts 行并返回检测到的编码（写入时复用，保证不破坏原编码）。</summary>
    private static (string[] Lines, Encoding Enc) ReadHostsWithEncoding()
    {
        var enc = DetectHostsEncoding();
        var lines = File.ReadAllLines(HostsPath, enc);
        return (lines, enc);
    }

    private static (bool Ok, string Msg) WriteHosts(List<(string Domain, string Ip, long Ms)> entries, Logger log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HostsPath)!);

            // 首次写入前备份原始 hosts
            if (!File.Exists(BackupPath) && File.Exists(HostsPath))
            {
                File.Copy(HostsPath, BackupPath, overwrite: false);
                log.Info($"[GithubHosts] 已备份原始 hosts → {BackupPath}");
            }

            Encoding enc;
            List<string> lines;
            if (File.Exists(HostsPath))
            {
                var (arr, detected) = ReadHostsWithEncoding();
                lines = arr.ToList();
                enc = detected;
            }
            else
            {
                lines = new List<string>();
                enc = new UTF8Encoding(false);
            }

            // 移除旧标记段（幂等重写）
            var inBlock = false;
            lines.RemoveAll(l =>
            {
                if (l.Trim().Equals(BeginTag, StringComparison.Ordinal)) { inBlock = true; return true; }
                if (l.Trim().Equals(EndTag, StringComparison.Ordinal)) { inBlock = false; return true; }
                return inBlock;
            });

            lines.Add(BeginTag);
            foreach (var (domain, ip, _) in entries)
                lines.Add($"{ip}  {domain}");
            lines.Add(EndTag);

            File.WriteAllLines(HostsPath, lines, enc);
            log.Info($"[GithubHosts] 已写入 {entries.Count} 条 hosts 条目 (enc={enc.EncodingName})");
            return (true, "ok");
        }
        catch (Exception ex)
        {
            log.Error($"[GithubHosts] 写入 hosts 失败: {ex.Message}");
            return (false, $"写入 hosts 失败: {ex.Message}");
        }
    }

    // ==================== DoH 解析 ====================

    /// <summary>单个域名的解析+测速结果（Ip 为空 = 解析/测速失败，见 Failed）。</summary>
    private sealed record OneResult(string Domain, string? Ip, long Ms, string? Failed);

    /// <summary>单个域名：DoH 多源解析 → TCP 443 测速选最优。每阶段回调实时进度。</summary>
    private static async Task<OneResult> ResolveOne(string domain, Logger log, Action<string, ProgressColor>? progress)
    {
        progress?.Invoke($"[解析] {domain}", ProgressColor.Default);
        var (ips, viaDoh) = await ResolveDoh(domain, log);
        if (!viaDoh || ips.Count == 0)
        {
            progress?.Invoke($"  ↳ 失败：DoH 全部源不可达，无真实 IP", ProgressColor.Fail);
            log.Info($"[GithubHosts] {domain}: DoH 源全部不可达");
            return new OneResult(domain, null, 0, $"{domain}(无DoH真值)");
        }

        progress?.Invoke($"  ↳ DoH 候选: {string.Join(", ", ips)}", ProgressColor.Info);
        var best = await PickFastest(ips, domain, log);
        if (best == null)
        {
            progress?.Invoke($"  ↳ 失败：所有候选 IP TCP443 不可达", ProgressColor.Fail);
            log.Info($"[GithubHosts] {domain}: 所有候选 IP TCP443 不可达");
            return new OneResult(domain, null, 0, $"{domain}(无可达IP)");
        }

        progress?.Invoke($"  ↳ 选定 {best.Value.Ip}（{best.Value.Ms}ms）", ProgressColor.Success);
        log.Info($"[GithubHosts] {domain} → {best.Value.Ip} ({best.Value.Ms}ms)");
        return new OneResult(domain, best.Value.Ip, best.Value.Ms, null);
    }

    /// <summary>从多个 DoH 源解析域名 A 记录，返回真实 IP 列表。viaDoh=false 表示全部源不可达。</summary>
    private static async Task<(List<string> Ips, bool ViaDoh)> ResolveDoh(string domain, Logger log)
    {
        var ips = new List<string>();
        var sawAny = false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                var url = string.Format(endpoint, Uri.EscapeDataString(domain));
                var json = await http.GetStringAsync(url);
                sawAny = true;
                var parsed = ParseDohJson(json);
                ips.AddRange(parsed.Where(ip => !ips.Contains(ip)));
                if (ips.Count >= 3) break; // 每个域名最多取 3 个候选，够测速了
            }
            catch (Exception ex)
            {
                log.Debug($"[GithubHosts] DoH 源失败 {endpoint} ({domain}): {ex.Message}");
            }
        }
        return (ips, sawAny);
    }

    /// <summary>解析 DoH JSON 响应中的 A 记录（兼容 alidns/google 格式）。</summary>
    private static List<string> ParseDohJson(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Answer", out var answer) && answer.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in answer.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetInt32() != 1) continue; // 仅 A 记录
                    if (item.TryGetProperty("data", out var data))
                    {
                        var ip = data.GetString();
                        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                            result.Add(ip);
                    }
                }
            }
        }
        catch
        {
            // 解析失败忽略（当作该源无结果）
        }
        return result;
    }

    // ==================== 本机解析 / TCP 测速 ====================

    /// <summary>本机 DNS 解析（观察当前系统解析结果，判断污染）。</summary>
    private static List<string> ResolveLocal(string domain)
    {
        try
        {
            var addrs = Dns.GetHostAddresses(domain);
            return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>对域名的候选 IP 做 TCP 443 测速，返回最快可达项；全部失败返回 null。</summary>
    private static async Task<(string Domain, string Ip, long Ms)?> PickFastest(List<string> ips, string domain, Logger log)
    {
        (string Ip, long Ms)? best = null;
        foreach (var ip in ips)
        {
            var ms = await Tcp443Measure(ip, 4000);
            if (ms < 0) { log.Debug($"[GithubHosts] {domain} {ip} TCP443 不可达"); continue; }
            if (best == null || ms < best.Value.Ms)
                best = (ip, ms);
        }
        return best == null ? null : (domain, best.Value.Ip, best.Value.Ms);
    }

    /// <summary>TCP 443 连通性（毫秒）。不可达返回 -1。</summary>
    private static async Task<long> Tcp443Measure(string ip, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(ip), 443, cts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>指定 IP 的 TCP 443 可达性（bool）。</summary>
    private static async Task<bool> Tcp443Reachable(string? ip, int timeoutMs)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        return await Tcp443Measure(ip, timeoutMs) >= 0;
    }

    private static string Join(IReadOnlyList<string> list)
        => list.Count == 0 ? "-" : string.Join(",", list);
}
