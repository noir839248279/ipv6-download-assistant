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
        Check(DefaultsActive && !master.Checked && master.Mixed && !counters["global"].Enabled,
            "Startup protects five services and leaves unrelated websites on original path");
        Check(cards["global"].Allowance.LinkArea.Length==0,"Other websites has no misleading auxiliary allowance link");
        Check(!SplitActive, "Split routing defaults to off independently of services");
        Check(counters.Values.All(r=>r.Mode==ServiceMode.Mixed),"All service mode selectors default to protected mixed");
        Check(router!.Pac().Contains("return url.substring(0,6)==='https:'?"),
            "Disabled other-websites policy keeps original PAC path");
        cards["quark"].ModeSelect.SelectedIndex=1;await serviceWork;
        Check(counters["quark"].Mode==ServiceMode.Strict && counters["bilibili"].Mode==ServiceMode.Mixed,
            "Mode selection affects only its own service");
        cards["quark"].ModeSelect.SelectedIndex=0;await serviceWork;
        int syncCalls=0;
        SyncServicesTest=_=>++syncCalls==1?Task.FromException(new IOException("Injected selector failure")):Task.CompletedTask;
        await Switch("quark",false);
        Check(DefaultsActive && syncCalls==2,"Single-service sync failure rolls back target without stopping all services");
        SyncServicesTest=null;
        counters["quark"].V6=12345;counters["quark"].Auxiliary.Reserve(100);
        await SetAll(false);
        Check(counters["quark"].V6==12345&&cards["quark"].Bytes[0]==12345,
            "Stopping capture retains cumulative totals on dashboard");
        await SetAll(true);
        Check(counters["quark"].V6==12345&&counters["quark"].Auxiliary.Used==100,
            "Restarting relay preserves totals and auxiliary allowance");
        ResetTraffic();
        var pendingRead = new TaskCompletionSource<SplitState>();
        var polling = RefreshClashBinding(() => pendingRead.Task);
        try
        {
            Check(clashPolling && !clashBusy && master.Enabled &&
                cards.Values.All(c => c.Toggle.Enabled && c.Toggle.Checked) && actions.All(b => b.Enabled),
                "Pending read-only Clash polling leaves service switches and actions interactive");
            cards["quark"].Toggle.Checked = false;
            Check(!router!.Counters["quark"].Enabled && router.Counters["bilibili"].Enabled,
                "A service switch remains effective during read-only polling");
            cards["quark"].Toggle.Checked = true;
        }
        finally
        {
            pendingRead.SetResult(new(new("WLAN", "Ethernet", true, true, 356, 372, 261, 261),
                false, false, "Isolated polling fixture"));
            await polling;
        }
        Check(!clashPolling && DefaultsActive, "Polling completion preserves all selected service policies");
        await RefreshClashBinding(() => Task.FromException<SplitState>(new IOException("Isolated read failure")));
        Check(master.Enabled && DefaultsActive && split.Toggle.Mixed && !split.Confirmed,
            "Failed polling preserves service selection and marks only split status unconfirmed");
        PaintClashBinding(new(new("WLAN", "Ethernet", true, true, 356, 372, 261, 261),
            false, false, "Isolated split off fixture"));
        clashBusy = true; SetControlsEnabled();
        Check(!master.Enabled && cards.Values.All(c => !c.Toggle.Enabled),
            "Mutating split operations still lock service controls");
        foreach (bool light in new[] { false, true })
        {
            DashboardTheme.Light = light;
            ApplyTheme();
            Check(cards.Values.All(c=>c.Allowance.BackColor==DashboardTheme.Panel),
                $"Auxiliary allowance controls follow {(light?"light":"dark")} theme");
            using var image = new Bitmap(master.Width, master.Height);
            master.DrawToBitmap(image, new Rectangle(Point.Empty, master.Size));
            Color track = image.GetPixel(Px(12), master.Height / 2);
            Check(track.ToArgb() == DashboardTheme.V6.ToArgb(),
                $"Checked but temporarily locked switches retain their on color ({(light ? "light" : "dark")})");
            split.Toggle.DrawToBitmap(image, new Rectangle(Point.Empty, split.Toggle.Size));
            Check(image.GetPixel(Px(36), split.Toggle.Height / 2).ToArgb() == DashboardTheme.Line.ToArgb(),
                $"Disabled off switches retain their off color ({(light ? "light" : "dark")})");
        }
        DashboardTheme.Light = false;
        clashBusy = false; SetControlsEnabled();
        var layoutSamples = new List<object>();
        bool contentBounded = true;
        foreach (int width in new[] { 1280, 920, 560, 1280, 920, 1280 })
        {
            ClientSize = new(Px(width), Px(850)); LayoutDashboard();
            viewport.AutoScrollPosition = new(0, canvas.Height);
            for (int pass = 0; pass < 3; pass++) LayoutDashboard();
            int trailing = canvas.Height - hint.Bottom;
            int extent = viewport.DisplayRectangle.Height;
            layoutSamples.Add(new { width, dpi = DeviceDpi, canvas = canvas.Bounds,
                hintBottom = hint.Bottom, trailing, extent, viewport = viewport.ClientSize,
                scroll = viewport.AutoScrollPosition, display = viewport.DisplayRectangle });
            contentBounded &= trailing >= 0 && trailing <= Px(24) &&
                Math.Abs(extent - Math.Max(canvas.Height, viewport.ClientSize.Height)) <= Px(2) &&
                canvas.Bottom >= Math.Min(canvas.Height, viewport.ClientSize.Height) - Px(2) &&
                !viewport.HorizontalScroll.Visible;
        }
        File.WriteAllText(prefix + ".layout.json", JsonSerializer.Serialize(layoutSamples,
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Check(contentBounded, "Repeated resize and bottom scrolling keep content extent within the footer");
        DashboardTheme.Light = true; ApplyTheme();
        using (var bottom = new Bitmap(viewport.Width, viewport.Height))
        {
            viewport.DrawToBitmap(bottom, new Rectangle(Point.Empty, viewport.Size));
            bottom.Save(prefix + ".bottom.png");
        }
        DashboardTheme.Light = false; ApplyTheme();
        viewport.AutoScrollPosition = Point.Empty;
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
        Check(cards.Values.All(c => c.ProtectionLost == c.Toggle.Checked) && hint.ForeColor == DashboardTheme.Error,
            "Ownership loss blocks enabled services without mislabeling disabled services");
        Snapshot("loss");
        await Switch("quark", false);
        Check(!router!.Counters["quark"].Enabled && !cards["quark"].ProtectionLost &&
            cards["quark"].State == "已关闭 · 原路径" && router.Counters["bilibili"].Enabled,
            "A single service can restore its original route after protection loss");
        await Switch("quark", true);
        Check(!router.Counters["quark"].Enabled && !cards["quark"].Toggle.Checked,
            "Protection loss still prevents silently re-enabling a service");
        await SetAll(false); await SetAll(true);
        Check(DefaultsActive && !lossNotified, "Off then on recovers isolated relay and all switches");
        Snapshot("empty");
        return new { state = "passed", checks, dpi = DeviceDpi };
    }
}
