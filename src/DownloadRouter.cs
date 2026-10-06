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
internal sealed class DownloadCounter
{
    public long V4,V6,Proxy,Last4,Last6,LastProxy;public double Rate4,Rate6,RateProxy;
    public volatile string State="尚未接管下载",Server="";
    public volatile bool Enabled;
    public long Failures;
    internal string ShareText {get{long a=Interlocked.Read(ref V4),b=Interlocked.Read(ref V6),u=Interlocked.Read(ref Proxy);double total=(double)a+b+u;return total==0?"暂无流量":(u>0?"已确认 IPv6 ":"IPv6 ")+(100d*b/total).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"%";}}
}
internal sealed class DownloadRouter : IAsyncDisposable
{
    public static readonly DownloadSite[] Sites={
        new("global","其他网站",Array.Empty<string>()),
        new("quark","夸克网盘",new[]{"quark.cn","quarkdrive.com","clouddrive.cn"}),
        new("bilibili","Bilibili",new[]{"bilivideo.com","bilivideo.cn","edge.mountaintoys.cn"}),
        new("youtube","YouTube",new[]{"googlevideo.com"}),
        new("steam","Steam",new[]{"steamcontent.com","dl.steam.clngaa.com","dl.steam.ksyna.com","xz.pphimalayanrt.com","st.dl.eccdnx.com","yif.gdtstream.com","trts.baishancdnx.cn"}),
        new("epic","Epic Games",new[]{"download.epicgames.com","download2.epicgames.com","download3.epicgames.com","download4.epicgames.com","epicgamescdn.com","epicgames-download1.akamaized.net"})};
    public readonly Dictionary<string,DownloadCounter> Counters=Sites.ToDictionary(x=>x.Id,_=>new DownloadCounter());
    readonly RedirectSettings settings=new();readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<int,(string Site,TcpClient Client)> sessions=new();
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
    public int Port {get;private set;}public string PacUrl {get;private set;}="";
    internal Func<string,Task<IPAddress[]>> Resolve=host=>Dns.GetHostAddressesAsync(host);
    internal Func<string,Task<IPAddress[]>> OriginalResolve=host=>Dns.GetHostAddressesAsync(host);
    internal string FallbackProxy="",Bypass="";
    internal bool TestMode;
    // Optional narrow experiment: only this content hostname may reach an IPv6 origin.
    internal string? ContentTestHost;
    internal bool ContentTestStopped;
    internal void StopContentTest(){ContentTestStopped=true;foreach(var s in sessions.Values)s.Client.Dispose();}
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
            foreach(var s in sessions.Values)if(!ProductionPolicies||s.Site!="global")s.Client.Dispose();
            foreach(var r in Counters.Values)r.State="代理设置失效：未受保护，请暂停下载";
            return false;
        }
        return true;
    }
    static readonly HashSet<string> QuarkControls=new(StringComparer.OrdinalIgnoreCase){"pan.quark.cn","drive.quark.cn","drive-pc.quark.cn","drive-h.quark.cn","drive-m.quark.cn","pan-api.quark.cn","open-api-drive.quark.cn","quark-translation-api-zb.quark.cn","browser-cloud.quark.cn","access-open.quark.cn","open-cms-api.quark.cn","hp-cms.quark.cn","userver.upaas.quark.cn","usync.quark.cn","px.wpk.quark.cn","unpm-upaas.quark.cn","vault.quark.cn","api-album.quark.cn","www.quark.cn","quark.cn"};
    public static string? Classify(string host)=>Sites.FirstOrDefault(s=>s.Domains.Any(d=>host.Equals(d,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase)))?.Id;
    internal static string? ContentSite(string host)=>QuarkControls.Contains(host)?null:Classify(host);
    internal object[] ServerReport()=>servers.Select(p=>new{host=p.Key,site=serverSites.GetValueOrDefault(p.Key,"global"),ipv4=Interlocked.Read(ref p.Value.V4),ipv6=Interlocked.Read(ref p.Value.V6),proxyUnknown=Interlocked.Read(ref p.Value.Proxy)}).Where(x=>x.site!="global"&&!IPAddress.TryParse(x.host,out _)).OrderByDescending(x=>x.ipv4+x.ipv6+x.proxyUnknown).Take(100).Cast<object>().ToArray();
    internal string MainServer(string site)=>servers.Where(p=>serverSites.GetValueOrDefault(p.Key)==site).OrderByDescending(p=>Interlocked.Read(ref p.Value.V4)+Interlocked.Read(ref p.Value.V6)+Interlocked.Read(ref p.Value.Proxy)).Select(p=>p.Key).FirstOrDefault()??"尚无内容连接";
    internal void MarkCaptureLost(){if(!ProtectionLost)ProtectionLossReason="network_core_exited";OwnershipProbe=()=>false;CheckOwnership();}
    bool CanServe(string site)=>CheckOwnership()||(ProductionPolicies&&site=="global"&&ContentTestHost==null);
    public async Task Start(bool system=true){
        TestMode=!system;
        if(system){settings.Prepare();FallbackProxy=settings.Proxy;Bypass=settings.Bypass;}
        if(ProductionPolicies){contentDns=new(FallbackProxy);Resolve=contentDns.Resolve;}
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;acceptLoops.Add(Task.Run(()=>Accept(listener)));
        if(ProductionPolicies){SteamPort=StartNative("steam");EpicPort=StartNative("epic");}
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));web=builder.Build();
        string path="/"+Guid.NewGuid().ToString("N")+".pac";
        web.MapGet(path,(HttpContext c)=>{c.Response.Headers.CacheControl="no-store, no-cache, must-revalidate";return Results.Text(Pac(),"application/x-ns-proxy-autoconfig");});
        await web.StartAsync();PacUrl=web.Urls.Single()+path;
    }
    public void Enable(string id,bool on){
        if(ProtectionLost)throw new InvalidOperationException("代理已失效。请先暂停下载，全部关闭后重新开启。");
        var row=Counters[id];row.Enabled=on;
        try{if(!TestMode){if(Counters.Values.Any(c=>c.Enabled)){
            if(settings.Owned==null){settings.Prepare();FallbackProxy=settings.Proxy;Bypass=settings.Bypass;_ = Pac();}
            settings.Apply(PacUrl);
        }else settings.Dispose();}}
        catch{row.Enabled=false;throw;}
        row.State=on?(ProductionPolicies&&id=="global"?"IPv6 优先；失败允许原路径":"仅 IPv6；等待内容连接"):"已关闭，允许 IPv4／原代理";
        foreach(var pair in sessions.Where(p=>p.Value.Site==id))pair.Value.Client.Dispose();
    }
    public void EnableAll(){foreach(var site in Sites)Enable(site.Id,true);}
    int StartNative(string site){var l=new TcpListener(IPAddress.Loopback,0);l.Start();nativeListeners.Add(l);acceptLoops.Add(Task.Run(()=>Accept(l,site)));return ((IPEndPoint)l.LocalEndpoint).Port;}
    // All web destinations use the relay while active; switches are evaluated there,
    // so cached PAC results cannot re-enable a disabled service.
    public string Pac(){
        if(ContentTestHost!=null){
            string original=ProxyFor("https");string httpOriginal=ProxyFor("http");
            string bypass=string.Join("||",Bypass.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Equals("<local>",StringComparison.OrdinalIgnoreCase)?"isPlainHostName(host)":"shExpMatch(host,"+JsonSerializer.Serialize(p)+")"));
            return "function FindProxyForURL(url,host){host=host.toLowerCase();if(host==="+JsonSerializer.Serialize(ContentTestHost)+"||(host.indexOf('dl-')===0&&/[.]quark[.]cn$/.test(host)))return "+JsonSerializer.Serialize("PROXY 127.0.0.1:"+Port)+";"+(bypass.Length>0?"if("+bypass+")return 'DIRECT';":"")+"return url.substring(0,6)==='https:'?"+JsonSerializer.Serialize(original)+":"+JsonSerializer.Serialize(httpOriginal)+";}";
        }
        string result="function FindProxyForURL(url,host){host=host.toLowerCase();if(isPlainHostName(host)||shExpMatch(host,'127.*')||shExpMatch(host,'10.*')||shExpMatch(host,'192.168.*')||shExpMatch(host,'169.254.*')||/^172[.](1[6-9]|2[0-9]|3[01])[.][0-9]+[.][0-9]+$/.test(host)||host==='::1'||host==='[::1]')return 'DIRECT';\n";
        return result+"if(url.substring(0,5)==='http:'||url.substring(0,6)==='https:')return "+JsonSerializer.Serialize("PROXY 127.0.0.1:"+Port)+";return 'DIRECT';}";
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
        IPAddress[] addresses;
        if(!dns.TryGetValue(host,out var cache)||DateTime.UtcNow-cache.Time>TimeSpan.FromSeconds(ProductionPolicies?30:120)){
            try{addresses=await Resolve(host).WaitAsync(TimeSpan.FromSeconds(5),ct);if(dns.Count<2000)dns[host]=(DateTime.UtcNow,addresses);}
            catch(Exception ex)when(ex is SocketException or TimeoutException or HttpRequestException or TaskCanceledException){addresses=Array.Empty<IPAddress>();}
        }else addresses=cache.Addresses;
        if(port==Port&&addresses.Any(IPAddress.IsLoopback))throw new IOException("不能转发到自身");
        if(preferV6){foreach(var ip in addresses.Where(a=>a.AddressFamily==AddressFamily.InterNetworkV6&&(TestMode||TrafficStore.Scope(a)=="公网")).Take(3)){var client=new TcpClient(ip.AddressFamily);try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(2500);await client.ConnectAsync(ip,port,timeout.Token);
                // Ordinary sites may fall back before handing an opaque TLS tunnel to the browser.
                // This separate, certificate-validated handshake sends no user headers or URLs.
                string tlsKey=host+"|"+ip;
                if(ProductionPolicies&&allowFallback&&scheme=="https"&&(!ordinaryTls.TryGetValue(tlsKey+"|"+port,out var checkedAt)||DateTime.UtcNow-checkedAt>TimeSpan.FromSeconds(60))){
                    using(var ssl=new SslStream(client.GetStream()))await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host},timeout.Token);
                    client.Dispose();client=new TcpClient(ip.AddressFamily);await client.ConnectAsync(ip,port,timeout.Token);
                    if(ordinaryTls.Count<2000)ordinaryTls[tlsKey+"|"+port]=DateTime.UtcNow;
                }
                row.State="正在使用 IPv6";row.Server=host;return(client,false);}catch{client.Dispose();}}
            if(!allowFallback)throw new IOException("IPv6 不可达，已拒绝回落 IPv4／原代理");}
        string proxy=ProxyFor(scheme);
        if(proxy!="DIRECT"){
            var target=new Uri("http://"+proxy[6..]);var c=new TcpClient();try{await c.ConnectAsync(target.Host,target.Port,ct);var stream=c.GetStream();await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"),ct);var reply=Encoding.ASCII.GetString(await Header(stream,ct));if(!reply.Split('\r')[0].Contains(" 200 "))throw new IOException("原代理连接失败");row.State=preferV6?"IPv6 不可达，已回退原代理（出口协议未知）":"原代理路径（出口协议未知）";row.Server=host;return(c,true);}catch{c.Dispose();throw;}}
        if(ProductionPolicies)addresses=await OriginalResolve(host).WaitAsync(TimeSpan.FromSeconds(5),ct);
        foreach(var ip in addresses.OrderBy(a=>a.AddressFamily==AddressFamily.InterNetwork?0:1).Take(6)){var c=new TcpClient(ip.AddressFamily);try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(2500);await c.ConnectAsync(ip,port,timeout.Token);row.State=ip.AddressFamily==AddressFamily.InterNetworkV6?"正在使用 IPv6":"IPv6 不可用，实际使用 IPv4";row.Server=host;return(c,false);}catch{c.Dispose();}}
        throw new IOException("IPv6 和原连接均不可达");
    }
    async Task Handle(TcpClient client,string? forcedSite=null){
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);handshake.CancelAfter(20000);
        var input=client.GetStream();var header=await Header(input,handshake.Token);var text=Encoding.Latin1.GetString(header);var first=text.Split('\r')[0].Split(' ');if(first.Length!=3)return;
        bool tunnel=first[0]=="CONNECT";Uri? url;
        if(!Uri.TryCreate(tunnel?"https://"+first[1]:first[1],UriKind.Absolute,out url)||url.Scheme is not ("http" or "https")||url.UserInfo.Length>0)return;
        string? processSite=ProductionPolicies&&forcedSite==null?Processes.ProxyClientSite(client):null;
        if(processSite!=null)Interlocked.Increment(ref processAttributed);
        string host=url.IdnHost;var site=forcedSite??processSite??(ProductionPolicies?ContentSite(host):Classify(host))??"global";
        var row=Counters[site];int id=Interlocked.Increment(ref connectionId);sessions[id]=(site,client);
        bool responseStarted=false;
        try{
            if(!CanServe(site))throw new IOException("代理设置失效");
            if(ContentTestHost!=null&&(ContentTestStopped||!host.Equals(ContentTestHost,StringComparison.OrdinalIgnoreCase)))throw new IOException("测试已停止或下载域名未验证，已拒绝连接");
            bool strict=row.Enabled&&!(ProductionPolicies&&site=="global");
            var connected=await Connect(host,url.Port,row.Enabled&&!ProtectionLost,row,url.Scheme,handshake.Token,ProductionPolicies&&site=="global");using var upstream=connected.Client;var output=upstream.GetStream();
            bool v6=((IPEndPoint)upstream.Client.RemoteEndPoint!).Address.AddressFamily==AddressFamily.InterNetworkV6;
            strict=row.Enabled&&!(ProductionPolicies&&site=="global");
            if(!CanServe(site)||(ContentTestHost!=null&&ContentTestStopped)||(strict&&(!v6||connected.Proxy)))throw new IOException("路由策略已变更，拒绝原路径");
            if(tunnel){responseStarted=true;await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),handshake.Token);}
            else{
                // Preserve the request path and Host; prevent cross-host reuse and strip proxy credentials.
                var lines=text.Split("\r\n");var clean=new StringBuilder(first[0]+" "+url.PathAndQuery+" "+first[2]+"\r\n");
                foreach(var line in lines.Skip(1).Where(l=>l.Length>0&&!l.StartsWith("Proxy-",StringComparison.OrdinalIgnoreCase)&&!l.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase)))clean.Append(line).Append("\r\n");clean.Append("Connection: close\r\n\r\n");await output.WriteAsync(Encoding.Latin1.GetBytes(clean.ToString()),handshake.Token);
            }
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            DownloadCounter? server=null;if(site!="global"&&servers.Count<2000){server=servers.GetOrAdd(host,_=>new());serverSites[host]=site;}
            var up=Pump(input,output,null,false,false,lifetime.Token);var down=Pump(output,input,row,v6,connected.Proxy,lifetime.Token,server);
            responseStarted=true;
            await Task.WhenAny(up,down);lifetime.Cancel();try{await Task.WhenAll(up,down);}catch{}
        }catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or TimeoutException or ObjectDisposedException or HttpRequestException or System.Text.Json.JsonException){Interlocked.Increment(ref row.Failures);row.State=ProtectionLost?"代理设置失效：未受保护，请暂停下载":row.Enabled&&site!="global"?"IPv6 请求未完成；没有回落 IPv4":"连接失败："+ex.GetType().Name;if(!responseStarted)try{using var replyTimeout=new CancellationTokenSource(1000);await input.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),replyTimeout.Token);}catch{}}
        finally{sessions.TryRemove(id,out _);}
    }
    static async Task Pump(Stream source,Stream target,DownloadCounter? row,bool v6,bool proxy,CancellationToken ct,DownloadCounter? server=null){byte[] buffer=new byte[64*1024];while(true){int n=await source.ReadAsync(buffer,ct);if(n==0)return;foreach(var c in new[]{row,server})if(c!=null){if(proxy)Interlocked.Add(ref c.Proxy,n);else if(v6)Interlocked.Add(ref c.V6,n);else Interlocked.Add(ref c.V4,n);}await target.WriteAsync(buffer.AsMemory(0,n),ct);}}
    public void Sample(){CheckOwnership();double seconds=Math.Max(.001,(DateTime.UtcNow-sampled).TotalSeconds);sampled=DateTime.UtcNow;foreach(var row in Counters.Values){long a=Interlocked.Read(ref row.V4),b=Interlocked.Read(ref row.V6),u=Interlocked.Read(ref row.Proxy);row.Rate4=Math.Max(0,a-row.Last4)/seconds;row.Rate6=Math.Max(0,b-row.Last6)/seconds;row.RateProxy=Math.Max(0,u-row.LastProxy)/seconds;row.Last4=a;row.Last6=b;row.LastProxy=u;if(ProtectionLost)row.State="代理设置失效：未受保护，请暂停下载";}}
    public void Reset(){foreach(var r in Counters.Values.Concat(servers.Values)){Interlocked.Exchange(ref r.V4,0);Interlocked.Exchange(ref r.V6,0);Interlocked.Exchange(ref r.Proxy,0);r.Last4=r.Last6=r.LastProxy=0;r.Rate4=r.Rate6=r.RateProxy=0;}}
    public async ValueTask DisposeAsync(){settings.Dispose();stop.Cancel();listener?.Stop();foreach(var l in nativeListeners)l.Stop();foreach(var s in sessions.Values)s.Client.Dispose();await Task.WhenAll(acceptLoops);if(web!=null){await web.StopAsync();await web.DisposeAsync();}contentDns?.Dispose();stop.Dispose();}
}


