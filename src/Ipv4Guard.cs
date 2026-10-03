using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace IPv6Lens;
public static class Ipv4Guard
{
    [StructLayout(LayoutKind.Sequential)] internal struct Display { public IntPtr Name, Description; }
    [StructLayout(LayoutKind.Sequential)] internal struct Session { public Guid Key; public Display Display; public uint Flags,Timeout,Pid; public IntPtr Sid,User; public int Kernel; }
    [StructLayout(LayoutKind.Explicit,Size=16)] internal struct Value { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public IntPtr Pointer; }
    [StructLayout(LayoutKind.Sequential)] internal struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct Condition { public Guid Key; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] internal struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit,Size=16)] internal struct Context { [FieldOffset(0)] public ulong Raw; [FieldOffset(0)] public Guid Key; }
    [StructLayout(LayoutKind.Sequential)] internal struct Filter {
        public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public Guid Layer,SubLayer;
        public Value Weight; public uint Count; public IntPtr Conditions; public Action Action; public Context Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] static extern uint FwpmEngineOpen0(string? server,uint auth,IntPtr identity,ref Session session,out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string name,out IntPtr blob);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(IntPtr engine,ref Filter filter,IntPtr sd,out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine,uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
    static void Check(uint result) { if(result!=0) throw new InvalidOperationException($"WFP 错误 0x{result:X8}"); }
    public static bool Allowed(string path)
    {
        if(!File.Exists(path) || !Path.IsPathFullyQualified(path)) return false;
        var full=Path.GetFullPath(path); var name=Path.GetFileNameWithoutExtension(full);
        if(full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return false;
        if(full.Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return false;
        return !new[]{"clash","mihomo","verge","sing-box","v2ray","xray","tailscale","wireguard","openvpn","ipv6lens","svchost","system"}.Any(x=>name.Contains(x,StringComparison.OrdinalIgnoreCase));
    }
    public static void Run(string[] args)
    {
        // No filter is installed until the parent identity and private pipe are verified.
        if(args.Length!=7 || !Program.Admin) return;
        IntPtr engine=IntPtr.Zero,blob=IntPtr.Zero,condition=IntPtr.Zero,name=IntPtr.Zero;
        NamedPipeClientStream? pipe=null;
        try {
            int parentId=int.Parse(args[1]); long birth=long.Parse(args[2]); int seconds=Math.Clamp(int.Parse(args[4]),1,120);
            string path=Encoding.UTF8.GetString(Convert.FromBase64String(args[5]));
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=birth || !Allowed(path) || args[6]!="v1") return;
            pipe=new NamedPipeClientStream(".",args[3],PipeDirection.InOut,PipeOptions.Asynchronous);
            pipe.Connect(10000);
            var session=new Session { Key=Guid.NewGuid(),Flags=1 };
            Check(FwpmEngineOpen0(null,10,IntPtr.Zero,ref session,out engine));
            Check(FwpmGetAppIdFromFileName0(path,out blob));
            var app=new Condition { Key=new("d78e1e87-8644-4ea5-9437-d809ecefc971"),Value=new Value { Type=12,Pointer=blob } };
            condition=Marshal.AllocHGlobal(Marshal.SizeOf<Condition>()); Marshal.StructureToPtr(app,condition,false);
            name=Marshal.StringToHGlobalUni("IPv6 Lens temporary IPv4 test");
            Check(FwpmTransactionBegin0(engine,0));
            foreach(var layer in new[]{"c38d57d1-05a7-4c33-904f-7fbceee60e82","e1cd9fe7-f4b5-4273-96c0-592e487b8650"}) {
                var filter=new Filter { Key=Guid.NewGuid(),Display=new Display {Name=name},Layer=new(layer),SubLayer=new("eebecc03-ced4-4380-819a-2734397b2b74"),Count=1,Conditions=condition,Action=new Action{Type=0x1001} };
                Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out _));
            }
            Check(FwpmTransactionCommit0(engine));
            pipe.WriteByte(1); pipe.Flush();
            // EOF also ends the lease. A crash of this helper causes Windows to delete the dynamic session.
            Task.WhenAny(parent.WaitForExitAsync(),Task.Delay(TimeSpan.FromSeconds(seconds)),pipe.ReadAsync(new byte[1],0,1)).GetAwaiter().GetResult();
        } catch { try { pipe?.WriteByte(0); } catch { } }
        finally {
            if(engine!=IntPtr.Zero) FwpmEngineClose0(engine);
            if(blob!=IntPtr.Zero) FwpmFreeMemory0(ref blob);
            if(condition!=IntPtr.Zero) Marshal.FreeHGlobal(condition);
            if(name!=IntPtr.Zero) Marshal.FreeHGlobal(name);
            pipe?.Dispose();
        }
    }
}
public sealed class TestLease : IDisposable
{
    NamedPipeServerStream? pipe;
    Process? helper;
    public DateTime Expires {get;private set;}
    internal int HelperPid => helper?.Id ?? throw new InvalidOperationException("No active guard");
    public bool Active => helper!=null && !helper.HasExited && DateTime.UtcNow<Expires;
    public async Task Start(string path,int seconds)
    {
        if(!Program.Admin || !Ipv4Guard.Allowed(path)) throw new InvalidOperationException("需要管理员模式及可验证的普通应用路径；系统与常见代理程序不能作为测试目标。");
        Dispose(); string id="IPv6Lens-"+Guid.NewGuid().ToString("N");
        pipe=new NamedPipeServerStream(id,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var si=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true};
        foreach(var a in new[]{"--ipv4-guard",Environment.ProcessId.ToString(),Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(),id,Math.Clamp(seconds,1,120).ToString(),Convert.ToBase64String(Encoding.UTF8.GetBytes(path)),"v1"}) si.ArgumentList.Add(a);
        helper=Process.Start(si);
        try {
            using var timeout=new CancellationTokenSource(15000); await pipe.WaitForConnectionAsync(timeout.Token);
            byte[] ready=new byte[1]; var n=await pipe.ReadAsync(ready,timeout.Token);
            if(n!=1 || ready[0]!=1) throw new InvalidOperationException("Windows 未接受临时过滤器，测试没有开始。");
            Expires=DateTime.UtcNow.AddSeconds(Math.Clamp(seconds,1,120));
        } catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        pipe?.Dispose(); pipe=null;
        if(helper!=null) { try { if(!helper.WaitForExit(1500)) { helper.Kill(); helper.WaitForExit(1500); } } catch { } helper.Dispose(); helper=null; }
        Expires=DateTime.MinValue;
    }
}
