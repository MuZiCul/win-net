using System.Text.Json;

namespace WinNetFix;

/// <summary>配置文件模型。对应 docs/design.md 第 2.3 节。</summary>
public sealed class Config
{
    public ProbeConfig Probe { get; set; } = new();
    public RepairConfig Repair { get; set; } = new();
    public LogConfig Log { get; set; } = new();

    public sealed class ProbeConfig
    {
        public int IntervalSec { get; set; } = 15;
        public int FailThreshold { get; set; } = 2;
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>网关 ICMP（不依赖 DNS，判定本地链路）。默认 true。</summary>
        public bool GatewayIcmp { get; set; } = true;

        /// <summary>外网 ICMP 目标（不依赖 DNS，判定出口）。</summary>
        public string PublicIcmpTarget { get; set; } = "8.8.8.8";

        /// <summary>域名 HTTP 目标（依赖 DNS，判定解析）。</summary>
        public string DnsHttpUrl { get; set; } = "https://www.msftconnecttest.com/connecttest.txt";

        /// <summary>HTTP 探测期望内容片段。</summary>
        public string DnsHttpExpected { get; set; } = "Microsoft Connect Test";
    }

    public sealed class RepairConfig
    {
        public bool RestartAdapter { get; set; } = true;

        /// <summary>DNS 层故障时先刷新缓存。</summary>
        public bool FlushDns { get; set; } = true;

        /// <summary>flush 无效则临时切换公共 DNS（仅以太网 IPv4，不改回）。</summary>
        public bool FallbackPublicDns { get; set; } = true;

        /// <summary>公共 DNS 主/备地址。</summary>
        public string[] PublicDns { get; set; } = { "8.8.8.8", "223.5.5.5" };

        public bool ReconnectWifi { get; set; } = true;

        /// <summary>WiFi 连接失败后的冷却期（秒），冷却期内不再尝试。</summary>
        public int WifiRetryCooldownSec { get; set; } = 60;

        /// <summary>空 = 使用断连前最后连接的 SSID。</summary>
        public string PreferredSsid { get; set; } = "";

        /// <summary>适配器匹配：auto 或名称正则。</summary>
        public string AdapterMatch { get; set; } = "auto";

        public int MaxRetry { get; set; } = 3;

        /// <summary>退避时长（秒），索引按重试次数取。</summary>
        public int[] BackoffSec { get; set; } = { 5, 15, 30 };

        /// <summary>AppFault（应用层不可达）静默期（秒）：期间内重复出现仅记 Debug，避免日志刷屏。</summary>
        public int AppFaultSilenceSec { get; set; } = 600;

        /// <summary>重启网卡后等待链路恢复的时长（秒）。</summary>
        public int RecoverWaitSec { get; set; } = 8;

        /// <summary>DNS 修复（flush/切DNS）后等待的时长（秒）。</summary>
        public int DnsWaitSec { get; set; } = 3;
    }

    public sealed class LogConfig
    {
        public string Level { get; set; } = "Info";
        public string Path { get; set; } = @"%ProgramData%\WinNetFix\logs\";
        public int RetentionDays { get; set; } = 30;
    }

    /// <summary>加载配置。若文件不存在则写入默认配置；解析失败则抛异常。</summary>
    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var def = new Config();
            File.WriteAllText(path, def.ToJson());
            return def;
        }

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)
                  ?? throw new InvalidDataException($"配置文件解析失败: {path}");
        return cfg;
    }

    public string ToJson() => JsonSerializer.Serialize(this, ConfigJsonContext.Default.Config);
}
