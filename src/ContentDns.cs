using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace IPv6Lens;
internal sealed class ContentDns : IDisposable
{
    readonly HttpClient http;
    internal Func<string,CancellationToken,Task<NodeAddress[]>>[] Sources;
    public ContentDns(string proxy){
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseProxy=!string.IsNullOrWhiteSpace(proxy)};
        if(handler.UseProxy){if(proxy.Contains('='))proxy=proxy.Split(';').FirstOrDefault(p=>p.StartsWith("https=",StringComparison.OrdinalIgnoreCase))?[6..]??throw new IOException("不支持的 DNS 代理配置");handler.Proxy=new WebProxy("http://"+proxy);}
        http=new(handler){Timeout=TimeSpan.FromSeconds(5)};
        Sources=new Func<string,CancellationToken,Task<NodeAddress[]>>[]{SystemLookup,DohLookup};
    }
    public Task<IPAddress[]> Resolve(string host)=>Lookup(host,false);
    internal Task<IPAddress[]> ResolveAll(string host)=>Lookup(host,true);
    async Task<IPAddress[]> Lookup(string host,bool allSources){
        if(IPAddress.TryParse(host,out var literal))return new[]{literal};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var pending=Sources.Select(source=>Query(source,host,deadline.Token)).ToList();
        bool answered=false;
        var addresses=new List<IPAddress>();
        try{
            while(pending.Count>0){
                var task=await Task.WhenAny(pending);pending.Remove(task);
                var result=await task;answered|=result.Answered;
                addresses.AddRange(result.Rows.Select(n=>n.Address));
                if(!allSources&&addresses.Count>0)return addresses.Distinct().ToArray();
            }
            if(!answered)throw new HttpRequestException("IPv6 DNS sources unavailable");
            return addresses.Distinct().ToArray();
        }finally{deadline.Cancel();}
    }
    static async Task<(NodeAddress[] Rows,bool Answered)> Query(
        Func<string,CancellationToken,Task<NodeAddress[]>> source,string host,CancellationToken ct){
        try{
            var rows=await source(host,ct).WaitAsync(ct);
            // TTL zero forbids caching, not using the answer for this connection.
            return(rows.Where(n=>ContentNodes.PublicV6(n.Address)).ToArray(),true);
        }catch(Exception ex)when(ex is SocketException or TimeoutException or HttpRequestException or JsonException or OperationCanceledException){
            return(Array.Empty<NodeAddress>(),false);
        }
    }
    static async Task<NodeAddress[]> SystemLookup(string host,CancellationToken ct)=>
        (await Dns.GetHostAddressesAsync(host,ct))
            .Where(ContentNodes.PublicV6).Select(ip=>new NodeAddress(ip,DateTime.UtcNow.AddSeconds(30),"系统 DNS")).ToArray();
    async Task<NodeAddress[]> DohLookup(string host,CancellationToken ct){
                using var response=await http.GetAsync("https://dns.alidns.com/resolve?name="+Uri.EscapeDataString(host)+"&type=AAAA",ct);
                response.EnsureSuccessStatusCode();
                using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if(doc.RootElement.TryGetProperty("Status",out var status)&&status.GetInt32() is not (0 or 3))
                    throw new HttpRequestException("DNS resolver returned an error");
                if(!doc.RootElement.TryGetProperty("Answer",out var answers))return Array.Empty<NodeAddress>();
                int ttl=answers.EnumerateArray().Select(a=>a.TryGetProperty("TTL",out var t)?t.GetInt32():30).DefaultIfEmpty(30).Min();
                return answers.EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==28)
                    .Select(a=>IPAddress.TryParse(a.GetProperty("data").GetString(),out var ip)?ip:null)
                    .Where(ip=>ip!=null&&ContentNodes.PublicV6(ip))
                    .Select(ip=>new NodeAddress(ip!,DateTime.UtcNow.AddSeconds(Math.Clamp(ttl,0,300)),"AliDNS")).ToArray();
    }
    internal async Task<NodeAddress[]> Discover(string host,CancellationToken ct){
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var responses=await Task.WhenAll(Sources.Select(source=>Query(source,host,deadline.Token)));
        ct.ThrowIfCancellationRequested();
        if(responses.All(x=>!x.Answered))throw new HttpRequestException("IPv6 DNS sources unavailable");
        return responses.SelectMany(x=>x.Rows).GroupBy(x=>x.Address)
            .Select(g=>g.OrderByDescending(x=>x.ExpiresUtc).First()).ToArray();
    }
    public void Dispose()=>http.Dispose();
}
