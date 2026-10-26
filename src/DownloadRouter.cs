using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IPv6Lens;
internal sealed record DownloadSite(string Id,string Name,string[] Domains);
internal sealed record DownloadFailure(DateTime TimeUtc,string Stage,string Error,string? Reason,string? SocketError,string? Host);
internal enum ServiceMode { Mixed, Strict }
internal sealed class AuxiliaryBudgetException() : IOException("辅助请求 IPv4 额度已用完；大文件仍限 IPv6");
internal sealed class IPv6RouteException(string reason,Exception? inner=null) : IOException("IPv6 route unavailable",inner)
{
    internal string Reason {get;}=reason;
}
internal sealed class DownloadCounter
{
    public long V4,V6,Proxy,Last4,Last6,LastProxy;public double Rate4,Rate6,RateProxy;
    public long Upload4,Upload6,UploadProxy,LastUpload4,LastUpload6,LastUploadProxy;public double UploadRate4,UploadRate6,UploadRateProxy;
    public volatile string State="尚未接管下载",Server="";
    public volatile bool Enabled;
    internal volatile ServiceMode Mode = ServiceMode.Strict;
    internal readonly AuxiliaryBudget Auxiliary = new();
    public long Failures;
    public volatile DownloadFailure? LastFailure;
    internal string ShareText {get{long a=Interlocked.Read(ref V4),b=Interlocked.Read(ref V6),u=Interlocked.Read(ref Proxy);double total=(double)a+b+u;return total==0?"暂无流量":(u>0?"已确认 IPv6 ":"IPv6 ")+(100d*b/total).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"%";}}
}
internal sealed class DownloadRouter : IAsyncDisposable
{
    sealed class RelaySession(string site,TcpClient client)
    {
        internal readonly string Site=site;
        internal readonly TcpClient Client=client;
        internal volatile bool IntentionalClose;
        internal void Close(){IntentionalClose=true;Client.Dispose();}
    }
    public static readonly DownloadSite[] Sites={
        new("global","其他网站",Array.Empty<string>()),
        new("quark","夸克网盘",new[]{"quark.cn","quarkdrive.com","clouddrive.cn"}),
        new("bilibili","Bilibili",new[]{"bilivideo.com","bilivideo.cn","edge.mountaintoys.cn"}),
        new("youtube","YouTube",new[]{"googlevideo.com"}),
        new("steam","Steam",new[]{"steamcontent.com","dl.steam.clngaa.com","dl.steam.ksyna.com","xz.pphimalayanrt.com","st.dl.eccdnx.com","yif.gdtstream.com","trts.baishancdnx.cn"}),
        new("epic","Epic Games",new[]{"download.epicgames.com","download2.epicgames.com","download3.epicgames.com","download4.epicgames.com","epicgamescdn.com","epicgames-download1.akamaized.net"})};
    public readonly Dictionary<string,DownloadCounter> Counters=Sites.ToDictionary(x=>x.Id,_=>new DownloadCounter());
    readonly RedirectSettings settings=new();readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<int,RelaySession> sessions=new();
    readonly ConcurrentDictionary<string,(DateTime Time,IPAddress[] Addresses)> dns=new();
    readonly ConcurrentDictionary<string,DateTime> ordinaryTls=new();
    readonly SemaphoreSlim limit=new(128);
    readonly List<Task> acceptLoops=new();
    internal Func<TcpListener,CancellationToken,ValueTask<TcpClient>> AcceptClient=(l,ct)=>l.AcceptTcpClientAsync(ct);
    readonly List<TcpListener> nativeListeners=new();
    readonly ConcurrentDictionary<string,DownloadCounter> servers=new();
    readonly ConcurrentDictionary<string,string> serverSites=new();
    ContentDns? contentDns;
    internal bool ProductionPolicies;
    internal int SteamPort {get;private set;}
    internal int EpicPort {get;private set;}
    TcpListener? listener;WebApplication? web;int connectionId;DateTime sampled=DateTime.UtcNow;
    string pacBaseUrl="";
    public int Port {get;private set;}public string PacUrl=>pacBaseUrl+"?v="+Volatile.Read(ref pacRevision);
    internal Func<string,Task<IPAddress[]>> Resolve=host=>Dns.GetHostAddressesAsync(host);
    internal Func<string,Task<IPAddress[]>> OriginalResolve=host=>Dns.GetHostAddressesAsync(host);
    internal readonly ContentNodes Nodes=new();
    internal Func<IPAddress,int,CancellationToken,Task<TcpClient>>? NodeConnector;
    internal Func<IPAddress,int,CancellationToken,Task<TcpClient>>? AutomaticConnector;
    int pacRevision;
    internal string[] ContentHosts()=>Counters.Values.OrderByDescending(c=>c.LastFailure?.TimeUtc)
        .Select(c=>c.LastFailure?.Host).Concat(servers.Keys.OrderBy(h=>h))
        .Where(h=>h!=null&&ContentSite(h)!=null&&!IPAddress.TryParse(h,out _))
        .Select(h=>h!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal string FallbackProxy="",Bypass="";
    internal bool TestMode;
    // Optional narrow experiment: only this content hostname may reach an IPv6 origin.
    internal string? ContentTestHost;
    internal bool ContentTestStopped;
    internal void StopContentTest(){ContentTestStopped=true;foreach(var s in sessions.Values)s.Close();}
    internal static bool QuarkDownloadHost(string host)=>host.EndsWith(".quark.cn",StringComparison.OrdinalIgnoreCase)&&host.StartsWith("dl-",StringComparison.OrdinalIgnoreCase);
    public bool ProtectionLost {get;private set;}
    public string? ProtectionLossReason {get;private set;}
    public DateTime? ProtectionLostAtUtc {get;private set;}
    long capacityRejected;
    long acceptErrors;int lastAcceptSocketError;
    long processAttributed;
    internal long AcceptErrors=>Interlocked.Read(ref acceptErrors);
    internal object Health()=>new{activeSessions=sessions.Count,capacityRejected=Interlocked.Read(ref capacityRejected),acceptErrors=AcceptErrors,lastAcceptSocketError=Volatile.Read(ref lastAcceptSocketError),activeAcceptLoops=acceptLoops.Count(t=>!t.IsCompleted),processAttributed=Interlocked.Read(ref processAttributed),ProtectionLossReason,ProtectionLostAtUtc};
    internal Func<bool>? OwnershipProbe;
    bool CheckOwnership(){
        if(ProtectionLost)return false;
        bool owned;try{owned=OwnershipProbe?.Invoke()??(TestMode||settings.StillOwned);}catch{owned=false;}
        if(Counters.Values.Any(c=>c.Enabled)&&!owned){
            ProtectionLost=true;
            ProtectionLossReason??="system_proxy_ownership_lost";ProtectionLostAtUtc=DateTime.UtcNow;
            foreach(var s in sessions.Values)if(!ProductionPolicies||Counters[s.Site].Enabled)s.Close();
            foreach(var r in Counters.Values)r.State=r.Enabled?"代理设置失效：未受保护，请暂停下载":"已关闭，允许 IPv4／原代理";
            return false;
        }
        return true;
    }
    static readonly HashSet<string> QuarkControls=new(StringComparer.OrdinalIgnoreCase){"pan.quark.cn","drive.quark.cn","drive-pc.quark.cn","drive-h.quark.cn","drive-m.quark.cn","pan-api.quark.cn","open-api-drive.quark.cn","quark-translation-api-zb.quark.cn","browser-cloud.quark.cn","access-open.quark.cn","open-cms-api.quark.cn","hp-cms.quark.cn","userver.upaas.quark.cn","usync.quark.cn","px.wpk.quark.cn","unpm-upaas.quark.cn","vault.quark.cn","api-album.quark.cn","browser-agent-api.quark.cn","sync-control.quark.cn","drive-social-api.quark.cn","www.quark.cn","quark.cn"};
    internal static readonly Dictionary<string,string> AuxiliaryHosts = BuildAuxiliaryHosts();
    static Dictionary<string,string> BuildAuxiliaryHosts(){
        var hosts=QuarkControls.ToDictionary(h=>h,_=>"quark",StringComparer.OrdinalIgnoreCase);
        void Add(string site,params string[] names){foreach(string name in names)hosts[name]=site;}
        Add("bilibili","www.bilibili.com","api.bilibili.com","passport.bilibili.com");
        Add("steam","store.steampowered.com","login.steampowered.com","api.steampowered.com","steamcommunity.com");
        Add("epic","accounts.epicgames.com","account-public-service-prod.ol.epicgames.com");
        Add("youtube","www.youtube.com","accounts.google.com","youtubei.googleapis.com");
        return hosts;
    }
    public static string? Classify(string host)=>AuxiliaryHosts.GetValueOrDefault(host)??Sites.FirstOrDefault(s=>s.Domains.Any(d=>host.Equals(d,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase)))?.Id;
    internal static bool IsControlHost(string host)=>QuarkControls.Contains(host);
    internal static IEnumerable<string> ControlHosts=>QuarkControls;
    internal static string? ContentSite(string host)=>AuxiliaryHosts.ContainsKey(host)?null:Classify(host);
    internal object[] ServerReport()=>servers.Select(p=>new{host=p.Key,site=serverSites.GetValueOrDefault(p.Key,"global"),ipv4=Interlocked.Read(ref p.Value.V4),ipv6=Interlocked.Read(ref p.Value.V6),proxyUnknown=Interlocked.Read(ref p.Value.Proxy)}).Where(x=>x.site!="global"&&!IPAddress.TryParse(x.host,out _)).OrderByDescending(x=>x.ipv4+x.ipv6+x.proxyUnknown).Take(100).Cast<object>().ToArray();
    internal string MainServer(string site)=>servers.Where(p=>serverSites.GetValueOrDefault(p.Key)==site).OrderByDescending(p=>Interlocked.Read(ref p.Value.V4)+Interlocked.Read(ref p.Value.V6)+Interlocked.Read(ref p.Value.Proxy)).Select(p=>p.Key).FirstOrDefault()??"尚无内容连接";
    internal void MarkCaptureLost(){if(!ProtectionLost)ProtectionLossReason="network_core_exited";OwnershipProbe=()=>false;CheckOwnership();}
    bool CanServe(string site)=>CheckOwnership()||(ProductionPolicies&&ContentTestHost==null&&!Counters[site].Enabled);
    internal void CheckHealth()=>CheckOwnership();
    public async Task Start(bool system=true){
        TestMode=!system;
        if(system){settings.Prepare();FallbackProxy=settings.Proxy;Bypass=settings.Bypass;}
        if(ProductionPolicies){contentDns=new(FallbackProxy);Resolve=contentDns.Resolve;Nodes.Lookup=contentDns.Discover;}
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;acceptLoops.Add(Task.Run(()=>Accept(listener)));
        if(ProductionPolicies){SteamPort=StartNative("steam");EpicPort=StartNative("epic");}
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));web=builder.Build();
        string path="/"+Guid.NewGuid().ToString("N")+".pac";
        web.MapGet(path,(HttpContext c)=>{c.Response.Headers.CacheControl="no-store, no-cache, must-revalidate";return Results.Text(Pac(),"application/x-ns-proxy-autoconfig");});
        await web.StartAsync();pacBaseUrl=web.Urls.Single()+path;
    }
    public void Enable(string id,bool on){
        if(ProtectionLost&&on)throw new InvalidOperationException("代理已失效。请先暂停下载，全部关闭后重新开启。");
        var row=Counters[id];bool previous=row.Enabled;row.Enabled=on;
        try{if(!TestMode){if(Counters.Values.Any(c=>c.Enabled)){
            if(settings.Owned==null&&!ProtectionLost){settings.Prepare();FallbackProxy=settings.Proxy;Bypass=settings.Bypass;_ = Pac();}
            if(!ProtectionLost){Interlocked.Increment(ref pacRevision);settings.Apply(PacUrl);}
        }else settings.Dispose();}}
        catch{row.Enabled=previous;throw;}
        row.State=on?(row.Mode==ServiceMode.Mixed?"混合保护：内容仅 IPv6":"严格模式：仅 IPv6"):"已关闭，允许 IPv4／原代理";
        foreach(var pair in sessions.Where(p=>p.Value.Site==id))pair.Value.Close();
    }
    internal void SetMode(string id,ServiceMode mode){
        var row=Counters[id];
        if(row.Mode==mode)return;
        if(ProtectionLost&&row.Enabled)throw new InvalidOperationException("接管已失效，不能更改已开启服务的模式");
        row.Mode=mode;
        row.State=row.Enabled?(mode==ServiceMode.Mixed?"混合保护：内容仅 IPv6":"严格模式：仅 IPv6"):"已关闭，允许 IPv4／原代理";
        foreach(var s in sessions.Values.Where(s=>s.Site==id))s.Close();
    }
    public void EnableAll(){foreach(var site in Sites)Enable(site.Id,true);}
    int StartNative(string site){var l=new TcpListener(IPAddress.Loopback,0);l.Start();nativeListeners.Add(l);acceptLoops.Add(Task.Run(()=>Accept(l,site)));return ((IPEndPoint)l.LocalEndpoint).Port;}
    // Disabled services bypass the relay; cached callers still obey Connect's policy.
    public string Pac(){
        if(ContentTestHost!=null){
            string scopedOriginal=ProxyFor("https");string scopedHttpOriginal=ProxyFor("http");
            string bypass=string.Join("||",Bypass.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Equals("<local>",StringComparison.OrdinalIgnoreCase)?"isPlainHostName(host)":"shExpMatch(host,"+JsonSerializer.Serialize(p)+")"));
            return "function FindProxyForURL(url,host){host=host.toLowerCase();if(host==="+JsonSerializer.Serialize(ContentTestHost)+"||(host.indexOf('dl-')===0&&/[.]quark[.]cn$/.test(host)))return "+JsonSerializer.Serialize("PROXY 127.0.0.1:"+Port)+";"+(bypass.Length>0?"if("+bypass+")return 'DIRECT';":"")+"return url.substring(0,6)==='https:'?"+JsonSerializer.Serialize(scopedOriginal)+":"+JsonSerializer.Serialize(scopedHttpOriginal)+";}";
        }
        string local=JsonSerializer.Serialize("PROXY 127.0.0.1:"+Port);
        string httpsOriginal=JsonSerializer.Serialize(ProxyFor("https"));
        string httpOriginal=JsonSerializer.Serialize(ProxyFor("http"));
        string original="url.substring(0,6)==='https:'?"+httpsOriginal+":"+httpOriginal;
        string Match(IEnumerable<string> domains)=>string.Join("||",domains.Select(d=>"host==="+JsonSerializer.Serialize(d)+"||dnsDomainIs(host,"+JsonSerializer.Serialize("."+d)+")"));
        string result="function FindProxyForURL(url,host){host=host.toLowerCase();if(isPlainHostName(host)||shExpMatch(host,'127.*')||shExpMatch(host,'10.*')||shExpMatch(host,'192.168.*')||shExpMatch(host,'169.254.*')||/^172[.](1[6-9]|2[0-9]|3[01])[.][0-9]+[.][0-9]+$/.test(host)||host==='::1'||host==='[::1]')return 'DIRECT';\n";
        string bypassOriginal=string.Join("||",Bypass.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
            .Select(p=>p.Equals("<local>",StringComparison.OrdinalIgnoreCase)?"isPlainHostName(host)":"shExpMatch(host,"+JsonSerializer.Serialize(p)+")"));
        if(bypassOriginal.Length>0)original="("+bypassOriginal+")?'DIRECT':("+original+")";
        foreach(var group in AuxiliaryHosts.GroupBy(p=>p.Value))
            result+="if("+string.Join("||",group.Select(p=>"host==="+JsonSerializer.Serialize(p.Key)))+")return "+(Counters[group.Key].Enabled?local:original)+";\n";
        foreach(var site in Sites.Where(s=>s.Id!="global")){
            string match=Match(site.Domains);
            result+="if("+match+")return "+(Counters[site.Id].Enabled?local:original)+";\n";
        }
        result+="if(url.substring(0,5)==='http:'||url.substring(0,6)==='https:')return "+(Counters["global"].Enabled?local:original)+";return 'DIRECT';}";
        return result;
    }
    string ProxyFor(string scheme){var proxy=FallbackProxy.Trim();if(proxy.Length==0)return "DIRECT";if(proxy.Contains('=')){proxy=proxy.Split(';').FirstOrDefault(p=>p.StartsWith(scheme+"=",StringComparison.OrdinalIgnoreCase))?[(scheme.Length+1)..]??"";}
        if(proxy.Length==0)return "DIRECT";if(!Uri.TryCreate("http://"+proxy,UriKind.Absolute,out var uri)||uri.Port<1||uri.AbsolutePath!="/")throw new InvalidOperationException("现有代理格式暂不支持，未更改设置");return "PROXY "+uri.Host+":"+uri.Port;}
    async Task Accept(TcpListener source,string? forcedSite=null){
        try{while(!stop.IsCancellationRequested){
            TcpClient client;
            try{client=await AcceptClient(source,stop.Token).ConfigureAwait(false);}
            catch(SocketException ex){
                if(stop.IsCancellationRequested)break;
                // A reset while accepting must not leave an open listener with no consumer.
                Interlocked.Increment(ref acceptErrors);Volatile.Write(ref lastAcceptSocketError,ex.ErrorCode);
                await Task.Delay(100,stop.Token).ConfigureAwait(false);continue;
            }
            if(!await limit.WaitAsync(0)){Interlocked.Increment(ref capacityRejected);client.Dispose();continue;}
            _=Task.Run(async()=>{try{await Handle(client,forcedSite);}catch{}finally{client.Dispose();limit.Release();}});
        }}catch(OperationCanceledException)when(stop.IsCancellationRequested){}catch(ObjectDisposedException)when(stop.IsCancellationRequested){}
    }
    static async Task<byte[]> Header(Stream stream,CancellationToken ct){using var bytes=new MemoryStream();byte[] one=new byte[1];int end=0;while(bytes.Length<32768){if(await stream.ReadAsync(one,ct)==0)throw new IOException();bytes.WriteByte(one[0]);end=(end<<8)|one[0];if(end==0x0d0a0d0a)return bytes.ToArray();}throw new IOException("Header too large");}
    async Task<(TcpClient Client,bool Proxy)> Connect(string host,int port,bool preferV6,DownloadCounter row,string scheme,CancellationToken ct,bool allowFallback=false){
        if(preferV6&&ProductionPolicies&&port==443&&Nodes.Selected(host) is IPAddress chosen){
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(4000);
            try{
                var pinned=await (NodeConnector?.Invoke(chosen,port,timeout.Token)??NodeNetwork.Connect(chosen,port,Nodes.InterfaceIndex,timeout.Token));
                var remote=((IPEndPoint)pinned.Client.RemoteEndPoint!).Address;
                if(remote.AddressFamily!=AddressFamily.InterNetworkV6||!TestMode&&!remote.Equals(chosen)){pinned.Dispose();throw new IOException("节点出口不匹配");}
                row.State="正在使用所选 IPv6 节点";row.Server=host;return(pinned,false);
            }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){
                ct.ThrowIfCancellationRequested();throw new IPv6RouteException("selected_node_unavailable",ex);
            }
        }
        IPAddress[] addresses=Array.Empty<IPAddress>();bool dnsFailed=false;Exception? lastError=null;
        var attempted=new HashSet<IPAddress>();
        // A disabled production category must not depend on the content AAAA service.
        if(preferV6||!ProductionPolicies){
            // Strict content routes get one fresh DNS/AAAA attempt after a transient
            // empty answer or a failed candidate. This remains IPv6-only.
            int resolveAttempts=preferV6&&!allowFallback?2:1;
            for(int resolveAttempt=0;resolveAttempt<resolveAttempts;resolveAttempt++){
                if(resolveAttempt>0)dns.TryRemove(host,out _);
                addresses=Array.Empty<IPAddress>();dnsFailed=false;lastError=null;
                if(!dns.TryGetValue(host,out var cache)||DateTime.UtcNow-cache.Time>TimeSpan.FromSeconds(ProductionPolicies?30:120)){
                    try{
                        addresses=await (resolveAttempt>0&&ProductionPolicies&&!TestMode&&contentDns!=null
                            ?contentDns.ResolveAll(host):Resolve(host)).WaitAsync(TimeSpan.FromSeconds(5),ct);
                        if(dns.Count<2000)dns[host]=(DateTime.UtcNow,addresses);
                    }
                    catch(Exception ex)when(ex is SocketException or TimeoutException or HttpRequestException or TaskCanceledException){ct.ThrowIfCancellationRequested();dnsFailed=true;}
                }else addresses=cache.Addresses;
                if(port==Port&&addresses.Any(IPAddress.IsLoopback))throw new IOException("不能转发到自身");
                if(preferV6){
                    var candidates=addresses.Where(a=>a.AddressFamily==AddressFamily.InterNetworkV6&&(TestMode||ContentNodes.PublicV6(a)))
                        .Distinct().OrderBy(a=>attempted.Contains(a)).Take(3).ToArray();
                    foreach(var ip in candidates)attempted.Add(ip);
                    if(candidates.Length>0){
                        try{
                            var client=await RaceIPv6(host,port,scheme,candidates,allowFallback,ct);
                            row.State="正在使用 IPv6";row.Server=host;return(client,false);
                        }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException){
                            ct.ThrowIfCancellationRequested();lastError=ex;
                        }
                    }
                    if(allowFallback||resolveAttempt==resolveAttempts-1)break;
                }else break;
            }
            if(preferV6&&!allowFallback){
                if(addresses.Length>0)dns.TryRemove(host,out _);
                string reason=dnsFailed?"dns_failed":lastError!=null?"ipv6_connect_failed":"no_aaaa";
                throw new IPv6RouteException(reason,lastError);
            }
        }
        string proxy=ProxyFor(scheme);
        if(proxy!="DIRECT"){
            var target=new Uri("http://"+proxy[6..]);var c=new TcpClient();try{await c.ConnectAsync(target.Host,target.Port,ct);var stream=c.GetStream();await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"),ct);var reply=Encoding.ASCII.GetString(await Header(stream,ct));if(!reply.Split('\r')[0].Contains(" 200 "))throw new IOException("原代理连接失败");row.State=preferV6?"IPv6 不可达，已回退原代理（出口协议未知）":"原代理路径（出口协议未知）";row.Server=host;return(c,true);}catch{c.Dispose();throw;}}
        if(ProductionPolicies)addresses=await OriginalResolve(host).WaitAsync(TimeSpan.FromSeconds(5),ct);
        if(port==Port&&addresses.Any(IPAddress.IsLoopback))throw new IOException("不能转发到自身");
        foreach(var ip in addresses.OrderBy(a=>a.AddressFamily==AddressFamily.InterNetwork?0:1).Take(6)){var c=new TcpClient(ip.AddressFamily);try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(2500);await c.ConnectAsync(ip,port,timeout.Token);row.State=ip.AddressFamily==AddressFamily.InterNetworkV6?"正在使用 IPv6":"IPv6 不可用，实际使用 IPv4";row.Server=host;return(c,false);}catch{c.Dispose();}}
        throw new IOException("IPv6 和原连接均不可达");
    }
    async Task<TcpClient> RaceIPv6(string host,int port,string scheme,IPAddress[] addresses,bool allowFallback,CancellationToken ct){
        using var race=CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(2500);
        async Task<TcpClient> Dial(IPAddress ip){
            if(AutomaticConnector!=null)return await AutomaticConnector(ip,port,race.Token);
            if(ProductionPolicies&&!TestMode&&!allowFallback)
                return await NodeNetwork.Connect(ip,port,Nodes.InterfaceIndex,race.Token);
            var client=new TcpClient(AddressFamily.InterNetworkV6);
            try{await client.ConnectAsync(ip,port,race.Token);return client;}
            catch{client.Dispose();throw;}
        }
        async Task<(TcpClient? Client,Exception? Error)> Attempt(IPAddress ip){
            TcpClient? client=null;
            try{
                client=await Dial(ip);
                string tlsKey=host+"|"+ip+"|"+port;
                if(ProductionPolicies&&allowFallback&&scheme=="https"&&
                    (!ordinaryTls.TryGetValue(tlsKey,out var checkedAt)||DateTime.UtcNow-checkedAt>TimeSpan.FromSeconds(60))){
                    using(var ssl=new SslStream(client.GetStream()))
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host},race.Token);
                    client.Dispose();client=null;client=await Dial(ip);
                    if(ordinaryTls.Count<2000)ordinaryTls[tlsKey]=DateTime.UtcNow;
                }
                race.Token.ThrowIfCancellationRequested();
                if(((IPEndPoint)client.Client.RemoteEndPoint!).Address.AddressFamily!=AddressFamily.InterNetworkV6)
                    throw new IOException("IPv6 candidate returned a non-IPv6 socket");
                return(client,null);
            }catch(Exception ex){client?.Dispose();return(null,ex);}
        }
        var pending=addresses.Select(Attempt).ToList();
        Exception? last=null;
        try{
            while(pending.Count>0){
                var task=await Task.WhenAny(pending);pending.Remove(task);
                var result=await task;
                if(result.Client!=null)return result.Client;
                last=result.Error;
            }
            ct.ThrowIfCancellationRequested();
            throw new IOException("No IPv6 candidate connected",last);
        }finally{
            race.Cancel();
            foreach(var task in pending){var result=await task;result.Client?.Dispose();}
        }
    }
    async Task Handle(TcpClient client,string? forcedSite=null){
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);handshake.CancelAfter(20000);
        var input=client.GetStream();var header=await Header(input,handshake.Token);var text=Encoding.Latin1.GetString(header);var first=text.Split('\r')[0].Split(' ');if(first.Length!=3)return;
        bool tunnel=first[0]=="CONNECT";Uri? url;
        if(!Uri.TryCreate(tunnel?"https://"+first[1]:first[1],UriKind.Absolute,out url)||url.Scheme is not ("http" or "https")||url.UserInfo.Length>0)return;
        string? processSite=ProductionPolicies&&forcedSite==null?Processes.ProxyClientSite(client):null;
        if(processSite!=null)Interlocked.Increment(ref processAttributed);
        string host=url.IdnHost;var site=forcedSite??processSite??Classify(host)??"global";
        bool auxiliary=ProductionPolicies&&AuxiliaryHosts.GetValueOrDefault(host)==site;
        var row=Counters[site];int id=Interlocked.Increment(ref connectionId);var session=new RelaySession(site,client);sessions[id]=session;
        bool responseStarted=false,originalControl=false;string stage="connect";
        try{
            if(!CanServe(site))throw new IOException("代理设置失效");
            if(ContentTestHost!=null&&(ContentTestStopped||!host.Equals(ContentTestHost,StringComparison.OrdinalIgnoreCase)))throw new IOException("测试已停止或下载域名未验证，已拒绝连接");
            originalControl=auxiliary&&row.Enabled&&row.Mode==ServiceMode.Mixed;
            if(originalControl&&row.Auxiliary.Used>=AuxiliaryBudget.Limit)throw new AuxiliaryBudgetException();
            bool legacyGlobal=ProductionPolicies&&site=="global"&&!row.Enabled;
            bool strict=row.Enabled&&!originalControl;
            // Login, listing and link issuance must not inherit the unrelated global IPv6 preference.
            var connected=await Connect(host,url.Port,row.Enabled&&!ProtectionLost&&!originalControl,row,url.Scheme,handshake.Token,legacyGlobal);using var upstream=connected.Client;Stream output=upstream.GetStream();
            bool v6=((IPEndPoint)upstream.Client.RemoteEndPoint!).Address.AddressFamily==AddressFamily.InterNetworkV6;
            if(originalControl&&(!v6||connected.Proxy))output=new AuxiliaryStream(output,row.Auxiliary);
            strict=row.Enabled&&!(auxiliary&&row.Mode==ServiceMode.Mixed);
            if(!CanServe(site)||(ContentTestHost!=null&&ContentTestStopped)||(strict&&(!v6||connected.Proxy)))throw new IOException("路由策略已变更，拒绝原路径");
            if(tunnel){responseStarted=true;await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),handshake.Token);}
            else{
                // Preserve the request path and Host; prevent cross-host reuse and strip proxy credentials.
                var lines=text.Split("\r\n");var clean=new StringBuilder(first[0]+" "+url.PathAndQuery+" "+first[2]+"\r\n");
                foreach(var line in lines.Skip(1).Where(l=>l.Length>0&&!l.StartsWith("Proxy-",StringComparison.OrdinalIgnoreCase)&&!l.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase)))clean.Append(line).Append("\r\n");clean.Append("Connection: close\r\n\r\n");var requestHeader=Encoding.Latin1.GetBytes(clean.ToString());await output.WriteAsync(requestHeader,handshake.Token);CountUpload(row,requestHeader.Length,v6,connected.Proxy);
            }
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            DownloadCounter? server=null;if(site!="global"&&servers.Count<2000){server=servers.GetOrAdd(host,_=>new());serverSites[host]=site;}
            stage="transfer";
            responseStarted=true;
            await Relay(input,output,client.Client,upstream.Client,row,v6,connected.Proxy,lifetime.Token,server);
        }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or TimeoutException or ObjectDisposedException or HttpRequestException or System.Text.Json.JsonException){
            if(session.IntentionalClose||stop.IsCancellationRequested)return;
            Interlocked.Increment(ref row.Failures);
            Exception? cause=ex;while(cause!=null&&cause is not SocketException)cause=cause.InnerException;
            string? reason=(ex as IPv6RouteException)?.Reason;
            row.LastFailure=new(DateTime.UtcNow,stage,ex.GetType().Name,reason,(cause as SocketException)?.SocketErrorCode.ToString(),site=="global"?null:host);
            row.State=ex is AuxiliaryBudgetException?ex.Message:ProtectionLost&&row.Enabled?"代理设置失效：未受保护，请暂停下载":originalControl?"辅助请求原路径失败；内容仍限 IPv6":row.Enabled?reason switch{"no_aaaa"=>"此下载域名未查到 IPv6；未回退 IPv4","dns_failed"=>"IPv6 解析失败；未回退 IPv4","ipv6_connect_failed"=>"IPv6 连接失败；未回退 IPv4","selected_node_unavailable"=>"所选节点不可达；请更新节点或恢复自动",_=>"IPv6 传输失败；未回退 IPv4"}:"连接失败："+ex.GetType().Name;
            if(!responseStarted)try{using var replyTimeout=new CancellationTokenSource(1000);await input.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),replyTimeout.Token);}catch{}
        }
        finally{sessions.TryRemove(id,out _);}
    }
    internal static async Task Relay(Stream input,Stream output,Socket client,Socket upstream,DownloadCounter row,bool v6,bool proxy,CancellationToken ct,DownloadCounter? server=null){
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up=Pump(input,output,row,v6,proxy,lifetime.Token,server,true);var down=Pump(output,input,row,v6,proxy,lifetime.Token,server);
        try{
            var first=await Task.WhenAny(up,down);
            if(up.IsFaulted)await up;
            if(down.IsFaulted)await down;
            await first; // Propagate the first real read/write failure before cleanup.
            // EOF closes only the destination's send half; the response may still be pending.
            (first==up?upstream:client).Shutdown(SocketShutdown.Send);
            await (first==up?down:up).WaitAsync(TimeSpan.FromSeconds(30),ct);
        }finally{
            lifetime.Cancel();
            try{await Task.WhenAll(up,down);}catch{} // Observe sibling cancellation, not the primary failure.
        }
    }
    static async Task Pump(Stream source,Stream target,DownloadCounter? row,bool v6,bool proxy,CancellationToken ct,DownloadCounter? server=null,bool upload=false){byte[] buffer=new byte[64*1024];while(true){int n=await source.ReadAsync(buffer,ct);if(n==0)return;foreach(var c in new[]{row,server})if(c!=null){if(upload)CountUpload(c,n,v6,proxy);else if(proxy)Interlocked.Add(ref c.Proxy,n);else if(v6)Interlocked.Add(ref c.V6,n);else Interlocked.Add(ref c.V4,n);}await target.WriteAsync(buffer.AsMemory(0,n),ct);}}
    static void CountUpload(DownloadCounter c,int n,bool v6,bool proxy){if(proxy)Interlocked.Add(ref c.UploadProxy,n);else if(v6)Interlocked.Add(ref c.Upload6,n);else Interlocked.Add(ref c.Upload4,n);}
    public void Sample(){CheckOwnership();double seconds=Math.Max(.001,(DateTime.UtcNow-sampled).TotalSeconds);sampled=DateTime.UtcNow;SampleCounters(Counters.Values,seconds);foreach(var row in Counters.Values)if(ProtectionLost&&row.Enabled)row.State="代理设置失效：未受保护，请暂停下载";}
    internal static void SampleCounters(IEnumerable<DownloadCounter> counters,double seconds){foreach(var row in counters){long a=Interlocked.Read(ref row.V4),b=Interlocked.Read(ref row.V6),u=Interlocked.Read(ref row.Proxy);row.Rate4=Math.Max(0,a-row.Last4)/seconds;row.Rate6=Math.Max(0,b-row.Last6)/seconds;row.RateProxy=Math.Max(0,u-row.LastProxy)/seconds;row.Last4=a;row.Last6=b;row.LastProxy=u;long x=Interlocked.Read(ref row.Upload4),y=Interlocked.Read(ref row.Upload6),z=Interlocked.Read(ref row.UploadProxy);row.UploadRate4=Math.Max(0,x-row.LastUpload4)/seconds;row.UploadRate6=Math.Max(0,y-row.LastUpload6)/seconds;row.UploadRateProxy=Math.Max(0,z-row.LastUploadProxy)/seconds;row.LastUpload4=x;row.LastUpload6=y;row.LastUploadProxy=z;}}
    public void Reset()=>ResetCounters(Counters.Values.Concat(servers.Values));
    internal static void ResetCounters(IEnumerable<DownloadCounter> counters){foreach(var r in counters){Interlocked.Exchange(ref r.V4,0);Interlocked.Exchange(ref r.V6,0);Interlocked.Exchange(ref r.Proxy,0);r.Last4=r.Last6=r.LastProxy=0;r.Rate4=r.Rate6=r.RateProxy=0;Interlocked.Exchange(ref r.Upload4,0);Interlocked.Exchange(ref r.Upload6,0);Interlocked.Exchange(ref r.UploadProxy,0);r.LastUpload4=r.LastUpload6=r.LastUploadProxy=0;r.UploadRate4=r.UploadRate6=r.UploadRateProxy=0;}}
    public async ValueTask DisposeAsync(){settings.Dispose();stop.Cancel();listener?.Stop();foreach(var l in nativeListeners)l.Stop();foreach(var s in sessions.Values)s.Close();await Task.WhenAll(acceptLoops);if(web!=null){await web.StopAsync();await web.DisposeAsync();}contentDns?.Dispose();stop.Dispose();}
}


