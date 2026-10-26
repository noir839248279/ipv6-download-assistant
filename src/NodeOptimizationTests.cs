using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class NodeOptimizationTests
{
    const string Host = "dl-fixture.quark.cn";
    static readonly Uri Resource = new("https://" + Host + "/original?signature=fixture");
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        try
        {
            DateTime now = DateTime.UtcNow;
            string? network = "fixture-network";
            var nodes = new ContentNodes { Now = () => now, NetworkIdentity = () => network };
            var addresses = Enumerable.Range(1, 6).Select(i => IPAddress.Parse("2001:db8::" + i)).ToArray();
            nodes.Lookup = (_, _) => Task.FromResult(addresses.Select((ip, i) =>
                new NodeAddress(ip, now.AddSeconds(30), i == 2 ? "AliDNS / 当前 IPv6 网段" : "fixture")).ToArray());
            var called = new List<IPAddress>();
            nodes.SpeedProbe = (url, ip, headers, _) =>
            {
                Check(url == Resource && headers.Count == 0, "Every candidate receives the identical original resource");
                called.Add(ip);
                return Task.FromResult(new NodeSpeed(ip.Equals(addresses[1]) ? 9000 : 1000, "fixture"));
            };
            var choice = await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None);
            Check(called.Count == 3 && called[0].Equals(addresses[2]), "At most three candidates are compared, with scoped DNS first");
            Check(NodeNetwork.ByteLimit * called.Count == 6 * 1024 * 1024, "Requested content samples total at most 6 MiB");
            Check(choice.Address.Equals(addresses[1]) && nodes.Preferred(Host)!.Equals(addresses[1]),
                "Measured throughput, not first candidate or TLS delay, determines the remembered winner");
            nodes.Select(Host, addresses[0]);
            Check(nodes.Selected(Host)!.Equals(addresses[0]) && nodes.Preferred(Host)!.Equals(addresses[1]),
                "Manual selection remains independent and retains router priority");
            now = now.AddSeconds(31);
            Check(nodes.Preferred(Host) == null && nodes.NeedsRevalidation(Host),
                "Expired DNS prevents immediate reuse and requests bounded revalidation");
            Check(nodes.Preferred(Host, new[] { addresses[1] })!.Equals(addresses[1]),
                "A current DNS answer can revalidate a still-fresh throughput memory");
            Check(nodes.Preferred(Host, new[] { addresses[0] }) == null, "A removed DNS address cannot be pinned from throughput memory");
            await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None);
            network = "other-network";
            Check(nodes.Preferred(Host) == null, "Changing physical-network identity invalidates the remembered choice");
            network = "fixture-network";
            await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None);
            now = now.AddMinutes(6);
            Check(nodes.Preferred(Host, addresses) == null, "Throughput memory is bounded to five minutes even with valid new DNS");
            await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None);
            nodes.Automatic(Host);
            Check(nodes.Selected(Host) == null && nodes.Preferred(Host) == null, "Restore automatic clears both manual and measured preference");
            int count = 0;
            nodes.SpeedProbe = (_, _, _, _) => { count++; throw new NodeRefusedException(429); };
            bool refused = false;
            try { await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None); }
            catch (NodeRefusedException) { refused = true; }
            Check(refused && count == 1 && nodes.Preferred(Host) == null,
                "A resource refusal stops all further candidates and cannot publish a choice");
            count = 0;
            nodes.ProbeAllowed = _ => false;
            bool blocked = false;
            try { await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked && count == 0, "A disabled content service cannot start an optimization");
            nodes.ProbeAllowed = _ => true;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nodes.SpeedProbe = async (_, _, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(1, "never"); };
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var running = nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, stop.Token);
                await entered.Task.WaitAsync(stop.Token);
                bool overlapping = false;
                try { await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, stop.Token); }
                catch (InvalidOperationException) { overlapping = true; }
                Check(overlapping, "Overlapping optimization cannot multiply the content budget");
                bool manualOverlap = false;
                try { await nodes.Measure(Host, NodeOrder.Speed, Resource, new Dictionary<string, string>(), null, stop.Token); }
                catch (InvalidOperationException) { manualOverlap = true; }
                Check(manualOverlap, "Manual speed tests share the same single-flight content budget");
                nodes.CancelService("bilibili");
                Check(!running.IsCompleted, "Disabling another service does not cancel this service's operation");
                nodes.CancelService("quark");
                bool cancelled = false;
                try { await running; } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && nodes.Preferred(Host) == null, "Disabling the matching service cancels and does not publish partial results");
            }
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var running = nodes.Optimize("cdn.example.net", new Uri("https://cdn.example.net/fixture"),
                    new Dictionary<string, string>(), null, stop.Token);
                await entered.Task.WaitAsync(stop.Token);
                nodes.CancelService("global");
                bool cancelled = false;
                try { await running; } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Custom content tests stop when the global category is disabled");
            }
            using (var data = new MemoryStream(new byte[NodeNetwork.ByteLimit + 100]))
            {
                long bytes = 0;
                await NodeNetwork.ReadSpeed(data, NodeNetwork.ByteLimit, Stopwatch.StartNew(),
                    CancellationToken.None, CancellationToken.None, n => bytes += n);
                Check(bytes == NodeNetwork.ByteLimit && data.Position == bytes, "IPv6 probe accounting counts only bytes actually read within the cap");
            }
            nodes.SpeedProbe = (_, _, _, _) => Task.FromResult(new NodeSpeed(double.NaN, "invalid"));
            bool invalid = false;
            try { await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None); }
            catch (IOException) { invalid = true; }
            Check(invalid && nodes.Preferred(Host) == null, "Missing or invalid speed samples cannot become a preferred node");
            nodes.NetworkIdentity = () => throw new IOException("fixture identity failure");
            try { await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None); }
            catch (IOException) { }
            nodes.NetworkIdentity = () => network;
            nodes.SpeedProbe = (_, _, _, _) => Task.FromResult(new NodeSpeed(1000, "fixture"));
            await nodes.Optimize(Host, Resource, new Dictionary<string, string>(), null, CancellationToken.None);
            Check(nodes.Preferred(Host) != null, "An identity lookup failure cannot leave the measurement semaphore locked");
            await AlistChecks(Check);
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }));
        }
    }).GetAwaiter().GetResult();

    sealed class AlistFixture : HttpMessageHandler
    {
        internal readonly List<(Uri Uri, string? Token, string? Client, string Body)> Requests = new();
        internal int Code = 200;
        internal bool Redirect, Oversize;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Authorization", out var token) ? token.Single() : null,
                request.Headers.TryGetValues("Client-Id", out var client) ? client.Single() : null, body));
            if (Redirect) return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example/") } };
            if (Oversize) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) };
            object data = request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => new { token = "local-fixture-token" },
                "/api/fs/list" => new { content = new[] {
                    new { name = "video.mkv", is_dir = false, size = 10000000, path = "opaque-provider-id", virtual_path = "/base/media/video.mkv" }
                }, total = 101 },
                _ => new { url = Resource.AbsoluteUri, header = new Dictionary<string, string[]> {
                    ["Cookie"] = new[] { "resource-cookie=fixture" }, ["User-Agent"] = new[] { "fixture-UA" } } }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = Code, data }), Encoding.UTF8, "application/json") };
        }
    }
    static async Task AlistChecks(Action<bool, string> check)
    {
        var fixture = new AlistFixture();
        using var client = new AlistResourceClient(5244, fixture);
        await client.Login("fixture", "not-a-real-password", "", CancellationToken.None);
        var page = await client.List("/media", "", 1, CancellationToken.None);
        var resource = await client.Resource(page.Entries.Single().LinkPath, CancellationToken.None);
        check(fixture.Requests.All(r => r.Uri.Host == "127.0.0.1" && r.Uri.Port == 5244),
            "AList authentication and metadata are sent only to the explicit loopback origin");
        check(fixture.Requests[0].Token == null && fixture.Requests.Skip(1).All(r => r.Token == "local-fixture-token"),
            "AList session authorization is confined to local API requests");
        check(fixture.Requests.All(r => r.Client == "ipv6lens-node-picker"),
            "The AList picker has a distinct device identity instead of replacing another client's session");
        check(page.More && page.Entries.Single().Path == "/media/video.mkv" &&
            fixture.Requests.Last().Body.Contains("/base/media/video.mkv"),
            "Paged listing keeps virtual navigation separate from the full admin link path");
        check(resource.Url == Resource && resource.Headers["Cookie"] == "resource-cookie=fixture" &&
            resource.Headers["User-Agent"] == "fixture-UA" && !resource.Headers.ContainsKey("Authorization"),
            "The original signed resource and its own headers are preserved without leaking AList session authorization");
        fixture.Code = 401;
        bool auth = false;
        try { await client.List("/", "", 1, CancellationToken.None); } catch (InvalidOperationException) { auth = true; }
        check(auth, "AList authentication failure stops without automatic retries");
        fixture.Code = 200; fixture.Redirect = true;
        bool redirect = false;
        try { await client.List("/", "", 1, CancellationToken.None); } catch (InvalidOperationException) { redirect = true; }
        check(redirect, "A local API redirect is rejected instead of forwarding credentials");
        fixture.Redirect = false; fixture.Oversize = true;
        bool oversized = false;
        try { await client.List("/", "", 1, CancellationToken.None); } catch (IOException) { oversized = true; }
        check(oversized, "AList metadata responses have a one-MiB bound");
        foreach (string url in new[] { "http://" + Host + "/file", "https://other.example/file", "https://127.0.0.1/file",
            "https://user:pass@" + Host + "/file", "https://" + Host + ":8443/file" })
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { url }));
            bool rejected = false;
            try { AlistResourceClient.ParseResource(doc.RootElement); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "An unsafe or non-Quark resource URL is rejected before any content request");
        }
        using var injection = JsonDocument.Parse(JsonSerializer.Serialize(new { url = Resource.AbsoluteUri,
            header = new Dictionary<string, string[]> { ["Host"] = new[] { "other.example" } } }));
        bool headers = false;
        try { AlistResourceClient.ParseResource(injection.RootElement); } catch (InvalidOperationException) { headers = true; }
        check(headers, "AList resource headers cannot override the signed origin or inject routing controls");
    }
}
