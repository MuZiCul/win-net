# Win 网络修复工具 — 技术设计文档

> 版本：v0.1.3（对应 Release v0.1.3）
> 日期：2026-08-24
> 状态：已实施并发布（含 WiFi 开关 WLAN API 详细技术、169.254 DHCP 修复、停手策略）

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
| 磁盘占用 | 单 exe ~15 MB（self-contained + Trim + 单文件） |
| 默认探测间隔 | 15 s |
| 断联判定 | 连续 2 次探测失败（约 30 s） |
| 恢复确认 | 重连后连续 2 次探测成功 |

---

## 2. 运行形态

### 2.1 交付物
- 单个可执行文件 `WinNetFix.exe`（C# / .NET 8，发布为 `self-contained` + `PublishSingleFile` + `PublishTrimmed`）。
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
| `--github-fix` | 修复 GitHub 连接（DoH 解析真实 IP 写入 hosts） |
| `--github-restore` | 还原 hosts（移除 WinNetFix GitHub 条目） |
| `--version` | 版本号 |

### 2.3 托盘菜单
`--run` 常驻时右下角托盘图标，右键菜单：

| 菜单项 | 类型 | 行为 |
|---|---|---|
| 开机自启 | 开关 | 注册/注销计划任务 |
| 自动执行修复 | 开关 | 关闭后只探测不修复（不重启网卡/修 DNS/WiFi），默认开 |
| 自动连接 WiFi | 开关 | 关闭后不主动碰 WiFi，默认开 |
| 重启网卡 | 动作 | 接口级禁用→启用，后台执行，气泡反馈 |
| 禁用并恢复网卡 | 动作 | PnP 设备级禁用→启用（重载驱动），需确认，PnP 不可用回退接口级 |
| 修复 DNS | 动作 | flushdns → 仍不通切公共 DNS → 气泡反馈 |
| 打开日志目录 | 动作 | 资源管理器打开 `exe目录\logs` |
| 修复 GitHub 连接 | 动作 | DoH 解析真实 IP → TCP443 测速 → 写入 hosts（标记段） |
| 还原 GitHub hosts | 动作 | 移除 WinNetFix GitHub 标记段 |
| 显示状态 | 动作 | 气泡显示当前网络状态/IP |
| 关于 | 动作 | 默认浏览器打开项目主页 |
| 卸载 | 动作 | 移除自启 + 卸载程序/提示手动删 |
| 退出 | 动作 | 结束常驻进程 |

开关仅运行时生效（不写入 config.json）。

**手动动作统一反馈**：所有动作类菜单项均在后台线程执行，执行期间自动修复让路（防并发操作网卡/DNS）；同时弹出**实时进度窗口**（`ProgressWindow`，纯 P/Invoke RichEdit 富文本）逐行显示过程，完成后弹详情框 + 托盘气泡。进度行按状态着色：绿色 `✔`=成功、红色 `✘`=失败、蓝色 `►`=候选/关键节点。

### 2.4 配置文件（`config.json`）
```json
{
  "probe": {
    "intervalSec": 15,
    "failThreshold": 2,
    "timeoutMs": 3000,
    "gatewayIcmp": true,          // 网关 ICMP（不依赖 DNS，判定本地链路）
    "publicIcmpTarget": "8.8.8.8", // 外网 ICMP 目标
    "dnsHttpUrl": "https://www.msftconnecttest.com/connecttest.txt"
  },
  "repair": {
    "restartAdapter": true,
    "recoverWaitSec": 20,          // 重启后等 DHCP 完成的等待
    "dhcpWaitSec": 20,             // 等 IP 就绪（非 169.254）轮询上限
    "renewDhcp": true,             // 169.254 时强制 ipconfig /release + /renew
    "flushDns": true,              // DNS 层故障时先刷新缓存
    "fallbackPublicDns": true,     // flush 无效则临时切换公共 DNS（仅以太网 IPv4，不改回）
    "publicDns": ["8.8.8.8", "223.5.5.5"],
    "reconnectWifi": true,
    "wifiRetryCooldownSec": 60,    // WiFi 连接失败冷却期
    "preferredSsid": "",           // 空 = 使用断连前最后连接的 SSID
    "adapterMatch": "auto",        // auto / 按名称正则
    "maxRetry": 3,
    "backoffSec": [5, 15, 30],
    "escalateCooldownSec": 1800,   // 冷却时长（30 分钟）：连续失败/停手后暂停多久再重试
    "maxEscalate": 3,              // 连续 Escalate 达此数进停手
    "disableAdapterPowerSaving": true,  // 启动时关闭有线网卡省电挂起（防僵尸状态）
    "powerSavingCheckIntervalSec": 0    // 省电复查间隔（0=仅启动时一次）
  },
  "log": {
    "level": "Info",
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
| `SuspectDown` | 达到 `failThreshold` 次失败且适配器仍 `Up` 且有合法 IP，判定为"卡死" |
| `LinkDown` | 探测失败但适配器 `Down`（物理拔线/端口 down），不修复，仅记录等待 |
| `DnsFault` | 网关/外网 ICMP 通但**域名解析失败**（仅 DNS 层故障） |
| `FlushDns` | 执行 `ipconfig /flushdns` 刷新 DNS 缓存（第一层修复） |
| `SetPublicDns` | `FlushDns` 后仍不通 → 临时将**以太网 IPv4** DNS 切到公共 DNS（不改回） |
| `RestartAdapter` | 执行禁用/启用**有线**网卡（链路层故障主修复）；等 IP 失败时**升级 PnP 驱动级重启**（等效"禁用/启用设备"，替代"重启电脑"） |
| `RenewDhcp` | **169.254（DHCP 拿不到 IP）**：强制 `ipconfig /release + /renew`，**不重启网卡** |
| `RecoverWait` | 修复后等待网络恢复（先等 IP 就绪，最多 `dhcpWaitSec`） |
| `Healthy` | 修复成功，回归 Monitoring |
| `Escalate` | 连续失败**长冷却**（`escalateCooldownSec`，默认 30 分钟），累计达 `maxEscalate` 进 Suspended |
| `Suspended` | **停手**：暂停干预 `escalateCooldownSec`（默认 30 分钟）；到点后探测，恢复则回归，仍不可用则**主动重启网卡再试**（非永久停手） |

### 4.2 状态转移图
```
            probe: 三层全通 + 合法 IP
Monitoring ───────────► Monitoring (stay)

            probe fail (适配器 Up)
Monitoring ──┬────────► LinkDown (适配器 Down) ──(恢复 Up)──► Monitoring
             │
             ├─(仅 169.254 无合法 IP)──► RenewDhcp ──ok──► RecoverWait ──ok──► Healthy
             │                              │ 仍失败
             └─(连续失败达阈值且有合法 IP)──► SuspectDown
                                              │
                    ┌─────────────────────────┴──────────────────────────┐
             网关/外网 ICMP 不通                                   网关/外网通 但 DNS 不通
                    │                                                     │
                    ▼                                                     ▼
             RestartAdapter                                     DnsFault ──► FlushDns
                    │ ok (等 DHCP 就绪)                                    │ ok(域名恢复)
                    ▼                                                      ▼
                RecoverWait                                          RecoverWait ──ok──► Healthy
                    │ 有线恢复 ok x2                                      │ 仍不通
                    ▼                                                      ▼
                 Healthy ──► Monitoring                          SetPublicDns (切以太网 IPv4 公共 DNS)
                    │ 仍失败 (达 maxRetry)                                  │ ok / 失败
                    ▼                                                      ▼
                 Escalate (长冷却 escalateCooldownSec)              RecoverWait ──ok──► Healthy ──► Monitoring
                    │ 累计达 maxEscalate 次
                    ▼
                 Suspended (停手 escalateCooldownSec，到点重启网卡再试，恢复则回归)
```

> 说明：
> - **WiFi 例行守护**：`Monitoring` 每次 tick 先执行 WiFi 守护（`EnsureWifiRoutine`）再探测有线。无论有线是否正常，只要 WiFi 未连接（含 `Software Off`）就主动打开无线并连接已存 SSID；已连接则不动。连接失败进入冷却期（默认 60s，可配置 `wifiRetryCooldownSec`），避免反复尝试。**WiFi 不作为有线修复失败的兜底**，两者解耦。
> - **分层判定**避免误重启：DNS 故障时（ICMP 通、域名不通）走 `FlushDns`，**不重启网卡**（重启救不了 DNS，反而打断连接）。
> - **169.254 不重启网卡**：适配器 Up 但只有 169.254（DHCP 未拿到 IP）时走 `RenewDhcp`（强制续租），**重启网卡无效且会反复打断 DHCP**。
> - `AppFault`（链路/DNS 通但 HTTP 不可达，如 SSL 被拦）：不重启网卡、不折腾 DNS，**仅记录、保持 Monitoring，不进 Escalate**（避免误耗 escalateCount 导致误停手），静默期内（`appFaultSilenceSec`）仅记 Debug。

### 4.3 重试、退避与停手
- 每次完整修复周期后 `retryCount++`。
- 若 `retryCount < maxRetry`：按 `backoffSec[retryCount]` 退避后重新进入 `RestartAdapter`。
- 超过 `maxRetry`：进入 `Escalate`，**长冷却 `escalateCooldownSec`（默认 1800s/30 分钟）** 后重置计数回到 `Monitoring`。
- **停手**：`Escalate` 累计 `maxEscalate`（默认 3）次后进入 `Suspended`——暂停干预 `escalateCooldownSec`（默认 30 分钟）避免高频打扰；到点后探测，网络恢复则自动回归 `Monitoring`，**仍不可用则主动重启网卡再试**（非永久停手），如此循环直到恢复或用户手动处理。
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

#### 5.1.1 PnP 驱动级重启（接口级重启无效时的升级手段）
接口级禁用/启用（`Disable-NetAdapter`/`netsh admin=disable/enable`）**只能重载接口配置，无法重载驱动**。
Intel I219 等网卡陷入"僵尸状态"时（表现为：无线正常、有线 169.254、`renew` 超时、接口级重启无效、**只有重启电脑才恢复**），
需用 **PnP 设备级重启**（等效设备管理器"禁用/启用设备"，能真正重载驱动，替代"重启电脑"）：
```powershell
# 1. 反查有线网卡 PnP 设备 ID（双保险：描述必须含 Ethernet/以太网 且 不含 Wireless/WLAN/802.11）
Get-NetAdapter -Name "<name>" | Select-Object Name, InterfaceDescription, PnpDeviceID

# 2. PnP 设备级重启（Win10 1809+ 支持）
pnputil /restart-device "PCI\VEN_8086&DEV_15B8&SUBSYS_..."
# 回退方案：
Disable-PnpDevice -InstanceId "PCI\..." -Confirm:$false
Enable-PnpDevice  -InstanceId "PCI\..." -Confirm:$false
```
- **只作用于有线网卡**：PnPDeviceID 是硬件唯一标识，有线/无线 ID 不同，配合描述校验，绝不会误伤无线。
- 触发时机：`RestartAdapter` 接口级重启成功但等 IP 仍失败（169.254 未消除）时自动升级执行。
- 需管理员权限；`pnputil /restart-device` 需 Win10 1809+（1809 以下自动回退 `Disable/Enable-PnpDevice`）。

#### 5.1.2 省电预防（启动自动关闭，防患于未然）
网卡僵尸状态的最大诱因是 **Windows 网卡省电**：网线断开后网卡进入睡眠（D3），链路恢复时驱动未唤醒 → 死机态。
工具启动（`--run`/`--once` 提权后）自动执行：
```powershell
# 关闭"断开时进入睡眠"（DeviceSleepOnDisconnect，Intel I219 的元凶）
Set-NetAdapterPowerManagement -Name "<name>" -DeviceSleepOnDisconnect Disabled
# 关闭"选择性挂起"（SelectiveSuspend，部分网卡不支持返回 Unsupported，无害）
Set-NetAdapterPowerManagement -Name "<name>" -SelectiveSuspend Disabled
```
- 启动时执行一次；配置 `powerSavingCheckIntervalSec` 可周期性复查（防设置被改回）。
- 查询逻辑兼容新旧参数（`DeviceSleepOnDisconnect` 优先，回退 `AllowComputerToTurnOffDevice`）。
- 配置开关：`disableAdapterPowerSaving`（默认 true）。`--status` 可查看当前省电状态（需管理员）。

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

### 5.6 GitHub 连接修复（DNS 污染专项）
国内访问 GitHub 不稳定的主因是 **DNS 污染**（域名解析返回被污染 IP），通用 DNS 修复（flushdns/切公共 DNS）对其无效。本工具通过 **DoH 获取真实 IP 写入 hosts** 绕过污染：

**诊断**（`GithubHosts.Diagnose`）：
- TCP 443 连接 `github.com`（本机解析 IP）判断可达性
- 本机 DNS 解析结果与 DoH 真值比对：不一致 = 污染

**修复**（`GithubHosts.Fix`，`--github-fix` / 托盘"修复 GitHub 连接"）：
1. 对核心域名清单（`github.com`、`api.github.com`、`raw.githubusercontent.com` 等 9 个）多源 DoH（阿里/阿里IP直连/Google）解析 A 记录
2. 对候选 IP 做 TCP 443 测速，选最快可达 IP
3. 写入 hosts 标记段（`# === WinNetFix GitHub Fix BEGIN/END ===`）
4. 首次写入前自动备份原 hosts → `hosts.winnetfix.bak`

**还原**（`GithubHosts.Restore`，`--github-restore` / 托盘"还原 GitHub hosts"）：删除标记段，不影响用户其他 hosts 内容。

**设计要点**：
- hosts 写入仅管理标记段内条目（幂等重写），不触碰用户自定义条目
- **编码保护**：读写前检测 hosts 编码（BOM→UTF8；无 BOM 严格 UTF-8 解码尝试→UTF8；失败→系统 ANSI/GBK），保证用户中文注释不被 UTF-8 误解码损坏
- 单个域名无可用 IP 时跳过（不写无效条目），全部失败则不修改 hosts
- 写入成功后执行 `ipconfig /flushdns`，确保 hosts 立即生效
- 域名解析超时 5s/源、测速超时 4s/IP，均有界不阻塞
- 需要管理员权限（写 `%SystemRoot%\System32\drivers\etc\hosts`）

### 5.7 实时进度窗口（ProgressWindow）
所有手动操作（重启网卡/禁用恢复/修复 DNS/修复 GitHub/还原 hosts）的实时过程反馈：

- **纯 P/Invoke 实现**：`CreateWindowEx` + RichEdit 控件（`RICHEDIT50W`，需先 `LoadLibrary("Msftedit.dll")`），消息循环复用托盘窗口的 `GetMessage` 循环（关闭进度窗口不退出托盘）。
- **跨线程安全**：后台修复线程通过 `SendMessage(EM_SETSEL/EM_REPLACESEL)` 追加文本；`AppendLine(string, ProgressColor)` 支持按行着色（`EM_SETCHARFORMAT` + `CHARFORMAT2W`）。
- **彩色符号**：`✔`=成功（绿）、`✘`=失败（红）、`►`=候选/关键节点（蓝），符号继承行颜色。
- **生命周期**：由主线程创建、字段持有引用防 GC；用户手动关闭；窗口标题栏/任务栏显示 exe 彩色应用图标（`ExtractIconEx` + `WM_SETICON`）；`WM_DESTROY` 释放图标句柄（防泄漏）。
- **回调协议**：各操作方法接受 `Action<string, ProgressColor>? progress` 逐阶段上报；`TrayApp.RunManualAction` 统一创建窗口 + 完成后 `NativeBox` 详情弹窗。
- **操作代次标记**：`TrayApp._opToken` 递增，旧任务完成后若代次不匹配则不弹详情框（防连续操作时旧结果干扰）。
- **标题状态标注**：完成后 `SetTitle` 更新为"（完成）/（失败）/（异常）"（`SetWindowTextW` 跨线程安全）。
- **关键约束**：进度窗口 `WM_DESTROY` 不 `PostQuitMessage`（否则会连带退出托盘消息循环）。

---

## 6. 错误处理矩阵

| 阶段 | 错误场景 | 错误码 | 处理 | 兜底 |
|------|----------|--------|------|------|
| 启动 | 非管理员权限 | E001 | 日志告警，仍尝试运行（重启网卡会失败） | 提示用户以管理员运行 |
| 启动 | 单实例已存在 | E002 | 退出，不重复拉起 | — |
| 探测 | 适配器 Down（物理拔线/端口 down） | E100 | **不重启**，转 `LinkDown` 等待 | 链路恢复 Up 后自动回 Monitoring |
| 探测 | 适配器 Up 但仅 169.254（DHCP 未拿 IP） | E110 | 计入失败计数 | 达阈值进入 RenewDhcp（**不重启网卡**） |
| 探测 | 网关/外网 ICMP 不通（链路层故障） | E101 | 计入失败计数 | 达阈值进入重启网卡（RestartAdapter） |
| 探测 | 网关/外网通但域名解析失败（DNS 层故障） | E102 | 计入失败计数 | 达阈值进入 FlushDns（**不重启网卡**） |
| DNS 修复 | `ipconfig /flushdns` 后域名仍不通 | E103 | 转 `SetPublicDns`：临时切以太网 IPv4 到公共 DNS（不改回） | 仍不通 → E104 |
| DNS 修复 | 切换公共 DNS 命令失败 | E104 | 权限/适配器异常，转 `Escalate` | 提示用户手动设置 DNS |
| 重启网卡 | 以太网适配器不存在/匹配失败 | E200 | 日志 + Escalate | — |
| 重启网卡 | 禁用失败（驱动忙） | E201 | 重试 1 次，仍失败 Escalate | 尝试 netsh 回退 |
| 重启网卡 | 启用失败 | E202 | 记录，尝试 netsh 回退 | 通知用户手动启用 |
| 重启网卡 | PnP 驱动级重启失败（驱动忙/无权限/版本低） | E203 | 记录，继续重试链路 | 提示用户手动重启电脑 |
| WiFi 例行守护 | 无无线网卡/wlansvc 未运行 | E300 | 记 Debug，跳过本次守护 | 下个周期再查 |
| WiFi 例行守护 | SSID 无 profile / 密码失效 | E301 | 记 Warn，跳过 | 提示用户重新保存 WiFi |
| WiFi 例行守护 | 连接超时 / 信号不可达 | E302 | 记 Warn，进入冷却期（默认 60s） | 冷却后下个周期重试 |
| WiFi 例行守护 | 无线硬件关闭（飞行模式） | E303 | 记 Warn，无法软件开启 | 提示用户手动开启 |
| 恢复确认 | 修复后仍不可达 | E400 | 进入退避重试 | 达到 maxRetry 进入 Escalate |
| DHCP 修复 | `ipconfig /renew` 失败 / 续租后仍 169.254 | E500 | 计入重试 | 达 maxRetry → Escalate |
| 停手 | 连续 `maxEscalate` 次失败 | E600 | 进 Suspended，暂停干预 `escalateCooldownSec` | 到点主动重启网卡再试，恢复回归 |

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
3. **日志脱敏**：日志不记录 WiFi 密码；SSID 以明文记录属必要诊断信息。日志目录**固定为安装目录 `logs\`（exe 所在目录，不支持自定义）**，安装版位于 Program Files 下时其 ACL 由 Program Files 继承；若目录不可写（如非管理员运行）则仅输出控制台、不落盘，不影响主流程。
4. **防火墙**：探测使用出站 80/443 + ICMP，不开放任何入站端口，不关闭防火墙。
5. **可逆性**：所有修复动作开始前记录原状态（适配器状态、已连接 SSID），可在日志中审计；不修改系统永久配置（不重置 Winsock/DNS）。
6. **自启动透明**：计划任务名称 `WinNetFix` 清晰可见，可一键 `--uninstall` 移除。

---

## 9. 日志与可观测性

- 滚动日志：`<安装目录>\logs\winnetfix-YYYYMMDD.log`（相对 exe 的 `logs` 目录），按 `retentionDays` 清理。
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
