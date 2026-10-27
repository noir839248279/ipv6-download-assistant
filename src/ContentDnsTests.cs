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
                foreach(string name in new[]{"video.bilivideo.com","cdn.steamcontent.com",
                    "download.epicgames.com"}){
                    Check((await dns.Resolve(name)).Single().Equals(Scoped),
                        "Known content service uses physical IPv6 context: "+DownloadRouter.ContentSite(name));
                }
                count=transport.Requests.Count;
                Check((await dns.Resolve("drive.quark.cn")).Single().Equals(Local) &&
                    transport.Requests.Count == count,
                    "Quark auxiliary hosts do not gain a new resolution policy");
                foreach(string name in new[]{"www.bilibili.com","api.steampowered.com",
                    "accounts.epicgames.com","www.youtube.com","rr1.googlevideo.com","ordinary.example","portal.local",
                    "bilivideo.com.attacker.example","dl-fixture.quark.cn.attacker.example"}){
                    Check((await dns.Resolve(name)).Single().Equals(Local)&&transport.Requests.Count==count,
                        "Auxiliary, ordinary, local, and suffix-lookalike hosts do not send network hints: "+name);
                }
                Check((await dns.Resolve("2001:db8::99")).Single().Equals(IPAddress.Parse("2001:db8::99")) &&
                    transport.Requests.Count == count,
                    "Literal IPv6 targets remain unchanged without additional DNS");
                dns.ClientSubnet = () => null;
                Check((await dns.Resolve(Host)).Single().Equals(Local) &&
                    transport.Requests.Count == count,
                    "Missing physical IPv6 context preserves existing DNS without fabricating a prefix");
                Check(dns.Scheduling.ForService("quark")?.Last?.Reason=="no_physical_ipv6_context",
                    "Missing physical context is recorded as skipped rather than fabricated success");
            }
            foreach (var fixture in new[] { new Fixture { Refuse = true }, new Fixture { Empty = true },
                new Fixture { IPv4Only = true } })
            {
                using var dns = Create(fixture);
                Check((await dns.Resolve(Host)).Single().Equals(Local),
                    "A refused, empty, or IPv4-only scoped response falls back to existing IPv6 answers");
                Check(dns.Scheduling.ForService("quark")?.DnsFallbacks==1&&
                    dns.Scheduling.Warning("quark")?.Contains("失败 1 次")==true,
                    "A successful DNS fallback retains a visible failure count");
                dns.Scheduling.Connected(Host,Local);
                Check(dns.Scheduling.ForService("quark")?.Recovered==1,
                    "Fallback connection success is distinct from the failed scheduling query");
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
                Check(dns.Scheduling.ForService("quark")?.Last?.Reason=="timeout_800ms",
                    "A slow scoped query records a timeout, not a target lacking IPv6");
            }
            using (var dns = Create(new Fixture { ZeroTtl = true }))
                Check((await dns.Resolve(Host)).Single().Equals(Scoped),
                    "TTL-zero scoped answers are usable for the current connection");
            using(var dns=Create(new Fixture())){
                await dns.Resolve(Host);
                dns.Scheduling.ConnectionFailed(Host,new[]{Scoped});
                Check(dns.Scheduling.NeedsOriginalFallback(Host),"Failed contextual candidates activate original-source retry");
                Check((await dns.ResolveFallback(Host)).SequenceEqual(new[]{Local}),
                    "Connection fallback excludes the already failed contextual source");
                dns.Scheduling.Connected(Host,Local);
                var summary=dns.Scheduling.ForService("quark");
                Check(summary?.ConnectFailures==1&&summary.Recovered==1,
                    "Contextual connect failure and recovered original IPv6 connection have separate counts");
                Check(!dns.Scheduling.NeedsOriginalFallback(Host),"A successful retry clears only the pending fallback state");
                dns.Scheduling.Record(Host,"ipv6_route_failed","ipv6_connect_failed");
                Check(dns.Scheduling.Warning("quark")?.Contains("兜底也失败")==true,
                    "Final IPv6 failure remains visible instead of claiming fallback success");
            }
            string reports=Path.Combine(Path.GetTempPath(),"ipv6-dns-fixture-"+Guid.NewGuid().ToString("N"));
            try{
                using var dns=Create(new Fixture{Empty=true});
                dns.Scheduling.Directory=reports;
                await dns.Resolve(Host);
                string report=File.ReadAllText(Path.Combine(reports,dns.Scheduling.LastReport!));
                Check(report.Contains("dns_fallback")&&report.Contains("no_aaaa")&&!report.Contains(Prefix),
                    "Automatic scheduling report includes failure evidence but not the personal network prefix");
                await dns.Resolve(Host);
                Check(System.IO.Directory.GetFiles(reports).Length==1,
                    "Repeated failures are counted without writing more than one report per five minutes");
                for(int i=0;i<160;i++)
                    dns.Scheduling.Record("dl-"+i+".quark.cn","dns_fallback","fixture");
                using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(dns.Scheduling.Snapshot()));
                Check(snapshot.RootElement.GetProperty("recent").GetArrayLength()==80&&
                    snapshot.RootElement.GetProperty("trackedHosts").GetInt32()==128,
                    "Recent events and tracked host state remain bounded under repeated failures");
            }finally{
                if(System.IO.Directory.Exists(reports))
                    foreach(string file in System.IO.Directory.GetFiles(reports))File.Delete(file);
                if(System.IO.Directory.Exists(reports))System.IO.Directory.Delete(reports);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }));
        }
    }).GetAwaiter().GetResult();
}
