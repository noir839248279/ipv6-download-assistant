using System.Diagnostics;
using System.Text.Json;

namespace IPv6Lens;

internal sealed class RuntimeDiagnostics
{
    internal sealed record ProcessSample(int Pid, double? CpuWholeMachinePercent, int Threads, long WorkingSet, string? Error = null);
    internal sealed record ServiceSample(string Id, bool Enabled, long IPv4, long IPv6, long ProxyUnknown,
        long Uploaded, long Failures, int ActiveContent, long ContentStarted, long ContentReceived);
    internal sealed record Sample(DateTime TimeUtc, ProcessSample? Core, ServiceSample[] Services);
    internal sealed record ServiceInterval(ServiceSample Totals, double IPv4RateBps, double IPv6RateBps,
        double ProxyUnknownRateBps, double UploadRateBps, long NewFailures, double ContentRateBps, double LowSeconds);
    internal sealed record Observation(DateTime TimeUtc, ProcessSample? Core, ServiceInterval[] Services);
    internal sealed record ReportStatus(string? LastReport, string? Error, string? Trigger, bool ManualPending);
    readonly object gate = new();
    readonly string directory;
    readonly Func<object> snapshot;
    readonly Queue<Observation> history = new();
    readonly Dictionary<string, double> lowSeconds = new();
    Sample? previous;
    DateTime lastAutomatic = DateTime.MinValue, lastManual = DateTime.MinValue;
    double highCpuSeconds;
    int manualPending;
    ReportStatus status = new(null, null, null, false);
    internal RuntimeDiagnostics(string directory, Func<object> snapshot)
    {
        this.directory = directory; this.snapshot = snapshot;
    }
    internal ReportStatus Status => Volatile.Read(ref status) with { ManualPending = Volatile.Read(ref manualPending) != 0 };
    internal void RequestManual() => Interlocked.Exchange(ref manualPending, 1);
    internal Observation[] History() { lock (gate) return history.ToArray(); }

    internal void Observe(Sample sample)
    {
        lock (gate)
        {
            double seconds = previous == null ? 0 : (sample.TimeUtc - previous.TimeUtc).TotalSeconds;
            // Long scheduling gaps and counter resets are not sustained slow-transfer evidence.
            bool contiguous = seconds > 0 && seconds <= 30;
            var intervals = new List<ServiceInterval>();
            var triggers = new List<string>();
            foreach (var row in sample.Services)
            {
                var old = previous?.Services.FirstOrDefault(s => s.Id == row.Id);
                bool valid = contiguous && old != null && row.IPv4 >= old.IPv4 && row.IPv6 >= old.IPv6 &&
                    row.ProxyUnknown >= old.ProxyUnknown && row.Uploaded >= old.Uploaded &&
                    row.ContentReceived >= old.ContentReceived && row.Failures >= old.Failures;
                long failures = valid ? row.Failures - old!.Failures : 0;
                double contentRate = valid ? (row.ContentReceived - old!.ContentReceived) / seconds : 0;
                double low = lowSeconds.GetValueOrDefault(row.Id);
                bool candidate = valid && row.Enabled && old!.Enabled && row.ActiveContent > 0 && old.ActiveContent > 0 &&
                    row.ContentReceived >= 64 * 1024 && contentRate < 1024 * 1024;
                low = candidate ? low + seconds : 0;
                lowSeconds[row.Id] = low;
                if (row.Enabled && failures >= 10) triggers.Add("failure_burst:" + row.Id);
                if (low >= 60) triggers.Add("low_throughput_candidate:" + row.Id);
                intervals.Add(new(row, valid ? (row.IPv4 - old!.IPv4) / seconds : 0,
                    valid ? (row.IPv6 - old!.IPv6) / seconds : 0, valid ? (row.ProxyUnknown - old!.ProxyUnknown) / seconds : 0,
                    valid ? (row.Uploaded - old!.Uploaded) / seconds : 0, failures, contentRate, low));
            }
            bool sameCore = sample.Core != null && previous?.Core?.Pid == sample.Core.Pid;
            highCpuSeconds = contiguous && sameCore && sample.Core?.CpuWholeMachinePercent >= 20
                ? highCpuSeconds + seconds : 0;
            if (highCpuSeconds >= 30) triggers.Add("sustained_core_high_cpu");
            history.Enqueue(new(sample.TimeUtc, sample.Core, intervals.ToArray()));
            while (history.Count > 120) history.Dequeue();
            previous = sample;
            bool manual = Interlocked.Exchange(ref manualPending, 0) != 0;
            if (manual && sample.TimeUtc - lastManual >= TimeSpan.FromSeconds(10))
            {
                lastManual = sample.TimeUtc;
                Save(sample.TimeUtc, new[] { "user_reported_slowdown" }.Concat(triggers).ToArray());
            }
            else if (triggers.Count > 0 && sample.TimeUtc - lastAutomatic >= TimeSpan.FromMinutes(5))
            {
                lastAutomatic = sample.TimeUtc;
                Save(sample.TimeUtc, triggers.ToArray());
            }
        }
    }
    void Save(DateTime time, string[] triggers)
    {
        try
        {
            var report = new
            {
                schema = "ipv6-download-assistant/runtime-1", version = typeof(RuntimeDiagnostics).Assembly.GetName().Version?.ToString(),
                timeUtc = time, triggers,
                interpretation = "Low throughput is a candidate, not proof of buffering: encrypted idle/paused/buffered streams are indistinguishable. Rates use independent counter deltas, not UI sampling. No routing, quota, node or network changes are performed.",
                thresholds = new { coreCpuWholeMachinePercent = 20, coreCpuSeconds = 30, failuresPerSample = 10,
                    contentBelowBytesPerSecond = 1048576, lowContentSeconds = 60, automaticCooldownSeconds = 300, historyLimit = 120 },
                history = history.ToArray(), router = snapshot(), localPlayback = PlaybackSnapshot()
            };
            string file = DiagnosticReportStore.Write(directory, "runtime", report);
            Volatile.Write(ref status, new(file, null, string.Join(", ", triggers), false));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref status, Status with { Error = ex.GetType().Name, Trigger = string.Join(", ", triggers) });
        }
    }
    static object PlaybackSnapshot()
    {
        try
        {
            var roles = new Dictionary<int, string>();
            foreach (string name in new[] { "alist", "BaoMiHua" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) roles[process.Id] = name;
            if (roles.Count == 0) return new { detected = false };
            var connections = Processes.Connections();
            return new
            {
                detected = true,
                processes = roles.Select(p => new { pid = p.Key, role = p.Value }).ToArray(),
                connections = connections.Where(c => roles.ContainsKey(c.Pid))
                    .GroupBy(c => new { role = roles[c.Pid], c.State, family = c.Remote.Contains(':') ? "IPv6" : "IPv4",
                        loopback = System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(c.Remote)), c.RemotePort })
                    .Select(g => new { g.Key, count = g.Count() }).Take(80).ToArray(),
                scope = "Endpoint family and loopback connection counts only; TUN/proxy final exit and player buffering intent are unknown. No URLs or media metadata."
            };
        }
        catch (Exception ex) { return new { error = ex.GetType().Name }; }
    }
}

internal sealed class CoreProcessSampler
{
    int previousPid;
    long previousTicks, previousCpuTicks;
    internal RuntimeDiagnostics.ProcessSample? Sample(int? pid)
    {
        if (pid == null) { previousPid = 0; return null; }
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            long now = Stopwatch.GetTimestamp(), cpu = process.TotalProcessorTime.Ticks;
            double? percent = previousPid == pid && now > previousTicks && cpu >= previousCpuTicks
                ? 100d * (cpu - previousCpuTicks) / TimeSpan.TicksPerSecond /
                    ((now - previousTicks) / (double)Stopwatch.Frequency) / Environment.ProcessorCount : null;
            previousPid = pid.Value; previousTicks = now; previousCpuTicks = cpu;
            return new(pid.Value, percent, process.Threads.Count, process.WorkingSet64);
        }
        catch (Exception ex)
        {
            previousPid = 0;
            return new(pid.Value, null, 0, 0, ex.GetType().Name);
        }
    }
}

internal static class DiagnosticReportStore
{
    internal static string Write(string directory, string prefix, object report)
    {
        Directory.CreateDirectory(directory);
        string name = prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json";
        string temporary = Path.Combine(directory, name + ".tmp"), file = Path.Combine(directory, name);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Retention failures must not hide a successfully written report.
        foreach (var old in new DirectoryInfo(directory).GetFiles(prefix + "-*.json").OrderByDescending(f => f.Name).Skip(20))
            try { old.Delete(); } catch { }
        return name;
    }
}
