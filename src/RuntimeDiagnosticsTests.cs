using System.Text.Json;

namespace IPv6Lens;

internal static class RuntimeDiagnosticsTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "runtime-fixture-" + Guid.NewGuid().ToString("N"));
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); checks.Add(message); }
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RuntimeDiagnostics.Sample Sample(int seconds, long received = 128 * 1024, int active = 1, long failures = 0,
            double cpu = 0, bool enabled = true, long started = 1, int pid = 123) =>
            new(start.AddSeconds(seconds), new(pid, cpu, 20, 1000),
                new[] { new RuntimeDiagnostics.ServiceSample("quark", enabled, 0, received, 0, 0, failures, active, started, received) });
        RuntimeDiagnostics Monitor(string name) => new(Path.Combine(root, name), () => new { scope = "local fixture; no upstream calls" });
        int Files(string name) => Directory.Exists(Path.Combine(root, name)) ? Directory.GetFiles(Path.Combine(root, name), "*.json").Length : 0;
        try
        {
            var slow = Monitor("slow");
            for (int s = 0; s <= 50; s += 10) slow.Observe(Sample(s, 128 * 1024 + s * 1000));
            Check(Files("slow") == 0, "Short low-throughput windows do not report");
            slow.Observe(Sample(60, 188 * 1024));
            Check(Files("slow") == 1 && slow.Status.Trigger == "low_throughput_candidate:quark",
                "Connected slow content produces a report without any ingress failure");
            using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "slow", slow.Status.LastReport!))))
            {
                Check(saved.RootElement.GetProperty("history").GetArrayLength() == 7 &&
                    saved.RootElement.GetProperty("interpretation").GetString()!.Contains("not proof of buffering"),
                    "Saved report includes bounded independent history and the idle/buffering limitation");
                Check(saved.RootElement.GetProperty("version").GetString() == typeof(DownloadRouter).Assembly.GetName().Version!.ToString(),
                    "Report version comes from the assembly");
            }
            slow.Observe(Sample(70));
            Check(Files("slow") == 1, "Automatic slowdown report obeys cooldown even when counters reset");

            var idle = Monitor("idle");
            for (int s = 0; s <= 80; s += 10) idle.Observe(Sample(s, active: 0));
            Check(Files("idle") == 0, "Idle service without active content does not report a slowdown");
            var disabled = Monitor("disabled");
            for (int s = 0; s <= 80; s += 10) disabled.Observe(Sample(s, failures: s * 100, enabled: false));
            Check(Files("disabled") == 0, "Disabled service does not trigger content slowdown or failure reports");
            var fast = Monitor("fast");
            for (int s = 0; s <= 80; s += 10) fast.Observe(Sample(s, 128 * 1024 + s * 1024 * 1024));
            Check(Files("fast") == 0, "Fast content is not mistaken for low throughput");
            var gap = Monitor("gap");
            gap.Observe(Sample(0)); gap.Observe(Sample(90));
            Check(Files("gap") == 0, "A scheduling gap is not accumulated as slow-transfer time");

            var burst = Monitor("burst");
            burst.Observe(Sample(0)); burst.Observe(Sample(10, failures: 10));
            Check(Files("burst") == 1 && burst.Status.Trigger == "failure_burst:quark",
                "Failure burst reports even when the local entry remains responsive");
            burst.Observe(Sample(20, failures: 40)); burst.Observe(Sample(30, failures: 60));
            Check(Files("burst") == 1, "Repeated failure bursts cannot flood disk");
            burst.Observe(Sample(300, failures: 70)); burst.Observe(Sample(310, failures: 80));
            Check(Files("burst") == 2, "Automatic reporting resumes after cooldown");

            var cpu = Monitor("cpu");
            cpu.Observe(Sample(0, active: 0, cpu: 55));
            cpu.Observe(Sample(10, active: 0, cpu: 55)); cpu.Observe(Sample(20, active: 0, cpu: 55));
            Check(Files("cpu") == 0, "Transient CPU load is not reported as sustained");
            cpu.Observe(Sample(30, active: 0, cpu: 55));
            Check(Files("cpu") == 1 && cpu.Status.Trigger == "sustained_core_high_cpu",
                "Sustained dedicated core CPU automatically captures a report with no UI timer");
            var restart = Monitor("restart");
            restart.Observe(Sample(0, active: 0, cpu: 55));
            restart.Observe(Sample(10, active: 0, cpu: 55));
            restart.Observe(Sample(20, active: 0, cpu: 55, pid: 124));
            restart.Observe(Sample(30, active: 0, cpu: 55, pid: 124));
            Check(Files("restart") == 0, "Core restart resets sustained CPU history");

            burst.RequestManual();
            Check(burst.Status.ManualPending, "Manual recording immediately exposes a pending state");
            burst.Observe(Sample(320, failures: 80));
            Check(Files("burst") == 3 && burst.Status.Trigger!.StartsWith("user_reported_slowdown") && !burst.Status.ManualPending,
                "Manual slowdown recording bypasses automatic cooldown without changing network policy");
            burst.RequestManual(); burst.Observe(Sample(321, failures: 80));
            Check(Files("burst") == 3 && !burst.Status.ManualPending, "Repeated manual clicks are coalesced and rate limited");
            for (int s = 330; s <= 1800; s += 10) burst.Observe(Sample(s, failures: 80, active: 0));
            Check(burst.History().Length == 120, "Runtime history is capped at 120 samples");

            Directory.CreateDirectory(root);
            string blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var disk = new RuntimeDiagnostics(blocked, () => new { });
            disk.RequestManual(); disk.Observe(Sample(0));
            Check(disk.Status.Error != null && disk.Status.LastReport == null, "Report write failure is exposed instead of silently swallowed");
            File.Delete(blocked);
            disk.RequestManual(); disk.Observe(Sample(10));
            Check(disk.Status.Error == null && disk.Status.LastReport != null, "Successful subsequent recording clears the write error");

            for (int n = 0; n < 23; n++) DiagnosticReportStore.Write(Path.Combine(root, "retention"), "runtime", new { n });
            Check(Files("retention") == 20 && Directory.GetFiles(Path.Combine(root, "retention"), "*.tmp").Length == 0,
                "Report retention keeps twenty complete files with no temporary remnants");
            var sampler = new CoreProcessSampler();
            Check(sampler.Sample(null) == null, "Missing core does not produce a fabricated CPU value");
            var measured = sampler.Sample(Environment.ProcessId);
            Check(measured?.Pid == Environment.ProcessId && measured.Threads > 0 && measured.CpuWholeMachinePercent == null,
                "First process observation captures identity and threads without inventing a CPU rate");
            Check(sampler.Sample(Environment.ProcessId)?.CpuWholeMachinePercent is >= 0,
                "Second process observation derives actual whole-machine CPU delta");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
            Environment.ExitCode = 1;
        }
    }
}
