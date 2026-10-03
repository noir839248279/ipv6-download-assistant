using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;

namespace IPv6Lens;
public record AppProcess(int Pid, int Parent, string Name, string Path, long Birth)
{
    public override string ToString() => $"{Name}  ·  PID {Pid}";
}
public static class Processes
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Entry {
        public uint Size, Usage, Pid; public IntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h,ref Entry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32NextW(IntPtr h,ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static List<AppProcess> List()
    {
        var result=new List<AppProcess>(); var h=CreateToolhelp32Snapshot(2,0);
        if(h==new IntPtr(-1)) return result;
        try {
            var e=new Entry {Size=(uint)Marshal.SizeOf<Entry>(),Exe=""};
            if(Process32FirstW(h,ref e)) do {
                if(e.Pid<=4) continue;
                string path=""; long birth=0; try { using var p=Process.GetProcessById((int)e.Pid); birth=p.StartTime.ToUniversalTime().Ticks; path=p.MainModule?.FileName??""; } catch { }
                result.Add(new((int)e.Pid,(int)e.Parent,e.Exe,path,birth));
            } while(Process32NextW(h,ref e));
        } finally { CloseHandle(h); }
        return result.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Pid).ToList();
    }
    public static HashSet<int> Group(AppProcess selected,List<AppProcess> all)
    {
        var ids=all.Where(p=>(p.Pid==selected.Pid && p.Birth==selected.Birth) || (selected.Path.Length>0 && p.Path.Equals(selected.Path,StringComparison.OrdinalIgnoreCase))).Select(p=>p.Pid).ToHashSet();
        var births=all.ToDictionary(p=>p.Pid,p=>p.Birth);
        bool changed; do { changed=false; foreach(var p in all) if(ids.Contains(p.Parent) && births.TryGetValue(p.Parent,out var birth) && p.Birth>=birth) changed|=ids.Add(p.Pid); } while(changed);
        return ids;
    }
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int cls,uint reserved);
    public record Connection(int Pid,string Local,int LocalPort,string Remote,int RemotePort,string State);
    internal static int? ClientOwner(IPEndPoint peer,IPEndPoint server)
    {
        // Reverse the accepted connection's full tuple; never attribute by source port alone.
        return Connections().FirstOrDefault(c=>c.LocalPort==peer.Port&&c.RemotePort==server.Port&&
            IPAddress.Parse(c.Local).Equals(peer.Address)&&IPAddress.Parse(c.Remote).Equals(server.Address)&&c.State=="已建立")?.Pid;
    }
    internal static string? ProxyClientSite(System.Net.Sockets.TcpClient client)
    {
        try{
            if(client.Client.RemoteEndPoint is not IPEndPoint peer||client.Client.LocalEndPoint is not IPEndPoint server||!IPAddress.IsLoopback(peer.Address))return null;
            int? pid=ClientOwner(peer,server);if(pid==null)return null;
            using var p=Process.GetProcessById(pid.Value);
            return p.ProcessName.ToLowerInvariant() switch {"steam" or "steamservice" or "steamwebhelper"=>"steam","epicgameslauncher" or "epicwebhelper"=>"epic",_=>null};
        }catch(System.ComponentModel.Win32Exception){return null;}catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
    }
    public static List<Connection> Connections()
    {
        var result=new List<Connection>();
        foreach(var family in new[]{2,23}) {
            int size=0; GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,5,0); if(size<=0) continue;
            var p=Marshal.AllocHGlobal(size);
            try {
                if(GetExtendedTcpTable(p,ref size,false,family,5,0)!=0) continue;
                var count=Marshal.ReadInt32(p); int rowSize=family==2?24:56;
                for(int i=0;i<count;i++) {
                    var r=IntPtr.Add(p,4+i*rowSize);
                    byte[] Read(int offset,int n) { var b=new byte[n]; Marshal.Copy(IntPtr.Add(r,offset),b,0,n); return b; }
                    int Port(int offset) { var b=Read(offset,2); return b[0]*256+b[1]; }
                    var local=family==2?new IPAddress(Read(4,4)):new IPAddress(Read(0,16),unchecked((uint)Marshal.ReadInt32(r,16)));
                    var remote=family==2?new IPAddress(Read(12,4)):new IPAddress(Read(24,16),unchecked((uint)Marshal.ReadInt32(r,40)));
                    int state=Marshal.ReadInt32(r,family==2?0:48);
                    result.Add(new(Marshal.ReadInt32(r,family==2?20:52),local.ToString(),Port(family==2?8:20),remote.ToString(),Port(family==2?16:44),state==5?"已建立":state==2?"监听":$"TCP 状态 {state}"));
                }
            } finally { Marshal.FreeHGlobal(p); }
        }
        return result;
    }
}
