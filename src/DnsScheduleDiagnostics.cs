using System.Net;

namespace IPv6Lens;

internal sealed class DnsScheduleDiagnostics
{
    internal sealed record Entry(DateTime TimeUtc, string Site, string Host, string Action, string? Reason);
    internal sealed record Service(string Site, long Queries, long Answers, long DnsFallbacks,
        long ConnectFailures, long Recovered, long RouteFailures, Entry? Last);
    sealed record HostState(IPAddress[] Scoped, bool Retrying);
    readonly object gate = new();
    readonly Dictionary<string, Service> services = new();
    readonly Dictionary<string, HostState> hosts = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<Entry> events = new();
    DateTime lastSave;
    internal string? Directory;
    internal volatile string? LastReport, ReportError;

    internal void Record(string host, string action, string? reason = null, IPAddress[]? scoped = null)
    {
        string? site = ContentDns.ScopedSite(host);
        if (site == null) return;
        bool save;
        lock (gate)
        {
            if(action=="ipv6_route_failed"&&!hosts.ContainsKey(host))return;
            var entry = new Entry(DateTime.UtcNow, site, host, action, reason);
            var old = services.GetValueOrDefault(site) ?? new(site, 0, 0, 0, 0, 0, 0, null);
            services[site] = old with {
                Queries = old.Queries + (action == "query" ? 1 : 0),
                Answers = old.Answers + (action == "scoped_answer" ? 1 : 0),
                DnsFallbacks = old.DnsFallbacks + (action == "dns_fallback" ? 1 : 0),
                ConnectFailures = old.ConnectFailures + (action == "scoped_connect_failed" ? 1 : 0),
                Recovered = old.Recovered + (action == "fallback_connected" ? 1 : 0),
                RouteFailures = old.RouteFailures + (action == "ipv6_route_failed" ? 1 : 0),
                Last = entry
            };
            events.Enqueue(entry);
            while (events.Count > 80) events.Dequeue();
            if (scoped != null || action == "dns_fallback")
            {
                if (!hosts.ContainsKey(host) && hosts.Count >= 128) hosts.Remove(hosts.Keys.First());
                hosts[host] = new(scoped ?? Array.Empty<IPAddress>(),
                    action == "dns_fallback" || hosts.GetValueOrDefault(host)?.Retrying == true);
            }
            save = Directory != null && action is "dns_fallback" or "scoped_connect_failed" or "ipv6_route_failed"
                && DateTime.UtcNow - lastSave >= TimeSpan.FromMinutes(5);
            if (save) lastSave = DateTime.UtcNow;
        }
        if (save)
        {
            try
            {
                LastReport = DiagnosticReportStore.Write(Directory!, "dns-scheduling",
                    new { schema = "ipv6-dns-scheduling/1", timeUtc = DateTime.UtcNow, scheduling = Snapshot() });
                ReportError = null;
            }
            catch (Exception ex) { ReportError = ex.GetType().Name; }
        }
    }
    internal bool NeedsOriginalFallback(string host)
    {
        lock(gate)return hosts.TryGetValue(host,out var state)&&state.Retrying;
    }
    internal void ConnectionFailed(string host, IPAddress[] candidates)
    {
        lock (gate)
        {
            if (!hosts.TryGetValue(host, out var state) || !candidates.Any(state.Scoped.Contains)) return;
            hosts[host] = state with { Retrying = true };
        }
        Record(host, "scoped_connect_failed", "retry_original_ipv6_sources");
    }
    internal void Connected(string host, IPAddress address)
    {
        bool retrying;
        lock (gate)
        {
            if (!hosts.TryGetValue(host, out var state)) return;
            retrying = state.Retrying;
            if (!retrying && !state.Scoped.Contains(address)) return;
            hosts[host] = state with { Retrying = false };
        }
        Record(host, retrying ? "fallback_connected" : "scoped_connected", "tcp_only_not_throughput");
    }
    internal Service? ForService(string site) { lock (gate) return services.GetValueOrDefault(site); }
    internal object Snapshot()
    {
        lock (gate) return new { services = services.Values.ToArray(), recent = events.ToArray(),
            trackedHosts = hosts.Count, lastReport = LastReport, reportError = ReportError };
    }
    internal string? Warning(string site)
    {
        var row = ForService(site);
        if (row == null || row.DnsFallbacks + row.ConnectFailures == 0) return null;
        return $"网段选址失败 {row.DnsFallbacks + row.ConnectFailures} 次 · " +
            (row.Last?.Action == "ipv6_route_failed" ? "IPv6 兜底也失败" : "已启用原解析兜底");
    }
    internal string Describe(string site)
    {
        var row = ForService(site);
        if (row == null) return "网段选址：尚无查询；已有客户端 IPv6 和手选节点仍优先。";
        return $"网段选址：查询 {row.Queries}，有 IPv6 答案 {row.Answers}，解析兜底 {row.DnsFallbacks}，" +
            $"候选建连失败批次 {row.ConnectFailures}，兜底建连成功 {row.Recovered}，最终连接失败 {row.RouteFailures}。\n" +
            $"最近：{row.Last?.TimeUtc.ToLocalTime():HH:mm:ss} {row.Last?.Host} / {row.Last?.Action} / {row.Last?.Reason}\n" +
            "兜底仍仅 IPv6；建连成功不代表下载速度更快。详细记录见导出报告的 dnsScheduling。";
    }
}
