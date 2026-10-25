using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace IPv6Lens;

internal static class DashboardTheme
{
    internal static bool Light;
    internal static Color Background => ColorTranslator.FromHtml(Light ? "#F3F5F9" : "#0B1020");
    internal static Color Panel => ColorTranslator.FromHtml(Light ? "#FFFFFF" : "#151C31");
    internal static Color Raised => ColorTranslator.FromHtml(Light ? "#EEF1F7" : "#202941");
    internal static Color Line => ColorTranslator.FromHtml(Light ? "#DDE3ED" : "#2A3550");
    internal static Color Text => ColorTranslator.FromHtml(Light ? "#172136" : "#EDF2FC");
    internal static Color Muted => ColorTranslator.FromHtml(Light ? "#59677D" : "#A0AEC6");
    internal static Color V6 => ColorTranslator.FromHtml(Light ? "#087E5A" : "#34D399");
    internal static Color V4 => ColorTranslator.FromHtml(Light ? "#5754BF" : "#929AF4");
    internal static Color Unknown => ColorTranslator.FromHtml(Light ? "#9B6500" : "#FBBF24");
    internal static Color Error => ColorTranslator.FromHtml(Light ? "#BE2544" : "#FFA0AE");
    internal static readonly string[] Protocols = { "IPv6", "IPv4", "未知" };
    internal static Color Protocol(int index) => index == 0 ? V6 : index == 1 ? V4 : Unknown;
}

// Paint in logical pixels, and scale once for the monitor's actual DPI.
internal abstract class DashboardSurface : Control
{
    readonly Dictionary<(float, bool), Font> fonts = new();
    protected float ScaleFactor => DeviceDpi / 96f;
    protected DashboardSurface()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }
    protected Font Face(float size, bool bold = false)
    {
        if (!fonts.TryGetValue((size, bold), out var font))
            fonts[(size, bold)] = font = new Font("Microsoft YaHei UI", size,
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return font;
    }
    protected void TextAt(Graphics g, string text, RectangleF rect, float size = 12,
        Color? color = null, bool bold = false, StringAlignment align = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color ?? DashboardTheme.Text);
        using var format = new StringFormat
        {
            Alignment = align, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, Face(size, bold), brush, rect, format);
    }
    protected static GraphicsPath Rounded(RectangleF r, float radius = 8)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    protected static void FillRound(Graphics g, RectangleF r, Color color, float radius = 8)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }
    protected static void Dot(Graphics g, float x, float y, Color color, float radius = 3)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(DashboardTheme.Background);
        g.ScaleTransform(ScaleFactor, ScaleFactor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float w = Width / ScaleFactor, h = Height / ScaleFactor;
        using var shape = Rounded(new(.5f, .5f, w - 1, h - 1));
        using var fill = new SolidBrush(DashboardTheme.Panel);
        using var border = new Pen(DashboardTheme.Line);
        g.FillPath(fill, shape);
        g.DrawPath(border, shape);
        PaintContent(g, w, h);
    }
    protected abstract void PaintContent(Graphics g, float width, float height);
    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var font in fonts.Values) font.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class DashboardToggle : CheckBox
{
    internal bool Mixed;
    internal DashboardToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new(46, 28); Text = "";
        Cursor = Cursors.Hand;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DashboardTheme.Panel);
        float s = DeviceDpi / 96f;
        g.ScaleTransform(s, s);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Width / s, h = Height / s;
        var track = new RectangleF(2, (h - 24) / 2, w - 4, 24);
        Color color = !Enabled ? DashboardTheme.Line :
            Mixed ? DashboardTheme.Unknown : Checked ? DashboardTheme.V6 : DashboardTheme.Line;
        using var path = DashboardSurfaceTogglePath(track);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        using var knob = new SolidBrush(Enabled ? Color.White : DashboardTheme.Muted);
        float x = Mixed ? (w - 18) / 2 : Checked ? w - 23 : 5;
        g.FillEllipse(knob, x, (h - 18) / 2, 18, 18);
        if (Mixed)
        {
            using var pen = new Pen(DashboardTheme.Background, 2);
            g.DrawLine(pen, x + 5, h / 2, x + 13, h / 2);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(DashboardTheme.Text) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
        }
    }
    static GraphicsPath DashboardSurfaceTogglePath(RectangleF r)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, 24, 24, 90, 180);
        p.AddArc(r.Right - 24, r.Y, 24, 24, 270, 180);
        p.CloseFigure();
        return p;
    }
}

internal sealed class DashboardButton : Button
{
    internal DashboardButton(string text, string accessibleName, EventHandler clicked)
    {
        Text = text; AccessibleName = accessibleName; Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        Size = new(96, 34); Click += clicked; RefreshTheme();
    }
    internal void RefreshTheme()
    {
        BackColor = DashboardTheme.Panel; ForeColor = DashboardTheme.Text;
        FlatAppearance.BorderColor = DashboardTheme.Line;
        FlatAppearance.MouseOverBackColor = DashboardTheme.Raised;
        FlatAppearance.MouseDownBackColor = DashboardTheme.Line;
    }
}

internal sealed class TrafficSummary : DashboardSurface
{
    readonly int protocol;
    readonly Queue<double> history = new();
    internal double Download, Upload;
    internal long Received, Sent;
    internal IReadOnlyCollection<double> History => history;
    internal TrafficSummary(int protocol) { this.protocol = protocol; AccessibleName = DashboardTheme.Protocols[protocol] + " 流量"; }
    internal void Update(double down, double up, long received, long sent, bool sample = true)
    {
        Download = down; Upload = up; Received = received; Sent = sent;
        if (sample) { history.Enqueue(Math.Max(0, down)); while (history.Count > 60) history.Dequeue(); }
        AccessibleDescription = $"下载 {TrafficStore.Bytes(down)}/s，上传 {TrafficStore.Bytes(up)}/s，累计下载 {TrafficStore.Bytes(received)}，累计上传 {TrafficStore.Bytes(sent)}";
        Invalidate();
    }
    internal void ResetHistory() { history.Clear(); Invalidate(); }
    protected override void PaintContent(Graphics g, float w, float h)
    {
        Color accent = DashboardTheme.Protocol(protocol);
        Dot(g, 22, 23, accent, 4);
        TextAt(g, DashboardTheme.Protocols[protocol] + " 流量", new(34, 10, w - 60, 26), 12, DashboardTheme.Muted, true);
        TextAt(g, TrafficStore.Bytes(Download) + "/s", new(18, 40, w - 36, 36), 27, bold: true);
        TextAt(g, "↑ " + TrafficStore.Bytes(Upload) + "/s", new(20, 80, w - 40, 22), 12, DashboardTheme.Muted);
        TextAt(g, "↓ 累计 " + TrafficStore.Bytes(Received), new(20, h - 49, w * .56f, 19), 11, DashboardTheme.Muted);
        TextAt(g, "↑ 累计 " + TrafficStore.Bytes(Sent), new(20, h - 29, w * .56f, 19), 11, DashboardTheme.Muted);
        float chartW = w * .38f, chartX = w - chartW - 16, chartY = h - 48;
        using var baseline = new Pen(DashboardTheme.Line);
        g.DrawLine(baseline, chartX, h - 16, w - 16, h - 16);
        var values = history.ToArray();
        if (values.Length < 2) return;
        double max = Math.Max(1024, values.Max());
        var points = values.Select((n, i) => new PointF(chartX + i * chartW / 59,
            chartY + 32 - (float)(n / max) * 30)).ToArray();
        using var line = new Pen(accent, 1.8f) { LineJoin = LineJoin.Round };
        g.DrawLines(line, points);
    }
}

internal sealed class TrafficShare : DashboardSurface
{
    internal long V6, V4, Unknown;
    internal double Percent => V6 + (double)V4 + Unknown is var total && total > 0 ? 100d * V6 / total : 0;
    internal void Update(long six, long four, long unknown)
    {
        V6 = six; V4 = four; Unknown = unknown;
        AccessibleName = $"IPv6 下载占比 {Percent:F1}%";
        Invalidate();
    }
    protected override void PaintContent(Graphics g, float w, float h)
    {
        float diameter = Math.Min(126, h - 36);
        var circle = new RectangleF(24, (h - diameter) / 2, diameter, diameter);
        using (var track = new Pen(DashboardTheme.Raised, 10)) g.DrawEllipse(track, circle);
        double total = V6 + (double)V4 + Unknown;
        long[] bytes = { V6, V4, Unknown };
        float angle = -90;
        if (total > 0)
            for (int i = 0; i < 3; i++)
            {
                float sweep = (float)(bytes[i] / total * 360);
                if (sweep <= 0) continue;
                using var pen = new Pen(DashboardTheme.Protocol(i), 10);
                g.DrawArc(pen, circle, angle, sweep);
                angle += sweep;
            }
        TextAt(g, Percent.ToString("F1") + "%", new(circle.X, circle.Y + diameter / 2 - 25, diameter, 33),
            24, DashboardTheme.V6, true, StringAlignment.Center);
        TextAt(g, "IPv6 下载占比", new(circle.X, circle.Y + diameter / 2 + 9, diameter, 20),
            11, DashboardTheme.Muted, align: StringAlignment.Center);
        float x = circle.Right + 30;
        TextAt(g, "累计下载", new(x, 14, w - x - 16, 24), 12, DashboardTheme.Muted);
        for (int i = 0; i < 3; i++)
        {
            float y = 48 + i * 34;
            Dot(g, x + 4, y + 11, DashboardTheme.Protocol(i), 4);
            TextAt(g, DashboardTheme.Protocols[i], new(x + 18, y, 44, 23), 12, DashboardTheme.Muted);
            TextAt(g, TrafficStore.Bytes(bytes[i]), new(x + 64, y, w - x - 133, 23), 14, bold: true);
            TextAt(g, (total > 0 ? 100d * bytes[i] / total : 0).ToString("F1") + "%",
                new(w - 76, y, 56, 23), 11, DashboardTheme.Muted, align: StringAlignment.Far);
        }
    }
}

internal sealed class SplitTile : DashboardSurface
{
    internal readonly DashboardToggle Toggle = new() { AutoCheck = false, Enabled = false, AccessibleName = "有线 / Wi-Fi 分流" };
    internal string Status = "正在读取 Clash Verge 状态";
    internal string EthernetName = "以太网", WifiName = "Wi-Fi";
    internal bool WiredOnline, WifiOnline, BindingOnly, Confirmed;
    internal SplitTile() { Controls.Add(Toggle); AccessibleName = "有线 / Wi-Fi 分流"; }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Toggle.SetBounds(Width - (int)(70 * ScaleFactor), (int)(14 * ScaleFactor),
            (int)(46 * ScaleFactor), (int)(28 * ScaleFactor));
    }
    protected override void PaintContent(Graphics g, float w, float h)
    {
        TextAt(g, "有线 / Wi-Fi 分流", new(20, 12, w - 105, 27), 16, bold: true);
        TextAt(g, Status, new(20, 42, w - 40, 25), 11, DashboardTheme.Muted);
        float laneWidth = (w - 52) / 2;
        Color muted = Toggle.Checked && Confirmed ? DashboardTheme.Text :
            Color.FromArgb(DashboardTheme.Light ? 150 : 110, DashboardTheme.Muted);
        for (int i = 0; i < 2; i++)
        {
            float x = 20 + i * (laneWidth + 12);
            FillRound(g, new(x, 80, laneWidth, h - 96), DashboardTheme.Raised, 6);
            TextAt(g, i == 0 ? EthernetName : WifiName, new(x + 12, 86, laneWidth - 24, 22), 13, muted, true);
            string detail = i == 0
                ? BindingOnly ? "DIRECT 同样绑定 Wi-Fi" : WiredOnline ? "已连接 · 普通直连" : "未连接"
                : WifiOnline ? "已连接 · 代理节点" : "未连接";
            TextAt(g, detail, new(x + 12, 111, laneWidth - 24, 20), 11, muted);
        }
    }
}

internal sealed class ServiceTile : DashboardSurface
{
    internal readonly DashboardToggle Toggle;
    internal readonly string Id;
    readonly string title, mark;
    readonly Color brand;
    internal readonly long[] Bytes = new long[3];
    internal double DownloadRate;
    internal double UploadRate;
    internal string Server = "尚未发现服务器", State = "等待连接";
    internal bool ProtectionLost;
    internal double Percent => Bytes.Sum(n => (double)n) is var total && total > 0 ? 100d * Bytes[0] / total : 0;
    internal ServiceTile(string id)
    {
        Id = id;
        (title, mark, brand) = id switch
        {
            "quark" => ("夸克网盘", "Q", Color.FromArgb(120, 123, 252)),
            "bilibili" => ("Bilibili", "B", Color.FromArgb(238, 113, 155)),
            "steam" => ("Steam", "S", Color.FromArgb(98, 161, 221)),
            "epic" => ("Epic Games", "E", Color.FromArgb(150, 160, 182)),
            "youtube" => ("YouTube", "▶", Color.FromArgb(245, 99, 110)),
            _ => ("其他网站", "◎", Color.FromArgb(53, 184, 167))
        };
        Toggle = new DashboardToggle { AccessibleName = title + " IPv6 接管" };
        Controls.Add(Toggle); AccessibleName = title;
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Toggle.SetBounds(Width - (int)(66 * ScaleFactor), (int)(16 * ScaleFactor),
            (int)(46 * ScaleFactor), (int)(28 * ScaleFactor));
    }
    internal void Update(DownloadCounter counter, string server, bool lost)
    {
        Bytes[0] = Interlocked.Read(ref counter.V6); Bytes[1] = Interlocked.Read(ref counter.V4); Bytes[2] = Interlocked.Read(ref counter.Proxy);
        DownloadRate = counter.Rate4 + counter.Rate6 + counter.RateProxy;
        UploadRate = counter.UploadRate4 + counter.UploadRate6 + counter.UploadRateProxy;
        ProtectionLost = lost;
        Server = Id == "global" ? "公共双栈 CDN" : server;
        State = lost ? Id == "global" ? "尝试原连接方式" : "接管失效 · 已阻断"
            : !counter.Enabled ? "未接管"
            : counter.RateProxy > 1024 && counter.RateProxy >= Math.Max(counter.Rate4, counter.Rate6) ? "正在使用原代理"
            : counter.Rate4 + counter.Rate6 > 1024 ? counter.Rate6 >= counter.Rate4 ? "下载中 · IPv6" : "下载中 · IPv4"
            : counter.UploadRate4 + counter.UploadRate6 + counter.UploadRateProxy > 1024 ? "上传中"
            : counter.State.Contains("失败") || counter.State.Contains("失效") ? counter.State
            : "等待连接";
        AccessibleDescription = $"{State}，下载 {TrafficStore.Bytes(DownloadRate)}/s，IPv6 占比 {Percent:F1}%，服务器 {Server}";
        Invalidate();
    }
    protected override void PaintContent(Graphics g, float w, float h)
    {
        FillRound(g, new(18, 18, 34, 34), Color.FromArgb(32, brand), 6);
        TextAt(g, mark, new(18, 18, 34, 34), 20, brand, true, StringAlignment.Center);
        TextAt(g, title, new(64, 15, w - 140, 25), 14, bold: true);
        TextAt(g, Server, new(64, 40, w - 84, 21), 10.5f, DashboardTheme.Muted);
        string rate = TrafficStore.Bytes(DownloadRate) + "/s";
        TextAt(g, rate, new(18, 73, w - 128, 32), 23, bold: true);
        TextAt(g, Percent.ToString("F1") + "% IPv6", new(w - 116, 78, 98, 25),
            11, DashboardTheme.V6, true, StringAlignment.Far);
        TextAt(g, "↑ " + TrafficStore.Bytes(UploadRate) + "/s", new(20, 107, w - 40, 20), 11, DashboardTheme.Muted);
        float barWidth = w - 40, offset = 20;
        FillRound(g, new(offset, 137, barWidth, 5), DashboardTheme.Raised, 2);
        double total = Bytes.Sum(n => (double)n);
        if (total > 0)
            for (int i = 0; i < 3; i++)
            {
                float width = (float)(Bytes[i] / total) * barWidth;
                if (width > 0)
                {
                    using var brush = new SolidBrush(DashboardTheme.Protocol(i));
                    g.FillRectangle(brush, offset, 137, width, 5);
                    offset += width;
                }
            }
        for (int i = 0; i < 3; i++)
        {
            float x = 20 + i * barWidth / 3;
            Dot(g, x + 3, 161, DashboardTheme.Protocol(i), 2.5f);
            TextAt(g, DashboardTheme.Protocols[i], new(x + 12, 150, barWidth / 3 - 14, 22), 11, DashboardTheme.Muted);
            TextAt(g, TrafficStore.Bytes(Bytes[i]), new(x, 176, barWidth / 3 - 6, 22), 12, bold: true);
        }
        using var rule = new Pen(DashboardTheme.Line);
        g.DrawLine(rule, 20, h - 42, w - 20, h - 42);
        Dot(g, 23, h - 22, ProtectionLost ? DashboardTheme.Error : Toggle.Checked ? DashboardTheme.V6 : DashboardTheme.Muted, 3);
        TextAt(g, State, new(34, h - 36, w - 148, 28), 11, ProtectionLost ? DashboardTheme.Error : DashboardTheme.Muted);
        string policy = ProtectionLost ? "接管失效" : !Toggle.Checked ? "原连接方式" : Id == "global" ? "IPv6 优先" : "仅 IPv6";
        Color color = ProtectionLost ? DashboardTheme.Error : !Toggle.Checked ? DashboardTheme.Muted :
            Id == "global" ? DashboardTheme.Unknown : DashboardTheme.V6;
        FillRound(g, new(w - 106, h - 33, 86, 23), Color.FromArgb(24, color), 4);
        TextAt(g, policy, new(w - 106, h - 33, 86, 23), 10.5f, color, true, StringAlignment.Center);
    }
}
