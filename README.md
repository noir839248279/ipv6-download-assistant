# IPv6 下载助手

Windows 11 桌面工具：按实际中继字节显示 IPv4、IPv6 和代理出口未知流量；普通网站优先 IPv6，已识别下载可严格要求 IPv6。

当前版本：3.0.7。使用 C# / .NET 10 WinForms，原生接管复用独立 Mihomo。

## 能力与边界

- Steam/Epic 的本机代理请求通过 Windows TCP 四元组反查来源进程，避免陌生 CDN 和 HTTP 跳转落入普通网站回退策略。
- 浏览器视频按 CDN 域名归类，不等于逐标签页识别。未知、绕过、第三方代理流量仍可能不受覆盖。
- IPv6-only 是已接管连接策略，不是整机 IPv4 防火墙。计数不是校园网账单。
- 不解密 HTTPS，不安装根证书；报告不保存签名 URL、Cookie、Token 或文件内容。
- 退出恢复原网络，下载可能重新使用 IPv4；应先暂停下载。

## 代理走 Wi-Fi / 恢复默认

主界面读取 Clash Verge 本机控制器的真实状态，每 10 秒刷新。开启时通过 Mihomo `PATCH /configs` 设置 `interface-name` 为检测到的 Wi-Fi 名称，恢复默认时清空此字段；每次写入后回读验证。不会重启 Clash、编辑订阅、修改 Windows Metric 或自动启用被禁用的网卡。

- 开启前必须有 Wi-Fi 默认路由，以太网在线且 IPv4/IPv6 默认路由的 RouteMetric + InterfaceMetric 均严格优于 Wi-Fi。否则拒绝开启并说明原因。恢复默认不受有线掉线限制。
- **这是 Clash 全局出站绑定，Clash 内的 DIRECT 国内网页也受影响。** 节点单独设置的网卡可覆盖全局；不声称所有代理流量绝对走 Wi-Fi。本助手直连与其他非 Clash 流量遵循 Windows 路由；特定路由、其他 VPN/TUN 及既有连接可能不同，不能仅凭默认 Metric 保证所有流量走有线。
- 新连接生效；已建立的连接不会自动迁移或被强制断开。API 写入无需管理员权限，助手原有 TUN 接管仍需要管理员。
- 退出助手不会自动撤销本次 Clash 运行时绑定；点击“恢复默认”解除。Clash 重载、订阅刷新或重启可能重置绑定，界面会同步真实状态。不会将它自动持久化或默认开启。
- 全局接口热切换无法严格实现“仅代理节点 Wi-Fi、Clash DIRECT 有线”。要精确实现，需要节点级 `interface-name`、provider `override` 或 Verge 扩展配置及重载，本版未改写这些配置。

## 构建

安装 .NET 10 SDK，在 Windows 中运行：

```powershell
./build.ps1 -Mihomo 'C:/path/to/mihomo.exe'
```

产物位于 app/，运行 IPv6Lens.exe。正常接管需要管理员权限。Mihomo 采用 v1.19.10；分发需遵守附带 GPLv3 许可和对应源码提供要求，见 THIRD-PARTY-NOTICES.md。

固定应用图标：src/assets/app.ico，后续版本沿用。

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

用户要求使用本账号的私有 GitHub 仓库存放后续更新。每次修改完成相应测试后同步源码、必要资源和构建说明。不得上传运行日志、签名下载链接、个人代理配置、凭据或用户文件。桌面继续原位更新既有目录和快捷方式，不按版本新增目录。

3.0.4：每栏独立显示IPv4、IPv6、未知出口的实时接收速度与累计量；未知仍指原代理出口地址族无法确认。报告增加三类速度字段，清零立即重置速度和累计。


3.0.5：三类流量均显示上传/下载实时速度和累计值。上行计数包括经过助手的TLS/HTTP流数据，普通HTTP头也计入上传；不是网卡计费或文件净大小。IPv6占比仍为下载占比。


3.0.6：两列三行卡片，每卡数据按IPv4/IPv6/未知列与上传下载速度/累计行对齐；状态放在底部。用户明确要求更新桌面版本号时，重命名现有目录和快捷方式。

3.0.7：新增代理走 Wi-Fi/恢复默认开关、Clash API 鉴权与回读、真实网卡/默认路由检查、定时同步及错误提示。DIRECT 范围、既有连接和网卡掉线限制均明确展示。

