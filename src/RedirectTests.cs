using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace IPv6Lens;
internal static class RedirectTests
{
    public static void PublicProbe(string path){Task.Run(async()=>{
        await using var router=new DownloadRouter();await router.Start(false);router.EnableAll();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy($"http://127.0.0.1:{router.Port}")}){Timeout=TimeSpan.FromSeconds(25)};
        var outcomes=new List<object>();
        foreach(var host in new[]{"www.bilibili.com","www.cloudflare.com"}){
            long before=router.Counters["global"].V6;try{using var response=await http.SendAsync(new HttpRequestMessage(HttpMethod.Head,"https://"+host),HttpCompletionOption.ResponseHeadersRead);outcomes.Add(new{host,status=(int)response.StatusCode,ipv6Received=router.Counters["global"].V6-before});}catch(Exception ex){outcomes.Add(new{host,error=ex.GetType().Name});}
        }
        File.WriteAllText(path,JsonSerializer.Serialize(new{outcomes,ipv4=router.Counters["global"].V4,ipv6=router.Counters["global"].V6,systemSettingsChanged=false},new JsonSerializerOptions{WriteIndented=true}));
    }).GetAwaiter().GetResult();}
    public static void Run(string path){var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}try{Task.Run(async()=>{
        using var tcp=new TcpListener(IPAddress.IPv6Any,0);tcp.Server.DualMode=true;tcp.Start();int port=((IPEndPoint)tcp.LocalEndpoint).Port;
        using var key=RSA.Create(2048);var req=new CertificateRequest("CN=test.quark.cn",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var generated=req.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1),DateTimeOffset.Now.AddHours(1));using var cert=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        using var tls=new TcpListener(IPAddress.IPv6Any,0);tls.Server.DualMode=true;tls.Start();int tlsPort=((IPEndPoint)tls.LocalEndpoint).Port;
        byte[] payload=Enumerable.Repeat((byte)71,2*1024*1024).ToArray();bool hostPreserved=false;
        async Task Serve(TcpClient c,bool secure){using(c){Stream s=c.GetStream();if(secure){var ssl=new SslStream(s);await ssl.AuthenticateAsServerAsync(cert);s=ssl;}using(s){byte[] b=new byte[1];var header=new StringBuilder();while(!header.ToString().EndsWith("\r\n\r\n")){if(await s.ReadAsync(b)==0)return;header.Append((char)b[0]);}if(header.ToString().Contains("Host: test.quark.cn:"))hostPreserved=true;await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));await s.WriteAsync(payload);}}}
        var serving=Task.Run(async()=>{try{while(true){var c=await tcp.AcceptTcpClientAsync();_=Serve(c,false);}}catch{}});var servingTls=Task.Run(async()=>{try{while(true){var c=await tls.AcceptTcpClientAsync();_=Serve(c,true).ContinueWith(t=>{if(t.Exception!=null)File.WriteAllText(path+".servererror",t.Exception.ToString());});}}catch{}});
        await using var router=new DownloadRouter();router.Resolve=h=>Task.FromResult(h.StartsWith("v4")?new[]{IPAddress.Loopback}:new[]{IPAddress.IPv6Loopback,IPAddress.Loopback});await router.Start(false);
        var handler=new HttpClientHandler{Proxy=new WebProxy($"http://127.0.0.1:{router.Port}"),UseProxy=true,ServerCertificateCustomValidationCallback=(_,c,_,_)=>c?.Thumbprint==cert.Thumbprint};using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)};
        router.Enable("quark",true);var bytes=await http.GetByteArrayAsync($"http://test.quark.cn:{port}/test?private=not-retained");
        Check(bytes.SequenceEqual(payload)&&router.Counters["quark"].V6>=payload.Length&&router.Counters["quark"].V4==0,"Enabled service relays exact HTTP payload over IPv6");
        Check(hostPreserved,"Origin Host preserved");
        bytes=await http.GetByteArrayAsync($"https://test.quark.cn:{tlsPort}/test");Check(bytes.SequenceEqual(payload),"HTTPS CONNECT preserves end-to-end TLS certificate and payload");
        async Task Reject(string url,string name){using var response=await http.GetAsync(url);Check(response.StatusCode==HttpStatusCode.BadGateway,name);}
        router.Enable("bilibili",true);await Reject($"http://v4.bilivideo.com:{port}/test","IPv4-only CDN is rejected instead of downloading");Check(router.Counters["bilibili"].V4==0,"Rejected IPv4-only CDN consumes no relay IPv4 bytes");
        router.Enable("quark",false);bytes=await http.GetByteArrayAsync($"http://test.quark.cn:{port}/test");Check(router.Counters["quark"].V4>=payload.Length&&router.Counters["bilibili"].Enabled,"One switch off restores original family without disabling another");
        router.Enable("global",true);bytes=await http.GetByteArrayAsync($"http://unrelated.example:{port}/");Check(bytes.SequenceEqual(payload)&&router.Counters["global"].V6>=payload.Length&&router.Counters["global"].V4==0,"Global switch sends an unlisted dual-stack website over IPv6");
        long before4=router.Counters["quark"].V4;bytes=await http.GetByteArrayAsync($"http://test.quark.cn:{port}/");Check(router.Counters["quark"].V4>=before4+payload.Length,"Explicit service off overrides global IPv6 preference");
        await Reject($"http://v4.unrelated.example:{port}/","Global strict mode rejects IPv4-only website");Check(router.Counters["global"].V4==0,"Global strict mode has zero IPv4 payload");
        router.Enable("global",false);before4=router.Counters["global"].V4;bytes=await http.GetByteArrayAsync($"http://unrelated.example:{port}/");Check(router.Counters["global"].V4>=before4+payload.Length,"Disabling global restores original address-family selection");
        long before6=router.Counters["bilibili"].V6;bytes=await http.GetByteArrayAsync($"http://test.bilivideo.com:{port}/");Check(router.Counters["bilibili"].V6>=before6+payload.Length,"Disabling global preserves enabled service IPv6 preference");
        router.EnableAll();Check(router.Counters.Count==6&&router.Counters.Values.All(c=>c.Enabled),"Default startup enables all six actual routing switches");
        using var proxyServer=new TcpListener(IPAddress.Loopback,0);proxyServer.Start();int proxyPort=((IPEndPoint)proxyServer.LocalEndpoint).Port;
        var proxyServing=Task.Run(async()=>{try{using var c=await proxyServer.AcceptTcpClientAsync();var s=c.GetStream();var h=new StringBuilder();byte[] b=new byte[1];while(!h.ToString().EndsWith("\r\n\r\n")){if(await s.ReadAsync(b)==0)return;h.Append((char)b[0]);}await s.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());await Serve(c,false);}catch{}});
        router.FallbackProxy="127.0.0.1:"+proxyPort;router.Resolve=_=>throw new SocketException((int)SocketError.HostNotFound);long oldV4=router.Counters["global"].V4,oldV6=router.Counters["global"].V6;
        await Reject($"http://dns-failed.example:{port}/","DNS failure cannot escape through original proxy");Check(router.Counters["global"].Proxy==0&&router.Counters["global"].V4==oldV4&&router.Counters["global"].V6==oldV6,"Strict failure consumes no proxy or IPv4 payload");
        router.Resolve=_=>Task.FromResult(new[]{IPAddress.IPv6Loopback,IPAddress.Loopback});using var unused=new TcpListener(IPAddress.IPv6Loopback,0);unused.Start();int refusedPort=((IPEndPoint)unused.LocalEndpoint).Port;unused.Stop();
        await Reject($"http://refused.example:{refusedPort}/","IPv6 connection refusal cannot fall back to original proxy");
        Check(!proxyServing.IsCompleted&&!proxyServer.Pending(),"Original proxy is never contacted while strict mode is enabled");
        Check(DownloadRouter.Classify("evilquark.cn")==null&&DownloadRouter.Classify("cdn.quark.cn")=="quark","CDN suffix boundary is exact");
        router.FallbackProxy="127.0.0.1:7897";router.Bypass="localhost;192.168.*";File.WriteAllText(path+".pac",router.Pac());Check(!router.Pac().Contains("PROXY 127.0.0.1:7897")&&router.Pac().Contains("192.168.*"),"PAC has no fallback to original proxy");
        router.Sample();Check(router.Counters["quark"].Rate6>0,"Actual relay counters produce IPv6 speed");router.Sample();Check(router.Counters["quark"].Rate6==0,"Idle relay speed resets");
        // Keep a CONNECT tunnel open, then simulate loss of system proxy ownership.
        using var tunnelClient=new TcpClient();await tunnelClient.ConnectAsync(IPAddress.Loopback,router.Port);var tunnelStream=tunnelClient.GetStream();await tunnelStream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT live.quark.cn:{port} HTTP/1.1\r\nHost: live.quark.cn:{port}\r\n\r\n"));
        var responseHeader=new StringBuilder();byte[] one=new byte[1];using var deadline=new CancellationTokenSource(5000);while(!responseHeader.ToString().EndsWith("\r\n\r\n")){if(await tunnelStream.ReadAsync(one,deadline.Token)==0)throw new Exception("Tunnel unexpectedly closed");responseHeader.Append((char)one[0]);}
        Check(responseHeader.ToString().Contains(" 200 "),"Existing tunnel established before ownership loss");
        router.OwnershipProbe=()=>false;router.Sample();Check(router.ProtectionLost,"Proxy takeover latches protection lost");
        bool disconnected=false;try{disconnected=await tunnelStream.ReadAsync(one,deadline.Token)==0;}catch(IOException){disconnected=true;}Check(disconnected,"Proxy takeover disconnects existing relay session");
        bool newRejected=false;try{using var response=await http.GetAsync($"http://after-loss.example:{port}/");newRejected=!response.IsSuccessStatusCode;}catch(HttpRequestException){newRejected=true;}Check(newRejected,"New requests rejected after ownership loss");
        router.OwnershipProbe=()=>true;router.Sample();Check(router.ProtectionLost,"Restored registry does not silently clear fault latch");
        Check(router.Counters.Values.All(c=>c.State.Contains("失效")),"Every card reports protection loss");
        await using var scoped=new DownloadRouter{ContentTestHost="test.quark.cn",FallbackProxy="127.0.0.1:7897",Bypass="<local>;192.168.*"};
        scoped.Resolve=_=>Task.FromResult(new[]{IPAddress.IPv6Loopback});await scoped.Start(false);scoped.Enable("quark",true);
        using var scopedHttp=new HttpClient(new HttpClientHandler{Proxy=new WebProxy($"http://127.0.0.1:{scoped.Port}"),UseProxy=true}){Timeout=TimeSpan.FromSeconds(5)};
        bytes=await scopedHttp.GetByteArrayAsync($"http://test.quark.cn:{port}/signed?auth_key=test");
        Check(bytes.SequenceEqual(payload)&&scoped.Counters["quark"].V6>=payload.Length&&scoped.Counters["quark"].V4==0&&scoped.Counters["quark"].Proxy==0,"Scoped CDN test transfers exact payload through IPv6 only");
        using(var unknown=await scopedHttp.GetAsync($"http://dl-other.quark.cn:{port}/"))Check(unknown.StatusCode==HttpStatusCode.BadGateway,"Unknown CDN is rejected before connection");
        File.WriteAllText(path+".scoped.pac",scoped.Pac());Check(DownloadRouter.QuarkDownloadHost("dl-other.quark.cn")&&!DownloadRouter.QuarkDownloadHost("dl-evilquark.cn"),"Download hostname boundary matches exact Quark suffix");
        scoped.StopContentTest();using(var timed=await scopedHttp.GetAsync($"http://test.quark.cn:{port}/"))Check(timed.StatusCode==HttpStatusCode.BadGateway,"Expired content test rejects new requests without reverting route");

        await using var production=new DownloadRouter{ProductionPolicies=true};await production.Start(false);
        production.Resolve=h=>Task.FromResult(h.StartsWith("v4")||h=="pan.quark.cn"?Array.Empty<IPAddress>():new[]{IPAddress.IPv6Loopback});
        production.OriginalResolve=_=>Task.FromResult(new[]{IPAddress.Loopback});production.EnableAll();
        using var productionHttp=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy($"http://127.0.0.1:{production.Port}")}){Timeout=TimeSpan.FromSeconds(10)};
        bytes=await productionHttp.GetByteArrayAsync($"http://v4.ordinary.example:{port}/");
        Check(bytes.SequenceEqual(payload)&&production.Counters["global"].V4>=payload.Length,"Production ordinary websites fall back to IPv4");
        bytes=await productionHttp.GetByteArrayAsync($"http://pan.quark.cn:{port}/");
        Check(bytes.SequenceEqual(payload)&&production.Counters["quark"].V4==0,"Quark known control API may use IPv4 without contaminating content counter");
        foreach(var host in new[]{"dl-test.quark.cn","test.bilivideo.com","test.googlevideo.com","test.steamcontent.com","download.epicgames.com"}){
            bytes=await productionHttp.GetByteArrayAsync($"http://{host}:{port}/");Check(bytes.SequenceEqual(payload),"Content IPv6 payload verified: "+host);
        }
        using(var rejected=await productionHttp.GetAsync($"http://v4.bilivideo.com:{port}/"))Check(rejected.StatusCode==HttpStatusCode.BadGateway&&production.Counters["bilibili"].V4==0,"Production content cannot use ordinary-site IPv4 fallback");
        using var nativeHttp=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy($"http://127.0.0.1:{production.SteamPort}")}){Timeout=TimeSpan.FromSeconds(10)};
        using(var rejected=await nativeHttp.GetAsync($"http://v4.unknown.example:{port}/"))Check(rejected.StatusCode==HttpStatusCode.BadGateway&&production.Counters["steam"].V4==0,"Native Steam listener rejects unknown IPv4 CDN");
        Check(production.MainServer("bilibili")=="test.bilivideo.com","Main server follows actual content counters");
        string report=JsonSerializer.Serialize(production.ServerReport());Check(!report.Contains("ordinary.example")&&!report.Contains("pan.quark.cn")&&!report.Contains("private="),"Server report excludes ordinary browsing and URL queries");
        production.Reset();Check(production.Counters.Values.All(c=>c.V4+c.V6+c.Proxy==0),"Reset clears all service totals");
        production.MarkCaptureLost();using(var rejected=await productionHttp.GetAsync($"http://dl-test.quark.cn:{port}/"))Check(rejected.StatusCode==HttpStatusCode.BadGateway,"Native capture loss rejects subsequent relay requests");
        bytes=await productionHttp.GetByteArrayAsync($"http://v4.recovery.example:{port}/");Check(bytes.SequenceEqual(payload)&&production.Counters["global"].V4>=payload.Length,"Ordinary websites remain usable through original path after capture loss");
        production.FallbackProxy="127.0.0.1:"+proxyPort;bytes=await productionHttp.GetByteArrayAsync($"http://recovery-proxy.example:{port}/");Check(bytes.SequenceEqual(payload)&&production.Counters["global"].Proxy>=payload.Length,"Capture loss preserves configured original proxy and unknown-exit accounting");
        long recoveryV4=production.Counters["global"].V4;production.FallbackProxy="127.0.0.1:"+refusedPort;
        bool proxyRejected=false;try{using var failed=await productionHttp.GetAsync($"http://unavailable-proxy.example:{port}/");proxyRejected=failed.StatusCode==HttpStatusCode.BadGateway;}catch(HttpRequestException){proxyRejected=true;}Check(proxyRejected&&production.Counters["global"].V4==recoveryV4,"Unavailable original proxy is not silently bypassed");
        Check(production.ProtectionLossReason=="network_core_exited"&&production.ProtectionLostAtUtc!=null,"First capture-loss cause and timestamp retained");
        Check(new DownloadCounter{V6=500,Proxy=700}.ShareText=="已确认 IPv6 41.7%","Unknown proxy bytes included in confirmed IPv6 share denominator");
        await using var tlsFallback=new DownloadRouter{ProductionPolicies=true};await tlsFallback.Start(false);tlsFallback.Resolve=_=>Task.FromResult(new[]{IPAddress.IPv6Loopback});tlsFallback.OriginalResolve=_=>Task.FromResult(new[]{IPAddress.Loopback});tlsFallback.Enable("global",true);
        using var fallbackHttp=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy($"http://127.0.0.1:{tlsFallback.Port}"),ServerCertificateCustomValidationCallback=(_,c,_,_)=>c?.Thumbprint==cert.Thumbprint}){Timeout=TimeSpan.FromSeconds(10)};
        bytes=await fallbackHttp.GetByteArrayAsync($"https://ordinary.example:{tlsPort}/");Check(bytes.SequenceEqual(payload)&&tlsFallback.Counters["global"].V4>=payload.Length,"Ordinary HTTPS IPv6 certificate preflight failure allows original-path fallback");

    }).GetAwaiter().GetResult();File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks},new JsonSerializerOptions{WriteIndented=true}));}catch(Exception ex){File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=1;}}
    public static void Owner(string ready){Task.Run(async()=>{await using var r=new DownloadRouter();await r.Start();r.EnableAll();File.WriteAllText(ready,r.PacUrl);Console.ReadLine();}).GetAwaiter().GetResult();}
    public static void LeaseTest(string report){var checks=new List<string>();try{Task.Run(async()=>{
        object Snapshot(){using var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")!;return new{url=k.GetValue("AutoConfigURL"),proxy=k.GetValue("ProxyServer"),enabled=k.GetValue("ProxyEnable"),bypass=k.GetValue("ProxyOverride")};}
        string before=JsonSerializer.Serialize(Snapshot());
        foreach(bool crash in new[]{false,true}){
            string ready=report+"."+Guid.NewGuid().ToString("N")+".ready";var si=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};si.ArgumentList.Add("--redirect-owner");si.ArgumentList.Add(ready);using var child=Process.Start(si)!;
            try{var end=DateTime.UtcNow.AddSeconds(15);while(!File.Exists(ready)&&DateTime.UtcNow<end&&!child.HasExited)await Task.Delay(100);if(!File.Exists(ready))throw new Exception("PAC owner startup failed");using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")){if(!Equals(k?.GetValue("AutoConfigURL"),File.ReadAllText(ready)))throw new Exception("PAC not applied");}
                string owned=JsonSerializer.Serialize(Snapshot());RedirectSettings.Recover(child.Id,-1);if(owned!=JsonSerializer.Serialize(Snapshot())||!File.Exists(RedirectSettings.Journal))throw new Exception("Stale watchdog restored current lease");checks.Add("Stale watchdog cannot restore another session's lease");
                if(crash)child.Kill();else{await child.StandardInput.WriteLineAsync("exit");await child.StandardInput.FlushAsync();}await child.WaitForExitAsync();end=DateTime.UtcNow.AddSeconds(8);while((File.Exists(RedirectSettings.Journal)||before!=JsonSerializer.Serialize(Snapshot()))&&DateTime.UtcNow<end)await Task.Delay(100);
                if(before!=JsonSerializer.Serialize(Snapshot())||File.Exists(RedirectSettings.Journal))throw new Exception("Original proxy not restored");checks.Add(crash?"Crash restores exact original proxy settings":"Normal shutdown restores exact original proxy settings");
            }finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}if(File.Exists(RedirectSettings.Journal)){var lease=JsonSerializer.Deserialize<RedirectSettings.Lease>(File.ReadAllText(RedirectSettings.Journal));if(lease?.Pid==child.Id)RedirectSettings.Recover();}}
        }
    }).GetAwaiter().GetResult();File.WriteAllText(report,JsonSerializer.Serialize(new{state="passed",checks}));}catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()}));Environment.ExitCode=1;}}
    public static void Live(string ready,string report,int seconds){Task.Run(async()=>{await using var r=new DownloadRouter();await r.Start();r.Enable("quark",true);r.Enable("bilibili",true);r.Enable("youtube",true);File.WriteAllText(ready,JsonSerializer.Serialize(new{r.PacUrl,r.Port}));var end=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<end&&!File.Exists(ready+".stop")){r.Sample();File.WriteAllText(report,JsonSerializer.Serialize(r.Counters.ToDictionary(p=>p.Key,p=>new{p.Value.V4,p.Value.V6,p.Value.Proxy,p.Value.State,p.Value.Server})));await Task.Delay(500);}}).GetAwaiter().GetResult();}
}


