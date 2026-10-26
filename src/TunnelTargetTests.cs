using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal static class TunnelTargetTests
{
    sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
    }

    static byte[] Hello(string? host)
    {
        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);
        body.AddRange(new byte[] { 0, 0, 2, 0x13, 1, 1, 0 });
        if (host != null)
        {
            byte[] name = Encoding.ASCII.GetBytes(host);
            void Length(int value) { body.Add((byte)(value >> 8)); body.Add((byte)value); }
            Length(name.Length + 9); Length(0); Length(name.Length + 5);
            Length(name.Length + 3); body.Add(0); Length(name.Length); body.AddRange(name);
        }
        byte[] result = new byte[body.Count + 4];
        result[0] = 1; result[1] = (byte)(body.Count >> 16);
        result[2] = (byte)(body.Count >> 8); result[3] = (byte)body.Count;
        body.CopyTo(result, 4);
        return result;
    }

    static byte[] Record(byte[] hello, int split = 0)
    {
        using var result = new MemoryStream();
        void Add(ReadOnlySpan<byte> payload)
        {
            byte[] header = { 22, 3, 3, 0, 0 };
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3), checked((ushort)payload.Length));
            result.Write(header); result.Write(payload);
        }
        if (split > 0) { Add(hello.AsSpan(0, split)); Add(hello.AsSpan(split)); }
        else Add(hello);
        return result.ToArray();
    }

    internal static void Run(string path)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new IOException(name); checks.Add(name); }
        try
        {
            Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var ct = deadline.Token;
                byte[] hello = Record(Hello("test.bilivideo.com"), 2);
                using (var stream = new Fragmented(hello))
                {
                    var target = await TunnelTarget.Read(stream, ct);
                    Check(target.Host == "test.bilivideo.com" && target.Prefix.SequenceEqual(hello),
                        "ClientHello split across records and tiny network reads is replayed byte for byte");
                }
                byte[] withTail = Record(Hello("test.quark.cn").Concat(new byte[] { 1, 2, 3 }).ToArray());
                using (var stream = new Fragmented(withTail))
                {
                    var target = await TunnelTarget.Read(stream, ct);
                    Check(target.Host == "test.quark.cn" && target.Prefix.SequenceEqual(withTail),
                        "Bytes after ClientHello in the same record are not lost");
                }
                foreach (string headers in new[] {
                    "GET /signed?x=a%2Fb HTTP/1.1\r\nHost: TEST.QUARK.CN:443\r\nRange: bytes=10-20\r\n\r\n",
                    "GET / HTTP/1.1\r\nHost: first.quark.cn\r\nHost: second.quark.cn\r\n\r\n" })
                {
                    byte[] bytes = Encoding.ASCII.GetBytes(headers);
                    using var stream = new Fragmented(bytes);
                    var target = await TunnelTarget.Read(stream, ct);
                    Check(target.Prefix.SequenceEqual(bytes) && target.Host ==
                        (headers.Contains("Range:") ? "test.quark.cn" : null),
                        headers.Contains("Range:") ? "HTTP Host extraction preserves path, signature and Range bytes" :
                        "Duplicate HTTP Host headers do not choose an arbitrary routing hostname");
                }
                using (var stream = new Fragmented(Record(Hello(null))))
                    Check((await TunnelTarget.Read(stream, ct)).Host == null,
                        "TLS without SNI remains explicitly unidentified");
                foreach (byte[] bad in new[] {
                    new byte[] { 22, 3, 3, 255, 255 },
                    new byte[] { 22, 3, 3, 0, 4, 1, 255, 255, 255 },
                    Record(new byte[] { 1, 0, 0, 1, 0 }),
                    Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + new string('a', 32768)),
                    new byte[] { 22, 3 } })
                {
                    bool rejected = false;
                    using var stream = new Fragmented(bad);
                    try { await TunnelTarget.Read(stream, ct); } catch (IOException) { rejected = true; }
                    Check(rejected, "Oversized or truncated routing metadata is rejected (" + checks.Count + ")");
                }
                await using var router = new DownloadRouter { ProductionPolicies = true };
                await router.Start(false);
                router.Enable("bilibili", true);
                int originalCalls = 0;
                router.Resolve = _ => Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") });
                router.OriginalResolve = _ => { Interlocked.Increment(ref originalCalls); return Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") }); };
                async Task Probe(byte[] payload)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, router.ServicePort("bilibili"), ct);
                    var stream = client.GetStream();
                    await stream.WriteAsync("CONNECT 192.0.2.1:443 HTTP/1.1\r\nHost: 192.0.2.1:443\r\n\r\n"u8.ToArray(), ct);
                    int end = 0; byte[] one = new byte[1];
                    while (end != 0x0d0a0d0a)
                    {
                        await stream.ReadExactlyAsync(one, ct); end = end << 8 | one[0];
                    }
                    await stream.WriteAsync(payload, ct);
                    Check(await stream.ReadAsync(one, ct) == 0,
                        "Failed raw-IP tunnel closes without injecting HTTP into encrypted traffic");
                }
                await Probe(Record(Hello(null)));
                Check(router.Counters["bilibili"].LastFailure?.Reason == "no_aaaa" &&
                    router.Counters["bilibili"].V4 == 0 && router.Counters["global"].Failures == 0 &&
                    originalCalls == 1,
                    "Missing SNI retains the native service and cannot use the disabled-global fallback");
                await Probe(new byte[] { 22, 3, 3, 255, 255 });
                Check(router.Counters["bilibili"].LastFailure?.Stage == "tunnel_target" &&
                    router.Counters["bilibili"].Failures == 2 && originalCalls == 1,
                    "Malformed routing metadata is diagnosed on the correct service before upstream dialing");
                Check(DownloadRouter.Sites.Where(s => s.Id != "global").Select(s => router.ServicePort(s.Id))
                    .Append(router.Port).Distinct().Count() == DownloadRouter.Sites.Length,
                    "Each native service has a separate immutable routing context");
            }).GetAwaiter().GetResult();
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "passed", checks }));
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(path, JsonSerializer.Serialize(new { state = "failed", checks, error = ex.ToString() }));
        }
    }
}
