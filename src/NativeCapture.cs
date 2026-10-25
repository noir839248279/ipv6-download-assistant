using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
namespace IPv6Lens;
internal sealed class NativeCapture : IAsyncDisposable
{
    Process? process;IntPtr job;string folder="";HttpClient? api;
    public string Status {get;private set;}="原生客户端接管未启动";
    public bool Active=>process!=null&&!process.HasExited;
    public int? ExitCode=>process!=null&&process.HasExited?process.ExitCode:null;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll")]static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr value,uint length);
    [DllImport("kernel32.dll")]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr value);
    internal static string CorePath=>Path.Combine(AppContext.BaseDirectory,"core","mihomo.exe");
    internal static object Config(DownloadRouter router,int control,string secret,bool tun)=>new Dictionary<string,object>{
        ["mode"]="rule",["ipv6"]=true,["log-level"]="silent",["find-process-mode"]="always",
        ["external-controller"]="127.0.0.1:"+control,["secret"]=secret,["allow-lan"]=false,
        ["tun"]=new Dictionary<string,object>{["enable"]=tun,["device"]="IPv6Lens-TUN",["stack"]="gvisor",["auto-route"]=true,["auto-detect-interface"]=true,["strict-route"]=false,["dns-hijack"]=new[]{"any:53","tcp://any:53"},["mtu"]=1500},
        ["dns"]=new Dictionary<string,object>{["enable"]=true,["ipv6"]=true,["enhanced-mode"]="redir-host",["default-nameserver"]=new[]{"223.5.5.5","119.29.29.29"},["nameserver"]=new[]{"https://223.5.5.5/dns-query"}},
        ["sniffer"]=new Dictionary<string,object>{["enable"]=true,["parse-pure-ip"]=true,["force-dns-mapping"]=true,["override-destination"]=true,["sniff"]=new{HTTP=new{ports=new[]{80,8080}},TLS=new{ports=new[]{443}},QUIC=new{ports=new[]{443}}}},
        ["proxies"]=new object[]{new{name="BrowserRelay",type="http",server="127.0.0.1",port=router.Port},new{name="SteamRelay",type="http",server="127.0.0.1",port=router.SteamPort},new{name="EpicRelay",type="http",server="127.0.0.1",port=router.EpicPort},
            new Dictionary<string,object>{["name"]="IPv6Origin",["type"]="direct",["ip-version"]="ipv6"}},
        ["rules"]=new[]{
            // Sniffing can erase DstIP. The origin socket's source family survives it.
            "AND,((PROCESS-NAME,"+Path.GetFileName(Environment.ProcessPath!)+"),(SRC-IP-CIDR,::/0)),IPv6Origin",
            "PROCESS-NAME,"+Path.GetFileName(Environment.ProcessPath!)+",DIRECT","PROCESS-NAME,mihomo.exe,DIRECT","PROCESS-NAME,verge-mihomo.exe,DIRECT","PROCESS-NAME,verge-mihomo-alpha.exe,DIRECT","PROCESS-NAME,clash.exe,DIRECT","PROCESS-NAME,sing-box.exe,DIRECT",
            "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve","IP-CIDR,10.0.0.0/8,DIRECT,no-resolve","IP-CIDR,172.16.0.0/12,DIRECT,no-resolve","IP-CIDR,192.168.0.0/16,DIRECT,no-resolve","IP-CIDR6,::1/128,DIRECT,no-resolve","IP-CIDR6,fe80::/10,DIRECT,no-resolve",
            "AND,((PROCESS-NAME,steam.exe),(NETWORK,UDP),(DST-PORT,443)),REJECT",
            "AND,((PROCESS-NAME,steamservice.exe),(NETWORK,UDP),(DST-PORT,443)),REJECT",
            "AND,((PROCESS-NAME,steamwebhelper.exe),(NETWORK,UDP),(DST-PORT,443)),REJECT",
            "AND,((PROCESS-NAME,EpicWebHelper.exe),(NETWORK,UDP),(DST-PORT,443)),REJECT",
            "AND,((PROCESS-NAME,EpicGamesLauncher.exe),(NETWORK,UDP),(DST-PORT,443)),REJECT",
            "AND,((PROCESS-NAME,steam.exe),(NETWORK,TCP),(OR,((DST-PORT,80),(DST-PORT,443)))),SteamRelay",
            "AND,((PROCESS-NAME,steamservice.exe),(NETWORK,TCP),(OR,((DST-PORT,80),(DST-PORT,443)))),SteamRelay",
            "AND,((PROCESS-NAME,steamwebhelper.exe),(NETWORK,TCP),(OR,((DST-PORT,80),(DST-PORT,443)))),SteamRelay",
            "AND,((PROCESS-NAME,EpicGamesLauncher.exe),(NETWORK,TCP),(OR,((DST-PORT,80),(DST-PORT,443)))),EpicRelay",
            "AND,((PROCESS-NAME,EpicWebHelper.exe),(NETWORK,TCP),(OR,((DST-PORT,80),(DST-PORT,443)))),EpicRelay",
            "DOMAIN-SUFFIX,quark.cn,BrowserRelay","DOMAIN-SUFFIX,quarkdrive.com,BrowserRelay","DOMAIN-SUFFIX,clouddrive.cn,BrowserRelay",
            "DOMAIN-SUFFIX,bilivideo.com,BrowserRelay","DOMAIN-SUFFIX,bilivideo.cn,BrowserRelay","DOMAIN-SUFFIX,googlevideo.com,BrowserRelay",
            "DOMAIN-SUFFIX,edge.mountaintoys.cn,BrowserRelay",
            "DOMAIN-SUFFIX,steamcontent.com,SteamRelay","DOMAIN,dl.steam.clngaa.com,SteamRelay","DOMAIN,dl.steam.ksyna.com,SteamRelay",
            "DOMAIN,download.epicgames.com,EpicRelay","DOMAIN,download2.epicgames.com,EpicRelay","DOMAIN,download3.epicgames.com,EpicRelay","DOMAIN,download4.epicgames.com,EpicRelay",
            "DOMAIN-SUFFIX,epicgamescdn.com,EpicRelay","DOMAIN-SUFFIX,epicgames-download1.akamaized.net,EpicRelay",
            "MATCH,DIRECT"}
    };
    static int FreePort(){using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();return ((IPEndPoint)listener.LocalEndpoint).Port;}
    public async Task Start(DownloadRouter router,bool tun=true){
        if(tun&&!Program.Admin)throw new IOException("原生客户端接管需要管理员权限");
        if(!File.Exists(CorePath))throw new IOException("缺少 core/mihomo.exe");
        // Refuse competing TUNs; do not edit any external application's config.
        if(tun){var active=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up&&(n.Name.Contains("Mihomo",StringComparison.OrdinalIgnoreCase)||n.Name.Contains("Clash",StringComparison.OrdinalIgnoreCase)||n.Name.Equals("Meta",StringComparison.OrdinalIgnoreCase)||n.Description.Contains("Meta Tunnel",StringComparison.OrdinalIgnoreCase)||n.Name.Contains("IPv6Lens-TUN",StringComparison.OrdinalIgnoreCase)));if(active)throw new IOException("检测到其他 TUN 正在运行，未叠加接管");}
        folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","core",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        int port=FreePort();string secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));string config=Path.Combine(folder,"config.json");File.WriteAllText(config,JsonSerializer.Serialize(Config(router,port,secret,tun)));
        job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new IOException("无法创建内核生命周期保护");
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION x64; kill child if owner crashes.
        IntPtr info=Marshal.AllocHGlobal(144);try{Marshal.Copy(new byte[144],0,info,144);Marshal.WriteInt32(info,16,0x2000);if(!SetInformationJobObject(job,9,info,144))throw new IOException("无法启用内核退出保护");}finally{Marshal.FreeHGlobal(info);}
        var si=new ProcessStartInfo(CorePath){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder,RedirectStandardOutput=true,RedirectStandardError=true};si.ArgumentList.Add("-d");si.ArgumentList.Add(folder);si.ArgumentList.Add("-f");si.ArgumentList.Add(config);
        process=Process.Start(si)??throw new IOException("网络内核无法启动");
        if(!AssignProcessToJobObject(job,process.Handle)){process.Kill(true);throw new IOException("无法保护网络内核生命周期");}
        // Drain but never persist raw network logs (may contain private hosts).
        process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
        api=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new Uri("http://127.0.0.1:"+port),Timeout=TimeSpan.FromSeconds(1)};api.DefaultRequestHeaders.Authorization=new("Bearer",secret);
        for(int i=0;i<30;i++){if(process.HasExited)throw new IOException("网络内核启动失败");try{using var r=await api.GetAsync("/configs");r.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await r.Content.ReadAsStringAsync());if(!tun||(data.RootElement.GetProperty("tun").GetProperty("enable").GetBoolean()&&System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name=="IPv6Lens-TUN"&&n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up))){Status=tun?"原生客户端接管已开启":"内核配置检查通过";return;}}catch{}await Task.Delay(200);}
        throw new IOException("网络内核未就绪");
    }
    public async ValueTask DisposeAsync(){
        if(process!=null){try{if(api!=null&&!process.HasExited){using var result=await api.PatchAsync("/configs",new StringContent("{\"tun\":{\"enable\":false}}",System.Text.Encoding.UTF8,"application/json"));}}catch{}
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();process=null;}
        api?.Dispose();api=null;if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}Status="原生客户端接管已关闭";
        if(folder.Length>0){try{File.Delete(Path.Combine(folder,"config.json"));}catch{}}
    }
}
