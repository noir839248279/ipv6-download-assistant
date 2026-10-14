using System.Text.Json;

namespace IPv6Lens;
internal sealed class DownloadForm : Form
{
    DownloadRouter? router; NativeCapture? native;
    readonly ClashInterfaceBinding clashBinding=new();
    sealed class TrafficColumn
    {
        public readonly Label[] Values;
        public TrafficColumn(Label[] values)=>Values=values;
        public void Set(double down,long received,double up,long sent){string[] texts={TrafficStore.Bytes(down)+"/s",TrafficStore.Bytes(received),TrafficStore.Bytes(up)+"/s",TrafficStore.Bytes(sent)};for(int i=0;i<4;i++)Values[i].Text=texts[i];}
    }
    readonly Dictionary<string,(CheckBox Switch,TrafficColumn Four,TrafficColumn Six,TrafficColumn Unknown,Label State)> cards=new();
    readonly Label network=Label("正在检查 IPv6 网络…",11), hint=Label("打开对应开关，再重新开始下载或刷新视频。",10);
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly System.Windows.Forms.Timer clashTimer=new(){Interval=10000};
    readonly CheckBox clashToggle=new(){Text="流量分流：正在读取 Clash 状态…",Appearance=Appearance.Button,AutoCheck=false,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,4,0),FlatStyle=FlatStyle.Flat,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",10)};
    readonly Label clashStatus=Label("启动 Clash Verge 后自动读取状态。",9);
    bool busy,closing,closeReady,lossNotified,clashBusy;readonly bool testMode;Task startup=Task.CompletedTask,clashWork=Task.CompletedTask;
    internal bool DefaultsActive=>router!=null&&(testMode||native?.Active==true)&&!router.ProtectionLost&&cards.Values.All(c=>c.Switch.Checked)&&router.Counters.Values.All(c=>c.Enabled);
    public DownloadForm(bool testMode=false){
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        this.testMode=testMode;
        Text="IPv6 下载助手 3.0.7";Width=1240;Height=1020;MinimumSize=new(1100,940);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(242,246,250);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),RowCount=6,ColumnCount=1};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.Absolute,52));root.RowStyles.Add(new(SizeType.Absolute,34));root.RowStyles.Add(new(SizeType.Absolute,42));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.Absolute,44));root.RowStyles.Add(new(SizeType.Absolute,60));
        root.Controls.Add(Label("IPv6 下载助手",25));root.Controls.Add(network);
        var clashBar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};clashBar.ColumnStyles.Add(new(SizeType.Percent,48));clashBar.ColumnStyles.Add(new(SizeType.Percent,52));
        clashToggle.Dock=DockStyle.Fill;clashToggle.Margin=new Padding(0,2,10,2);clashBar.Controls.Add(clashToggle,0,0);
        clashStatus.Margin=Padding.Empty;clashStatus.Dock=DockStyle.Fill;clashBar.Controls.Add(clashStatus,1,0);root.Controls.Add(clashBar);
        var cardGrid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Margin=Padding.Empty};
        cardGrid.ColumnStyles.Add(new(SizeType.Percent,50));cardGrid.ColumnStyles.Add(new(SizeType.Percent,50));for(int i=0;i<3;i++)cardGrid.RowStyles.Add(new(SizeType.Percent,100f/3));root.Controls.Add(cardGrid);
        foreach(var site in DownloadRouter.Sites){
            var card=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=Color.White,Padding=new Padding(14,10,14,8),Margin=new Padding(5)};
            card.RowStyles.Add(new(SizeType.Absolute,36));card.RowStyles.Add(new(SizeType.Percent,100));card.RowStyles.Add(new(SizeType.Absolute,50));
            var heading=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};heading.ColumnStyles.Add(new(SizeType.Percent,100));heading.ColumnStyles.Add(new(SizeType.Absolute,160));heading.Controls.Add(Label(site.Name,14));
            var toggle=new CheckBox{Text="仅 IPv6：关",Appearance=Appearance.Button,TextAlign=ContentAlignment.MiddleCenter,Dock=DockStyle.Fill,Margin=new Padding(2),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(236,241,245)};
            heading.Controls.Add(toggle);card.Controls.Add(heading);
            var metrics=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=5,Margin=new Padding(0,6,0,4)};metrics.ColumnStyles.Add(new(SizeType.Absolute,92));for(int i=0;i<3;i++)metrics.ColumnStyles.Add(new(SizeType.Percent,100f/3));for(int i=0;i<5;i++)metrics.RowStyles.Add(new(SizeType.Percent,20));
            string[] rows={"","下载速度","累计下载","上传速度","累计上传"};for(int i=0;i<5;i++){var name=Label(rows[i],10);name.Margin=Padding.Empty;name.ForeColor=Color.FromArgb(102,115,128);metrics.Controls.Add(name,0,i);}
            var v4=MakeColumn(metrics,1,"IPv4",Color.FromArgb(28,48,64));var v6=MakeColumn(metrics,2,"IPv6",Color.FromArgb(0,125,98));var unknown=MakeColumn(metrics,3,"未知出口",Color.FromArgb(151,98,0));card.Controls.Add(metrics);
            var state=Label("原连接方式\n开启后禁止 IPv4 回落",9);state.Margin=new Padding(0,4,0,0);state.BackColor=Color.FromArgb(247,249,252);card.Controls.Add(state);cardGrid.Controls.Add(card);cards[site.Id]=(toggle,v4,v6,unknown,state);
            toggle.CheckedChanged+=async(_,_)=>await Switch(site.Id,toggle.Checked);
        }
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill};root.Controls.Add(actions);
        void Button(string title,Action run){var b=new Button{Text=title,AutoSize=true,Height=32,FlatStyle=FlatStyle.Flat,BackColor=Color.White};b.Click+=(_,_)=>run();actions.Controls.Add(b);}
        Button("全部开启",async()=>await SetAll(true));Button("全部关闭",async()=>await SetAll(false));
        Button("清零流量",()=>router?.Reset());Button("导出报告",Export);
        Button("详细诊断",()=>{using var detail=new MainForm();detail.ShowDialog(this);});
        Button("使用说明",()=>MessageBox.Show("默认全部开启：普通网站优先直连 IPv6，失败后允许 IPv4／原代理；内容下载仅允许 IPv6，不可达则失败。\n\n夸克已知登录接口归入其他网站。Bilibili/YouTube 只按视频 CDN 计数，不把整个浏览器算进去。Steam/Epic 使用独立 Mihomo 内核接管 HTTP/HTTPS；登录也可能受限制。第三方代理内的目的流量无法可靠还原。\n\n流量分流开关通过 Clash Verge 的 Mihomo 全局 interface-name 绑定网卡；Clash 内所有出站都受影响，包括规则命中的 DIRECT 直连请求。Clash 以外的流量及本助手自身仍遵循 Windows 默认路由。开启前会核实 Wi-Fi 已连接、以太网 IPv4/IPv6 默认路由优先。\n\n开关是策略，不代表当前已下载。以实际速度、累计与主服务器判断；代理出口协议未知的字节不算 IPv6。\n\n请暂停旧下载再启动新下载。关闭开关、退出或接管失效后可能恢复 IPv4。本工具不是整机 IPv4 防火墙。\n\n退出自动保存脱敏报告，不记录 URL、Cookie、Token、文件名或普通网页域名。",Text));
        hint.Text="网站可回退 IPv4；已接管下载不可回退。关闭／退出前请暂停下载，避免恢复原路径后消耗额度。\n数字为助手接收字节；代理出口未知单独显示。已有连接、未知 CDN 和第三方代理可能不受接管。";root.Controls.Add(hint);
        timer.Tick+=(_,_)=>RefreshCards();
        clashToggle.Click+=async(_,_)=>{if(clashBusy||closing)return;clashWork=ToggleClashBinding();await clashWork;};
        clashTimer.Tick+=async(_,_)=>{if(clashBusy||closing)return;clashWork=RefreshClashBinding();await clashWork;};
        Shown+=(_,_)=>{timer.Start();startup=Initialize();};
        FormClosing+=async(_,e)=>{if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;timer.Stop();clashTimer.Stop();await startup;await clashWork;try{SaveAutomaticReport();await StopRouting();}catch(Exception ex){MessageBox.Show("恢复设置未完成："+ex.Message);closing=false;timer.Start();clashTimer.Start();return;}closeReady=true;Close();};
    }
    static TrafficColumn MakeColumn(TableLayoutPanel grid,int column,string title,Color color){var header=Label(title,10);header.ForeColor=color;header.TextAlign=ContentAlignment.MiddleRight;header.Margin=Padding.Empty;grid.Controls.Add(header,column,0);var values=new Label[4];for(int i=0;i<4;i++){var value=Label(i%2==0?"0 B/s":"0 B",11);value.ForeColor=color;value.TextAlign=ContentAlignment.MiddleRight;value.Margin=Padding.Empty;grid.Controls.Add(value,column,i+1);values[i]=value;}return new(values);}
    void RefreshCards(){
            if(busy||closing)return;
            if(native!=null&&!native.Active)router?.MarkCaptureLost();router?.Sample();if(router==null)return;
            foreach(var (id,c) in cards){var r=router.Counters[id];c.Four.Set(r.Rate4,r.V4,r.UploadRate4,r.Upload4);c.Six.Set(r.Rate6,r.V6,r.UploadRate6,r.Upload6);c.Unknown.Set(r.RateProxy,r.Proxy,r.UploadRateProxy,r.UploadProxy);long known=r.V4+r.V6;string share=r.ShareText;
                string route=r.RateProxy>1024&&r.RateProxy>=Math.Max(r.Rate4,r.Rate6)?"当前主要经原代理（出口未知）":r.Rate4+r.Rate6>1024?(known<65536?"少量数据，下载待确认":r.Rate6>=r.Rate4?"当前主要 IPv6":"当前主要 IPv4"):r.State=="正在使用 IPv6"?"当前空闲；上次连接 IPv6":r.State;
                c.State.Text=route+" · "+share+"\n"+(id=="global"?"未知：原代理出口协议未确认":"主服务器："+router.MainServer(id));
                if(router.ProtectionLost){c.Switch.Text=id=="global"?"原路径：可尝试":"失效：未保护";c.Switch.BackColor=id=="global"?Color.PapayaWhip:Color.MistyRose;c.State.ForeColor=Color.DarkRed;c.State.Text=(id=="global"?"普通网页仍尝试原代理／直连":"已接管下载拒绝请求，请暂停下载")+"\n"+share;}
            }
            if(router.ProtectionLost&&!lossNotified){lossNotified=true;SaveAutomaticReport();hint.Text="接管失效：普通网页仍尝试原路径；已接管下载拒绝请求。请暂停下载，旁路可能消耗 IPv4。";hint.ForeColor=Color.DarkRed;
                if(!testMode)MessageBox.Show(this,"网络接管已失效！\n\n助手或网络内核已失去接管，无法阻止应用绕过助手继续使用 IPv4。请立即在夸克／浏览器中暂停下载。\n\n暂停后排除其他代理软件冲突，再全部关闭、重新开启。", "IPv4 流量风险",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        
    }
    internal void SimulateOwnershipLoss(){if(router!=null){router.OwnershipProbe=()=>false;RefreshCards();}}
    internal void PreviewClashBinding(){if(testMode)PaintClashBinding(new("WLAN",new("WLAN","以太网",true,true,35,51,20,30)));}
    async Task Initialize(){await SetAll(true);if(testMode){network.Text="本机界面测试：不进行公网探测";clashToggle.Text="本机界面测试：未操作 Clash Verge";clashToggle.Enabled=false;clashStatus.Text="Clash 配置与网卡状态不会被更改。";return;}if(closing)return;try{var result=await Diagnostics.Network();if(!closing)network.Text=result.Verdict;}catch{if(!closing)network.Text="IPv6 检测未完成；开启后以实际连接为准";}if(!closing){await RefreshClashBinding();clashTimer.Start();}}
    async Task RefreshClashBinding(){
        if(testMode||closing||clashBusy)return;
        clashBusy=true;
        try{await ReadAndPaintClashBinding();}
        finally{clashBusy=false;}
    }
    async Task ReadAndPaintClashBinding(){
        try{var state=await clashBinding.ReadAsync();if(!closing)PaintClashBinding(state);}
        catch(Exception ex){if(!closing){clashToggle.Text="🔀 代理走 Wi-Fi：状态未知，点击重试";clashToggle.Checked=false;clashToggle.BackColor=Color.FromArgb(236,241,245);clashStatus.Text=ClashError(ex);}}
    }
    static string ClashError(Exception ex)=>ex switch {HttpRequestException=>"无法连接 Clash Verge，请先启动 Clash Verge 并检查本机控制器。",OperationCanceledException=>"Clash Verge 请求超时；状态未确认，请重试。",System.Text.Json.JsonException=>"Clash 返回内容无法解析，未确认当前状态。",IOException=>"Clash 配置正在变动或不可读，请稍后重试。",_=>ex is InvalidOperationException or TimeoutException?ex.Message:"读取或切换失败，请检查 Clash Verge 和网卡状态。"};
    void PaintClashBinding(ClashBindingSnapshot state){
        bool on=state.IsWifiBound;clashToggle.Checked=on;
        clashToggle.BackColor=on?Color.FromArgb(192,237,220):Color.FromArgb(236,241,245);
        clashToggle.Text=on
            ? state.Network.CanEnable
                ? "📶 代理走 Wi-Fi：已开启 · 点击恢复默认"
                : "⚠ 已绑定 Wi-Fi · 有线异常 · 点击恢复默认"
            : !state.IsDefault ? "🔀 Clash 已绑定其他网卡 · 点击恢复默认"
            : state.Network.CanEnable ? "🔀 代理走 Wi-Fi：已关闭 · 当前有线优先"
                : "🔀 代理走 Wi-Fi：已关闭 · 有线尚未就绪";
        string active=on?"含 Clash DIRECT；仅新连接生效。":"Clash 跟随系统路由。";
        if(!on&&!string.IsNullOrEmpty(state.InterfaceName))active=$"Clash 当前接口为 {state.InterfaceName}（不是 Wi-Fi）。";
        string priority=state.Network.EthernetFirstIpv4&&state.Network.EthernetFirstIpv6?"以太网优先":"有线未优先";
        clashStatus.Text=$"{(state.Network.WifiName.Length>0?state.Network.WifiName:"Wi-Fi")}：{(state.Network.WifiConnected?"在线":"离线")}；{(state.Network.EthernetName.Length>0?state.Network.EthernetName:"以太网")}：{(state.Network.EthernetConnected?"在线":"离线")}；{priority}\n{active}";
    }
    async Task ToggleClashBinding(){
        if(testMode||closing||clashBusy)return;
        clashBusy=true;clashToggle.Enabled=false;
        try{
            var current=await clashBinding.ReadAsync();
            if(closing)return;
            if(!current.IsDefault)await clashBinding.RestoreDefaultAsync();
            else{
                if(!current.Network.CanEnable)throw new InvalidOperationException(current.Network.EnableBlockReason);
                var answer=MessageBox.Show(this,
                    "Clash 全局出站将优先绑定 Wi-Fi，规则命中的 DIRECT 国内网站也受影响；节点已有独立网卡设置时可能覆盖全局设置。\n\n本助手直连请求仍跟随系统路由，目前以太网的 IPv4/IPv6 默认路由优先。这不是整机流量隔离，也不保证旁路、VPN 和特定路由走有线。\n\n仅新连接生效，既有连接需重新建立。退出助手不会撤销此项；可点击恢复默认，Clash 重载配置也可能重置它。是否继续？",
                    "确认流量分流范围",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
                if(answer!=DialogResult.Yes)return;
                await clashBinding.SetWifiAsync();
            }
            await ReadAndPaintClashBinding();
        }catch(Exception ex){if(!closing)MessageBox.Show(this,ClashError(ex),"流量分流未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);await ReadAndPaintClashBinding();}
        finally{clashBusy=false;if(!closing)clashToggle.Enabled=true;}
    }
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
    object Report()=>new{schema="ipv6-download-assistant/3",version="3.0.7",timeUtc=DateTime.UtcNow,
        protectionLost=router?.ProtectionLost,nativeCapture=native?.Active??false,network=network.Text,
        nativeExitCode=native?.ExitCode,routerHealth=router?.Health(),
        scope="Sent and received relay stream bytes, not campus billing. Enabled content is IPv6-only; ordinary websites may fall back. Existing/bypass/proxied/unknown traffic may be outside capture. Proxy exit family unknown.",
        services=router?.Counters.Select(p=>new{id=p.Key,enabled=p.Value.Enabled,ipv4=Interlocked.Read(ref p.Value.V4),ipv6=Interlocked.Read(ref p.Value.V6),proxyUnknown=Interlocked.Read(ref p.Value.Proxy),ipv4RateBps=p.Value.Rate4,ipv6RateBps=p.Value.Rate6,proxyUnknownRateBps=p.Value.RateProxy,ipv4Uploaded=Interlocked.Read(ref p.Value.Upload4),ipv6Uploaded=Interlocked.Read(ref p.Value.Upload6),proxyUnknownUploaded=Interlocked.Read(ref p.Value.UploadProxy),ipv4UploadRateBps=p.Value.UploadRate4,ipv6UploadRateBps=p.Value.UploadRate6,proxyUnknownUploadRateBps=p.Value.UploadRateProxy,failures=Interlocked.Read(ref p.Value.Failures),state=p.Value.State}).ToArray(),servers=router?.ServerReport()};
    void WriteReport(string path)=>File.WriteAllText(path,JsonSerializer.Serialize(Report(),new JsonSerializerOptions{WriteIndented=true}));
    void SaveAutomaticReport(){if(testMode||router==null)return;try{string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","reports");Directory.CreateDirectory(dir);WriteReport(Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"));}catch{}}
    void Export(){using var save=new SaveFileDialog{Filter="JSON 诊断报告|*.json",FileName="IPv6-download-report.json"};if(save.ShowDialog()!=DialogResult.OK)return;try{WriteReport(save.FileName);hint.Text="报告已导出：包含平台 CDN 域名与流量，不含 URL、账号、文件名或普通网站域名。";}catch(Exception ex){hint.Text="导出失败："+ex.GetType().Name;}}
}
