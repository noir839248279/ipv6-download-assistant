using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace IPv6Lens;
public record BrowserMessage(string Session,string Host,string Address,string Protocol,string Site,long Bytes,string Evidence="known-cdn");
public record BrowserRow(string Session,string Host,string Address,string Protocol,string Site,long Bytes,double Rate,string Evidence="known-cdn");
public sealed class BrowserFeed : IAsyncDisposable
{
    readonly object gate=new();
    readonly Dictionary<string,BrowserRow> rows=new();
    readonly Dictionary<string,long> previous=new();
    readonly HashSet<string> acceptedBatches=new();
    readonly Queue<string> batchOrder=new();
    WebApplication? app;
    public string Token {get;}=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string Endpoint {get;private set;}="";
    public string Status {get;private set;}="尚未配对浏览器扩展";
    public DateTime LastSeen {get;private set;}
    public bool Incomplete {get;private set;}
    public string ActiveSession {get;private set;}="";
    DateTime sampled=DateTime.UtcNow;
    public async Task Start()
    {
        var builder=WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=> {k.Listen(IPAddress.Loopback,0);k.Limits.MaxRequestBodySize=32768;});
        app=builder.Build();
        app.MapPost("/traffic",async (HttpContext context)=> {
            if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-IPv6Lens-Key"].ToString()),Encoding.UTF8.GetBytes(Token))) return Results.Unauthorized();
            try {
                var messages=await context.Request.ReadFromJsonAsync<BrowserMessage[]>();
                if(messages==null || messages.Length>100 || messages.Any(x=>!Valid(x))) return Results.BadRequest();
                lock(gate) {
                    string batch=context.Request.Headers["X-IPv6Lens-Batch"].ToString();
                    if(batch.Length>0 && !Guid.TryParseExact(batch,"N",out _))return Results.BadRequest();
                    if(batch.Length>0 && acceptedBatches.Contains(batch))return Results.Ok();
                    if(context.Request.Headers["X-IPv6Lens-Incomplete"]=="1") Incomplete=true;
                    var active=context.Request.Headers["X-IPv6Lens-Session"].ToString();
                    if(active.Length is >=1 and <=64 && active.All(char.IsAsciiLetterOrDigit)) ActiveSession=active;
                    foreach(var msg in messages) {
                        string key=$"{msg.Session}|{msg.Site}|{msg.Host}|{msg.Address}|{msg.Protocol}|{msg.Evidence}";
                        if(!rows.TryGetValue(key,out var row)) { if(rows.Count>=5000) {Incomplete=true;continue;} row=new(msg.Session,msg.Host,msg.Address,msg.Protocol,msg.Site,0,0,msg.Evidence); }
                        rows[key]=row with {Bytes=row.Bytes+msg.Bytes};
                    }
                    if(batch.Length>0){acceptedBatches.Add(batch);batchOrder.Enqueue(batch);while(batchOrder.Count>10000)acceptedBatches.Remove(batchOrder.Dequeue());}
                    LastSeen=DateTime.UtcNow; Status="扩展已连接；CDP 编码传输字节，不含完整链路开销";
                }
                return Results.Ok();
            } catch { return Results.BadRequest(); }
        });
        await app.StartAsync(); Endpoint=app.Urls.Single()+"/traffic";
    }
    public static bool Valid(BrowserMessage m) => m!=null && m.Session!=null && m.Session.Length is >=1 and <=64 && m.Session.All(char.IsAsciiLetterOrDigit) && m.Host!=null && m.Host.Length<=253 && Uri.CheckHostName(m.Host)==UriHostNameType.Dns && m.Address!=null && (m.Address=="unknown" || IPAddress.TryParse(m.Address,out _)) && m.Protocol is "h2" or "h3" or "http/1.1" or "other" && m.Bytes>=0 && m.Bytes<=1_000_000_000 && m.Site is "Bilibili" or "YouTube" && (m.Evidence is "media-mime" or "media-type" or "cdn-video-path" or "youtube-ump" || m.Evidence=="known-cdn" && ((m.Site=="Bilibili" && (m.Host=="bilivideo.com" || m.Host.EndsWith(".bilivideo.com") || m.Host=="bilivideo.cn" || m.Host.EndsWith(".bilivideo.cn"))) || (m.Site=="YouTube" && (m.Host=="googlevideo.com" || m.Host.EndsWith(".googlevideo.com")))));
    public List<BrowserRow> Sample()
    {
        lock(gate) {
            var now=DateTime.UtcNow; double seconds=Math.Max(.001,(now-sampled).TotalSeconds); sampled=now;
            var list=new List<BrowserRow>();
            foreach(var item in rows) { previous.TryGetValue(item.Key,out long old); list.Add(item.Value with {Rate=(item.Value.Bytes-old)/seconds}); previous[item.Key]=item.Value.Bytes; }
            return list.OrderByDescending(x=>x.Rate).ThenByDescending(x=>x.Bytes).ToList();
        }
    }
    public void Clear() {lock(gate) {rows.Clear();previous.Clear();Incomplete=false;sampled=DateTime.UtcNow;}}
    public async ValueTask DisposeAsync() {if(app!=null) {await app.StopAsync();await app.DisposeAsync();}}
}
