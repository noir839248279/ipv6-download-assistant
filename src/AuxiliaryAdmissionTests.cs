using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class AuxiliaryAdmissionTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        try
        {
            CheckLimits(Check);
            Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var origin = new TcpListener(IPAddress.IPv6Any, 0);
                origin.Server.DualMode = true;
                origin.Start();
                int port = ((IPEndPoint)origin.LocalEndpoint).Port;
                var workers = new List<Task>();
                async Task Serve()
                {
                    try
                    {
                        while (true)
                        {
                            var client = await origin.AcceptTcpClientAsync(stop.Token);
                            workers.Add(Task.Run(async () =>
                            {
                                using (client)
                                try
                                {
                                    await Header(client.GetStream(), stop.Token);
                                    await client.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), stop.Token);
                                }
                                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                            }));
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                var serving = Serve();
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
                router.OriginalResolve = _ => Task.FromResult(new[] { IPAddress.Loopback });
                router.Enable("bilibili", true); router.SetMode("bilibili", ServiceMode.Mixed);
                router.Enable("youtube", true); router.SetMode("youtube", ServiceMode.Mixed);
                var held = new List<TcpClient>();
                async Task<string> Tunnel(string host, bool keep = false)
                {
                    var client = new TcpClient();
                    try
                    {
                        await client.ConnectAsync(IPAddress.Loopback, router.Port, stop.Token);
                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"), stop.Token);
                        string response = await Header(client.GetStream(), stop.Token);
                        if (keep && response.StartsWith("HTTP/1.1 200"))
                        { held.Add(client); client = null!; }
                        return response;
                    }
                    finally { client?.Dispose(); }
                }
                try
                {
                    for (int i = 0; i < 8; i++)
                        Check((await Tunnel("www.bilibili.com", true)).StartsWith("HTTP/1.1 200"), "Admits bounded auxiliary tunnel " + (i + 1));
                    string busy = await Tunnel("www.bilibili.com");
                    Check(busy.StartsWith("HTTP/1.1 503") && busy.Contains("Retry-After:"),
                        "Ninth same-host auxiliary tunnel is rejected before consuming upstream resources");
                    Check((await Tunnel("accounts.google.com", true)).StartsWith("HTTP/1.1 200"),
                        "A busy auxiliary host does not block another service");
                    using var http = new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + router.Port) })
                        { Timeout = TimeSpan.FromSeconds(3) };
                    Check(await http.GetStringAsync($"http://content.bilivideo.com:{port}/") == "ok" && router.Counters["bilibili"].V6 > 0,
                        "IPv6 content still transfers during auxiliary saturation");
                    var first = held[0].GetStream();
                    await first.WriteAsync("GET / HTTP/1.1\r\nHost: fixture\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                    held[0].Client.Shutdown(SocketShutdown.Send);
                    await first.CopyToAsync(Stream.Null, stop.Token);
                    bool recovered = false;
                    for (int i = 0; i < 40 && !recovered; i++)
                    {
                        await Task.Delay(25, stop.Token);
                        recovered = (await Tunnel("www.bilibili.com", true)).StartsWith("HTTP/1.1 200");
                    }
                    Check(recovered, "Normal EOF releases the auxiliary permit");
                    using var proxy = new TcpListener(IPAddress.Loopback, 0);
                    proxy.Start();
                    router.FallbackProxy = "127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
                    int attempts = 0;
                    var rejecting = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                using var client = await proxy.AcceptTcpClientAsync(stop.Token);
                                Interlocked.Increment(ref attempts);
                                await Header(client.GetStream(), stop.Token);
                                await client.GetStream().WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop.Token);
                            }
                        }
                        catch (OperationCanceledException) { }
                    });
                    try
                    {
                        for (int i = 0; i < 3; i++)
                            Check((await Tunnel("api.bilibili.com")).StartsWith("HTTP/1.1 502"), "Records upstream failure " + (i + 1));
                        Check((await Tunnel("api.bilibili.com")).StartsWith("HTTP/1.1 503") && attempts == 3,
                            "Repeated failed upstream connections cool down without more proxy attempts");
                        Check(await http.GetStringAsync($"http://other.bilivideo.com:{port}/") == "ok",
                            "Auxiliary cooldown does not alter content IPv6 routing");
                        router.Enable("bilibili", false);
                        Check((await Tunnel("api.bilibili.com")).StartsWith("HTTP/1.1 502") && attempts == 4,
                            "Disabled service is not retained in auxiliary cooldown");
                    }
                    finally { stop.Cancel(); await rejecting; }
                }
                finally
                {
                    foreach (var client in held) client.Dispose();
                    stop.Cancel(); await serving; await Task.WhenAll(workers);
                }
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }
    static void CheckLimits(Action<bool, string> check)
    {
        long now = 1000;
        var gate = new AuxiliaryAdmission(new[] { "a", "b", "c", "d", "e" }, () => now);
        var held = new List<AuxiliaryAdmission.Lease>();
        foreach (string host in new[] { "a", "b", "c", "d" })
            for (int i = 0; i < AuxiliaryAdmission.PerHostLimit; i++)
            {
                if (!gate.TryEnter(host, out var lease, out _)) throw new IOException("Unexpected fixture admission failure");
                held.Add(lease!);
            }
        check(!gate.TryEnter("e", out _, out _), "Combined auxiliary tunnels cannot exceed 32 slots");
        foreach (var lease in held) { lease.Dispose(); lease.Dispose(); }
        check(gate.TryEnter("e", out var recovered, out _), "Disposal releases slots exactly once");
        recovered!.Dispose();
        gate.TryEnter("e", out var earlier, out _);
        for (int i = 0; i < 3; i++)
        {
            gate.TryEnter("e", out var failed, out _);
            failed!.ConnectFailed(); failed.Dispose();
        }
        earlier!.Connected(); earlier.Dispose();
        check(!gate.TryEnter("e", out _, out int retry) && retry == 15,
            "An older in-flight success cannot erase a newly opened cooldown");
        now += 15000;
        check(gate.TryEnter("e", out var after, out _), "Cooldown expires automatically without toggling services");
        after!.Connected(); after.Dispose();
    }
    static async Task<string> Header(Stream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1]; int end = 0;
        while (end != 0x0d0a0d0a && text.Length < 8192)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Unexpected fixture EOF");
            text.Append((char)one[0]); end = (end << 8) | one[0];
        }
        return text.ToString();
    }
}
