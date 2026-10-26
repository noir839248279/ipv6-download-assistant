using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IPv6Lens;

internal sealed record NodeAddress(IPAddress Address, DateTime ExpiresUtc, string Source);
internal sealed record ContentNode(NodeAddress Dns, double? LatencyMs = null, double? BytesPerSecond = null,
    string Status = "未测试", DateTime? MeasuredUtc = null);
internal enum NodeOrder { Latency, Speed }
internal sealed record NodeSpeed(double BytesPerSecond, string Status);
internal sealed class NodeRefusedException(int status) : IOException("资源拒绝测速（HTTP " + status + "）")
{
    internal int StatusCode => status;
}

internal sealed class ContentNodes
{
    readonly object gate = new();
    readonly Dictionary<string, List<ContentNode>> hosts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IPAddress> selected = new(StringComparer.OrdinalIgnoreCase);
    internal Func<DateTime> Now = () => DateTime.UtcNow;
    internal Func<string, CancellationToken, Task<NodeAddress[]>> Lookup = (_, _) => Task.FromResult(Array.Empty<NodeAddress>());
    internal Func<string, IPAddress, CancellationToken, Task<double>> LatencyProbe;
    internal Func<Uri, IPAddress, IReadOnlyDictionary<string, string>, CancellationToken, Task<NodeSpeed>> SpeedProbe;
    internal int InterfaceIndex { get; set; }
    internal ContentNodes()
    {
        LatencyProbe = (host, ip, ct) => NodeNetwork.Latency(host, ip, InterfaceIndex, ct);
        SpeedProbe = (url, ip, headers, ct) => NodeNetwork.Speed(url, ip, InterfaceIndex, headers, ct);
    }
    internal static string Host(string value)
    {
        value = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns ||
            DownloadRouter.IsControlHost(value) || value.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请输入有效的内容域名；登录、目录接口和本地域名不能测速");
        return value;
    }
    internal static bool PublicV6(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 &&
        !ip.IsIPv4MappedToIPv6 && TrafficStore.Scope(ip) == "公网";
    void Prune(string host)
    {
        if (hosts.TryGetValue(host, out var rows))
        {
            rows.RemoveAll(n => n.Dns.ExpiresUtc <= Now());
            if (selected.TryGetValue(host, out var ip) && rows.All(n => !n.Dns.Address.Equals(ip))) selected.Remove(host);
        }
    }
    internal ContentNode[] Snapshot(string host)
    {
        host = Host(host);
        lock (gate) { Prune(host); return hosts.TryGetValue(host, out var rows) ? rows.ToArray() : Array.Empty<ContentNode>(); }
    }
    internal async Task Refresh(string host, CancellationToken ct)
    {
        host = Host(host);
        var addresses = await Lookup(host, ct);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            Prune(host);
            if (!hosts.ContainsKey(host) && hosts.Count >= 100) throw new IOException("节点域名数量已达上限");
            var old = hosts.GetValueOrDefault(host) ?? new();
            var merged = old.ToDictionary(n => n.Dns.Address);
            foreach (var entry in addresses.Where(a => PublicV6(a.Address) && a.ExpiresUtc > Now()))
            {
                var expires = entry.ExpiresUtc < Now().AddMinutes(5) ? entry.ExpiresUtc : Now().AddMinutes(5);
                var dns = entry with { ExpiresUtc = expires };
                merged[entry.Address] = merged.TryGetValue(entry.Address, out var prior) &&
                    prior.MeasuredUtc >= Now().AddMinutes(-5) ? prior with { Dns = dns } : new(dns);
            }
            var keep = selected.GetValueOrDefault(host);
            hosts[host] = merged.Values.OrderByDescending(n => n.Dns.Address.Equals(keep))
                .ThenByDescending(n => n.Dns.ExpiresUtc).Take(8).ToList();
        }
    }
    internal static ContentNode[] Rank(IEnumerable<ContentNode> source, NodeOrder order) =>
        (order == NodeOrder.Speed
            ? source.OrderBy(n => !n.BytesPerSecond.HasValue).ThenByDescending(n => n.BytesPerSecond)
            : source.OrderBy(n => !n.LatencyMs.HasValue).ThenBy(n => n.LatencyMs))
        .ThenBy(n => n.Dns.Address.ToString(), StringComparer.Ordinal).ToArray();
    internal void Select(string host, IPAddress ip)
    {
        host = Host(host);
        lock (gate)
        {
            Prune(host);
            var row = hosts.GetValueOrDefault(host)?.SingleOrDefault(n => n.Dns.Address.Equals(ip));
            if (row == null || !PublicV6(ip))
                throw new InvalidOperationException("节点已过期，请刷新节点");
            selected[host] = ip;
        }
    }
    internal IPAddress? Selected(string host)
    {
        lock (gate) { Prune(host); return selected.GetValueOrDefault(host); }
    }
    internal void Automatic(string host) { lock (gate) selected.Remove(Host(host)); }
    internal void Best(string host, NodeOrder order)
    {
        var best = Rank(Snapshot(host), order).FirstOrDefault(n => order == NodeOrder.Speed
            ? n.BytesPerSecond.HasValue : n.LatencyMs.HasValue);
        if (best == null) throw new InvalidOperationException(order == NodeOrder.Speed ? "没有有效下载测速结果" : "没有有效延迟结果");
        Select(host, best.Dns.Address);
    }
    internal static Uri SpeedUrl(string host, string text)
    {
        host = Host(host);
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.Port != 443 || url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
            !string.Equals(url.IdnHost, host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请粘贴当前域名实际文件的 HTTPS 下载链接（必须 443 端口）；网页首页不能测速");
        return url;
    }
    internal static Dictionary<string, string> Headers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(StringComparer.OrdinalIgnoreCase);
        if (text.Length > 16384) throw new InvalidOperationException("请求头过长");
        Dictionary<string, string>? parsed;
        try { parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text); }
        catch (JsonException) { throw new InvalidOperationException("请求头需要是 JSON 字符串对象"); }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parsed ?? new())
        {
            if (!new[] { "Cookie", "User-Agent", "Referer", "Origin", "Authorization" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) ||
                pair.Value == null || pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidOperationException("仅支持 Cookie、User-Agent、Referer、Origin、Authorization 请求头");
            result[pair.Key] = pair.Value;
        }
        return result;
    }
    internal async Task Measure(string host, NodeOrder order, Uri? url, IReadOnlyDictionary<string, string> headers,
        IProgress<int>? progress, CancellationToken ct)
    {
        host = Host(host);
        if (order == NodeOrder.Speed) url = SpeedUrl(host, url?.AbsoluteUri ?? "");
        int completed = 0;
        foreach (var row in Snapshot(host))
        {
            ct.ThrowIfCancellationRequested();
            if (row.Dns.ExpiresUtc <= Now()) continue;
            ContentNode result = row with { LatencyMs = null, BytesPerSecond = null, Status = "测试失败", MeasuredUtc = Now() };
            try
            {
                if (order == NodeOrder.Latency)
                    result = row with { LatencyMs = await LatencyProbe(host, row.Dns.Address, ct), Status = "TLS 可达", MeasuredUtc = Now() };
                else
                {
                    var speed = await SpeedProbe(url!, row.Dns.Address, headers, ct);
                    result = row with { BytesPerSecond = speed.BytesPerSecond, Status = speed.Status, MeasuredUtc = Now() };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (NodeRefusedException)
            {
                Store(host, result with { Status = "资源拒绝，测速已停止" });
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or
                System.Security.Authentication.AuthenticationException or OperationCanceledException or TimeoutException)
            { result = result with { Status = ex is OperationCanceledException or TimeoutException ? "超时" : "连接或资源失败" }; }
            Store(host, result);
            progress?.Report(++completed);
        }
    }
    void Store(string host, ContentNode row)
    {
        lock (gate)
        {
            Prune(host);
            if (!hosts.TryGetValue(host, out var rows)) return;
            int index = rows.FindIndex(n => n.Dns.Address.Equals(row.Dns.Address));
            if (index >= 0) rows[index] = row with { Dns = rows[index].Dns };
            if (!row.LatencyMs.HasValue && !row.BytesPerSecond.HasValue &&
                selected.GetValueOrDefault(host)?.Equals(row.Dns.Address) == true) selected.Remove(host);
        }
    }
}

internal static class NodeNetwork
{
    internal const int ByteLimit = 4 * 1024 * 1024;
    [DllImport("iphlpapi.dll")] static extern uint GetBestInterfaceEx(byte[] address, out uint index);
    internal static int CaptureIPv4Interface()
    {
        var address = new IPEndPoint(IPAddress.Parse("223.5.5.5"), 443).Serialize();
        var bytes = new byte[address.Size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = address[i];
        return GetBestInterfaceEx(bytes, out uint index) == 0 && PhysicalIPv4Interface((int)index) != null ? (int)index : 0;
    }
    static NetworkInterface? PhysicalIPv4Interface(int index) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
    {
        try
        {
            return n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                !n.Name.Contains("TUN", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) &&
                n.GetIPProperties().GetIPv4Properties()?.Index == index;
        }
        catch (NetworkInformationException) { return false; }
    });
    internal static async Task<TcpClient> ConnectIPv4(IPAddress ip, int port, int interfaceIndex, CancellationToken ct)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any))
            throw new IOException("辅助目标不是可用 IPv4 地址");
        var nic = PhysicalIPv4Interface(interfaceIndex) ?? throw new IOException("原 IPv4 出口不可用");
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            // Windows IP_UNICAST_IF takes the IPv4 interface index in network byte order.
            client.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(interfaceIndex));
            await client.ConnectAsync(ip, port, ct);
            var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
            if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local))) throw new IOException("辅助出口验证失败");
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    internal static int CaptureInterface()
    {
        var address = new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 443).Serialize();
        var bytes = new byte[address.Size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = address[i];
        if (GetBestInterfaceEx(bytes, out uint index) != 0) return 0;
        return PhysicalInterface((int)index) == null ? 0 : (int)index;
    }
    static NetworkInterface? PhysicalInterface(int index) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
    {
        try
        {
            return n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                !n.Name.Contains("TUN", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) &&
                n.GetIPProperties().GetIPv6Properties()?.Index == index &&
                n.GetIPProperties().UnicastAddresses.Any(a => ContentNodes.PublicV6(a.Address));
        }
        catch (NetworkInformationException) { return false; }
    });
    internal static async Task<TcpClient> Connect(IPAddress ip, int port, int interfaceIndex, CancellationToken ct)
    {
        if (!ContentNodes.PublicV6(ip)) throw new IOException("节点不是公网 IPv6");
        var nic = PhysicalInterface(interfaceIndex) ?? throw new IOException("原 IPv6 出口不可用，请全部关闭后重新开启接管");
        var client = new TcpClient(AddressFamily.InterNetworkV6);
        try
        {
            // Per-socket egress prevents TUN sniffing from replacing a deliberately selected IP.
            client.Client.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, interfaceIndex);
            await client.ConnectAsync(ip, port, ct);
            var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
            if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local))) throw new IOException("节点出口验证失败");
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    internal static async Task<double> Latency(string host, IPAddress ip, int interfaceIndex, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var watch = Stopwatch.StartNew();
        using var client = await Connect(ip, 443, interfaceIndex, timeout.Token);
        using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        return watch.Elapsed.TotalMilliseconds;
    }
    internal static async Task<NodeSpeed> Speed(Uri url, IPAddress ip, int interfaceIndex,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0,
            ConnectCallback = async (_, token) =>
            {
                var client = await Connect(ip, url.Port, interfaceIndex, token);
                return new NetworkStream(client.Client, ownsSocket: true);
            } };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ByteLimit - 1);
        foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is 403 or 429) throw new NodeRefusedException((int)response.StatusCode);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != 0 || range.To is not long end ||
            end < 0 || end >= ByteLimit) throw new IOException("资源未提供有效 Range 响应");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await ReadSpeed(stream, end + 1, watch, timeout.Token, ct);
    }
    internal static async Task<NodeSpeed> ReadSpeed(Stream stream, long expected, Stopwatch watch,
        CancellationToken deadline, CancellationToken user)
    {
        if (expected < 256 * 1024 || expected > ByteLimit) throw new IOException("资源样本不足");
        byte[] buffer = new byte[65536];
        long received = 0;
        bool bounded = false;
        try
        {
            while (received < expected)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - received)), deadline);
                if (count == 0) throw new IOException("响应不完整");
                received += count;
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or IOException) &&
            deadline.IsCancellationRequested && !user.IsCancellationRequested && received >= 256 * 1024) { bounded = true; }
        user.ThrowIfCancellationRequested();
        return new(received / Math.Max(.001, watch.Elapsed.TotalSeconds), bounded ? "10秒有界样本" : "Range 完整");
    }
}
