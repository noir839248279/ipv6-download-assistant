using System.Diagnostics;

namespace IPv6Lens;

// Observe bypass traffic without putting disabled services back through the relay.
internal sealed class PassiveTraffic : IDisposable
{
    readonly TrafficStore store = new() { RetainIdle = TimeSpan.FromMinutes(2) };
    readonly EtwCollector collector;
    readonly PassiveAccounting accounting = new();
    bool otherTun;
    DateTime nextProcesses;
    internal string Status => collector.Status + "；" + collector.DnsStatus;
    internal string Coverage => (collector.Status.StartsWith("正在采集") ? "关闭接管仍监测" : "旁路监测不可用") +
        (collector.DnsStatus.StartsWith("进程 DNS") ? " · 浏览器 / AList 按进程 DNS 关联" : " · 浏览器 / AList DNS 关联不可用") +
        (accounting.UnattributedReceived + accounting.UnattributedSent > 0 ? " · 有流量未归属" : " · 代理 / 加密 DNS 可能漏计");
    internal string ServiceStatus(string id) => collector.Status.StartsWith("正在采集")
        ? accounting.ServiceStatus(id) : "已关闭 · 监测不可用（见提示）";
    internal long Lost=>collector.Lost+store.Dropped;
    internal PassiveTraffic()
    {
        collector = new(store,"passive")
        {
            ProcessStarted = (pid, name) =>
            {
                if (accounting.HasOwner(pid))
                { accounting.Forget(pid); store.ForgetPid(pid); }
                return accounting.Track(pid, name);
            },
            ProcessStopped = pid => {
                if (accounting.HasOwner(pid))
                { accounting.Forget(pid); store.ForgetPid(pid); }
            },
            DnsObserved = (pid, host, results, status) => accounting.ObserveDns(pid, host, results, status, DateTime.UtcNow)
        };
    }
    internal void Start() { RefreshProcesses(); collector.Start(); }
    void RefreshProcesses()
    {
        var found = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    // Unsupported processes are not enrolled and do not retain flow state.
                    string name = process.ProcessName;
                    if (PassiveAccounting.SupportedProcess(name) && accounting.Track(process.Id, name, process.StartTime.ToUniversalTime().Ticks))
                    { found.Add(process.Id); store.AddPid(process.Id); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (int pid in accounting.Pids().Except(found))
        { accounting.Forget(pid); store.ForgetPid(pid); }
        nextProcesses = DateTime.UtcNow.AddSeconds(5);
        otherTun=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>
            n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up &&
            (n.Name.Contains("Clash",StringComparison.OrdinalIgnoreCase)||n.Name.Contains("Mihomo",StringComparison.OrdinalIgnoreCase)||
             n.Name.Equals("Meta",StringComparison.OrdinalIgnoreCase)||n.Description.Contains("Tunnel",StringComparison.OrdinalIgnoreCase)));
    }
    internal void Sample(IReadOnlyDictionary<string, DownloadCounter> counters, bool tunActive)
    {
        if (DateTime.UtcNow >= nextProcesses) RefreshProcesses();
        accounting.Sample(store.Sample(false), counters, tunActive || otherTun, DateTime.UtcNow);
    }
    internal static void Account(DownloadCounter row, long received, long sent, string family, bool tunActive)
    {
        if (row.Enabled) return;
        if (tunActive)
        {
            Interlocked.Add(ref row.Proxy, Math.Max(0, received)); Interlocked.Add(ref row.UploadProxy, Math.Max(0, sent));
            row.State = "旁路监测 · TUN 出口未知";
        }
        else if (family == "IPv4")
        {
            Interlocked.Add(ref row.V4, Math.Max(0, received)); Interlocked.Add(ref row.Upload4, Math.Max(0, sent));
            row.State = "直连监测 · IPv4";
        }
        else
        {
            Interlocked.Add(ref row.V6, Math.Max(0, received)); Interlocked.Add(ref row.Upload6, Math.Max(0, sent));
            row.State = "直连监测 · IPv6";
        }
    }
    public void Dispose() => collector.Dispose();
}
