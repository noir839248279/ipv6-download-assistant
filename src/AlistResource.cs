using System.Net;
using System.Text;
using System.Text.Json;

namespace IPv6Lens;

internal sealed record AlistEntry(string Name, string Path, bool Directory, long Size, string LinkPath);
internal sealed record AlistPage(AlistEntry[] Entries, bool More);
internal sealed record AlistResource(Uri Url, Dictionary<string, string> Headers);

// Only user-selected local AList metadata crosses this boundary. No cookies or
// database secrets are discovered, and the API client never fetches file bodies.
internal sealed class AlistResourceClient : IDisposable
{
    readonly HttpClient http;
    string token = "";
    const int ResponseLimit = 1024 * 1024;
    internal AlistResourceClient(int port = 5244, HttpMessageHandler? transport = null)
    {
        if (port is < 1 or > 65535) throw new InvalidOperationException("AList 端口无效");
        http = new(transport ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Client-Id", "ipv6lens-node-picker");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IPv6Lens-NodePicker/1.0");
    }
    async Task<JsonElement> Request(string endpoint, object body, CancellationToken ct, bool auth = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (auth && token.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw Error((int)response.StatusCode);
        if (response.Content.Headers.ContentLength > ResponseLimit) throw new IOException("AList 返回内容过大");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ResponseLimit + 1 - (int)content.Length)), ct);
            if (read == 0) break;
            content.Write(buffer, 0, read);
            if (content.Length > ResponseLimit) throw new IOException("AList 返回内容过大");
        }
        using var doc = JsonDocument.Parse(content.ToArray());
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 200)
            throw Error(code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 500);
        if (!root.TryGetProperty("data", out var data)) throw new IOException("AList 返回数据不完整");
        return data.Clone();
    }
    static InvalidOperationException Error(int code) => new(code switch
    {
        400 => "AList 登录或请求失败，请核对账号、密码",
        401 => "请先登录本机 AList",
        402 => "请填写正确的 AList 两步验证码",
        403 => "AList 权限不足；取原始链接需要管理员账号",
        429 => "AList 暂时限制请求，请稍后再试；没有自动重试",
        _ => "本机 AList 请求未完成（" + code + "），未更改其设置"
    });
    internal async Task Login(string username, string password, string otp, CancellationToken ct)
    {
        token = "";
        var data = await Request("api/auth/login", new { username, password, otp_code = otp }, ct, false);
        token = data.GetProperty("token").GetString() ?? "";
        if (token.Length == 0) throw new IOException("AList 没有返回登录凭据");
    }
    internal async Task<AlistPage> List(string path, string password, int page, CancellationToken ct)
    {
        var data = await Request("api/fs/list", new { path, password, page, per_page = 100, refresh = false }, ct);
        var rows = new List<AlistEntry>();
        if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
            {
                string name = item.GetProperty("name").GetString() ?? "";
                // Never use object storage's opaque path as a virtual API path.
                if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\')) continue;
                string relative = path.TrimEnd('/') + "/" + name;
                string actual = item.TryGetProperty("virtual_path", out var virtualPath) ? virtualPath.GetString() ?? relative : relative;
                if (!actual.StartsWith('/') || actual.Contains('\r') || actual.Contains('\n')) continue;
                rows.Add(new(name, relative, item.GetProperty("is_dir").GetBoolean(),
                    item.TryGetProperty("size", out var size) ? size.GetInt64() : 0, actual));
            }
        bool more = data.TryGetProperty("has_more", out var hasMore) ? hasMore.GetBoolean() :
            data.TryGetProperty("total", out var total) && total.GetInt64() > page * 100;
        return new(rows.OrderByDescending(e => e.Directory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), more);
    }
    internal async Task<AlistResource> Resource(string path, CancellationToken ct) =>
        ParseResource(await Request("api/fs/link", new { path }, ct));
    internal static AlistResource ParseResource(JsonElement data)
    {
        string text = data.GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || !DownloadRouter.QuarkDownloadHost(url.IdnHost))
            throw new InvalidOperationException("所选文件没有返回夸克原始下载地址；未开始测速");
        url = ContentNodes.SpeedUrl(url.IdnHost, text);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("header", out var raw) && raw.ValueKind == JsonValueKind.Object)
            foreach (var property in raw.EnumerateObject())
            {
                string value = property.Value.ValueKind == JsonValueKind.Array
                    ? string.Join(property.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ",
                        property.Value.EnumerateArray().Select(v => v.GetString()))
                    : property.Value.GetString() ?? "";
                headers.Add(property.Name, value);
            }
        headers = ContentNodes.Headers(JsonSerializer.Serialize(headers));
        return new(url, headers);
    }
    public void Dispose() { token = ""; http.Dispose(); }
}
