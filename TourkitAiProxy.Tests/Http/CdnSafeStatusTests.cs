using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TourkitAiProxy.Services.Http;

namespace TourkitAiProxy.Tests.Http;

/// Ca lỗi thật 06/11/2026 trên travelai.vn: app trả 502 kèm JSON báo lỗi đọc hiểu được,
/// nhưng Cloudflare thay luôn thân phản hồi bằng trang HTML của nó (6.390 byte,
/// "travelai.vn | 502: Bad gateway"). Giao diện gọi r.json() → "Unexpected token '&lt;'".
/// Máy dev không có Cloudflare nên JSON về nguyên vẹn — lỗi chỉ lộ trên bản chạy thật.
public class CdnSafeStatusTests
{
    [Theory]
    [InlineData(502)]   // Bad gateway — ĐÃ đo: Cloudflare thay thân phản hồi
    [InlineData(504)]   // Gateway timeout — cùng họ lỗi cổng nối, chặn luôn
    public void Hai_status_bi_CDN_nuot_thi_doi(int status)
        => Assert.True(CdnSafeStatusMiddleware.CanThay(status));

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]   // hết hạn mức AI — giao diện có nhánh riêng, đổi đi là hỏng
    [InlineData(500)]   // ĐÃ đo: Cloudflare cho qua nguyên văn
    [InlineData(503)]
    public void Status_khac_giu_nguyen(int status)
        => Assert.False(CdnSafeStatusMiddleware.CanThay(status));

    [Fact]
    public async Task Endpoint_tra_502_thi_ra_ngoai_thanh_422_va_giu_status_goc_o_header()
    {
        var ctx = await ChayAsync(502);

        Assert.Equal(CdnSafeStatusMiddleware.SafeStatus, ctx.Response.StatusCode);
        Assert.Equal("502", ctx.Response.Headers["X-Upstream-Status"]);
    }

    [Fact]
    public async Task Endpoint_tra_504_cung_thanh_422()
    {
        var ctx = await ChayAsync(504);

        Assert.Equal(CdnSafeStatusMiddleware.SafeStatus, ctx.Response.StatusCode);
        Assert.Equal("504", ctx.Response.Headers["X-Upstream-Status"]);
    }

    [Fact]
    public async Task Endpoint_tra_200_thi_khong_dung_den_va_khong_them_header()
    {
        var ctx = await ChayAsync(200);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("X-Upstream-Status"));
    }

    [Fact]
    public async Task Het_han_muc_AI_van_la_429_de_giao_dien_mo_hop_nap_luot()
    {
        var ctx = await ChayAsync(429);

        Assert.Equal(429, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Status_chua_doi_khi_con_trong_pipeline_chi_doi_luc_day_header_di()
    {
        var (ctx, dayHeaderDi) = Dung(502);
        await new CdnSafeStatusMiddleware(c => { c.Response.StatusCode = 502; return Task.CompletedTask; })
            .InvokeAsync(ctx);

        // Middleware ghi log/đo đạc chạy SAU endpoint vẫn phải thấy 502 thật.
        Assert.Equal(502, ctx.Response.StatusCode);

        await dayHeaderDi();
        Assert.Equal(CdnSafeStatusMiddleware.SafeStatus, ctx.Response.StatusCode);
    }

    // Chạy trọn một lượt: endpoint gán `statusCuaEndpoint` → middleware → đẩy header đi.
    private static async Task<HttpContext> ChayAsync(int statusCuaEndpoint)
    {
        var (ctx, dayHeaderDi) = Dung(statusCuaEndpoint);
        var mw = new CdnSafeStatusMiddleware(c =>
        {
            c.Response.StatusCode = statusCuaEndpoint;
            return Task.CompletedTask;
        });
        await mw.InvokeAsync(ctx);
        await dayHeaderDi();
        return ctx;
    }

    // DefaultHttpContext chỉ GHI NHẬN callback OnStarting chứ không bao giờ gọi, mà toàn bộ việc
    // đổi status nằm trong đó → phải tự dựng response feature chạy được callback.
    private static (HttpContext Ctx, Func<Task> DayHeaderDi) Dung(int _)
    {
        var feature = new ResponseFeatureGiaLap();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(feature);
        return (new DefaultHttpContext(features), feature.ChayCallbackAsync);
    }

    private sealed class ResponseFeatureGiaLap : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Cb, object State)> _dangCho = new();

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => false;

        public void OnStarting(Func<object, Task> callback, object state) => _dangCho.Add((callback, state));
        public void OnCompleted(Func<object, Task> callback, object state) { }

        public async Task ChayCallbackAsync()
        {
            foreach (var (cb, state) in _dangCho) await cb(state);
        }
    }
}
