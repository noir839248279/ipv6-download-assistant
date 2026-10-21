# IPv6 下载助手

Windows 11 桌面工具：按实际中继字节显示 IPv4、IPv6 和代理出口未知流量；普通网站优先 IPv6，已识别下载可严格要求 IPv6。

当前版本：3.0.10。使用 C# / .NET 10 WinForms，原生接管复用独立 Mihomo。

## 下载安装

从 [GitHub Releases](https://github.com/noir839248279/ipv6-download-assistant/releases/latest) 下载 `IPv6-Download-Assistant-*-win-x64-portable.zip`，解压到固定目录后运行其中的 `IPv6Lens.exe`，允许管理员权限提示。适用于 Windows 11 x64；已包含 .NET 运行时和独立内核，无需安装 .NET 或 WSL。整个文件夹须保留，不能只移动主程序。

Wi-Fi / 有线双线分流默认关闭，仅在手动点击后开启。启动时读取 Clash 当前真实状态，不会自动设置 Wi-Fi 绑定；若 Clash 已被其他工具绑定，界面如实显示。其他 IPv6 下载开关仍默认开启。使用前先暂停现有下载，开启后重新开始；退出前也应暂停下载，避免恢复原路径后产生 IPv4 流量。

## 能力与边界

- Steam/Epic 的本机代理请求通过 Windows TCP 四元组反查来源进程，避免陌生 CDN 和 HTTP 跳转落入普通网站回退策略。
- 浏览器视频按 CDN 域名归类，不等于逐标签页识别。未知、绕过、第三方代理流量仍可能不受覆盖。
- IPv6-only 是已接管连接策略，不是整机 IPv4 防火墙。计数不是校园网账单。
- 不解密 HTTPS，不安装根证书；报告不保存签名 URL、Cookie、Token 或文件内容。
- 退出恢复原网络，下载可能重新使用 IPv4；应先暂停下载。

## 代理走 Wi-Fi / 恢复默认

主界面的分流按钮现在执行设置，不再要求用户手动调好路由。

- 开启：读取原值，临时调整两张物理网卡 IPv4/IPv6 的 ActiveStore 接口开销，使有线默认路由总开销更低；同时热重载 Clash 运行配置，将全局/DIRECT 绑定有线，将代理节点和 proxy-provider override 绑定 Wi-Fi。原有节点选择保留，订阅文件不修改。
- 当前 Clash 开着 TUN 时，分流期间自动停用其 TUN，避免与助手自己的接管冲突；关闭恢复原 TUN 状态。恢复 Clash TUN 前会暂停助手下载接管，避免两套 TUN 叠加。
- 关闭、正常退出：恢复原路由开销、自动开销开关以及 Clash 原运行配置。失败执行回滚；异常退出由独立守护进程尝试恢复，未完成的恢复在下次启动重试。Windows 路由调整仅作用于 ActiveStore，不改变重启后的持久配置。
- 需要管理员权限（助手原有网络接管同样需要）。热重载需要数秒，已有连接不会自动迁移，下载请暂停后重新开始。
- 两张网卡必须在线，有线应有 IPv4/IPv6 默认路由；自动调整优先级无需另行设置。无线/有线失联、Clash 订阅重载会显示需要恢复，不冒充仍然正常。
- 配置与恢复快照使用 Windows 当前用户 DPAPI 加密，仅保存在本机，不进入诊断报告或 GitHub。恢复时尽量保留用户后续路由或订阅更改。
- 适用 Clash 规则模式。第三方 VPN、特定路由、应用自身网卡绑定、localhost 二级代理不在全流量保证范围；遇到无法安全处理的本地二级代理或混合 DIRECT 提供器，拒绝修改并说明原因。

## 构建

安装 .NET 10 SDK，在 Windows 中运行：

```powershell
./build.ps1 -Mihomo 'C:/path/to/mihomo.exe'
```

产物位于 app/，运行 IPv6Lens.exe。正常接管需要管理员权限。Mihomo 采用 v1.19.10；分发需遵守附带 GPLv3 许可和对应源码提供要求，见 THIRD-PARTY-NOTICES.md。

固定应用图标：src/assets/app.ico，后续版本沿用。

制作同样的免安装包：

```powershell
./package.ps1 -Mihomo 'C:/path/to/mihomo.exe' -MihomoSource 'C:/path/to/mihomo-v1.19.10-source.zip'
```

脚本生成 `releases/` 中的 ZIP，按固定文件清单打包，不包含本机配置和日志。

## 本地回归

```powershell
./app/IPv6Lens.exe --redirect-test relay.json
./app/IPv6Lens.exe --relay-resilience-test resilience.json
./app/IPv6Lens.exe --redirect-lease-test lease.json
./app/IPv6Lens.exe --native-config-test config.json
./app/IPv6Lens.exe --clash-interface-test clash-unit.json
./app/IPv6Lens.exe --clash-interface-core-test clash-core.json
./app/IPv6Lens.exe --clash-interface-probe clash-probe.json
```

前两项使用本地服务；lease 测试临时修改并恢复系统 PAC，应先退出助手；native-config 只验证独立内核配置，不开启 TUN。进程测试使用同名 curl 探针，不冒充实际游戏更新验收。

## 维护约定

用户已要求公开本仓库，后续更新在本仓库同步并通过 Releases 提供下载。每次修改完成相应测试后同步源码、必要资源和构建说明。不得上传运行日志、签名下载链接、个人代理配置、凭据或用户文件。桌面继续原位更新既有目录和快捷方式，不按版本新增目录。

3.0.4：每栏独立显示IPv4、IPv6、未知出口的实时接收速度与累计量；未知仍指原代理出口地址族无法确认。报告增加三类速度字段，清零立即重置速度和累计。


3.0.5：三类流量均显示上传/下载实时速度和累计值。上行计数包括经过助手的TLS/HTTP流数据，普通HTTP头也计入上传；不是网卡计费或文件净大小。IPv6占比仍为下载占比。


3.0.6：两列三行卡片，每卡数据按IPv4/IPv6/未知列与上传下载速度/累计行对齐；状态放在底部。用户明确要求更新桌面版本号时，重命名现有目录和快捷方式。

3.0.7：新增代理走 Wi-Fi/恢复默认开关、Clash API 鉴权与回读、真实网卡/默认路由检查、定时同步及错误提示。DIRECT 范围、既有连接和网卡掉线限制均明确展示。


3.0.8：自动调整有线优先，节点级 Wi-Fi 绑定，DIRECT 留在有线；加入路由/配置事务回滚、退出恢复和异常退出守护。

专项验证：`--split-test <报告路径>` 不修改用户网络；`--split-live-test <报告路径>` 需要管理员权限、双网在线且无助手运行，临时启用并恢复真实设置，包含失败注入和异常退出恢复，不应在下载中运行。


3.0.9：UI 现代化全面重构。顶栏内嵌专属头像图标与操作区；双网卡分流 Hero 控制卡清晰展示以太网/Wi-Fi 双通道拓扑；全局宏观流量条汇总；6 大平台监控卡片升级为 3 列独立度量徽章卡片（IPv6 / IPv4 / 未知出口），均独立展示下载与上传实时速度及累计总量。

3.0.10：取消启动时自动绑定 Wi-Fi，双线分流由用户手动开启；提供 Windows x64 免安装发布包。
