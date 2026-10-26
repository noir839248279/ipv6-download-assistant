using System.Diagnostics;
using System.Security.Principal;

namespace IPv6Lens;
internal static class Program
{
    public static bool Admin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    [STAThread] static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--routing-recovery-test"){RoutingRecoveryTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--node-test"){NodeTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--node-live-test"){NodeTests.Live(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-egress-test"){NativeEgressTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--relay-policy-test"){RelayPolicyTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--mixed-policy-test"){MixedPolicyTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--proxy-bypass-test"){ProxyBypassTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-pipeline-test"){NativePipelineTests.Run(args[1]);return;}
        if(args.Length==4&&args[0]=="--proxy-bypass-probe"){ProxyBypassTests.Probe(int.Parse(args[1]),int.Parse(args[2]),args[3]);return;}
        if(args.Length==2&&args[0]=="--relay-resilience-test"){RelayResilienceTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-delivery-test"){string prefix=args[1];NativeTests.Suite(prefix+".suite.json");if(Environment.ExitCode!=0)return;args=new[]{"--ui-native-test",prefix+".png"};}
        if(args.Length==2&&args[0]=="--platform-probes"){PlatformProbes.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-config-test"){NativeTests.ConfigTest(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-suite"){NativeTests.Suite(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-owner"){NativeTests.Owner(args[1]);return;}
        if(args.Length==2&&args[0]=="--native-core-test"){NativeTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--redirect-public-test"){RedirectTests.PublicProbe(args[1]);return;}
        if(args.Length==2&&args[0]=="--redirect-test"){RedirectTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--redirect-lease-test"){RedirectTests.LeaseTest(args[1]);return;}
        if(args.Length==2&&args[0]=="--redirect-owner"){RedirectTests.Owner(args[1]);return;}
        if(args.Length==4&&args[0]=="--redirect-live"){RedirectTests.Live(args[1],args[2],int.Parse(args[3]));return;}
        if(args.Length==1&&args[0]=="--redirect-recover"){RedirectSettings.Recover();return;}
        if(args.Length==3&&args[0]=="--redirect-watch"){RedirectSettings.Watch(int.Parse(args[1]),long.Parse(args[2]));return;}
        if (args.Length > 0 && args[0] == "--self-test") { SelfTest.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.Length == 3 && args[0] == "--integration-test") { IntegrationTests.Run(args[1],args[2]); return; }
        if (args.Length == 3 && args[0] == "--lease-owner") { IntegrationTests.LeaseOwner(args[1],args[2]); return; }
        if (args.Length == 3 && args[0] == "--browser-test-backend") { IntegrationTests.BrowserBackend(args[1],args[2]); return; }
        if (args.Length == 2 && args[0] == "--network-check") { File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(Diagnostics.Network().GetAwaiter().GetResult(),new System.Text.Json.JsonSerializerOptions{WriteIndented=true})); return; }
        if (args.Length == 3 && args[0] == "--trace-watch") { EtwCollector.Watch(int.Parse(args[1]),long.Parse(args[2])); return; }
        if (args.Length == 4 && args[0] == "--trace-watch" && args[3]=="passive") { EtwCollector.Watch(int.Parse(args[1]),long.Parse(args[2]),args[3]); return; }
        if (args.Length == 2 && args[0] == "--trace-owner") { using var c=new EtwCollector(new TrafficStore());c.Start();File.WriteAllText(args[1],c.Status);Console.ReadLine();return; }
        if (args.Length == 4 && args[0] == "--observe-process") { IntegrationTests.Observe(args[1],args[2],int.Parse(args[3]));return; }
        if (args.Length == 2 && args[0] == "--stop-test-process") { IntegrationTests.StopTestProcess(int.Parse(args[1]));return; }
        if (args.Length == 2 && args[0] == "--clash-interface-test") { ClashInterfaceTests.Run(args[1]);return; }
        if (args.Length == 2 && args[0] == "--clash-interface-probe") { ClashInterfaceTests.Probe(args[1]);return; }
        if (args.Length == 2 && args[0] == "--clash-interface-core-test") { ClashInterfaceTests.CoreTest(args[1]);return; }
        if (args.Length > 0 && args[0] == "--ipv4-guard") { Ipv4Guard.Run(args); return; }
        if(args.Length==3&&args[0]=="--split-watch"){SplitRouting.Watch(int.Parse(args[1]),long.Parse(args[2]));return;}
        if(args.Length==2&&args[0]=="--split-test"){SplitRoutingTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--split-live-test"){SplitRoutingTests.Live(args[1]);return;}
        if(args.Length==2&&args[0]=="--split-owner-test"){SplitRoutingTests.Owner(args[1]);return;}
        ApplicationConfiguration.Initialize();
        if(args.Length==2&&args[0]=="--traffic-share-test"){TrafficShareTests.Run(args[1]);return;}
        if(args.Length==2&&args[0]=="--node-ui-test"){NodeForm.UiTest(args[1]);return;}
        bool dashboardTest=args.Length==2&&args[0]=="--dashboard-test";
        bool uiSmoke=dashboardTest||args.Length==2&&args[0] is "--ui-smoke" or "--ui-smoke-loss" or "--ui-smoke-split";
        bool uiNative=args.Length==2&&args[0]=="--ui-native-test";
        if(!uiSmoke&&args.Length==0&&!Admin){try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"});}catch{MessageBox.Show("未开启网络接管。Steam/Epic 接管需要管理员权限。");}return;}
        if(!uiSmoke){try{SplitRouting.RecoverStale();}catch(Exception ex){MessageBox.Show("上次分流恢复未完成："+ex.Message);return;}}
        var form=new DownloadForm(uiSmoke);
        if(dashboardTest){
            form.ShowInTaskbar=false;form.Opacity=0;
            form.Shown+=async(_,_)=>{
                try{
                    var result=await form.VerifyDashboard(args[1]);
                    File.WriteAllText(args[1]+".json",System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                }catch(Exception ex){
                    Environment.ExitCode=1;
                    File.WriteAllText(args[1]+".json",System.Text.Json.JsonSerializer.Serialize(new{state="failed",error=ex.ToString()}));
                }finally{form.Close();}
            };
            Application.Run(form);return;
        }
        if(uiSmoke||uiNative) {
            form.ShowInTaskbar=false;form.Opacity=0;
            var timer=new System.Windows.Forms.Timer {Interval=uiNative?12000:3500};
            timer.Tick+=(_,_)=> {timer.Stop();if(args[0]=="--ui-smoke-loss")form.SimulateOwnershipLoss();if(args[0]=="--ui-smoke-split")form.PreviewClashBinding();using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(args[1]);File.WriteAllText(args[1]+".json",System.Text.Json.JsonSerializer.Serialize(new{defaultsActive=form.DefaultsActive,splitActive=form.SplitActive,services=DownloadRouter.Sites.Length}));form.Close();timer.Dispose();};
            form.Shown+=(_,_)=>timer.Start();
        }
        Application.Run(form);
    }
}
