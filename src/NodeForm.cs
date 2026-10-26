using System.Net;

namespace IPv6Lens;

internal sealed class NodeForm : Form
{
    readonly ContentNodes nodes;
    readonly ComboBox service = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox host = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox order = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly TextBox url = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly TextBox headers = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label current = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly List<Button> workButtons = new();
    readonly Button cancel;
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    CancellationTokenSource? pending;
    string displayedHost = "";
    string? focusedNode;
    bool closing;
    bool applyingPreset;
    static readonly (string Label, string Site, string Host)[] Presets =
    {
        ("夸克网盘", "quark", "dl-pc-zb.drive.quark.cn"),
        ("Bilibili", "bilibili", "cn-sdjn-cm-02-02.bilivideo.com"),
        ("Steam", "steam", "dl.steam.clngaa.com"),
        ("自定义域名", "", "")
    };
    internal NodeForm(ContentNodes nodes, IEnumerable<string> knownHosts)
    {
        this.nodes = nodes;
        Text = "IPv6 节点"; Font = new Font("Microsoft YaHei UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(940, 640); MinimumSize = new(760, 550);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = DashboardTheme.Background; ForeColor = DashboardTheme.Text;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16) };
        foreach (float height in new[] { 128f, 82f }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (float height in new[] { 32f, 48f, 48f, 0f }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Field(string label, Control control, int row)
        {
            fields.RowStyles.Add(new(SizeType.Percent, 25f));
            fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            fields.Controls.Add(control, 1, row);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        Field("快速选择", service, 0);
        Field("内容域名", host, 1);
        Field("测速链接（速度）", url, 2);
        Field("请求头（可选）", headers, 3);
        service.AccessibleName = "服务类型";
        host.AccessibleName = "内容域名";
        url.AccessibleName = "有效HTTPS测速链接";
        headers.AccessibleName = "测速请求头JSON";
        tips.SetToolTip(host, "可选择已经观察到的服务器，也可以切到“自定义域名”后直接输入。");
        tips.SetToolTip(url, "夸克 / AList 可直接点“AList 文件择优”，不用填链接；其他来源可在这里填真实文件 HTTPS 地址。");
        tips.SetToolTip(headers, "例如 {\"User-Agent\":\"...\"}；可含 Cookie、Referer、Origin、Authorization，仅本次内存使用。");
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        Button Add(FlowLayoutPanel parent, string text, Func<Task> action, int width = 116)
        {
            var button = new DashboardButton(text, text, async (_, _) => await action()) { Width = width, Height = 34 };
            parent.Controls.Add(button); workButtons.Add(button); return button;
        }
        var refresh = Add(toolbar, "刷新", () => Work(async ct =>
        {
            displayedHost = ContentNodes.Host(host.Text); host.Text = displayedHost;
            await nodes.Refresh(displayedHost, ct);
            focusedNode = null;
            status.Text = nodes.Snapshot(displayedHost).Length == 0
                ? "没有可用的公网 IPv6 节点"
                : "节点已更新；可直接选中地址试用，也可以先测延迟或下载速度";
        }, requireRows: false), 64);
        refresh.AccessibleName = "刷新节点"; tips.SetToolTip(refresh, "刷新 IPv6 节点");
        Add(toolbar, "测延迟", () => Measure(NodeOrder.Latency));
        Add(toolbar, "测下载速度", () => Measure(NodeOrder.Speed), 112);
        var guide = new DashboardButton("怎么测速", "AList 文件择优或手动测速说明", (_, _) => ShowSpeedGuide())
        { Width = 92, Height = 34 };
        toolbar.Controls.Add(guide); workButtons.Add(guide);
        order.Items.AddRange(new object[] { "延迟最低优先", "速度最快优先" });
        order.SelectedIndex = 0; toolbar.Controls.Add(order);
        Add(toolbar, "AList 文件择优", FromAlist, 132);
        Add(toolbar, "一键择优", OptimizeCurrent, 96);
        cancel = new DashboardButton("取消", "取消节点测试", (_, _) => pending?.Cancel()) { Width = 74, Enabled = false };
        toolbar.Controls.Add(cancel);
        tips.SetToolTip(workButtons[1], "不需要下载链接；测量TCP连接和TLS握手总时延。");
        tips.SetToolTip(workButtons[2], "需要把当前域名实际文件的HTTPS下载链接粘贴到上方；不是网页地址。");
        tips.SetToolTip(workButtons[4], "登录本机 AList 管理员账号后选择夸克文件，自动取原始链接；最多读取6MiB正文，不改画质或AList设置。");
        tips.SetToolTip(workButtons[5], "用上方链接比较最多3个IPv6地址，短期记忆最快样本；只影响新连接，失败不回退IPv4。");
        foreach (var column in new[] { ("ip", "IPv6 地址", 35f, 260), ("latency", "TLS 延迟", 13f, 90),
            ("speed", "下载 MB/s", 14f, 95), ("ttl", "DNS有效期", 10f, 75), ("state", "状态 / 来源", 28f, 150) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Item1, HeaderText = column.Item2,
                FillWeight = column.Item3, MinimumWidth = column.Item4, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.BackgroundColor = DashboardTheme.Panel; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = DashboardTheme.Raised, ForeColor = DashboardTheme.Text };
        grid.DefaultCellStyle = new() { BackColor = DashboardTheme.Panel, ForeColor = DashboardTheme.Text,
            SelectionBackColor = DashboardTheme.Line, SelectionForeColor = DashboardTheme.Text };
        grid.GridColor = DashboardTheme.Line; grid.RowTemplate.Height = 32;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        Add(footer, "使用所选节点", () => Work(ct =>
        {
            if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not IPAddress ip)
                throw new InvalidOperationException("请先选中一行 IPv6 地址");
            nodes.Select(displayedHost, ip);
            focusedNode = ip.ToString();
            var measured = nodes.Snapshot(displayedHost).FirstOrDefault(n => n.Dns.Address.Equals(ip));
            status.Text = measured?.LatencyMs.HasValue == true || measured?.BytesPerSecond.HasValue == true
                ? "已选中该节点；新的 HTTPS/443 内容连接会使用它"
                : "已选中未测速节点；新的 HTTPS/443 内容连接会直接尝试它，失败不会回退IPv4";
            return Task.CompletedTask;
        }));
        Add(footer, "使用最快节点", () => Work(ct =>
        {
            nodes.Best(displayedHost, SortOrder);
            focusedNode = nodes.Selected(displayedHost)?.ToString();
            status.Text = SortOrder == NodeOrder.Speed ? "已选择实测下载速度最快的有效节点" : "已选择 TLS 延迟最低的有效节点";
            return Task.CompletedTask;
        }));
        Add(footer, "恢复自动", () => Work(ct =>
        {
            nodes.Automatic(displayedHost); focusedNode = null; status.Text = "已恢复自动解析；已有连接保持不变";
            return Task.CompletedTask;
        }, requireRows: false));
        layout.Controls.Add(fields, 0, 0); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(current, 0, 3); layout.Controls.Add(footer, 0, 4); layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);
        foreach (var control in new Control[] { service, host, url, headers, order })
        { control.BackColor = DashboardTheme.Raised; control.ForeColor = DashboardTheme.Text; }
        var observedHosts = knownHosts.Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var item in Presets) service.Items.Add(item.Label);
        foreach (var name in observedHosts.Concat(Presets.Select(p => p.Host))
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            host.Items.Add(name);
        service.SelectedIndexChanged += (_, _) => ApplyPreset(observedHosts);
        service.SelectedIndex = 0;
        status.Text = nodes.InterfaceIndex > 0
            ? "夸克 / AList：点“AList 文件择优”后选视频；其他来源可刷新后直接选地址试用"
            : "未找到可用的物理 IPv6 出口，请关闭接管后重新开启";
        host.TextChanged += (_, _) =>
        {
            if (host.Text.Equals(displayedHost, StringComparison.OrdinalIgnoreCase)) return;
            if (!applyingPreset && service.SelectedIndex != 3) service.SelectedIndex = 3;
            url.Clear(); headers.Clear(); grid.Rows.Clear(); displayedHost = ""; focusedNode = null;
            current.Text = "当前：自动";
        };
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is IPAddress ip)
                focusedNode = ip.ToString();
        };
        order.SelectedIndexChanged += (_, _) => PaintRows();
        clock.Tick += (_, _) => { if (pending == null) UpdateTtl(); };
        Shown += (_, _) => clock.Start();
        FormClosing += (_, e) =>
        {
            if (pending != null) { e.Cancel = true; closing = true; pending.Cancel(); }
            else { clock.Stop(); url.Clear(); headers.Clear(); }
        };
    }
    async Task FromAlist()
    {
        if (pending != null) return;
        if (nodes.InterfaceIndex <= 0) { status.Text = "没有可用的物理 IPv6 出口"; return; }
        using var picker = new AlistResourceForm();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Resource is not AlistResource resource) return;
        url.Clear(); headers.Clear();
        host.Text = resource.Url.IdnHost;
        displayedHost = ContentNodes.Host(host.Text);
        try { await Optimize(resource.Url, resource.Headers); }
        finally { resource.Headers.Clear(); }
    }
    Task OptimizeCurrent() => Work(async ct =>
    {
        displayedHost = ContentNodes.Host(host.Text);
        var resource = ContentNodes.SpeedUrl(displayedHost, url.Text);
        await OptimizeCore(resource, ContentNodes.Headers(headers.Text), ct);
    }, requireRows: false);
    Task Optimize(Uri resource, IReadOnlyDictionary<string, string> requestHeaders) =>
        Work(ct => OptimizeCore(resource, requestHeaders, ct), requireRows: false);
    async Task OptimizeCore(Uri resource, IReadOnlyDictionary<string, string> requestHeaders, CancellationToken ct)
    {
        if (nodes.InterfaceIndex <= 0) throw new InvalidOperationException("没有可用的物理 IPv6 出口");
        status.Text = "正在比较下载速度（最多读取 6 MiB 正文，仅 IPv6）";
        var progress = new Progress<int>(count =>
        {
            if (IsDisposed || closing || pending == null) return;
            status.Text = $"已比较 {count} 个节点（最多 3 个）"; PaintRows();
        });
        var result = await nodes.Optimize(displayedHost, resource, requestHeaders, progress, ct);
        order.SelectedIndex = 1;
        focusedNode = result.Address.ToString();
        status.Text = $"已择优：{result.BytesPerSecond / 1e6:F2} MB/s 短样本；新连接生效，已有连接不打断";
    }
    void ApplyPreset(string[] observedHosts)
    {
        int index = service.SelectedIndex;
        if (index < 0 || index >= Presets.Length) return;
        applyingPreset = true;
        try
        {
            if (index == Presets.Length - 1)
            {
                host.Text = "";
                status.Text = "请输入自定义域名，然后点刷新";
                host.Focus();
                return;
            }
            var preset = Presets[index];
            string selected = observedHosts.FirstOrDefault(h =>
                string.Equals(DownloadRouter.ContentSite(h), preset.Site, StringComparison.OrdinalIgnoreCase))
                ?? preset.Host;
            host.Text = selected;
            status.Text = preset.Label + "：先点刷新；也可以直接选中节点试用";
        }
        finally { applyingPreset = false; }
    }
    void ShowSpeedGuide()
    {
        MessageBox.Show(this,
            "夸克 / 爆米花通过本机 AList：\r\n" +
            "1. 开启夸克接管，点“AList 文件择优”。\r\n" +
            "2. 用本机 AList 管理员账号登录（仅连接 127.0.0.1:5244），打开文件夹并选择要看的原视频。不需要自己找链接。\r\n" +
            "3. 自动比较最多3个IPv6地址，每个最多读取2MiB正文。账号、密码、链接和请求头不保存；请求及TLS开销另计。\r\n" +
            "4. 显示“已择优”后，重新发起播放或下载，新连接才会用择优地址；不主动打断已有播放。按域名和当前网络最多记忆5分钟，到期恢复自动，不后台重复测速。\r\n\r\n" +
            "不想测速：点“刷新”，选中地址后点“使用所选节点”即可试用。\r\n" +
            "其他来源手动测速：填写与上方内容域名一致的真实文件 HTTPS/443 链接，再点“一键择优”或“测下载速度”。网页首页、分享页不能测速。\r\n\r\n" +
            "Bilibili：播放视频后，在网络请求中找视频文件请求（通常是 .m4s 或 .mp4），复制 URL。\r\n" +
            "Steam：Steam客户端不直接显示文件 URL，需要使用能显示实际网络请求的下载工具；只测延迟即可直接选节点。\r\n" +
            "签名链接是文件的临时通行证，过期后重新选文件即可；不是IPv6节点只能用这么久。测速失败不会放开内容IPv4，也不降低画质。\r\n\r\n" +
            "短样本用于比较当前候选，不能保证其他文件或持续播放同样快。没有链接也可以测延迟或直接试用节点。",
            "怎么测速", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    void UpdateTtl()
    {
        if (displayedHost.Length == 0 || IsDisposed) return;
        var rows = ContentNodes.Rank(nodes.Snapshot(displayedHost), SortOrder);
        bool same = rows.Length == grid.Rows.Count;
        for (int i = 0; same && i < rows.Length; i++)
            same = grid.Rows[i].Tag is IPAddress ip && ip.Equals(rows[i].Dns.Address);
        if (!same) { PaintRows(); return; }
        for (int i = 0; i < rows.Length; i++)
            grid.Rows[i].Cells["ttl"].Value = $"{Math.Max(0, (int)(rows[i].Dns.ExpiresUtc - nodes.Now()).TotalSeconds)} 秒";
        current.Text = SelectionText();
    }
    NodeOrder SortOrder => order.SelectedIndex == 1 ? NodeOrder.Speed : NodeOrder.Latency;
    async Task Measure(NodeOrder mode)
    {
        await Work(async ct =>
        {
            if (nodes.InterfaceIndex <= 0) throw new InvalidOperationException("没有物理 IPv6 出口，请全部关闭后重新开启接管");
            Uri? resource = mode == NodeOrder.Speed ? ContentNodes.SpeedUrl(displayedHost, url.Text) : null;
            var requestHeaders = mode == NodeOrder.Speed ? ContentNodes.Headers(headers.Text) : new Dictionary<string, string>();
            int total = mode == NodeOrder.Speed ? Math.Min(ContentNodes.SampleCount, nodes.Snapshot(displayedHost).Length) :
                nodes.Snapshot(displayedHost).Length;
            var progress = new Progress<int>(count =>
            {
                if (IsDisposed || closing || pending == null) return;
                status.Text = $"已测试 {count} / {total}"; PaintRows();
            });
            status.Text = mode == NodeOrder.Speed ? "正在测试下载速度（最多读取6MiB正文，仅IPv6，可取消）" : "正在测试 TLS 延迟";
            await nodes.Measure(displayedHost, mode, resource, requestHeaders, progress, ct);
            order.SelectedIndex = mode == NodeOrder.Speed ? 1 : 0;
            status.Text = "测试结束；仅有效且未过期的结果参与排序";
        });
    }
    async Task Work(Func<CancellationToken, Task> work, bool requireRows = true)
    {
        if (pending != null) return;
        using var cancelSource = new CancellationTokenSource();
        pending = cancelSource; SetWorking(true);
        try
        {
            if (requireRows && (displayedHost.Length == 0 || nodes.Snapshot(displayedHost).Length == 0))
                throw new InvalidOperationException("请先刷新当前域名的节点");
            await work(cancelSource.Token);
        }
        catch (OperationCanceledException) { status.Text = "已取消；已完成的测试结果保留"; }
        catch (NodeRefusedException ex) { status.Text = $"资源拒绝测速（HTTP {ex.StatusCode}），已停止；请检查链接或授权"; }
        catch (InvalidOperationException ex) { status.Text = ex.Message; }
        catch (Exception) { status.Text = "操作未完成，请检查 IPv6 出口或重新刷新"; }
        finally
        {
            pending = null; SetWorking(false); PaintRows();
            if (closing) Close();
        }
    }
    void SetWorking(bool working)
    {
        foreach (var button in workButtons) button.Enabled = !working;
        service.Enabled = host.Enabled = url.Enabled = headers.Enabled = order.Enabled = !working;
        cancel.Enabled = working;
    }
    void PaintRows()
    {
        if (displayedHost.Length == 0 || IsDisposed) return;
        int scrollRow = grid.FirstDisplayedScrollingRowIndex;
        grid.Rows.Clear();
        int selectedIndex = -1;
        var rows = ContentNodes.Rank(nodes.Snapshot(displayedHost), SortOrder);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            int index = grid.Rows.Add(row.Dns.Address.ToString(), row.LatencyMs is double ms ? $"{ms:F0} ms" : "未测",
                row.BytesPerSecond is double speed ? $"{speed / 1e6:F2}" : "未测",
                $"{Math.Max(0, (int)(row.Dns.ExpiresUtc - nodes.Now()).TotalSeconds)} 秒", row.Status + " / " + row.Dns.Source);
            grid.Rows[index].Tag = row.Dns.Address;
            if (row.Dns.Address.ToString() == focusedNode) selectedIndex = index;
        }
        grid.ClearSelection();
        if (selectedIndex >= 0)
        {
            grid.Rows[selectedIndex].Selected = true;
            grid.CurrentCell = grid.Rows[selectedIndex].Cells[0];
        }
        if (scrollRow >= 0 && scrollRow < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = scrollRow;
        current.Text = SelectionText();
    }
    string SelectionText() => nodes.Selected(displayedHost) is IPAddress chosen
            ? "当前已选择：" + chosen + "（新建HTTPS/443连接生效）"
            : nodes.Preferred(displayedHost) is IPAddress preferred
            ? "自动择优：" + preferred + "（仅新连接，最多记忆5分钟并复核DNS）"
            : "当前：自动（可直接选中地址试用）";
    protected override void Dispose(bool disposing)
    {
        if (disposing) { clock.Dispose(); tips.Dispose(); pending?.Cancel(); }
        base.Dispose(disposing);
    }
    internal static void UiTest(string prefix)
    {
        var nodes = new ContentNodes { InterfaceIndex = 17, NetworkIdentity = () => "fixture-network" };
        const string domain = "dl-test.quark.cn";
        var expiry = DateTime.UtcNow.AddMinutes(3);
        nodes.Lookup = (_, _) => Task.FromResult(Enumerable.Range(1, 6).Select(i =>
            new NodeAddress(IPAddress.Parse("2001:db8:abcd:1005:3::" + i), expiry, "测试 DNS")).ToArray());
        nodes.LatencyProbe = (_, ip, _) => Task.FromResult((double)ip.GetAddressBytes()[15] * 12);
        nodes.SpeedProbe = (_, ip, _, _) => Task.FromResult(new NodeSpeed(10_000_000d / ip.GetAddressBytes()[15], "Range 完整"));
        using var form = new NodeForm(nodes, new[] { domain }) { ShowInTaskbar = false, Opacity = 0 };
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        form.Shown += async (_, _) =>
        {
            try
            {
                form.clock.Stop();
                Check(form.service.Items.Cast<string>().SequenceEqual(new[] { "夸克网盘", "Bilibili", "Steam", "自定义域名" }),
                    "Service presets include Quark, Bilibili, Steam, and custom host");
                await form.Work(async ct => { form.displayedHost = domain; await nodes.Refresh(domain, ct); }, false);
                Check(form.grid.Rows.Count == 6, "Node refresh populates all candidate rows");
                var untested = (IPAddress)form.grid.Rows[0].Tag!;
                form.grid.Rows[0].Selected = true;
                await form.Work(_ =>
                {
                    nodes.Select(domain, untested);
                    form.focusedNode = untested.ToString();
                    return Task.CompletedTask;
                });
                Check(nodes.Selected(domain)?.Equals(untested) == true, "Untested candidate can be selected for direct trial");
                form.UpdateTtl();
                Check(form.grid.SelectedRows.Count == 1 &&
                    form.grid.SelectedRows[0].Tag is IPAddress selected && selected.Equals(untested),
                    "TTL refresh preserves the selected candidate");
                form.url.Text = "https://" + domain + "/fixture?private=value";
                await form.Measure(NodeOrder.Latency);
                await form.Measure(NodeOrder.Speed);
                Check(form.order.SelectedIndex == 1 && form.grid.Rows[0].Cells["speed"].Value?.ToString() == "10.00",
                    "Speed test updates the grid and orders real measurements descending");
                Check(form.url.UseSystemPasswordChar && form.headers.UseSystemPasswordChar, "Resource URLs and headers are masked");
                nodes.Best(domain, NodeOrder.Speed); form.PaintRows();
                Check(form.current.Text.Contains("::1"), "Chosen node is visible in the current selection status");
                await form.OptimizeCurrent();
                Check(form.current.Text.StartsWith("自动择优：") && form.status.Text.StartsWith("已择优：") &&
                    nodes.Selected(domain) == null && nodes.Preferred(domain) != null,
                    "One-click optimization reports the applied choice distinctly from manual selection");
                Check(form.workButtons.Any(b => b.Text == "AList 文件择优"),
                    "The local AList file picker is available without a copied signed URL");
                foreach (int width in new[] { 940, 760 })
                {
                    form.ClientSize = new(width * form.DeviceDpi / 96, 640 * form.DeviceDpi / 96);
                    form.PerformLayout();
                    Check(form.grid.Height > 100 && form.status.Bottom <= form.ClientSize.Height &&
                        form.current.Width > 300, $"Node panel retains usable grid and status at width {width}");
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(prefix + "." + width + ".png");
                }
                var pending = new TaskCompletionSource<double>();
                nodes.LatencyProbe = (_, _, token) => pending.Task.WaitAsync(token);
                var test = form.Measure(NodeOrder.Latency);
                await Task.Delay(10);
                Check(!form.host.Enabled && form.cancel.Enabled && form.workButtons.All(b => !b.Enabled),
                    "Active tests lock mutable inputs while cancellation stays available");
                form.cancel.PerformClick();
                await test;
                Check(form.pending == null && form.host.Enabled && !form.cancel.Enabled, "Cancel restores all controls");
                form.host.Text = "test.bilivideo.com";
                Check(form.url.Text == "" && form.headers.Text == "" && form.grid.Rows.Count == 0,
                    "Changing hostname clears credentials and old node rows");
                File.WriteAllText(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new { state = "passed", checks }));
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                File.WriteAllText(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }));
            }
            finally { form.Close(); }
        };
        Application.Run(form);
    }
}
