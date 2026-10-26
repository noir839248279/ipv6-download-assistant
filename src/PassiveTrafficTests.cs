using System.Net;
using System.Text.Json;

namespace IPv6Lens;

internal static class PassiveTrafficTests
{
    internal static void Run(string path)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        try
        {
            DateTime now = DateTime.UtcNow;
            var accounting = new PassiveAccounting();
            var counters = DownloadRouter.Sites.ToDictionary(s => s.Id, _ => new DownloadCounter());
            FlowView Flow(int pid, string remote, long received, long sent = 10, int port = 50000) =>
                new(new(pid, "TCP", "192.0.2.200", port, remote, 443), received, sent, 0, now,
                    TrafficStore.Scope(IPAddress.Parse(remote)));
            void Sample(params FlowView[] flows) => accounting.Sample(flows, counters, false, now);

            Check(accounting.Track(101, "Quark.exe", 1) && accounting.Track(102, "steamwebhelper", 1) &&
                accounting.Track(103, "alist", 1) && accounting.Track(104, "chrome", 1) &&
                !accounting.Track(105, "unrelated", 1),
                "Only supported native services, browsers and local players/servers are observed");
            Sample(Flow(101, "192.0.2.1", 1000), Flow(102, "2001:db8::2", 2000));
            Check(counters["quark"].V4 == 1000 && counters["steam"].V6 == 2000,
                "Disabled native services keep receiving actual IPv4 and IPv6 byte deltas");
            Sample(Flow(101, "192.0.2.1", 1300, 30), Flow(102, "2001:db8::2", 2400, 40));
            DownloadRouter.SampleCounters(counters.Values, 1);
            Check(counters["quark"].V4 == 1300 && counters["steam"].V6 == 2400 &&
                counters["quark"].Upload4 == 30 && counters["steam"].Upload6 == 40 &&
                counters["quark"].Rate4 == 1300 && counters["steam"].Rate6 == 2400,
                "Disabled accounting retains numeric totals, upload bytes and speed sampling");

            accounting.ObserveDns(103, "media.pds.quark.cn.", "type: 5 alias.example;2001:db8::3;", 0, now);
            accounting.ObserveDns(104, "upos-test.bilivideo.com", "::ffff:192.0.2.4;", 0, now);
            Sample(Flow(103, "2001:db8::3", 3000), Flow(104, "192.0.2.4", 4000));
            Check(counters["quark"].V6 == 3000 && counters["bilibili"].V4 == 4000 &&
                accounting.ServiceStatus("quark").Contains("DNS 关联") &&
                accounting.ServiceStatus("bilibili").Contains("DNS 关联"),
                "Local AList Quark and browser Bilibili use the querying process DNS correlation");
            Sample(Flow(103, "127.0.0.1", 50000), Flow(103, "172.16.0.1", 60000));
            Check(counters["quark"].V6 == 3000 && counters["quark"].V4 == 1300,
                "Local AList/player transfers and private-network bytes are not charged twice");
            long beforeUnknown = accounting.UnattributedReceived;
            Sample(Flow(104, "2001:db8::3", 100), Flow(103, "192.0.2.90", 200));
            Check(counters["bilibili"].V6 == 0 && counters["quark"].V4 == 1300 &&
                accounting.UnattributedReceived == beforeUnknown + 300,
                "Another process DNS and unrecognized AList storage traffic never become service bytes");
            accounting.ObserveDns(104, "unrelated.example", "192.0.2.4;", 0, now);
            Sample(Flow(104, "192.0.2.4", 4500));
            Check(counters["bilibili"].V4 == 4000,
                "A shared IP observed for an unrelated hostname stops Bilibili attribution");
            accounting.ObserveDns(104, "upos-test.bilivideo.com", "192.0.2.4;", 0, now);
            Sample(Flow(104, "192.0.2.4", 4600));
            Check(counters["bilibili"].V4 == 4000,
                "A repeated platform query cannot erase a shared-address ambiguity");

            accounting.ObserveDns(104, "upos-error.bilivideo.com", "192.0.2.5;", 9003, now);
            Sample(Flow(104, "192.0.2.5", 500));
            Check(counters["bilibili"].V4 == 4000, "Failed DNS queries cannot classify a flow");
            accounting.ObserveDns(104, "upos-new.bilivideo.com", "192.0.2.6;", 0, now);
            now = now.AddSeconds(61);
            Sample(Flow(104, "192.0.2.6", 600));
            Check(counters["bilibili"].V4 == 4000, "An expired DNS association cannot classify a new connection");
            Sample(Flow(103, "2001:db8::3", 3500));
            Check(counters["quark"].V6 == 3500, "An established flow keeps its association after the short DNS window");

            var q = counters["quark"];
            q.Enabled = true; q.RouteRevision++;
            Sample(Flow(101, "192.0.2.1", 2000));
            Check(q.V4 == 1300, "Enabled service ETW bytes do not duplicate relay accounting");
            q.Enabled = false; q.RouteRevision++;
            Sample(Flow(101, "192.0.2.1", 2300));
            Check(q.V4 == 1300, "The first interval after disabling discards ambiguous enabled-period bytes");
            Sample(Flow(101, "192.0.2.1", 2600));
            Check(q.V4 == 1600, "Disabled native accounting resumes on the next clean interval");
            q.Enabled = true; q.RouteRevision++; q.Enabled = false; q.RouteRevision++;
            Sample(Flow(101, "192.0.2.1", 3000));
            Check(q.V4 == 1600, "A rapid on/off cycle within one sample cannot double count relay traffic");
            accounting.Sample(new[] { Flow(101, "192.0.2.1", 3200) }, counters, true, now);
            Check(q.V4 == 1600 && q.Proxy == 200 && accounting.ServiceStatus("quark").Contains("未知"),
                "An active TUN is reported as unknown egress, never asserted to be physical IPv4/IPv6");
            DownloadRouter.ResetCounters(counters.Values);
            Sample(Flow(101, "192.0.2.1", 3300));
            Check(q.V4 == 100 && q.V6 == 0 && q.Proxy == 0,
                "Clearing visible totals keeps flow baselines and counts only new disabled traffic");

            accounting.Track(104, "chrome", 2);
            Sample(Flow(104, "192.0.2.4", 100));
            Check(counters["bilibili"].V4 == 0, "PID reuse discards the previous process DNS and flow state");
            accounting.Forget(103);
            Sample(Flow(103, "2001:db8::3", 9999));
            Check(q.V6 == 0, "Terminated owners cannot keep attributing traffic under a recycled PID");
            now = now.AddMinutes(3);
            Sample();
            Check(accounting.AssociationCount == 0 && accounting.FlowCount == 0,
                "Idle DNS and flow baselines expire instead of accumulating for the program lifetime");
            accounting.Track(106, "firefox", 1);
            for (int i = 0; i < PassiveAccounting.MaxAssociations + 10; i++)
                accounting.ObserveDns(106, "upos-limit.bilivideo.com", $"2001:db8:1::{i + 1:x};", 0, now);
            Check(accounting.AssociationCount == PassiveAccounting.MaxAssociations,
                "DNS correlation has a fixed capacity even under a query burst");
            Check(q.Auxiliary.Used == 0 && counters.Values.All(c => !c.Enabled),
                "Passive monitoring never enables interception or consumes the mixed IPv4 allowance");
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks,
                scope = "Deterministic ETW/DNS accounting fixtures; no TUN, public traffic or administrator action." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }
}
