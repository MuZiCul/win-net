# Win 网络修复工具 — 技术设计文档

> 版本：v0.1.0（对应 Release v0.1.0）
> 日期：2026-08-20
> 状态：已实施并发布（含 WiFi 开关 WLAN API 详细技术）

---

## 1. 概述

### 1.1 目标
提供一个**常驻后台、资源占用极小**的 Windows 工具，主用网络为**有线（以太网）**，逻辑如下：
1. **监测有线网络**：周期性探测有线适配器是否"卡死"（适配器 `Status=Up` 但探测连续失败）。
2. **卡死后重启有线网卡**（禁用再启用以太网适配器）—— 清除网卡死锁、IP 冲突、169.254 私有地址、DHCP 卡住等软故障，触发重新协商链路与续租。
3. **WiFi 例行守护（与有线状态无关）**：每个监测周期都检查 WiFi 状态，只要 **WiFi 未连接（含软件开关 Software Off）**，就主动打开无线网卡并连接已保存的 SSID（profile）。已连接则**不触碰**（不重连、不切换）。

全程无需用户干预，适用于长期在线但需要高可用的场景（如远程桌面、挂机下载、监控终端）。

### 1.2 非目标（范围边界）
- 不做通用网络修复全家桶（Winsock 重置、Hosts 清洗、防火墙重置等不在本期）。
- **DNS 故障兜底允许临时切换公共 DNS**（见 5.5），且**不主动改回原配置**（原 DNS 本就有问题，切公共 DNS 是改善；避免反复横跳引入 bug）。
- 切换 DNS **仅作用于以太网适配器 IPv4**，不触碰 WiFi 的 DNS 配置。
- **不主动在"有线 / WiFi"之间来回切换**：WiFi 连上后保持双连接共存，不主动切回纯有线。
- 不在 WiFi 已连接时重复重连或重启无线网卡。
- 不采集、不上传任何用户数据；WiFi 密码不硬编码、不写入日志。
- 不做 Web/图形配置界面。
- 不做跨平台（仅 Windows 10 / 11）。

### 1.3 关键指标
| 指标 | 目标值 |
|------|--------|
| 常驻内存 | < 15 MB（.NET 单文件，无 UI 线程） |
| CPU 占用 | 空闲时 ≈ 0%，探测瞬间 < 1% |
| 磁盘占用 | 单 exe < 5 MB（Release 裁剪后） |
| 默认探测间隔 | 15 s |
| 断联判定 | 连续 2 次探测失败（约 30 s） |
| 恢复确认 | 重连后连续 2 次探测成功 |

---

## 2. 运行形态

### 2.1 交付物
- 单个可执行文件 `WinNetFix.exe`（C# / .NET 8，发布为 `self-contained` + `PublishSingleFile` + `Trim` + `ReadyToRun`）。
- 启动方式：隐藏控制台窗口（`WindowStyle = Hidden`，或编译为 Windows 应用无控制台）。
- 计划任务自启：提供 `--install` / `--uninstall` 参数注册/注销 Task Scheduler 任务（触发器：登录时 / 系统启动时；权限：最高可用）。

### 2.2 命令行参数
| 参数 | 说明 |
|------|------|
| `--run` | 常驻运行（默认，不带参数等同） |
| `--install` | 注册开机自启计划任务 |
| `--uninstall` | 注销计划任务 |
| `--once` | 仅执行一次探测+修复（调试/排障用） |
| `--config <path>` | 指定配置文件（默认 `%ProgramData%\WinNetFix\config.json`） |
| `--status` | 打印当前状态机状态与最近日志 |
| `--version` | 版本号 |

### 2.3 配置文件（`config.json`）
```json
{
  "probe": {
    "intervalSec": 15,
    "failThreshold": 2,
    "timeoutMs": 3000,
    "layers": {
      "gatewayIcmp": true,        // 默认网关 IP，不依赖 DNS，判定本地链路
      "publicIcmp": "8.8.8.8",    // 外网 IP，不依赖 DNS，判定出口可达
      "dnsHttp": "https://www.msftconnecttest.com/connecttest.txt" // 依赖 DNS，判定域名解析
    }
  },
  "repair": {
  "restartAdapter": true,
  "flushDns": true,             // DNS 层故障时先刷新缓存
  "fallbackPublicDns": true,    // flush 无效则临时切换公共 DNS（仅以太网 IPv4，不改回）
  "publicDns": ["8.8.8.8", "223.5.5.5"],
  "reconnectWifi": true,
  "preferredSsid": "",          // 空 = 使用断连前最后连接的 SSID
  "adapterMatch": "auto",        // auto / 按名称正则
  "maxRetry": 3,
  "backoffSec": [5, 15, 30]
  },
  "log": {
    "level": "Info",
    "path": "%ProgramData%\\WinNetFix\\logs\\",
    "retentionDays": 30
  }
}
```

---

## 3. 架构设计

### 3.1 分层
```
┌─────────────────────────────────────────────┐
│                  Program / Main              │  进程入口、参数解析、单例互斥
├─────────────────────────────────────────────┤
│               Scheduler (Timer)              │  固定间隔触发探测循环
├──────────────┬──────────────┬───────────────┤
│  ProbeLayer  │ StateMachine │  ExecLayer    │
│  连通性探测  │  决策/状态机 │ 网卡/WiFi执行 │
├──────────────┴──────────────┴───────────────┤
│   AdapterManager   │   WlanManager           │  netsh / WMI 封装
├─────────────────────────────────────────────┤
│              Config / Logger / Metrics       │  配置、日志、自检指标
└─────────────────────────────────────────────┘
```

### 3.2 模块职责
- **Program**：解析参数、确保单实例（`Mutex`）、按模式启动。
- **Scheduler**：`System.Timers.Timer`，按 `intervalSec` 触发 `Tick`，内部加锁防止重入。
- **ProbeLayer**：**仅针对有线（以太网）适配器**做**分层**连通性探测，返回 `ProbeResult{GatewayOk, PublicOk, DnsOk, AppOk, AdapterUp, Layer}`：
  - `GatewayOk`：默认网关 ICMP 可达（不依赖 DNS，判定本地链路）。
  - `PublicOk`：外网 IP（如 8.8.8.8）ICMP 可达（不依赖 DNS，判定出口）。
  - `DnsOk`：`Dns.GetHostAddresses(目标域名)` 解析成功（判定 DNS 层健康）。
  - `AppOk`：HTTP 可达（附加诊断，**不参与 DNS 判定**）。
  - `AdapterUp=false`：物理链路已断开（拔线/端口 down），不作为"卡死"处理。
  - `Layer`：故障分层结论（`Link` / `Dns` / `App` / `None`），供状态机决策。
- **StateMachine**：核心决策（见第 4 节）。
- **ExecLayer**：
  - `AdapterManager`：枚举/匹配**以太网**适配器（按 `InterfaceType=802.3` 或名称正则），禁用+启用。
  - `WlanManager`：**WiFi 例行守护**——每次 tick 检查 `netsh wlan show interfaces`，未连接（含 `Software Off`）则打开无线并 `netsh wlan connect`，已连接则跳过；失败进冷却期。
- **Config / Logger / Metrics**：读取 JSON、写滚动日志、暴露 `--status`。

---

## 4. 状态机

### 4.1 状态定义
| 状态 | 含义 |
|------|------|
| `Monitoring` | 正常监测有线适配器（周期性**分层**探测）+ **WiFi 例行守护**（每次 tick 检查，未连接则主动打开并连接） |
| `SuspectDown` | 达到 `failThreshold` 次失败且适配器仍 `Up`，判定为"卡死" |
| `LinkDown` | 探测失败但适配器 `Down`（物理拔线/端口 down），不修复，仅记录等待 |
| `DnsFault` | 网关/外网 ICMP 通但**域名解析失败**（仅 DNS 层故障） |
| `FlushDns` | 执行 `ipconfig /flushdns` 刷新 DNS 缓存（第一层修复） |
| `SetPublicDns` | `FlushDns` 后仍不通 → 临时将**以太网 IPv4** DNS 切到公共 DNS（不改回） |
| `RestartAdapter` | 执行禁用/启用**有线**网卡（链路层故障主修复） |
| `RecoverWait` | 修复后等待网络恢复 |
| `Healthy` | 修复成功，回归 Monitoring |
| `Escalate` | 超过 `maxRetry` 仍失败，写错误日志并暂停一段时间 |

### 4.2 状态转移图
```
            probe: 三层全通
Monitoring ───────────► Monitoring (stay)

            probe fail (适配器 Up)
Monitoring ──┬────────► LinkDown (适配器 Down) ──(恢复 Up)──► Monitoring
             │
             └─(连续失败达阈值)──► SuspectDown
                                      │
                  ┌───────────────────┴───────────────────┐
           网关/外网 ICMP 不通                      网关/外网通 但 DNS 不通
                  │                                    │
                  ▼                                    ▼
           RestartAdapter                        DnsFault ──► FlushDns
                  │ ok                               │ ok(域名恢复)
                  ▼                                   ▼
              RecoverWait                       RecoverWait ──ok──► Healthy
                  │ 有线恢复 ok x2                   │ 仍不通
                  ▼                                   ▼
               Healthy ──► Monitoring           SetPublicDns (切以太网 IPv4 公共 DNS)
                  │ 仍失败 (达 maxRetry)               │ ok / 失败
                  ▼                                   ▼
               Escalate ──(暂停退避)──► Monitoring    RecoverWait ──ok──► Healthy ──► Monitoring
```

> 说明：
> - **WiFi 例行守护**：`Monitoring` 每次 tick 先执行 WiFi 守护（`EnsureWifiRoutine`）再探测有线。无论有线是否正常，只要 WiFi 未连接（含 `Software Off`）就主动打开无线并连接已存 SSID；已连接则不动。连接失败进入冷却期（默认 60s，可配置 `wifiRetryCooldownSec`），避免反复尝试。**WiFi 不作为有线修复失败的兜底**，两者解耦。
> - **分层判定**避免误重启：DNS 故障时（ICMP 通、域名不通）走 `FlushDns`，**不重启网卡**（重启救不了 DNS，反而打断连接）。
> - `AppFault`（链路/DNS 通但 HTTP 不可达，如 SSL 被拦）：不重启网卡、不折腾 DNS，首次 Warn + 进 `Escalate` 观察，静默期内（`appFaultSilenceSec`）仅记 Debug。

### 4.3 重试与退避
- 每次完整修复周期后 `retryCount++`。
- 若 `retryCount < maxRetry`：按 `backoffSec[retryCount]` 退避后重新进入 `RestartAdapter`。
- 超过 `maxRetry`：进入 `Escalate`，暂停 `backoffSec` 最后一项时长，然后重置计数器回到 `Monitoring`（避免死循环拖垮系统）。
- `LinkDown` 状态**不计入重试**，不重启网卡（物理断线重启无效），待链路恢复 `Up` 后自动回 `Monitoring`。

---

## 5. 修复命令清单

> 全部使用 Windows 自带命令，无第三方依赖。执行前记录原始状态以便回滚。

### 5.1 重启有线网卡（主修复）
仅针对**以太网（802.3）**适配器，不参与 WiFi。
```powershell
# 枚举活动以太网适配器（按 InterfaceType / 名称匹配，排除隧道、回环、Hyper-V）
Get-NetAdapter | Where-Object {
    $_.Status -eq 'Up' -and
    ($_.InterfaceType -eq 6 -or $_.Name -match '以太网|Ethernet')
}

# 禁用 + 启用（PowerShell NetAdapter 模块，需管理员）
Disable-NetAdapter -Name "<name>" -Confirm:$false
Start-Sleep -Seconds 3
Enable-NetAdapter  -Name "<name>" -Confirm:$false
```
回退方案：若 `Disable-NetAdapter` 不可用，改用
```cmd
netsh interface set interface name="<name>" admin=disable
netsh interface set interface name="<name>" admin=enable
```
> 判定"卡死"前提：适配器 `Status=Up`（物理链路在）但探测失败。若 `Status=Down`（拔线/端口 down），**不执行本步骤**，转 `LinkDown` 等待。

### 5.2 WiFi 例行守护（Monitoring 每次 tick 执行）
与有线状态无关，每个监测周期都检查；未连接（含软件开关 Software Off）才主动打开并连接，已连接则跳过。

#### 5.2.1 状态检测（`WlanManager.CheckWifi`）
```cmd
netsh wlan show interfaces
```
- `State = connected` → 已连接，跳过本周期。
- `Radio status: Hardware On / Software Off` → 软件开关关闭，需先打开（见 5.2.2）。
- `Radio status: Hardware Off` → **飞行模式**，系统锁定，软件无法打开，Warn 提示手动开启。
- `There is no wireless interface` / wlansvc 未运行 → 无无线能力，跳过。
> 注意：netsh 在中文系统输出本地化标签，工具统一经 `chcp 65001` 管道强制英文标签（见 ProcessRunner）。

#### 5.2.2 打开/关闭 WiFi 软件开关（核心：必须用 WLAN API，netsh 打不开）
**关键技术结论**（实测验证）：
- `netsh interface set interface admin=enable` 只能启用接口层，**无法改变 radio 软开关（Software Off）**。
- `netsh wlan set autoconfig enabled=yes` 同样打不开 Software Off。
- Windows 的 `Software Off` 是 wlansvc 管理的 radio 电源状态，**必须通过 `wlanapi.dll` 的 `WlanSetInterface` + `wlan_intf_opcode_radio_state` 设置**。

**P/Invoke 实现要点**（`WlanApi.cs`，参照 `emoacht/ManagedNativeWifi`）：
```
opcode  wlan_intf_opcode_radio_state = 4            （不是 0x1000000D！）
```
- **查询状态** `WlanQueryInterface`：返回 `WLAN_RADIO_STATE` =
  `dwNumberOfPhys(4)` + N × `WLAN_PHY_RADIO_STATE(12)`；
  每 PHY 12 字节 = `{dwPhyIndex(4), dot11SoftwareRadioState(4), dot11HardwareRadioState(4)}`。
  任一 PHY 的 `dot11SoftwareRadioState == 1(On)` 即视为开关开。
- **设置状态** `WlanSetInterface`：**必须传单个 `WLAN_PHY_RADIO_STATE`（12 字节）**，
  结构 `{dwPhyIndex=0, dot11SoftwareRadioState=On/Off, dot11HardwareRadioState=0}`；
  **绝不能传整个 PHY 数组**（否则返回 `ERROR_INVALID_PARAMETER (87)`）。
- 接口匹配：`WlanEnumInterfaces` 返回的列表头 `{dwNumberOfItems(4), dwIndex(4)}`（共 8 字节），
  数组从偏移 8 开始；匹配用**接口描述名**（如 `Intel(R) Dual Band Wireless-AC 3165`），
  而非 netsh 接口名（`WLAN`）。
- 需管理员权限；`ERROR_NOT_SUPPORTED (50)` 表示驱动不支持（换 netsh 回退）。

```csharp
// 打开：SetRadioState(iface, on: true)
var buf = new byte[12];
BitConverter.GetBytes(0u).CopyTo(buf, 0);          // dwPhyIndex = 0
BitConverter.GetBytes(on ? 1u : 2u).CopyTo(buf, 4); // dot11SoftwareRadioState: 1=On, 2=Off
BitConverter.GetBytes(0u).CopyTo(buf, 8);          // dot11HardwareRadioState = 0
WlanSetInterface(h, ref guid, 4, 12, bufPtr, IntPtr.Zero);
```

#### 5.2.3 连接流程（`EnsureWifiUp`）
```cmd
:: 1. 若 Software Off → WlanApi.SetRadioState(iface, on:true) 打开开关
::    （netsh 无法做到，必须用 WLAN API）
:: 2. 确定目标 SSID：preferredSsid → 最后连接 SSID → 首个 profile
netsh wlan connect name="<SSID>"
:: 3. 等待 8s，校验 State = connected；失败进冷却期（wifiRetryCooldownSec，默认 60s）
```
- 打开开关后必须**复检** `Software Off` 是否消除；未消除则**不再尝试连接**（radio 关着连不上），明确 Warn 提示手动开。
- profile 不存在/密码失效 → 记 Warn 后跳过（不进入有线修复流程，二者解耦）。
- **不主动切回有线**：WiFi 连上后保持双连接，工具不再干预无线。

### 5.3 分层探测（ProbeLayer 内部，仅限有线）
按依赖关系由低到高分层，定位故障层：
1. **网关 ICMP**（不依赖 DNS）：`Ping(默认网关 IP)`，判定本地链路。
2. **外网 ICMP**（不依赖 DNS）：`Ping("8.8.8.8")`，判定出口可达。
3. **DNS 解析**（`Dns.GetHostAddresses(目标域名)`）：解析成功即 DNS 层健康。
4. **应用 HTTP**（`HttpClient.GetAsync(connecttest.txt)`）：**附加诊断**，状态码 200 且内容含 `Microsoft Connect Test`。
- 适配器状态：通过 `Get-NetAdapter` / `NetworkInterface.OperationalStatus` 读取，区分 `Up`（卡死候选）与 `Down`（物理断线）。
- 结论映射：
  - 网关/外网不通 → `Layer=Link`（走重启网卡）。
  - 网关/外网通、DNS 解析失败 → `Layer=Dns`（走 FlushDns → 切公共 DNS）。
  - 链路/DNS 通、HTTP 失败 → `Layer=App`（SSL 拦截/防火墙封 443 等，**不修复**，记录观察）。
  - 全通 → 正常。
- **重要**：DNS 层健康与否以**解析成功**为准，不用 HTTP 可达性判定（HTTP 失败可能是 SSL/防火墙拦截，不是 DNS 问题）。

### 5.4 刷新 DNS 缓存（DNS 层故障第一层修复）
仅当分层判定为 `Layer=Dns` 时执行：
```cmd
ipconfig /flushdns
```
- 零风险、可逆，清除本地 DNS 解析缓存（解决挂机后 DNS 记录过期/污染导致的"能 ping IP 但不能开网页"）。
- 执行后等 `3s` 重新走分层探测确认域名恢复。
- 若 flushdns **后仍不通** → 进入 `SetPublicDns`（5.5）做第二层修复。

### 5.5 临时切换公共 DNS（DNS 层故障第二层修复，真正修好）
当 `FlushDns` 无效，判定为上游 DNS 服务器故障/被污染，临时将**以太网适配器 IPv4** 的 DNS 切到公共 DNS，**不再改回**（原 DNS 本就有问题，切公共 DNS 是改善；避免反复横跳）：
```cmd
:: 仅作用于以太网（按 adapterMatch 匹配到的名称），不改 WiFi
netsh interface ip set dns name="以太网" static 8.8.8.8
netsh interface ip add dns name="以太网" 223.5.5.5 index=2
```
- 执行前可用 `netsh interface ip show dns "以太网"` 记录原配置（仅日志留痕，不用于改回）。
- 执行后等 `3s` 重新走分层探测确认域名恢复。
- **仅改以太网 IPv4，不触碰 WiFi 的 DNS 配置**（避免影响已连无线）。
- 配置项 `publicDns` 可自定义主/备 DNS 地址。
- 若切 DNS 命令失败（权限/适配器异常）→ 进入 `Escalate`（错误码 E104）。

---

## 6. 错误处理矩阵

| 阶段 | 错误场景 | 错误码 | 处理 | 兜底 |
|------|----------|--------|------|------|
| 启动 | 非管理员权限 | E001 | 日志告警，仍尝试运行（重启网卡会失败） | 提示用户以管理员运行 |
| 启动 | 单实例已存在 | E002 | 退出，不重复拉起 | — |
| 探测 | 适配器 Down（物理拔线/端口 down） | E100 | **不重启**，转 `LinkDown` 等待 | 链路恢复 Up 后自动回 Monitoring |
| 探测 | 网关/外网 ICMP 不通（链路层故障） | E101 | 计入失败计数 | 达阈值进入重启网卡（RestartAdapter） |
| 探测 | 网关/外网通但域名解析失败（DNS 层故障） | E102 | 计入失败计数 | 达阈值进入 FlushDns（**不重启网卡**） |
| DNS 修复 | `ipconfig /flushdns` 后域名仍不通 | E103 | 转 `SetPublicDns`：临时切以太网 IPv4 到公共 DNS（不改回） | 仍不通 → E104 |
| DNS 修复 | 切换公共 DNS 命令失败 | E104 | 权限/适配器异常，转 `Escalate` | 提示用户手动设置 DNS |
| 重启网卡 | 以太网适配器不存在/匹配失败 | E200 | 日志 + Escalate | — |
| 重启网卡 | 禁用失败（驱动忙） | E201 | 重试 1 次，仍失败 Escalate | 尝试 netsh 回退 |
| 重启网卡 | 启用失败 | E202 | 记录，尝试 netsh 回退 | 通知用户手动启用 |
| WiFi 例行守护 | 无无线网卡/wlansvc 未运行 | E300 | 记 Debug，跳过本次守护 | 下个周期再查 |
| WiFi 例行守护 | SSID 无 profile / 密码失效 | E301 | 记 Warn，跳过 | 提示用户重新保存 WiFi |
| WiFi 例行守护 | 连接超时 / 信号不可达 | E302 | 记 Warn，进入冷却期（默认 60s） | 冷却后下个周期重试 |
| WiFi 例行守护 | 无线硬件关闭（飞行模式） | E303 | 记 Warn，无法软件开启 | 提示用户手动开启 |
| 恢复确认 | 修复后仍不可达 | E400 | 进入退避重试 | 达到 maxRetry 暂停 |

---

## 7. 兼容性矩阵

| 项目 | Windows 10 21H2+ | Windows 11 22H2+ | 备注 |
|------|------------------|------------------|------|
| `Get-NetAdapter` | ✅ | ✅ | Win10 默认有 |
| `netsh wlan` | ✅ | ✅ | 全版本一致 |
| `HttpClient` (TLS1.2/1.3) | ✅ | ✅ | .NET 8 内置 |
| 计划任务自启 | ✅ | ✅ | Task Scheduler |
| 仅有线（无 WiFi） | ✅（跳过重连） | ✅ | 自动识别 |
| 多网卡（有线+无线） | ✅ | ✅ | 主修有线；无线保持常开，仅未连时按需激活兜底 |

**兼容约束**
- 仅支持 x64（主流桌面）。如需 ARM64 单独发布。
- 必须管理员权限运行（重启网卡、连接 WiFi 需要）。

---

## 8. 安全与合规

1. **权限最小化**：仅在需要操作系统网络栈时提权；普通探测不需要。安装计划任务时请求提权一次。
2. **无数据外发**：探测仅访问 `msftconnecttest.com`（微软官方连通性检测）与用户可配置的 IP；不收集 MAC、SSID 列表、地理位置等并上传。
3. **日志脱敏**：日志不记录 WiFi 密码；SSID 以明文记录属必要诊断信息，文件权限限制为管理员读写（`%ProgramData%` 默认 ACL）。
4. **防火墙**：探测使用出站 80/443 + ICMP，不开放任何入站端口，不关闭防火墙。
5. **可逆性**：所有修复动作开始前记录原状态（适配器状态、已连接 SSID），可在日志中审计；不修改系统永久配置（不重置 Winsock/DNS）。
6. **自启动透明**：计划任务名称 `WinNetFix` 清晰可见，可一键 `--uninstall` 移除。

---

## 9. 日志与可观测性

- 滚动日志：`%ProgramData%\WinNetFix\logs\winnetfix-YYYYMMDD.log`，按 `retentionDays` 清理。
- 日志字段：`[时间戳] [级别] [状态机状态] 消息`。
- `--status` 输出：当前状态、最近 5 次探测结果、上次修复时间、重试计数。
- 可选：写入 Windows 事件日志（Application 下 `WinNetFix` 源），便于集中排障。

---

## 10. 测试计划

### 10.1 单元测试（核心逻辑，无真实网络依赖）
- 状态机转移：模拟 ProbeResult 序列，验证 `Monitoring→SuspectDown→RestartAdapter→...→Healthy` 路径。
- 退避与 maxRetry 边界：验证退避数组越界时不崩、达到阈值进入 Escalate。
- 配置解析：非法 JSON、缺字段时的默认值兜底。

### 10.2 集成/手动测试
| 用例 | 操作 | 预期 |
|------|------|------|
| 正常监测 | 联网静置 | 状态保持 Monitoring，日志周期探测成功 |
| 制造断联 | 禁用网卡或拔网线 | 约 30s 后自动重启网卡并恢复 |
| WiFi 重连 | 手动断开 WiFi | 自动重连到原 SSID |
| SSID 密码失效 | 改 WiFi 密码 | 重连失败进入 Escalate，日志 E301，不崩溃 |
| 长时间稳定性 | 运行 24h | 内存不增长（无泄漏），无重复拉起 |
| 自启动 | 重启系统 | 登录后自动后台运行 |
| 卸载 | `--uninstall` | 计划任务移除，进程退出 |

### 10.3 性能基线
- 用 Process Explorer / `dotnet-counters` 记录常驻 1h 的 Private Bytes、CPU%，确认达标（见 1.3）。

---

## 11. 后续可扩展（非本期）
- 邮件/Webhook 通知断网事件。
- 多 SSID 优先级列表与故障转移。
- 网卡驱动自动更新检测。
- 系统托盘图标（轻量，会略微增加内存）。

---

## 12. 风险与待决问题
1. **管理员权限常态化**：常驻以管理员运行存在安全面，需评估是否改为「普通运行 + 修复时提权（UAC 弹窗）」—— 但弹窗会打断无值守场景。待决。
2. **`Get-NetAdapter` 在无 PowerShell 环境的极简镜像**：仅家庭/专业版 Windows 默认具备，风险低。
3. **公司域环境组策略**：可能禁止 netsh 改适配器，需在企业环境实测。
4. **误判**：短暂抖动即重启网卡可能影响正在进行的连接，默认值 failThreshold=2 与 15s 间隔可缓解，提供配置可调。
5. **双连接路由优先级**：WiFi 兜底连上后与有线并存，Windows 默认按接口跃点数（metric）选路，通常有线优先；若兜底后流量误走 WiFi，可在配置中调整 metric。本期不主动改路由。
6. **仅治"卡死"不治"真断线"**：物理拔线（适配器 Down）不重启网卡（重启无效），仅等待链路恢复。若用户期望拔线后也能"做点什么"，需另行评估。
7. **自动改 DNS 不主动还原**：DNS 故障时工具会把以太网 IPv4 的 DNS 临时切到公共 DNS 且**不改回**（原 DNS 本就有问题，切公共是改善）。但这会改变系统网络配置——需在首次安装/README 明确告知用户此行为，保留其知情权；若用户介意，可配置 `fallbackPublicDns: false` 退回"仅 flushdns"。
8. **仅改以太网 DNS**：切换公共 DNS 只作用于以太网适配器 IPv4，不碰 WiFi 的 DNS，避免影响已连无线。
