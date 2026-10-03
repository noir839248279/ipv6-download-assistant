using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace IPv6Lens;
internal static class RelayResilienceTests
{
    public static void Run(string path){var checks=new List<string>();try{Task.Run(async()=>{
        using var origin=new TcpListener(IPAddress.IPv6Any,0);origin.Server.DualMode=true;origin.Start();
        int originPort=((IPEndPoint)origin.LocalEndpoint).Port;
        var serving=Task.Run(async()=>{try{while(true){var c=await origin.AcceptTcpClientAsync();_=Task.Run(async()=>{using(c){try{var s=c.GetStream();var b=new byte[1];int end=0;var header=new StringBuilder();while(end!=0x0d0a0d0a){if(await s.ReadAsync(b)==0)return;header.Append((char)b[0]);end=(end<<8)|b[0];}string reply=header.ToString().StartsWith("GET /redirect ")?$"HTTP/1.1 302 Found\r\nLocation: http://new-cdn.example:{originPort}/file\r\nContent-Length: 0\r\nConnection: close\r\n\r\n":"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";await s.WriteAsync(Encoding.ASCII.GetBytes(reply));}catch{}}});}}catch(ObjectDisposedException){}catch(SocketException){}});
        await using var router=new DownloadRouter{ProductionPolicies=true};
        var attempts=new ConcurrentDictionary<TcpListener,int>();
        router.AcceptClient=async(l,ct)=>{int n=attempts.AddOrUpdate(l,1,(_,v)=>v+1);if(n<=3){await Task.Yield();throw new SocketException((int)SocketError.ConnectionReset);}return await l.AcceptTcpClientAsync(ct);};
        await router.Start(false);router.Resolve=_=>Task.FromResult(new[]{IPAddress.IPv6Loopback});router.EnableAll();
        async Task Probe(int port){using var h=new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+port)};using var http=new HttpClient(h){Timeout=TimeSpan.FromSeconds(3)};string body=await http.GetStringAsync($"http://test.quark.cn:{originPort}/probe");if(body!="ok")throw new Exception("Unexpected relay body");}
        await Probe(router.Port);await Probe(router.SteamPort);await Probe(router.EpicPort);
        if(router.AcceptErrors!=9)throw new Exception("Accept errors missing from diagnostics");
        checks.Add("All three listeners recover after three injected accept ConnectionReset errors each");
        for(int batch=0;batch<20;batch++){
            await Task.WhenAll(Enumerable.Range(0,15).Select(async i=>{using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,router.Port);if(i%2==0){await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT partial"));c.Client.LingerState=new LingerOption(true,0);}}));
            await Probe(router.Port);
        }
        checks.Add("300 incomplete or aborted real TCP connections interleaved with successful requests");
        if(router.Counters["quark"].V4!=0||router.Counters["steam"].V4!=0||router.Counters["epic"].V4!=0)throw new Exception("IPv4 fallback detected");
        checks.Add("Recovered listeners retain IPv6-only transfer policy");
        using(var pairListener=new TcpListener(IPAddress.Loopback,0)){
            pairListener.Start();using var caller=new TcpClient();await caller.ConnectAsync(IPAddress.Loopback,((IPEndPoint)pairListener.LocalEndpoint).Port);using var accepted=await pairListener.AcceptTcpClientAsync();
            if(Processes.ClientOwner((IPEndPoint)accepted.Client.RemoteEndPoint!, (IPEndPoint)accepted.Client.LocalEndPoint!)!=Environment.ProcessId)throw new Exception("TCP owner mismatch");
            checks.Add("Real accepted TCP tuple resolves to exact caller PID");
        }
        router.Resolve=h=>Task.FromResult(h.StartsWith("v4.")?Array.Empty<IPAddress>():new[]{IPAddress.IPv6Loopback});
        int fallbackCalls=0;router.OriginalResolve=_=>{Interlocked.Increment(ref fallbackCalls);return Task.FromResult(new[]{IPAddress.Loopback});};
        string folder=Path.Combine(Path.GetTempPath(),"IPv6Lens-process-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try{
            foreach(var item in new[]{("steam.exe","steam"),("steamservice.exe","steam"),("steamwebhelper.exe","steam"),("EpicGamesLauncher.exe","epic"),("EpicWebHelper.exe","epic")}){
                string exe=Path.Combine(folder,item.Item1);File.Copy(Path.Combine(Environment.SystemDirectory,"curl.exe"),exe);
                async Task<int> Request(string host,string resource){var si=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string a in new[]{"--proxy","http://127.0.0.1:"+router.Port,"--noproxy","","--location","--fail","--silent","--max-time","5",$"http://{host}:{originPort}/{resource}"})si.ArgumentList.Add(a);using var p=Process.Start(si)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();string body=await output;await error;if(p.ExitCode==0&&body!="ok")throw new Exception("Unexpected process probe body");return p.ExitCode;}
                long before=router.Counters[item.Item2].V6,other=router.Counters["global"].V6;
                if(await Request("unlisted.example","redirect")!=0||router.Counters[item.Item2].V6<=before||router.Counters["global"].V6!=other)throw new Exception("Unknown/redirected CDN escaped process classification: "+item.Item1);
                if(await Request("v4.unlisted.example","file")==0||fallbackCalls!=0)throw new Exception("IPv4-only process destination escaped strict policy: "+item.Item1);
                checks.Add(item.Item1+": real named probe through shared proxy attributes unknown and redirected CDN, rejects IPv4 without fallback");
                if(item.Item2=="steam"&&item.Item1=="steam.exe"){
                    router.Enable("steam",false);if(await Request("v4.unlisted.example","file")!=0||router.Counters["steam"].V4==0)throw new Exception("Disabled process switch did not restore original route");router.Enable("steam",true);fallbackCalls=0;
                    checks.Add("Steam switch off permits original route while retaining Steam accounting");
                }
            }
        }finally{foreach(string file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);}
        foreach(string host in new[]{"xz.pphimalayanrt.com","st.dl.eccdnx.com","yif.gdtstream.com","trts.baishancdnx.cn"})if(DownloadRouter.ContentSite(host)!="steam"||DownloadRouter.ContentSite("evil"+host)=="steam")throw new Exception("CDN boundary mismatch");
        checks.Add("Observed Steam CDNs matched with hostname boundary checks");
        origin.Stop();await serving;
    }).GetAwaiter().GetResult();File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks},new JsonSerializerOptions{WriteIndented=true}));}
    catch(Exception ex){File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=1;}}
}
