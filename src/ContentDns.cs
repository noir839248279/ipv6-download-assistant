using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace IPv6Lens;
internal sealed class ContentDns : IDisposable
{
    readonly HttpClient http;
    public ContentDns(string proxy){
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseProxy=!string.IsNullOrWhiteSpace(proxy)};
        if(handler.UseProxy){if(proxy.Contains('='))proxy=proxy.Split(';').FirstOrDefault(p=>p.StartsWith("https=",StringComparison.OrdinalIgnoreCase))?[6..]??throw new IOException("不支持的 DNS 代理配置");handler.Proxy=new WebProxy("http://"+proxy);}
        http=new(handler){Timeout=TimeSpan.FromSeconds(5)};
    }
    public async Task<IPAddress[]> Resolve(string host){
        if(IPAddress.TryParse(host,out var literal))return new[]{literal};
        using var response=await http.GetAsync("https://dns.alidns.com/resolve?name="+Uri.EscapeDataString(host)+"&type=AAAA");response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result=new List<IPAddress>();
        if(doc.RootElement.TryGetProperty("Answer",out var answers))foreach(var a in answers.EnumerateArray())if(a.GetProperty("type").GetInt32()==28&&IPAddress.TryParse(a.GetProperty("data").GetString(),out var ip)&&ip.AddressFamily==AddressFamily.InterNetworkV6&&TrafficStore.Scope(ip)=="公网")result.Add(ip);
        return result.Distinct().ToArray();
    }
    internal async Task<NodeAddress[]> Discover(string host,CancellationToken ct){
        async Task<NodeAddress[]> SystemLookup(){
            try{return (await Dns.GetHostAddressesAsync(host,ct).WaitAsync(TimeSpan.FromSeconds(5),ct))
                .Where(ContentNodes.PublicV6).Select(ip=>new NodeAddress(ip,DateTime.UtcNow.AddSeconds(30),"系统 DNS")).ToArray();}
            catch(Exception ex)when(ex is SocketException or TimeoutException){return Array.Empty<NodeAddress>();}
        }
        async Task<NodeAddress[]> DohLookup(){
            try{
                using var response=await http.GetAsync("https://dns.alidns.com/resolve?name="+Uri.EscapeDataString(host)+"&type=AAAA",ct);
                response.EnsureSuccessStatusCode();
                using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if(!doc.RootElement.TryGetProperty("Answer",out var answers))return Array.Empty<NodeAddress>();
                int ttl=answers.EnumerateArray().Select(a=>a.TryGetProperty("TTL",out var t)?t.GetInt32():30).DefaultIfEmpty(30).Min();
                return answers.EnumerateArray().Where(a=>a.GetProperty("type").GetInt32()==28)
                    .Select(a=>IPAddress.TryParse(a.GetProperty("data").GetString(),out var ip)?ip:null)
                    .Where(ip=>ip!=null&&ContentNodes.PublicV6(ip))
                    .Select(ip=>new NodeAddress(ip!,DateTime.UtcNow.AddSeconds(Math.Clamp(ttl,0,300)),"AliDNS")).ToArray();
            }catch(Exception ex)when(ex is HttpRequestException or JsonException or OperationCanceledException){
                ct.ThrowIfCancellationRequested();return Array.Empty<NodeAddress>();
            }
        }
        var responses=await Task.WhenAll(SystemLookup(),DohLookup());
        ct.ThrowIfCancellationRequested();
        return responses.SelectMany(x=>x).GroupBy(x=>x.Address)
            .Select(g=>g.OrderByDescending(x=>x.ExpiresUtc).First()).ToArray();
    }
    public void Dispose()=>http.Dispose();
}
