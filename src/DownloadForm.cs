using System.Text.Json;

namespace IPv6Lens;
internal sealed class DownloadForm : Form
{
    DownloadRouter? router; NativeCapture? native;
    readonly SplitRouting clashBinding=new();
    sealed class TrafficColumn
    {
        public readonly Label DownRate, DownTotal, UpRate, UpTotal;
        public TrafficColumn(Label downRate, Label downTotal, Label upRate, Label upTotal)
        {
            DownRate=downRate; DownTotal=downTotal; UpRate=upRate; UpTotal=upTotal;
        }
        public void Set(double down,long received,double up,long sent){
            DownRate.Text="↓ 下载: "+TrafficStore.Bytes(down)+"/s";
            DownTotal.Text="  累计: "+TrafficStore.Bytes(received);
            UpRate.Text="↑ 上传: "+TrafficStore.Bytes(up)+"/s";
            UpTotal.Text="  累计: "+TrafficStore.Bytes(sent);
        }
    }
    sealed class CardView
    {
        public readonly CheckBox Switch;
        public readonly TrafficColumn Four, Six, Unknown;
        public readonly Label State, Share, Server;
        public CardView(CheckBox sw, TrafficColumn four, TrafficColumn six, TrafficColumn unk, Label st, Label sh, Label srv)
        {
            Switch=sw; Four=four; Six=six; Unknown=unk; State=st; Share=sh; Server=srv;
        }
    }
    readonly Dictionary<string, CardView> cards=new();
    readonly Label network=Label("正在检查 IPv6 网络…",11), hint=Label("打开对应开关，再重新开始下载或刷新视频。",10.5f);
    readonly Label gV6Down=Label("↓ 下载: 0 B/s (累计 0 B)",11f), gV6Up=Label("↑ 上传: 0 B/s (累计 0 B)",11f), gV6Share=Label("0.0%",11.5f);
    readonly Label gV4Down=Label("↓ 下载: 0 B/s (累计 0 B)",11f), gV4Up=Label("↑ 上传: 0 B/s (累计 0 B)",11f);
    readonly Label gProxyDown=Label("↓ 下载: 0 B/s (累计 0 B)",11f), gProxyUp=Label("↑ 上传: 0 B/s (累计 0 B)",11f);
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly System.Windows.Forms.Timer clashTimer=new(){Interval=10000};
    readonly CheckBox clashToggle=new(){Enabled=false,Text="流量分流：正在读取 Clash 状态…",Appearance=Appearance.Button,AutoCheck=false,TextAlign=ContentAlignment.MiddleCenter,FlatStyle=FlatStyle.Flat,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Bold)};
    readonly Label clashStatus=Label("启动 Clash Verge 后自动读取状态。",10.5f);
    bool busy,closing,closeReady,lossNotified,clashBusy;readonly bool testMode;Task startup=Task.CompletedTask,clashWork=Task.CompletedTask;
    internal bool DefaultsActive=>router!=null&&(testMode||native?.Active==true)&&!router.ProtectionLost&&cards.Values.All(c=>c.Switch.Checked)&&router.Counters.Values.All(c=>c.Enabled);
    internal bool SplitActive=>clashToggle.Checked;
    public DownloadForm(bool testMode=false){
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        this.testMode=testMode;
        Text="IPv6 下载助手 3.0.10";Width=1560;Height=1080;MinimumSize=new(1360,960);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(243,246,250);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20,12,20,12),RowCount=6,ColumnCount=1};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.Absolute,74));
        root.RowStyles.Add(new(SizeType.Absolute,106));
        root.RowStyles.Add(new(SizeType.Absolute,96));
        root.RowStyles.Add(new(SizeType.Percent,100));
        root.RowStyles.Add(new(SizeType.Absolute,32));
        root.RowStyles.Add(new(SizeType.Absolute,26));

        // 1. 顶部 Header (图标垂直居中，操作按钮宽幅 700px 容纳 6 颗大字按钮，副标题独立行不被覆盖)
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
        header.RowStyles.Add(new(SizeType.Percent,100));
        header.ColumnStyles.Add(new(SizeType.Absolute,58));header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.Absolute,700));
        var appIconBox=new PictureBox{Width=48,Height=48,SizeMode=PictureBoxSizeMode.Zoom,Image=Icon?.ToBitmap()??new Bitmap(48,48),Margin=new Padding(0,8,10,8),Dock=DockStyle.None,Anchor=AnchorStyles.Left|AnchorStyles.Top};
        header.Controls.Add(appIconBox,0,0);

        var titleBox=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty};
        titleBox.RowStyles.Add(new(SizeType.Absolute,40));titleBox.RowStyles.Add(new(SizeType.Percent,100));
        var titleRow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=Padding.Empty};
        titleRow.Controls.Add(new Label{Text="IPv6 下载助手",Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold),ForeColor=Color.FromArgb(15,23,42),AutoSize=true,Margin=new Padding(0,0,10,0)});
        var verBadge=new Label{Text="v3.0.10",Font=new Font("Microsoft YaHei UI",10.5f,FontStyle.Bold),ForeColor=Color.FromArgb(6,95,70),BackColor=Color.FromArgb(209,250,229),AutoSize=true,Padding=new Padding(6,3,6,3),Margin=new Padding(0,6,0,0)};
        titleRow.Controls.Add(verBadge);titleBox.Controls.Add(titleRow,0,0);
        network.ForeColor=Color.FromArgb(71,85,105);network.AutoEllipsis=true;network.Margin=Padding.Empty;titleBox.Controls.Add(network,0,1);
        header.Controls.Add(titleBox,1,0);

        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty,Padding=new Padding(0,14,0,0)};
        header.Controls.Add(actions,2,0);
        void Button(string title,Action run,Color? bg=null,Color? fg=null){
            var b=new Button{Text=title,AutoSize=true,Height=38,FlatStyle=FlatStyle.Flat,BackColor=bg??Color.White,ForeColor=fg??Color.FromArgb(30,41,59),Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold),Margin=new Padding(4,0,4,0),Padding=new Padding(10,0,10,0)};
            b.FlatAppearance.BorderColor=Color.FromArgb(203,213,225);
            b.Click+=(_,_)=>{if(!clashBusy)run();};actions.Controls.Add(b);
        }
        Button("使用说明",()=>MessageBox.Show("默认全部开启：普通网站优先直连 IPv6，失败后允许 IPv4／原代理；内容下载仅允许 IPv6，不可达则失败。\n\n夸克已知登录接口归入其他网站。Bilibili/YouTube 只按视频 CDN 计数，不把整个浏览器算进去。Steam/Epic 使用独立 Mihomo 内核接管 HTTP/HTTPS；登录也可能受限制。第三方代理内的目的流量无法可靠还原。\n\n流量分流开关自动设置有线默认路由优先，将 Clash 代理节点及提供器绑定 Wi-Fi，DIRECT 绑定有线。关闭或退出恢复原设置，异常退出由守护进程恢复。需连接两张网卡并使用 Clash 规则模式，原有 TUN 自动临时停用并恢复；不修改订阅文件。热重载需数秒，已有连接需重新建立；第三方 VPN、特定路由及另行绑定网卡的软件不在保证范围。\n\n开关是策略，不代表当前已下载。以实际速度、累计与主服务器判断；代理出口协议未知的字节不算 IPv6。\n\n请暂停旧下载再启动新下载。关闭开关、退出或接管失效后可能恢复 IPv4。本工具不是整机 IPv4 防火墙。\n\n退出自动保存脱敏报告，不记录 URL、Cookie、Token、文件名或普通网页域名。",Text));
        Button("详细诊断",()=>{using var detail=new MainForm();detail.ShowDialog(this);});
        Button("导出报告",Export);
        Button("清零流量",()=>router?.Reset());
        Button("全部关闭",async()=>await SetAll(false));
        Button("全部开启",async()=>await SetAll(true),Color.FromArgb(16,185,129),Color.White);
        root.Controls.Add(header);

        // 2. Hero Card: 双网卡智能分流卡片 (加高至 118px，大字清晰排版)
        var hero=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,BackColor=Color.FromArgb(15,23,42),Padding=new Padding(22,12,22,12),Margin=new Padding(0,4,0,6)};
        hero.RowStyles.Add(new(SizeType.Percent,100));
        hero.ColumnStyles.Add(new(SizeType.Percent,66));hero.ColumnStyles.Add(new(SizeType.Percent,34));
        var heroLeft=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty};
        heroLeft.RowStyles.Add(new(SizeType.Absolute,32));heroLeft.RowStyles.Add(new(SizeType.Percent,100));
        var heroTitle=new Label{Text="🔀 双网卡自动分流：代理走 Wi-Fi / 直连走有线",Font=new Font("Microsoft YaHei UI",13f,FontStyle.Bold),ForeColor=Color.White,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
        heroLeft.Controls.Add(heroTitle,0,0);
        clashStatus.ForeColor=Color.FromArgb(203,213,225);clashStatus.AutoEllipsis=true;clashStatus.TextAlign=ContentAlignment.TopLeft;heroLeft.Controls.Add(clashStatus,0,1);
        hero.Controls.Add(heroLeft,0,0);
        clashToggle.Dock=DockStyle.Fill;clashToggle.Margin=new Padding(16,10,0,10);
        hero.Controls.Add(clashToggle,1,0);root.Controls.Add(hero);

        // 3. 全局三类出口宏观指标条 (加高至 108px，大字号粗体高清晰呈现)
        var globalStrip=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,2,0,6)};
        globalStrip.RowStyles.Add(new(SizeType.Percent,100));
        for(int i=0;i<3;i++)globalStrip.ColumnStyles.Add(new(SizeType.Percent,33.33f));
        Panel GlobalCard(string title,Label line1,Label line2,Label? tag,Color bg,Color border,Color textCol){
            var p=new Panel{Dock=DockStyle.Fill,BackColor=bg,Padding=new Padding(14,8,14,8),Margin=new Padding(4,0,4,0)};
            var tl=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Margin=Padding.Empty};
            tl.RowStyles.Add(new(SizeType.Absolute,28));tl.RowStyles.Add(new(SizeType.Percent,50));tl.RowStyles.Add(new(SizeType.Percent,50));
            var headRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=tag!=null?2:1,RowCount=1,Margin=Padding.Empty};
            headRow.RowStyles.Add(new(SizeType.Percent,100));
            if(tag!=null){headRow.ColumnStyles.Add(new(SizeType.Percent,72));headRow.ColumnStyles.Add(new(SizeType.Percent,28));}
            var head=new Label{Text=title,Font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Bold),ForeColor=textCol,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
            headRow.Controls.Add(head,0,0);
            if(tag!=null){tag.Font=new Font("Microsoft YaHei UI",11.5f,FontStyle.Bold);tag.ForeColor=textCol;tag.Dock=DockStyle.Fill;tag.TextAlign=ContentAlignment.MiddleRight;headRow.Controls.Add(tag,1,0);}
            tl.Controls.Add(headRow,0,0);
            line1.Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold);line1.ForeColor=Color.FromArgb(15,23,42);line1.Dock=DockStyle.Fill;line1.TextAlign=ContentAlignment.MiddleLeft;
            line2.Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold);line2.ForeColor=Color.FromArgb(51,65,85);line2.Dock=DockStyle.Fill;line2.TextAlign=ContentAlignment.MiddleLeft;
            tl.Controls.Add(line1,0,1);tl.Controls.Add(line2,0,2);p.Controls.Add(tl);return p;
        }
        globalStrip.Controls.Add(GlobalCard("🟢 IPv6 公网直连 (免流主力)",gV6Down,gV6Up,gV6Share,Color.FromArgb(236,253,245),Color.FromArgb(167,243,208),Color.FromArgb(6,95,70)),0,0);
        globalStrip.Controls.Add(GlobalCard("⚪ IPv4 常规直连 (不可达回退)",gV4Down,gV4Up,null,Color.FromArgb(248,250,252),Color.FromArgb(226,232,240),Color.FromArgb(51,65,85)),1,0);
        globalStrip.Controls.Add(GlobalCard("🟡 未知",gProxyDown,gProxyUp,null,Color.FromArgb(254,243,199),Color.FromArgb(253,230,138),Color.FromArgb(146,64,14)),2,0);
        root.Controls.Add(globalStrip);

        // 4. 6 大平台监控卡片 (字号加大，指标更清晰更易读)
        var cardGrid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Margin=Padding.Empty};
        cardGrid.ColumnStyles.Add(new(SizeType.Percent,50));cardGrid.ColumnStyles.Add(new(SizeType.Percent,50));
        for(int i=0;i<3;i++)cardGrid.RowStyles.Add(new(SizeType.Percent,100f/3));
        root.Controls.Add(cardGrid);

        foreach(var site in DownloadRouter.Sites){
            var card=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=Color.White,Padding=new Padding(16,8,16,6),Margin=new Padding(5)};
            card.RowStyles.Add(new(SizeType.Absolute,38));card.RowStyles.Add(new(SizeType.Percent,100));card.RowStyles.Add(new(SizeType.Absolute,46));

            // 卡片头部
            var heading=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            heading.ColumnStyles.Add(new(SizeType.Percent,100));heading.ColumnStyles.Add(new(SizeType.Absolute,150));
            string siteTitle=site.Id switch{
                "quark"=>"📁 夸克网盘",
                "bilibili"=>"📺 Bilibili",
                "steam"=>"🎮 Steam",
                "epic"=>"⚡ Epic Games",
                "youtube"=>"▶️ YouTube",
                _=>"🌐 其他网站"
            };
            heading.Controls.Add(new Label{Text=siteTitle,Font=new Font("Microsoft YaHei UI",14f,FontStyle.Bold),ForeColor=Color.FromArgb(15,23,42),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft});
            var toggle=new CheckBox{Text="仅 IPv6：关",Appearance=Appearance.Button,TextAlign=ContentAlignment.MiddleCenter,Dock=DockStyle.Fill,Margin=new Padding(1),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(241,245,249),Font=new Font("Microsoft YaHei UI",10.5f,FontStyle.Bold)};
            heading.Controls.Add(toggle);card.Controls.Add(heading);

            // 核心三列度量 (IPv6, IPv4, 未知出口)
            var metrics=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,4,0,2)};
            for(int i=0;i<3;i++)metrics.ColumnStyles.Add(new(SizeType.Percent,33.33f));

            var shareLabel=new Label{Text="0.0%",Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold),ForeColor=Color.FromArgb(6,95,70),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight,AutoEllipsis=false};
            // 累计流量采用深色高对比度，解决灰色看不清的问题：
            // IPv6 采用 Deep Teal 森林深绿 Color.FromArgb(4,120,87)
            // IPv4 采用 Solid Dark Slate 深黑灰 Color.FromArgb(30,41,59)
            // 未知出口 采用 Deep Amber 浓郁琥珀棕 Color.FromArgb(146,64,14)
            var v6Col=BuildProtocolBox(metrics,0,"🟢 IPv6",shareLabel,Color.FromArgb(240,253,244),Color.FromArgb(6,95,70),Color.FromArgb(4,120,87));
            var v4Col=BuildProtocolBox(metrics,1,"⚪ IPv4",null,Color.FromArgb(248,250,252),Color.FromArgb(51,65,85),Color.FromArgb(30,41,59));
            var unkCol=BuildProtocolBox(metrics,2,"🟡 未知",null,Color.FromArgb(255,251,235),Color.FromArgb(180,83,9),Color.FromArgb(146,64,14));
            card.Controls.Add(metrics);

            // 卡片底栏 (2 行清晰布局，彻底解决服务器域名与状态挤压省略号问题)
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0,2,0,0)};
            footer.RowStyles.Add(new(SizeType.Absolute,22));footer.RowStyles.Add(new(SizeType.Percent,100));
            var server=new Label{Text=site.Id=="global"?"公共双栈 CDN 路由":"主服务器：尚未发现",Font=new Font("Microsoft YaHei UI",10f),ForeColor=Color.FromArgb(71,85,105),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            var stateRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            stateRow.RowStyles.Add(new(SizeType.Percent,100));
            stateRow.ColumnStyles.Add(new(SizeType.Percent,64));stateRow.ColumnStyles.Add(new(SizeType.Percent,36));
            var state=new Label{Text="原连接方式",Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold),ForeColor=Color.FromArgb(51,65,85),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            var secBadge=new Label{Text=site.Id=="global"?"双栈优先 · 直连保护":"仅 IPv6 · 阻断保护",Font=new Font("Microsoft YaHei UI",9.5f,FontStyle.Bold),ForeColor=Color.FromArgb(4,120,87),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight};
            stateRow.Controls.Add(state,0,0);stateRow.Controls.Add(secBadge,1,0);
            footer.Controls.Add(server,0,0);footer.Controls.Add(stateRow,0,1);card.Controls.Add(footer);

            cardGrid.Controls.Add(card);
            cards[site.Id]=new CardView(toggle,v4Col,v6Col,unkCol,state,shareLabel,server);
            toggle.CheckedChanged+=async(_,_)=>await Switch(site.Id,toggle.Checked);
        }

        // 5. 提示栏
        hint.ForeColor=Color.FromArgb(51,65,85);root.Controls.Add(hint);

        // 6. 底栏安全声明
        var secFooter=new Label{Text="安全声明：已接管大型下载严格仅允许 IPv6；若节点不可达将阻断请求，防止偷跑 IPv4 计费流量。退出自动还原系统路由与网络设置。",Font=new Font("Microsoft YaHei UI",9.5f),ForeColor=Color.FromArgb(100,116,139),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
        root.Controls.Add(secFooter);

        timer.Tick+=(_,_)=>RefreshCards();
        clashToggle.Click+=async(_,_)=>{if(clashBusy||closing)return;clashWork=ToggleClashBinding();await clashWork;};
        clashTimer.Tick+=async(_,_)=>{if(clashBusy||closing)return;clashWork=RefreshClashBinding();await clashWork;};
        Shown+=(_,_)=>{timer.Start();startup=Initialize();};
        FormClosing+=async(_,e)=>{if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;timer.Stop();clashTimer.Stop();await startup;await clashWork;try{SaveAutomaticReport();await StopRouting();if(!testMode)await clashBinding.RestoreAsync();}catch(Exception ex){MessageBox.Show("恢复设置未完成："+ex.Message);closing=false;timer.Start();clashTimer.Start();return;}closeReady=true;Close();};
    }

    static TrafficColumn BuildProtocolBox(TableLayoutPanel parent,int col,string title,Label? extra,Color bg,Color headerCol,Color totalCol){
        var p=new Panel{Dock=DockStyle.Fill,BackColor=bg,Padding=new Padding(10,5,10,5),Margin=new Padding(3,0,3,0)};
        var tl=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=5,ColumnCount=1,Margin=Padding.Empty};
        tl.RowStyles.Add(new(SizeType.Absolute,26));
        tl.RowStyles.Add(new(SizeType.Percent,25));
        tl.RowStyles.Add(new(SizeType.Percent,25));
        tl.RowStyles.Add(new(SizeType.Percent,25));
        tl.RowStyles.Add(new(SizeType.Percent,25));

        var top=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=extra!=null?2:1,RowCount=1,Margin=Padding.Empty};
        top.RowStyles.Add(new(SizeType.Percent,100));
        if(extra!=null){top.ColumnStyles.Add(new(SizeType.AutoSize));top.ColumnStyles.Add(new(SizeType.Percent,100));}
        var h=new Label{Text=title,Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold),ForeColor=headerCol,AutoSize=true,TextAlign=ContentAlignment.MiddleLeft};
        top.Controls.Add(h,0,0);
        if(extra!=null){
            extra.Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold);
            extra.ForeColor=headerCol;
            extra.Dock=DockStyle.Fill;
            extra.TextAlign=ContentAlignment.MiddleRight;
            extra.AutoEllipsis=false;
            top.Controls.Add(extra,1,0);
        }
        tl.Controls.Add(top,0,0);

        var dr=new Label{Text="↓ 下载: 0 B/s",Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold),ForeColor=Color.FromArgb(15,23,42),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};
        var dt=new Label{Text="  累计: 0 B",Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold),ForeColor=totalCol,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};
        var ur=new Label{Text="↑ 上传: 0 B/s",Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold),ForeColor=Color.FromArgb(15,23,42),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};
        var ut=new Label{Text="  累计: 0 B",Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold),ForeColor=totalCol,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false};

        tl.Controls.Add(dr,0,1);
        tl.Controls.Add(dt,0,2);
        tl.Controls.Add(ur,0,3);
        tl.Controls.Add(ut,0,4);

        p.Controls.Add(tl);parent.Controls.Add(p,col,0);
        return new TrafficColumn(dr,dt,ur,ut);
    }

    void RefreshCards(){
        if(busy||closing)return;
        if(native!=null&&!native.Active)router?.MarkCaptureLost();router?.Sample();if(router==null)return;
        long sumV4=0,sumV6=0,sumProxy=0;long sumUp4=0,sumUp6=0,sumUpProxy=0;
        double sumRate4=0,sumRate6=0,sumRateProxy=0;double sumUpRate4=0,sumUpRate6=0,sumUpRateProxy=0;

        foreach(var (id,c) in cards){
            var r=router.Counters[id];
            c.Four.Set(r.Rate4,r.V4,r.UploadRate4,r.Upload4);
            c.Six.Set(r.Rate6,r.V6,r.UploadRate6,r.Upload6);
            c.Unknown.Set(r.RateProxy,r.Proxy,r.UploadRateProxy,r.UploadProxy);
            long bV4=Interlocked.Read(ref r.V4), bV6=Interlocked.Read(ref r.V6), bProxy=Interlocked.Read(ref r.Proxy);
            double siteTotal=(double)bV4+bV6+bProxy;
            c.Share.Text=siteTotal==0?"0.0%":(100d*bV6/siteTotal).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"%";
            long known=bV4+bV6;
            string route=r.RateProxy>1024&&r.RateProxy>=Math.Max(r.Rate4,r.Rate6)?"主要走原代理":r.Rate4+r.Rate6>1024?(known<65536?"握手数据":r.Rate6>=r.Rate4?"正在使用 IPv6":"正在使用 IPv4"):r.State=="正在使用 IPv6"?"空闲 · 上次 IPv6":r.State;
            c.State.Text=route;
            c.Server.Text=id=="global"?"公共双栈 CDN 路由":"主服务器："+(string.IsNullOrEmpty(router.MainServer(id))?"尚未发现":router.MainServer(id));

            if(router.ProtectionLost){
                c.Switch.Text=id=="global"?"原路径：可尝试":"失效：未保护";
                c.Switch.BackColor=id=="global"?Color.PapayaWhip:Color.MistyRose;
                c.State.ForeColor=Color.DarkRed;
                c.State.Text=id=="global"?"尝试原代理／直连":"接管失效，已阻断";
            }

            sumV4+=bV4;sumV6+=bV6;sumProxy+=bProxy;
            sumUp4+=Interlocked.Read(ref r.Upload4);sumUp6+=Interlocked.Read(ref r.Upload6);sumUpProxy+=Interlocked.Read(ref r.UploadProxy);
            sumRate4+=r.Rate4;sumRate6+=r.Rate6;sumRateProxy+=r.RateProxy;
            sumUpRate4+=r.UploadRate4;sumUpRate6+=r.UploadRate6;sumUpRateProxy+=r.UploadRateProxy;
        }

        double totalKnown=(double)sumV4+sumV6+sumProxy;
        string gShare=totalKnown==0?"0.0%":(100d*sumV6/totalKnown).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+"%";
        gV6Share.Text=gShare+" 占比";
        gV6Down.Text=$"↓ 下载: {TrafficStore.Bytes(sumRate6)}/s (累计 {TrafficStore.Bytes(sumV6)})";
        gV6Up.Text=$"↑ 上传: {TrafficStore.Bytes(sumUpRate6)}/s (累计 {TrafficStore.Bytes(sumUp6)})";
        gV4Down.Text=$"↓ 下载: {TrafficStore.Bytes(sumRate4)}/s (累计 {TrafficStore.Bytes(sumV4)})";
        gV4Up.Text=$"↑ 上传: {TrafficStore.Bytes(sumUpRate4)}/s (累计 {TrafficStore.Bytes(sumUp4)})";
        gProxyDown.Text=$"↓ 下载: {TrafficStore.Bytes(sumRateProxy)}/s (累计 {TrafficStore.Bytes(sumProxy)})";
        gProxyUp.Text=$"↑ 上传: {TrafficStore.Bytes(sumUpRateProxy)}/s (累计 {TrafficStore.Bytes(sumProxy)})";

        if(router.ProtectionLost&&!lossNotified){
            lossNotified=true;SaveAutomaticReport();hint.Text="接管失效：普通网页仍尝试原路径；已接管下载拒绝请求。请暂停下载，旁路可能消耗 IPv4。";hint.ForeColor=Color.DarkRed;
            if(!testMode)MessageBox.Show(this,"网络接管已失效！\n\n助手或网络内核已失去接管，无法阻止应用绕过助手继续使用 IPv4。请立即在夸克／浏览器中暂停下载。\n\n暂停后排除其他代理软件冲突，再全部关闭、重新开启。","IPv4 流量风险",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }
    internal void SimulateOwnershipLoss(){if(router!=null){router.OwnershipProbe=()=>false;RefreshCards();}}
    internal void PreviewClashBinding(){if(testMode)PaintClashBinding(new(new("WLAN","以太网",true,true,356,372,261,261),true,true,"代理节点走 Wi-Fi；DIRECT 与普通直连优先走有线。"));}
    async Task Initialize(){
        await SetAll(true);
        if(testMode){
            network.Text="本机界面测试：不进行公网探测";
            clashToggle.Text="本机界面测试：未操作 Clash Verge";
            clashToggle.Enabled=false;
            clashStatus.Text="Clash 配置与网卡状态不会被更改。";
            return;
        }
        if(closing)return;
        try{
            var result=await Diagnostics.Network();
            if(!closing)network.Text=result.Verdict;
        }catch{
            if(!closing)network.Text="IPv6 检测未完成；开启后以实际连接为准";
        }
        if(!closing){
            await RefreshClashBinding();
            clashToggle.Enabled=true;
            clashTimer.Start();
        }
    }
    async Task RefreshClashBinding(){
        if(testMode||closing||clashBusy)return;
        clashBusy=true;
        try{await ReadAndPaintClashBinding();}
        finally{clashBusy=false;}
    }
    async Task ReadAndPaintClashBinding(){
        try{var state=await clashBinding.ReadAsync();if(!closing)PaintClashBinding(state);}
        catch(Exception ex){if(!closing){clashToggle.Text="🔀 代理走 Wi-Fi：状态未知，点击重试";clashToggle.Checked=false;clashToggle.BackColor=Color.FromArgb(241,245,249);clashToggle.ForeColor=Color.FromArgb(30,41,59);clashStatus.Text=ClashError(ex);}}
    }
    static string ClashError(Exception ex)=>ex switch {HttpRequestException=>"无法连接 Clash Verge，请先启动 Clash Verge 并检查本机控制器。",OperationCanceledException=>"Clash Verge 请求超时；状态未确认，请重试。",System.Text.Json.JsonException=>"Clash 返回内容无法解析，未确认当前状态。",IOException=>"Clash 配置正在变动或不可读，请稍后重试。",_=>ex is InvalidOperationException or TimeoutException?ex.Message:"读取或切换失败，请检查 Clash Verge 和网卡状态。"};
    void PaintClashBinding(SplitState state){
        bool active=state.Active;
        clashToggle.Checked=active;
        clashToggle.BackColor=active?Color.FromArgb(52,211,153):state.Managed?Color.FromArgb(251,191,36):Color.FromArgb(241,245,249);
        clashToggle.ForeColor=active?Color.FromArgb(15,23,42):state.Managed?Color.FromArgb(15,23,42):Color.FromArgb(30,41,59);
        clashToggle.Text=active?"📶 代理已走 Wi-Fi：反重力及代理流量锁定无线":state.Managed?"⚠ 分流需要恢复 · 点击恢复默认":"🔀 开启分流：反重力代理走 Wi-Fi / 直连走有线";
        clashToggle.Enabled=true;
        string wired=state.Network.EthernetConnected?"有线在线":"有线未连接";
        string wifi=state.Network.WifiConnected?$"Wi-Fi 在线 ({state.Network.WifiName})":"Wi-Fi 未连接";
        clashStatus.Text=$"{wired}；{wifi}"+Environment.NewLine+state.Message;
    }
    async Task ToggleClashBinding(){
        if(testMode||closing||clashBusy||busy)return;
        clashBusy=true;clashToggle.Enabled=false;foreach(var card in cards.Values)card.Switch.Enabled=false;
        try{
            bool hasLease=SplitRouting.ReadLease()!=null;
            var current=await clashBinding.ReadAsync();
            if(hasLease){
                clashToggle.Text="正在恢复原路由和 Clash 设置…";
                clashStatus.Text="请等待校验完成；已有连接可能需要重新建立。";
                if(SplitRouting.RestoresTun){await SetAll(false);hint.Text="将恢复 Clash 原有 TUN 模式；助手下载接管已暂停，避免两套接管冲突。";}
                await clashBinding.RestoreAsync();
            }else if(current.Active){
                clashToggle.Text="正在恢复默认网卡绑定…";
                await clashBinding.ClearWifiBindingAsync();
            }else{
                clashToggle.Text="正在将代理绑定至 Wi-Fi…";
                if(current.Network.WifiConnected){
                    if(current.Network.DualNicReady && Program.Admin){
                        await clashBinding.EnableAsync();
                    }else{
                        await clashBinding.SetWifiBindingOnlyAsync(current.Network.WifiName);
                    }
                    if(router==null)await SetAll(true);
                }else{
                    throw new InvalidOperationException("未找到已连接的 Wi-Fi 网卡，无法绑定无线网。");
                }
            }
            await ReadAndPaintClashBinding();
        }catch(Exception ex){
            if(!closing)MessageBox.Show(this,ClashError(ex),"流量分流未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            await ReadAndPaintClashBinding();
        }finally{clashBusy=false;if(!closing){clashToggle.Enabled=true;foreach(var card in cards.Values)card.Switch.Enabled=!busy;}}
    }
    void PaintSwitch(string id,bool on){
        var c=cards[id];c.State.ForeColor=Color.FromArgb(30,41,59);c.Switch.Checked=on;
        c.Switch.Text=(id=="global"?"IPv6 优先":"仅 IPv6 下载")+(on?"：开":"：关");
        c.Switch.BackColor=on?Color.FromArgb(167,243,208):Color.FromArgb(241,245,249);
        c.Switch.ForeColor=on?Color.FromArgb(6,95,70):Color.FromArgb(71,85,105);
    }
    async Task SetAll(bool on){
        if(busy||closing)return;
        if(on&&router?.ProtectionLost==true){hint.Text="请先暂停下载，全部关闭后再重新开启。";return;}
        busy=true;foreach(var c in cards.Values)c.Switch.Enabled=false;
        try{
            if(router==null&&on)await StartRouting();
            if(closing)return;
            if(router!=null){if(on)router.EnableAll();else {await StopRouting();lossNotified=false;}}
            foreach(var id in cards.Keys){PaintSwitch(id,on);if(!on)cards[id].State.Text="未保护：已允许 IPv4／原代理";}
            hint.ForeColor=Color.FromArgb(30,41,59);
            hint.Text=on?"已开启：网站 IPv6 优先；已接管下载仅 IPv6。请暂停后重启下载，已有连接不保证迁移。":"全部已关闭，允许 IPv4／原代理。请暂停下载以免继续消耗额度。";
        }catch(Exception ex){await StopRouting();foreach(var id in cards.Keys)PaintSwitch(id,false);hint.Text="未开启："+ex.Message;}
        finally{busy=false;if(!closing)foreach(var c in cards.Values)c.Switch.Enabled=true;}
    }
    static Label Label(string text,float size)=>new(){Text=text,Font=new Font("Microsoft YaHei UI",size),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Color.FromArgb(30,41,59)};
    async Task Switch(string id,bool on){
        if(busy)return;
        if(router?.ProtectionLost==true){busy=true;cards[id].Switch.Checked=router.Counters[id].Enabled;busy=false;hint.Text="失效未保护：先暂停下载，再全部关闭并重新开启。";return;}
        busy=true;foreach(var c in cards.Values)c.Switch.Enabled=false;
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
    object Report()=>new{schema="ipv6-download-assistant/3",version="3.0.10",timeUtc=DateTime.UtcNow,
        protectionLost=router?.ProtectionLost,nativeCapture=native?.Active??false,network=network.Text,
        nativeExitCode=native?.ExitCode,routerHealth=router?.Health(),
        scope="Sent and received relay stream bytes, not campus billing. Enabled content is IPv6-only; ordinary websites may fall back. Existing/bypass/proxied/unknown traffic may be outside capture. Proxy exit family unknown.",
        services=router?.Counters.Select(p=>new{id=p.Key,enabled=p.Value.Enabled,ipv4=Interlocked.Read(ref p.Value.V4),ipv6=Interlocked.Read(ref p.Value.V6),proxyUnknown=Interlocked.Read(ref p.Value.Proxy),ipv4RateBps=p.Value.Rate4,ipv6RateBps=p.Value.Rate6,proxyUnknownRateBps=p.Value.RateProxy,ipv4Uploaded=Interlocked.Read(ref p.Value.Upload4),ipv6Uploaded=Interlocked.Read(ref p.Value.Upload6),proxyUnknownUploaded=Interlocked.Read(ref p.Value.UploadProxy),ipv4UploadRateBps=p.Value.UploadRate4,ipv6UploadRateBps=p.Value.UploadRate6,proxyUnknownUploadRateBps=p.Value.UploadRateProxy,failures=Interlocked.Read(ref p.Value.Failures),state=p.Value.State}).ToArray(),servers=router?.ServerReport()};
    void WriteReport(string path)=>File.WriteAllText(path,JsonSerializer.Serialize(Report(),new JsonSerializerOptions{WriteIndented=true}));
    void SaveAutomaticReport(){if(testMode||router==null)return;try{string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"IPv6Lens","reports");Directory.CreateDirectory(dir);WriteReport(Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"));}catch{}}
    void Export(){using var save=new SaveFileDialog{Filter="JSON 诊断报告|*.json",FileName="IPv6-download-report.json"};if(save.ShowDialog()!=DialogResult.OK)return;try{WriteReport(save.FileName);hint.Text="报告已导出：包含平台 CDN 域名与流量，不含 URL、账号、文件名或普通网站域名。";}catch(Exception ex){hint.Text="导出失败："+ex.GetType().Name;}}
}
