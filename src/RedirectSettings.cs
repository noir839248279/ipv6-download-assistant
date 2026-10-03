using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IPv6Lens;
internal sealed class RedirectSettings : IDisposable
{
    const string Key=@"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal static string Journal=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","redirect-lease.json");
    internal record Lease(int Pid,long Birth,int Flags,string? Previous,string Owned);
    Process? guard;
    public string Proxy {get;private set;}="";
    public string Bypass {get;private set;}="";
    public string? Owned {get;private set;}
    [StructLayout(LayoutKind.Explicit,Size=16)] struct Option { [FieldOffset(0)] public int Id;[FieldOffset(8)] public IntPtr Value; }
    [StructLayout(LayoutKind.Sequential)] struct Options {public int Size;public IntPtr Connection;public int Count,Error;public IntPtr Items;}
    [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool InternetSetOptionW(IntPtr handle,int option,ref Options buffer,int length);
    [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool InternetQueryOptionW(IntPtr handle,int option,ref Options buffer,ref int length);
    [DllImport("wininet.dll",EntryPoint="InternetSetOptionW")] static extern bool Notify(IntPtr handle,int option,IntPtr buffer,int length);
    static int Flags(){var mem=Marshal.AllocHGlobal(16);try{Marshal.StructureToPtr(new Option{Id=1},mem,false);var o=new Options{Size=Marshal.SizeOf<Options>(),Count=1,Items=mem};int size=o.Size;if(!InternetQueryOptionW(IntPtr.Zero,75,ref o,ref size))throw new System.ComponentModel.Win32Exception();return Marshal.PtrToStructure<Option>(mem).Value.ToInt32();}finally{Marshal.FreeHGlobal(mem);}}
    static void Set(int flags,string? url){var mem=Marshal.AllocHGlobal(32);var value=Marshal.StringToHGlobalUni(url??"");try{Marshal.StructureToPtr(new Option{Id=1,Value=new IntPtr(flags)},mem,false);Marshal.StructureToPtr(new Option{Id=4,Value=value},mem+16,false);var o=new Options{Size=Marshal.SizeOf<Options>(),Count=2,Items=mem};if(!InternetSetOptionW(IntPtr.Zero,75,ref o,o.Size))throw new System.ComponentModel.Win32Exception();Notify(IntPtr.Zero,39,IntPtr.Zero,0);Notify(IntPtr.Zero,37,IntPtr.Zero,0);}finally{Marshal.FreeHGlobal(value);Marshal.FreeHGlobal(mem);}}
    internal static bool OwnerAlive(Lease lease){try{using var p=Process.GetProcessById(lease.Pid);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==lease.Birth;}catch{return false;}}
    public void Prepare(){
        if(File.Exists(Journal)){var lease=JsonSerializer.Deserialize<Lease>(File.ReadAllText(Journal))!;if(OwnerAlive(lease))throw new InvalidOperationException("另一个 IPv6 下载窗口正在使用开关，请先关闭它。");Recover();}
        using var key=Registry.CurrentUser.OpenSubKey(Key)!;
        if(!string.IsNullOrWhiteSpace(key.GetValue("AutoConfigURL") as string))throw new InvalidOperationException("已有其他 PAC 分流设置，未覆盖。请先在原软件中关闭 PAC 再使用开关。");
        Proxy=Convert.ToInt32(key.GetValue("ProxyEnable",0))==1?key.GetValue("ProxyServer") as string??"":"";
        Bypass=key.GetValue("ProxyOverride") as string??"";
    }
    public void Apply(string url){
        if(Owned!=null){using var check=Registry.CurrentUser.OpenSubKey(Key);if(!StillOwned)throw new InvalidOperationException("代理设置已被其他软件更改，请关闭开关后重试。");return;}
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        using var key=Registry.CurrentUser.OpenSubKey(Key)!;
        var lease=new Lease(Environment.ProcessId,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,Flags(),key.GetValue("AutoConfigURL") as string,url);
        File.WriteAllText(Journal,JsonSerializer.Serialize(lease));
        using(var run=Registry.CurrentUser.CreateSubKey(RunKey))run.SetValue("IPv6LensRestore","\""+Environment.ProcessPath+"\" --redirect-recover");
        var si=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};si.ArgumentList.Add("--redirect-watch");si.ArgumentList.Add(Environment.ProcessId.ToString());si.ArgumentList.Add(lease.Birth.ToString());
        try{guard=Process.Start(si)??throw new Exception("恢复进程无法启动");Set(4,url);Owned=url;}catch{Recover();throw;}
    }
    public bool StillOwned {get {using var k=Registry.CurrentUser.OpenSubKey(Key);return Owned!=null&&Equals(k?.GetValue("AutoConfigURL"),Owned)&&(Flags()&4)!=0;}}
    internal static void Recover(int? expectedPid=null,long? expectedBirth=null){
        using var mutex=new Mutex(false,@"Local\IPv6Lens-RedirectRecovery");try{mutex.WaitOne();}catch(AbandonedMutexException){}
        try{
            if(expectedPid!=null&&!File.Exists(Journal))return;
            if(File.Exists(Journal)){
                var lease=JsonSerializer.Deserialize<Lease>(File.ReadAllText(Journal))!;
                if(expectedPid!=null&&(lease.Pid!=expectedPid||lease.Birth!=expectedBirth))return;
                using var key=Registry.CurrentUser.OpenSubKey(Key,true)!;
                if(Equals(key.GetValue("AutoConfigURL"),lease.Owned)){Set(lease.Flags,lease.Previous);if(lease.Previous==null)key.DeleteValue("AutoConfigURL",false);}
                File.Delete(Journal);
            }
            using var run=Registry.CurrentUser.OpenSubKey(RunKey,true);run?.DeleteValue("IPv6LensRestore",false);
        }finally{mutex.ReleaseMutex();}
    }
    internal static void Watch(int pid,long birth){try{using var p=Process.GetProcessById(pid);if(p.StartTime.ToUniversalTime().Ticks==birth)p.WaitForExit();}catch(ArgumentException){}Recover(pid,birth);}
    public void Dispose(){if(Owned!=null){Recover();Owned=null;}if(guard!=null){try{if(!guard.HasExited)guard.Kill();}catch{}guard.Dispose();guard=null;}}
}
