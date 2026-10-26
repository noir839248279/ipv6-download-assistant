using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class MixedPolicyTests
{
    internal static void Run(string path)
    {
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new IOException(name);checks.Add(name);}
        try
        {
            Task.Run(async () =>
            {
                using var origin=new TcpListener(IPAddress.IPv6Any,0);
                origin.Server.DualMode=true;origin.Start();
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var serving=Task.Run(async () =>
                {
                    try
                    {
                        while(!stop.IsCancellationRequested)
                        {
                            var client=await origin.AcceptTcpClientAsync(stop.Token);
                            _=Task.Run(async () =>
                            {
                                using(client)
                                try
                                {
                                    var stream=client.GetStream();
                                    byte[] b=new byte[1];int end=0;
                                    while(end!=0x0d0a0d0a){if(await stream.ReadAsync(b,stop.Token)==0)return;end=(end<<8)|b[0];}
                                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(),stop.Token);
                                }
                                catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){}
                            });
                        }
                    }
                    catch(OperationCanceledException){}
                });
                int port=((IPEndPoint)origin.LocalEndpoint).Port;
                await using var router=new DownloadRouter{ProductionPolicies=true};
                await router.Start(false);
                int originalQueries=0;
                router.Resolve=_=>Task.FromResult(new[]{IPAddress.Loopback});
                router.OriginalResolve=_=>{originalQueries++;return Task.FromResult(new[]{IPAddress.Loopback});};
                router.EnableAll();
                using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+router.Port)})
                    {Timeout=TimeSpan.FromSeconds(3)};
                async Task<bool> Ok(string host){using var r=await http.GetAsync($"http://{host}:{port}/");return r.IsSuccessStatusCode;}
                foreach(var pair in DownloadRouter.AuxiliaryHosts.GroupBy(p=>p.Value).Select(g=>g.First()))
                {
                    string site=pair.Value;
                    router.SetMode(site,ServiceMode.Strict);
                    int before=originalQueries;
                    Check(!await Ok(pair.Key)&&originalQueries==before,$"{site}: strict auxiliary never queries original IPv4");
                    router.SetMode(site,ServiceMode.Mixed);
                    Check(await Ok(pair.Key)&&router.Counters[site].V4>0&&router.Counters[site].Auxiliary.Used>0,
                        $"{site}: mixed auxiliary IPv4 is metered and charged");
                }
                foreach(string host in new[]{"dl-test.quark.cn","test.bilivideo.com","test.googlevideo.com","test.steamcontent.com","download.epicgames.com","unknown.example"})
                {
                    string id=DownloadRouter.Classify(host)??"global";
                    router.SetMode(id,ServiceMode.Mixed);
                    int before=originalQueries;
                    Check(!await Ok(host)&&originalQueries==before,$"{id}: mixed content or unknown host cannot fall back");
                }
                router.Resolve=_=>Task.FromResult(new[]{IPAddress.IPv6Loopback});
                Check(await Ok("fresh.bilivideo.com")&&router.Counters["bilibili"].V6>0,"Mixed content succeeds on actual IPv6 socket");
                long used=router.Counters["quark"].Auxiliary.Used;
                router.Reset();router.Enable("quark",false);router.Enable("quark",true);
                Check(router.Counters["quark"].Auxiliary.Used==used,"Reset and toggles cannot refill the allowance");
                var allowance=router.Counters["quark"].Auxiliary;
                while(allowance.Used<AuxiliaryBudget.Limit)allowance.Reserve(65536);
                int beforeCap=originalQueries;
                Check(!await Ok("drive.quark.cn")&&originalQueries==beforeCap,"Exhausted allowance blocks before opening original connection");
                Check(router.Counters["quark"].State.Contains("额度"),"Allowance exhaustion has actionable state");
                var concurrent=new AuxiliaryBudget();
                long reserved=0;
                await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(() =>
                {
                    try{while(true)Interlocked.Add(ref reserved,concurrent.Reserve(7919));}
                    catch(AuxiliaryBudgetException){}
                })));
                Check(reserved==AuxiliaryBudget.Limit&&concurrent.Used==AuxiliaryBudget.Limit,"Concurrent auxiliary tunnels share one atomic cap");
                var refund=new AuxiliaryBudget();
                using(var wrapped=new AuxiliaryStream(new MemoryStream(new byte[3]),refund))
                {
                    Check(await wrapped.ReadAsync(new byte[100])==3&&refund.Used==3,"Short reads refund unused reservations");
                    Check(await wrapped.ReadAsync(new byte[100])==0&&refund.Used==3,"EOF does not consume allowance");
                }
                var oversized=new AuxiliaryBudget();
                long received=0;
                using(var wrapped=new AuxiliaryStream(new MemoryStream(new byte[3*1024*1024]),oversized))
                {
                    byte[] buffer=new byte[65536];
                    try{while(true){int n=await wrapped.ReadAsync(buffer);if(n==0)break;received+=n;}}
                    catch(AuxiliaryBudgetException){}
                }
                Check(received==AuxiliaryBudget.Limit,"An auxiliary host serving a large body is cut at the cap");
                using(var proxy=new TcpListener(IPAddress.Loopback,0))
                {
                    proxy.Start();
                    var response=Task.Run(async () =>
                    {
                        using var client=await proxy.AcceptTcpClientAsync(stop.Token);
                        var stream=client.GetStream();
                        await ReadHeader(stream,stop.Token);
                        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),stop.Token);
                        await ReadHeader(stream,stop.Token);
                        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(),stop.Token);
                    });
                    long old4=router.Counters["bilibili"].V4,oldAllowance=router.Counters["bilibili"].Auxiliary.Used;
                    router.FallbackProxy="127.0.0.1:"+((IPEndPoint)proxy.LocalEndpoint).Port;
                    Check(await Ok("api.bilibili.com"),"Mixed auxiliary can retain an original upstream proxy");
                    await response;
                    Check(router.Counters["bilibili"].Proxy>0&&router.Counters["bilibili"].V4==old4&&
                        router.Counters["bilibili"].Auxiliary.Used>oldAllowance,
                        "Unknown proxy exit is charged to allowance without claiming IPv4");
                }
                var observed=new DownloadCounter();
                PassiveTraffic.Account(observed,2048,100,"IPv4",false);
                PassiveTraffic.Account(observed,4096,200,"IPv6",false);
                PassiveTraffic.Account(observed,8192,300,"IPv4",true);
                DownloadRouter.SampleCounters(new[]{observed},1);
                Check(observed.V4==2048&&observed.V6==4096&&observed.Proxy==8192&&observed.Rate4==2048,
                    "Disabled direct observations produce protocol totals and rates; TUN stays unknown");
                observed.Enabled=true;PassiveTraffic.Account(observed,999,999,"IPv4",false);
                Check(observed.V4==2048,"Enabled relay service is excluded from passive accounting");
                stop.Cancel();await serving;
            }).GetAwaiter().GetResult();
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex)
        {
            Environment.ExitCode=1;
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    static async Task ReadHeader(Stream stream,CancellationToken ct)
    {
        byte[] buffer=new byte[1];int end=0;
        while(end!=0x0d0a0d0a){if(await stream.ReadAsync(buffer,ct)==0)throw new IOException("Fixture EOF");end=(end<<8)|buffer[0];}
    }
}
