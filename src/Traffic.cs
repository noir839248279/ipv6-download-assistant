using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace IPv6Lens;
public record FlowKey(int Pid, string Transport, string Local, int LocalPort, string Remote, int RemotePort);
public record FlowView(FlowKey Key, long Received, long Sent, double Rate, DateTime LastSeen, string Scope)
{
    public string Family => TrafficStore.Family(IPAddress.Parse(Key.Remote));
    public string Role => Received >= 1024 * 1024 || Rate >= 128 * 1024 ? "大流量候选（按字节推断）" : "小流量／用途未知";
}
public record Summary(long V4, long V6, double Rate4, double Rate6, long Local, long Other)
{
    public double? Share => V4 + V6 == 0 ? null : 100.0 * V6 / (V4 + V6);
    public string Verdict => Rate4 + Rate6 < 1024 ? "当前没有明显的公网下载流量" : Rate6 / (Rate4 + Rate6) >= .8 ? "当前大流量主要经过 IPv6" : Rate4 / (Rate4 + Rate6) >= .8 ? "当前大流量主要经过 IPv4" : "当前 IPv4 / IPv6 混合传输";
}
public sealed class TrafficStore
{
    sealed class Counter { public long Rx, Tx, Previous; public double Rate; public DateTime Seen; }
    readonly object gate = new();
    readonly Dictionary<FlowKey, Counter> flows = new();
    readonly HashSet<int> pids = new();
    long last = Stopwatch.GetTimestamp();
    public long Dropped { get; private set; }
    internal TimeSpan? RetainIdle;
    public void Select(IEnumerable<int> ids) { lock(gate) { pids.Clear(); pids.UnionWith(ids); flows.Clear(); Dropped = 0; last = Stopwatch.GetTimestamp(); } }
    public void AddPid(int pid) { lock(gate) pids.Add(pid); }
    public void RemovePid(int pid) { lock(gate) pids.Remove(pid); }
    internal void ForgetPid(int pid) { lock (gate) { pids.Remove(pid); foreach (var key in flows.Keys.Where(k => k.Pid == pid).ToArray()) flows.Remove(key); } }
    public bool Contains(int pid) { lock(gate) return pids.Contains(pid); }
    public void Add(int pid, string transport, IPAddress source, int sport, IPAddress dest, int dport, int bytes, bool receive)
    {
        if (bytes <= 0) return;
        lock(gate)
        {
            if (!pids.Contains(pid)) return;
            var key = receive ? new FlowKey(pid,transport,dest.ToString(),dport,source.ToString(),sport) : new FlowKey(pid,transport,source.ToString(),sport,dest.ToString(),dport);
            if (!flows.TryGetValue(key, out var c)) {
                if (flows.Count >= 20000) { Dropped++; return; }
                flows[key] = c = new();
            }
            if (receive) c.Rx += bytes; else c.Tx += bytes;
            c.Seen = DateTime.UtcNow;
        }
    }
    public List<FlowView> Sample(bool sorted = true)
    {
        lock(gate)
        {
            var now = Stopwatch.GetTimestamp(); var seconds = (now-last)/(double)Stopwatch.Frequency; last=now;
            if (RetainIdle is TimeSpan idle)
                foreach (var key in flows.Where(p => p.Value.Seen < DateTime.UtcNow - idle).Select(p => p.Key).ToArray()) flows.Remove(key);
            foreach(var c in flows.Values) { c.Rate = (c.Rx-c.Previous)/Math.Max(.001,seconds); c.Previous=c.Rx; }
            var result = flows.Select(x=>new FlowView(x.Key,x.Value.Rx,x.Value.Tx,x.Value.Rate,x.Value.Seen,Scope(IPAddress.Parse(x.Key.Remote))));
            return sorted ? result.OrderByDescending(x=>x.Rate).ThenByDescending(x=>x.Received).ToList() : result.ToList();
        }
    }
    public static string Scope(IPAddress ip)
    {
        if(ip.IsIPv4MappedToIPv6) ip=ip.MapToIPv4();
        if(IPAddress.IsLoopback(ip)) return "本机／代理候选";
        var b=ip.GetAddressBytes();
        if(ip.AddressFamily==AddressFamily.InterNetworkV6) return (b[0]&0xe0)==0x20 ? "公网" : "局域网／隧道／特殊";
        return b[0] is 0 or 10 or 127 || b[0]>=224 || (b[0]==169 && b[1]==254) || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168) || (b[0]==100 && b[1]>=64 && b[1]<=127) || (b[0]==198 && b[1] is 18 or 19) ? "局域网／隧道／特殊" : "公网";
    }
    public static string Family(IPAddress ip) => ip.IsIPv4MappedToIPv6 || ip.AddressFamily==AddressFamily.InterNetwork ? "IPv4" : "IPv6";
    public static Summary Sum(IEnumerable<FlowView> input)
    {
        var f=input.ToArray(); var v4=f.Where(x=>x.Scope=="公网" && x.Family=="IPv4").ToArray(); var v6=f.Where(x=>x.Scope=="公网" && x.Family=="IPv6").ToArray();
        return new(v4.Sum(x=>x.Received),v6.Sum(x=>x.Received),v4.Sum(x=>x.Rate),v6.Sum(x=>x.Rate),f.Where(x=>x.Scope=="本机／代理候选").Sum(x=>x.Received),f.Where(x=>x.Scope=="局域网／隧道／特殊").Sum(x=>x.Received));
    }
    public static string Bytes(double b) => b>=1e9 ? $"{b/1e9:F2} GB" : b>=1e6 ? $"{b/1e6:F2} MB" : b>=1e3 ? $"{b/1e3:F1} KB" : $"{b:F0} B";
}
public sealed class EtwCollector : IDisposable
{
    readonly TrafficStore store;
    TraceEventSession? session;
    Process? watchdog;
    readonly string role;
    internal Func<int, string, bool>? ProcessStarted;
    internal Action<int>? ProcessStopped;
    internal Action<int, string, string, uint>? DnsObserved;
    internal string DnsStatus { get; private set; } = "未启用进程 DNS 关联";
    public string Status { get; private set; } = "未启动";
    public long Lost => session?.EventsLost ?? 0;
    public EtwCollector(TrafficStore store,string role="") { this.store=store;this.role=role; }
    public void Start()
    {
        if(!Program.Admin) { Status="未采集：请点击「管理员模式」授权真实流量监控"; return; }
        try {
            foreach(var name in TraceEventSession.GetActiveSessionNames().Where(n=>n.StartsWith("IPv6Lens-"))) {
                if(!int.TryParse(name[9..].Split('-')[0],out int owner))continue;
                try{using var process=Process.GetProcessById(owner);if(!process.HasExited)continue;}catch(ArgumentException){}
                try{using var orphan=new TraceEventSession(name,TraceEventSessionOptions.Attach);orphan.Stop();}catch{}
            }
            session=new TraceEventSession("IPv6Lens-"+Environment.ProcessId+(role.Length>0?"-"+role:"")) { StopOnDispose=true, BufferSizeMB=64 };
            // TraceEvent must enable the kernel provider before Source is materialized.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            if (DnsObserved != null)
            {
                try
                {
                    session.EnableProvider("Microsoft-Windows-DNS-Client", Microsoft.Diagnostics.Tracing.TraceEventLevel.Informational);
                    session.Source.Dynamic.All += d =>
                    {
                        if ((int)d.ID != 3008 || d.ProviderName != "Microsoft-Windows-DNS-Client" || !store.Contains(d.ProcessID)) return;
                        try
                        {
                            DnsObserved(d.ProcessID, d.PayloadByName("QueryName")?.ToString() ?? "",
                                d.PayloadByName("QueryResults")?.ToString() ?? "",
                                Convert.ToUInt32(d.PayloadByName("QueryStatus")));
                        }
                        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidCastException)
                        { DnsStatus = "DNS 事件格式不可用；浏览器 / AList 统计可能缺失"; }
                    };
                    DnsStatus = "进程 DNS 关联已启用（共享地址 / 加密 DNS 可能漏计）";
                }
                catch (Exception ex) { DnsStatus = "DNS 关联不可用：" + ex.GetType().Name; }
            }
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            foreach(var a in new[]{"--trace-watch",Environment.ProcessId.ToString(),Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString()})start.ArgumentList.Add(a);
            if(role.Length>0)start.ArgumentList.Add(role);
            watchdog=Process.Start(start);
            var k=session.Source.Kernel;
            // TCP receive fields are local -> remote, unlike incoming UDP datagrams.
            k.TcpIpRecv += d=>store.Add(d.ProcessID,"TCP",d.daddr,d.dport,d.saddr,d.sport,d.size,true);
            k.TcpIpSend += d=>store.Add(d.ProcessID,"TCP",d.saddr,d.sport,d.daddr,d.dport,d.size,false);
            k.TcpIpRecvIPV6 += d=>store.Add(d.ProcessID,"TCP",d.daddr,d.dport,d.saddr,d.sport,d.size,true);
            k.TcpIpSendIPV6 += d=>store.Add(d.ProcessID,"TCP",d.saddr,d.sport,d.daddr,d.dport,d.size,false);
            k.UdpIpRecv += d=>store.Add(d.ProcessID,"UDP",d.saddr,d.sport,d.daddr,d.dport,d.size,true);
            k.UdpIpSend += d=>store.Add(d.ProcessID,"UDP",d.saddr,d.sport,d.daddr,d.dport,d.size,false);
            k.UdpIpRecvIPV6 += d=>store.Add(d.ProcessID,"UDP",d.saddr,d.sport,d.daddr,d.dport,d.size,true);
            k.UdpIpSendIPV6 += d=>store.Add(d.ProcessID,"UDP",d.saddr,d.sport,d.daddr,d.dport,d.size,false);
            k.ProcessStart += d=> {
                if (ProcessStarted != null) { if (ProcessStarted(d.ProcessID, d.ImageFileName)) store.AddPid(d.ProcessID); }
                else if(store.Contains(d.ParentID)) store.AddPid(d.ProcessID);
            };
            k.ProcessStop += d=> { store.RemovePid(d.ProcessID); ProcessStopped?.Invoke(d.ProcessID); };
            Status="正在采集 TCP + UDP（含 QUIC），统计从本次监控开始";
            var source=session.Source;
            Task.Run(()=> { try { source.Process(); } catch(Exception ex) { Status="采集停止："+ex.GetType().Name; } });
        } catch(Exception ex) { Status="采集不可用："+ex.Message; Dispose(); }
    }
    public void Dispose() { session?.Dispose(); session=null;if(watchdog!=null){try{if(!watchdog.HasExited)watchdog.Kill();}catch{}watchdog.Dispose();watchdog=null;} }
    internal static void Watch(int pid,long birth,string role="")
    {
        if(!Program.Admin)return;
        try{using var parent=Process.GetProcessById(pid);if(parent.StartTime.ToUniversalTime().Ticks!=birth)return;parent.WaitForExit();}catch(ArgumentException){}
        try{using var orphan=new TraceEventSession("IPv6Lens-"+pid+(role.Length>0?"-"+role:""),TraceEventSessionOptions.Attach);orphan.Stop();}catch{}
    }
}
