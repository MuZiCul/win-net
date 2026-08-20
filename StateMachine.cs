namespace WinNetFix;

public enum FixState
{
    Monitoring,
    SuspectDown,
    LinkDown,
    DnsFault,
    FlushDns,
    SetPublicDns,
    RestartAdapter,
    RecoverWait,
    Healthy,
    Escalate,
}

/// <summary>
/// 核心决策状态机。对应 docs/design.md 第 4 节。
///
/// 简化说明（实现取向）：
/// - Monitoring：周期探测，分层判定故障类型。
/// - LinkDown：适配器 Down（物理拔线），不修复，等待链路恢复。
/// - SuspectDown：链路层连续失败达阈值 → RestartAdapter。
/// - RestartAdapter：重启以太网 → RecoverWait（等 8s）→ 重探。
/// - DnsFault：DNS 层连续失败达阈值 → FlushDns → 仍不通 → SetPublicDns → 仍不通 → Escalate。
/// - RecoverWait 后仍失败：重试（backoff）→ 达 maxRetry → EnsureWifiUp（WiFi 兜底）。
/// - Escalate：暂停退避最大时长后回 Monitoring，重置计数。
/// </summary>
public sealed class StateMachine
{
    private readonly Config _config;
    private readonly Logger _log;
    private readonly ProbeLayer _probe;
    private readonly WlanManager _wlan;
    private readonly bool _verbose;   // 是否打印每个状态转换（--once 调试用）

    private int _linkFailCount;      // 链路层连续失败计数
    private int _dnsFailCount;       // DNS 层连续失败计数
    private int _retryCount;         // 修复周期重试次数
    private DateTime _lastAppFaultWarn = DateTime.MinValue; // AppFault 静默期时间戳

    public FixState State { get; private set; } = FixState.Monitoring;
    public DateTime LastFixTime { get; private set; }
    public ProbeResult? LastProbe { get; private set; }

    public StateMachine(Config config, Logger log, bool verbose = false)
    {
        _config = config;
        _log = log;
        _probe = new ProbeLayer(config);
        _wlan = new WlanManager(log, config.Repair.WifiRetryCooldownSec);
        _verbose = verbose;
    }

    /// <summary>执行一次完整 tick（探测 + 按状态决策）。由外部循环调用。</summary>
    public void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            _log.Error($"[StateMachine] tick 异常: {ex}");
            State = FixState.Monitoring;
        }
    }

    private void TickCore()
    {
        switch (State)
        {
            case FixState.Monitoring:
            case FixState.Healthy:
                DoMonitoring();
                break;

            case FixState.Escalate:
                DoEscalate();
                break;

            case FixState.SuspectDown:
                // 进入修复周期
                _retryCount = 0;
                Transition(FixState.RestartAdapter);
                break;

            case FixState.RestartAdapter:
                DoRestartAdapter();
                break;

            case FixState.RecoverWait:
                // 等待已在转移处 sleep，此处直接重新探测
                DoRecoverCheck();
                break;

            case FixState.DnsFault:
                DoDnsFault();
                break;

            case FixState.FlushDns:
                DoFlushDns();
                break;

            case FixState.SetPublicDns:
                DoSetPublicDns();
                break;

            case FixState.LinkDown:
                DoLinkDown();
                break;

            default:
                State = FixState.Monitoring;
                break;
        }
    }

    /// <summary>正常监测：分层探测 + 例行 WiFi 守护，决定进入何状态。</summary>
    private void DoMonitoring()
    {
        // 例行 WiFi 守护：无论有线是否正常，WiFi 未连接（含 Software Off）都主动打开并连接
        EnsureWifiRoutine();

        var r = _probe.Probe();
        LastProbe = r;
        _log.Info($"[Probe] seq={r.ProbeSeq} adapterUp={r.AdapterUp} gw={r.GatewayIp} gwOk={r.GatewayOk} publicOk={r.PublicOk} dnsOk={r.DnsOk} appOk={r.AppOk}");

        if (!r.AdapterUp)
        {
            // 物理链路断开：不修复，仅记录。重置失败计数。
            if (State != FixState.LinkDown)
            {
                _linkFailCount = 0;
                _dnsFailCount = 0;
                Transition(FixState.LinkDown);
            }
            return;
        }

        // 适配器 Up
        if (State == FixState.LinkDown)
        {
            // 从 LinkDown 恢复：回到正常监测
            _log.Info("[StateMachine] 链路恢复 Up，回归 Monitoring");
            Transition(FixState.Monitoring);
            return;
        }

        if (r.LinkFault)
        {
            _dnsFailCount = 0;
            _linkFailCount++;
            _log.Debug($"[StateMachine] 链路层故障计数 {_linkFailCount}/{_config.Probe.FailThreshold}");
            if (_linkFailCount >= _config.Probe.FailThreshold)
            {
                _log.Warn("[StateMachine] 判定有线卡死，进入修复流程");
                Transition(FixState.SuspectDown);
            }
            return;
        }

        if (r.DnsFault)
        {
            _linkFailCount = 0;
            _dnsFailCount++;
            _log.Debug($"[StateMachine] DNS 层故障计数 {_dnsFailCount}/{_config.Probe.FailThreshold}");
            if (_dnsFailCount >= _config.Probe.FailThreshold)
            {
                _log.Warn("[StateMachine] 判定 DNS 故障，进入 DNS 修复流程");
                Transition(FixState.DnsFault);
            }
            return;
        }

        if (r.AppFault)
        {
            // 链路/DNS 均通但 HTTP 不可达（SSL 拦截/防火墙封 443 等）：非本工具可修。
            // 不重启网卡、不折腾 DNS。首次 Warn + 进 Escalate 观察一次；
            // 静默期内重复出现仅记 Debug 并保持 Monitoring，避免日志刷屏。
            _linkFailCount = 0;
            _dnsFailCount = 0;
            var silenceSec = _config.Repair.AppFaultSilenceSec;
            if ((DateTime.Now - _lastAppFaultWarn).TotalSeconds >= silenceSec)
            {
                _lastAppFaultWarn = DateTime.Now;
                _log.Warn("[StateMachine] 应用层不可达（链路/DNS 正常但 HTTP 失败），跳过修复，等待观察");
                Transition(FixState.Escalate);
            }
            else
            {
                _log.Debug("[StateMachine] 应用层仍不可达（静默期内，跳过）");
                State = FixState.Monitoring;
            }
            return;
        }

        // 全部正常
        if (_linkFailCount != 0 || _dnsFailCount != 0 || _retryCount != 0)
        {
            _log.Info("[StateMachine] 网络正常，重置计数");
            _linkFailCount = 0;
            _dnsFailCount = 0;
            _retryCount = 0;
        }
        State = FixState.Monitoring;
    }

    private void DoRestartAdapter()
    {
        if (!_config.Repair.RestartAdapter)
        {
            _log.Warn("[StateMachine] 配置关闭重启网卡，进入 Escalate");
            Transition(FixState.Escalate);
            return;
        }

        var adapter = AdapterManager.FindEthernetAdapter(_config.Repair.AdapterMatch);
        if (adapter == null)
        {
            _log.Error("[AdapterManager] 未找到以太网适配器（E200），进入 Escalate");
            Transition(FixState.Escalate);
            return;
        }

        if (AdapterManager.RestartAdapter(adapter.Name, _log))
        {
            LastFixTime = DateTime.Now;
            Thread.Sleep(TimeSpan.FromSeconds(_config.Repair.RecoverWaitSec));
            Transition(FixState.RecoverWait);
        }
        else
        {
            // 重启失败，进入重试/兜底
            HandleRecoveryFailed(useDns: false);
        }
    }

    /// <summary>修复后重探：成功回 Monitoring；失败走重试/兜底。</summary>
    private void DoRecoverCheck()
    {
        var r = _probe.Probe();
        LastProbe = r;
        _log.Info($"[Probe] 恢复确认 seq={r.ProbeSeq} adapterUp={r.AdapterUp} gw={r.GatewayIp} gwOk={r.GatewayOk} publicOk={r.PublicOk} dnsOk={r.DnsOk} appOk={r.AppOk}");

        if (r.AllOk)
        {
            _log.Info("[StateMachine] 修复成功，网络恢复正常");
            Transition(FixState.Healthy);
            return;
        }

        // 仍不可用（可能链路或 DNS），走重试/兜底
        HandleRecoveryFailed(useDns: r.DnsFault && !r.LinkFault);
    }

    private void DoDnsFault()
    {
        if (!_config.Repair.FlushDns)
        {
            Transition(FixState.SetPublicDns);
            return;
        }
        DnsManager.FlushDns(_log);
        Thread.Sleep(TimeSpan.FromSeconds(_config.Repair.DnsWaitSec));
        Transition(FixState.FlushDns);
    }

    private void DoFlushDns()
    {
        var r = _probe.Probe();
        LastProbe = r;
        if (r.AllOk)
        {
            _log.Info("[StateMachine] flushdns 后 DNS 恢复正常");
            Transition(FixState.Healthy);
            return;
        }

        if (r.DnsFault && !r.LinkFault)
        {
            // flush 无效，升级到切换公共 DNS
            _log.Warn("[StateMachine] flushdns 无效，切换公共 DNS");
            Transition(FixState.SetPublicDns);
            return;
        }

        // 修复后变成链路故障或仍无网：走重试/兜底
        HandleRecoveryFailed(useDns: false);
    }

    private void DoSetPublicDns()
    {
        if (!_config.Repair.FallbackPublicDns)
        {
            _log.Warn("[StateMachine] 配置关闭公共 DNS 兜底，进入重试流程");
            HandleRecoveryFailed(useDns: false);
            return;
        }

        var adapter = AdapterManager.FindEthernetAdapter(_config.Repair.AdapterMatch);
        if (adapter == null)
        {
            _log.Error("[DnsManager] 未找到以太网适配器（E104），无法切 DNS");
            HandleRecoveryFailed(useDns: false);
            return;
        }

        if (DnsManager.SetPublicDns(adapter.Name, _config.Repair.PublicDns, _log))
        {
            Thread.Sleep(TimeSpan.FromSeconds(_config.Repair.DnsWaitSec));
            var r = _probe.Probe();
            LastProbe = r;
            if (r.AllOk)
            {
                _log.Info("[StateMachine] 切换公共 DNS 后恢复上网");
                Transition(FixState.Healthy);
                return;
            }
            _log.Warn("[StateMachine] 切换公共 DNS 后仍不通");
        }
        else
        {
            _log.Error("[DnsManager] 切换公共 DNS 失败（E104）");
        }

        HandleRecoveryFailed(useDns: false);
    }

    /// <summary>
    /// 例行 WiFi 守护：无论有线状态如何，只要 WiFi 未连接（含 Software Off），
    /// 都主动打开无线并连接已存 SSID。已连接则跳过。由 Monitoring 每次调用。
    /// </summary>
    private void EnsureWifiRoutine()
    {
        if (!_config.Repair.ReconnectWifi)
        {
            // 配置关闭时保持现状（不主动开 WiFi）
            return;
        }

        _wlan.EnsureWifiUp(_config.Repair.PreferredSsid);
    }

    /// <summary>Escalate：暂停退避最大时长，重置计数后回归 Monitoring。</summary>
    private void DoEscalate()
    {
        var sec = _config.Repair.BackoffSec.Length > 0 ? _config.Repair.BackoffSec[^1] : 30;
        _log.Warn($"[StateMachine] Escalate：暂停 {sec}s 后回归 Monitoring");
        Thread.Sleep(TimeSpan.FromSeconds(sec));
        _linkFailCount = 0;
        _dnsFailCount = 0;
        _retryCount = 0;
        Transition(FixState.Monitoring);
    }

    private void DoLinkDown()
    {
        // 适配器 Down：仅记录，等待。循环会周期性回来，这里 sleep 一个探测间隔再重探。
        _log.Debug("[StateMachine] 有线链路 Down（物理断开），等待恢复");
        Thread.Sleep(TimeSpan.FromSeconds(_config.Probe.IntervalSec));
        var r = _probe.Probe();
        LastProbe = r;
        if (r.AdapterUp)
        {
            _log.Info("[StateMachine] 有线链路恢复 Up");
            Transition(FixState.Monitoring);
        }
    }

    /// <summary>恢复失败：按退避重试；达 maxRetry 则 WiFi 兜底或 Escalate。</summary>
    private void HandleRecoveryFailed(bool useDns)
    {
        _retryCount++;
        _log.Warn($"[StateMachine] 恢复失败，重试计数 {_retryCount}/{_config.Repair.MaxRetry}");

        if (_retryCount < _config.Repair.MaxRetry)
        {
            // 未达上限：退避后重新进入修复
            var idx = Math.Min(_retryCount - 1, _config.Repair.BackoffSec.Length - 1);
            var sec = _config.Repair.BackoffSec.Length > 0 ? _config.Repair.BackoffSec[idx] : 5;
            _log.Info($"[StateMachine] 退避 {sec}s 后重试");
            Thread.Sleep(TimeSpan.FromSeconds(sec));
            Transition(useDns ? FixState.DnsFault : FixState.RestartAdapter);
            return;
        }

        // 达上限 → Escalate（WiFi 由例行守护负责，不作为修复兜底）
        _log.Warn("[StateMachine] 达到最大重试，进入 Escalate");
        Transition(FixState.Escalate);
    }

    private void Transition(FixState next)
    {
        if (_verbose || State != next)
            _log.Debug($"[StateMachine] {State} → {next}");
        State = next;
    }
}
