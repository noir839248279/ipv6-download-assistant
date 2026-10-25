using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class RoutingRecoveryTests
{
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); checks.Add(message); }
        try
        {
            await DnsChecks(Check);
            await RaceChecks(Check);
            await NativeChecks(Check);
            await PacCases(path + ".pac.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();

    static async Task DnsChecks(Action<bool, string> check)
    {
        using var dns = new ContentDns("");
        var address = IPAddress.Parse("2001:db8::10");
        Task<NodeAddress[]> Valid(string _, CancellationToken ct) =>
            Task.FromResult(new[] { new NodeAddress(address, DateTime.UtcNow.AddSeconds(60), "fixture") });
        Task<NodeAddress[]> Empty(string _, CancellationToken ct) => Task.FromResult(Array.Empty<NodeAddress>());
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] { Empty, Valid };
        check((await dns.Resolve("fixture.invalid")).SequenceEqual(new[] { address }),
            "An empty source cannot hide another source's IPv6 answer");
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] { Valid,
            (_, _) => Task.FromResult(new[] { new NodeAddress(IPAddress.Parse("2001:db8::20"), DateTime.UtcNow, "TTL-zero") }) };
        check((await dns.ResolveAll("fixture.invalid")).Length == 2,
            "Connection retry can merge other DNS sources and use TTL-zero answers immediately");
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] {
            (_, _) => throw new HttpRequestException("fixture"), Valid };
        check((await dns.Resolve("fixture.invalid")).Single().Equals(address),
            "A failed DNS source cannot hide a successful IPv6 answer");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] {
            async (_, ct) => {
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cancelled.TrySetResult(); }
                return Array.Empty<NodeAddress>();
            }, Valid };
        check((await dns.Resolve("fixture.invalid")).Single().Equals(address),
            "A slow DNS source does not delay a usable IPv6 answer");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        check(true, "Unused DNS source receives cancellation");
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] { Empty, Empty };
        check((await dns.Resolve("fixture.invalid")).Length == 0, "Confirmed empty DNS answers remain empty, never fabricated");
        dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] {
            (_, _) => throw new HttpRequestException("fixture"), (_, _) => throw new SocketException() };
        bool failed = false;
        try { await dns.Resolve("fixture.invalid"); } catch (HttpRequestException) { failed = true; }
        check(failed, "All DNS sources failing is distinct from a successful no-AAAA response");
    }

    static async Task Reply(TcpListener listener, string body, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var b = new byte[1]; int end = 0;
        while (end != 0x0d0a0d0a)
        {
            if (await stream.ReadAsync(b, ct) == 0) throw new IOException("Fixture closed");
            end = (end << 8) | b[0];
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);
    }

    static async Task RaceChecks(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var origin = new TcpListener(IPAddress.IPv6Loopback, 0);
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        origin.Start(); proxy.Start();
        int port = ((IPEndPoint)origin.LocalEndpoint).Port;
        await using var router = new DownloadRouter { ProductionPolicies = true };
        await router.Start(false);
        router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
        var slow = IPAddress.Parse("2001:db8::11");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int v4Attempts = 0;
        router.Resolve = _ => Task.FromResult(new[] { slow, IPAddress.Loopback, IPAddress.IPv6Loopback });
        router.AutomaticConnector = async (ip, targetPort, ct) =>
        {
            if (ip.AddressFamily != AddressFamily.InterNetworkV6) Interlocked.Increment(ref v4Attempts);
            if (ip.Equals(slow))
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cancelled.TrySetResult(); }
            }
            var client = new TcpClient(AddressFamily.InterNetworkV6);
            try { await client.ConnectAsync(ip, targetPort, ct); return client; }
            catch { client.Dispose(); throw; }
        };
        router.Enable("bilibili", true);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = true,
            Proxy = new WebProxy("http://127.0.0.1:" + router.Port) }) { Timeout = TimeSpan.FromSeconds(4) };
        var serving = Reply(origin, "ok", timeout.Token);
        var watch = Stopwatch.StartNew();
        check(await http.GetStringAsync("http://race.bilivideo.com:" + port + "/") == "ok",
            "A later reachable IPv6 candidate succeeds despite a stalled first candidate");
        await serving;
        check(watch.Elapsed < TimeSpan.FromSeconds(2), "Candidate race avoids serial 2.5-second waits");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        check(v4Attempts == 0 && !proxy.Pending() && router.Counters["bilibili"].V4 == 0,
            "Candidate racing cancels losers without contacting IPv4 or the original proxy");
        router.Resolve = _ => Task.FromResult(Array.Empty<IPAddress>());
        using var missing = await http.GetAsync("http://empty.bilivideo.com:" + port + "/");
        check(missing.StatusCode == HttpStatusCode.BadGateway && router.ContentHosts().First() == "empty.bilivideo.com",
            "Node panel receives the actual failing content host ahead of successful older hosts");
    }

    static async Task NativeChecks(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var dns = new FixtureDns();
        using var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        using var v4 = new TcpListener(IPAddress.Loopback, 0);
        v6.Start(); v4.Start();
        int port6 = ((IPEndPoint)v6.LocalEndpoint).Port, port4 = ((IPEndPoint)v4.LocalEndpoint).Port;
        using var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start(); int mixedPort = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        await using var router = new DownloadRouter { ProductionPolicies = true };
        await router.Start(false);
        router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
        router.Enable("quark", true); router.Enable("bilibili", true);
        await using var core = new NativeCapture();
        await core.Start(router, false, config =>
        {
            config["mixed-port"] = mixedPort;
            config["log-level"] = "debug";
            config["dns"] = new Dictionary<string, object> {
                ["enable"] = true, ["ipv6"] = true, ["enhanced-mode"] = "redir-host",
                ["nameserver"] = new[] { "udp://127.0.0.1:" + dns.Port },
                ["default-nameserver"] = new[] { "127.0.0.1:" + dns.Port }
            };
            config["sniffer"] = new { enable = false };
            // Let the isolated loopback caller reach the real service selectors.
            config["rules"] = ((string[])config["rules"]).Where(r =>
                !r.Contains("PROCESS-NAME", StringComparison.Ordinal) && !r.StartsWith("IP-CIDR", StringComparison.Ordinal)).ToArray();
        });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = true,
            Proxy = new WebProxy("http://127.0.0.1:" + mixedPort) }) { Timeout = TimeSpan.FromSeconds(5) };
        var serving = Reply(v6, "v6", timeout.Token);
        string first;
        try { first = await http.GetStringAsync("http://switch.quark.cn:" + port6 + "/"); }
        catch(Exception ex) { throw new IOException(string.Join("\n", core.TestLog) + "\nRouter: " +
            JsonSerializer.Serialize(router.Counters.Select(p=>new {site=p.Key,p.Value.State,p.Value.LastFailure})), ex); }
        check(first == "v6",
            "Real isolated Mihomo routes enabled Quark into the strict IPv6 relay");
        await serving;
        router.Enable("quark", false);
        await core.SyncServices(router, new[] { "quark" });
        long before = router.Counters["quark"].V4 + router.Counters["quark"].V6 + router.Counters["quark"].Proxy;
        serving = Reply(v4, "v4", timeout.Token);
        check(await http.GetStringAsync("http://switch.quark.cn:" + port4 + "/") == "v4",
            "Turning Quark off sends real core traffic directly to the IPv4 fixture");
        await serving;
        check(router.Counters["quark"].V4 + router.Counters["quark"].V6 + router.Counters["quark"].Proxy == before,
            "Disabled native Quark traffic does not pass through the content relay");
        serving = Reply(v6, "v6", timeout.Token);
        check(await http.GetStringAsync("http://switch.bilivideo.com:" + port6 + "/") == "v6",
            "Disabling Quark leaves Bilibili on strict IPv6");
        await serving;
        router.Enable("quark", true);
        await core.SyncServices(router, new[] { "quark" });
        serving = Reply(v6, "v6", timeout.Token);
        check(await http.GetStringAsync("http://switch.quark.cn:" + port6 + "/") == "v6",
            "Re-enabling Quark restores strict IPv6 through the same running core");
        await serving;
    }

    sealed class FixtureDns : IAsyncDisposable
    {
        readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly CancellationTokenSource stop = new();
        readonly Task serving;
        internal int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        internal FixtureDns() { serving = Serve(); }
        async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var request = await socket.ReceiveAsync(stop.Token);
                    byte[] query = request.Buffer;
                    int end = 12;
                    while (query[end] != 0) end += query[end] + 1;
                    end++;
                    bool ipv4 = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end, 2)) == 1;
                    end += 4;
                    byte[] reply = new byte[end + (ipv4 ? 16 : 0)];
                    query.AsSpan(0, end).CopyTo(reply);
                    reply[2] = 0x81; reply[3] = 0x80;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6, 2), (ushort)(ipv4 ? 1 : 0));
                    reply.AsSpan(8, 4).Clear();
                    if (ipv4)
                    {
                        reply[end] = 0xc0; reply[end + 1] = 0x0c;
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 2, 2), 1);
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 4, 2), 1);
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(end + 6, 4), 60);
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 10, 2), 4);
                        IPAddress.Loopback.GetAddressBytes().CopyTo(reply, end + 12);
                    }
                    await socket.SendAsync(reply, request.RemoteEndPoint, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await serving; socket.Dispose(); stop.Dispose();
        }
    }

    static async Task PacCases(string path)
    {
        await using var router = new DownloadRouter();
        await router.Start(false);
        router.FallbackProxy = "http=127.0.0.1:18080;https=127.0.0.1:18443";
        router.Bypass = "*.lan";
        router.Enable("bilibili", true); router.Enable("global", true);
        string relay = "PROXY 127.0.0.1:" + router.Port;
        object Case(string url, string expected) => new { url, expected };
        var snapshots = new List<object>();
        snapshots.Add(new { pac = router.Pac(), cases = new[] {
            Case("https://cdn.quark.cn/", "PROXY 127.0.0.1:18443"),
            Case("http://cdn.quark.cn/", "PROXY 127.0.0.1:18080"),
            Case("https://cdn.bilivideo.com/", relay),
            Case("https://pan.quark.cn/", "PROXY 127.0.0.1:18443"),
            Case("https://ordinary.example/", relay),
            Case("https://192.168.1.1/", "DIRECT") } });
        router.Enable("quark", true); router.Enable("global", false);
        snapshots.Add(new { pac = router.Pac(), cases = new[] {
            Case("https://cdn.quark.cn/", relay),
            Case("https://pan.quark.cn/", "PROXY 127.0.0.1:18443"),
            Case("https://evilquark.cn/", "PROXY 127.0.0.1:18443"),
            Case("https://host.lan/", "DIRECT"),
            Case("https://ordinary.example/", "PROXY 127.0.0.1:18443") } });
        File.WriteAllText(path, JsonSerializer.Serialize(snapshots));
    }
}
