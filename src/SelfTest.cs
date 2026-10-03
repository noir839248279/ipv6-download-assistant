using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Net.Http.Json;

namespace IPv6Lens;
internal static class SelfTest
{
    public static void Run(string? file)
    {
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new Exception("FAILED: "+name);checks.Add(name);}
        try {
            var store=new TrafficStore();store.Select(new[]{42});
            void Rx(string ip,int bytes,int pid=42)=>store.Add(pid,"TCP",IPAddress.Parse(ip),443,IPAddress.Parse("192.168.1.2"),50000,bytes,true);
            Rx("1.1.1.1",2000);Rx("2606:4700::1111",20_000_000);Rx("127.0.0.1",9_000_000);Rx("198.18.0.1",3_000_000);Rx("8.8.8.8",999999,99);
            var flows=store.Sample();var s=TrafficStore.Sum(flows);
            Check(s.V4==2000&&s.V6==20_000_000,"byte-weighted IPv4/IPv6 accounting and process isolation");
            Check(s.Local==9_000_000&&s.Other==3_000_000,"loopback and fake-IP not counted as public IPv4");
            Check(s.Share>99.9&&s.Verdict.Contains("主要经过 IPv6"),"control IPv4 cannot dominate IPv6 content conclusion");
            Check(TrafficStore.Sum(store.Sample()).Rate4==0,"idle sample clears rate instead of preserving stale speed");
            store.Select(new[]{42});Rx("1.1.1.1",20_000_000);Rx("2606:4700::1111",10);
            Check(TrafficStore.Sum(store.Sample()).Verdict.Contains("主要经过 IPv4"),"mere IPv6 connection does not imply IPv6 download");
            Check(TrafficStore.Scope(IPAddress.Parse("::1"))=="本机／代理候选"&&TrafficStore.Scope(IPAddress.Parse("::ffff:127.0.0.1"))=="本机／代理候选","IPv6 loopback normalization");
            Check(TrafficStore.Family(IPAddress.Parse("::ffff:1.1.1.1"))=="IPv4","IPv4-mapped addresses retain IPv4 accounting");
            Check(TrafficStore.Scope(IPAddress.Parse("fd00::1"))!="公网"&&TrafficStore.Scope(IPAddress.Parse("fe80::1"))!="公网","ULA and link-local excluded");
            Check(TrafficStore.Sum(Array.Empty<FlowView>()).Share==null,"no data is unknown, never 0 percent success");
            var msg=new BrowserMessage("tab123","rr1.googlevideo.com","2606:4700::1111","h3","YouTube",1234);
            Check(BrowserFeed.Valid(msg)&&!BrowserFeed.Valid(msg with{Host="evilgooglevideo.com"})&&!BrowserFeed.Valid(msg with{Bytes=-1}),"browser whitelist boundary and negative-byte rejection");
            var report=Report.Build(flows,new(){new("tab123","rr1.googlevideo.com","2606:4700::1111","h3","YouTube",1234,0)},null,"running",0,0);
            using var doc=JsonDocument.Parse(report);
            Check(!report.Contains("1.1.1.1")&&!report.Contains("2606:4700::1111")&&!report.Contains("rr1.googlevideo.com")&&!report.Contains("tab123"),"report strips raw endpoints and browsing identifiers");
            Check(Marshal.SizeOf<Ipv4Guard.Session>()==72&&Marshal.SizeOf<Ipv4Guard.Condition>()==40&&Marshal.SizeOf<Ipv4Guard.Filter>()==200,"WFP x64 ABI sizes");
            Check(Marshal.OffsetOf<Ipv4Guard.Filter>("Context").ToInt32()==152&&Marshal.OffsetOf<Ipv4Guard.Filter>("EffectiveWeight").ToInt32()==184,"WFP union alignment");
            Check(!Ipv4Guard.Allowed(Environment.ProcessPath!)&&!Ipv4Guard.Allowed(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","svchost.exe")),"self/system test target denial");
            var tcp=Processes.Connections();Check(tcp.All(c=>c.LocalPort is >=0 and <=65535 && c.RemotePort is >=0 and <=65535),"native TCP inventory parses port ranges");
            var selection=new AppProcess(42,0,"app.exe","C:\\fixture\\app.exe",1);
            Check(!Processes.Group(selection,new(){new(42,0,"different.exe","C:\\fixture\\different.exe",2)}).Contains(42),"PID reuse cannot silently select a different application");
            Task.Run(async()=> {
                await using var feed=new BrowserFeed();await feed.Start();
                using var http=new HttpClient(new HttpClientHandler{UseProxy=false});
                using var denied=await http.PostAsJsonAsync(feed.Endpoint,new[]{msg});
                Check(denied.StatusCode==HttpStatusCode.Unauthorized,"loopback API rejects unpaired clients");
                http.DefaultRequestHeaders.Add("X-IPv6Lens-Key",feed.Token);
                http.DefaultRequestHeaders.Add("X-IPv6Lens-Session","tab123");
                using var accepted=await http.PostAsJsonAsync(feed.Endpoint,new[]{msg});
                var data=feed.Sample();
                Check(accepted.IsSuccessStatusCode&&data.Single().Bytes==1234&&feed.ActiveSession=="tab123","browser loopback ingestion and tab isolation header");
                http.DefaultRequestHeaders.Add("X-IPv6Lens-Batch",Guid.NewGuid().ToString("N"));
                using var once=await http.PostAsJsonAsync(feed.Endpoint,new[]{msg});
                using var replay=await http.PostAsJsonAsync(feed.Endpoint,new[]{msg});
                Check(once.IsSuccessStatusCode&&replay.IsSuccessStatusCode&&feed.Sample().Single().Bytes==2468,"browser acknowledged-batch retries do not double count");
                http.DefaultRequestHeaders.Remove("X-IPv6Lens-Batch");
                using var invalid=await http.PostAsJsonAsync(feed.Endpoint,new[]{msg with{Bytes=-5}});
                Check(invalid.StatusCode==HttpStatusCode.BadRequest&&feed.Sample().Single().Bytes==2468,"malformed browser sample cannot corrupt totals");
            }).GetAwaiter().GetResult();
            File.WriteAllText(file??Path.Combine(AppContext.BaseDirectory,"self-test.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(file??Path.Combine(AppContext.BaseDirectory,"self-test.json"),ex.ToString());Environment.ExitCode=1;}
    }
}
