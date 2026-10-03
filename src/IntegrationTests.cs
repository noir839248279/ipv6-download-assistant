using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;
internal static class IntegrationTests
{
    const int TcpBytes=8*1024*1024,UdpCount=256,UdpBytes=1200;
    record Outcome(string Name,bool Passed,object Evidence);
    sealed class Fixture : IDisposable
    {
        public Process Process {get;}
        public Fixture(string path) {Process=System.Diagnostics.Process.Start(new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true})!;if(Process.StandardOutput.ReadLine()!="READY")throw new Exception("Fixture startup failed");}
        public async Task<(bool Ok,long Bytes,string Error)> Command(string protocol,int family,int port) {
            await Process.StandardInput.WriteLineAsync($"{protocol} {family} {port} {UdpCount}");await Process.StandardInput.FlushAsync();
            var line=await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            using var doc=JsonDocument.Parse(line??"{}");var root=doc.RootElement;
            return(root.GetProperty("ok").GetBoolean(),root.GetProperty("bytes").GetInt64(),root.GetProperty("error").GetString()??"");
        }
        public void Dispose(){try{Process.StandardInput.WriteLine("exit");if(!Process.WaitForExit(1000))Process.Kill();}catch{}Process.Dispose();}
    }
    sealed class Servers : IDisposable
    {
        readonly CancellationTokenSource stop=new();
        readonly List<IDisposable> resources=new();
        public Dictionary<(string,int),int> Ports {get;}=new();
        public Servers(){foreach(int family in new[]{4,6}) {
            var ip=family==6?IPAddress.IPv6Loopback:IPAddress.Loopback;
            var listener=new TcpListener(ip,0);listener.Start();resources.Add(listener);Ports[("tcp",family)]=((IPEndPoint)listener.LocalEndpoint).Port;
            _=Task.Run(async()=>{try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);_ = SendTcp(client);}}catch(OperationCanceledException){}catch(ObjectDisposedException){}});
            var udp=new UdpClient(new IPEndPoint(ip,0));resources.Add(udp);Ports[("udp",family)]=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            _=Task.Run(async()=>{try{while(!stop.IsCancellationRequested){var request=await udp.ReceiveAsync(stop.Token);for(int i=0;i<UdpCount;i++){await udp.SendAsync(new byte[UdpBytes],request.RemoteEndPoint,stop.Token);await Task.Delay(2,stop.Token);}}}catch(OperationCanceledException){}catch(ObjectDisposedException){}});
        }}
        async Task SendTcp(TcpClient client){using(client){try{var stream=client.GetStream();await stream.ReadExactlyAsync(new byte[1],stop.Token);byte[] payload=new byte[65536];for(int i=0;i<TcpBytes/payload.Length;i++)await stream.WriteAsync(payload,stop.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException){}}}
        public void Dispose(){stop.Cancel();foreach(var resource in resources)resource.Dispose();stop.Dispose();}
    }
    public static void Run(string reportPath,string fixturePath)
    {
        var results=new List<Outcome>();
        void Save(string state) => File.WriteAllText(reportPath,JsonSerializer.Serialize(new{state,admin=Program.Admin,time=DateTime.UtcNow,results},new JsonSerializerOptions{WriteIndented=true}));
        void Check(string name,bool ok,object evidence){results.Add(new(name,ok,evidence));Save("running");}
        try {
            Save("starting");if(!Program.Admin)throw new InvalidOperationException("Administrator authorization required");
            Task.Run(async()=> {
                using var servers=new Servers();using var target=new Fixture(fixturePath);
                var otherPath=Path.Combine(Path.GetDirectoryName(fixturePath)!,"ControlFixture.exe");
                File.Copy(fixturePath,otherPath,true);using var control=new Fixture(otherPath);
                var store=new TrafficStore();store.Select(new[]{target.Process.Id});using var collector=new EtwCollector(store);collector.Start();
                Check("ETW collector starts",collector.Status.StartsWith("正在采集"),collector.Status);
                await Task.Delay(1500);
                foreach(var protocol in new[]{"tcp","udp"})foreach(int family in new[]{4,6}) {
                    store.Select(new[]{target.Process.Id});var actual=await target.Command(protocol,family,servers.Ports[(protocol,family)]);
                    await Task.Delay(2200);var flows=store.Sample();long captured=flows.Where(f=>f.Key.Transport.Equals(protocol,StringComparison.OrdinalIgnoreCase)&&f.Family==$"IPv{family}").Sum(f=>f.Received);
                    long expected=protocol=="tcp"?TcpBytes:UdpCount*UdpBytes;
                    Check($"ETW {protocol.ToUpperInvariant()} IPv{family} known-byte receive",actual.Ok&&actual.Bytes==expected&&captured==expected,new{application=actual.Bytes,etw=captured,expected,actual.Error,lost=collector.Lost,observed=flows.Select(f=>new{f.Key.Pid,f.Key.Transport,f.Family,f.Received,f.Sent,f.Key.RemotePort})});
                    Check($"ETW {protocol.ToUpperInvariant()} IPv{family} remote endpoint",flows.Where(f=>f.Received>0).All(f=>f.Key.RemotePort==servers.Ports[(protocol,family)])&&flows.Any(f=>f.Received>0),new{serverPort=servers.Ports[(protocol,family)],observedPorts=flows.Where(f=>f.Received>0).Select(f=>f.Key.RemotePort)});
                }
                using(var lease=new TestLease()) {
                    await lease.Start(fixturePath,40);
                    var blocked=await target.Command("tcp",4,servers.Ports[("tcp",4)]);var allowed=await target.Command("tcp",6,servers.Ports[("tcp",6)]);
                    var unaffected=await control.Command("tcp",4,servers.Ports[("tcp",4)]);
                    var udp4=await target.Command("udp",4,servers.Ports[("udp",4)]);var udp6=await target.Command("udp",6,servers.Ports[("udp",6)]);
                    Check("WFP blocks target IPv4 TCP and UDP",!blocked.Ok&&!udp4.Ok,new{tcp4=blocked.Error,udp4=udp4.Error});
                    Check("WFP preserves target IPv6 and other application IPv4",allowed.Ok&&udp6.Ok&&unaffected.Ok,new{tcp6=allowed.Ok,udp6=udp6.Ok,otherIPv4=unaffected.Ok});
                    lease.Dispose();var restored=await target.Command("tcp",4,servers.Ports[("tcp",4)]);Check("WFP explicit restore",restored.Ok,new{restored.Ok,restored.Error});
                    await lease.Start(fixturePath,3);await Task.Delay(3800);restored=await target.Command("tcp",4,servers.Ports[("tcp",4)]);Check("WFP independent deadline restore",restored.Ok,new{restored.Ok,restored.Error});lease.Dispose();
                    await lease.Start(fixturePath,30);using(var guard=Process.GetProcessById(lease.HelperPid)){guard.Kill();await guard.WaitForExitAsync();}await Task.Delay(300);restored=await target.Command("tcp",4,servers.Ports[("tcp",4)]);Check("WFP guard crash restore",restored.Ok,new{restored.Ok,restored.Error});lease.Dispose();
                }
                foreach(bool crash in new[]{false,true}) {
                    string ready=Path.Combine(Path.GetDirectoryName(reportPath)!,"lease-ready-"+Guid.NewGuid().ToString("N")+".json");
                    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
                    foreach(var a in new[]{"--lease-owner",fixturePath,ready})start.ArgumentList.Add(a);
                    using var owner=Process.Start(start)!;
                    try {
                        var limit=DateTime.UtcNow.AddSeconds(12);while(!File.Exists(ready)&&DateTime.UtcNow<limit&&!owner.HasExited)await Task.Delay(100);
                        if(!File.Exists(ready))throw new Exception("Lease owner not ready");
                        var blocked=await target.Command("tcp",4,servers.Ports[("tcp",4)]);if(crash)owner.Kill();else {await owner.StandardInput.WriteLineAsync("exit");await owner.StandardInput.FlushAsync();}
                        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(700);
                        var restored=await target.Command("tcp",4,servers.Ports[("tcp",4)]);
                        Check(crash?"WFP parent crash restore":"WFP parent normal close restore",!blocked.Ok&&restored.Ok,new{wasBlocked=!blocked.Ok,restored.Ok,restored.Error});
                    }finally{if(!owner.HasExited){owner.Kill();await owner.WaitForExitAsync();}}
                }
                Check("No ETW lost events",collector.Lost==0,new{lost=collector.Lost});
                collector.Dispose(); // Release the kernel slot before the independent crash test.
                var traceReady=Path.Combine(Path.GetDirectoryName(reportPath)!,"trace-ready-"+Guid.NewGuid().ToString("N")+".txt");
                var traceStart=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};traceStart.ArgumentList.Add("--trace-owner");traceStart.ArgumentList.Add(traceReady);
                using(var owner=Process.Start(traceStart)!){try{
                    var end=DateTime.UtcNow.AddSeconds(12);while(!File.Exists(traceReady)&&DateTime.UtcNow<end&&!owner.HasExited)await Task.Delay(100);
                    string traceName="IPv6Lens-"+owner.Id;bool started=Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSessionNames().Contains(traceName);
                    owner.Kill();await owner.WaitForExitAsync();end=DateTime.UtcNow.AddSeconds(5);
                    while(Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSessionNames().Contains(traceName)&&DateTime.UtcNow<end)await Task.Delay(200);
                    Check("ETW parent crash stops kernel session",started&&!Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSessionNames().Contains(traceName),new{started});
                }finally{if(!owner.HasExited)owner.Kill();}}
            }).GetAwaiter().GetResult();
            Save(results.All(x=>x.Passed)?"passed":"failed");Environment.ExitCode=results.All(x=>x.Passed)?0:1;
        }catch(Exception ex){results.Add(new("Integration runner",false,ex.ToString()));Save("failed");Environment.ExitCode=1;}
    }
    public static void LeaseOwner(string path,string ready)
    {
        using var lease=new TestLease();lease.Start(path,25).GetAwaiter().GetResult();File.WriteAllText(ready,"{\"active\":true}");Console.ReadLine();
    }
    public static void BrowserBackend(string pairing,string report)
    {
        Task.Run(async()=> {
            await using var feed=new BrowserFeed();await feed.Start();
            File.WriteAllText(pairing,JsonSerializer.Serialize(new{endpoint=feed.Endpoint,token=feed.Token}));
            var deadline=DateTime.UtcNow.AddMinutes(5);
            while(DateTime.UtcNow<deadline && !File.Exists(pairing+".stop")) {
                File.WriteAllText(report,JsonSerializer.Serialize(new{feed.ActiveSession,feed.Incomplete,rows=feed.Sample()}));
                await Task.Delay(500);
            }
        }).GetAwaiter().GetResult();
    }
    public static void Observe(string name,string report,int seconds)
    {
        Task.Run(async()=>{
            var all=Processes.List();var target=all.FirstOrDefault(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase))??throw new Exception("Target is not running");
            var store=new TrafficStore();var selected=Processes.Group(target,all);store.Select(selected);using var collector=new EtwCollector(store);collector.Start();
            var end=DateTime.UtcNow.AddSeconds(Math.Clamp(seconds,5,300));double peak4=0,peak6=0;List<FlowView> flows=new();
            while(DateTime.UtcNow<end){await Task.Delay(1000);all=Processes.List();var current=Processes.Group(target,all);foreach(var id in selected.Except(current))store.RemovePid(id);foreach(var id in current)store.AddPid(id);selected=current;flows=store.Sample();var total=TrafficStore.Sum(flows);peak4=Math.Max(peak4,total.Rate4);peak6=Math.Max(peak6,total.Rate6);File.WriteAllText(report,JsonSerializer.Serialize(new{state="running",collector=collector.Status,totals=total,peak4,peak6,flows}));}
            Dictionary<string,string[]>? dns=null;try{dns=await Diagnostics.DnsCandidates(flows.Select(f=>f.Key.Remote));}catch{}
            File.WriteAllText(report,JsonSerializer.Serialize(new{state="complete",collector=collector.Status,totals=TrafficStore.Sum(flows),peak4,peak6,lost=collector.Lost,flows,dns},new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(report+".redacted.json",Report.Build(flows,new(),null,collector.Status,collector.Lost,store.Dropped));
        }).GetAwaiter().GetResult();
    }
    public static void StopTestProcess(int pid)
    {
        if(!Program.Admin||pid==Environment.ProcessId)return;
        using var process=Process.GetProcessById(pid);var expected=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","ui-app","IPv6Lens.exe"));
        if(!string.Equals(process.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase))return;
        if(!process.CloseMainWindow()||!process.WaitForExit(3000)){process.Kill();process.WaitForExit();}
        try{using var session=new Microsoft.Diagnostics.Tracing.Session.TraceEventSession("IPv6Lens-"+pid,Microsoft.Diagnostics.Tracing.Session.TraceEventSessionOptions.Attach);session.Stop();}catch{}
    }
}
