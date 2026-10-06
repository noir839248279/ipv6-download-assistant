using System.Text.Json;

namespace IPv6Lens;
internal sealed class DownloadForm : Form
{
    DownloadRouter? router; NativeCapture? native;
    readonly Dictionary<string,(CheckBox Switch,Label Four,Label Six,Label Unknown,Label State)> cards=new();
    readonly Label network=Label("正在检查 IPv6 网络…",11), hint=Label("打开对应开关，再重新开始下载或刷新视频。",10);
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    bool busy,closing,closeReady,lossNotified;readonly bool testMode;Task startup=Task.CompletedTask;
    internal bool DefaultsActive=>router!=null&&(testMode||native?.Active==true)&&!router.ProtectionLost&&cards.Values.All(c=>c.Switch.Checked)&&router.Counters.Values.All(c=>c.Enabled);
    public DownloadForm(bool testMode=false){
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        this.testMode=testMode;
        Text="IPv6 下载助手 3.0.4";Width=1220;Height=940;MinimumSize=new(1120,860);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(242,246,250);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),RowCount=DownloadRouter.Sites.Length+4,ColumnCount=1};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.Absolute,52));root.RowStyles.Add(new(SizeType.Absolute,34));for(int i=0;i<DownloadRouter.Sites.Length;i++)root.RowStyles.Add(new(SizeType.Percent,100f/DownloadRouter.Sites.Length));root.RowStyles.Add(new(SizeType.Absolute,44));root.RowStyles.Add(new(SizeType.Absolute,76));
        root.Controls.Add(Label("IPv6 下载助手",25));root.Controls.Add(network);
        foreach(var site in DownloadRouter.Sites){
            var card=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,BackColor=Color.White,Padding=new Padding(12),Margin=new Padding(0,4,0,4)};
            card.RowStyles.Add(new(SizeType.Percent,100));
            card.ColumnStyles.Add(new(SizeType.Absolute,180));card.ColumnStyles.Add(new(SizeType.Percent,20));card.ColumnStyles.Add(new(SizeType.Percent,20));card.ColumnStyles.Add(new(SizeType.Percent,20));card.ColumnStyles.Add(new(SizeType.Percent,40));
            var left=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,Margin=new Padding(0)};left.RowStyles.Add(new(SizeType.Absolute,30));left.RowStyles.Add(new(SizeType.Percent,100));left.Controls.Add(Label(site.Name,12));
            var toggle=new CheckBox{Text="仅 IPv6：关",Appearance=Appearance.Button,TextAlign=ContentAlignment.MiddleCenter,Dock=DockStyle.Fill,Margin=new Padding(3,1,8,1),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(236,241,245)};
            left.Controls.Add(toggle);card.Controls.Add(left);var v4=Label("IPv4  0 B/s\n累计 0 B",11);var v6=Label("IPv6  0 B/s\n累计 0 B",11);v6.ForeColor=Color.FromArgb(0,125,98);var unknown=Label("未知  0 B/s\n累计 0 B",11);unknown.ForeColor=Color.FromArgb(151,98,0);unknown.AccessibleDescription="经原代理转发，外部连接使用IPv4或IPv6无法确认。";var state=Label("原连接方式\n开启后禁止 IPv4 回落",10);card.Controls.Add(v4);card.Controls.Add(v6);card.Controls.Add(unknown);card.Controls.Add(state);root.Controls.Add(card);cards[site.Id]=(toggle,v4,v6,unknown,state);
            toggle.CheckedChanged+=async(_,_)=>await Switch(site.Id,toggle.Checked);
        }
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};root.Controls.Add(actions);
        void Button(string title,Action run){var b=new Button{Text=title,AutoSize=true,Height=32,FlatStyle=FlatStyle.Flat,BackColor=Color.White};b.Click+=(_,_)=>run();actions.Controls.Add(b);}
        Button("全部开启",async()=>await SetAll(true));Button("全部关闭",async()=>await SetAll(false));
        Button("清零流量",()=>router?.Reset());Button("导出报告",Export);
        Button("详细诊断",()=>{using var detail=new MainForm();detail.ShowDialog(this);});
        Button("使用说明",()=>MessageBox.Show("默认全部开启：普通网站优先直连 IPv6，失败后允许 IPv4／原代理；内容下载仅允许 IPv6，不可达则失败。\n\n夸克已知登录接口归入其他网站。Bilibili/YouTube 只按视频 CDN 计数，不把整个浏览器算进去。Steam/Epic 使用独立 Mihomo 内核接管 HTTP/HTTPS；登录也可能受限制。第三方代理内的目的流量无法可靠还原。\n\n开关是策略，不代表当前已下载。以实际速度、累计与主服务器判断；代理出口协议未知的字节不算 IPv6。\n\n请暂停旧下载再启动新下载。关闭开关、退出或接管失效后可能恢复 IPv4。本工具不是整机 IPv4 防火墙。\n\n退出自动保存脱敏报告，不记录 URL、Cookie、Token、文件名或普通网页域名。",Text));
        hint.Text="网站可回退 IPv4；已接管下载不可回退。关闭／退出前请暂停下载，避免恢复原路径后消耗额度。\n数字为助手接收字节；代理出口未知单独显示。已有连接、未知 CDN 和第三方代理可能不受接管。";root.Controls.Add(hint);
        timer.Tick+=(_,_)=>RefreshCards();
        Shown+=(_,_)=>{timer.Start();startup=Initialize();};
        FormClosing+=async(_,e)=>{if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;timer.Stop();await startup;try{SaveAutomaticReport();await StopRouting();}catch(Exception ex){MessageBox.Show("恢复设置未完成："+ex.Message);closing=false;timer.Start();return;}closeReady=true;Close();};
    }
    void RefreshCards(){
            if(busy||closing)return;
            if(native!=null&&!native.Active)router?.MarkCaptureLost();router?.Sample();if(router==null)return;
            foreach(var (id,c) in cards){var r=router.Counters[id];c.Four.Text=$"IPv4  {TrafficStore.Bytes(r.Rate4)}/s\n累计 {TrafficStore.Bytes(r.V4)}";c.Six.Text=$"IPv6  {TrafficStore.Bytes(r.Rate6)}/s\n累计 {TrafficStore.Bytes(r.V6)}";c.Unknown.Text=$"未知  {TrafficStore.Bytes(r.RateProxy)}/s\n累计 {TrafficStore.Bytes(r.Proxy)}";long known=r.V4+r.V6;string share=r.ShareText;
                string route=r.RateProxy>1024&&r.RateProxy>=Math.Max(r.Rate4,r.Rate6)?"当前主要经原代理（出口未知）":r.Rate4+r.Rate6>1024?(known<65536?"少量数据，下载待确认":r.Rate6>=r.Rate4?"当前主要 IPv6":"当前主要 IPv4"):r.State=="正在使用 IPv6"?"当前空闲；上次连接 IPv6":r.State;
                c.State.Text=route+" · "+share+"\n"+(id=="global"?"未知：原代理出口协议未确认":"主服务器："+router.MainServer(id));
                if(router.ProtectionLost){c.Switch.Text=id=="global"?"原路径：可尝试":"失效：未保护";c.Switch.BackColor=id=="global"?Color.PapayaWhip:Color.MistyRose;c.State.ForeColor=Color.DarkRed;c.State.Text=(id=="global"?"普通网页仍尝试原代理／直连":"已接管下载拒绝请求，请暂停下载")+"\n"+share;}
            }
            if(router.ProtectionLost&&!lossNotified){lossNotified=true;SaveAutomaticReport();hint.Text="接管失效：普通网页仍尝试原路径；已接管下载拒绝请求。请暂停下载，旁路可能消耗 IPv4。";hint.ForeColor=Color.DarkRed;
                if(!testMode)MessageBox.Show(this,"网络接管已失效！\n\n助手或网络内核已失去接管，无法阻止应用绕过助手继续使用 IPv4。请立即在夸克／浏览器中暂停下载。\n\n暂停后排除其他代理软件冲突，再全部关闭、重新开启。", "IPv4 流量风险",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        
    }
    internal void SimulateOwnershipLoss(){if(router!=null){router.OwnershipProbe=()=>false;RefreshCards();}}
    async Task Initialize(){await SetAll(true);if(testMode){network.Text="本机界面测试：不进行公网探测";return;}if(closing)return;try{var result=await Diagnostics.Network();if(!closing)network.Text=result.Verdict;}catch{if(!closing)network.Text="IPv6 检测未完成；开启后以实际连接为准";}}
    void PaintSwitch(string id,bool on){var c=cards[id];c.State.ForeColor=Color.FromArgb(28,48,64);c.Switch.Checked=on;c.Switch.Text=(id=="global"?"IPv6 优先":"仅 IPv6 下载")+(on?"：开":"：关");c.Switch.BackColor=on?Color.FromArgb(192,237,220):Color.FromArgb(236,241,245);}
    async Task SetAll(bool on){if(busy||closing)return;if(on&&router?.ProtectionLost==true){hint.Text="请先暂停下载，全部关闭后再重新开启。";return;}busy=true;foreach(var c in cards.Values)c.Switch.Enabled=false;try{
        if(router==null&&on)await StartRouting();
        if(closing)return;
        if(router!=null){if(on)router.EnableAll();else {await StopRouting();lossNotified=false;}}
        foreach(var id in cards.Keys){PaintSwitch(id,on);if(!on)cards[id].State.Text="未保护：已允许 IPv4／原代理";}
        hint.ForeColor=Color.FromArgb(28,48,64);
        hint.Text=on?"已开启：网站 IPv6 优先；已接管下载仅 IPv6。请暂停后重启下载，已有连接不保证迁移。":"全部已关闭，允许 IPv4／原代理。请暂停下载以免继续消耗额度。";
    }catch(Exception ex){await StopRouting();foreach(var id in cards.Keys)PaintSwitch(id,false);hint.Text="未开启："+ex.Message;}finally{busy=false;if(!closing)foreach(var c in cards.Values)c.Switch.Enabled=true;}}
    static Label Label(string text,int size)=>new(){Text=text,Font=new Font("Microsoft YaHei UI",size),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Color.FromArgb(28,48,64)};
    async Task Switch(string id,bool on){
        if(busy)return;if(router?.ProtectionLost==true){busy=true;cards[id].Switch.Checked=router.Counters[id].Enabled;busy=false;hint.Text="失效未保护：先暂停下载，再全部关闭并重新开启。";return;}busy=true;foreach(var c in cards.Values)c.Switch.Enabled=false;
        try{
            if(router==null&&on)await StartRouting(id);
            if(closing)return;
            router?.Enable(id,on);PaintSwitch(id,on);
            hint.Text=on?"已开启仅 IPv6：接管的请求禁止回落；已有直连需重新建立连接，数字仅统计助手流量。":"已允许该服务使用 IPv4／原代理，可能消耗额度。";
        }catch(Exception ex){await StopRouting();foreach(var key in cards.Keys)PaintSwitch(key,false);cards[id].State.Text="未开启："+ex.Message;hint.Text=ex.Message;}
        finally{busy=false;if(!closing)foreach(var c in cards.Values)c.Switch.Enabled=true;}
    }
    async Task StartRouting(string? selected=null){
        router=new DownloadRouter{ProductionPolicies=true};
        await router.Start(!testMode);
        foreach(var pair in router.Counters)router.Enable(pair.Key,selected==null||pair.Key==selected);
        if(!testMode){native=new NativeCapture();await native.Start(router);}
    }
    async Task StopRouting(){
        if(native!=null){await native.DisposeAsync();native=null;}
        if(router!=null){await router.DisposeAsync();router=null;}
    }
    object Report()=>new{schema="ipv6-download-assistant/3",version="3.0.4",timeUtc=DateTime.UtcNow,
        protectionLost=router?.ProtectionLost,nativeCapture=native?.Active??false,network=network.Text,
        nativeExitCode=native?.ExitCode,routerHealth=router?.Health(),
        scope="Received relay bytes, not campus billing. Enabled content is IPv6-only; ordinary websites may fall back. Existing/bypass/proxied/unknown traffic may be outside capture. Proxy exit family unknown.",
        services=router?.Counters.Select(p=>new{id=p.Key,enabled=p.Value.Enabled,ipv4=Interlocked.Read(ref p.Value.V4),ipv6=Interlocked.Read(ref p.Value.V6),proxyUnknown=Interlocked.Read(ref p.Value.Proxy),ipv4RateBps=p.Value.Rate4,ipv6RateBps=p.Value.Rate6,proxyUnknownRateBps=p.Value.RateProxy,failures=Interlocked.Read(ref p.Value.Failures),state=p.Value.State}).ToArray(),servers=router?.ServerReport()};
    void WriteReport(string path)=>File.WriteAllText(path,JsonSerializer.Serialize(Report(),new JsonSerializerOptions{WriteIndented=true}));
    void SaveAutomaticReport(){if(testMode||router==null)return;try{string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","reports");Directory.CreateDirectory(dir);WriteReport(Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"));}catch{}}
    void Export(){using var save=new SaveFileDialog{Filter="JSON 诊断报告|*.json",FileName="IPv6-download-report.json"};if(save.ShowDialog()!=DialogResult.OK)return;try{WriteReport(save.FileName);hint.Text="报告已导出：包含平台 CDN 域名与流量，不含 URL、账号、文件名或普通网站域名。";}catch(Exception ex){hint.Text="导出失败："+ex.GetType().Name;}}
}
