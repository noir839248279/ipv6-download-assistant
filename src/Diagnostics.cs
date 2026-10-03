using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace IPv6Lens;
public record Probe(string Host,string Family,bool Success,string Detail);
public record NetworkReport(DateTime Time,int GlobalV6,int V4,List<string> Interfaces,List<Probe> Probes)
{
    public string Verdict {
        get {
            bool a=Probes.Any(x=>x.Family=="IPv4" && x.Success), b=Probes.Any(x=>x.Family=="IPv6" && x.Success);
            return a&&b?"IPv4 / IPv6 双栈直连验证成功":b?"IPv6 直连验证成功；IPv4 探测未成功":a?"IPv4 正常；IPv6 直连探测未成功":"直连探测未成功，不能仅凭此判定整个网络故障";
        }
    }
}
public static class Diagnostics
{
    public static async Task<Probe> ProbeHost(string host,AddressFamily family,int port=443)
    {
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try {
            var ips=await Dns.GetHostAddressesAsync(host,cts.Token);
            var addresses=ips.Where(ip=>ip.AddressFamily==family).Take(4).ToArray();
            if(addresses.Length==0) return new(host,family==AddressFamily.InterNetworkV6?"IPv6":"IPv4",false,"DNS 无对应地址记录");
            foreach(var ip in addresses) {
                try {
                    using var perAddress=CancellationTokenSource.CreateLinkedTokenSource(cts.Token); perAddress.CancelAfter(2000);
                    if(port!=443 || IPAddress.TryParse(host,out _)) {
                        using var client=new TcpClient(family); await client.ConnectAsync(ip,port,perAddress.Token);
                        return new(host,family==AddressFamily.InterNetworkV6?"IPv6":"IPv4",true,$"TCP {port} 可达；未验证 TLS 或业务下载");
                    }
                    using var handler=new SocketsHttpHandler { UseProxy=false, ConnectCallback=async (_,cancel)=> {
                        var socket=new Socket(family,SocketType.Stream,ProtocolType.Tcp);
                        try { await socket.ConnectAsync(new IPEndPoint(ip,port),cancel); return new NetworkStream(socket,true); }
                        catch { socket.Dispose(); throw; }
                    }};
                    using var http=new HttpClient(handler); using var req=new HttpRequestMessage(HttpMethod.Head,new UriBuilder("https",host,port).Uri);
                    using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,perAddress.Token);
                    return new(host,family==AddressFamily.InterNetworkV6?"IPv6":"IPv4",true,$"直连 TLS / HTTP 成功（{(int)response.StatusCode}）；未验证文件访问权限");
                } catch(Exception ex) when(ex is SocketException or HttpRequestException or OperationCanceledException) { }
            }
            return new(host,family==AddressFamily.InterNetworkV6?"IPv6":"IPv4",false,$"DNS 有对应地址（本次尝试 {addresses.Length} 个）；连接／TLS 失败或超时。不等于不支持 IPv6");
        } catch(Exception ex) { return new(host,family==AddressFamily.InterNetworkV6?"IPv6":"IPv4",false,"探测未完成："+ex.GetType().Name); }
    }
    public static async Task<NetworkReport> Network()
    {
        var adapters=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).ToArray();
        int v6=0,v4=0; var rows=new List<string>();
        foreach(var n in adapters) {
            var ips=n.GetIPProperties().UnicastAddresses.Select(x=>x.Address).ToArray();
            if(ips.Length==0)continue;
            int a=ips.Count(x=>x.AddressFamily==AddressFamily.InterNetworkV6 && TrafficStore.Scope(x)=="公网");
            int b=ips.Count(x=>x.AddressFamily==AddressFamily.InterNetwork); v6+=a; v4+=b;
            rows.Add($"{n.NetworkInterfaceType}：IPv4 地址 {b} 个，全球单播 IPv6 地址 {a} 个");
        }
        var tests=await Task.WhenAll(new[]{"www.bilibili.com","www.cloudflare.com"}.SelectMany(h=>new[]{AddressFamily.InterNetwork,AddressFamily.InterNetworkV6}.Select(f=>ProbeHost(h,f))));
        return new(DateTime.UtcNow,v6,v4,rows,tests.ToList());
    }
    public static async Task<Dictionary<string,string[]>> DnsCandidates(IEnumerable<string> remotes)
    {
        var wanted=remotes.ToHashSet(); var found=new Dictionary<string,List<string>>();
        var si=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8 };
        si.ArgumentList.Add("-NoProfile"); si.ArgumentList.Add("-NonInteractive"); si.ArgumentList.Add("-Command");
        si.ArgumentList.Add("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); @(Get-DnsClientCache -ErrorAction Stop | Where-Object { $_.Type -eq 1 -or $_.Type -eq 28 } | Select-Object Entry,Data) | ConvertTo-Json -Compress");
        using var p=Process.Start(si)!;
        var read=p.StandardOutput.ReadToEndAsync(); var err=p.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(6000);
        try { await p.WaitForExitAsync(timeout.Token); } catch { p.Kill(); throw new TimeoutException("DNS 缓存读取超时"); }
        var text=await read; await err;
        if(p.ExitCode!=0) throw new InvalidOperationException("DNS 缓存不可用（系统策略或 DNS 服务）");
        if(string.IsNullOrWhiteSpace(text)) return new();
        using var doc=JsonDocument.Parse(text);
        foreach(var row in doc.RootElement.EnumerateArray()) {
            var ip=row.GetProperty("Data").GetString()??""; var host=row.GetProperty("Entry").GetString()??"";
            if(!wanted.Contains(ip) || Uri.CheckHostName(host)!=UriHostNameType.Dns) continue;
            if(!found.TryGetValue(ip,out var names)) found[ip]=names=new();
            if(names.Count<8 && !names.Contains(host)) names.Add(host);
        }
        return found.ToDictionary(x=>x.Key,x=>x.Value.ToArray());
    }
}
