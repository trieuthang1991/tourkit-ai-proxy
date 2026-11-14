using System.Collections.Concurrent;
using System.Text;
using TourkitAiProxy.Domain.Mcp;

namespace TourkitAiProxy.Services.Mcp.OAuth;

/// <summary>Tải tài liệu client OAuth (CIMD) theo URL client_id. Đứng sau giao diện để dịch vụ OAuth test được không cần mạng.</summary>
public interface IClientMetadataFetcher
{
    /// <summary>URL có được phép tải không — thuần cú pháp, không DNS, không mạng.</summary>
    bool CanFetch(string? clientIdUrl);
    /// <summary>Tài liệu đã kiểm (client_id = URL, redirect trong danh sách trắng); null nếu không tải/không hợp lệ.</summary>
    Task<ClientMetadata?> FetchAsync(string clientIdUrl, CancellationToken ct);
}

/// <summary>
/// Tải CIMD (Client ID Metadata Document) — Claude.ai và ChatGPT dùng URL làm client_id, ta phải tự đi
/// đọc URL đó. Đây là chỗ máy chủ gọi một địa chỉ do NGƯỜI NGOÀI đưa vào, nên chặn SSRF nhiều lớp:
/// <list type="bullet">
/// <item>Chỉ <c>https</c>, cổng 443, host đúng TỪNG CHỮ trong danh sách trắng (không so đuôi tên miền),
/// không userinfo, không fragment. Host ngoài danh sách thì KHÔNG phân giải DNS, không mở kết nối.</item>
/// <item>Client HTTP có tên <see cref="HttpClientName"/> tắt tự theo chuyển hướng; mọi mã khác 200 đều
/// là lỗi, và phản hồi phải đến từ đúng URL đã hỏi (phòng ai đó lỡ bật lại chuyển hướng).</item>
/// <item>Tối đa <see cref="MaxBytes"/>: Content-Length lớn hơn thì bỏ không đọc; không có Content-Length
/// thì đọc tới ngưỡng rồi NGỪNG (không tải nốt 10 MB). Tối đa 5 giây cho cả lượt.</item>
/// </list>
/// Đệm theo URL: thành công 1 giờ (tài liệu ít đổi, Claude/ChatGPT gọi authorize liên tục), lỗi 60 giây
/// (để một URL hỏng không biến mỗi lượt authorize thành một lượt gọi ra ngoài); trần cứng
/// <see cref="MaxCacheEntries"/> mục; cùng URL thì các lời gọi đồng thời dùng chung một lượt tải.
/// </summary>
public sealed class ClientMetadataFetcher : IClientMetadataFetcher
{
    public const string HttpClientName = "mcp-cimd";
    public const int MaxBytes = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(60);

    /// <summary>Trần CỨNG số URL trong đệm: client_id do người ngoài chọn, không để họ làm phình RAM.</summary>
    public const int MaxCacheEntries = 500;
    internal int CachedCount => _cache.Count;

    private readonly IHttpClientFactory _factory;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<ClientMetadataFetcher> _log;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, (ClientMetadata? Doc, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheWrite = new();
    // Mỗi URL tối đa MỘT lượt tải đang chạy: Claude/ChatGPT bắn vài request authorize cùng lúc, không nhân
    // thành vài lượt gọi ra ngoài. Lazy để hai luồng cùng GetOrAdd không cùng khởi động tải.
    private readonly ConcurrentDictionary<string, Lazy<Task<ClientMetadata?>>> _inFlight = new(StringComparer.Ordinal);

    public ClientMetadataFetcher(IHttpClientFactory factory, McpOAuthOptions options, ILogger<ClientMetadataFetcher> log,
        TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        _factory = factory; _options = options; _log = log;
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Kiểm cú pháp URL client_id. Không có bước nào chạm mạng.</summary>
    public static bool IsAllowedUrl(string? url, IReadOnlyCollection<string> hosts)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 200) return false;
        // Kiểm trên chuỗi thô trước khi để Uri "chuẩn hoá giùm": '#', '@', '\' là các mẹo đánh lừa bộ tách host.
        // '?': tài liệu CIMD là một URL cố định — query cho phép sinh vô số client_id từ một tài liệu
        // (lách/phình đệm), nên không nhận.
        if (url.IndexOfAny(new[] { '?', '#', '@', '\\' }) >= 0) return false;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || u.Port != 443 || !string.IsNullOrEmpty(u.UserInfo)) return false;
        if (u.HostNameType != UriHostNameType.Dns) return false;   // IP thô (169.254.169.254, 127.0.0.1…) không bao giờ qua
        if (u.AbsolutePath.Length <= 1) return false;               // CIMD bắt buộc có đường dẫn
        var host = u.IdnHost.ToLowerInvariant();
        foreach (var h in hosts)
            if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool CanFetch(string? clientIdUrl) => IsAllowedUrl(clientIdUrl, _options.CimdHosts);

    public async Task<ClientMetadata?> FetchAsync(string clientIdUrl, CancellationToken ct)
    {
        if (!CanFetch(clientIdUrl)) return null;
        if (_cache.TryGetValue(clientIdUrl, out var hit) && hit.Expires > _clock.GetUtcNow()) return hit.Doc;

        var lazy = _inFlight.GetOrAdd(clientIdUrl, u => new Lazy<Task<ClientMetadata?>>(() => LoadAndCacheAsync(u)));
        // Lượt tải chung KHÔNG gắn ct của một người gọi (người này huỷ không làm hỏng người khác); nó tự giới
        // hạn bằng timeout 5 giây. Người huỷ thì được trả về ngay bằng OperationCanceledException.
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<ClientMetadata?> LoadAndCacheAsync(string url)
    {
        try
        {
            var doc = await DownloadAsync(url, CancellationToken.None);
            Remember(url, doc);
            return doc;
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }

    private void Remember(string url, ClientMetadata? doc)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheWrite)
        {
            if (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(url))
            {
                foreach (var kv in _cache)
                    if (kv.Value.Expires <= now) _cache.TryRemove(kv.Key, out _);
                if (_cache.Count >= MaxCacheEntries)
                {
                    // Đầy mà toàn mục còn hạn: lỗi mới thì thôi không đệm (chỉ là tối ưu); tài liệu hợp lệ thì
                    // đẩy mục sắp hết hạn nhất ra để client thật không bị kẹt sau rác.
                    if (doc == null) return;
                    var oldest = _cache.MinBy(kv => kv.Value.Expires);
                    _cache.TryRemove(oldest.Key, out _);
                }
            }
            _cache[url] = (doc, now + (doc != null ? SuccessTtl : FailureTtl));
        }
    }

    private async Task<ClientMetadata?> DownloadAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var http = _factory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (res.StatusCode != System.Net.HttpStatusCode.OK)
                return Fail(url, $"mã HTTP {(int)res.StatusCode}");
            if (res.RequestMessage?.RequestUri is { } final && final.AbsoluteUri != new Uri(url).AbsoluteUri)
                return Fail(url, "phản hồi đến từ URL khác (bị chuyển hướng)");
            var media = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return Fail(url, $"Content-Type '{media}' không phải JSON");
            if (res.Content.Headers.ContentLength is > MaxBytes)
                return Fail(url, $"tài liệu {res.Content.Headers.ContentLength} byte, quá {MaxBytes}");

            await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[MaxBytes + 1];
            var total = 0;
            while (total < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token);
                if (n == 0) break;
                total += n;
            }
            if (total > MaxBytes) return Fail(url, $"tài liệu vượt {MaxBytes} byte");

            var json = Encoding.UTF8.GetString(buf, 0, total);
            var (doc, err) = ClientMetadataDocument.ParseCimd(url, json, _options.ExtraRedirectUris);
            return doc ?? Fail(url, $"tài liệu không hợp lệ ({err})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(url, $"quá {_timeout.TotalSeconds:0.#} giây");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return Fail(url, ex.Message);
        }
    }

    private ClientMetadata? Fail(string url, string why)
    {
        _log.LogWarning("Không tải được tài liệu client OAuth {Url}: {Why}", url, why);
        return null;
    }
}
