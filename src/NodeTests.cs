using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class NodeTests
{
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new IOException(name); checks.Add(name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        try
        {
            const string host = "dl-test.quark.cn";
            var a = IPAddress.Parse("2001:db8::10");
            var b = IPAddress.Parse("2001:db8::20");
            var now = DateTime.UtcNow;
            var nodes = new ContentNodes { Now = () => now };
            nodes.Lookup = (_, _) => Task.FromResult(new[] { new NodeAddress(a, now.AddMinutes(3), "fixture"),
                new NodeAddress(b, now.AddMinutes(3), "fixture"), new NodeAddress(a, now.AddMinutes(2), "duplicate"),
                new NodeAddress(IPAddress.Loopback, now.AddMinutes(3), "v4"),
                new NodeAddress(IPAddress.IPv6Loopback, now.AddMinutes(3), "local") });
            await nodes.Refresh(host, CancellationToken.None);
            Check(nodes.Snapshot(host).Length == 2, "Only distinct public IPv6 candidates survive discovery");
            Reject(() => ContentNodes.Host("drive.quark.cn"), "Control APIs are excluded from node selection");
            Check(ContentNodes.Host("cdn.example.net") == "cdn.example.net", "Custom content hostnames are accepted");
            nodes.Select(host, a);
            Check(nodes.Selected(host)!.Equals(a), "Refreshed nodes can be selected before testing");
            nodes.Automatic(host);
            Reject(() => nodes.Best(host, NodeOrder.Speed), "Missing speed data cannot win fastest-node selection");
            nodes.LatencyProbe = (_, ip, _) => Task.FromResult(ip.Equals(a) ? 90d : 30d);
            await nodes.Measure(host, NodeOrder.Latency, null, new Dictionary<string, string>(), null, CancellationToken.None);
            nodes.Best(host, NodeOrder.Latency);
            Check(nodes.Selected(host)!.Equals(b), "Latency ranking selects the lowest TLS delay");
            Check(nodes.Snapshot(host).All(n => n.BytesPerSecond == null), "Latency is never presented as download speed");
            nodes.SpeedProbe = (_, ip, _, _) => Task.FromResult(new NodeSpeed(ip.Equals(a) ? 1000 : 400, "fixture"));
            var url = ContentNodes.SpeedUrl(host, "https://" + host + "/file?signature=private");
            await nodes.Measure(host, NodeOrder.Speed, url, new Dictionary<string, string>(), null, CancellationToken.None);
            nodes.Best(host, NodeOrder.Speed);
            Check(nodes.Selected(host)!.Equals(a), "Speed ranking selects measured throughput independently of latency");
            nodes.Automatic(host);
            Check(nodes.Selected(host) == null, "Restore automatic clears only the current hostname selection");
            Reject(() => ContentNodes.SpeedUrl(host, "https://other.quark.cn/file"), "Speed probe cannot redirect credentials to another hostname");
            Reject(() => ContentNodes.SpeedUrl(host, "http://" + host + "/file"), "Speed probe requires HTTPS");
            Reject(() => ContentNodes.SpeedUrl(host, "https://user:pass@" + host + "/file"), "Embedded URL credentials are rejected");
            Reject(() => ContentNodes.SpeedUrl(host, "https://" + host + ":8443/file"), "Nonstandard probe ports are rejected");
            Reject(() => ContentNodes.Headers("{\"Host\":\"other.example\"}"), "Origin Host cannot be overridden by request headers");
            Reject(() => ContentNodes.Headers("{\"Cookie\":\"x\\r\\ny\"}"), "Request header newline injection is rejected");
            Check(ContentNodes.Headers("{\"User-Agent\":\"fixture\"}")["User-Agent"] == "fixture", "Allowed resource headers are preserved");
            int calls = 0;
            nodes.SpeedProbe = (_, _, _, _) => { calls++; throw new NodeRefusedException(429); };
            bool refused = false;
            try { await nodes.Measure(host, NodeOrder.Speed, url, new Dictionary<string, string>(), null, CancellationToken.None); }
            catch (NodeRefusedException) { refused = true; }
            Check(refused && calls == 1, "HTTP 429 stops the run instead of trying another node");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool cancelled = false;
                try { await nodes.Refresh(host, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation propagates instead of publishing a refresh");
            }
            now = now.AddMinutes(6);
            Check(nodes.Snapshot(host).Length == 0 && nodes.Selected(host) == null, "TTL expiry removes candidates and stale selection");
            Reject(() => nodes.Select(host, a), "Expired candidates cannot be selected again");
            using (var data = new MemoryStream(new byte[NodeNetwork.ByteLimit + 100]))
            {
                var speed = await NodeNetwork.ReadSpeed(data, NodeNetwork.ByteLimit, Stopwatch.StartNew(), CancellationToken.None, CancellationToken.None);
                Check(data.Position == NodeNetwork.ByteLimit && speed.BytesPerSecond > 0, "Speed probe reads no more than its 4 MiB bound");
            }
            using (var shortBody = new MemoryStream(new byte[1024]))
            {
                bool rejected = false;
                try { await NodeNetwork.ReadSpeed(shortBody, 512 * 1024, Stopwatch.StartNew(), CancellationToken.None, CancellationToken.None); }
                catch (IOException) { rejected = true; }
                Check(rejected, "Truncated response cannot become a successful speed result");
            }
            await RouterChecks(Check, a);
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();

    static async Task RouterChecks(Action<bool, string> check, IPAddress chosen)
    {
        const string host = "dl-test.quark.cn";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var origin = new TcpListener(IPAddress.IPv6Loopback, 0);
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        origin.Start(); proxy.Start();
        int originalRequests = 0, pinRequests = 0, dnsQueries = 0;
        async Task ReadHeader(Stream stream)
        {
            var bytes = new byte[1]; int end = 0, count = 0;
            while (end != 0x0d0a0d0a && count++ < 32768)
            {
                if (await stream.ReadAsync(bytes, ct) == 0) throw new IOException("Fixture header closed");
                end = (end << 8) | bytes[0];
            }
        }
        async Task Serve(TcpListener listener, bool original)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await ReadHeader(stream);
            if (original)
            {
                Interlocked.Increment(ref originalRequests);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                await ReadHeader(stream);
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), ct);
        }
        var serving = Serve(origin, false);
        await using var router = new DownloadRouter { ProductionPolicies = true };
        await router.Start(false);
        router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
        router.Resolve = _ => { dnsQueries++; throw new SocketException(); };
        router.Nodes.Lookup = (_, _) => Task.FromResult(new[] { new NodeAddress(chosen, DateTime.UtcNow.AddMinutes(2), "fixture") });
        router.Nodes.LatencyProbe = (_, _, _) => Task.FromResult(1d);
        await router.Nodes.Refresh(host, ct);
        await router.Nodes.Measure(host, NodeOrder.Latency, null, new Dictionary<string, string>(), null, ct);
        router.Nodes.Select(host, chosen);
        router.NodeConnector = async (ip, port, token) =>
        {
            check(ip.Equals(chosen) && port == 443, "Router passes the exact chosen IPv6 to its origin connector");
            pinRequests++;
            var client = new TcpClient(AddressFamily.InterNetworkV6);
            await client.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)origin.LocalEndpoint).Port, token);
            return client;
        };
        using var http = new HttpClient(new HttpClientHandler { UseProxy = true,
            Proxy = new WebProxy("http://127.0.0.1:" + router.Port) }) { Timeout = TimeSpan.FromSeconds(5) };
        router.Enable("quark", true);
        check(await http.GetStringAsync("http://" + host + ":443/") == "ok", "Selected IPv6 routes actual relay payload");
        await serving;
        check(pinRequests == 1 && dnsQueries == 0 && originalRequests == 0 && router.Counters["quark"].V4 == 0,
            "Selection bypasses DNS re-resolution and never contacts the original proxy");
        router.NodeConnector = (_, _, _) => throw new IOException("Fixture selected node unavailable");
        using (var failed = await http.GetAsync("http://" + host + ":443/"))
            check(failed.StatusCode == HttpStatusCode.BadGateway && !proxy.Pending(), "Selected-node failure never silently falls back");
        using var retryOrigin = new TcpListener(IPAddress.IPv6Loopback, 0);
        retryOrigin.Start();
        var retryServing = Serve(retryOrigin, false);
        int retryPort = ((IPEndPoint)retryOrigin.LocalEndpoint).Port;
        int retryQueries = 0;
        router.Nodes.Automatic(host);
        router.NodeConnector = null;
        router.Resolve = _ =>
        {
            Interlocked.Increment(ref retryQueries);
            return Task.FromResult(retryQueries == 1 ? Array.Empty<IPAddress>() : new[] { IPAddress.IPv6Loopback });
        };
        check(await http.GetStringAsync("http://" + host + ":" + retryPort + "/") == "ok",
            "Automatic strict IPv6 route refreshes AAAA after an empty result");
        await retryServing;
        check(retryQueries == 2, "Automatic retry performs one fresh AAAA lookup without IPv4 fallback");
        router.Nodes.Select(host, chosen);
        var originalServing = Serve(proxy, true);
        router.Enable("quark", false);
        check(await http.GetStringAsync("http://" + host + ":443/") == "ok", "Turning the service off bypasses its selected node");
        await originalServing;
        check(originalRequests == 1 && pinRequests == 1 && router.Counters["quark"].Proxy > 0,
            "Disabled service restores the original proxy and truthful byte accounting");
        check(router.Nodes.Selected(host)!.Equals(chosen), "Disabling service does not erase the session's user choice");
    }

    internal static void Live(string path) => Task.Run(async () =>
    {
        try
        {
            string proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? "";
            if (proxy.Length > 0) proxy = new Uri(proxy).Authority;
            using var dns = new ContentDns(proxy);
            await using var router = new DownloadRouter { ProductionPolicies = true };
            await router.Start(false);
            var nodes = router.Nodes;
            nodes.InterfaceIndex = NodeNetwork.CaptureInterface(); nodes.Lookup = dns.Discover;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            const string host = "dl-pc-zb.drive.quark.cn";
            await nodes.Refresh(host, timeout.Token);
            await nodes.Measure(host, NodeOrder.Latency, null, new Dictionary<string, string>(), null, timeout.Token);
            var first = nodes.Snapshot(host).FirstOrDefault(n => n.LatencyMs.HasValue) ?? throw new IOException("No reachable current AAAA");
            double latency = first.LatencyMs!.Value;
            nodes.Select(host, first.Dns.Address);
            router.Resolve = _ => throw new IOException("Selected node must not be re-resolved");
            router.Enable("quark", true);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = true,
                Proxy = new WebProxy("http://127.0.0.1:" + router.Port), AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://" + host + "/");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (router.Counters["quark"].V6 == 0 || router.Counters["quark"].V4 != 0 || router.Counters["quark"].Proxy != 0)
                throw new IOException("Selected route accounting failed");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", host, remote = first.Dns.Address.ToString(),
                nodes.InterfaceIndex, latencyMs = latency, tlsCertificateValidated = true, ipv4Fallback = false, status = (int)response.StatusCode,
                selectedRouterTested = true,
                fullPlaybackTested = false, tunActiveTested = false }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", error = ex.GetType().Name }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();
}
