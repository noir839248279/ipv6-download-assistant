using System.Net;

namespace IPv6Lens;

internal sealed class NodeForm : Form
{
    readonly ContentNodes nodes;
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
    bool closing;
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
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Field(string label, Control control, int row)
        {
            fields.RowStyles.Add(new(SizeType.Percent, 33.333f));
            fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            fields.Controls.Add(control, 1, row);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        Field("内容域名", host, 0);
        Field("测速链接", url, 1);
        Field("请求头（可选）", headers, 2);
        host.AccessibleName = "内容域名"; url.AccessibleName = "有效HTTPS测速链接"; headers.AccessibleName = "测速请求头JSON";
        tips.SetToolTip(url, "当前域名的有效资源链接，仅用于本次测速，不保存。");
        tips.SetToolTip(headers, "例如 {\"User-Agent\":\"...\"}；可含 Cookie、Referer、Origin、Authorization，仅本次内存使用。");
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        Button Add(FlowLayoutPanel parent, string text, Func<Task> action, int width = 116)
        {
            var button = new DashboardButton(text, text, async (_, _) => await action()) { Width = width, Height = 34 };
            parent.Controls.Add(button); workButtons.Add(button); return button;
        }
        var refresh = Add(toolbar, "↻", () => Work(async ct =>
        {
            displayedHost = ContentNodes.Host(host.Text); host.Text = displayedHost;
            await nodes.Refresh(displayedHost, ct);
            status.Text = nodes.Snapshot(displayedHost).Length == 0 ? "没有可用的公网 IPv6 节点" : "节点已更新";
        }, requireRows: false), 44);
        refresh.AccessibleName = "刷新节点"; tips.SetToolTip(refresh, "刷新 IPv6 节点");
        Add(toolbar, "测延迟", () => Measure(NodeOrder.Latency));
        Add(toolbar, "测速度", () => Measure(NodeOrder.Speed));
        order.Items.AddRange(new object[] { "延迟最低优先", "速度最快优先" });
        order.SelectedIndex = 0; toolbar.Controls.Add(order);
        cancel = new DashboardButton("取消", "取消节点测试", (_, _) => pending?.Cancel()) { Width = 74, Enabled = false };
        toolbar.Controls.Add(cancel);
        tips.SetToolTip(workButtons[1], "测量TCP连接和TLS握手总时延，不是ICMP ping。");
        tips.SetToolTip(workButtons[2], "同一链接顺序测速，最多8节点、每节点4MiB或10秒；403/429停止。");
        foreach (var column in new[] { ("ip", "IPv6 地址", 35f, 260), ("latency", "TLS 延迟", 13f, 90),
            ("speed", "下载 MB/s", 14f, 95), ("ttl", "有效期", 10f, 65), ("state", "状态 / 来源", 28f, 150) })
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
                throw new InvalidOperationException("请选择一个已通过测试的节点");
            nodes.Select(displayedHost, ip); status.Text = "已选择节点，仅该域名新的 HTTPS/443 内容连接生效";
            return Task.CompletedTask;
        }));
        Add(footer, "使用最快节点", () => Work(ct =>
        {
            nodes.Best(displayedHost, SortOrder);
            status.Text = SortOrder == NodeOrder.Speed ? "已选择实测下载速度最快的有效节点" : "已选择 TLS 延迟最低的有效节点";
            return Task.CompletedTask;
        }));
        Add(footer, "恢复自动", () => Work(ct =>
        {
            nodes.Automatic(displayedHost); status.Text = "已恢复自动解析；已有连接保持不变";
            return Task.CompletedTask;
        }, requireRows: false));
        layout.Controls.Add(fields, 0, 0); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(current, 0, 3); layout.Controls.Add(footer, 0, 4); layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);
        foreach (var control in new Control[] { host, url, headers, order })
        { control.BackColor = DashboardTheme.Raised; control.ForeColor = DashboardTheme.Text; }
        foreach (var name in knownHosts.Concat(new[] { "dl-pc-zb.drive.quark.cn" }).Distinct(StringComparer.OrdinalIgnoreCase))
            host.Items.Add(name);
        if (host.Items.Count > 0) host.SelectedIndex = 0;
        status.Text = nodes.InterfaceIndex > 0 ? "等待刷新节点；未测试的节点不参与择优" : "未找到可用的物理 IPv6 出口";
        host.TextChanged += (_, _) =>
        {
            if (host.Text.Equals(displayedHost, StringComparison.OrdinalIgnoreCase)) return;
            url.Clear(); headers.Clear(); grid.Rows.Clear(); displayedHost = "";
            current.Text = "当前：自动";
        };
        order.SelectedIndexChanged += (_, _) => PaintRows();
        clock.Tick += (_, _) => { if (pending == null) PaintRows(); };
        Shown += (_, _) => clock.Start();
        FormClosing += (_, e) =>
        {
            if (pending != null) { e.Cancel = true; closing = true; pending.Cancel(); }
            else { clock.Stop(); url.Clear(); headers.Clear(); }
        };
    }
    NodeOrder SortOrder => order.SelectedIndex == 1 ? NodeOrder.Speed : NodeOrder.Latency;
    async Task Measure(NodeOrder mode)
    {
        await Work(async ct =>
        {
            if (nodes.InterfaceIndex <= 0) throw new InvalidOperationException("没有物理 IPv6 出口，请全部关闭后重新开启接管");
            Uri? resource = mode == NodeOrder.Speed ? ContentNodes.SpeedUrl(displayedHost, url.Text) : null;
            var requestHeaders = mode == NodeOrder.Speed ? ContentNodes.Headers(headers.Text) : new Dictionary<string, string>();
            int total = nodes.Snapshot(displayedHost).Length;
            var progress = new Progress<int>(count =>
            {
                if (IsDisposed || closing || pending == null) return;
                status.Text = $"已测试 {count} / {total}"; PaintRows();
            });
            status.Text = mode == NodeOrder.Speed ? "正在测试下载速度（最多32MiB，可取消）" : "正在测试 TLS 延迟";
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
        host.Enabled = url.Enabled = headers.Enabled = order.Enabled = !working;
        cancel.Enabled = working;
    }
    void PaintRows()
    {
        if (displayedHost.Length == 0 || IsDisposed) return;
        string? focused = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag?.ToString() : null;
        int scrollRow = grid.FirstDisplayedScrollingRowIndex;
        grid.Rows.Clear();
        foreach (var row in ContentNodes.Rank(nodes.Snapshot(displayedHost), SortOrder))
        {
            int index = grid.Rows.Add(row.Dns.Address.ToString(), row.LatencyMs is double ms ? $"{ms:F0} ms" : "未测",
                row.BytesPerSecond is double speed ? $"{speed / 1e6:F2}" : "未测",
                $"{Math.Max(0, (int)(row.Dns.ExpiresUtc - nodes.Now()).TotalSeconds)} 秒", row.Status + " / " + row.Dns.Source);
            grid.Rows[index].Tag = row.Dns.Address;
            grid.Rows[index].Selected = row.Dns.Address.ToString() == focused;
        }
        if (focused == null) grid.ClearSelection();
        if (scrollRow >= 0 && scrollRow < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = scrollRow;
        current.Text = nodes.Selected(displayedHost) is IPAddress chosen
            ? "当前：" + chosen + "（本次会话）" : "当前：自动（未选择或节点已过期）";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { clock.Dispose(); tips.Dispose(); pending?.Cancel(); }
        base.Dispose(disposing);
    }
    internal static void UiTest(string prefix)
    {
        var nodes = new ContentNodes { InterfaceIndex = 17 };
        const string domain = "dl-test.quark.cn";
        nodes.Lookup = (_, _) => Task.FromResult(Enumerable.Range(1, 6).Select(i =>
            new NodeAddress(IPAddress.Parse("2001:db8:abcd:1005:3::" + i), DateTime.UtcNow.AddMinutes(3), "测试 DNS")).ToArray());
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
                await form.Work(async ct => { form.displayedHost = domain; await nodes.Refresh(domain, ct); }, false);
                Check(form.grid.Rows.Count == 6, "Node refresh populates all candidate rows");
                form.url.Text = "https://" + domain + "/fixture?private=value";
                await form.Measure(NodeOrder.Latency);
                await form.Measure(NodeOrder.Speed);
                Check(form.order.SelectedIndex == 1 && form.grid.Rows[0].Cells["speed"].Value?.ToString() == "10.00",
                    "Speed test updates the grid and orders real measurements descending");
                Check(form.url.UseSystemPasswordChar && form.headers.UseSystemPasswordChar, "Resource URLs and headers are masked");
                nodes.Best(domain, NodeOrder.Speed); form.PaintRows();
                Check(form.current.Text.Contains("::1"), "Chosen node is visible in the current selection status");
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
