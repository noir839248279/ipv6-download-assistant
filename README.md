# IPv6 下载助手

Windows 11 桌面工具：按实际中继字节显示 IPv4、IPv6 和代理出口未知流量；普通网站优先 IPv6，已识别下载可严格要求 IPv6。

当前版本：3.0.4。使用 C# / .NET 10 WinForms，原生接管复用独立 Mihomo。

## 能力与边界

- Steam/Epic 的本机代理请求通过 Windows TCP 四元组反查来源进程，避免陌生 CDN 和 HTTP 跳转落入普通网站回退策略。
- 浏览器视频按 CDN 域名归类，不等于逐标签页识别。未知、绕过、第三方代理流量仍可能不受覆盖。
- IPv6-only 是已接管连接策略，不是整机 IPv4 防火墙。计数不是校园网账单。
- 不解密 HTTPS，不安装根证书；报告不保存签名 URL、Cookie、Token 或文件内容。
- 退出恢复原网络，下载可能重新使用 IPv4；应先暂停下载。

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
```

前两项使用本地服务；lease 测试临时修改并恢复系统 PAC，应先退出助手；native-config 只验证独立内核配置，不开启 TUN。进程测试使用同名 curl 探针，不冒充实际游戏更新验收。

## 维护约定

用户要求使用本账号的私有 GitHub 仓库存放后续更新。每次修改完成相应测试后同步源码、必要资源和构建说明。不得上传运行日志、签名下载链接、个人代理配置、凭据或用户文件。桌面继续原位更新既有目录和快捷方式，不按版本新增目录。

3.0.4：每栏独立显示IPv4、IPv6、未知出口的实时接收速度与累计量；未知仍指原代理出口地址族无法确认。报告增加三类速度字段，清零立即重置速度和累计。

