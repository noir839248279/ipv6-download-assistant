using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class LocalNetworkTests
{
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new IOException(message);
            checks.Add(message);
        }
        try
        {
            foreach (string address in new[] { "127.0.0.2", "10.1.2.3", "172.16.0.1",
                "172.31.255.254", "192.168.12.1", "169.254.2.1", "::1", "fd12::1",
                "fc00::1", "fe80::1", "::ffff:192.168.1.1" })
                Check(LocalNetwork.Contains(IPAddress.Parse(address)), address + " is local");
            foreach (string address in new[] { "172.15.1.1", "172.32.0.1", "169.253.1.1",
                "192.169.1.1", "223.5.5.5", "2001:db8::1", "fec0::1", "::" })
                Check(!LocalNetwork.Contains(IPAddress.Parse(address)), address + " is not a local bypass");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var origin = new TcpListener(IPAddress.Loopback, 0);
            using var originalProxy = new TcpListener(IPAddress.Loopback, 0);
            origin.Start(); originalProxy.Start();
            int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            await using var router = new DownloadRouter { ProductionPolicies = true };
            await router.Start(false);
            router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)originalProxy.LocalEndpoint).Port;
            int contentResolves = 0, systemResolves = 0;
            var privateHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "portal.fixture.invalid", "nas.another.example", "gateway.campus.test" };
            router.Resolve = _ =>
            {
                Interlocked.Increment(ref contentResolves);
                return Task.FromResult(Array.Empty<IPAddress>());
            };
            router.OriginalResolve = host =>
            {
                Interlocked.Increment(ref systemResolves);
                return Task.FromResult(new[] { privateHosts.Contains(host) ? IPAddress.Loopback : IPAddress.Parse("203.0.113.1") });
            };
            router.Enable("global", true);
            router.Enable("quark", true);
            router.Enable("bilibili", true);
            using var http = new HttpClient(new HttpClientHandler
            {
                UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + router.Port),
                AllowAutoRedirect = false
            }) { Timeout = TimeSpan.FromSeconds(4) };
            async Task Serve()
            {
                using var client = await origin.AcceptTcpClientAsync(timeout.Token);
                var stream = client.GetStream();
                var bytes = new byte[1]; int end = 0;
                while (end != 0x0d0a0d0a)
                {
                    if (await stream.ReadAsync(bytes, timeout.Token) == 0) throw new IOException("Fixture closed");
                    end = (end << 8) | bytes[0];
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\nLocation: /login\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            }
            foreach (string host in privateHosts)
            {
                var serving = Serve();
                using var response = await http.GetAsync("http://" + host + ":" + originPort + "/");
                Check(response.StatusCode == HttpStatusCode.Found, host + " reaches its private origin with global strict mode enabled");
                await serving;
            }
            Check(contentResolves == 0 && systemResolves == privateHosts.Count && !originalProxy.Pending(),
                "Unknown private names use system DNS and direct origin, without content DNS or original proxy");
            Check(router.Counters["global"].V4 > 0 && router.Counters["global"].V6 == 0,
                "Legacy relay local traffic records actual IPv4 bytes, not fictitious IPv6");
            int localLookups = systemResolves;
            foreach (string host in new[] { "missing.quark.cn", "missing.bilivideo.com" })
            {
                using var rejected = await http.GetAsync("http://" + host + ":" + originPort + "/");
                Check(rejected.StatusCode == HttpStatusCode.BadGateway, host + " remains strict without AAAA");
            }
            Check(systemResolves == localLookups && router.Counters["quark"].V4 == 0 && router.Counters["bilibili"].V4 == 0,
                "Recognized public services do not acquire a system-DNS IPv4 bypass");
            using (var rejected = await http.GetAsync("http://public.fixture.invalid:" + originPort + "/"))
                Check(rejected.StatusCode == HttpStatusCode.BadGateway && !originalProxy.Pending(),
                    "A public IPv4 answer does not bypass the selected global strict policy");
            using var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start(); int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
            using (var failed = await http.GetAsync("http://portal.fixture.invalid:" + closedPort + "/"))
                Check(failed.StatusCode == HttpStatusCode.BadGateway && !originalProxy.Pending(),
                    "An unreachable private target never falls back to the external proxy");

            object Case(string url, string expected) => new { url, expected };
            string relay = "PROXY 127.0.0.1:" + router.Port;
            var dns = new Dictionary<string, string> {
                ["portal.fixture.invalid"] = "172.16.17.18", ["nas.another.example"] = "192.168.20.1",
                ["gateway.campus.test"] = "10.90.1.2", ["v6.lan.test"] = "fd12::1",
                ["public.fixture.invalid"] = "203.0.113.1",
                ["mixed.fixture.invalid"] = "192.168.20.1;203.0.113.1",
                ["cdn.quark.cn"] = "192.168.20.1" };
            var snapshots = new List<object>();
            foreach (bool global in new[] { true, false })
            {
                router.Enable("global", global);
                string ordinary = global ? relay : "PROXY " + router.FallbackProxy;
                string privateName = global ? "DIRECT" : ordinary;
                snapshots.Add(new { pac = router.Pac(), dns, maxDnsCalls=global?100:0, cases = new[] {
                    Case("http://portal.fixture.invalid/", privateName),
                    Case("http://NAS.ANOTHER.EXAMPLE./", privateName),
                    Case("http://gateway.campus.test/", privateName),
                    Case("http://v6.lan.test/", privateName),
                    Case("http://172.16.17.18/", "DIRECT"),
                    Case("http://[fd12::1]/", "DIRECT"),
                    Case("http://[fe80::1]/", "DIRECT"),
                    Case("https://cdn.quark.cn/", relay),
                    Case("https://cdn.bilivideo.com/", relay),
                    Case("http://public.fixture.invalid/", ordinary),
                    Case("http://mixed.fixture.invalid/", ordinary),
                    Case("http://unresolved.fixture.invalid/", ordinary) } });
            }
            File.WriteAllText(path + ".pac.json", JsonSerializer.Serialize(snapshots));
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();

    internal static void Probe(string url, string path) => Task.Run(async () =>
    {
        try
        {
            var target = new Uri(url, UriKind.Absolute);
            if (target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 ||
                target.Query.Length != 0 || target.Fragment.Length != 0 || target.AbsolutePath != "/")
                throw new IOException("Use only a credential-free root URL");
            var addresses = await Dns.GetHostAddressesAsync(target.IdnHost).WaitAsync(TimeSpan.FromSeconds(5));
            if (addresses.Length == 0 || addresses.Any(a => !LocalNetwork.Contains(a)))
                throw new IOException("Probe requires a verified local-network destination");
            await using var router = new DownloadRouter { ProductionPolicies = true };
            await router.Start(false);
            router.Enable("global", true);
            router.FallbackProxy = "127.0.0.1:1";
            using var http = new HttpClient(new HttpClientHandler { UseProxy = true,
                Proxy = new WebProxy("http://127.0.0.1:" + router.Port), AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
            bool ok = (int)response.StatusCode < 400 && router.Counters["global"].Failures == 0;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = ok ? "passed" : "failed",
                status = (int)response.StatusCode, globalStrict = true, v4 = router.Counters["global"].V4,
                v6 = router.Counters["global"].V6, routes = router.RecentRoutes() }));
            if (!ok) Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", error = ex.GetType().Name }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();
}
