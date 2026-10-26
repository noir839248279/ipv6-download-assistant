using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class ProxyBypassTests
{
    internal static void Probe(int proxyPort,int originPort,string report)
    {
        try
        {
            using var http=new HttpClient(new HttpClientHandler { Proxy=new WebProxy("http://127.0.0.1:"+proxyPort),UseProxy=true })
                { Timeout=TimeSpan.FromSeconds(5) };
            string body=http.GetStringAsync("http://drive-pc.quark.cn:"+originPort+"/").GetAwaiter().GetResult();
            File.WriteAllText(report,body=="original-path"?"original-path":"unexpected-body");
        }
        catch { File.WriteAllText(report,"blocked"); }
    }

    internal static void Run(string path)
    {
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new IOException(name);checks.Add(name);}
        string folder=Path.Combine(Path.GetTempPath(),"IPv6Lens-proxy-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Task.Run(async () =>
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var origin=new TcpListener(IPAddress.Loopback,0);
                origin.Start();
                int originPort=((IPEndPoint)origin.LocalEndpoint).Port;
                int originCount=0;
                var serving=Task.Run(async () =>
                {
                    try
                    {
                        while(!stop.IsCancellationRequested)
                        {
                            using var client=await origin.AcceptTcpClientAsync(stop.Token);
                            var stream=client.GetStream();
                            byte[] b=new byte[1];int end=0;
                            while(end!=0x0d0a0d0a){if(await stream.ReadAsync(b,stop.Token)==0)break;end=(end<<8)|b[0];}
                            Interlocked.Increment(ref originCount);
                            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\noriginal-path"u8.ToArray(),stop.Token);
                        }
                    }
                    catch(OperationCanceledException){}
                });
                // The renamed fixture provides real Windows process metadata to Mihomo.
                string helper=Path.Combine(folder,"verge-mihomo.exe");
                File.Copy(Environment.ProcessPath!,helper);
                await using var router=new DownloadRouter { ProductionPolicies=true };
                await router.Start(false);
                router.Enable("quark",true);router.SetMode("quark",ServiceMode.Mixed);
                router.OriginalResolve=_=>throw new IOException("Fixture detected proxy recapture");
                foreach(bool legacy in new[]{true,false})
                {
                    using var listener=new TcpListener(IPAddress.Loopback,0);
                    listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                    await using var core=new NativeCapture();
                    await core.Start(router,false,config=>
                    {
                        config["mixed-port"]=port;config["bind-address"]="127.0.0.1";
                        config["log-level"]="debug";
                        config["dns"]=new Dictionary<string,object>{{"enable",false}};
                        config["sniffer"]=new Dictionary<string,object>{{"enable",false}};
                        config["hosts"]=new Dictionary<string,object>{{"drive-pc.quark.cn","127.0.0.1"}};
                        // Local origins stand in for public endpoints; remove private-IP
                        // bypasses so only actual process-rule ordering determines the path.
                        var rules=((string[])config["rules"]).Where(r=>!r.StartsWith("IP-CIDR",StringComparison.Ordinal)).ToList();
                        if(legacy)
                        {
                            var auxiliary=rules.Where(r=>r.StartsWith("DOMAIN,",StringComparison.Ordinal)).ToArray();
                            rules.RemoveAll(r=>r.StartsWith("DOMAIN,",StringComparison.Ordinal));
                            rules.InsertRange(3,auxiliary);
                        }
                        config["rules"]=rules.ToArray();
                    });
                    long failures=router.Counters.Values.Sum(c=>c.Failures);
                    string result=Path.Combine(folder,legacy?"before.txt":"after.txt");
                    var si=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};
                    foreach(string arg in new[]{"--proxy-bypass-probe",port.ToString(),originPort.ToString(),result})si.ArgumentList.Add(arg);
                    using var process=Process.Start(si)??throw new IOException("Fixture failed to start");
                    try { await process.WaitForExitAsync(stop.Token); }
                    finally { if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();} }
                    string body=File.ReadAllText(result);
                    Check(legacy ? body=="blocked"&&router.Counters.Values.Sum(c=>c.Failures)>failures :
                        body=="original-path"&&router.Counters.Values.Sum(c=>c.Failures)==failures,
                        legacy?"Real core reproduces original-proxy recapture under 3.3.0 ordering":
                            "Real core bypasses the original proxy process under corrected ordering");
                }
                Check(originCount==1,"Only corrected proxy process reaches local origin; no public traffic or TUN");
                stop.Cancel();await serving;
            }).GetAwaiter().GetResult();
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="passed",checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex)
        {
            Environment.ExitCode=1;
            File.WriteAllText(path,JsonSerializer.Serialize(new{state="failed",checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            // Only files created by this fixture, never recurse over a computed root.
            foreach(string name in new[]{"verge-mihomo.exe","before.txt","after.txt"})
                try{File.Delete(Path.Combine(folder,name));}catch{}
            try{Directory.Delete(folder);}catch{}
        }
    }
}
