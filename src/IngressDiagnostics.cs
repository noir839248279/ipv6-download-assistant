using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

// This thread must remain observable when the UI or shared thread pool stalls.
internal sealed class IngressDiagnostics : IDisposable
{
    internal sealed record ProbeResult(DateTime TimeUtc, string Stage, string Result, long ElapsedMs);
    readonly int port;
    readonly string path, directory;
    readonly Func<object> snapshot;
    readonly TimeSpan interval;
    readonly ManualResetEvent stopping = new(false);
    readonly Thread worker;
    internal volatile ProbeResult? LastProbe;
    internal volatile string? LastReport, ReportError;
    int failures;
    DateTime lastSaved = DateTime.MinValue;

    internal IngressDiagnostics(int port, string path, Func<object> snapshot, string directory, TimeSpan? interval = null)
    {
        this.port = port; this.path = path; this.snapshot = snapshot; this.directory = directory;
        this.interval = interval ?? TimeSpan.FromSeconds(10);
        worker = new Thread(Run) { IsBackground = true, Name = "IPv6Lens ingress diagnostics" };
    }

    internal object Status() => new { lastProbe = LastProbe, consecutiveFailures = Volatile.Read(ref failures),
        lastReport = LastReport, reportError = ReportError, running = worker.IsAlive };
    internal void Start() => worker.Start();

    void Run()
    {
        while (!stopping.WaitOne(interval))
        {
            try { Observe(Probe(port, path)); }
            catch (Exception ex) { ReportError = ex.GetType().Name; }
        }
    }

    internal static ProbeResult Probe(int port, string path, int timeoutMs = 1000)
    {
        var watch = Stopwatch.StartNew();
        string stage = "tcp_connect";
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Blocking = false;
            try { socket.Connect(IPAddress.Loopback, port); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress) { }
            if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectWrite))
                return new(DateTime.UtcNow, stage, "timeout", watch.ElapsedMilliseconds);
            var error = (SocketError)(int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
            if (error != SocketError.Success) return new(DateTime.UtcNow, stage, error.ToString(), watch.ElapsedMilliseconds);
            socket.Blocking = true;
            socket.SendTimeout = socket.ReceiveTimeout = timeoutMs;
            stage = "local_response";
            byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            int sent = 0;
            while (sent < request.Length) sent += socket.Send(request, sent, request.Length - sent, SocketFlags.None);
            byte[] reply = new byte[128]; int length = 0;
            var responseWatch = Stopwatch.StartNew();
            while (length < reply.Length && responseWatch.ElapsedMilliseconds < timeoutMs)
            {
                socket.ReceiveTimeout = Math.Max(1, timeoutMs - (int)responseWatch.ElapsedMilliseconds);
                int read = socket.Receive(reply, length, reply.Length - length, SocketFlags.None);
                if (read == 0) return new(DateTime.UtcNow, stage, "closed", watch.ElapsedMilliseconds);
                length += read;
                string text = Encoding.ASCII.GetString(reply, 0, length);
                if (text.Contains("\r\n\r\n"))
                    return new(DateTime.UtcNow, stage, text == "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n" ? "ok" : "unexpected_response", watch.ElapsedMilliseconds);
            }
            return new(DateTime.UtcNow, stage, "timeout", watch.ElapsedMilliseconds);
        }
        catch (SocketException ex) { return new(DateTime.UtcNow, stage, ex.SocketErrorCode.ToString(), watch.ElapsedMilliseconds); }
        catch (Exception ex) { return new(DateTime.UtcNow, stage, ex.GetType().Name, watch.ElapsedMilliseconds); }
    }

    internal void Observe(ProbeResult result)
    {
        LastProbe = result;
        if (result.Result == "ok") { Interlocked.Exchange(ref failures, 0); return; }
        if (Interlocked.Increment(ref failures) < 3 || result.TimeUtc - lastSaved < TimeSpan.FromMinutes(5)) return;
        // Limit write attempts too, including disk-full/permission failures.
        lastSaved = result.TimeUtc;
        try
        {
            ThreadPool.GetAvailableThreads(out int workers, out int io);
            object sockets;
            try
            {
                sockets = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                    .Where(c => c.LocalEndPoint.Port == port || c.RemoteEndPoint.Port == port)
                    .GroupBy(c => new { side = c.LocalEndPoint.Port == port ? "entry" : "caller", state = c.State.ToString() })
                    .Select(g => new { g.Key.side, g.Key.state, count = g.Count() }).ToArray();
            }
            catch (Exception ex) { sockets = new { error = ex.GetType().Name }; }
            var report = new
            {
                schema = "ipv6-download-assistant/ingress-1",
                version = typeof(DownloadRouter).Assembly.GetName().Version?.ToString(),
                timeUtc = result.TimeUtc, trigger = "three_consecutive_local_probe_failures",
                probe = result, consecutiveFailures = failures,
                threadPool = new { count = ThreadPool.ThreadCount, pending = ThreadPool.PendingWorkItemCount,
                    availableWorkers = workers, availableIo = io },
                sockets, router = snapshot()
            };
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "ingress-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            LastReport = Path.GetFileName(file); ReportError = null;
            foreach (var old in new DirectoryInfo(directory).GetFiles("ingress-*.json").OrderByDescending(f => f.Name).Skip(20))
                old.Delete();
        }
        catch (Exception ex) { ReportError = ex.GetType().Name; }
    }

    public void Dispose()
    {
        stopping.Set();
        if (!worker.IsAlive || worker.Join(4000)) stopping.Dispose();
    }
}
