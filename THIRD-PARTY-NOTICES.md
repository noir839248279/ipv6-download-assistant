# Third-party components

The application uses Microsoft .NET 10 / Windows Forms / ASP.NET Core (MIT), Microsoft.Diagnostics.Tracing.TraceEvent 3.1.28 and its dependencies (see the restored NuGet dependency graph).

- .NET license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Windows Forms license: https://github.com/dotnet/winforms/blob/main/LICENSE.TXT
- ASP.NET Core license: https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt
- TraceEvent / PerfView license: https://github.com/microsoft/perfview/blob/main/LICENSE.TXT

MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.


## Mihomo

本软件通过独立的 `core/mihomo.exe` 进程完成本地网络接管与规则分流，主程序通过本地配置和控制接口与内核通信。

发布包使用 Mihomo Meta v1.19.10（Windows amd64），未修改内核二进制。Mihomo 采用 GNU General Public License v3.0（GPL-3.0）授权。完整许可证见 `third-party/mihomo-LICENSE.txt`，该版本的官方源码归档见 `third-party/mihomo-v1.19.10-source.zip`。

来源：https://github.com/MetaCubeX/mihomo/tree/v1.19.10
源码归档：https://codeload.github.com/MetaCubeX/mihomo/zip/refs/tags/v1.19.10
双网卡分流热重载本机运行配置，不改写订阅文件。恢复记录保存在当前电脑，使用当前 Windows 用户的 DPAPI 加密，用于还原网络设置，不随诊断报告导出。

主程序还使用 YamlDotNet 16.3.0、Microsoft.Diagnostics.Tracing.TraceEvent 3.1.28 和 .NET 10。各组件的来源与许可见本文件相应条目。

## YamlDotNet 16.3.0

Used to preserve structured Clash configuration during runtime splitting.
Source: https://github.com/aaubry/YamlDotNet/tree/v16.3.0
License: MIT (the permission and disclaimer text above also applies).
Copyright (c) Antoine Aubry and contributors
