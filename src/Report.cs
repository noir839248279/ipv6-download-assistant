using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;
public static class Report
{
    public static string Build(List<FlowView> flows,List<BrowserRow> browser,NetworkReport? network,string capture,long lost,long dropped,bool browserIncomplete=false)
    {
        // New secret for each export: identifiers only correlate inside that report.
        using var hmac=new HMACSHA256(RandomNumberGenerator.GetBytes(32));
        string Id(string text) => Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)))[..12];
        return JsonSerializer.Serialize(new {
            schema="ipv6-lens-report/1",version="1.0.0",exportedUtc=DateTime.UtcNow,
            units="decimal bytes; ETW transport receive bytes; CDP encoded transfer bytes kept separate",
            privacy="No raw IP, domain, PID, process name/path, URL, headers, titles, file name, token or payload. Per-export random pseudonyms.",
            collector=capture.StartsWith("正在采集")?"running":"unavailable_or_stopped",eventsLost=lost,eventsDropped=dropped,browserIncomplete,
            network=network==null?null:new {network.Time,network.GlobalV6,network.V4,network.Verdict,probes=network.Probes.Select(p=>new{p.Host,p.Family,p.Success,p.Detail})},
            totals=TrafficStore.Sum(flows),
            flows=flows.Select(f=>new{process=Id(f.Key.Pid.ToString()),server=Id(f.Key.Remote),f.Key.RemotePort,f.Key.Transport,f.Family,f.Scope,f.Received,f.Sent,f.Rate,f.Role}),
            browser=browser.Select(b=>new {session=Id(b.Session),server=Id(b.Address),host=Id(b.Host),b.Site,b.Protocol,b.Bytes,b.Rate,b.Evidence,scope=System.Net.IPAddress.TryParse(b.Address,out var ip)?TrafficStore.Scope(ip):"unknown",family=System.Net.IPAddress.TryParse(b.Address,out var ip2)?TrafficStore.Family(ip2):"unknown"}),
            limitations=new[]{"ETW losses, offload, retransmission and observation start can affect accounting; not carrier billing bytes.","Small traffic is not proof of authentication; large traffic is only a content candidate.","DNS cache association is a candidate, not proof of a process hostname.","Local proxy egress cannot be attributed to individual clients; remote proxy onward route is unknown.","VPN/TUN can change the actual physical address family; no campus free-traffic entitlement is inferred.","Browser data is not summed into ETW totals; unrecognized CDN/worker/iframe requests may be missing."}
        },new JsonSerializerOptions {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    }
}
