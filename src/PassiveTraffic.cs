using System.Diagnostics;

namespace IPv6Lens;

// Observe bypass traffic without putting disabled services back through the relay.
internal sealed class PassiveTraffic : IDisposable
{
    readonly TrafficStore store = new();
    readonly EtwCollector collector;
    readonly Dictionary<int, string> owners = new();
    readonly Dictionary<FlowKey, (long Rx, long Tx)> previous = new();
    readonly Dictionary<string,bool> wasEnabled = new();
    bool otherTun;
    DateTime nextProcesses;
    internal string Status => collector.Status;
    internal long Lost=>collector.Lost+store.Dropped;
    internal PassiveTraffic() { collector = new(store,"passive"); }
    internal void Start() { RefreshProcesses(); collector.Start(); }
    void RefreshProcesses()
    {
        var found = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    string? site = process.ProcessName.ToLowerInvariant() switch
                    {
                        "quark" => "quark",
                        "steam" or "steamservice" or "steamwebhelper" => "steam",
                        "epicgameslauncher" or "epicwebhelper" => "epic",
                        _ => null
                    };
                    if (site != null) { found[process.Id] = site; store.AddPid(process.Id); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (int pid in owners.Keys.Except(found.Keys)) store.RemovePid(pid);
        owners.Clear();
        foreach (var pair in found) owners[pair.Key] = pair.Value;
        nextProcesses = DateTime.UtcNow.AddSeconds(5);
        otherTun=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>
            n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up &&
            (n.Name.Contains("Clash",StringComparison.OrdinalIgnoreCase)||n.Name.Contains("Mihomo",StringComparison.OrdinalIgnoreCase)||
             n.Name.Equals("Meta",StringComparison.OrdinalIgnoreCase)||n.Description.Contains("Tunnel",StringComparison.OrdinalIgnoreCase)));
    }
    internal void Sample(IReadOnlyDictionary<string, DownloadCounter> counters, bool tunActive)
    {
        if (DateTime.UtcNow >= nextProcesses) RefreshProcesses();
        foreach (var flow in store.Sample())
        {
            var old = previous.GetValueOrDefault(flow.Key);
            previous[flow.Key] = (flow.Received, flow.Sent);
            if (!owners.TryGetValue(flow.Key.Pid, out string? id) || !counters.TryGetValue(id, out var row) ||
                row.Enabled || wasEnabled.GetValueOrDefault(id) || flow.Scope != "公网") continue;
            Account(row, flow.Received - old.Rx, flow.Sent - old.Tx, flow.Family, tunActive||otherTun);
        }
        foreach(var pair in counters)wasEnabled[pair.Key]=pair.Value.Enabled;
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
