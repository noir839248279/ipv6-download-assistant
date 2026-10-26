using System.Net;

namespace IPv6Lens;

// DNS correlation is scoped to the querying process, never a machine-wide IP guess.
internal sealed class PassiveAccounting
{
    internal const int MaxAssociations = 4096, MaxFlows = 20000;
    sealed record Owner(string Name, long Birth, string? Site);
    sealed record Association(string? Site, DateTime Expires);
    sealed record Baseline(long Rx, long Tx, DateTime Seen, string? Site, int Revision, bool Enabled);
    readonly object gate = new();
    readonly Dictionary<int, Owner> owners = new();
    readonly Dictionary<(int Pid, string Ip), Association> names = new();
    readonly Dictionary<FlowKey, Baseline> previous = new();
    readonly Dictionary<string, string> states = new();
    DateTime nextDnsPrune;
    internal long UnattributedReceived { get; private set; }
    internal long UnattributedSent { get; private set; }
    internal int AssociationCount { get { lock (gate) return names.Count; } }
    internal int FlowCount { get { lock (gate) return previous.Count; } }

    static bool Identify(string name, out string? site)
    {
        site = name switch
        {
            "quark" => "quark",
            "bilibili" => "bilibili",
            "steam" or "steamservice" or "steamwebhelper" => "steam",
            "epicgameslauncher" or "epicwebhelper" => "epic",
            _ => null
        };
        return site != null || name is "alist" or "openlist" or "baomihua" or
            "chrome" or "msedge" or "firefox" or "dot" or "msedgewebview2" or "quarkbrowser";
    }
    internal static bool SupportedProcess(string name) =>
        Identify(Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), out _);
    internal bool Track(int pid, string processName, long birth = 0)
    {
        string name = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();
        if (pid <= 0 || !Identify(name, out string? site)) return false;
        lock (gate)
        {
            if (owners.TryGetValue(pid, out var old) &&
                (old.Name != name || old.Birth != 0 && birth != 0 && old.Birth != birth))
                ForgetCore(pid);
            if (owners.Count >= MaxAssociations && !owners.ContainsKey(pid)) return false;
            owners[pid] = new(name, birth == 0 ? owners.GetValueOrDefault(pid)?.Birth ?? 0 : birth, site);
            return true;
        }
    }
    internal void Forget(int pid) { lock (gate) ForgetCore(pid); }
    void ForgetCore(int pid)
    {
        if (!owners.Remove(pid)) return;
        foreach (var key in names.Keys.Where(k => k.Pid == pid).ToArray()) names.Remove(key);
        foreach (var key in previous.Keys.Where(k => k.Pid == pid).ToArray()) previous.Remove(key);
    }
    internal int[] Pids() { lock (gate) return owners.Keys.ToArray(); }
    internal bool HasOwner(int pid) { lock (gate) return owners.ContainsKey(pid); }
    internal void ObserveDns(int pid, string host, string results, uint status, DateTime now)
    {
        if (status != 0 || host.Length > 253 || results.Length > 8192) return;
        host = host.TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return;
        lock (gate)
        {
            if (!owners.TryGetValue(pid, out var owner) || owner.Site != null) return;
            if (now >= nextDnsPrune)
            {
                foreach (var key in names.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) names.Remove(key);
                nextDnsPrune = now.AddSeconds(5);
            }
            string? site = DownloadRouter.Classify(host);
            foreach (string token in results.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(32))
            {
                if (!IPAddress.TryParse(token, out var ip) || TrafficStore.Scope(ip) != "公网") continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var key = (pid, ip.ToString());
                string? value = names.TryGetValue(key, out var old) && old.Expires > now && old.Site != site ? null : site;
                if (names.Count < MaxAssociations || names.ContainsKey(key))
                    names[key] = new(value, now.AddSeconds(60));
            }
        }
    }
    internal void Sample(IReadOnlyCollection<FlowView> flows, IReadOnlyDictionary<string, DownloadCounter> counters,
        bool tunActive, DateTime now)
    {
        lock (gate)
        {
            foreach (var key in previous.Where(p => p.Value.Seen < now.AddMinutes(-2)).Select(p => p.Key).ToArray()) previous.Remove(key);
            foreach (var key in names.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) names.Remove(key);
            foreach (string id in counters.Keys)
                states[id] = id == "global" ? "原路径 · 未分类流量不归属" : "已关闭 · 被动监测等待流量";
            foreach (var flow in flows)
            {
                if (!owners.TryGetValue(flow.Key.Pid, out var owner)) continue;
                if (!previous.TryGetValue(flow.Key, out var old) && previous.Count >= MaxFlows) continue;
                long rx = Math.Max(0, flow.Received - (old?.Rx ?? 0));
                long tx = Math.Max(0, flow.Sent - (old?.Tx ?? 0));
                var address = IPAddress.Parse(flow.Key.Remote);
                string ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
                var match = names.GetValueOrDefault((flow.Key.Pid, ip));
                string? site = owner.Site;
                if (site == null)
                {
                    // Keep an established association after its DNS window expires, but
                    // stop attributing it when another observed query makes the IP ambiguous.
                    site = match != null ? match.Site : old?.Site;
                }
                DownloadCounter? row = site != null ? counters.GetValueOrDefault(site) : null;
                previous[flow.Key] = new(flow.Received, flow.Sent, flow.LastSeen, site,
                    row?.RouteRevision ?? 0, row?.Enabled ?? false);
                if (flow.Scope != "公网" || rx + tx == 0) continue;
                if (row == null)
                {
                    UnattributedReceived += rx; UnattributedSent += tx;
                    continue;
                }
                if (row.Enabled || old?.Enabled == true ||
                    old != null && (old.Site != site || old.Revision != row.RouteRevision)) continue;
                PassiveTraffic.Account(row, rx, tx, flow.Family, tunActive);
                states[site!] = row.State = tunActive ? "关闭监测 · TUN 出口未知" :
                    owner.Site == null ? "关闭监测 · DNS 关联 " + flow.Family : "关闭监测 · " + flow.Family;
            }
        }
    }
    internal string ServiceStatus(string id)
    {
        lock (gate) return states.GetValueOrDefault(id, "已关闭 · 被动监测等待流量");
    }
}
