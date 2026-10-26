using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class NativePipelineTests
{
    internal static void Run(string path)
    {
        var checks=new List<string>();
        void Check(bool pass,string name){if(!pass)throw new IOException(name);checks.Add(name);}
        try
        {
            Task.Run(async () =>
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var componentErrors=new System.Collections.Concurrent.ConcurrentQueue<string>();
                using var key=RSA.Create(2048);
                var request=new CertificateRequest("CN=pipeline.invalid",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
                using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
                using var origin=new TcpListener(IPAddress.IPv6Any,0);
                origin.Server.DualMode=true;origin.Start();
                int originPort=((IPEndPoint)origin.LocalEndpoint).Port;
                var originTasks=new List<Task>();
                var originServing=Task.Run(async () =>
                {
                    try
                    {
                        while(true)
                        {
                            var client=await origin.AcceptTcpClientAsync(stop.Token);
                            originTasks.Add(Task.Run(async () =>
                            {
                                using(client)
                                using(var tls=new SslStream(client.GetStream()))
                                try
                                {
                                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},stop.Token);
                                    await Header(tls,stop.Token);
                                    await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\npipeline"u8.ToArray(),stop.Token);
                                }
                                catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                                {if(!stop.IsCancellationRequested)componentErrors.Enqueue("origin: "+ex);}
                            }));
                        }
                    }
                    catch(OperationCanceledException){}
                });
                using var proxy=new TcpListener(IPAddress.Loopback,0);
                proxy.Start();int proxyPort=((IPEndPoint)proxy.LocalEndpoint).Port,proxyConnections=0;
                var proxyTasks=new List<Task>();
                var proxyServing=Task.Run(async () =>
                {
                    try
                    {
                        while(true)
                        {
                            var caller=await proxy.AcceptTcpClientAsync(stop.Token);
                            proxyTasks.Add(Task.Run(async () =>
                            {
                                using(caller)
                                using(var upstream=new TcpClient())
                                try
                                {
                                    string header=await Header(caller.GetStream(),stop.Token);
                                    if(!header.StartsWith($"CONNECT www.youtube.com:{originPort} HTTP/1.1\r\n",StringComparison.Ordinal))
                                        throw new IOException("Unexpected original-proxy destination: "+header.Split('\r')[0]);
                                    Interlocked.Increment(ref proxyConnections);
                                    await upstream.ConnectAsync(IPAddress.Loopback,originPort,stop.Token);
                                    await caller.GetStream().WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),stop.Token);
                                    using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                                    var up=caller.GetStream().CopyToAsync(upstream.GetStream(),lifetime.Token);
                                    var down=upstream.GetStream().CopyToAsync(caller.GetStream(),lifetime.Token);
                                    try{await Task.WhenAny(up,down);}finally{lifetime.Cancel();try{await Task.WhenAll(up,down);}catch(OperationCanceledException){}}
                                }
                                catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException)
                                {if(!stop.IsCancellationRequested)componentErrors.Enqueue("proxy: "+ex);}
                            }));
                        }
                    }
                    catch(OperationCanceledException){}
                });
                await using var router=new DownloadRouter{ProductionPolicies=true};
                await router.Start(false);
                router.FallbackProxy="127.0.0.1:"+proxyPort;
                int originalV6DnsCalls=0;
                router.Resolve=host=>{
                    if(host=="original-v6.bilivideo.com"){Interlocked.Increment(ref originalV6DnsCalls);throw new SocketException((int)SocketError.HostNotFound);}
                    return Task.FromResult(new[]{IPAddress.IPv6Loopback});
                };
                router.OriginalResolve=_=>Task.FromResult(new[]{IPAddress.Loopback});
                router.Enable("bilibili",true);router.SetMode("bilibili",ServiceMode.Mixed);
                router.Enable("quark",true);router.SetMode("quark",ServiceMode.Mixed);
                router.Enable("youtube",true);router.SetMode("youtube",ServiceMode.Mixed);
                using var free=new TcpListener(IPAddress.Loopback,0);free.Start();
                int corePort=((IPEndPoint)free.LocalEndpoint).Port;free.Stop();
                await using var dns=new RoutingRecoveryTests.FixtureDns();
                await using var core=new NativeCapture();
                await core.Start(router,false,config=>
                {
                    config["mixed-port"]=corePort;config["bind-address"]="127.0.0.1";
                    config["dns"]=new Dictionary<string,object>{
                        ["enable"]=true,["ipv6"]=true,["enhanced-mode"]="redir-host",
                        ["nameserver"]=new[]{"udp://127.0.0.1:"+dns.Port},
                        ["default-nameserver"]=new[]{"127.0.0.1:"+dns.Port},
                        ["direct-nameserver"]=new[]{"udp://127.0.0.1:"+dns.Port}};
                    // The local caller stands in for a browser, not the protected helper's origin socket.
                    string own=Path.GetFileName(Environment.ProcessPath!);
                    config["rules"]=((string[])config["rules"]).Where(r=>!r.Contains("PROCESS-NAME,"+own,StringComparison.Ordinal)&&
                        !r.StartsWith("IP-CIDR",StringComparison.Ordinal)).ToArray();
                    config["sniffer"]=new Dictionary<string,object>{
                        ["enable"]=true,["parse-pure-ip"]=true,["force-dns-mapping"]=true,["override-destination"]=false,
                        ["sniff"]=new Dictionary<string,object>{{"TLS",new{ports=new[]{originPort}}}}};
                });
                async Task Probe(int port,string host)
                {
                    using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+port),
                        ServerCertificateCustomValidationCallback=(_,c,_,_)=>c?.Thumbprint==certificate.Thumbprint})
                        {Timeout=TimeSpan.FromSeconds(4)};
                    try
                    {
                        Check(await http.GetStringAsync($"https://{host}:{originPort}/")=="pipeline",
                            (port==router.Port?"Explicit relay":"Sniffing core")+" preserves HTTPS "+host);
                    }
                    catch(Exception ex)
                    {
                        throw new IOException("Pipeline failure: "+JsonSerializer.Serialize(new{componentErrors,
                            health=router.Health(),routes=router.RecentRoutes(),proxyConnections,
                            coreExit=core.ExitCode,coreLog=core.TestLog.ToArray()}),ex);
                    }
                }
                async Task ProbeRawTls(string address,string host)
                {
                    using var client=new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback,corePort,stop.Token);
                    var stream=client.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {address}:{originPort} HTTP/1.1\r\nHost: {address}:{originPort}\r\n\r\n"),stop.Token);
                    if(!(await Header(stream,stop.Token)).StartsWith("HTTP/1.1 200"))throw new IOException("Raw TLS CONNECT failed");
                    using var tls=new SslStream(stream,false,(_,c,_,_)=>c?.GetCertHashString()==certificate.Thumbprint);
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host},stop.Token);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"),stop.Token);
                    string header=await Header(tls,stop.Token);
                    byte[] body=new byte[8];await tls.ReadExactlyAsync(body,stop.Token);
                    Check(header.StartsWith("HTTP/1.1 200")&&Encoding.ASCII.GetString(body)=="pipeline",
                        "Raw-IP core CONNECT recovers TLS SNI and replays ClientHello end to end: "+host);
                }
                try
                {
                    await Probe(router.Port,"www.bilibili.com");
                    await Probe(corePort,"www.bilibili.com");
                    await Probe(corePort,"pipeline.bilivideo.com");
                    await ProbeRawTls("127.0.0.1","raw.bilivideo.com");
                    await ProbeRawTls("[::1]","original-v6.bilivideo.com");
                    Check(originalV6DnsCalls==0,"A healthy client-selected IPv6 server is used without a second DNS lookup");
                    await Probe(corePort,"px.wpk.quark.cn");
                    await Probe(corePort,"www.youtube.com");
                    Check(router.Counters["quark"].V4>0&&router.Counters["quark"].Proxy==0&&router.Counters["quark"].Auxiliary.Used>0,
                        "Exact Quark auxiliary HTTPS bypasses original proxy with metered IPv4");
                    Check(proxyConnections==1&&router.Counters["youtube"].Proxy>0&&router.Counters["youtube"].V4==0&&
                        router.Counters["bilibili"].Proxy==0&&router.Counters["bilibili"].V4>0,
                        "Domestic auxiliary bypasses the original proxy; overseas auxiliary preserves unknown-exit accounting");
                    Check(router.Counters["bilibili"].V6>0,"Content HTTPS remains actual IPv6 without original-proxy fallback");
                    Check(router.Counters["bilibili"].ContentStarted==3&&router.Counters["bilibili"].ContentReceived>0&&
                        router.Counters["quark"].ContentStarted==0&&router.Counters["quark"].ContentReceived==0,
                        "Diagnostic content progress excludes auxiliary HTTPS traffic");
                    Check(router.Counters["bilibili"].Failures==0,"No hidden relay failures in the complete local chain");
                }
                finally
                {
                    stop.Cancel();await Task.WhenAll(originServing,proxyServing);
                    await Task.WhenAll(originTasks.Concat(proxyTasks));
                }
            }).GetAwaiter().GetResult();
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex)
        {
            Environment.ExitCode=1;
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    static async Task<string> Header(Stream stream,CancellationToken ct)
    {
        var text=new StringBuilder();byte[] b=new byte[1];int end=0;
        while(end!=0x0d0a0d0a&&text.Length<8192)
        {
            if(await stream.ReadAsync(b,ct)==0)throw new IOException("Unexpected fixture EOF");
            text.Append((char)b[0]);end=(end<<8)|b[0];
        }
        if(end!=0x0d0a0d0a)throw new IOException("Fixture header too large");
        return text.ToString();
    }
}
