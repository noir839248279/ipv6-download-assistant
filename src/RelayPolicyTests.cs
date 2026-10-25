using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class RelayPolicyTests
{
    public static void Run(string path)
    {
        var checks = new List<string>();
        var failures = new List<string>();
        async Task Case(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(12)); checks.Add(name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        Task.Run(async () =>
        {
            using var origin = new TcpListener(IPAddress.IPv6Any, 0);
            origin.Server.DualMode = true;
            origin.Start();
            int port = ((IPEndPoint)origin.LocalEndpoint).Port;
            using var servingStop = new CancellationTokenSource();
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (!servingStop.IsCancellationRequested)
                    {
                        var connection = await origin.AcceptTcpClientAsync(servingStop.Token);
                        _ = Task.Run(async () =>
                        {
                            using (connection)
                            {
                                try
                                {
                                    var stream = connection.GetStream();
                                    byte[] b = new byte[1];
                                    int end = 0;
                                    var header = new StringBuilder();
                                    while (end != 0x0d0a0d0a)
                                    {
                                        if (await stream.ReadAsync(b, servingStop.Token) == 0) return;
                                        header.Append((char)b[0]); end = (end << 8) | b[0];
                                    }
                                    if (header.ToString().StartsWith("HALF "))
                                    {
                                        while (await stream.ReadAsync(b, servingStop.Token) != 0) { }
                                        await Task.Delay(100, servingStop.Token);
                                    }
                                    if (header.ToString().StartsWith("RESET "))
                                    {
                                        connection.Client.LingerState = new LingerOption(true, 0);
                                        connection.Client.Close(0);
                                        return;
                                    }
                                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), servingStop.Token);
                                }
                                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
            });
            foreach (string site in new[] { "quark", "bilibili" })
            {
                string host = site == "quark" ? "dl-test.quark.cn" : "test.bilivideo.com";
                await Case(site + ": strict IPv6, switch off restores IPv4 without content DNS, switch on blocks IPv4", async () =>
                {
                    await using var router = new DownloadRouter { ProductionPolicies = true };
                    await router.Start(false);
                    int contentQueries = 0, originalQueries = 0;
                    bool failContentDns = false;
                    router.Resolve = _ =>
                    {
                        Interlocked.Increment(ref contentQueries);
                        if (failContentDns) throw new InvalidOperationException("Disabled path must not query content DNS");
                        return Task.FromResult(new[] { IPAddress.IPv6Loopback });
                    };
                    router.OriginalResolve = _ =>
                    {
                        Interlocked.Increment(ref originalQueries);
                        return Task.FromResult(new[] { IPAddress.Loopback });
                    };
                    router.EnableAll();
                    using var http = Client(router);
                    Check(await http.GetStringAsync($"http://{host}:{port}/") == "ok", "IPv6 response missing");
                    Check(router.Counters[site].V6 > 0 && router.Counters[site].V4 == 0 && originalQueries == 0, "Strict route escaped IPv6");
                    router.Enable(site, false);
                    int before = contentQueries;
                    failContentDns = true;
                    string freshHost = "new-" + host;
                    Check(await http.GetStringAsync($"http://{freshHost}:{port}/") == "ok", "Disabled IPv4 path failed");
                    Check(contentQueries == before && originalQueries > 0 && router.Counters[site].V4 > 0, "Disabled path still depends on content DNS");
                    Check(router.Counters[site == "quark" ? "bilibili" : "quark"].Enabled, "Other policy was disabled");
                    router.Enable(site, true);
                    router.Resolve = _ => Task.FromResult(new[] { IPAddress.Loopback });
                    int beforeOriginal = originalQueries;
                    using var blocked = await http.GetAsync($"http://ipv4-only-{host}:{port}/");
                    Check(blocked.StatusCode == HttpStatusCode.BadGateway && originalQueries == beforeOriginal, "Re-enabled service fell back to IPv4");
                    var deadline = DateTime.UtcNow.AddSeconds(1);
                    while (router.Counters[site].LastFailure == null && DateTime.UtcNow < deadline) await Task.Delay(10);
                    Check(router.Counters[site].LastFailure?.Reason == "no_aaaa", "Missing IPv6 reason not exported");
                });
                await Case(site + ": delayed response survives client TCP send half-close", async () =>
                {
                    await using var router = new DownloadRouter { ProductionPolicies = true };
                    await router.Start(false);
                    router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                    router.Enable(site, true);
                    using var caller = new TcpClient();
                    await caller.ConnectAsync(IPAddress.Loopback, router.Port);
                    var stream = caller.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"));
                    Check((await Header(stream)).Contains(" 200 "), "Tunnel failed");
                    await stream.WriteAsync("HALF / HTTP/1.1\r\nHost: local\r\n\r\n"u8.ToArray());
                    caller.Client.Shutdown(SocketShutdown.Send);
                    using var body = new MemoryStream();
                    using var timeout = new CancellationTokenSource(3000);
                    await stream.CopyToAsync(body, timeout.Token);
                    Check(Encoding.ASCII.GetString(body.ToArray()).EndsWith("\r\n\r\nok"), "Response was truncated after upload EOF");
                    Check(router.Counters[site].V4 == 0 && router.Counters[site].Proxy == 0, "Half-close used fallback");
                });
            }
            await Case("Disabled service uses original proxy without content DNS or false IPv6 accounting", async () =>
            {
                using var proxy = new TcpListener(IPAddress.Loopback, 0);
                proxy.Start();
                using var proxyStop = new CancellationTokenSource(4000);
                var servingProxy = Task.Run(async () =>
                {
                    using var c = await proxy.AcceptTcpClientAsync(proxyStop.Token);
                    var stream = c.GetStream();
                    await Header(stream);
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), proxyStop.Token);
                    await Header(stream);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), proxyStop.Token);
                });
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
                router.Resolve = _ => throw new InvalidOperationException("Original proxy must not depend on content DNS");
                router.OriginalResolve = _ => throw new InvalidOperationException("Original proxy resolves the target");
                router.EnableAll();
                router.Enable("quark", false);
                using var http = Client(router);
                Check(await http.GetStringAsync($"http://dl-test.quark.cn:{port}/") == "ok", "Original proxy response failed");
                Check(router.Counters["quark"].Proxy > 0 && router.Counters["quark"].V4 == 0 && router.Counters["quark"].V6 == 0, "Proxy family misreported");
                await servingProxy;
            });
            foreach (bool upload in new[] { true, false })
                await Case((upload ? "Upload" : "Download") + " transport errors propagate instead of being swallowed", async () =>
                {
                    using var broken = new BrokenStream();
                    using var normal = new MemoryStream();
                    using var caller = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    using var remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    bool propagated = false;
                    try { await DownloadRouter.Relay(upload ? broken : normal, upload ? normal : broken, caller, remote, new(), true, false, CancellationToken.None); }
                    catch (IOException ex) when (ex.Message == "Injected transport failure") { propagated = true; }
                    Check(propagated, "Relay hid the primary I/O exception");
                });
            await Case("Actual mid-transfer reset increments failures and exports sanitized transfer diagnostics", async () =>
            {
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                router.Enable("quark", true);
                using var caller = new TcpClient();
                await caller.ConnectAsync(IPAddress.Loopback, router.Port);
                var stream = caller.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT dl-test.quark.cn:{port} HTTP/1.1\r\nHost: dl-test.quark.cn:{port}\r\n\r\n"));
                Check((await Header(stream)).Contains(" 200 "), "Tunnel failed");
                await stream.WriteAsync("RESET /private?token=synthetic-secret HTTP/1.1\r\nHost: dl-test.quark.cn\r\n\r\n"u8.ToArray());
                var row = router.Counters["quark"];
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (row.LastFailure == null && DateTime.UtcNow < deadline) await Task.Delay(10);
                Check(row.Failures > 0 && row.LastFailure?.Stage == "transfer", "Transfer reset was omitted from failures: " + JsonSerializer.Serialize(new { row.Failures, row.LastFailure, row.State }));
                string report = JsonSerializer.Serialize(row.LastFailure);
                Check(report.Contains("dl-test.quark.cn") && !report.Contains("synthetic-secret") && !report.Contains("/private"), "Diagnostics expose a URL or payload");
                Check(row.V4 == 0 && row.Proxy == 0, "Reset caused fallback");
            });
            await Case("Switching a service off closes its old tunnel without recording an I/O failure", async () =>
            {
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                router.Enable("quark", true);
                using var caller = new TcpClient();
                await caller.ConnectAsync(IPAddress.Loopback, router.Port);
                var stream = caller.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT dl-test.quark.cn:{port} HTTP/1.1\r\nHost: dl-test.quark.cn:{port}\r\n\r\n"));
                Check((await Header(stream)).Contains(" 200 "), "Tunnel failed");
                router.Enable("quark", false);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (JsonSerializer.SerializeToElement(router.Health()).GetProperty("activeSessions").GetInt32() != 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Check(JsonSerializer.SerializeToElement(router.Health()).GetProperty("activeSessions").GetInt32() == 0, "Old tunnel stayed active");
                Check(router.Counters["quark"].Failures == 0 && router.Counters["quark"].LastFailure == null, "User cancellation misreported as transfer failure");
            });
            await Case("Protection loss: explicitly disabled service recovers, enabled service stays blocked", async () =>
            {
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                router.OriginalResolve = _ => Task.FromResult(new[] { IPAddress.Loopback });
                router.EnableAll();
                router.OwnershipProbe = () => false;
                router.Sample();
                Check(router.ProtectionLost, "Loss latch missing");
                router.Enable("quark", false);
                using var http = Client(router);
                Check(await http.GetStringAsync($"http://dl-test.quark.cn:{port}/") == "ok" && router.Counters["quark"].V4 > 0, "Explicitly disabled service cannot recover");
                using var blocked = await http.GetAsync($"http://test.bilivideo.com:{port}/");
                Check(blocked.StatusCode == HttpStatusCode.BadGateway && router.Counters["bilibili"].V4 == 0, "Other protected service escaped");
                bool rejected = false;
                try { router.Enable("quark", true); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && !router.Counters["quark"].Enabled, "Lost protection allowed enabling");
                router.Sample();
                Check(!router.Counters["quark"].State.Contains("未受保护"), "Disabled status still claims blocked");
            });
            servingStop.Cancel();
            await serving;
        }).GetAwaiter().GetResult();
        File.WriteAllText(path, JsonSerializer.Serialize(new { state = failures.Count == 0 ? "passed" : "failed", checks, failures }, new JsonSerializerOptions { WriteIndented = true }));
        if (failures.Count != 0) Environment.ExitCode = 1;
    }

    static HttpClient Client(DownloadRouter router) => new(new HttpClientHandler
    {
        UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + router.Port)
    }) { Timeout = TimeSpan.FromSeconds(4) };

    sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Injected transport failure"));
    }

    static async Task<string> Header(Stream stream)
    {
        using var timeout = new CancellationTokenSource(3000);
        using var data = new MemoryStream();
        byte[] b = new byte[1];
        int end = 0;
        while (end != 0x0d0a0d0a)
        {
            if (await stream.ReadAsync(b, timeout.Token) == 0) throw new IOException("Unexpected EOF");
            data.WriteByte(b[0]); end = (end << 8) | b[0];
        }
        return Encoding.ASCII.GetString(data.ToArray());
    }
}
