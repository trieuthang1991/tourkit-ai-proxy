using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services.Mcp.OAuth;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Tải tài liệu client (CIMD) là chỗ máy chủ ta đi gọi một URL do NGƯỜI NGOÀI đưa — cửa SSRF kinh điển.
/// Chỉ https, đúng host trong danh sách trắng, cổng 443; không theo chuyển hướng; tối đa 64 KB; tối đa 5 s.
/// </summary>
public class ClientMetadataFetcherTests
{
    private const string Url = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private const string Cb = "https://claude.ai/api/mcp/auth_callback";
    private static string Doc(string clientId = Url) => "{\"client_id\":\"" + clientId + "\",\"client_name\":\"Claude\",\"redirect_uris\":[\"" + Cb + "\"]}";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return respond(request, ct);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public string? LastName;
        public HttpClient CreateClient(string name) { LastName = name; return new HttpClient(h, disposeHandler: false); }
    }

    private static HttpResponseMessage Json(string body, string type = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static (ClientMetadataFetcher F, Handler H, Factory Fa) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeProvider? clock = null, TimeSpan? timeout = null, string[]? extraHosts = null)
    {
        var h = new Handler(respond);
        var fa = new Factory(h);
        var f = new ClientMetadataFetcher(fa, new McpOAuthOptions("https://x.example.vn", cimdHosts: extraHosts),
            NullLogger<ClientMetadataFetcher>.Instance, clock, timeout);
        return (f, h, fa);
    }

    [Theory]
    [InlineData("https://claude.ai/oauth/x", true)]
    [InlineData("https://CLAUDE.AI/oauth/x", true)]
    [InlineData("https://chatgpt.com/oauth/client.json", true)]
    [InlineData("https://claude.ai:443/oauth/x", true)]
    [InlineData("http://claude.ai/oauth/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://evil.com/x", false)]
    [InlineData("https://claude.ai.evil.com/x", false)]
    [InlineData("https://evilclaude.ai/x", false)]
    [InlineData("https://user:pw@claude.ai/x", false)]
    [InlineData("https://claude.ai@evil.com/x", false)]
    [InlineData("https://claude.ai:8443/x", false)]
    [InlineData("https://claude.ai/x#frag", false)]
    [InlineData("https://claude.ai/x?a=1", false)]
    [InlineData("https://claude.ai/x?", false)]
    [InlineData("https://claude.ai/", false)]
    [InlineData("https://claude.ai", false)]
    [InlineData("ftp://claude.ai/x", false)]
    [InlineData("claude.ai/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Chi_tai_URL_https_dung_host_danh_sach_trang(string? url, bool ok)
        => Assert.Equal(ok, ClientMetadataFetcher.IsAllowedUrl(url, McpOAuthOptions.DefaultCimdHosts));

    [Fact]
    public async Task Host_ngoai_danh_sach_khong_bao_gio_goi_mang()
    {
        var (f, h, _) = Make((_, _) => Task.FromResult(Json(Doc())));
        foreach (var u in new[] { "https://169.254.169.254/latest/meta-data/iam", "http://claude.ai/x", "https://evil.com/c.json", "https://claude.ai:8443/x" })
        {
            Assert.False(f.CanFetch(u));
            Assert.Null(await f.FetchAsync(u, default));
        }
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Host_them_bang_cau_hinh_duoc_tai()
    {
        const string u = "https://mcp.example.org/client.json";
        var (f, h, fa) = Make((_, _) => Task.FromResult(Json(Doc(u))), extraHosts: new[] { "MCP.example.org" });
        var doc = await f.FetchAsync(u, default);
        Assert.NotNull(doc);
        Assert.Equal(1, h.Calls);
        Assert.Equal(ClientMetadataFetcher.HttpClientName, fa.LastName);
    }

    [Fact]
    public async Task Tai_lieu_hop_le_duoc_doc_va_dem_1_gio()
    {
        var clock = new ManualClock();
        var (f, h, _) = Make((_, _) => Task.FromResult(Json(Doc())), clock);
        var d = await f.FetchAsync(Url, default);
        Assert.NotNull(d);
        Assert.Equal(Url, d!.ClientId);
        Assert.Equal("Claude", d.ClientName);
        Assert.Equal(new[] { Cb }, d.RedirectUris);
        await f.FetchAsync(Url, default);
        clock.Advance(TimeSpan.FromMinutes(59));
        await f.FetchAsync(Url, default);
        Assert.Equal(1, h.Calls);
        clock.Advance(TimeSpan.FromMinutes(2));
        await f.FetchAsync(Url, default);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Loi_duoc_dem_ngan_roi_thu_lai()
    {
        var clock = new ManualClock();
        var (f, h, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)), clock);
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.Equal(1, h.Calls);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.Equal(2, h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Chuyen_huong_bi_tu_choi_khong_di_theo(HttpStatusCode code)
    {
        var (f, h, _) = Make((_, _) =>
        {
            var r = new HttpResponseMessage(code);
            r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return Task.FromResult(r);
        });
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Phan_hoi_tu_URL_khac_bi_tu_choi()
    {
        // Phòng khi handler lỡ bật AllowAutoRedirect: phản hồi phải đến từ đúng URL đã hỏi.
        var (f, _, _) = Make((req, _) =>
        {
            var r = Json(Doc());
            r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.com/c.json");
            return Task.FromResult(r);
        });
        Assert.Null(await f.FetchAsync(Url, default));
    }

    /// Luồng vô tận đếm số byte đã bị đọc — để chứng minh ta NGỪNG đọc chứ không tải hết 10 MB.
    private sealed class EndlessStream : Stream
    {
        public long BytesRead;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = (byte)' ';
            BytesRead += count;
            return count;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Tai_lieu_qua_64KB_bi_tu_choi_va_ngung_doc()
    {
        var s = new EndlessStream();
        var (f, _, _) = Make((_, _) =>
        {
            var c = new StreamContent(s);
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = c });
        });
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.InRange(s.BytesRead, ClientMetadataFetcher.MaxBytes, ClientMetadataFetcher.MaxBytes + 128 * 1024);
    }

    [Fact]
    public async Task Content_Length_qua_lon_bi_tu_choi()
    {
        var (f, _, _) = Make((_, _) =>
        {
            var r = Json(Doc() + new string(' ', 10 * 1024 * 1024));
            return Task.FromResult(r);
        });
        Assert.Null(await f.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Khong_phai_JSON_hoac_client_id_khac_URL_bi_tu_choi()
    {
        Assert.Null(await Make((_, _) => Task.FromResult(Json(Doc(), "text/html"))).F.FetchAsync(Url, default));
        Assert.Null(await Make((_, _) => Task.FromResult(Json(Doc("https://claude.ai/khac")))).F.FetchAsync(Url, default));
        Assert.Null(await Make((_, _) => Task.FromResult(Json("{rac"))).F.FetchAsync(Url, default));
        var evilRedirect = "{\"client_id\":\"" + Url + "\",\"redirect_uris\":[\"https://evil.com/cb\"]}";
        Assert.Null(await Make((_, _) => Task.FromResult(Json(evilRedirect))).F.FetchAsync(Url, default));
        // application/...+json cũng là JSON.
        Assert.NotNull(await Make((_, _) => Task.FromResult(Json(Doc(), "application/oauth-client+json"))).F.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Treo_qua_han_thi_bo_khong_cho_vo_han()
    {
        var (f, _, _) = Make(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(Doc());
        }, timeout: TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        Assert.Null(await f.FetchAsync(Url, default));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Mất {sw.Elapsed}");
    }

    [Fact]
    public async Task Dem_co_tran_cung_khong_phinh_vo_han()
    {
        // Đường dẫn do người ngoài chọn: mỗi URL lạ một mục đệm — phải có trần cứng, kể cả khi mục chưa hết hạn.
        var (f, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        for (var i = 0; i < ClientMetadataFetcher.MaxCacheEntries + 200; i++)
            await f.FetchAsync($"https://claude.ai/p{i}", default);
        Assert.InRange(f.CachedCount, 1, ClientMetadataFetcher.MaxCacheEntries);
    }

    [Fact]
    public async Task Dem_day_van_dem_tai_lieu_hop_le_moi()
    {
        var (f, h, _) = Make((req, _) => Task.FromResult(Json(Doc(req.RequestUri!.AbsoluteUri))));
        for (var i = 0; i < ClientMetadataFetcher.MaxCacheEntries + 50; i++)
            Assert.NotNull(await f.FetchAsync($"https://claude.ai/p{i}", default));
        Assert.InRange(f.CachedCount, 1, ClientMetadataFetcher.MaxCacheEntries);
        var before = h.Calls;
        var last = $"https://claude.ai/p{ClientMetadataFetcher.MaxCacheEntries + 49}";
        Assert.NotNull(await f.FetchAsync(last, default));
        Assert.Equal(before, h.Calls);   // mục mới nhất vẫn nằm trong đệm
    }

    [Fact]
    public async Task Nhieu_loi_goi_dong_thoi_cung_URL_chi_tai_mot_lan()
    {
        var gate = new TaskCompletionSource();
        var (f, h, _) = Make(async (_, _) => { await gate.Task; return Json(Doc()); });
        var tasks = Enumerable.Range(0, 10).Select(_ => f.FetchAsync(Url, default)).ToArray();
        await Task.Delay(50);
        gate.SetResult();
        var docs = await Task.WhenAll(tasks);
        Assert.All(docs, Assert.NotNull);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Nguoi_goi_huy_khong_lam_hong_luot_tai_chung()
    {
        var gate = new TaskCompletionSource();
        var (f, h, _) = Make(async (_, _) => { await gate.Task; return Json(Doc()); });
        using var cts = new CancellationTokenSource();
        var cancelled = f.FetchAsync(Url, cts.Token);
        var other = f.FetchAsync(Url, default);
        cts.Cancel();
        // Người huỷ được trả về NGAY (không chờ lượt tải chung); chặn 3 s để test không treo nếu sai.
        var ex = await Record.ExceptionAsync(() => cancelled.WaitAsync(TimeSpan.FromSeconds(3)));
        gate.SetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.NotNull(await other);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Loi_mang_khong_nem_ra_ngoai()
    {
        var (f, _, _) = Make((_, _) => throw new HttpRequestException("DNS chết"));
        Assert.Null(await f.FetchAsync(Url, default));
    }
}
