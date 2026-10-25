using System.Text.Json;

namespace IPv6Lens;

internal sealed partial class DownloadForm
{
    // These fixtures are available only through the network-isolated UI test entry point.
    internal async Task<object> VerifyDashboard(string prefix)
    {
        if (!testMode) throw new InvalidOperationException("Dashboard tests require isolated UI mode.");
        await startup;
        timer.Stop();
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        void Snapshot(string name)
        {
            using var image = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(image, new Rectangle(Point.Empty, canvas.Size));
            image.Save(prefix + "." + name + ".png");
        }
        Check(DefaultsActive && master.Checked && !master.Mixed, "Startup: six services and master enabled");
        Check(!SplitActive, "Split routing defaults to off independently of services");
        ClientSize = new(Px(1280), Px(920)); LayoutDashboard();
        Check(cards["quark"].Top == cards["steam"].Top && cards["epic"].Top > cards["quark"].Top,
            "Desktop window uses three service columns at actual display DPI");
        cards["quark"].Toggle.Checked = false;
        Check(!master.Checked && master.Mixed && serviceCount.Text == "5 / 6 已开启", "Single switch updates mixed master and count");
        Check(router != null && !router.Counters["quark"].Enabled && router.Counters["steam"].Enabled,
            "Single switch changes only its own router policy");
        await SetAll(false);
        Check(!master.Checked && !master.Mixed && router == null && cards.Values.All(c => !c.Toggle.Checked),
            "Master off releases relay and clears all switches");
        await SetAll(true);
        Check(DefaultsActive && master.Checked && !master.Mixed, "Master on starts relay and restores all six policies");
        cards["epic"].Toggle.Checked = false;
        Check(master.Mixed && !master.Checked, "Partial selection is visually distinct");
        for (int i = 0; i < 6; i++)
        {
            var r = router!.Counters[cards.Keys.ElementAt(i)];
            r.V6 = 3 * 1024 * 1024; r.V4 = 512 * 1024; r.Proxy = 512 * 1024;
            r.Upload6 = 96 * 1024; r.Upload4 = 16 * 1024; r.UploadProxy = 16 * 1024;
            r.Rate6 = (i + 1) * 512 * 1024; r.Rate4 = 12 * 1024; r.RateProxy = 4 * 1024;
            r.UploadRate6 = 16 * 1024; r.UploadRate4 = 1024; r.UploadRateProxy = 512;
        }
        RefreshCards(false);
        Check(Math.Abs(share.Percent - 75) < .001 && cards.Values.All(c => Math.Abs(c.Percent - 75) < .001),
            "Ring and all six bars include unknown bytes in their 75% denominator");
        Check(summaries[0].Received == 18 * 1024 * 1024 && summaries[0].Sent == 576 * 1024,
            "Global received and uploaded totals match all six counters");
        Check(summaries[2].Download == 24 * 1024 && summaries[2].Upload == 3072,
            "Unknown download and upload speeds remain separate");
        foreach (var summary in summaries)
        {
            double baseRate = summary.Download;
            for (int i = 0; i < 65; i++)
                summary.Update(baseRate * (.55 + i % 9 * .05), summary.Upload, summary.Received, summary.Sent);
            Check(summary.History.Count == 60, "Sparkline history is bounded to 60 samples");
        }
        network.Text = "界面测试 · 固定样本数据";
        cards["quark"].Server = "dl-test.quark.cn";
        cards["bilibili"].Server = "upos-example.bilivideo.com";
        cards["steam"].Server = "cache-example.steamcontent.com";
        cards["youtube"].Server = "example.googlevideo.com";
        cards["quark"].State = "下载中 · IPv6";
        Snapshot("dark");
        actions[^1].PerformClick();
        Check(DashboardTheme.Light && BackColor == DashboardTheme.Background, "Theme button updates the entire dashboard");
        Snapshot("light");
        actions[^1].PerformClick();
        PreviewClashBinding();
        Check(SplitActive && split.WiredOnline && split.WifiOnline && !split.BindingOnly,
            "Split preview shows wired direct and Wi-Fi proxy lanes");
        Snapshot("split");
        ClientSize = new(Px(920), Px(850)); LayoutDashboard();
        Check(cards["quark"].Top == cards["bilibili"].Top && cards["steam"].Top > cards["quark"].Top,
            "Medium window uses two service columns");
        Snapshot("medium");
        ClientSize = new(Px(560), Px(850)); LayoutDashboard();
        Check(cards["bilibili"].Top > cards["quark"].Top && summaries[1].Top > summaries[0].Top,
            "Narrow window uses one column and a scrollable canvas");
        Check(canvas.Controls.Cast<Control>().All(c => c.Left >= 0 && c.Right <= canvas.Width),
            "All dashboard controls remain horizontally inside narrow canvas");
        Snapshot("narrow");
        ClientSize = new(Px(1280), Px(920)); LayoutDashboard();
        WriteReport(prefix + ".report.json");
        using (var doc = JsonDocument.Parse(File.ReadAllText(prefix + ".report.json")))
        {
            var services = doc.RootElement.GetProperty("services");
            Check(services.GetArrayLength() == 6 && services.EnumerateArray().All(s => s.GetProperty("ipv6Uploaded").GetInt64() == 96 * 1024),
                "Export retains all six services and upload totals");
        }
        ResetTraffic();
        Check(share.Percent == 0 && summaries.All(s => s.Received == 0 && s.Sent == 0 && s.Download == 0 && s.Upload == 0 && s.History.Count == 0),
            "Reset immediately clears ring, speeds, upload/download totals, and histories");
        Check(cards.Values.All(c => c.Bytes.All(b => b == 0)), "Reset clears all six service bars");
        SimulateOwnershipLoss();
        Check(cards.Values.All(c => c.ProtectionLost) && hint.ForeColor == DashboardTheme.Error,
            "Ownership loss is visible on every service and in the status line");
        Snapshot("loss");
        await SetAll(false); await SetAll(true);
        Check(DefaultsActive && !lossNotified, "Off then on recovers isolated relay and all switches");
        Snapshot("empty");
        return new { state = "passed", checks, dpi = DeviceDpi };
    }
}
