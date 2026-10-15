using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;
internal sealed record SplitLease(int Pid,long Birth,string Marker,string Wifi,string Ethernet,string SourcePath,string SourceHash,string OriginalYaml,SplitRoute[] Routes,Dictionary<string,string> Choices);
internal sealed record SplitState(ClashNetworkSnapshot Network,bool Managed,bool Active,string Message);

internal sealed class SplitRouting
{
    internal static string Journal=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","split-lease.bin");
    const string MutexName=@"Local\IPv6Lens-SplitRouting-v1";
    static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    internal static HttpClient Client()
    {
        var settings=ClashInterfaceBinding.ReadControllerSettings();
        var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=settings.BaseUri,Timeout=TimeSpan.FromSeconds(30)};
        if(settings.Secret.Length>0)client.DefaultRequestHeaders.Authorization=new("Bearer",settings.Secret);
        return client;
    }
    internal static async Task<string> Get(HttpClient api,string path)
    {
        using var response=await api.GetAsync(path).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Clash 控制器请求失败（HTTP {(int)response.StatusCode}）。未读取或展示私密配置。");
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }
    internal static async Task Reload(HttpClient api,string yaml)
    {
        using var response=await api.PutAsync("configs?force=true",new StringContent(JsonSerializer.Serialize(new{payload=yaml}),Encoding.UTF8,"application/json")).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Clash 未接受分流配置（HTTP {(int)response.StatusCode}），正在恢复原设置。");
    }
    static Dictionary<string,string> Choices(string json)
    {
        using var data=JsonDocument.Parse(json);var choices=new Dictionary<string,string>();
        foreach(var p in data.RootElement.GetProperty("proxies").EnumerateObject())
            if(p.Value.TryGetProperty("type",out var type)&&type.GetString()=="Selector"&&p.Value.TryGetProperty("now",out var now)&&now.GetString() is {} name)choices[p.Name]=name;
        return choices;
    }
    static async Task RestoreChoices(HttpClient api,Dictionary<string,string> choices)
    {
        foreach(var (group,name) in choices){
            using var response=await api.PutAsync("proxies/"+Uri.EscapeDataString(group),new StringContent(JsonSerializer.Serialize(new{name}),Encoding.UTF8,"application/json")).ConfigureAwait(false);
            // Groups may have disappeared after a subscription change. Never log names or responses.
            if(!response.IsSuccessStatusCode && response.StatusCode!=HttpStatusCode.NotFound && response.StatusCode!=HttpStatusCode.BadRequest)
                throw new InvalidOperationException("Clash 节点选择恢复未完成，请检查当前选择。");
        }
    }
    static bool HasMarker(string proxies,string marker){using var doc=JsonDocument.Parse(proxies);return doc.RootElement.GetProperty("proxies").TryGetProperty(marker,out _);}
    internal static SplitLease? ReadLease()
    {
        if(!File.Exists(Journal))return null;
        byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(Journal),null,DataProtectionScope.CurrentUser);
        try{return JsonSerializer.Deserialize<SplitLease>(plain,JsonOptions)??throw new IOException("恢复记录不可读。");}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    static void WriteLease(SplitLease lease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(lease);
        try{File.WriteAllBytes(Journal+".tmp",ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));File.Move(Journal+".tmp",Journal,true);}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    internal static bool OwnerAlive(SplitLease lease){try{using var p=Process.GetProcessById(lease.Pid);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==lease.Birth;}catch{return false;}}
    static T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,MutexName);bool owned=false;
        try{try{owned=mutex.WaitOne(TimeSpan.FromSeconds(60));}catch(AbandonedMutexException){owned=true;}if(!owned)throw new TimeoutException("另一项分流操作还在完成，请稍后重试。");return action();}finally{if(owned)mutex.ReleaseMutex();}
    }
    static string SourcePath()
    {
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"io.github.clash-verge-rev.clash-verge-rev");
        foreach(var name in new[]{"clash-verge-runtime.yaml","clash-verge.yaml"}){var path=Path.Combine(dir,name);if(File.Exists(path))return path;}
        throw new InvalidOperationException("未找到 Clash Verge 生成的完整运行配置，请先启动 Clash Verge。");
    }
    static string Fingerprint(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); // Detect later configuration edits before restoring.
    internal async Task<SplitState> ReadAsync()
    {
        var network=await Task.Run(ClashNetworkProbe.Read).ConfigureAwait(false);var lease=ReadLease();
        using var api=Client();
        if(lease==null){await Get(api,"configs").ConfigureAwait(false);return new(network,false,false,"开启后自动调整有线优先，无需手动设置。");}
        bool applied=false;
        try {
            using var doc=JsonDocument.Parse(await Get(api,"configs").ConfigureAwait(false));
            applied=doc.RootElement.TryGetProperty("interface-name",out var iface)&&iface.GetString()==lease.Ethernet&&HasMarker(await Get(api,"proxies").ConfigureAwait(false),lease.Marker);
        }catch{return new(network,true,false,"Clash 暂不可达；恢复记录已保留，点击恢复默认。");}
        bool active=applied&&network.CanEnable;
        return new(network,true,active,active?"代理节点走 Wi-Fi；DIRECT 与普通直连优先走有线。":"网卡或 Clash 配置已变化；点击恢复默认后重新开启。");
    }
    internal Task EnableAsync()=>Task.Run(()=>Locked(()=>{Enable();return true;}));
    internal Task FailForTestAsync(string point)=>Task.Run(()=>Locked(()=>{Enable(point);return true;}));
    void Enable(string? fail=null)
    {
        if(!Program.Admin)throw new InvalidOperationException("自动调整路由需要管理员权限，请以管理员身份运行助手。");
        if(ReadLease() is {} existing){if(OwnerAlive(existing)&&existing.Pid!=Environment.ProcessId)throw new InvalidOperationException("另一个助手窗口正在管理分流。");RestoreCore(existing);}
        var network=ClashNetworkProbe.Read();var routes=SplitRoutes.Capture(network);
        using var api=Client();string live=Get(api,"configs").GetAwaiter().GetResult();
        using(var doc=JsonDocument.Parse(live)){
            if(!doc.RootElement.TryGetProperty("mode",out var mode)||mode.GetString()!="rule")throw new InvalidOperationException("请将 Clash Verge 切到规则模式后开启分流。");
        }
        string source=SourcePath(),sourceText=File.ReadAllText(source);
        string original=SplitConfig.WithLiveSettings(sourceText,live);
        var marker="IPv6Lens-Split-"+Guid.NewGuid().ToString("N");
        string split=SplitConfig.Build(original,network.WifiName,network.EthernetName,marker);
        using(var providers=JsonDocument.Parse(Get(api,"providers/proxies").GetAwaiter().GetResult())){
            // A provider override applies to all its nodes. Do not bind provider-supplied DIRECT nodes to Wi-Fi.
            var yaml=SplitConfig.Parse(original);
            if(SplitConfig.Get(yaml,"proxy-providers") is YamlDotNet.RepresentationModel.YamlMappingNode declared)
                foreach(var provider in providers.RootElement.GetProperty("providers").EnumerateObject())
                    if(declared.Children.ContainsKey(new YamlDotNet.RepresentationModel.YamlScalarNode(provider.Name))&&provider.Value.TryGetProperty("proxies",out var nodes)&&nodes.EnumerateArray().Any(p=>p.TryGetProperty("type",out var t)&&t.GetString()=="Direct"))
                        throw new InvalidOperationException("订阅提供器含直连节点，无法安全统一绑定无线网卡。");
        }
        var choices=Choices(Get(api,"proxies").GetAwaiter().GetResult());
        using var self=Process.GetCurrentProcess();
        var lease=new SplitLease(Environment.ProcessId,self.StartTime.ToUniversalTime().Ticks,marker,network.WifiName,network.EthernetName,source,Fingerprint(sourceText),original,routes,choices);
        WriteLease(lease);
        try {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"--split-watch",lease.Pid.ToString(),lease.Birth.ToString()})start.ArgumentList.Add(arg);
            using var watcher=Process.Start(start)??throw new IOException("无法启动分流恢复守护进程。");
            SplitRoutes.Apply(routes);
            if(fail=="routes")throw new InvalidOperationException("test failure after routes");
            if(!ClashNetworkProbe.Read().CanEnable)throw new InvalidOperationException("调整后仍无法确认有线优先，已尝试恢复原设置。");
            Reload(api,split).GetAwaiter().GetResult();
            if(fail=="reload")throw new InvalidOperationException("test failure after reload");
            RestoreChoices(api,choices).GetAwaiter().GetResult();
            using var readback=JsonDocument.Parse(Get(api,"configs").GetAwaiter().GetResult());
            if(readback.RootElement.GetProperty("interface-name").GetString()!=network.EthernetName||!HasMarker(Get(api,"proxies").GetAwaiter().GetResult(),marker))
                throw new InvalidOperationException("Clash 分流回读不一致。");
        } catch {try{RestoreCore(lease);}catch{throw new InvalidOperationException("开启失败，部分设置仍待恢复；请保持助手打开，点击恢复默认重试。");}throw;}
    }
    internal static bool RestoresTun {get{var lease=ReadLease();return lease!=null&&SplitConfig.Get(SplitConfig.Parse(lease.OriginalYaml),"tun") is YamlDotNet.RepresentationModel.YamlMappingNode tun&&SplitConfig.Text(tun,"enable").Equals("true",StringComparison.OrdinalIgnoreCase);}}
    internal Task RestoreAsync()=>Task.Run(()=>Locked(()=>{if(ReadLease() is {} lease){if(OwnerAlive(lease)&&lease.Pid!=Environment.ProcessId)throw new InvalidOperationException("另一助手窗口拥有分流设置，请在原窗口关闭。");RestoreCore(lease);}return true;}));
    static void RestoreCore(SplitLease lease)
    {
        Exception? failure=null;
        try{
            using var api=Client();
            if(HasMarker(Get(api,"proxies").GetAwaiter().GetResult(),lease.Marker)){
                string yaml=lease.OriginalYaml;
                if(File.Exists(lease.SourcePath)){string current=File.ReadAllText(lease.SourcePath);if(Fingerprint(current)!=lease.SourceHash)yaml=current;}
                var choices=Choices(Get(api,"proxies").GetAwaiter().GetResult());
                Reload(api,yaml).GetAwaiter().GetResult();RestoreChoices(api,choices).GetAwaiter().GetResult();
                if(HasMarker(Get(api,"proxies").GetAwaiter().GetResult(),lease.Marker))throw new IOException("Clash 仍保留分流标记。");
            }
        }catch(Exception ex){failure=ex;}
        // Network recovery must still run when Clash is unavailable. A new core starts from untouched files.
        try{SplitRoutes.Restore(lease.Routes);}catch(Exception ex){failure=ex;}
        if(failure!=null)throw new InvalidOperationException("恢复尚未全部完成，记录已保留；请启动 Clash 后点击恢复默认重试。",failure);
        File.Delete(Journal);
    }
    internal static void RecoverStale()=>Locked(()=>{if(ReadLease() is {} lease&&!OwnerAlive(lease))RestoreCore(lease);return true;});
    internal static void Watch(int pid,long birth)
    {
        try{
            while(ReadLease() is {} lease&&lease.Pid==pid&&lease.Birth==birth&&OwnerAlive(lease))Thread.Sleep(1000);
            // Bounded retries handle controller restart without leaving Windows route changes behind.
            for(int i=0;i<12;i++){try{Locked(()=>{if(ReadLease() is {} lease&&lease.Pid==pid&&lease.Birth==birth&&!OwnerAlive(lease))RestoreCore(lease);return true;});return;}catch{Thread.Sleep(5000);}}
        }catch{}
    }
}
