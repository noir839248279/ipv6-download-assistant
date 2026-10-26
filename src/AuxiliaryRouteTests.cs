using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace IPv6Lens;

internal static class AuxiliaryRouteTests
{
    internal static void RunInterface(string path)
    {
        var checks = new List<string>();
        try
        {
            Task.Run(async () =>
            {
                int index = NodeNetwork.CaptureIPv4Interface();
                var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == index);
                var address = nic?.GetIPProperties().UnicastAddresses.Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (index == 0 || address == null) throw new IOException("Requires an active physical IPv4 interface without TUN");
                checks.Add("Captured an available physical IPv4 interface");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var listener = new TcpListener(address, 0);
                listener.Start();
                using var client = await NodeNetwork.ConnectIPv4(address, ((IPEndPoint)listener.LocalEndpoint).Port, index, deadline.Token);
                using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                if (!((IPEndPoint)client.Client.LocalEndPoint!).Address.Equals(address))
                    throw new IOException("Bound socket used another interface address");
                await client.GetStream().WriteAsync(new byte[] { 42 }, deadline.Token);
                byte[] value = new byte[1];
                if (await accepted.GetStream().ReadAsync(value, deadline.Token) != 1 || value[0] != 42)
                    throw new IOException("Bound local connection failed");
                checks.Add("Production IP_UNICAST_IF socket connects and transfers locally on the captured interface");
                try
                {
                    using var invalid = await NodeNetwork.ConnectIPv4(address, ((IPEndPoint)listener.LocalEndpoint).Port, 0, deadline.Token);
                    throw new InvalidOperationException("Missing physical interface did not fail closed");
                }
                catch (IOException) { checks.Add("Missing physical interface fails closed instead of choosing another route"); }
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }

    internal static void Run(string path)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        try
        {
            Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var origin = new TcpListener(IPAddress.IPv6Any, 0);
                origin.Server.DualMode = true; origin.Start();
                using var original = new TcpListener(IPAddress.Loopback, 0);
                original.Start();
                int port = ((IPEndPoint)origin.LocalEndpoint).Port, proxyCalls = 0, dnsCalls = 0;
                async Task Serve(TcpListener listener, bool brokenProxy)
                {
                    try
                    {
                        while (true)
                        {
                            using var client = await listener.AcceptTcpClientAsync(stop.Token);
                            if (brokenProxy) Interlocked.Increment(ref proxyCalls);
                            byte[] b = new byte[1]; int end = 0;
                            while (end != 0x0d0a0d0a)
                            {
                                if (await client.GetStream().ReadAsync(b, stop.Token) == 0) break;
                                end = (end << 8) | b[0];
                            }
                            await client.GetStream().WriteAsync(brokenProxy
                                ? "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()
                                : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), stop.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                var serving = Task.WhenAll(Serve(origin, false), Serve(original, true));
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)original.LocalEndpoint).Port;
                router.OriginalResolve = _ => { Interlocked.Increment(ref dnsCalls); return Task.FromResult(new[] { IPAddress.Loopback }); };
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.Loopback });
                router.Enable("quark", true); router.SetMode("quark", ServiceMode.Mixed);
                using var http = new HttpClient(new HttpClientHandler
                    { UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + router.Port) }) { Timeout = TimeSpan.FromSeconds(3) };
                async Task<bool> Ok(string host)
                {
                    using var response = await http.GetAsync($"http://{host}:{port}/");
                    return response.IsSuccessStatusCode;
                }
                try
                {
                    var row = router.Counters["quark"];
                    Check(await Ok("px.wpk.quark.cn") && proxyCalls == 0 && row.V4 > 0 && row.Proxy == 0 && row.Auxiliary.Used > 0,
                        "Exact auxiliary host succeeds with metered IPv4 despite a broken original proxy");
                    Check(router.RecentRoutes().Any(r => r.Host == "px.wpk.quark.cn" && r.Family == "IPv4" && r.Peer == "127.0.0.1"),
                        "Route events identify the real IPv4 peer, not unknown proxy egress");
                    int directQueries = dnsCalls;
                    router.SetMode("quark", ServiceMode.Strict);
                    Check(!await Ok("px.wpk.quark.cn") && dnsCalls == directQueries && proxyCalls == 0,
                        "Strict mode never enters the auxiliary IPv4 exception");
                    router.SetMode("quark", ServiceMode.Mixed);
                    Check(!await Ok("dl-test.quark.cn") && dnsCalls == directQueries && proxyCalls == 0,
                        "Content failure does not silently spend IPv4");
                    Check(!DownloadRouter.DirectAuxiliaryHost("sub.px.wpk.quark.cn") &&
                        !DownloadRouter.DirectAuxiliaryHost("px.wpk.quark.cn.example") &&
                        !DownloadRouter.DirectAuxiliaryHost("drive-pc.quark.cn"),
                        "Direct exception is exact-host only, not all Quark APIs or suffix lookalikes");
                    while (row.Auxiliary.Used < row.Auxiliary.Capacity) row.Auxiliary.Reserve(65536);
                    Check(!await Ok("px.wpk.quark.cn") && dnsCalls == directQueries,
                        "Exhausted allowance cannot open another IPv4 connection");
                    router.Enable("quark", false);
                    Check(!await Ok("px.wpk.quark.cn") && proxyCalls == 1 && dnsCalls == directQueries,
                        "Disabled service restores original route and does not retain the exception");
                    router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                    router.Enable("quark", true);
                    Check(await Ok("dl-ipv6.quark.cn") && row.V6 > 0 && proxyCalls == 1,
                        "IPv6 content remains usable when the auxiliary allowance is exhausted");
                }
                finally { stop.Cancel(); await serving; }
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }
}
