using System.Net;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace IPv6Lens;

internal static class SplitConfig
{
    internal static YamlMappingNode Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("Clash 配置必须是单份 YAML 映射。");
        return root;
    }
    internal static YamlNode? Get(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    internal static string Text(YamlMappingNode map, string key) => (Get(map,key) as YamlScalarNode)?.Value ?? "";
    internal static void Set(YamlMappingNode map, string key, string value) => map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
    internal static string Dump(YamlMappingNode root) { var writer=new StringWriter(); new YamlStream(new YamlDocument(root)).Save(writer, false); return writer.ToString(); }

    // Preserve live choices that can differ from the generated file. Never export this configuration.
    internal static string WithLiveSettings(string yaml, string liveJson)
    {
        var root=Parse(yaml); var live=Parse(liveJson);
        foreach(var key in new[]{"mode","ipv6","log-level","interface-name","allow-lan","bind-address","port","socks-port","mixed-port","redir-port","tproxy-port","sniffing","tcp-concurrent"})
            if(Get(live,key) is {} value) root.Children[new YamlScalarNode(key)]=value;
        if(Get(live,"tun") is YamlMappingNode liveTun) {
            var tun=Get(root,"tun") as YamlMappingNode ?? new YamlMappingNode();
            foreach(var key in new[]{"enable","device","stack","auto-route","auto-detect-interface","strict-route"})
                if(Get(liveTun,key) is {} value)tun.Children[new YamlScalarNode(key)]=value;
            root.Children[new YamlScalarNode("tun")]=tun;
        }
        return Dump(root);
    }

    internal static string Build(string yaml, string wifi, string ethernet, string marker)
    {
        if(string.IsNullOrWhiteSpace(wifi)||string.IsNullOrWhiteSpace(ethernet)||wifi==ethernet)throw new InvalidOperationException("需要两张不同的有线和无线网卡。");
        var root=Parse(yaml); Set(root,"interface-name",ethernet);
        var tun=Get(root,"tun") as YamlMappingNode ?? new YamlMappingNode();
        Set(tun,"enable","false");root.Children[new YamlScalarNode("tun")]=tun;
        var proxies=Get(root,"proxies") as YamlSequenceNode ?? new YamlSequenceNode();
        int remote=0;
        foreach(var item in proxies.Children) {
            if(item is not YamlMappingNode proxy)throw new InvalidOperationException("代理节点配置格式无法识别，未修改。");
            var type=Text(proxy,"type").ToLowerInvariant();
            if(type is "direct" or "dns") Set(proxy,"interface-name",ethernet);
            else if(type is not ("reject" or "reject-drop" or "pass")) {
                var server=Text(proxy,"server");
                if(server.Equals("localhost",StringComparison.OrdinalIgnoreCase)||(IPAddress.TryParse(server.Trim('[',']'),out var ip)&&IPAddress.IsLoopback(ip)))
                    throw new InvalidOperationException("订阅包含本地二级代理，不能保证其外部出口走 Wi-Fi；未更改配置。");
                Set(proxy,"interface-name",wifi); remote++;
            }
        }
        if(proxies.Children.OfType<YamlMappingNode>().Any(p=>Text(p,"name")==marker))throw new InvalidOperationException("分流标记与现有节点重名。");
        proxies.Add(new YamlMappingNode {{"name",marker},{"type","direct"},{"interface-name",ethernet}});
        root.Children[new YamlScalarNode("proxies")]=proxies;
        if(Get(root,"proxy-providers") is YamlMappingNode providers) foreach(var entry in providers.Children) {
            if(entry.Value is not YamlMappingNode provider)throw new InvalidOperationException("代理提供器配置格式无法识别。");
            if(Get(provider,"payload") is YamlSequenceNode payload && payload.Children.OfType<YamlMappingNode>().Any(p=>Text(p,"type").Equals("direct",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("代理提供器混入直连节点，无法安全统一绑定 Wi-Fi。");
            var overrides=Get(provider,"override") as YamlMappingNode ?? new YamlMappingNode();
            Set(overrides,"interface-name",wifi); provider.Children[new YamlScalarNode("override")]=overrides;remote++;
        }
        if(remote==0)throw new InvalidOperationException("当前配置没有可绑定的代理节点。");
        return Dump(root);
    }
}
