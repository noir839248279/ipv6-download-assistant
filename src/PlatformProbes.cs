using System.Net;
using System.Text.Json;
namespace IPv6Lens;
internal static class PlatformProbes
{
    // Explicit test entry only. Public roots are reachability tests, never file-download evidence.
    internal static void Run(string path)=>Task.Run(async()=>{
        var results=new List<object>();
        await using var router=new DownloadRouter{ProductionPolicies=true,FallbackProxy="127.0.0.1:7897"};
        await router.Start(false);router.EnableAll();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+router.Port),AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(18)};
        foreach(var host in new[]{"dl-pc-sz.drive.quark.cn","cn-sdjn-cm-02-02.bilivideo.com","dl.steam.clngaa.com","dl.steam.ksyna.com","download.epicgames.com","epicgames-download1.akamaized.net","rr1---sn-3v2upjvh-puhs.googlevideo.com"}){
            string site=DownloadRouter.ContentSite(host)??"global";var row=router.Counters[site];long a=row.V6,b=row.V4;
            try{using var response=await http.SendAsync(new HttpRequestMessage(HttpMethod.Head,(site=="steam"?"http://":"https://")+host+"/"),HttpCompletionOption.ResponseHeadersRead);results.Add(new{host,status=(int)response.StatusCode,ipv6=row.V6-a,ipv4=row.V4-b,meaning="Root HEAD only; not actual file validation"});}
            catch(Exception ex){results.Add(new{host,error=ex.GetType().Name,ipv6=row.V6-a,ipv4=row.V4-b,meaning="No fallback; failure does not establish permanent IPv6 incompatibility"});}
        }
        // Fetch public video metadata through the user's existing Codex proxy, then test a bounded IPv6 segment.
        string stage="video metadata",videoHost="";try{
            using var meta=new HttpClient(new HttpClientHandler{Proxy=new WebProxy("http://127.0.0.1:7897"),UseProxy=true}){Timeout=TimeSpan.FromSeconds(12)};
            meta.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");meta.DefaultRequestHeaders.Referrer=new Uri("https://www.bilibili.com/");
            using var view=JsonDocument.Parse(await meta.GetStringAsync("https://api.bilibili.com/x/web-interface/view?bvid=BV1GJ411x7h7"));
            long cid=view.RootElement.GetProperty("data").GetProperty("cid").GetInt64();
            stage="playback metadata";using var play=JsonDocument.Parse(await meta.GetStringAsync("https://api.bilibili.com/x/player/playurl?bvid=BV1GJ411x7h7&cid="+cid+"&qn=16&fnval=16&fourk=0"));
            var data=play.RootElement.GetProperty("data");var urls=new List<string>();
            if(data.TryGetProperty("dash",out var dash)){
                var video=dash.GetProperty("video")[0];urls.Add(video.GetProperty("baseUrl").GetString()!);
                if(video.TryGetProperty("backupUrl",out var backup)&&backup.ValueKind==JsonValueKind.Array)urls.AddRange(backup.EnumerateArray().Select(x=>x.GetString()!));
            }else urls.Add(data.GetProperty("durl")[0].GetProperty("url").GetString()!);
            foreach(string url in urls.Distinct().Take(4)){
                var uri=new Uri(url);videoHost=uri.IdnHost;stage="IPv6 media range";if(DownloadRouter.ContentSite(uri.IdnHost)!="bilibili")continue;
                try{
                    using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.Range=new(0,65535);request.Headers.Referrer=new Uri("https://www.bilibili.com/");request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                    long before=router.Counters["bilibili"].V6;using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
                    byte[] buffer=new byte[65536];int received=0;using var timeout=new CancellationTokenSource(10000);await using var body=await response.Content.ReadAsStreamAsync();
                    if(response.IsSuccessStatusCode)while(received<buffer.Length){int n=await body.ReadAsync(buffer.AsMemory(received),timeout.Token);if(n==0)break;received+=n;}
                    results.Add(new{host=uri.IdnHost,status=(int)response.StatusCode,mime=response.Content.Headers.ContentType?.MediaType,bodyBytes=received,ipv6=router.Counters["bilibili"].V6-before,meaning="Public Bilibili video bounded Range; URL and payload not retained"});
                    if(received==65536)break;
                }catch(Exception ex){results.Add(new{test="Bilibili candidate",host=videoHost,error=ex.GetType().Name});}
            }
        }catch(Exception ex){results.Add(new{test="Bilibili bounded public video",stage,host=videoHost,error=ex.GetType().Name});}
        File.WriteAllText(path,JsonSerializer.Serialize(new{timeUtc=DateTime.UtcNow,systemSettingsChanged=false,results,servers=router.ServerReport()},new JsonSerializerOptions{WriteIndented=true}));
    }).GetAwaiter().GetResult();
}
