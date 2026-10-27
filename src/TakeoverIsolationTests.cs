using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class TakeoverIsolationTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        try
        {
            Check(NativeCapture.SupportedCore("Mihomo Meta v1.19.10-ipv6lens.2 windows amd64 with go1.24.3\n"),
                "The private patched Windows core is accepted");
            Check(!NativeCapture.SupportedCore("Mihomo Meta v1.19.10 windows amd64 with go1.24.3") &&
                !NativeCapture.SupportedCore("Mihomo Meta v1.19.10-ipv6lens.10 windows amd64") &&
                !NativeCapture.SupportedCore("Mihomo Meta v1.19.10-ipv6lens.1 windows amd64") &&
                !NativeCapture.SupportedCore("Mihomo Meta v1.19.10-ipv6lens.2 linux amd64") &&
                !NativeCapture.SupportedCore("Mihomo Meta v1.19.10-ipv6lens.2 windows arm64"),
                "Unpatched, mismatched and wrong-platform cores are rejected before takeover");
            Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Enable("bilibili", true);
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                var config = JsonSerializer.SerializeToElement(NativeCapture.Config(router, 1, "", false));
                Check(!config.GetProperty("sniffer").GetProperty("override-destination").GetBoolean(),
                    "Sniffing classifies services without erasing the client's original destination IP");
                Check(config.GetProperty("sniffer").GetProperty("parse-pure-ip").GetBoolean(),
                    "Enabled Bilibili retains pure-IP content identification");
                Check(DownloadRouter.DirectAuxiliaryHost("www.bilibili.com") &&
                    DownloadRouter.DirectAuxiliaryHost("drive-pc.quark.cn") &&
                    !DownloadRouter.DirectAuxiliaryHost("accounts.google.com") &&
                    !DownloadRouter.DirectAuxiliaryHost("dl-test.quark.cn") &&
                    !DownloadRouter.DirectAuxiliaryHost("www.bilibili.com.example"),
                    "Domestic auxiliary direct routes are exact hosts, never content or overseas proxies");

                // Loopback answers are excluded from Mihomo's reverse DNS map.
                // An existing physical NIC address still keeps all traffic local.
                var selectedAddress = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                        n.GetIPProperties().GatewayAddresses.Count > 0)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .First(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                using var selected = new TcpListener(selectedAddress, 0);
                selected.Start();
                int originPort = ((IPEndPoint)selected.LocalEndpoint).Port;
                using var changed = new TcpListener(IPAddress.Parse("127.0.0.2"), originPort);
                using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, originPort);
                changed.Start(); ipv6.Start();
                var workers = new List<Task>();
                async Task Serve(TcpListener listener, string body)
                {
                    try
                    {
                        while (true)
                        {
                            var client = await listener.AcceptTcpClientAsync(stop.Token);
                            workers.Add(Task.Run(async () =>
                            {
                                using (client)
                                try
                                {
                                    while (true)
                                    {
                                        await Header(client.GetStream(), stop.Token);
                                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}"), stop.Token);
                                    }
                                }
                                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
                            }));
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                var serving = Task.WhenAll(Serve(selected, "chosen"), Serve(changed, "wrong"), Serve(ipv6, "ipv6"));
                using var free = new TcpListener(IPAddress.Loopback, 0);
                free.Start(); int corePort = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
                await using var dns = new RoutingRecoveryTests.FixtureDns(selectedAddress);
                await using var directDns = new RoutingRecoveryTests.FixtureDns(IPAddress.Parse("127.0.0.2"));
                using var freeDns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int dnsPort = ((IPEndPoint)freeDns.Client.LocalEndPoint!).Port;
                freeDns.Close();
                void Configure(Dictionary<string,object> generated)
                {
                    generated["mixed-port"] = corePort;
                    generated["bind-address"] = "127.0.0.1";
                    generated["dns"] = new Dictionary<string, object> { ["enable"] = true, ["ipv6"] = true,
                        ["enhanced-mode"] = "redir-host", ["listen"] = "127.0.0.1:" + dnsPort,
                        ["nameserver"] = new[] { "udp://127.0.0.1:" + dns.Port },
                        ["direct-nameserver"] = new[] { "udp://127.0.0.1:" + directDns.Port } };
                    var sniffer = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(generated["sniffer"]))!;
                    sniffer["sniff"] = new { HTTP = new { ports = new[] { originPort } } };
                    generated["sniffer"] = sniffer;
                    generated["rules"] = ((string[])generated["rules"]).Where(r =>
                        !r.Contains("PROCESS-NAME,", StringComparison.Ordinal) &&
                        !r.StartsWith("IP-CIDR", StringComparison.Ordinal)).ToArray();
                }
                async Task<TcpClient> Open()
                {
                    var client = new TcpClient();
                    try
                    {
                        await client.ConnectAsync(IPAddress.Loopback, corePort, stop.Token);
                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                            $"CONNECT {selectedAddress}:{originPort} HTTP/1.1\r\nHost: {selectedAddress}:{originPort}\r\n\r\n"), stop.Token);
                        if (!(await Header(client.GetStream(), stop.Token)).StartsWith("HTTP/1.1 200"))
                            throw new IOException("Core CONNECT failed");
                        return client;
                    }
                    catch { client.Dispose(); throw; }
                }
                async Task<string> Request(TcpClient client, string host)
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET / HTTP/1.1\r\nHost: {host}:{originPort}\r\n\r\n"), stop.Token);
                    string header = await Header(stream, stop.Token);
                    int length = int.Parse(header.Split("\r\n").Single(x => x.StartsWith("Content-Length:"))[15..].Trim());
                    byte[] body = new byte[length];
                    await stream.ReadExactlyAsync(body, stop.Token);
                    return Encoding.ASCII.GetString(body);
                }
                async Task<string> Once(string host)
                {
                    using var client = await Open();
                    return await Request(client, host);
                }
                await using (var legacy = new NativeCapture())
                {
                    await legacy.Start(router, false, generated =>
                    {
                        Configure(generated);
                        var sniffer = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(generated["sniffer"]))!;
                        sniffer["override-destination"] = true;
                        generated["sniffer"] = sniffer;
                    });
                    Check(await Once("disabled.quark.cn") == "wrong",
                        "Old destination override reproduces changing a disabled service's chosen server");
                }
                await using var core = new NativeCapture();
                await core.Start(router, false, Configure);
                try
                {
                    using (var dnsClient = new UdpClient())
                    {
                        using var query = new MemoryStream();
                        query.Write(new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                        foreach (string label in "disabled.quark.cn".Split('.'))
                        {
                            query.WriteByte((byte)label.Length);
                            query.Write(Encoding.ASCII.GetBytes(label));
                        }
                        query.Write(new byte[] { 0, 0, 1, 0, 1 });
                        await dnsClient.SendAsync(query.ToArray(), new IPEndPoint(IPAddress.Loopback, dnsPort), stop.Token);
                        var answer = await dnsClient.ReceiveAsync(stop.Token);
                        Check(answer.Buffer.Length >= 16 &&
                            BinaryPrimitives.ReadUInt16BigEndian(answer.Buffer.AsSpan(6, 2)) == 1 &&
                            new IPAddress(answer.Buffer[^4..]).Equals(selectedAddress),
                            "A real redir-host DNS mapping is seeded with the original local peer");
                    }
                    int directQueries = directDns.Queries;
                    Check(await Once("disabled.quark.cn") == "chosen",
                        "Disabled native Quark preserves its original peer despite DNS mapping and a different DIRECT resolver");
                    Check(directDns.Queries == directQueries,
                        "Mapped DIRECT traffic does not re-resolve the client's selected peer");
                    using var bili = await Open();
                    Check(await Request(bili, "enabled.bilivideo.com") == "ipv6",
                        "Enabled pure-IP Bilibili still reaches the IPv6 content relay");
                    router.Enable("quark", true);
                    await core.SyncServices(router, new[] { "quark" });
                    Check(await Once("disabled.quark.cn") == "ipv6",
                        "Turning Quark on re-enables its hostname capture");
                    Check(await Request(bili, "enabled.bilivideo.com") == "ipv6",
                        "Switching Quark does not interrupt an existing Bilibili stream");
                    router.Enable("quark", false);
                    await core.SyncServices(router, new[] { "quark" });
                    Check(await Once("disabled.quark.cn") == "chosen",
                        "Turning Quark off restores its original destination while Bilibili stays on");
                    Check(router.Counters["bilibili"].Enabled && !router.Counters["quark"].Enabled,
                        "A single service switch cannot change another service state");
                    Check(await Request(bili, "enabled.bilivideo.com") == "ipv6",
                        "The unrelated Bilibili stream survives both switch directions");
                    Check(router.Counters["quark"].V4 == 0 && router.Counters["bilibili"].V4 == 0,
                        "Enabled content remains IPv6-only throughout switching");
                }
                finally { stop.Cancel(); await serving; await Task.WhenAll(workers); }
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }
    static async Task<string> Header(Stream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1]; int end = 0;
        while (end != 0x0d0a0d0a && text.Length < 8192)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Fixture EOF");
            text.Append((char)one[0]); end = (end << 8) | one[0];
        }
        if (end != 0x0d0a0d0a) throw new IOException("Fixture header limit");
        return text.ToString();
    }
}
