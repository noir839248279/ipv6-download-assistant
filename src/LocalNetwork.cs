using System.Net;
using System.Net.Sockets;

namespace IPv6Lens;

internal static class LocalNetwork
{
    internal static bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
              bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : address.AddressFamily == AddressFamily.InterNetworkV6 &&
              ((bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal);
    }

    // Pure address checks: never resolve a hostname inside the address predicate.
    internal const string PacFunctions = """
        function lensLocalAddress(ip){
          if(!ip)return false;
          ip=ip.toLowerCase().replace(/^\[|\]$/g,'');
          if(ip.indexOf('::ffff:')===0)return lensLocalAddress(ip.substring(7));
          if(ip.indexOf(':')>=0)return ip==='::1'||ip==='0:0:0:0:0:0:0:1'||/^(fc|fd)[0-9a-f]{2}:/.test(ip)||/^fe[89ab][0-9a-f]:/.test(ip);
          var p=ip.split('.');
          if(p.length!==4)return false;
          for(var i=0;i<4;i++)if(!/^[0-9]{1,3}$/.test(p[i])||Number(p[i])>255)return false;
          var a=Number(p[0]),b=Number(p[1]);
          return a===127||a===10||(a===172&&b>=16&&b<=31)||(a===192&&b===168)||(a===169&&b===254);
        }
        function lensLocalHost(host){
          if(lensLocalAddress(host))return true;
          var ips=null;
          try{
            if(typeof dnsResolveEx==='function')ips=dnsResolveEx(host);
            else if(typeof dnsResolve==='function')ips=dnsResolve(host);
          }catch(e){return false;}
          if(!ips)return false;
          var list=ips.split(';');
          for(var i=0;i<list.length;i++)if(!lensLocalAddress(list[i]))return false;
          return list.length>0;
        }
        """;
}
