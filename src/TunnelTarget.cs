using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace IPv6Lens;

// Read routing metadata only; replay every byte without terminating TLS.
internal static class TunnelTarget
{
    internal sealed record Result(string? Host, byte[] Prefix);
    internal const int Limit = 65536;
    internal static async Task<Result> Read(Stream stream, CancellationToken ct)
    {
        using var prefix = new MemoryStream();
        byte[] first = new byte[1];
        await stream.ReadExactlyAsync(first, ct);
        prefix.Write(first);
        if (first[0] == 22)
        {
            using var handshake = new MemoryStream();
            for (int record = 0; record < 16; record++)
            {
                byte[] header = new byte[5];
                if (record == 0)
                {
                    header[0] = first[0];
                    await stream.ReadExactlyAsync(header.AsMemory(1), ct);
                    prefix.Write(header, 1, 4);
                }
                else
                {
                    await stream.ReadExactlyAsync(header, ct);
                    prefix.Write(header);
                }
                int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
                if (header[0] != 22 || header[1] != 3 || length == 0 || length > 18432 ||
                    prefix.Length + length > Limit) throw new IOException("Invalid TLS routing record");
                byte[] payload = new byte[length];
                await stream.ReadExactlyAsync(payload, ct);
                prefix.Write(payload);
                handshake.Write(payload);
                byte[] hello = handshake.ToArray();
                if (hello.Length < 4) continue;
                int size = hello[1] << 16 | hello[2] << 8 | hello[3];
                if (hello[0] != 1 || size > Limit - 4) throw new IOException("Expected bounded ClientHello");
                if (hello.Length >= size + 4)
                    return new(ParseHello(hello.AsSpan(4, size)), prefix.ToArray());
            }
            throw new IOException("Too many TLS routing records");
        }
        if (first[0] is >= (byte)'A' and <= (byte)'Z')
        {
            int end = first[0];
            while (end != 0x0d0a0d0a)
            {
                if (prefix.Length >= 32768) throw new IOException("HTTP routing header too large");
                await stream.ReadExactlyAsync(first, ct);
                prefix.Write(first); end = end << 8 | first[0];
            }
            string[] lines = Encoding.Latin1.GetString(prefix.ToArray()).Split("\r\n");
            var names = lines.Skip(1).Where(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
            string? host = null;
            if (names.Length == 1 && Uri.TryCreate("http://" + names[0][5..].Trim(), UriKind.Absolute, out var url) &&
                url.UserInfo.Length == 0 && url.AbsolutePath == "/" && url.Query.Length == 0 && url.Fragment.Length == 0)
                host = Domain(url.IdnHost);
            return new(host, prefix.ToArray());
        }
        return new(null, prefix.ToArray());
    }
    static string? Domain(string host)
    {
        host = host.TrimEnd('.');
        return host.Length is > 0 and <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns &&
            !IPAddress.TryParse(host, out _) ? host.ToLowerInvariant() : null;
    }
    static string? ParseHello(ReadOnlySpan<byte> data)
    {
        int at = 34;
        if (data.Length <= at) throw new IOException("Truncated ClientHello");
        int session = data[at++];
        if (at + session + 2 > data.Length) throw new IOException("Truncated TLS session");
        at += session;
        int ciphers = BinaryPrimitives.ReadUInt16BigEndian(data[at..]); at += 2;
        if (ciphers == 0 || (ciphers & 1) != 0 || at + ciphers + 1 > data.Length)
            throw new IOException("Invalid TLS ciphers");
        at += ciphers;
        int compression = data[at++];
        if (compression == 0 || at + compression > data.Length) throw new IOException("Invalid TLS compression");
        at += compression;
        if (at == data.Length) return null;
        if (at + 2 > data.Length) throw new IOException("Truncated TLS extensions");
        int extensions = BinaryPrimitives.ReadUInt16BigEndian(data[at..]); at += 2;
        if (at + extensions != data.Length) throw new IOException("Invalid TLS extensions");
        int end = at + extensions;
        while (at < end)
        {
            if (at + 4 > end) throw new IOException("Truncated TLS extension");
            int kind = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]); at += 4;
            if (at + length > end) throw new IOException("Invalid TLS extension length");
            if (kind == 0)
            {
                if (length < 2 || BinaryPrimitives.ReadUInt16BigEndian(data[at..]) != length - 2)
                    throw new IOException("Invalid TLS server name list");
                int name = at + 2, namesEnd = at + length;
                while (name < namesEnd)
                {
                    if (name + 3 > namesEnd) throw new IOException("Truncated TLS server name");
                    byte type = data[name++];
                    int size = BinaryPrimitives.ReadUInt16BigEndian(data[name..]); name += 2;
                    if (name + size > namesEnd) throw new IOException("Invalid TLS server name");
                    if (type == 0)
                    {
                        var value = data.Slice(name, size);
                        foreach (byte b in value)
                            if (b < 33 || b > 126) throw new IOException("Invalid TLS server name characters");
                        return Domain(Encoding.ASCII.GetString(value));
                    }
                    name += size;
                }
                return null;
            }
            at += length;
        }
        return null;
    }
}
