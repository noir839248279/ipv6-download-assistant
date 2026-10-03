using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace IPv6Lens;
public sealed class MainForm : Form
{
    readonly TrafficStore store=new();
    readonly EtwCollector collector;
    readonly BrowserFeed browser=new();
    readonly TestLease lease=new();
    readonly ComboBox appList=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=310};
    readonly Label network=TextLabel("IPv6 状态尚未检测",12), verdict=TextLabel("选择正在运行的软件，开始观察实际下载流量",16), status=TextLabel("",9);
    readonly Label speed4=TextLabel("—",23),speed6=TextLabel("—",23),total4=TextLabel("—",12),total6=TextLabel("—",12),share=TextLabel("—",23);
    readonly Label scope=TextLabel("",10),videoSummary=TextLabel("尚未接收浏览器数据",12),testState=TextLabel("没有正在进行的测试",11);
    readonly DataGridView flowGrid=Grid(),connectionGrid=Grid(),videoGrid=Grid();
    readonly TextBox diagnostic=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",10)};
    readonly TextBox cdnHost=new(){Width=270,PlaceholderText="实际发现的 CDN 域名或服务器 IP"};
    readonly NumericUpDown duration=new(){Minimum=10,Maximum=120,Value=60,Width=65};
    readonly ComboBox videoSite=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=130};
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly TabControl tabs=new(){Dock=DockStyle.Fill};
    List<AppProcess> apps=new(); HashSet<int> ids=new(); AppProcess? selected;
    List<FlowView> latest=new(); List<BrowserRow> latestBrowser=new(); List<Processes.Connection> connections=new();
    Dictionary<string,string[]> domains=new(); NetworkReport? report;
    bool refreshing,closing,dnsBusy,testStarting; int ticks;
    public MainForm()
    {
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        collector=new(store);
        Text="IPv6 Lens · 校园网流量观察 1.0"; Width=1250;Height=880;MinimumSize=new(1060,750);StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(243,247,249);Font=new Font("Microsoft YaHei UI",10);AutoScaleMode=AutoScaleMode.Dpi;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(22)};
        foreach(var h in new[]{54f,45,48,126,52,30}) root.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,46));Controls.Add(root);
        var title=TextLabel("IPv6 Lens    /    看见真正的下载流量",22);title.ForeColor=Color.FromArgb(16,60,76);root.Controls.Add(title);
        var bar=Row();bar.Controls.Add(appList);bar.Controls.Add(Button("刷新应用",async()=>await RefreshApps()));bar.Controls.Add(Button("开始监控",StartMonitor));bar.Controls.Add(Button("清零",()=>{if(selected!=null){store.Select(ids);latest.Clear();}browser.Clear();}));
        bar.Controls.Add(Button("管理员模式",Elevate));bar.Controls.Add(Button("导出诊断",Export));root.Controls.Add(bar);
        var networkBar=Row(); network.Width=760;networkBar.Controls.Add(network); networkBar.Controls.Add(Button("检测网络",CheckNetwork));root.Controls.Add(networkBar);
        var metrics=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};for(int i=0;i<3;i++)metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33f));
        metrics.Controls.Add(Card("IPv4 公网下载",speed4,total4),0,0);metrics.Controls.Add(Card("IPv6 公网下载",speed6,total6),1,0);metrics.Controls.Add(Card("本次 IPv6 下载占比",share,TextLabel("按公网接收字节计算",11)),2,0);root.Controls.Add(metrics);
        root.Controls.Add(verdict);root.Controls.Add(scope);
        root.Controls.Add(tabs);
        var detail=new TabPage("连接与下载服务器");tabs.TabPages.Add(detail);
        var detailLayout=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1};detailLayout.RowStyles.Add(new(SizeType.Absolute,42));detailLayout.RowStyles.Add(new(SizeType.Percent,64));detailLayout.RowStyles.Add(new(SizeType.Percent,36));detail.Controls.Add(detailLayout);
        var actions=Row(); actions.Controls.Add(cdnHost);actions.Controls.Add(Button("检查 CDN 的 IPv4 / IPv6",ProbeCdn));actions.Controls.Add(Button("观察本地代理出站",ObserveProxy));detailLayout.Controls.Add(actions);
        detailLayout.Controls.Add(flowGrid);detailLayout.Controls.Add(connectionGrid);
        flowGrid.CellClick+=(_,e)=>{if(e.RowIndex>=0 && e.RowIndex<latest.Count){var f=latest[e.RowIndex];cdnHost.Text=domains.TryGetValue(f.Key.Remote,out var names)&&names.Length==1?names[0]:f.Key.Remote;}};
        var video=new TabPage("B站 / YouTube 标签页");tabs.TabPages.Add(video);
        var vl=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=4,ColumnCount=1};vl.RowStyles.Add(new(SizeType.Absolute,44));vl.RowStyles.Add(new(SizeType.Absolute,56));vl.RowStyles.Add(new(SizeType.Percent,100));vl.RowStyles.Add(new(SizeType.Absolute,60));video.Controls.Add(vl);
        var vb=Row();videoSite.Items.AddRange(new[]{"Bilibili","YouTube"});videoSite.SelectedIndex=0;vb.Controls.Add(videoSite);vb.Controls.Add(Button("复制扩展配对码",()=>{if(browser.Endpoint.Length>0){Clipboard.SetText(JsonSerializer.Serialize(new{endpoint=browser.Endpoint,token=browser.Token}));MessageBox.Show("配对码已复制。在扩展弹窗中粘贴后点击配对。每次启动桌面软件需重新配对。","浏览器配对");}}));vb.Controls.Add(Button("打开扩展文件夹",()=>OpenFolder("browser-extension")));vl.Controls.Add(vb);vl.Controls.Add(videoSummary);vl.Controls.Add(videoGrid);
        vl.Controls.Add(TextLabel("按当前标签页媒体响应统计（可能含广告媒体），与进程总量独立；缓存不计，未识别内容不推断。\n经代理／TUN 时，报告端点不能证明物理出口或代理服务器之后的协议。",10));
        var diagTab=new TabPage("网络诊断与临时测试");tabs.TabPages.Add(diagTab);
        var dl=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};dl.RowStyles.Add(new(SizeType.Absolute,64));dl.RowStyles.Add(new(SizeType.Absolute,46));dl.RowStyles.Add(new(SizeType.Absolute,38));dl.RowStyles.Add(new(SizeType.Percent,100));diagTab.Controls.Add(dl);
        dl.Controls.Add(TextLabel("临时 IPv4 隔离测试：最多 120 秒，关闭、异常退出或到期自动恢复。\n阻止所选 EXE 同路径所有实例的 IPv4（包括本地代理），不同 EXE 子进程不在范围。\n先完成登录，再测试新下载。失败不能证明 CDN 不支持 IPv6。",10));
        var testBar=Row();testBar.Controls.Add(TextLabel("测试秒数",10));testBar.Controls.Add(duration);testBar.Controls.Add(Button("临时阻止此应用 IPv4",StartTest));testBar.Controls.Add(Button("立即恢复",()=>{lease.Dispose();testState.Text="已恢复；临时 WFP 会话已关闭";}));dl.Controls.Add(testBar);dl.Controls.Add(testState);dl.Controls.Add(diagnostic);
        status.ForeColor=Color.FromArgb(70,90,100);root.Controls.Add(status);
        timer.Tick+=async(_,_)=>await Tick();
        Shown+=async(_,_)=> {collector.Start();try{await browser.Start();}catch(Exception ex){Log("浏览器配对服务启动失败："+ex.GetType().Name);}await RefreshApps();timer.Start();CheckNetwork();};
        FormClosing+=(_,_)=>{closing=true;timer.Stop();lease.Dispose();collector.Dispose();};
        FormClosed+=async(_,_)=>await browser.DisposeAsync();
    }
    static Label TextLabel(string text,int size)=>new(){Text=text,Font=new Font("Microsoft YaHei UI",size),Dock=DockStyle.Fill,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(28,55,70)};
    static FlowLayoutPanel Row()=>new(){Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(0,3,0,0)};
    static Button Button(string text,Action action){var b=new Button{Text=text,AutoSize=true,Height=34,FlatStyle=FlatStyle.Flat,BackColor=Color.White,Padding=new Padding(7,1,7,1)};b.FlatAppearance.BorderColor=Color.FromArgb(198,215,223);b.Click+=(_,_)=>action();return b;}
    static Panel Card(string title,Label value,Label foot){var p=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,BackColor=Color.White,Padding=new Padding(16,6,10,6),Margin=new Padding(0,0,12,8)};p.RowStyles.Add(new(SizeType.Percent,25));p.RowStyles.Add(new(SizeType.Percent,45));p.RowStyles.Add(new(SizeType.Percent,30));p.Controls.Add(TextLabel(title,11));p.Controls.Add(value);p.Controls.Add(foot);return p;}
    static DataGridView Grid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,AutoGenerateColumns=true};
    void Log(string message){diagnostic.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n\r\n");}
    async Task RefreshApps(){apps=await Task.Run(Processes.List);if(closing)return;var previous=(appList.SelectedItem as AppProcess)?.Pid;appList.DataSource=apps;var pick=apps.FindIndex(x=>x.Pid==previous);if(pick>=0)appList.SelectedIndex=pick;else{pick=apps.FindIndex(x=>x.Name.Contains("quark",StringComparison.OrdinalIgnoreCase));if(pick>=0)appList.SelectedIndex=pick;}}
    void StartMonitor(){if(appList.SelectedItem is not AppProcess p)return;if(lease.Active||testStarting){MessageBox.Show("请先立即恢复当前临时测试，再切换监控对象。");return;}selected=p;ids=Processes.Group(p,apps);store.Select(ids);domains.Clear();latest.Clear();Log($"开始监控 {p.Name}，含同路径实例与已发现子进程，共 {ids.Count} 个。统计从现在开始。");}
    async Task Tick()
    {
        if(refreshing||closing)return;refreshing=true;
        try {
            if(++ticks%3==1){var scan=await Task.Run(()=> (Apps:Processes.List(),Connections:Processes.Connections()));if(closing)return;apps=scan.Apps;connections=scan.Connections;if(selected!=null){var current=Processes.Group(selected,apps);foreach(var id in ids.Except(current))store.RemovePid(id);foreach(var id in current)store.AddPid(id);ids=current;}}
            latest=store.Sample();var s=TrafficStore.Sum(latest);bool collecting=collector.Status.StartsWith("正在采集");
            speed4.Text=collecting?TrafficStore.Bytes(s.Rate4)+"/s":"—";speed6.Text=collecting?TrafficStore.Bytes(s.Rate6)+"/s":"—";
            total4.Text=collecting?"累计 "+TrafficStore.Bytes(s.V4):"流量采集未运行";total6.Text=collecting?"累计 "+TrafficStore.Bytes(s.V6):"流量采集未运行";share.Text=collecting&&s.Share!=null?$"{s.Share:F1}%":"—";
            verdict.Text=selected==null?"选择正在运行的软件，开始观察实际下载流量":!collecting?"没有可用采集数据，不能判断 IPv4 / IPv6 占比":s.V4+s.V6==0&&s.Local>0?"发现本机流量：请检查代理出站，不能判为公网 IPv4 下载":s.V4+s.V6==0&&s.Other>0?"发现局域网／隧道流量，公网协议尚不明确":s.Verdict;
            scope.Text=selected==null?"统计范围：未选择应用":$"监控 {selected.Name} · {ids.Count} 个进程 | 本机 {TrafficStore.Bytes(s.Local)} | 局域网／隧道 {TrafficStore.Bytes(s.Other)} | 从本次选择或清零开始";
            if(selected!=null && collecting && s.Local>s.V4+s.V6)
                verdict.Text="本次接收主要经过本机端点／代理，公网下载协议需观察代理出站";
            else if(selected!=null && collecting && s.Other>s.V4+s.V6)
                verdict.Text="本次接收主要经过局域网／隧道，公网下载协议尚不明确";
            flowGrid.DataSource=latest.Take(1000).Select(f=>new{PID=f.Key.Pid,协议=f.Key.Transport+" "+f.Family,服务器=f.Key.Remote,端口=f.Key.RemotePort,下载速度=TrafficStore.Bytes(f.Rate)+"/s",累计接收=TrafficStore.Bytes(f.Received),范围=f.Scope,用途=f.Role,域名候选=domains.TryGetValue(f.Key.Remote,out var ns)?string.Join(" / ",ns):"未知",最后活动=f.LastSeen.ToLocalTime().ToString("HH:mm:ss")}).ToList();
            connectionGrid.DataSource=connections.Where(x=>ids.Contains(x.Pid)).Take(500).Select(x=>new{PID=x.Pid,当前TCP状态=x.State,本地=x.Local+":"+x.LocalPort,远端=x.Remote+":"+x.RemotePort}).ToList();
            latestBrowser=browser.Sample();var rows=latestBrowser.Where(x=>x.Site==(string)videoSite.SelectedItem! && x.Session==browser.ActiveSession).ToList();
            long b4=0,b6=0,other=0;double r4=0,r6=0;
            foreach(var b in rows){if(IPAddress.TryParse(b.Address,out var ip)&&TrafficStore.Scope(ip)=="公网"){if(TrafficStore.Family(ip)=="IPv6"){b6+=b.Bytes;r6+=b.Rate;}else{b4+=b.Bytes;r4+=b.Rate;}}else other+=b.Bytes;}
            var vShare=b4+b6==0?"未知":$"{100.0*b6/(b4+b6):F1}%";
            videoSummary.Text=$"IPv4 {TrafficStore.Bytes(r4)}/s · {TrafficStore.Bytes(b4)}   |   IPv6 {TrafficStore.Bytes(r6)}/s · {TrafficStore.Bytes(b6)}   |   占比 {vShare}\n本机／特殊／未知端点：{TrafficStore.Bytes(other)} · {((DateTime.UtcNow-browser.LastSeen).TotalSeconds<5?"扩展在线": "未接收实时扩展数据")}";
            videoGrid.DataSource=rows.Take(500).Select(x=>new{站点=x.Site,标签会话=x.Session[..Math.Min(8,x.Session.Length)],域名=x.Host,报告的端点=x.Address,协议=x.Protocol,下载速度=TrafficStore.Bytes(x.Rate)+"/s",已接收=TrafficStore.Bytes(x.Bytes),识别依据=x.Evidence switch{"media-mime"=>"媒体 MIME","media-type"=>"媒体资源","youtube-ump"=>"YouTube UMP","cdn-video-path"=>"CDN 视频路径",_=>"已知 CDN"}}).ToList();
            if(browser.Incomplete)videoSummary.Text+=" · 数据曾丢失，统计不完整";
            if(lease.Active)testState.Text=$"临时测试中，还剩 {Math.Ceiling((lease.Expires-DateTime.UtcNow).TotalSeconds)} 秒 · 随时点击立即恢复";else if(lease.Expires!=DateTime.MinValue){lease.Dispose();testState.Text="临时测试已结束，IPv4 过滤已撤销";}
            long lost=collector.Lost;status.Text=collector.Status+$"\nETW 丢失事件 {lost} · 条目上限丢弃 {store.Dropped}。传输层字节不是校园网计费账单；TUN/VPN 可能改变实际外网路径。";
            if(lost>0||store.Dropped>0)verdict.Text="统计不完整（丢失事件）："+verdict.Text;
            if(tabs.SelectedIndex==1){
                speed4.Text=TrafficStore.Bytes(r4)+"/s";speed6.Text=TrafficStore.Bytes(r6)+"/s";total4.Text="累计 "+TrafficStore.Bytes(b4);total6.Text="累计 "+TrafficStore.Bytes(b6);share.Text=vShare;
                scope.Text=$"当前 {videoSite.SelectedItem} 标签页媒体会话 · 本机／特殊／未知端点 {TrafficStore.Bytes(other)} · CDP 编码字节";
                verdict.Text=rows.Count==0?"尚无该标签页的媒体流量证据":b4+b6==0&&other>0?"媒体经过本机／特殊端点，公网 IPv4 / IPv6 比例未知":new Summary(b4,b6,r4,r6,0,0).Verdict;
                if(browser.Incomplete)verdict.Text="统计不完整："+verdict.Text;
                status.Text="浏览器标签页媒体统计，与进程 ETW 总量独立；同一页面可能包含广告媒体。\n回环代理与隧道端点不计入公网占比；代理服务器之后的网络在本机不可见。";
            }
            if(ticks%15==1 && !dnsBusy && latest.Count>0){dnsBusy=true;var remotes=latest.Take(300).Select(x=>x.Key.Remote).ToArray();_ = UpdateDns(remotes);}
        } catch(Exception ex){Log("刷新未完成："+ex.GetType().Name);}finally{refreshing=false;}
    }
    async Task UpdateDns(string[] remotes){try{var result=await Diagnostics.DnsCandidates(remotes);if(!closing)domains=result;}catch(Exception ex){if(!closing)Log(ex.Message);}finally{dnsBusy=false;}}
    async void CheckNetwork(){network.Text="正在分别测试 IPv4 / IPv6 直连 HTTPS…";try{report=await Diagnostics.Network();if(closing)return;network.Text=report.Verdict;Log($"全球单播 IPv6 地址：{report.GlobalV6}；有地址不等于外网可用。\r\n"+string.Join("\r\n",report.Interfaces)+"\r\n"+string.Join("\r\n",report.Probes.Select(p=>$"{p.Host} {p.Family}: {p.Detail}")));}catch(Exception ex){if(!closing){network.Text="检测未完成";Log(ex.Message);}}}
    async void ProbeCdn(){string host=cdnHost.Text.Trim();if(Uri.CheckHostName(host)==UriHostNameType.Unknown){MessageBox.Show("请选择一个实际发现的服务器，或输入纯域名；不要粘贴下载链接。");return;}try{Log("检查 "+host+"：DNS 缓存只提供候选域名，不证明应用请求归属。IP 探测不代表存在域名 AAAA 记录。");var probes=await Task.WhenAll(Diagnostics.ProbeHost(host,AddressFamily.InterNetwork),Diagnostics.ProbeHost(host,AddressFamily.InterNetworkV6));if(!closing)foreach(var p in probes)Log(p.Family+"："+p.Detail);}catch(Exception ex){if(!closing)Log(ex.GetType().Name);}}
    void ObserveProxy(){var ports=latest.Where(f=>f.Scope=="本机／代理候选").OrderByDescending(f=>f.Received).Select(f=>f.Key.RemotePort).ToHashSet();var owners=connections.Where(c=>c.State=="监听"&&ports.Contains(c.LocalPort)&&!ids.Contains(c.Pid)).Select(c=>c.Pid).Distinct().ToArray();if(owners.Length!=1){MessageBox.Show("尚未唯一识别代理监听进程。请从应用列表选择 Clash / mihomo 等实际代理核心，再开始监控。TUN 模式可能没有本地代理连接。");return;}var p=apps.FirstOrDefault(a=>a.Pid==owners[0]);if(p==null)return;MessageBox.Show("即将观察 "+p.Name+" 的全部出站流量。这些字节不能精确归因到刚才的应用；代理服务器之后的协议在本机不可见。");appList.DataSource=apps;appList.SelectedItem=p;StartMonitor();}
    async void StartTest(){if(selected==null)return;if(lease.Active||testStarting)return;var target=selected;if(!Program.Admin){MessageBox.Show("请先点击管理员模式，再选择测试应用。");return;}if(!Ipv4Guard.Allowed(target.Path)){MessageBox.Show("此目标无法安全测试：路径不可读取、属于系统组件、代理核心或本软件。");return;}if(MessageBox.Show($"将临时阻止 {target.Name} 的 IPv4，共 {duration.Value} 秒。\n\n目标：{target.Path}\n\n同一路径的所有实例都会受到影响（浏览器所有窗口）。不同 EXE 子进程不受此规则影响。IPv4 登录接口和本地代理也会被阻止。请暂停后重新发起下载以验证新连接。\n\n关闭软件、异常退出或超时将自动恢复。是否开始？","仅此应用的临时 IPv4 测试",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;try{testStarting=true;await lease.Start(target.Path,(int)duration.Value);if(closing){lease.Dispose();return;}Log("临时 WFP 会话已生效。请验证新下载并查看 IPv4 字节是否停止增长。失败原因可能是控制接口依赖 IPv4，不代表内容 CDN 不支持 IPv6。");}catch(Exception ex){if(!closing)MessageBox.Show(ex.Message);}finally{testStarting=false;}}
    void Export(){using var save=new SaveFileDialog{Filter="JSON 诊断报告|*.json",FileName="IPv6Lens-diagnostic-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"};if(save.ShowDialog()!=DialogResult.OK)return;try{File.WriteAllText(save.FileName,Report.Build(latest,latestBrowser,report,collector.Status,collector.Lost,store.Dropped,browser.Incomplete));MessageBox.Show("报告已导出。服务器、域名与进程已匿名化；不含原始 IP、应用路径、下载链接、配对码或请求内容。");}catch(Exception ex){MessageBox.Show(ex.Message);}}
    void Elevate(){if(Program.Admin){MessageBox.Show("当前已处于管理员模式。");return;}try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"});Close();}catch{MessageBox.Show("未获得管理员授权，仍可查看 TCP 连接与使用浏览器扩展。");}}
    void OpenFolder(string name){string baseDir=AppContext.BaseDirectory;var candidates=new[]{Path.Combine(baseDir,name),Path.Combine(baseDir,"..",name),Path.Combine(baseDir,"..","..",name)};var path=candidates.FirstOrDefault(Directory.Exists);if(path!=null)Process.Start(new ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true});else MessageBox.Show("请打开发布包中的 "+name+" 文件夹。");}
}
