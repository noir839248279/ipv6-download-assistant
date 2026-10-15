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

本包复用本机已安装 Clash Verge 的 Mihomo Meta v1.19.10 Windows amd64 内核，未修改二进制，通过独立进程及本地 HTTP API 工作。GPL-3.0 许可证和对应官方版本源码归档见 third-party/。

来源：https://github.com/MetaCubeX/mihomo/tree/v1.19.10
源码归档：https://codeload.github.com/MetaCubeX/mihomo/zip/refs/tags/v1.19.10
3.0.8 的分流功能只热重载用户 Clash 运行配置，不改写订阅文件；本机恢复快照采用 Windows DPAPI 加密，不上传用户配置或 API 密钥。未复制其他闭源 CDN 工具的实现。

## YamlDotNet 16.3.0

Used to preserve structured Clash configuration during runtime splitting.
Source: https://github.com/aaubry/YamlDotNet/tree/v16.3.0
License: MIT (the permission and disclaimer text above also applies).
Copyright (c) Antoine Aubry and contributors
