using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace IPv6Lens;

internal static class RelayLifetimeTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        try
        {
            Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(38));
                using var inbound = new TcpListener(IPAddress.Loopback, 0);
                using var outbound = new TcpListener(IPAddress.IPv6Loopback, 0);
                inbound.Start(); outbound.Start();
                using var caller = new TcpClient();
                await caller.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)inbound.LocalEndpoint).Port, deadline.Token);
                using var accepted = await inbound.AcceptTcpClientAsync(deadline.Token);
                using var upstream = new TcpClient(AddressFamily.InterNetworkV6);
                await upstream.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)outbound.LocalEndpoint).Port, deadline.Token);
                using var origin = await outbound.AcceptTcpClientAsync(deadline.Token);
                var callerStream = caller.GetStream();
                var originStream = origin.GetStream();
                var counter = new DownloadCounter();
                var relay = DownloadRouter.Relay(accepted.GetStream(), upstream.GetStream(),
                    accepted.Client, upstream.Client, counter, true, false, deadline.Token);
                caller.Client.Shutdown(SocketShutdown.Send);
                var one = new byte[1];
                if (await originStream.ReadAsync(one, deadline.Token) != 0)
                    throw new IOException("Upload EOF did not reach origin");
                checks.Add("Client half-close reaches origin without closing download");
                var watch = Stopwatch.StartNew();
                int received = 0;
                try
                {
                    while (watch.Elapsed < TimeSpan.FromSeconds(32))
                    {
                        await originStream.WriteAsync(new byte[] { 42 }, deadline.Token);
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        readTimeout.CancelAfter(1500);
                        if (await callerStream.ReadAsync(one, readTimeout.Token) != 1 || one[0] != 42)
                            throw new IOException("Active half-closed transfer was truncated");
                        received++;
                        await Task.Delay(250, deadline.Token);
                    }
                    origin.Client.Shutdown(SocketShutdown.Send);
                    await relay.WaitAsync(deadline.Token);
                }
                finally
                {
                    deadline.Cancel();
                    try { await relay; } catch when (deadline.IsCancellationRequested) { }
                }
                if (counter.V6 != received || counter.V4 != 0 || counter.Proxy != 0)
                    throw new IOException("Transfer accounting changed");
                checks.Add("Active half-closed IPv6 download survives the former 30-second cutoff");
                checks.Add("Exact IPv6 accounting and normal EOF completion are preserved");
                await CheckIdleAndCancellation(checks);
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }

    static async Task CheckIdleAndCancellation(List<string> checks)
    {
        foreach (string scenario in new[] { "client-eof", "server-eof", "fully-open" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var incoming = new TcpListener(IPAddress.Loopback, 0);
            using var outgoing = new TcpListener(IPAddress.Loopback, 0);
            incoming.Start(); outgoing.Start();
            using var caller = new TcpClient();
            await caller.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)incoming.LocalEndpoint).Port, deadline.Token);
            using var client = await incoming.AcceptTcpClientAsync(deadline.Token);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)outgoing.LocalEndpoint).Port, deadline.Token);
            using var origin = await outgoing.AcceptTcpClientAsync(deadline.Token);
            var relay = DownloadRouter.Relay(client.GetStream(), upstream.GetStream(), client.Client, upstream.Client,
                new DownloadCounter(), false, false, deadline.Token, halfCloseIdle: TimeSpan.FromMilliseconds(200));
            try
            {
                if (scenario == "fully-open")
                {
                    await Task.Delay(450, deadline.Token);
                    if (relay.IsCompleted) throw new IOException("Fully open connection gained an idle timeout");
                    deadline.Cancel();
                    try { await relay; throw new IOException("Cancellation did not propagate"); }
                    catch (OperationCanceledException) { }
                    checks.Add("Fully open connections have no added idle cutoff and cancellation releases both pumps");
                }
                else
                {
                    (scenario == "client-eof" ? caller : origin).Client.Shutdown(SocketShutdown.Send);
                    try { await relay.WaitAsync(TimeSpan.FromSeconds(2)); throw new IOException("Half-closed idle connection retained"); }
                    catch (TimeoutException) when (relay.IsCompleted) { }
                    checks.Add(scenario + ": genuinely idle half-closed connection is reclaimed");
                }
            }
            finally { deadline.Cancel(); try { await relay; } catch { } }
        }
    }
}
