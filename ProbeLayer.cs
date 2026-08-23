using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinNetFix;

/// <summary>分层探测结果。故障层判定见 ProbeLayer.Probe。</summary>
public sealed record ProbeResult(
    bool AdapterUp,
    bool HasValidIp,  // 是否有非 169.254 的合法 IPv4（判定 DHCP 是否拿到 IP）
    bool GatewayOk,
    bool PublicOk,
    bool DnsOk,      // DNS 解析成功（Dns.GetHostAddresses）
    bool AppOk,      // 应用层 HTTP 可达（附加信息，不参与 DNS 判定）
    string? AdapterName,
    string? GatewayIp,
    string? AdapterIp,  // 当前 IPv4 地址（169.254 即 DHCP 未就绪）
    int ProbeSeq)    // 探测序号，用于日志
{
    /// <summary>DHCP 故障：适配器 Up 但只有 169.254（APIPA），未拿到合法 IP。</summary>
    public bool DhcpFault => AdapterUp && !HasValidIp;

    /// <summary>链路层故障：适配器 Up 且有合法 IP，但网关或外网 ICMP 不通（不依赖 DNS）。</summary>
    public bool LinkFault => AdapterUp && HasValidIp && (!GatewayOk || !PublicOk);

    /// <summary>DNS 层故障：链路通但域名解析失败。</summary>
    public bool DnsFault => AdapterUp && HasValidIp && GatewayOk && PublicOk && !DnsOk;

    /// <summary>应用层故障：链路通、DNS 解析通、但 HTTP 不可达（SSL 拦截/防火墙封 443 等）。非本工具可修，不重启网卡。</summary>
    public bool AppFault => AdapterUp && HasValidIp && GatewayOk && PublicOk && DnsOk && !AppOk;

    /// <summary>网络栈健康（不含 AppOk：应用层被拦不代表网络栈故障）。</summary>
    public bool AllOk => AdapterUp && HasValidIp && GatewayOk && PublicOk && DnsOk;
}

/// <summary>仅针对以太网（有线）适配器的分层连通性探测。</summary>
public sealed class ProbeLayer
{
    private readonly Config _config;
    private readonly HttpClient _http;
    private int _seq;

    public ProbeLayer(Config config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(config.Probe.TimeoutMs) };
    }

    /// <summary>执行一次分层探测。</summary>
    public ProbeResult Probe()
    {
        var seq = ++_seq;

        // 1. 找到活动的以太网适配器（匹配逻辑与 AdapterManager 一致）
        var adapter = AdapterManager.FindEthernetAdapter(_config.Repair.AdapterMatch);
        bool adapterUp = adapter is { OperationalStatus: OperationalStatus.Up };

        // 2. 检查适配器 IP：是否有非 169.254 的合法 IPv4（判定 DHCP 是否完成）
        string? adapterIp = null;
        bool hasValidIp = false;
        if (adapter != null && adapterUp)
        {
            var ipv4 = adapter.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            adapterIp = ipv4.FirstOrDefault();
            hasValidIp = ipv4.Any(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal));
        }

        // 3. 网关 ICMP
        string? gw = null;
        bool gwOk = false;
        if (_config.Probe.GatewayIcmp)
        {
            gw = GetDefaultGateway();
            if (gw != null)
                gwOk = PingOk(gw, _config.Probe.TimeoutMs);
        }
        else
        {
            gwOk = true; // 未启用网关探测时不判链路
        }

        // 4. 外网 ICMP
        bool publicOk = _config.Probe.PublicIcmpTarget.Length > 0
                        && PingOk(_config.Probe.PublicIcmpTarget, _config.Probe.TimeoutMs);

        // 5. DNS 解析（直接解析目标域名，不依赖 HTTP）
        bool dnsOk = DnsResolveOk(_config.Probe.DnsHttpUrl);

        // 6. 应用层 HTTP（附加探测，仅诊断，不参与 DNS 判定）
        bool appOk = HttpOk();

        return new ProbeResult(adapterUp, hasValidIp, gwOk, publicOk, dnsOk, appOk,
            adapter?.Name, gw, adapterIp, seq);
    }

    /// <summary>从 URL 提取主机名并解析，成功即视为 DNS 层健康。</summary>
    private bool DnsResolveOk(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            var host = new Uri(url).Host;
            var addrs = Dns.GetHostAddresses(host);
            return addrs != null && addrs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool PingOk(string host, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(host, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private bool HttpOk()
    {
        try
        {
            var url = _config.Probe.DnsHttpUrl;
            if (string.IsNullOrWhiteSpace(url)) return true;
            using var cts = new CancellationTokenSource(_config.Probe.TimeoutMs);
            var resp = _http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return false;
            var body = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            var expected = _config.Probe.DnsHttpExpected;
            return string.IsNullOrWhiteSpace(expected) || body.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取默认网关 IPv4 地址（首个活动的以太网适配器）。</summary>
    public static string? GetDefaultGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
            if (IsVirtual(ni.Description)) continue;

            foreach (var gw in ni.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    return gw.Address.ToString();
            }
        }
        return null;
    }

    /// <summary>排除虚拟/隧道/回环适配器。</summary>
    public static bool IsVirtual(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return true;
        desc = desc.ToLowerInvariant();
        return desc.Contains("virtual") || desc.Contains("loopback") || desc.Contains("tunnel")
            || desc.Contains("hyper-v") || desc.Contains("tap-") || desc.Contains("pseudo")
            || desc.Contains("bluetooth");
    }
}
