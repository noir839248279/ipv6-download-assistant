using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;
internal sealed record SplitRoute(int Index,string Guid,string Alias,string Family,int Metric,bool Automatic,int Applied);
internal static class SplitRoutes
{
    internal static string Run(string script, object data)
    {
        string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));
        var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        string prelude="$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new();$data=ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+encoded+"')));";
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude+script))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("无法启动网卡设置组件。");
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(20000)){try{process.Kill(true);}catch{}throw new TimeoutException("网卡操作超时；恢复记录已保留。");}
        string text=output.GetAwaiter().GetResult();_=errors.GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new InvalidOperationException("Windows 未能完成网卡操作，请确认以管理员身份运行且网卡仍可用。恢复记录已保留。");
        return text;
    }
    internal static SplitRoute[] Capture(ClashNetworkSnapshot network)
    {
        if(!network.WifiConnected||!network.EthernetConnected)throw new InvalidOperationException("请连接并启用 Wi-Fi 和以太网；软件会自动调整有线优先级。");
        if(!network.EthernetIpv4Metric.HasValue||!network.EthernetIpv6Metric.HasValue)throw new InvalidOperationException("以太网尚未获得完整的 IPv4/IPv6 默认路由，请完成校园网认证。");
        if(!network.WifiIpv4Metric.HasValue&&!network.WifiIpv6Metric.HasValue)throw new InvalidOperationException("Wi-Fi 没有外网默认路由。");
        string output=Run("""
            $result=@(foreach($family in @('IPv4','IPv6')) {
              $wired=Get-NetAdapter -Name $data.Ethernet -Physical
              $wifi=Get-NetAdapter -Name $data.Wifi -Physical
              $route=@(Get-NetRoute -InterfaceIndex $wired.ifIndex -AddressFamily $family -PolicyStore ActiveStore | Where-Object DestinationPrefix -in @('0.0.0.0/0','::/0'))
              if($route.Count -eq 0){throw 'missing wired route'}
              $wirelessMetric=[int](($route|Measure-Object RouteMetric -Maximum).Maximum)+100
              if($wirelessMetric -gt 9999){throw 'unsupported route metric'}
              foreach($adapter in @($wired,$wifi)){
                if($adapter.Status -ne 'Up'){throw 'adapter offline'}
                $i=Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily $family -PolicyStore ActiveStore
                [pscustomobject]@{Index=[int]$adapter.ifIndex;Guid=[string]$adapter.InterfaceGuid;Alias=$adapter.Name;Family=$family;Metric=[int]$i.InterfaceMetric;Automatic=([string]$i.AutomaticMetric -eq 'Enabled');Applied=$(if($adapter.ifIndex -eq $wired.ifIndex){5}else{$wirelessMetric})}
              }
            })
            ConvertTo-Json -InputObject $result -Compress
            """,new{Ethernet=network.EthernetName,Wifi=network.WifiName});
        return JsonSerializer.Deserialize<SplitRoute[]>(output)??throw new InvalidOperationException("无法读取原网卡设置。");
    }
    internal static void Apply(SplitRoute[] routes) => Run("""
        foreach($r in $data){
          $adapter=Get-NetAdapter | Where-Object ifIndex -eq $r.Index | Select-Object -First 1
          if([string]$adapter.InterfaceGuid -ne $r.Guid -or $adapter.Status -ne 'Up'){throw 'adapter changed'}
          Set-NetIPInterface -InterfaceIndex $r.Index -AddressFamily $r.Family -PolicyStore ActiveStore -AutomaticMetric Disabled -InterfaceMetric $r.Applied
        }
        """,routes);
    internal static void Restore(SplitRoute[] routes) => Run("""
        $failed=$false
        foreach($r in $data){try{
          $adapter=Get-NetAdapter | Where-Object ifIndex -eq $r.Index | Select-Object -First 1 -ErrorAction Stop
          if([string]$adapter.InterfaceGuid -ne $r.Guid){continue}
          $now=Get-NetIPInterface -InterfaceIndex $r.Index -AddressFamily $r.Family -PolicyStore ActiveStore
          # Only restore settings still owned by this transaction; preserve subsequent user changes.
          if([string]$now.AutomaticMetric -eq 'Disabled' -and [int]$now.InterfaceMetric -eq [int]$r.Applied){
            Set-NetIPInterface -InterfaceIndex $r.Index -AddressFamily $r.Family -PolicyStore ActiveStore -AutomaticMetric Disabled -InterfaceMetric $r.Metric
            if($r.Automatic){Set-NetIPInterface -InterfaceIndex $r.Index -AddressFamily $r.Family -PolicyStore ActiveStore -AutomaticMetric Enabled}
          }
        }catch{$failed=$true}}
        if($failed){throw 'restore incomplete'}
        """,routes);
}
