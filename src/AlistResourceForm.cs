namespace IPv6Lens;

internal sealed class AlistResourceForm : Form
{
    readonly AlistResourceClient client;
    readonly TextBox username = new() { Dock = DockStyle.Fill };
    readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly TextBox otp = new() { Dock = DockStyle.Fill };
    readonly TextBox directoryPassword = new() { Width = 120, UseSystemPasswordChar = true };
    readonly TextBox location = new() { Dock = DockStyle.Fill, ReadOnly = true, Text = "/" };
    readonly ListView files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly List<Control> commands = new();
    readonly Button previous, next, cancel;
    CancellationTokenSource? pending;
    string path = "/";
    int page = 1;
    bool more, closing;
    internal AlistResource? Resource { get; private set; }
    internal AlistResourceForm(AlistResourceClient? client = null)
    {
        this.client = client ?? new();
        Text = "选择本机 AList 的夸克文件";
        Font = new("Microsoft YaHei UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(800, 540); MinimumSize = new(680, 500);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = DashboardTheme.Background; ForeColor = DashboardTheme.Text;
        username.AccessibleName = "AList 管理员账号"; password.AccessibleName = "AList 密码";
        otp.AccessibleName = "AList 两步验证码"; files.AccessibleName = "AList 文件列表";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 6 };
        foreach (float height in new[] { 64f, 40f }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (float height in new[] { 38f, 44f, 38f }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var login = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        foreach (float width in new[] { 28f, 28f, 20f, 24f }) login.ColumnStyles.Add(new(SizeType.Percent, width));
        void LoginField(string label, TextBox field, int column)
        {
            login.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill }, column, 0);
            login.Controls.Add(field, column, 1);
        }
        LoginField("AList 管理员账号", username, 0); LoginField("密码", password, 1); LoginField("两步验证码（如有）", otp, 2);
        Button Button(string label, Func<Task> action)
        {
            var button = new DashboardButton(label, label, async (_, _) => await action()) { Height = 32, AutoSize = true };
            commands.Add(button); return button;
        }
        login.Controls.Add(Button("登录并打开", () => Work(async ct =>
        {
            await this.client.Login(username.Text.Trim(), password.Text, otp.Text.Trim(), ct);
            password.Clear(); otp.Clear();
            path = "/"; page = 1; await LoadDirectory(ct);
        })), 3, 1);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        navigation.ColumnStyles.Add(new(SizeType.Absolute, 90)); navigation.ColumnStyles.Add(new(SizeType.Percent, 100));
        navigation.Controls.Add(Button("上级目录", () => Work(async ct =>
        {
            int end = path.LastIndexOf('/');
            path = end > 0 ? path[..end] : "/"; page = 1; await LoadDirectory(ct);
        })), 0, 0);
        navigation.Controls.Add(location, 1, 0);
        files.Columns.Add("文件 / 文件夹", 580); files.Columns.Add("大小", 120);
        files.DoubleClick += async (_, _) => await Choose();
        var paging = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        previous = Button("上一页", () => Work(async ct => { page = Math.Max(1, page - 1); await LoadDirectory(ct); }));
        next = Button("下一页", () => Work(async ct => { page++; await LoadDirectory(ct); }));
        paging.Controls.Add(previous); paging.Controls.Add(next);
        paging.Controls.Add(new Label { Text = "目录密码", AutoSize = true, Margin = new(14, 8, 4, 0) });
        paging.Controls.Add(directoryPassword);
        paging.Controls.Add(Button("重新加载", () => Work(LoadDirectory)));
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        footer.Controls.Add(Button("选择此文件", Choose));
        cancel = new DashboardButton("取消", "取消或关闭文件选择", (_, _) =>
        { if (pending != null) { closing = true; pending.Cancel(); } else Close(); }) { Height = 32, Width = 80 };
        footer.Controls.Add(cancel);
        layout.Controls.Add(login, 0, 0); layout.Controls.Add(navigation, 0, 1);
        layout.Controls.Add(files, 0, 2); layout.Controls.Add(paging, 0, 3);
        layout.Controls.Add(footer, 0, 4); layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);
        foreach (var field in new Control[] { username, password, otp, location, directoryPassword, files })
        { field.BackColor = DashboardTheme.Panel; field.ForeColor = DashboardTheme.Text; }
        status.Text = "127.0.0.1:5244";
        previous.Enabled = next.Enabled = false;
        FormClosing += (_, e) =>
        { if (pending != null) { closing = true; e.Cancel = true; pending.Cancel(); } };
    }
    async Task LoadDirectory(CancellationToken ct)
    {
        var result = await client.List(path, directoryPassword.Text, page, ct);
        ct.ThrowIfCancellationRequested();
        location.Text = path; more = result.More;
        files.Items.Clear();
        foreach (var row in result.Entries)
        {
            var item = new ListViewItem((row.Directory ? "[目录] " : "") + row.Name) { Tag = row };
            item.SubItems.Add(row.Directory ? "" : (row.Size / 1048576d).ToString("F1") + " MiB");
            files.Items.Add(item);
        }
        status.Text = "第 " + page + " 页";
    }
    Task Choose() => Work(async ct =>
    {
        if (files.SelectedItems.Count != 1 || files.SelectedItems[0].Tag is not AlistEntry entry)
            throw new InvalidOperationException("请先选择文件或文件夹");
        if (entry.Directory) { path = entry.Path; page = 1; await LoadDirectory(ct); return; }
        Resource = await client.Resource(entry.LinkPath, ct);
        ct.ThrowIfCancellationRequested();
        DialogResult = DialogResult.OK; closing = true;
    });
    async Task Work(Func<CancellationToken, Task> action)
    {
        if (pending != null) return;
        using var stop = new CancellationTokenSource();
        pending = stop;
        foreach (var control in commands) control.Enabled = false;
        username.Enabled = password.Enabled = otp.Enabled = directoryPassword.Enabled = files.Enabled = false;
        status.Text = "正在读取本机 AList…";
        try { await action(stop.Token); }
        catch (OperationCanceledException) { status.Text = "已取消"; }
        catch (InvalidOperationException ex) { status.Text = ex.Message; }
        catch (Exception) { status.Text = "无法读取本机 AList，请确认已启动且端口为 5244"; }
        finally
        {
            pending = null;
            foreach (var control in commands) control.Enabled = true;
            username.Enabled = password.Enabled = otp.Enabled = directoryPassword.Enabled = files.Enabled = true;
            previous.Enabled = page > 1; next.Enabled = more;
            if (closing) Close();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { pending?.Cancel(); password.Clear(); otp.Clear(); directoryPassword.Clear(); client.Dispose(); }
        base.Dispose(disposing);
    }
    sealed class Fixture : HttpMessageHandler
    {
        internal int Calls;
        internal bool Hold;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Hold) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            using var input = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string path = input.RootElement.TryGetProperty("path", out var value) ? value.GetString() ?? "/" : "/";
            object data = request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => new { token = "ui-fixture-token" },
                "/api/fs/list" => new { content = new[] { new {
                    name = path == "/" ? "media" : "fixture-video.mkv", is_dir = path == "/",
                    size = path == "/" ? 0 : 8_000_000_000L, virtual_path = path == "/" ? "/media" : "/media/fixture-video.mkv"
                } }, total = 1 },
                _ => new { url = "https://dl-fixture.quark.cn/original?signature=fixture" }
            };
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { code = 200, data })) };
        }
    }
    internal static void UiTest(string prefix)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        var fixture = new Fixture();
        using var form = new AlistResourceForm(new AlistResourceClient(5244, fixture))
        { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try
            {
                Check(fixture.Calls == 0, "Opening the picker makes no implicit API calls or downloads");
                Check(form.password.UseSystemPasswordChar && form.directoryPassword.UseSystemPasswordChar,
                    "Account and directory passwords are masked");
                form.username.Text = "fixture"; form.password.Text = "fixture-password";
                await form.Work(async ct =>
                {
                    await form.client.Login(form.username.Text, form.password.Text, "", ct);
                    form.password.Clear(); await form.LoadDirectory(ct);
                });
                Check(form.files.Items.Count == 1 && !form.previous.Enabled && !form.next.Enabled && form.password.Text == "",
                    "A local login loads the first directory and clears the password field");
                form.files.Items[0].Selected = true;
                await form.Choose();
                Check(form.path == "/media" && form.location.Text == "/media" &&
                    form.files.Items[0].Tag is AlistEntry { Directory: false },
                    "Choosing a directory navigates before requesting a resource");
                foreach (int width in new[] { 800, 680 })
                {
                    form.ClientSize = new(width * form.DeviceDpi / 96, 540 * form.DeviceDpi / 96);
                    form.PerformLayout();
                    Check(form.files.Height > 100 && form.status.Bottom <= form.ClientSize.Height &&
                        form.username.Width > 100, $"AList picker keeps the file list and controls usable at width {width}");
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(prefix + "." + width + ".png");
                }
                fixture.Hold = true;
                var loading = form.Work(form.LoadDirectory);
                await fixture.Entered.Task;
                Check(!form.files.Enabled && form.commands.All(c => !c.Enabled) && form.cancel.Enabled,
                    "Only cancellation stays active during an AList operation");
                form.pending!.Cancel();
                await loading; fixture.Hold = false;
                Check(form.pending == null && form.files.Enabled && form.status.Text == "已取消",
                    "Cancellation restores controls without a repeated request");
                form.files.Items[0].Selected = true;
                await form.Choose();
            }
            catch (Exception ex) { failure = ex; form.Close(); }
        };
        var result = form.ShowDialog();
        try
        {
            if (failure != null) throw failure;
            Check(result == DialogResult.OK && form.Resource?.Url.IdnHost == "dl-fixture.quark.cn",
                "Selecting a file returns its resource and closes the modal with an OK result");
            File.WriteAllText(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new { state = "failed", checks, error = ex.Message }));
        }
    }
}
