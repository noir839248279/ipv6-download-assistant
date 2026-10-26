using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class IngressDiagnosticsTests
{
    internal static void Run(string path) => Task.Run(async () =>
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); checks.Add(message); }
        string reports = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ingress-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var router = new DownloadRouter { ProductionPolicies = true };
            await router.Start(false);
            router.Enable("quark", true);
            router.Resolve = _ => throw new IOException("Probe must not resolve any host");
            router.OwnershipProbe = () => throw new IOException("Probe must not query WinInet");
            var probe = await Task.Run(() => IngressDiagnostics.Probe(router.Port, router.DiagnosticPath));
            Check(probe.Result == "ok", "Local entry replies without DNS, upstream or ownership calls");
            Check(!router.ProtectionLost && router.Counters["quark"].Enabled &&
                router.Counters.Values.All(c => c.V4 + c.V6 + c.Proxy + c.Failures + c.Upload4 + c.Upload6 + c.UploadProxy == 0),
                "Health probe leaves policy and service accounting untouched");
            using (var pending = new TcpClient())
            {
                await pending.ConnectAsync(IPAddress.Loopback, router.Port, timeout.Token);
                bool found = false;
                for (int i = 0; i < 50 && !found; i++)
                {
                    var health = JsonSerializer.SerializeToElement(router.Health());
                    found = health.GetProperty("work").EnumerateArray().Any(w => w.GetProperty("stage").GetString() == "request_header");
                    if (!found) await Task.Delay(10, timeout.Token);
                }
                Check(found, "Pending request headers appear even before a service session exists");
            }

            router.OwnershipProbe = () => true;
            router.Resolve = _ => Task.FromResult(new[] { IPAddress.IPv6Loopback });
            using var origin = new TcpListener(IPAddress.IPv6Loopback, 0);
            origin.Start();
            int port = ((IPEndPoint)origin.LocalEndpoint).Port;
            using var transfer = new TcpClient();
            await transfer.ConnectAsync(IPAddress.Loopback, router.Port, timeout.Token);
            var input = transfer.GetStream();
            await input.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT dl-fixture.quark.cn:{port} HTTP/1.1\r\nHost: dl-fixture.quark.cn\r\n\r\n"), timeout.Token);
            using var upstream = await origin.AcceptTcpClientAsync(timeout.Token);
            var response = new List<byte>();
            byte[] one = new byte[1];
            while (!Encoding.ASCII.GetString(response.ToArray()).EndsWith("\r\n\r\n"))
            {
                if (await input.ReadAsync(one, timeout.Token) == 0) throw new IOException("Tunnel closed");
                response.Add(one[0]);
            }
            Check(Encoding.ASCII.GetString(response.ToArray()).StartsWith("HTTP/1.1 200"), "Local IPv6 content transfer established");
            using var diagnostics = new IngressDiagnostics(router.Port, router.DiagnosticPath, router.IngressSnapshot, reports);
            var failure = new IngressDiagnostics.ProbeResult(DateTime.UtcNow, "local_response", "TimedOut", 1000);
            diagnostics.Observe(failure); diagnostics.Observe(failure);
            Check(!Directory.Exists(reports), "One or two failed samples do not produce a report");
            diagnostics.Observe(failure);
            string file = Directory.GetFiles(reports).Single();
            var report = JsonDocument.Parse(File.ReadAllText(file));
            Check(report.RootElement.GetProperty("router").GetProperty("health").GetProperty("work")
                .EnumerateArray().Any(w => w.GetProperty("stage").GetString() == "transfer"), "Automatic snapshot captures active transfer stage");
            Check(!File.ReadAllText(file).Contains(router.DiagnosticPath), "Report omits the private probe path");
            diagnostics.Observe(failure);
            diagnostics.Observe(failure with { Result = "ok" });
            diagnostics.Observe(failure); diagnostics.Observe(failure); diagnostics.Observe(failure);
            Check(Directory.GetFiles(reports).Length == 1, "Repeated failures and flapping obey the five-minute report cooldown");
            probe = await Task.Run(() => IngressDiagnostics.Probe(router.Port, router.DiagnosticPath));
            await upstream.GetStream().WriteAsync("still-connected"u8.ToArray(), timeout.Token);
            byte[] payload = new byte[15];
            await input.ReadExactlyAsync(payload, timeout.Token);
            Check(probe.Result == "ok" && Encoding.ASCII.GetString(payload) == "still-connected" &&
                router.Counters["quark"].Enabled && router.Counters["quark"].V6 == 15 &&
                router.Counters["quark"].V4 == 0 && router.Counters["quark"].Proxy == 0,
                "Probes and fault reports preserve active IPv6 transfers without IPv4 fallback");
            var runtimeSample = router.RuntimeSample().Services.Single(s => s.Id == "quark");
            Check(runtimeSample.ActiveContent == 1 && runtimeSample.ContentStarted == 1 &&
                runtimeSample.ContentReceived == 15 && router.Counters["quark"].Last6 == 0,
                "Runtime sampling sees real content progress without consuming the UI sampling clock");

            await using var stalled = new DownloadRouter();
            stalled.AcceptClient = async (l, ct) => { await Task.Delay(Timeout.Infinite, ct); return await l.AcceptTcpClientAsync(ct); };
            await stalled.Start(false);
            string stalledReports = Path.Combine(reports, "stalled");
            int runtimeSamples = 0;
            using (var monitor = new IngressDiagnostics(stalled.Port, stalled.DiagnosticPath, stalled.IngressSnapshot,
                stalledReports, TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref runtimeSamples)))
            {
                monitor.Start();
                for (int i = 0; i < 70 && monitor.LastReport == null; i++) await Task.Delay(100, timeout.Token);
                Check(monitor.LastReport != null && monitor.LastProbe?.Stage == "local_response" &&
                    monitor.LastProbe.Result != "ok", "Dedicated watchdog detects stalled accept progress and automatically saves a report");
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(stalledReports, monitor.LastReport!)));
                var listener = saved.RootElement.GetProperty("router").GetProperty("health").GetProperty("listeners")[0];
                Check(listener.GetProperty("accepted").GetInt64() == 0 &&
                    listener.GetProperty("taskStatus").GetString() != "Faulted",
                    "Report distinguishes an unfinished accept task from actual accept progress");
                Check(Volatile.Read(ref runtimeSamples) >= 3, "Independent runtime sampling continues while local entry probes fail");
            }
            using var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start(); int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
            probe = IngressDiagnostics.Probe(closedPort, "/fixture", 150);
            Check(probe.Stage == "tcp_connect" && probe.Result != "ok", "TCP entry failure is distinct from a missing local HTTP reply");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }).GetAwaiter().GetResult();
}
