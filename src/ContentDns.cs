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
    public void Dispose()=>http.Dispose();
}
