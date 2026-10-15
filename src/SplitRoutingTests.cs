using System.Diagnostics;
using System.Net;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace IPv6Lens;
internal static class SplitRoutingTests
{
    static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    internal static void Run(string output)
    {
        var checks=new List<string>();
        try{
            const string original="""
                mode: rule
                interface-name: old-interface
                mixed-port: 0
                tun: {enable: false}
                proxies:
                  - {name: remote, type: socks5, server: 192.0.2.5, port: 1080, interface-name: old-interface}
                  - {name: custom-direct, type: direct}
                proxy-groups:
                  - {name: choice, type: select, proxies: [remote, DIRECT]}
                proxy-providers:
                  inline-test:
                    type: inline
                    payload:
                      - {name: provider-node, type: socks5, server: 192.0.2.6, port: 1080}
                    override: {udp: true}
                rules: [MATCH,choice]
                """;
            string built=SplitConfig.Build(original,"WLAN","以太网","test-marker");var root=SplitConfig.Parse(built);
            Require(SplitConfig.Text(root,"interface-name")=="以太网","Builtin DIRECT must default to Ethernet");checks.Add("builtin_direct_ethernet");
            var proxies=((YamlSequenceNode)SplitConfig.Get(root,"proxies")!).Children.OfType<YamlMappingNode>().ToArray();
            Require(SplitConfig.Text(proxies[0],"interface-name")=="WLAN","Remote node must override prior interface");checks.Add("remote_node_wifi_overrides_previous");
            Require(SplitConfig.Text(proxies[1],"interface-name")=="以太网","Custom DIRECT wired");checks.Add("custom_direct_ethernet");
            var providers=(YamlMappingNode)SplitConfig.Get(root,"proxy-providers")!;var provider=(YamlMappingNode)providers.Children.First().Value;var over=(YamlMappingNode)SplitConfig.Get(provider,"override")!;
            Require(SplitConfig.Text(over,"interface-name")=="WLAN"&&SplitConfig.Text(over,"udp")=="true","Provider overrides preserved");checks.Add("provider_wifi_preserves_other_fields");
            Require(SplitConfig.Text(SplitConfig.Parse(original),"interface-name")=="old-interface","Source unchanged");checks.Add("source_configuration_unchanged");
            var live=SplitConfig.Parse(SplitConfig.WithLiveSettings(original,"{\"mode\":\"rule\",\"interface-name\":\"\",\"tun\":{\"enable\":false}}"));
            Require(SplitConfig.Text(live,"interface-name")=="","Live original interface preserved");checks.Add("restore_baseline_uses_live_settings");
            try{SplitConfig.Build(original.Replace("192.0.2.5","127.0.0.1"),"WLAN","以太网","test-marker");throw new Exception("Loopback node accepted");}catch(InvalidOperationException){checks.Add("local_secondary_proxy_rejected");}
            try{SplitConfig.Build(original,"WLAN","WLAN","test-marker");throw new Exception("Same adapter accepted");}catch(InvalidOperationException){checks.Add("same_adapter_rejected");}
            var unpreferred=new ClashNetworkSnapshot("WLAN","以太网",true,true,35,51,281,281);
            Require(!unpreferred.CanEnable,"Regression baseline must reproduce former failure");
            Require((unpreferred with {EthernetIpv4Metric=261,EthernetIpv6Metric=261,WifiIpv4Metric=356,WifiIpv6Metric=372}).CanEnable,"Automatic plan must prefer Ethernet");checks.Add("wifi_first_regression_resolved_by_total_metrics");
            Core(checks);
            File.WriteAllText(output,JsonSerializer.Serialize(new{passed=true,checks},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(output,JsonSerializer.Serialize(new{passed=false,checks,error=ex.Message}));Environment.ExitCode=1;}
    }
    static void Core(List<string> checks)
    {
        string folder=Path.Combine(Path.GetTempPath(),"IPv6Lens-split-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string secret=Guid.NewGuid().ToString("N");
        string original=JsonSerializer.Serialize(new Dictionary<string,object>{["mode"]="rule",["external-controller"]="127.0.0.1:"+port,["secret"]=secret,["log-level"]="silent",["tun"]=new{enable=false},["dns"]=new{enable=false},["proxies"]=new[]{new{name="remote",type="socks5",server="192.0.2.5",port=1080}},["proxy-providers"]=new Dictionary<string,object>{["local-provider"]=new{type="inline",payload=new[]{new{name="provider-node",type="socks5",server="192.0.2.6",port=1080}}}},["rules"]=new[]{"MATCH,DIRECT"}});
        string config=Path.Combine(folder,"config.json");File.WriteAllText(config,original);
        var start=new ProcessStartInfo(NativeCapture.CorePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{"-d",folder,"-f",config})start.ArgumentList.Add(a);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var api=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(10)};api.DefaultRequestHeaders.Authorization=new("Bearer",secret);
        try{
            bool ready=false;for(int i=0;i<30&&!process.HasExited;i++){try{SplitRouting.Get(api,"configs").GetAwaiter().GetResult();ready=true;break;}catch{Thread.Sleep(100);}}Require(ready,"Isolated core ready");
            string changed=SplitConfig.Build(original,"WLAN","以太网","IPv6Lens-Test");
            SplitRouting.Reload(api,changed).GetAwaiter().GetResult();
            using(var read=JsonDocument.Parse(SplitRouting.Get(api,"configs").GetAwaiter().GetResult()))Require(read.RootElement.GetProperty("interface-name").GetString()=="以太网","Real reload wired global");checks.Add("real_core_accepts_wired_global_and_wifi_nodes");
            using(var read=JsonDocument.Parse(SplitRouting.Get(api,"proxies").GetAwaiter().GetResult()))Require(read.RootElement.GetProperty("proxies").TryGetProperty("IPv6Lens-Test",out _),"Marker applied");checks.Add("real_core_marker_readback");
            SplitRouting.Reload(api,original).GetAwaiter().GetResult();
            using(var read=JsonDocument.Parse(SplitRouting.Get(api,"proxies").GetAwaiter().GetResult()))Require(!read.RootElement.GetProperty("proxies").TryGetProperty("IPv6Lens-Test",out _),"Marker removed");checks.Add("real_core_restore_original");
        }finally{if(!process.HasExited){process.Kill(true);process.WaitForExit();}stdout.GetAwaiter().GetResult();stderr.GetAwaiter().GetResult();File.Delete(config);}
    }
    internal static void Owner(string output){
        try{new SplitRouting().EnableAsync().GetAwaiter().GetResult();File.WriteAllText(output,"ready");Thread.Sleep(Timeout.Infinite);}
        catch(Exception ex){File.WriteAllText(output,"failed: "+ex.Message);Environment.ExitCode=1;}
    }
    internal static void Live(string output)
    {
        var checks=new List<string>();var service=new SplitRouting();
        try{
            Require(Program.Admin,"Live test requires admin");Require(SplitRouting.ReadLease()==null,"Existing split must not be overwritten by test");
            var before=ClashNetworkProbe.Read();checks.Add("baseline_read");
            service.EnableAsync().GetAwaiter().GetResult();
            var active=service.ReadAsync().GetAwaiter().GetResult();Require(active.Active,"Split must be active after automatic routing");checks.Add("automatic_wired_priority_and_node_split_active");
            service.RestoreAsync().GetAwaiter().GetResult();var after=ClashNetworkProbe.Read();
            Require(after==before,"Route priorities must return to baseline");Require(SplitRouting.ReadLease()==null,"Recovery journal cleared");checks.Add("original_routes_and_clash_restored");
            foreach(var point in new[]{"routes","reload"}){
                bool failed=false;try{service.FailForTestAsync(point).GetAwaiter().GetResult();}catch(InvalidOperationException){failed=true;}
                Require(failed&&SplitRouting.ReadLease()==null&&ClashNetworkProbe.Read()==before,"Failure must rollback: "+point);
                checks.Add("rollback_after_"+point);
            }
            string ready=output+".ready";if(File.Exists(ready))File.Delete(ready);
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--split-owner-test");start.ArgumentList.Add(ready);
            using(var owner=Process.Start(start)!){
                try{
                    for(int i=0;i<90&&!File.Exists(ready)&&!owner.HasExited;i++)Thread.Sleep(1000);
                    Require(File.Exists(ready)&&File.ReadAllText(ready)=="ready","Crash owner must finish activation");
                    owner.Kill();owner.WaitForExit();
                    for(int i=0;i<90&&SplitRouting.ReadLease()!=null;i++)Thread.Sleep(1000);
                    Require(SplitRouting.ReadLease()==null&&ClashNetworkProbe.Read()==before,"Watchdog must restore routes after owner crash");
                    checks.Add("watchdog_restores_after_owner_killed");
                }finally{if(!owner.HasExited){owner.Kill();owner.WaitForExit();}if(File.Exists(ready))File.Delete(ready);}
            }
            File.WriteAllText(output,JsonSerializer.Serialize(new{passed=true,checks,before,after},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){try{service.RestoreAsync().GetAwaiter().GetResult();}catch{checks.Add("recovery_pending");}File.WriteAllText(output,JsonSerializer.Serialize(new{passed=false,checks,error=ex.Message}));Environment.ExitCode=1;}
    }
}
