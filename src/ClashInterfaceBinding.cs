using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal sealed record ClashControllerSettings(Uri BaseUri, string Secret);
internal sealed record AdapterSnapshot(string Name, string Medium, bool IsUp);
internal sealed record DefaultRouteSnapshot(string Adapter, string Family, int Metric);

internal sealed record ClashNetworkSnapshot(
    string WifiName,
    string EthernetName,
    bool WifiConnected,
    bool EthernetConnected,
    int? WifiIpv4Metric,
    int? WifiIpv6Metric,
    int? EthernetIpv4Metric,
    int? EthernetIpv6Metric)
{
    public bool EthernetFirstIpv4 => EthernetIpv4Metric.HasValue &&
        (!WifiIpv4Metric.HasValue || EthernetIpv4Metric.Value < WifiIpv4Metric.Value);

    public bool EthernetFirstIpv6 => EthernetIpv6Metric.HasValue &&
        (!WifiIpv6Metric.HasValue || EthernetIpv6Metric.Value < WifiIpv6Metric.Value);

    public bool CanEnable => WifiConnected && EthernetConnected &&
        (WifiIpv4Metric.HasValue || WifiIpv6Metric.HasValue) &&
        EthernetFirstIpv4 && EthernetFirstIpv6;

    public bool DualNicReady => CanEnable;
    public bool WifiReady => WifiConnected && (WifiIpv4Metric.HasValue || WifiIpv6Metric.HasValue);

    public string EnableBlockReason
    {
        get
        {
            if (!WifiConnected) return "没有已连接的 Wi-Fi 网卡，无法将 Clash 绑定到无线网。";
            if (!EthernetConnected) return "有线网卡当前未连接或已禁用。请先连接并启用以太网，再开启分流。";
            if (!WifiIpv4Metric.HasValue && !WifiIpv6Metric.HasValue)
                return "Wi-Fi 当前没有可用的默认路由，无法确认代理能够通过无线网访问外网。";
            if (!EthernetIpv4Metric.HasValue)
                return "以太网没有 IPv4 默认路由，无法确认其他 IPv4 流量会走有线网。";
            if (!EthernetIpv6Metric.HasValue)
                return "以太网没有 IPv6 默认路由，不能保证 IPv6 下载走有线网。";
            if (!EthernetFirstIpv4 || !EthernetFirstIpv6)
                return "Windows 当前没有同时优先使用以太网的 IPv4 和 IPv6 默认路由。为避免流量走错网卡，未开启分流。";
            return "";
        }
    }

    public string Summary
    {
        get
        {
            var wifi = WifiConnected
                ? $"{WifiName} 已连接（IPv4 {MetricText(WifiIpv4Metric)}，IPv6 {MetricText(WifiIpv6Metric)}）"
                : "Wi-Fi 未连接";
            var ethernet = EthernetConnected
                ? $"{EthernetName} 已连接（IPv4 {MetricText(EthernetIpv4Metric)}，IPv6 {MetricText(EthernetIpv6Metric)}）"
                : "以太网未连接/已禁用";
            return $"{wifi}；{ethernet}";
        }
    }

    static string MetricText(int? metric) => metric.HasValue ? metric.Value.ToString() : "无默认路由";
}

internal sealed record ClashBindingSnapshot(string InterfaceName, ClashNetworkSnapshot Network)
{
    public bool IsWifiBound => !string.IsNullOrEmpty(Network.WifiName) && string.Equals(InterfaceName, Network.WifiName, StringComparison.OrdinalIgnoreCase);
    public bool IsDefault => string.IsNullOrEmpty(InterfaceName);
}

internal sealed class ClashInterfaceBinding
{
    readonly Func<ClashControllerSettings> settingsReader;
    readonly Func<HttpMessageHandler> handlerFactory;
    readonly Func<ClashNetworkSnapshot> networkReader;

    internal ClashInterfaceBinding()
        : this(ReadControllerSettings, () => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, ClashNetworkProbe.Read)
    {
    }

    internal ClashInterfaceBinding(
        Func<ClashControllerSettings> settingsReader,
        Func<HttpMessageHandler> handlerFactory,
        Func<ClashNetworkSnapshot> networkReader)
    {
        this.settingsReader = settingsReader;
        this.handlerFactory = handlerFactory;
        this.networkReader = networkReader;
    }

    internal async Task<ClashBindingSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsReader();
        using var client = CreateClient(settings);
        using var response = await client.GetAsync("configs", cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
        var value = InterfaceValue(document.RootElement);
        var network = await Task.Run(networkReader, cancellationToken).ConfigureAwait(false);
        return new(value, network);
    }

    internal async Task SetWifiAsync(CancellationToken cancellationToken = default)
    {
        var network = await Task.Run(networkReader, cancellationToken).ConfigureAwait(false);
        if (!network.CanEnable) throw new InvalidOperationException(network.EnableBlockReason);
        await SetInterfaceAsync(network.WifiName, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RestoreDefaultAsync(CancellationToken cancellationToken = default)
    {
        await SetInterfaceAsync("", cancellationToken).ConfigureAwait(false);
    }

    HttpClient CreateClient(ClashControllerSettings settings)
    {
        var client = new HttpClient(handlerFactory(), disposeHandler: true) { BaseAddress = settings.BaseUri, Timeout = TimeSpan.FromSeconds(3) };
        if (!string.IsNullOrEmpty(settings.Secret))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Secret);
        return client;
    }

    async Task SetInterfaceAsync(string interfaceName, CancellationToken cancellationToken)
    {
        var settings = settingsReader();
        using var client = CreateClient(settings);
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), "configs")
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["interface-name"] = interfaceName }), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);

        var actual = await ReadInterfaceAsync(client, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, interfaceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clash Verge 回读的网卡与请求不一致，无法确认切换成功。请查看当前状态或点击恢复默认。");
    }

    static async Task<string> ReadInterfaceAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("configs", cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
        return InterfaceValue(document.RootElement);
    }
    static string InterfaceValue(JsonElement root) => root.TryGetProperty("interface-name", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidOperationException("Clash API 未返回网卡字段，当前内核可能不支持此开关；未确认默认状态。");

    static Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var code = (int)response.StatusCode;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Clash Verge 控制器鉴权失败。请重新启动 Clash Verge 后重试。",
            HttpStatusCode.NotFound => "Clash Verge 控制器未提供所需的 Mihomo API。",
            _ => $"Clash Verge 控制器返回 HTTP {code}。请检查 Clash Verge 是否正常运行。"
        };
        throw new InvalidOperationException(message);
    }

    internal static ClashControllerSettings ReadControllerSettings()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.clash-verge-rev.clash-verge-rev");
        foreach (var name in new[] { "clash-verge-runtime.yaml", "clash-verge.yaml", "config.yaml", "verge.yaml" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            string? controller = ReadRootScalar(path, "external-controller");
            if (string.IsNullOrWhiteSpace(controller)) continue;
            var secret = ReadRootScalar(path, "secret") ?? "";
            if (!Uri.TryCreate(controller.Contains("://", StringComparison.Ordinal) ? controller : "http://" + controller, UriKind.Absolute, out var uri))
                continue;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !IsLoopback(uri.Host.Trim('[', ']')) || uri.Port < 1 || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("Clash Verge 控制器必须是本机 HTTP 地址；出于安全考虑，不会访问远程控制器。");
            return new(new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" }.Uri, secret);
        }
        throw new InvalidOperationException("未找到 Clash Verge 的本机 Mihomo 控制器配置。请先启动 Clash Verge。");
    }

    static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    static string? ReadRootScalar(string path, string key)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            var prefix = key + ":";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = line[prefix.Length..].Trim();
            if (value.StartsWith('"'))
            {
                // Generated Verge files use the JSON-compatible YAML scalar subset.
                try { return JsonSerializer.Deserialize<string>(value); }
                catch(JsonException) { throw new InvalidOperationException("Clash 控制器字段使用了暂不支持的 YAML 格式，未发送请求。"); }
            }
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            return comment >= 0 ? value[..comment].TrimEnd() : value;
        }
        return null;
    }
}

internal static class ClashNetworkProbe
{
    const string ProbeScript = """
        $ErrorActionPreference='Stop'
        $adapters=@(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | ForEach-Object {
          [pscustomobject]@{Name=$_.Name;Medium=[string]$_.PhysicalMediaType;IsUp=($_.Status -eq 'Up')}
        })
        $interfaces=Get-NetIPInterface -AddressFamily IPv4,IPv6 -ErrorAction SilentlyContinue | Where-Object ConnectionState -eq 'Connected'
        $routes=Get-NetRoute -AddressFamily IPv4,IPv6 -DestinationPrefix '0.0.0.0/0','::/0' -ErrorAction SilentlyContinue
        $paths=@(foreach($route in $routes) {
          $iface=$interfaces | Where-Object { $_.InterfaceIndex -eq $route.InterfaceIndex -and $_.AddressFamily -eq $route.AddressFamily } | Select-Object -First 1
          if($iface) {
            $adapter=$adapters | Where-Object Name -eq $iface.InterfaceAlias | Select-Object -First 1
            if($adapter) {
              [pscustomobject]@{Adapter=$adapter.Name;Medium=$adapter.Medium;Family=[string]$route.AddressFamily;Metric=([int]$iface.InterfaceMetric+[int]$route.RouteMetric)}
            }
          }
        })
        ConvertTo-Json -InputObject ([pscustomobject]@{Adapters=$adapters;Routes=$paths}) -Compress
        """;

    internal static ClashNetworkSnapshot Read()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::OutputEncoding=[Text.UTF8Encoding]::new();" + ProbeScript);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法读取 Windows 网卡和默认路由状态。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("读取 Windows 网卡状态超时。");
        }
        var output = outputTask.GetAwaiter().GetResult();
        _ = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("无法读取 Windows 网卡和默认路由状态。");

        using var json = JsonDocument.Parse(output);
        var adapters = json.RootElement.GetProperty("Adapters").EnumerateArray().Select(x => new AdapterSnapshot(
            x.GetProperty("Name").GetString() ?? "",
            x.GetProperty("Medium").GetString() ?? "",
            x.GetProperty("IsUp").GetBoolean())).ToArray();
        var routes = json.RootElement.GetProperty("Routes").EnumerateArray().Select(x => new DefaultRouteSnapshot(
            x.GetProperty("Adapter").GetString() ?? "",
            x.GetProperty("Family").GetString() ?? "",
            x.GetProperty("Metric").GetInt32())).ToArray();
        return Evaluate(adapters, routes);
    }

    internal static ClashNetworkSnapshot Evaluate(AdapterSnapshot[] adapters, DefaultRouteSnapshot[] routes)
    {
        static bool IsWifi(AdapterSnapshot adapter) =>
            adapter.Medium.Contains("802.11", StringComparison.OrdinalIgnoreCase) ||
            adapter.Medium.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
            adapter.Name.Equals("WLAN", StringComparison.OrdinalIgnoreCase) ||
            adapter.Name.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase);

        static bool IsEthernet(AdapterSnapshot adapter) =>
            !IsWifi(adapter) &&
            (adapter.Medium.Contains("802.3", StringComparison.OrdinalIgnoreCase) ||
             adapter.Medium.Contains("ethernet", StringComparison.OrdinalIgnoreCase) ||
             adapter.Name.Equals("以太网", StringComparison.OrdinalIgnoreCase) ||
             adapter.Name.Equals("Ethernet", StringComparison.OrdinalIgnoreCase));

        var wifi = adapters.Where(IsWifi).OrderByDescending(x => x.IsUp).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var ethernet = adapters.Where(IsEthernet).OrderByDescending(x => x.IsUp).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var wifiName = wifi?.Name ?? "";
        var ethernetName = ethernet?.Name ?? "";
        int? Metric(string name, string family) => routes.Where(x =>
            x.Adapter.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            x.Family.Equals(family, StringComparison.OrdinalIgnoreCase)).Select(x => (int?)x.Metric).Min();

        return new(
            wifiName,
            ethernetName,
            wifi?.IsUp == true,
            ethernet?.IsUp == true,
            Metric(wifiName, "IPv4"),
            Metric(wifiName, "IPv6"),
            Metric(ethernetName, "IPv4"),
            Metric(ethernetName, "IPv6"));
    }
}
