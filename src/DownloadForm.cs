using System.Text.Json;

namespace IPv6Lens;

internal sealed partial class DownloadForm : Form
{
    static readonly string Version = typeof(DownloadForm).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    DownloadRouter? router;
    NativeCapture? native;
    readonly Dictionary<string,DownloadCounter> counters = DownloadRouter.Sites.ToDictionary(s=>s.Id,_=>new DownloadCounter { Mode=ServiceMode.Mixed });
    PassiveTraffic? passive;
    DateTime lastSample = DateTime.UtcNow;
    internal Func<IEnumerable<string>,Task>? SyncServicesTest;
    internal Func<bool> HasIPv6 = () => NodeNetwork.CaptureInterface() != 0;
    bool? ipv6Available;
    DateTime nextNetworkCheck;
    readonly SplitRouting clashBinding = new();
    readonly Dictionary<string, ServiceTile> cards = new();
    readonly Panel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Panel canvas = new();
    readonly Label title = MakeLabel("IPv6 下载助手", 18, true);
    readonly Label version = MakeLabel("v" + Version, 9);
    readonly Label network = MakeLabel("正在检查本机 IPv6 出口…", 9);
    readonly Label hint = MakeLabel("正在准备服务监测…", 9);
    readonly Label coverage = MakeLabel("关闭接管仍监测流量；浏览器 / AList 使用 DNS 关联，未识别部分不计入服务。", 9);
    readonly LinkLabel diagnosticStatus = new() { Text = "诊断：点击打开报告目录",
        AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.HoverUnderline };
    static string ReportsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPv6Lens", "reports");
    string? lastSavedReport, reportWriteError, seenRuntimeReport, seenIngressReport;
    readonly Label sectionTitle = MakeLabel("服务监控", 12, true);
    readonly Label serviceCount = MakeLabel("0 / 6 已开启", 9);
    readonly Label masterText = MakeLabel("全部关闭", 9, true);
    readonly PictureBox appIcon = new() { SizeMode = PictureBoxSizeMode.Zoom };
    readonly DashboardToggle master = new() { AutoCheck = false, AccessibleName = "全部服务接管" };
    readonly SplitTile split = new();
    readonly TrafficShare share = new();
    readonly TrafficSummary[] summaries = { new(0), new(1), new(2) };
    readonly List<DashboardButton> actions = new();
    readonly ToolTip tooltips = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer clashTimer = new() { Interval = 10000 };
    bool busy, closing, closeReady, lossNotified, clashBusy, clashPolling, layingOut;
    readonly bool testMode;
    Task startup = Task.CompletedTask, clashWork = Task.CompletedTask, serviceWork = Task.CompletedTask;
    float ScaleFactor => DeviceDpi / 96f;
    int Px(float n) => (int)Math.Round(n * ScaleFactor);
    internal bool DefaultsActive => router != null && (testMode || native?.Active == true) &&
        !router.ProtectionLost && cards.Where(p=>p.Key!="global").All(p=>p.Value.Toggle.Checked) &&
        router.Counters.Where(p=>p.Key!="global").All(p=>p.Value.Enabled);
    internal bool SplitActive => split.Toggle.Checked;

    public DownloadForm(bool testMode = false)
    {
        this.testMode = testMode;
        if (testMode) HasIPv6 = () => true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = "IPv6 下载助手 " + Version;
        ClientSize = new(Px(1280), Px(920)); MinimumSize = new(Px(540), Px(600));
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        appIcon.Image = Icon?.ToBitmap();
        Controls.Add(viewport); viewport.Controls.Add(canvas);
        canvas.Controls.AddRange(new Control[] { appIcon, title, version, network, split, share,
            sectionTitle, serviceCount, masterText, master, hint, coverage, diagnosticStatus });
        canvas.Controls.AddRange(summaries);
        void Action(string text, string description, Action run, int width = 96)
        {
            var button = new DashboardButton(text, description, (_, _) => { if (!closing && !clashBusy) run(); })
            { Tag = width };
            tooltips.SetToolTip(button, description); actions.Add(button); canvas.Controls.Add(button);
        }
        Action("使用说明", "查看使用说明", () => MessageBox.Show(this, UsageText, "使用说明"));
        Action("详细诊断", "打开连接与网络诊断", () => { using var detail = new MainForm(); detail.ShowDialog(this); });
        Action("IPv6 节点", "刷新、测试并选择 IPv6 内容节点", () =>
        {
            if (router == null) { MessageBox.Show(this, "请先开启需要调整的内容服务", "IPv6 节点"); return; }
            using var nodes = new NodeForm(router.Nodes, router.ContentHosts()); nodes.ShowDialog(this);
        });
        Action("导出报告", "导出脱敏 JSON 诊断报告", Export);
        Action("记录卡顿", "保存当前现场和近期采样，不测速、不换节点", RecordSlowdown);
        Action("清零流量", "清零累计流量和趋势", ResetTraffic);
        Action("◐", "切换深色 / 浅色主题", () => { DashboardTheme.Light = !DashboardTheme.Light; ApplyTheme(); }, 36);
        diagnosticStatus.LinkClicked += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ReportsDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportsDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { hint.Text = "打开报告目录失败：" + ex.GetType().Name; }
        };
        tooltips.SetToolTip(master, "全部开启 / 全部关闭");
        tooltips.SetToolTip(split.Toggle, "开启分流或恢复原网络设置");
        foreach (string id in new[] { "quark", "bilibili", "steam", "epic", "youtube", "global" })
        {
            var tile = new ServiceTile(id);
            cards[id] = tile; canvas.Controls.Add(tile);
            tooltips.SetToolTip(tile.Toggle, "仅切换此服务；关闭后恢复原路径");
            tooltips.SetToolTip(tile.MixedToggle, "混合模式开：仅明确辅助请求允许限额 IPv4，初始 2 MiB；视频、下载和未知请求仍仅 IPv6。关：辅助请求也必须使用 IPv6。不会自动补充额度。");
            tile.Allowance.LinkClicked += (_,_) => {
                if(busy||closing||counters[id].Mode!=ServiceMode.Mixed)return;
                if(MessageBox.Show(this,"仅为"+tile.AccessibleName+"增加 2 MiB 辅助原路径额度（收发合计）。可能使用 IPv4 或原代理；下载和视频仍只允许 IPv6。是否补充？",
                    "补充辅助额度",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                counters[id].Auxiliary.Grant();
                hint.Text=tile.AccessibleName+"辅助额度已补充 2 MiB；请重试刚才的操作，大文件仍仅 IPv6";
                RefreshCards(false);
            };
            tile.Toggle.CheckedChanged += async (_, _) =>
            {
                if (busy || closing) return;
                serviceWork = Switch(id, tile.Toggle.Checked); await serviceWork;
            };
            tile.MixedToggle.CheckedChanged += async (_, _) =>
            {
                if (busy || closing) return;
                serviceWork = ChangeMode(id, tile.MixedToggle.Checked ? ServiceMode.Mixed : ServiceMode.Strict);
                await serviceWork;
            };
        }
        master.Click += async (_, _) =>
        {
            if (busy || closing || clashBusy) return;
            serviceWork = SetAll(!cards.Values.All(c => c.Toggle.Checked)); await serviceWork;
        };
        split.Toggle.Click += async (_, _) =>
        {
            if (clashBusy || clashPolling || closing) return;
            clashWork = ToggleClashBinding(); await clashWork;
        };
        timer.Tick += (_, _) => RefreshCards();
        clashTimer.Tick += async (_, _) =>
        {
            if (clashBusy || clashPolling || closing) return;
            clashWork = RefreshClashBinding(); await clashWork;
        };
        viewport.SizeChanged += (_, _) => LayoutDashboard();
        DpiChanged += (_, _) => LayoutDashboard();
        Shown += (_, _) => { LayoutDashboard(); timer.Start(); startup = Initialize(); };
        FormClosing += async (_, e) =>
        {
            if (closeReady) return;
            e.Cancel = true; if (closing) return;
            closing = true; timer.Stop(); clashTimer.Stop(); SetControlsEnabled();
            await startup; await clashWork; await serviceWork;
            try
            {
                SaveAutomaticReport(); await StopRouting();
                if (!testMode) await clashBinding.RestoreAsync();
                passive?.Dispose(); passive=null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "恢复设置未完成：" + ex.Message);
                closing = false; timer.Start(); clashTimer.Start(); SetControlsEnabled(); return;
            }
            closeReady = true; Close();
        };
        ApplyTheme(); LayoutDashboard();
    }

    void LayoutDashboard()
    {
        if (IsDisposed || layingOut) return;
        layingOut = true;
        int scrollY = -viewport.AutoScrollPosition.Y;
        viewport.SuspendLayout();
        canvas.SuspendLayout();
        try
        {
            LayoutDashboardContent();
            // Forget the old extent when a column change shortens the canvas.
            viewport.AutoScrollMinSize = new(0, canvas.Height);
        }
        finally
        {
            canvas.ResumeLayout();
            viewport.ResumeLayout(true);
            viewport.PerformLayout();
            viewport.AutoScrollPosition = new(0,
                Math.Min(scrollY, Math.Max(0, canvas.Height - viewport.ClientSize.Height)));
            layingOut = false;
        }
    }

    void LayoutDashboardContent()
    {
        int width = Math.Max(Px(480), viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        canvas.Width = width;
        float w = width / ScaleFactor, gap = 14, left = 24, inner = w - 48;
        void Place(Control c, float x, float y, float cw, float ch) => c.SetBounds(Px(x), Px(y), Px(cw), Px(ch));
        bool compact = w < 1120;
        Place(appIcon, left, 22, 42, 42);
        Place(title, left + 54, 17, 202, 31);
        Place(version, left + 260, 22, 62, 23);
        Place(network, left + 54, 51, compact ? inner - 58 :
            Math.Max(100, w - 24 - actions.Sum(b => (int)b.Tag! + 6) - left - 66), 23);
        float actionX = compact ? left : w - 24 - actions.Sum(b => (int)b.Tag! + 6) + 6;
        float actionY = compact ? 88 : 29;
        foreach (var button in actions)
        {
            int bw = (int)button.Tag!;
            if (compact && actionX + bw > w - 24) { actionX = left; actionY += 40; }
            Place(button, actionX, actionY, bw, 34); actionX += bw + 6;
        }
        float y = compact ? actionY + 54 : 96;
        if (w >= 850)
        {
            float splitWidth = (inner - gap) * .52f;
            Place(split, left, y, splitWidth, 166);
            Place(share, left + splitWidth + gap, y, inner - splitWidth - gap, 166);
            y += 180;
        }
        else
        {
            Place(split, left, y, inner, 166);
            Place(share, left, y + 180, inner, 166); y += 360;
        }
        int statColumns = w >= 760 ? 3 : 1;
        float statWidth = (inner - (statColumns - 1) * gap) / statColumns;
        for (int i = 0; i < 3; i++)
            Place(summaries[i], left + i % statColumns * (statWidth + gap),
                y + i / statColumns * 166, statWidth, 152);
        y += statColumns == 3 ? 166 : 498;
        Place(sectionTitle, left, y + 7, 100, 32);
        Place(serviceCount, left + 106, y + 7, 118, 32);
        Place(masterText, w - 24 - 142, y + 7, 80, 32);
        Place(master, w - 24 - 48, y + 9, 46, 28);
        y += 52;
        int columns = w >= 1100 ? 3 : w >= 730 ? 2 : 1;
        float tileWidth = (inner - (columns - 1) * gap) / columns;
        int index = 0;
        foreach (var tile in cards.Values)
        {
            Place(tile, left + index % columns * (tileWidth + gap),
                y + index / columns * 344, tileWidth, 330); index++;
        }
        y += ((cards.Count + columns - 1) / columns) * 344;
        Place(coverage, left, y, inner, 30);
        // Anchor to actual pixel edges; rounding adjacent logical positions at
        // fractional DPI can otherwise overlap the footer by one pixel.
        diagnosticStatus.SetBounds(Px(left), coverage.Bottom, Px(inner), Px(30));
        hint.SetBounds(Px(left), diagnosticStatus.Bottom, Px(inner), Px(34));
        canvas.Height = hint.Bottom + Px(18);
    }

    static Label MakeLabel(string text, float size, bool bold = false) => new()
    {
        Text = text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };
    void ApplyTheme()
    {
        BackColor = viewport.BackColor = canvas.BackColor = DashboardTheme.Background;
        foreach (var label in new[] { title, version, network, hint, coverage, sectionTitle, serviceCount, masterText })
        {
            label.BackColor = DashboardTheme.Background;
            label.ForeColor = label == title || label == sectionTitle ? DashboardTheme.Text : DashboardTheme.Muted;
        }
        version.ForeColor = DashboardTheme.V6;
        if (ipv6Available == false) network.ForeColor = DashboardTheme.Unknown;
        diagnosticStatus.LinkColor = diagnosticStatus.ActiveLinkColor = DashboardTheme.V6;
        foreach (var button in actions) button.RefreshTheme();
        foreach (var tile in cards.Values) tile.RefreshAllowanceTheme();
        if (router?.ProtectionLost == true) hint.ForeColor = DashboardTheme.Error;
        canvas.Invalidate(true);
    }
    void SyncMaster()
    {
        int count = cards.Values.Count(c => c.Toggle.Checked);
        master.Checked = count == cards.Count; master.Mixed = count > 0 && count < cards.Count;
        masterText.Text = count == cards.Count ? "全部开启" : count == 0 ? "全部关闭" : "部分开启";
        serviceCount.Text = $"{count} / {cards.Count} 已开启";
        master.AccessibleDescription = masterText.Text;
        master.Invalidate();
    }
    void SetControlsEnabled()
    {
        bool enabled = !busy && !closing && !clashBusy;
        master.Enabled = enabled;
        foreach (var tile in cards.Values) { tile.Toggle.Enabled = enabled; tile.MixedToggle.Enabled = enabled && tile.Id != "global"; }
        foreach (var button in actions) button.Enabled = !closing && !busy && !clashBusy;
        split.Toggle.Enabled = enabled && !clashPolling && !testMode;
    }
    void PaintSwitch(string id, bool on)
    {
        cards[id].Toggle.Checked = on;
        if (!on) cards[id].State = "未接管";
        cards[id].Invalidate(); SyncMaster();
    }
    void ResetTraffic()
    {
        if(router!=null)router.Reset();else DownloadRouter.ResetCounters(counters.Values);
        foreach (var summary in summaries) summary.ResetHistory();
        RefreshCards(false);
        hint.Text = "累计流量和趋势已清零";
    }
    void RefreshCards(bool sample = true)
    {
        if (busy || closing) return;
        if (native != null && !native.Active) router?.MarkCaptureLost();
        if (sample)
        {
            if (DateTime.UtcNow >= nextNetworkCheck) UpdateNetworkAvailability();
            passive?.Sample(counters, native?.Active == true);
            double seconds=Math.Max(.001,(DateTime.UtcNow-lastSample).TotalSeconds);lastSample=DateTime.UtcNow;
            // One sampling clock survives stop/start, preventing spurious rate spikes.
            router?.CheckHealth();
            DownloadRouter.SampleCounters(counters.Values,seconds);
        }
        long[] totals = new long[3], uploaded = new long[3];
        double[] rates = new double[3], uploadRates = new double[3];
        foreach (var (id, tile) in cards)
        {
            var r = counters[id];
            string server = router?.MainServer(id) ?? "尚未发现服务器";
            if (server == "尚无内容连接") server = "尚未发现服务器";
            tile.Update(r, server, router?.ProtectionLost == true);
            if (!r.Enabled && passive != null) tile.State = passive.ServiceStatus(id);
            if (r.Enabled && ipv6Available == false)
                tile.State = "无本机 IPv6 出口 · 内容未回退";
            totals[0] += Interlocked.Read(ref r.V6); totals[1] += Interlocked.Read(ref r.V4); totals[2] += Interlocked.Read(ref r.Proxy);
            uploaded[0] += Interlocked.Read(ref r.Upload6); uploaded[1] += Interlocked.Read(ref r.Upload4); uploaded[2] += Interlocked.Read(ref r.UploadProxy);
            rates[0] += r.Rate6; rates[1] += r.Rate4; rates[2] += r.RateProxy;
            uploadRates[0] += r.UploadRate6; uploadRates[1] += r.UploadRate4; uploadRates[2] += r.UploadRateProxy;
        }
        for (int i = 0; i < 3; i++) summaries[i].Update(rates[i], uploadRates[i], totals[i], uploaded[i], sample);
        share.Update(totals[0], totals[1], totals[2]); SyncMaster();
        var reportStatus = router?.RuntimeReportStatus;
        if (reportStatus?.LastReport is string runtimeFile && runtimeFile != seenRuntimeReport)
            lastSavedReport = seenRuntimeReport = runtimeFile;
        if (router?.IngressReportFile is string ingressFile && ingressFile != seenIngressReport)
            lastSavedReport = seenIngressReport = ingressFile;
        string? diagnosticError = reportStatus?.Error ?? router?.IngressReportError ?? reportWriteError;
        diagnosticStatus.Text = diagnosticError != null ? "诊断保存失败：" + diagnosticError + " · 点击打开目录" :
            reportStatus?.ManualPending == true ? "正在记录卡顿现场（下一次后台采样，约 10 秒内）…" :
            lastSavedReport != null ? "诊断已保存：" + lastSavedReport + " · 点击打开目录" :
            router == null ? "诊断：接管已关闭 · 点击打开报告目录" :
            "诊断：后台记录运行中 · 点击打开报告目录";
        diagnosticStatus.LinkColor = diagnosticError == null ? DashboardTheme.V6 : DashboardTheme.Error;
        tooltips.SetToolTip(diagnosticStatus, ReportsDirectory + "\n" + (reportStatus?.Trigger ?? "低速疑似 / 连续失败 / 内核高 CPU；不会改变网络路径"));
        if (passive != null) coverage.Text = passive.Coverage;
        tooltips.SetToolTip(coverage, (passive?.Status ?? "旁路监测未启动") + "；丢失事件 " + (passive?.Lost ?? 0) +
            "。开启服务按中继字节统计；关闭后按原生进程或本进程的 DNS 关联统计公网字节。AList 不把本机转发重复计量。加密 DNS、共享地址与代理可能无法归属；TUN/代理出口未知，不是整机账单。");
        if (router?.ProtectionLost == true && !lossNotified)
        {
            lossNotified = true; SaveAutomaticReport();
            hint.Text = "接管失效，请暂停下载，全部关闭后重新开启。";
            hint.ForeColor = DashboardTheme.Error;
            if (!testMode) MessageBox.Show(this,
                "网络接管已失效，请先暂停下载。\n排除代理冲突后，全部关闭并重新开启接管。",
                "接管失效", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    internal void SimulateOwnershipLoss()
    {
        if (router != null) { router.OwnershipProbe = () => false; RefreshCards(); }
    }
    internal void PreviewClashBinding()
    {
        if (testMode) PaintClashBinding(new(new("WLAN", "以太网", true, true, 356, 372, 261, 261),
            true, true, "代理节点走 Wi-Fi；DIRECT 与普通直连优先走有线。"));
    }
    async Task Initialize()
    {
        if(!testMode){passive=new PassiveTraffic();passive.Start();}
        UpdateNetworkAvailability();
        await SetAll(true,false);
        if (ipv6Available != true)
        {
            hint.Text = "未检测到 IPv6 出口：已开启的内容仍保持 IPv6 限制。关闭服务才恢复可能收费的原路径。";
            hint.ForeColor = DashboardTheme.Unknown;
            RefreshCards(false);
        }
        if (testMode)
        {
            if (ipv6Available == true) network.Text = "界面测试 · 不进行公网探测";
            split.Status = "已关闭 · Clash Verge";
            split.Invalidate(); return;
        }
        if (closing) return;
        if (!closing) { await RefreshClashBinding(); SetControlsEnabled(); clashTimer.Start(); }
    }
    void UpdateNetworkAvailability()
    {
        nextNetworkCheck = DateTime.UtcNow.AddSeconds(5);
        try
        {
            ipv6Available = HasIPv6();
            network.Text = ipv6Available == true
                ? "本机有 IPv6 出口 · 不代表资源服务器可达"
                : "本机无 IPv6 出口 · 原路径可能使用收费 IPv4";
            network.ForeColor = ipv6Available == true ? DashboardTheme.Muted : DashboardTheme.Unknown;
        }
        catch
        {
            ipv6Available = null;
            network.Text = "本机 IPv6 状态读取失败 · 尚未验证连通性";
        }
    }
    async Task RefreshClashBinding(Func<Task<SplitState>>? isolatedReader = null)
    {
        if ((testMode && isolatedReader == null) || closing || clashBusy || clashPolling || busy) return;
        if (!testMode && isolatedReader != null) throw new InvalidOperationException("Isolated reader requires UI test mode.");
        clashPolling = true; SetControlsEnabled();
        try { await ReadAndPaintClashBinding(isolatedReader); }
        finally { clashPolling = false; SetControlsEnabled(); }
    }
    async Task ReadAndPaintClashBinding(Func<Task<SplitState>>? isolatedReader = null)
    {
        try
        {
            var state = await (isolatedReader == null ? clashBinding.ReadAsync() : isolatedReader());
            if (!closing) PaintClashBinding(state);
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                // A failed read must not erase the last confirmed switch state.
                split.Status = ClashError(ex); split.Confirmed = false;
                split.Toggle.Mixed = true; split.Toggle.Invalidate(); split.Invalidate();
                tooltips.SetToolTip(split, split.Status);
            }
        }
    }
    static string ClashError(Exception ex) => ex switch
    {
        HttpRequestException => "无法连接 Clash Verge，请先启动",
        OperationCanceledException => "Clash Verge 请求超时，请重试",
        JsonException => "Clash 返回内容无法解析",
        IOException => "Clash 配置正在变动，请稍后重试",
        _ => ex is InvalidOperationException or TimeoutException ? ex.Message : "读取失败，请检查 Clash Verge 和网卡"
    };
    void PaintClashBinding(SplitState state)
    {
        split.Confirmed = true;
        split.Toggle.Checked = state.Active;
        split.Toggle.Mixed = state.Managed && !state.Active;
        split.BindingOnly = state.Active && !state.Managed;
        split.EthernetName = string.IsNullOrWhiteSpace(state.Network.EthernetName) ? "以太网" : state.Network.EthernetName;
        split.WifiName = string.IsNullOrWhiteSpace(state.Network.WifiName) ? "Wi-Fi" : state.Network.WifiName;
        split.WiredOnline = state.Network.EthernetConnected; split.WifiOnline = state.Network.WifiConnected;
        split.Status = state.Active ? state.Managed ? "已开启 · 代理 Wi-Fi / 直连有线" : "仅 Wi-Fi 绑定 · 再次点击解除"
            : state.Managed ? "分流状态已变化 · 点击恢复" : "已关闭 · Clash Verge";
        tooltips.SetToolTip(split, state.Message);
        split.Toggle.Invalidate(); split.Invalidate();
    }
    async Task ToggleClashBinding()
    {
        if (testMode || closing || clashBusy || clashPolling || busy) return;
        clashBusy = true; SetControlsEnabled();
        try
        {
            bool hasLease = SplitRouting.ReadLease() != null;
            var current = await clashBinding.ReadAsync();
            if (hasLease)
            {
                split.Status = "正在恢复原网络设置…"; split.Invalidate();
                if (SplitRouting.RestoresTun)
                {
                    await SetAll(false); hint.Text = "将恢复 Clash TUN 模式，下载接管已暂停";
                }
                await clashBinding.RestoreAsync();
            }
            else if (current.Active)
            {
                split.Status = "正在解除 Wi-Fi 绑定…"; split.Invalidate();
                await clashBinding.ClearWifiBindingAsync();
            }
            else
            {
                split.Status = "正在启用分流…"; split.Invalidate();
                if (!current.Network.WifiConnected) throw new InvalidOperationException("未找到已连接的 Wi-Fi 网卡");
                if (current.Network.DualNicReady && Program.Admin) await clashBinding.EnableAsync();
                else await clashBinding.SetWifiBindingOnlyAsync(current.Network.WifiName);
                if (router == null) await SetAll(true);
            }
            await ReadAndPaintClashBinding();
        }
        catch (Exception ex)
        {
            if (!closing) MessageBox.Show(this, ClashError(ex), "分流未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await ReadAndPaintClashBinding();
        }
        finally { clashBusy = false; SetControlsEnabled(); }
    }
    async Task SetAll(bool on,bool includeOther=true)
    {
        if (busy || closing) return;
        if (on && router?.ProtectionLost == true) { hint.Text = "请先暂停下载，全部关闭后再开启"; return; }
        var previous=counters.ToDictionary(p=>p.Key,p=>p.Value.Enabled);
        bool created=router==null;
        busy = true; SetControlsEnabled();
        try
        {
            if (router == null && on) await StartRouting(includeOther:includeOther);
            if (closing) return;
            if (router != null)
            {
                if (on) { foreach(string id in cards.Keys)router.Enable(id,includeOther||id!="global"); await SyncServices(cards.Keys); }
                else { SaveAutomaticReport(); await StopRouting(); lossNotified = false; }
            }
            foreach (var id in cards.Keys) PaintSwitch(id, on&&(includeOther||id!="global"));
            hint.ForeColor = DashboardTheme.Muted;
            hint.Text = on ? includeOther?"全部接管已开启；不支持 IPv6 的普通网站也会被阻止":"五项服务已接管；普通网站保持原路径，大文件不回退 IPv4" : "接管已关闭，新连接使用原路径；累计流量保留";
        }
        catch (Exception ex)
        {
            await Rollback(previous,created);
            hint.Text = "批量切换未完成：" + ex.Message; hint.ForeColor = DashboardTheme.Error;
        }
        finally { busy = false; SetControlsEnabled(); RefreshCards(false); }
    }
    async Task Switch(string id, bool on)
    {
        if (busy || closing) return;
        if (on && router?.ProtectionLost == true)
        {
            busy = true; cards[id].Toggle.Checked = router.Counters[id].Enabled; busy = false;
            SyncMaster(); hint.Text = "接管失效：请先暂停下载，全部关闭后再开启"; return;
        }
        bool previous=counters[id].Enabled,created=router==null;
        busy = true; SetControlsEnabled();
        try
        {
            if (router == null && on) await StartRouting(id);
            if (closing) return;
            router?.Enable(id, on);
            if(router!=null)await SyncServices(new[]{id});
            if(router!=null&&!router.Counters.Values.Any(c=>c.Enabled)){SaveAutomaticReport();await StopRouting();}
            PaintSwitch(id, on);
            hint.ForeColor=DashboardTheme.Muted;
            hint.Text = on ? "该服务已开启；大文件只走 IPv6，请重新开始下载"
                : "仅该服务已关闭，新连接恢复原路径；累计流量保留";
            if(native?.Status.Contains("清理未完成")==true)hint.Text=native.Status;
        }
        catch (Exception ex)
        {
            await Rollback(new Dictionary<string,bool>{{id,previous}},created);
            hint.Text = "该服务切换失败，已尝试恢复原状态：" + ex.Message;hint.ForeColor=DashboardTheme.Error;
        }
        finally { busy = false; SetControlsEnabled(); RefreshCards(false); }
    }
    async Task StartRouting(string? selected = null,bool includeOther=true)
    {
        router = new DownloadRouter { ProductionPolicies = true };
        foreach(var pair in counters)router.Counters[pair.Key]=pair.Value;
        await router.Start(!testMode);
        foreach (var pair in router.Counters) router.Enable(pair.Key, selected == null ? includeOther||pair.Key!="global" : pair.Key == selected);
        if (!testMode) { native = new NativeCapture(); router.CoreProcessId = () => native?.ProcessId; await native.Start(router); }
    }
    async Task StopRouting()
    {
        if (native != null) { await native.DisposeAsync(); native = null; }
        if (router != null) { await router.DisposeAsync(); router = null; }
        foreach(var row in counters.Values){if(row.Enabled)Interlocked.Increment(ref row.RouteRevision);row.Enabled=false;row.State="已关闭 · 原路径";row.Rate4=row.Rate6=row.RateProxy=row.UploadRate4=row.UploadRate6=row.UploadRateProxy=0;}
    }
    async Task SyncServices(IEnumerable<string> ids)
    {
        if(testMode && SyncServicesTest!=null)await SyncServicesTest(ids);
        else if(native!=null && router!=null)await native.SyncServices(router,ids);
    }
    async Task Rollback(Dictionary<string,bool> previous,bool created)
    {
        try
        {
            if(created)await StopRouting();
            else if(router!=null)
            {
                foreach(var pair in previous)router.Enable(pair.Key,pair.Value);
                await SyncServices(previous.Keys);
            }
        }
        catch { router?.MarkCaptureLost(); }
        foreach(string key in cards.Keys)PaintSwitch(key,counters[key].Enabled);
    }
    async Task ChangeMode(string id,ServiceMode mode)
    {
        if(busy||closing)return;
        ServiceMode previous=counters[id].Mode;
        busy=true;SetControlsEnabled();
        try
        {
            if(router!=null){router.SetMode(id,mode);await SyncServices(new[]{id});}
            else counters[id].Mode=mode;
            hint.Text=cards[id].AccessibleName+(mode==ServiceMode.Mixed
                ? "混合模式已开启：仅辅助请求可用限额 IPv4，内容仍仅 IPv6"
                : "混合模式已关闭：严格 IPv6，辅助请求也不使用 IPv4");
            hint.ForeColor=DashboardTheme.Muted;
        }
        catch(Exception ex)
        {
            try
            {
                if(router!=null){router.SetMode(id,previous);await SyncServices(new[]{id});}
                else counters[id].Mode=previous;
            }
            catch{router?.MarkCaptureLost();}
            hint.Text="模式切换未完成："+ex.Message;hint.ForeColor=DashboardTheme.Error;
        }
        finally
        {
            cards[id].MixedToggle.Checked=counters[id].Mode==ServiceMode.Mixed;
            busy=false;SetControlsEnabled();RefreshCards(false);
        }
    }
    const string UsageText = """
        快速上手
        暂停现有下载，打开助手后重新开始下载或刷新视频。

        服务开关
        每个服务右上角是接管开关，下面是独立的混合模式开关。混合开：只有列明的登录、列表、取链接等辅助域名可用限额 IPv4；混合关：辅助请求也只能用 IPv6。两种模式的视频、下载、未知域名都仅用 IPv6，失败不会偷偷回退 IPv4。
        本机没有可用 IPv6 出口时，已开启服务继续保持内容 IPv6 限制，并明确提示不可用，不会自动关闭保护。主动关闭该服务才恢复原路径，可能使用收费 IPv4。状态检查只读取本机路由，不发送公网探测。

        混合保护额度
        每服务初始 2 MiB 原路径辅助流量（收发合计，代理出口未知也扣额度）；用尽会阻止辅助请求，可能导致登录/取下载链接失败。可点击卡片额度旁“补充”，确认后增加 2 MiB，不会自动增加或放开内容 IPv4。清零或切换开关不会恢复额度。这是中继字节保护，不是校园网计费保证；HTTPS 无法按文件大小准确分流。
        默认仅开启五项内容服务，“其他网站”保持关闭、恢复原网络。主动全部开启会连普通网站也限制 IPv6。

        流量
        各服务显示 IPv4 / IPv6 / 未知的累计下载和当前下载速度，顶部另有上传统计。关闭接管仍继续监测。夸克、Steam、Epic 原生进程按进程归属；浏览器与本机 AList 按本进程近期 DNS 结果关联，未识别的部分明确提示，不把全部浏览器或 AList 流量算作同一服务。本机播放器到 AList 不重复计量。共享 IP、加密 DNS、本机代理等可能导致漏计；经 TUN 无法确认最终出口时标记未知。不等于整机或运营商账单。

        有线 / Wi-Fi 分流
        默认关闭。完整分流：直连走有线，代理节点走 Wi-Fi，关闭或退出时恢复；异常退出由恢复进程处理。
        仅 Wi-Fi 绑定：DIRECT 同样受绑定影响，需再次点击解除，退出不会自动清除。

        退出与诊断
        退出前暂停下载。详细诊断可查看连接；导出报告包含平台 CDN 域名、流量与接管状态，不记录 URL、Cookie、Token 或文件名。
        自动记录内核高 CPU、连续失败和低速疑似现场；低速记录不能区分正常暂停/缓冲。卡顿时可直接点“记录卡顿”，约 10 秒后在底部查看保存结果，点击可打开报告目录。不需要下载链接，不测速、不切换节点。
        """;
    object Report() => new
    {
        schema = "ipv6-download-assistant/3", version = Version, timeUtc = DateTime.UtcNow,
        protectionLost = router?.ProtectionLost, nativeCapture = native?.Active ?? false, network = network.Text,
        nativeExitCode = native?.ExitCode, routerHealth = router?.Health(),
        runtimeHistory = router?.RuntimeHistory(),
        scope = "Enabled: relay stream bytes. Mixed permits only exact auxiliary hosts with a shared per-service 2 MiB allowance; grants require confirmation. All content/unknown hosts remain IPv6-only, including when local IPv6 is unavailable. Disabling restores the possibly paid original path. Disabled: passive native process public bytes or same-process recent DNS correlation for browser/AList; incomplete under encrypted DNS, shared IPs and local proxies. Loopback AList is excluded. TUN/proxy exit is unknown. Not campus billing.",
        recentRoutes=router?.RecentRoutes(),
        passiveStatus=passive?.Status,passiveLostEvents=passive?.Lost,
        services = counters.Select(p => new
        {
            id = p.Key, enabled = p.Value.Enabled, mode=p.Value.Mode.ToString(),
            auxiliaryUsed= p.Value.Auxiliary.Used,auxiliaryLimit=p.Value.Auxiliary.Capacity,
            ipv4 = Interlocked.Read(ref p.Value.V4),
            ipv6 = Interlocked.Read(ref p.Value.V6), proxyUnknown = Interlocked.Read(ref p.Value.Proxy),
            ipv4RateBps = p.Value.Rate4, ipv6RateBps = p.Value.Rate6, proxyUnknownRateBps = p.Value.RateProxy,
            ipv4Uploaded = Interlocked.Read(ref p.Value.Upload4), ipv6Uploaded = Interlocked.Read(ref p.Value.Upload6),
            proxyUnknownUploaded = Interlocked.Read(ref p.Value.UploadProxy),
            ipv4UploadRateBps = p.Value.UploadRate4, ipv6UploadRateBps = p.Value.UploadRate6,
            proxyUnknownUploadRateBps = p.Value.UploadRateProxy,
            failures = Interlocked.Read(ref p.Value.Failures), state = p.Value.State, lastFailure = p.Value.LastFailure
        }).ToArray(), servers = router?.ServerReport()
    };
    void WriteReport(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Report(), new JsonSerializerOptions { WriteIndented = true }));
    void SaveAutomaticReport()
    {
        if (testMode || router == null) return;
        try
        {
            lastSavedReport = DiagnosticReportStore.Write(ReportsDirectory, "automatic", Report());
            reportWriteError = null;
        }
        catch (Exception ex) { reportWriteError = ex.GetType().Name; diagnosticStatus.Text = "诊断保存失败：" + reportWriteError; }
    }
    void RecordSlowdown()
    {
        if (router != null)
        {
            router.RequestSlowReport();
            hint.Text = "已标记卡顿，后台正在记录；无需测速或提供链接";
            RefreshCards(false);
        }
        else
        {
            try
            {
                lastSavedReport = DiagnosticReportStore.Write(ReportsDirectory, "automatic",
                    new { trigger = "user_reported_slowdown_without_capture", report = Report() });
                reportWriteError = null;
                diagnosticStatus.Text = "诊断已保存：" + lastSavedReport + " · 点击打开目录";
            }
            catch (Exception ex) { reportWriteError = ex.GetType().Name; diagnosticStatus.Text = "诊断保存失败：" + reportWriteError; }
        }
    }
    void Export()
    {
        using var save = new SaveFileDialog { Filter = "JSON 诊断报告|*.json", FileName = "IPv6-download-report.json" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try { WriteReport(save.FileName); hint.Text = "诊断报告已导出"; }
        catch (Exception ex) { hint.Text = "导出失败：" + ex.GetType().Name; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); clashTimer.Dispose(); tooltips.Dispose();
            passive?.Dispose();passive=null;
            var image = appIcon.Image; appIcon.Image = null; image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
