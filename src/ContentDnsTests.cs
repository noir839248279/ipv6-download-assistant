using System.Net;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class ContentDnsTests
{
    const string Host = "dl-pc-fixture.drive.quark.cn";
    const string Prefix = "2001:db8:1234::/48";
    static readonly IPAddress Local = IPAddress.Parse("2001:db8::10");
    static readonly IPAddress Scoped = IPAddress.Parse("2001:db8::20");

    sealed class Fixture : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = new();
        internal bool Refuse, Empty, Slow, IPv4Only, ZeroTtl, Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            if (Slow)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            if (Refuse) return new(HttpStatusCode.ServiceUnavailable);
            var rows = Empty ? Array.Empty<object>() : new object[] {
                new { type = IPv4Only ? 1 : 28, TTL = ZeroTtl ? 0 : 60,
                    data = IPv4Only ? "192.0.2.9" : Scoped.ToString() } };
            return new(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { Status = 0, Answer = rows }),
                    Encoding.UTF8, "application/json") };
        }
    }

    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); checks.Add(message); }
        ContentDns Create(Fixture transport)
        {
            var dns = new ContentDns("", transport) { ClientSubnet = () => Prefix };
            dns.Sources = new Func<string, CancellationToken, Task<NodeAddress[]>>[] {
                (_, _) => Task.FromResult(new[] { new NodeAddress(Local, DateTime.UtcNow.AddSeconds(30), "system") }),
                (_, _) => Task.FromResult(Array.Empty<NodeAddress>()) };
            return dns;
        }
        try
        {
            Check(NodeNetwork.ClientSubnetAddress(IPAddress.Parse("2001:db8:1234:abcd:ffff::1")) == Prefix,
                "Physical IPv6 context is masked to /48 without a device address or interface identifier");
            Check(NodeNetwork.ClientSubnetAddress(IPAddress.Parse("192.0.2.1")) == null &&
                NodeNetwork.ClientSubnetAddress(IPAddress.IPv6Loopback) == null &&
                NodeNetwork.ClientSubnetAddress(IPAddress.Parse("fd00::1")) == null &&
                NodeNetwork.ClientSubnetAddress(IPAddress.Parse("fe80::1")) == null,
                "IPv4, loopback, ULA, and link-local addresses cannot become client subnets");
            Check(NodeNetwork.ClientSubnet(0) == null,
                "An unavailable physical interface does not borrow an arbitrary adapter's address");
            var transport = new Fixture();
            using (var dns = Create(transport))
            {
                Check((await dns.Resolve(Host)).SequenceEqual(new[] { Scoped }),
                    "Quark automatic lookup prefers an IPv6 answer scoped to the physical network over an immediate unscoped answer");
                var query = Uri.UnescapeDataString(transport.Requests.Single().Query);
                Check(query.Contains("edns_client_subnet=" + Prefix) && query.Contains("type=AAAA"),
                    "Scoped lookup sends the coarse subnet and asks only for AAAA");
                Check(!query.Contains("Cookie") && !query.Contains("download_url"),
                    "DNS request does not contain credentials or resource URLs");
                Check((await dns.ResolveAll(Host)).SequenceEqual(new[] { Scoped, Local }),
                    "Retry retains scoped priority while including existing DNS alternatives");
                var nodes = await dns.Discover(Host, CancellationToken.None);
                Check(nodes.Any(n => n.Address.Equals(Scoped) && n.Source.Contains("IPv6")) &&
                    nodes.Any(n => n.Address.Equals(Local)),
                    "Node discovery includes the same scoped source as automatic routing");
                int count = transport.Requests.Count;
                Check((await dns.Resolve("video.bilivideo.com")).Single().Equals(Local) &&
                    transport.Requests.Count == count,
                    "Other services keep their existing local-DNS preference");
                Check((await dns.Resolve("drive.quark.cn")).Single().Equals(Local) &&
                    transport.Requests.Count == count,
                    "Quark auxiliary hosts do not gain a new resolution policy");
                Check((await dns.Resolve("2001:db8::99")).Single().Equals(IPAddress.Parse("2001:db8::99")) &&
                    transport.Requests.Count == count,
                    "Literal IPv6 targets remain unchanged without additional DNS");
                dns.ClientSubnet = () => null;
                Check((await dns.Resolve(Host)).Single().Equals(Local) &&
                    transport.Requests.Count == count,
                    "Missing physical IPv6 context preserves existing DNS without fabricating a prefix");
            }
            foreach (var fixture in new[] { new Fixture { Refuse = true }, new Fixture { Empty = true },
                new Fixture { IPv4Only = true } })
            {
                using var dns = Create(fixture);
                Check((await dns.Resolve(Host)).Single().Equals(Local),
                    "A refused, empty, or IPv4-only scoped response falls back to existing IPv6 answers");
            }
            var slow = new Fixture { Slow = true };
            using (var dns = Create(slow))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Check((await dns.Resolve(Host)).Single().Equals(Local) &&
                    watch.Elapsed < TimeSpan.FromSeconds(2),
                    "A stalled scoped resolver cannot block usable fallback answers");
                for (int i = 0; i < 20 && !slow.Cancelled; i++) await Task.Delay(10);
                Check(slow.Cancelled, "The unused scoped request is cancelled");
            }
            using (var dns = Create(new Fixture { ZeroTtl = true }))
                Check((await dns.Resolve(Host)).Single().Equals(Scoped),
                    "TTL-zero scoped answers are usable for the current connection");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }));
        }
    }).GetAwaiter().GetResult();
}
