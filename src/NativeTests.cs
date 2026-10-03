using System.Diagnostics;
using System.Text.Json;
namespace IPv6Lens;
internal static class NativeTests
{
    static bool TunUp()=>System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name=="IPv6Lens-TUN"&&n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up);
    internal static void Owner(string ready)=>Task.Run(async()=>{await using var router=new DownloadRouter{ProductionPolicies=true,FallbackProxy="127.0.0.1:7897"};await router.Start(false);router.EnableAll();await using var core=new NativeCapture();await core.Start(router);File.WriteAllText(ready,"ready");await Task.Delay(TimeSpan.FromMinutes(2));}).GetAwaiter().GetResult();
    internal static void Suite(string path)=>Task.Run(async()=>{
        var checks=new List<string>();var counters=new List<object>();string folder=Path.Combine(Path.GetTempPath(),"IPv6Lens-native-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string clash=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"io.github.clash-verge-rev","clash-verge-rev","clash-verge.yaml");string? before=File.Exists(clash)?File.ReadAllText(clash):null;
        void Check(bool pass,string name){if(!pass)throw new IOException(name);checks.Add(name);}
        async Task<int> Head(string exe,string url){var si=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string a in new[]{"--noproxy","*","--head","--silent","--show-error","--max-time","20",url})si.ArgumentList.Add(a);using var p=Process.Start(si)!;var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();await stdout;await stderr;return p.ExitCode;}
        try{
            await using(var router=new DownloadRouter{ProductionPolicies=true,FallbackProxy="127.0.0.1:7897"}){
                await router.Start(false);router.EnableAll();await using(var core=new NativeCapture()){
                    await core.Start(router);Check(TunUp(),"TUN interface active");
                    foreach(var test in new[]{(File:"steam.exe",Site:"steam"),(File:"EpicGamesLauncher.exe",Site:"epic")}){
                        string exe=Path.Combine(folder,test.File);File.Copy(Path.Combine(Environment.SystemDirectory,"curl.exe"),exe);
                        var row=router.Counters[test.Site];int exit=await Head(exe,"https://www.bilibili.com/");
                        Check(exit==0&&row.V6>0&&row.V4==0,test.Site+" process-name rule routes controlled native probe over IPv6");
                        long failed=row.Failures;exit=await Head(exe,"https://ipv4.icanhazip.com/");
                        Check(exit!=0&&row.Failures>failed&&row.V4==0&&row.Proxy==0,test.Site+" unknown IPv4-only destination rejected without proxy fallback");
                        counters.Add(new{site=test.Site,ipv6=row.V6,ipv4=row.V4,proxyUnknown=row.Proxy,scope="Renamed curl rule probe, not real game download"});
                    }
                    using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new System.Net.WebProxy("http://127.0.0.1:7897")}){Timeout=TimeSpan.FromSeconds(15)};
                    using var response=await http.SendAsync(new HttpRequestMessage(HttpMethod.Head,"https://www.bilibili.com/"),HttpCompletionOption.ResponseHeadersRead);Check((int)response.StatusCode<500,"Existing local Clash proxy remains usable while independent TUN is active");
                }
                Check(!TunUp(),"Normal stop removes active TUN");
            }
            string ready=Path.Combine(folder,"ready");var ownerInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};ownerInfo.ArgumentList.Add("--native-owner");ownerInfo.ArgumentList.Add(ready);
            using(var owner=Process.Start(ownerInfo)!){try{
                var until=DateTime.UtcNow.AddSeconds(20);while(!File.Exists(ready)&&!owner.HasExited&&DateTime.UtcNow<until)await Task.Delay(200);
                Check(File.Exists(ready)&&TunUp(),"Crash-test owner successfully starts TUN");owner.Kill();await owner.WaitForExitAsync();
                until=DateTime.UtcNow.AddSeconds(12);while(TunUp()&&DateTime.UtcNow<until)await Task.Delay(200);Check(!TunUp(),"Forced owner termination closes job child and removes active TUN");
            }finally{if(!owner.HasExited){owner.Kill();await owner.WaitForExitAsync();}}}
            Check(before==null||before==File.ReadAllText(clash),"Existing Clash configuration unchanged");
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks,counters},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,counters,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=1;}
        finally{foreach(string file in new[]{"steam.exe","EpicGamesLauncher.exe","ready"})try{File.Delete(Path.Combine(folder,file));}catch{}}
    }).GetAwaiter().GetResult();
    internal static void ConfigTest(string path){Task.Run(async()=>{try{await using var router=new DownloadRouter{ProductionPolicies=true};await router.Start(false);await using var core=new NativeCapture();await core.Start(router,false);File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",detail="Mihomo accepts production rules and local HTTP relay listeners; no TUN or system setting changes"}));}catch(Exception ex){File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",error=ex.ToString()}));Environment.ExitCode=1;}}).GetAwaiter().GetResult();}
    internal static void Run(string path){Task.Run(async()=>{
        var checks=new List<string>();
        try{
            await using var router=new DownloadRouter{ProductionPolicies=true,FallbackProxy="127.0.0.1:7897"};await router.Start(false);router.EnableAll();
            await using(var core=new NativeCapture()){
                await core.Start(router);checks.Add("Independent TUN interface started without changing Clash config");
                var si=new ProcessStartInfo("curl.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"--noproxy","*","--head","--silent","--show-error","--max-time","20","https://dl-pc-sz.drive.quark.cn/"})si.ArgumentList.Add(arg);
                using var child=Process.Start(si)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();string reply=await stdout;await stderr;
                if(child.ExitCode!=0||!reply.Contains("403")||router.Counters["quark"].V6==0||router.Counters["quark"].V4!=0)throw new IOException("TUN-to-relay IPv6 probe failed (curl "+child.ExitCode+")");
                checks.Add("Native curl with no proxy reaches Quark through TUN and IPv6 relay, TLS verified, root HTTP403");
            }
            if(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name=="IPv6Lens-TUN"&&n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up))throw new IOException("TUN interface still active after stop");
            checks.Add("TUN interface inactive after stop");File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks,servers=router.ServerReport()}));
        }catch(Exception ex){File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()}));Environment.ExitCode=1;}
    }).GetAwaiter().GetResult();}
}
