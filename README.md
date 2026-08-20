# WinNetFix — Windows 网络自愈工具

常驻后台、零依赖、极小资源占用的 Windows 网络修复工具。主用有线网络，WiFi 作为常备通道自动守护。

## 功能

- **有线卡死自愈**：周期性探测有线（以太网）连通性，卡死（适配器 Up 但无网）时自动禁用/启用网卡恢复。
- **WiFi 例行守护**：每个监测周期检查 WiFi，**未连接（含软件开关关闭）时自动打开开关并连接已存 SSID**；已连接则不触碰。
- **DNS 分层诊断与修复**：网关/外网 ICMP → DNS 解析 → 应用 HTTP 分层判定；DNS 故障自动 `ipconfig /flushdns`，仍不通则临时切换以太网 IPv4 到公共 DNS（8.8.8.8 / 223.5.5.5，不改回）。
- **应用层故障不误修**：链路/DNS 正常但 HTTP 被拦（SSL/防火墙）时不做无意义重启，仅记录观察。
- **UAC 自动提权**：需要管理员权限时自动弹窗提权重启，无需手动右键。

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)。

```powershell
# 调试构建
dotnet build -c Debug

# 单文件发布（self-contained，免装 .NET）
.\publish.ps1
# 产物在 publish/WinNetFix.exe
```

## 用法

> 所有操作均需管理员权限（工具会自动触发 UAC 提权）。

```powershell
# 常驻运行（默认）：隐藏窗口，后台守护
WinNetFix.exe --run

# 注册开机自启计划任务（登录时以最高权限运行）
WinNetFix.exe --install

# 注销计划任务
WinNetFix.exe --uninstall

# 执行一次完整探测+修复流程（调试）
WinNetFix.exe --once

# 查看当前网络状态（只读，无需提权）
WinNetFix.exe --status

# 手动打开/关闭 WiFi 软件开关（供测试与后续功能）
WinNetFix.exe --wifi-on
WinNetFix.exe --wifi-off
```

## 配置文件

默认路径：`%ProgramData%\WinNetFix\config.json`（首次运行自动生成）。

```jsonc
{
  "probe": {
    "intervalSec": 15,          // 探测间隔（秒）
    "failThreshold": 2,         // 连续失败阈值
    "timeoutMs": 3000,          // 单次探测超时
    "gatewayIcmp": true,        // 网关 ICMP（判定本地链路）
    "publicIcmpTarget": "8.8.8.8",
    "dnsHttpUrl": "https://www.msftconnecttest.com/connecttest.txt"
  },
  "repair": {
    "restartAdapter": true,     // 有线卡死时重启网卡
    "flushDns": true,           // DNS 故障先刷缓存
    "fallbackPublicDns": true,  // 刷缓存无效则切公共 DNS（仅以太网 IPv4，不改回）
    "publicDns": ["8.8.8.8", "223.5.5.5"],
    "reconnectWifi": true,      // WiFi 例行守护
    "wifiRetryCooldownSec": 60, // WiFi 连接失败冷却期
    "preferredSsid": "",        // 空 = 用最后连接/首个 profile
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

## 行为说明与限制

- **必须管理员权限**：重启网卡、开 WiFi 等操作需要管理员。`--run` 非管理员时会提示，修复类操作会失败。
- **飞行模式（Hardware Off）**：系统锁定无线 radio，任何软件都无法自动打开。工具会检测并提示手动开启，不做强行绕过。
- **WiFi 开关（Software Off）**：工具可自动打开/关闭（通过 Windows WLAN API）。
- **切公共 DNS 不改回**：DNS 故障时以太网 IPv4 会切到公共 DNS 且不还原（原 DNS 本就有问题，切公共是改善）。若介意可在配置关闭 `fallbackPublicDns`。
- **不主动切回有线**：WiFi 连上后与有线双连接共存，工具不干预。

## 日志

`%ProgramData%\WinNetFix\logs\winnetfix-YYYYMMDD.log`，按天滚动，默认保留 30 天。

## 设计文档

详见 [docs/design.md](docs/design.md)。
