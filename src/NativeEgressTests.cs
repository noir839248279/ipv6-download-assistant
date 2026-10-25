using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IPv6Lens;

internal static class NativeEgressTests
{
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        string folder = Path.Combine(Path.GetTempPath(), "IPv6Lens-egress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Process? core = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        v6.Server.DualMode = false;
        v6.Start();
        int originPort = ((IPEndPoint)v6.LocalEndpoint).Port;
        var v4 = new TcpListener(IPAddress.Loopback, originPort);
        v4.Start();
        int accepted4 = 0;
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int dnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port;
        async Task DnsServer()
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var request = await dns.ReceiveAsync(token);
                    byte[] query = request.Buffer;
                    int end = 12;
                    var labels = new List<string>();
                    while (end < query.Length && query[end] != 0)
                    {
                        int length = query[end++];
                        labels.Add(Encoding.ASCII.GetString(query, end, length));
                        end += length;
                    }
                    end++;
                    int type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end, 2));
                    end += 4;
                    string host = string.Join(".", labels);
                    bool exists = type == 1 || type == 28 && host == "dual-egress-test.invalid";
                    byte[] address = type == 28 ? IPAddress.IPv6Loopback.GetAddressBytes() : IPAddress.Loopback.GetAddressBytes();
                    var reply = new byte[end + (exists ? 12 + address.Length : 0)];
                    query.AsSpan(0, end).CopyTo(reply);
                    reply[2] = 0x81;
                    reply[3] = 0x80;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6, 2), (ushort)(exists ? 1 : 0));
                    reply.AsSpan(8, 4).Clear();
                    if (exists)
                    {
                        reply[end] = 0xc0;
                        reply[end + 1] = 0x0c;
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 2, 2), (ushort)type);
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 4, 2), 1);
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(end + 6, 4), 60);
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(end + 10, 2), (ushort)address.Length);
                        address.CopyTo(reply, end + 12);
                    }
                    await dns.SendAsync(reply, request.RemoteEndPoint, token);
                }
            }
            catch (OperationCanceledException) { }
        }
        Task servingDns = DnsServer();
        var clients = new ConcurrentBag<Task>();
        void Check(bool pass, string name)
        {
            if (!pass) throw new IOException(name);
            checks.Add(name);
        }
        async Task Serve(TcpListener listener, string family)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    if (family == "IPv4") Interlocked.Increment(ref accepted4);
                    clients.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var header = new StringBuilder();
                            var b = new byte[1];
                            while (!header.ToString().EndsWith("\r\n\r\n"))
                            {
                                if (await stream.ReadAsync(b, token) == 0) return;
                                header.Append((char)b[0]);
                                if (header.Length > 4096) throw new IOException("Test header too large");
                            }
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Length: {family.Length}\r\nConnection: close\r\n\r\n{family}"), token);
                        }
                    }, token));
                }
            }
            catch (OperationCanceledException) { }
        }
        Task serving6 = Serve(v6, "IPv6"), serving4 = Serve(v4, "IPv4");
        try
        {
            await using var router = new DownloadRouter { ProductionPolicies = true };
            await router.Start(false);
            router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
            router.Enable("bilibili", true);
            int FreePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            int proxyPort = FreePort(), proxy6Port = FreePort(), controlPort = FreePort(), coreDnsPort = FreePort();
            var config = JsonSerializer.SerializeToNode(NativeCapture.Config(router, controlPort, "", false))!;
            var origin = config["proxies"]!.AsArray().SingleOrDefault(p => p!["name"]!.GetValue<string>() == "IPv6Origin");
            Check(origin?["ip-version"]?.GetValue<string>() == "ipv6",
                "Recaptured IPv6 origin has an IPv6-only outbound, not IPv6 preference");
            Check(config["rules"]![0]!.GetValue<string>() ==
                "AND,((PROCESS-NAME," + Path.GetFileName(Environment.ProcessPath!) + "),(SRC-IP-CIDR,::/0)),IPv6Origin",
                "IPv6-only origin rule precedes the generic process DIRECT bypass");
            config["mixed-port"] = proxyPort;
            config["bind-address"] = "127.0.0.1";
            config["listeners"] = new JsonArray(new JsonObject
            {
                ["name"] = "ipv6-origin-probe", ["type"] = "mixed",
                ["listen"] = "::1", ["port"] = proxy6Port, ["udp"] = false
            });
            config["dns"] = new JsonObject
            {
                ["enable"] = true, ["ipv6"] = true, ["enhanced-mode"] = "redir-host",
                ["listen"] = "127.0.0.1:" + coreDnsPort,
                ["nameserver"] = new JsonArray("udp://127.0.0.1:" + dnsPort),
                ["default-nameserver"] = new JsonArray("127.0.0.1:" + dnsPort)
            };
            config["hosts"] = new JsonObject { ["egress-test.invalid"] = "127.0.0.1" };
            config["sniffer"]!["sniff"] = new JsonObject
            {
                ["HTTP"] = new JsonObject { ["ports"] = new JsonArray(originPort) }
            };
            string originRule = config["rules"]![0]!.GetValue<string>();
            config["rules"] = new JsonArray(originRule,
                "DOMAIN,egress-test.invalid,DIRECT", "DOMAIN,test.bilivideo.com,BrowserRelay", "MATCH,REJECT");
            string configPath = Path.Combine(folder, "config.json");
            File.WriteAllText(configPath, config.ToJsonString());
            var start = new ProcessStartInfo(NativeCapture.CorePath)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-d", folder, "-f", configPath }) start.ArgumentList.Add(arg);
            core = Process.Start(start) ?? throw new IOException("Core failed to start");
            core.OutputDataReceived += (_, _) => { };
            core.ErrorDataReceived += (_, _) => { };
            core.BeginOutputReadLine();
            core.BeginErrorReadLine();
            using var api = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            bool ready = false;
            for (int attempt = 0; attempt < 40 && !core.HasExited; attempt++)
            {
                try
                {
                    using var response = await api.GetAsync($"http://127.0.0.1:{controlPort}/configs", token);
                    ready = response.IsSuccessStatusCode;
                    if (ready) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, token);
            }
            Check(ready, "Isolated production core starts without TUN or system changes");
            async Task<string> Request(string address, string host = "egress-test.invalid", bool ipv6Source = false)
            {
                using var client = new TcpClient(ipv6Source ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                await client.ConnectAsync(ipv6Source ? IPAddress.IPv6Loopback : IPAddress.Loopback,
                    ipv6Source ? proxy6Port : proxyPort, token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"CONNECT {address}:{originPort} HTTP/1.1\r\nHost: {address}:{originPort}\r\n\r\n"), token);
                var header = new StringBuilder();
                var b = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n"))
                {
                    if (await stream.ReadAsync(b, token) == 0) throw new IOException("CONNECT closed");
                    header.Append((char)b[0]);
                    if (header.Length > 4096) throw new IOException("CONNECT header too large");
                }
                if (!header.ToString().StartsWith("HTTP/1.1 200")) throw new IOException("CONNECT failed");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"GET / HTTP/1.1\r\nHost: {host}:{originPort}\r\nConnection: close\r\n\r\n"), token);
                using var reader = new StreamReader(stream, Encoding.ASCII);
                return await reader.ReadToEndAsync(token);
            }
            string reply6 = await Request("[::1]", "dual-egress-test.invalid", true);
            Check(reply6.EndsWith("\r\n\r\nIPv6"),
                "IPv6 source guard survives destination rewrite and reaches the IPv6 listener");
            string reply4 = await Request("127.0.0.1");
            Check(reply4.EndsWith("\r\n\r\nIPv4"),
                "Explicit IPv4 destinations remain usable for disabled-service routing");
            Check(config["sniffer"]!["enable"]!.GetValue<bool>(),
                "Domain sniffing remains enabled for native application classification");
            Check(config["sniffer"]!["override-destination"]!.GetValue<bool>(),
                "Sniffed hostname remains available to the HTTP content relay");
            Check((await Request("[::1]", "test.bilivideo.com")).EndsWith("\r\n\r\nIPv6") &&
                router.Counters["bilibili"].V6 > 0 && router.Counters["bilibili"].V4 == 0 &&
                router.Counters["global"].V6 == 0,
                "Pure-IP native content request retains its hostname and strict Bilibili classification");
            // Populate redir-host's reverse mapping before the literal IPv6 request.
            var dnsQuestion = new List<byte> { 0x16, 0x28, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (string label in "dual-egress-test.invalid".Split('.'))
            {
                dnsQuestion.Add((byte)label.Length);
                dnsQuestion.AddRange(Encoding.ASCII.GetBytes(label));
            }
            dnsQuestion.AddRange(new byte[] { 0, 0, 28, 0, 1 });
            using (var seed = new UdpClient())
            {
                await seed.SendAsync(dnsQuestion.ToArray(), new IPEndPoint(IPAddress.Loopback, coreDnsPort), token);
                var response = await seed.ReceiveAsync(token);
                Check(BinaryPrimitives.ReadUInt16BigEndian(response.Buffer.AsSpan(6, 2)) > 0,
                    "Real redir-host DNS response seeds the IPv6 reverse mapping");
            }
            Check((await Request("[::1]", "dual-egress-test.invalid", true)).EndsWith("\r\n\r\nIPv6"),
                "DNS-mapped origin with an IPv4 alternative reaches the actual IPv6 listener");
            bool blocked = false;
            int before4 = Volatile.Read(ref accepted4);
            try { blocked = !(await Request("v4-egress-test.invalid", "v4-egress-test.invalid", true)).EndsWith("\r\n\r\nIPv4"); }
            catch (IOException) { blocked = true; }
            Check(blocked && Volatile.Read(ref accepted4) == before4,
                "IPv6-only origin rejects an IPv4-only DNS answer without contacting IPv4 origin");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally
        {
            if (core != null)
            {
                if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync(); }
                core.Dispose();
            }
            timeout.Cancel();
            await Task.WhenAll(serving6, serving4, servingDns);
            await Task.WhenAll(clients);
            v6.Stop();
            v4.Stop();
            try { File.Delete(Path.Combine(folder, "config.json")); } catch (IOException) { }
        }
    }).GetAwaiter().GetResult();
}
