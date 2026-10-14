using System.Net;
using System.Text.Json;

namespace IPv6Lens;

internal static class ClashInterfaceTests
{
    sealed class ApiState
    {
        public string InterfaceName = "";
        public int Patches;
        public string? Authorization;
        public string? LastPayload;
        public bool IgnorePatch;
        public bool MissingField;
    }

    sealed class ApiHandler(ApiState state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            state.Authorization = request.Headers.Authorization?.ToString();
            if (request.Method == HttpMethod.Get)
                return Json(state.MissingField ? new Dictionary<string,string>() : new Dictionary<string, string> { ["interface-name"] = state.InterfaceName });
            if (request.Method.Method == "PATCH")
            {
                state.LastPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(state.LastPayload);
                if(!state.IgnorePatch)state.InterfaceName = document.RootElement.GetProperty("interface-name").GetString() ?? "";
                state.Patches++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };
    }

    internal static void Run(string output)
    {
        var passed = new List<string>();
        var wifi = new AdapterSnapshot("WLAN", "Native 802.11", true);
        var ethernet = new AdapterSnapshot("以太网", "802.3", true);
        var preferred = ClashNetworkProbe.Evaluate(
            new[] { wifi, ethernet },
            new[]
            {
                new DefaultRouteSnapshot("WLAN", "IPv4", 35),
                new DefaultRouteSnapshot("以太网", "IPv4", 20),
                new DefaultRouteSnapshot("WLAN", "IPv6", 51),
                new DefaultRouteSnapshot("以太网", "IPv6", 30)
            });
        Require(preferred.CanEnable, "wired-first dual-stack routes should allow the split");
        passed.Add("wired_first_dual_stack_routes_allow_split");

        var wifiOnly = ClashNetworkProbe.Evaluate(
            new[] { wifi, ethernet with { IsUp = false } },
            new[] { new DefaultRouteSnapshot("WLAN", "IPv4", 35), new DefaultRouteSnapshot("WLAN", "IPv6", 51) });
        Require(!wifiOnly.CanEnable && wifiOnly.EnableBlockReason.Contains("以太网", StringComparison.Ordinal), "disabled Ethernet should block the split");
        passed.Add("disabled_ethernet_blocks_split");

        var reversed = ClashNetworkProbe.Evaluate(
            new[] { wifi, ethernet },
            new[]
            {
                new DefaultRouteSnapshot("WLAN", "IPv4", 20),
                new DefaultRouteSnapshot("以太网", "IPv4", 35),
                new DefaultRouteSnapshot("WLAN", "IPv6", 51),
                new DefaultRouteSnapshot("以太网", "IPv6", 30)
            });
        Require(!reversed.CanEnable && reversed.EnableBlockReason.Contains("优先", StringComparison.Ordinal), "wireless-first IPv4 should block the split");
        passed.Add("wireless_first_route_blocks_split");

        var state = new ApiState();
        var settings = new ClashControllerSettings(new Uri("http://127.0.0.1:9097/"), "local-test-secret");
        var controller = new ClashInterfaceBinding(() => settings, () => new ApiHandler(state), () => preferred);
        var initial = controller.ReadAsync().GetAwaiter().GetResult();
        Require(!initial.IsWifiBound && initial.InterfaceName.Length == 0, "empty interface-name should represent the default state");
        passed.Add("startup_reads_default_state");

        controller.SetWifiAsync().GetAwaiter().GetResult();
        Require(state.InterfaceName == "WLAN" && state.LastPayload == "{\"interface-name\":\"WLAN\"}", "enable should PATCH the exact Wi-Fi alias");
        Require(state.Authorization == "Bearer local-test-secret" && state.Patches == 1, "controller requests should authenticate");
        passed.Add("enable_patches_and_verifies_wifi");

        controller.RestoreDefaultAsync().GetAwaiter().GetResult();
        Require(state.InterfaceName.Length == 0 && state.LastPayload == "{\"interface-name\":\"\"}" && state.Patches == 2, "restore should clear interface-name");
        passed.Add("restore_patches_and_verifies_default");

        var unauthorized = false;
        var failed = new ClashInterfaceBinding(
            () => settings,
            () => new StaticHandler(HttpStatusCode.Unauthorized),
            () => preferred);
        try { failed.ReadAsync().GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { unauthorized = ex.Message.Contains("鉴权失败", StringComparison.Ordinal) && !ex.Message.Contains("secret", StringComparison.OrdinalIgnoreCase); }
        Require(unauthorized, "unauthorized API errors should be friendly and must not reveal credentials");
        passed.Add("api_auth_errors_are_sanitized");

        var blocked = new ClashInterfaceBinding(() => settings, () => new ApiHandler(state), () => wifiOnly);
        try { blocked.SetWifiAsync().GetAwaiter().GetResult(); throw new Exception("Expected route guard rejection"); }
        catch(InvalidOperationException) { Require(state.Patches == 2, "Invalid routes must not PATCH"); }
        passed.Add("route_guard_makes_no_mutation");
        state.InterfaceName="WLAN";
        blocked.RestoreDefaultAsync().GetAwaiter().GetResult();
        Require(state.InterfaceName == "", "Restore must work even with Ethernet disconnected");
        passed.Add("restore_works_when_ethernet_offline");
        state.IgnorePatch=true;
        try { controller.SetWifiAsync().GetAwaiter().GetResult(); throw new Exception("Expected readback mismatch"); }
        catch(InvalidOperationException) { passed.Add("http_success_without_effect_is_rejected"); }
        state.MissingField=true;
        try { controller.ReadAsync().GetAwaiter().GetResult(); throw new Exception("Expected missing field rejection"); }
        catch(InvalidOperationException) { passed.Add("missing_field_is_unknown_not_default"); }
        Require(!new ClashBindingSnapshot("",wifiOnly with { WifiName="" }).IsWifiBound, "No Wi-Fi must not appear bound");
        passed.Add("missing_wifi_is_not_enabled");
        Require(!(preferred with { EthernetIpv6Metric=null }).CanEnable, "No wired IPv6 default must block");
        Require(!(preferred with { EthernetIpv4Metric=35 }).CanEnable, "Equal route metrics must block ambiguous routing");
        passed.Add("missing_ipv6_and_equal_metrics_block");

        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = passed }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Probe(string output) => Task.Run(async()=>{
        var snapshot=await new ClashInterfaceBinding().ReadAsync();
        File.WriteAllText(output,JsonSerializer.Serialize(new {snapshot.InterfaceName,snapshot.IsWifiBound,snapshot.Network},new JsonSerializerOptions{WriteIndented=true}));
    }).GetAwaiter().GetResult();

    internal static void CoreTest(string output) => Task.Run(async()=>{
        string folder=Path.Combine(Path.GetTempPath(),"IPv6Lens-clash-api-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string config=Path.Combine(folder,"config.json");
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);
        listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string secret=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(config,JsonSerializer.Serialize(new Dictionary<string,object>{
            ["external-controller"]="127.0.0.1:"+port,["secret"]=secret,["mode"]="rule",["log-level"]="silent",
            ["ipv6"]=true,["rules"]=new[]{"MATCH,DIRECT"},["dns"]=new{enable=false},["tun"]=new{enable=false}
        }));
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"core","mihomo.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var argument in new[]{"-d",folder,"-f",config})start.ArgumentList.Add(argument);
        System.Diagnostics.Process? process=null;
        try {
            process=System.Diagnostics.Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            // Synthetic routes exercise API behavior without altering the user's adapters.
            var routes=new ClashNetworkSnapshot("WLAN","Ethernet",true,true,35,51,20,30);
            var service=new ClashInterfaceBinding(()=>new(new Uri($"http://127.0.0.1:{port}/"),secret),()=>new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false},()=>routes);
            bool ready=false;
            for(int i=0;i<25&&!process.HasExited;i++){try{await service.ReadAsync();ready=true;break;}catch(HttpRequestException){await Task.Delay(200);}}
            Require(ready,"Isolated Mihomo failed to start");
            await service.SetWifiAsync();Require((await service.ReadAsync()).IsWifiBound,"Real PATCH WLAN readback");
            await service.RestoreDefaultAsync();Require((await service.ReadAsync()).IsDefault,"Real PATCH empty readback");
            File.WriteAllText(output,JsonSerializer.Serialize(new{passed=true,scope="Isolated Mihomo API roundtrip; no TUN, no Windows route or user Clash changes",checks=new[]{"read_config","patch_wifi_readback","clear_interface_readback"}}));
            process.Kill(true);await process.WaitForExitAsync();await stdout;await stderr;
        } finally {
            if(process!=null){if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();}
            File.Delete(config);
        }
    }).GetAwaiter().GetResult();

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
