using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Cửa vào <c>/mcp</c>: chỉ tồn tại khi bật cờ, chỉ cho qua khi khoá còn sống VÀ phiên gốc còn, và
/// không bao giờ cho qua khi không kiểm được (CSDL chết = đóng cửa, không phải mở toang).
/// </summary>
public class McpEndpointGuardTests
{
    private static string Src(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    /// Thân một hàm theo tên — đủ cho guard nguồn, không cần parser C#.
    private static string Body(string src, string signature)
    {
        var i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Không thấy '{signature}' — guard đã lạc khỏi mã nguồn thật.");
        var open = src.IndexOf('{', i);
        int depth = 0, j = open;
        for (; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) break;
        }
        return src.Substring(open, j - open + 1);
    }

    [Fact]
    public void Tao_khoa_khong_co_JSON_thi_tra_400_chu_khong_500()
    {
        var body = Body(Src("TourkitAiProxy.Endpoints/McpEndpoints.cs"), "g.MapPost(\"\"");
        Assert.Contains("HasJsonContentType()", body);
        Assert.Contains("catch (InvalidOperationException)", body);
    }

    // ── Cờ ────────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_chi_map_khi_bat_co()
    {
        var body = Body(Src("TourkitAiProxy.Endpoints/EndpointRegistration.cs"),
            "private static void MapMcp(WebApplication app, IConfiguration cfg)");

        // Nhánh BẬT: map cả khoá lẫn máy chủ /mcp, rồi return trước nhánh tắt.
        var batCo = body.IndexOf("if (FeatureFlags.Mcp(cfg))", StringComparison.Ordinal);
        var ret = body.IndexOf("return;", StringComparison.Ordinal);
        var mapServer = body.IndexOf("MapMcpServer()", StringComparison.Ordinal);
        Assert.True(batCo >= 0 && mapServer > batCo && mapServer < ret,
            "MapMcpServer() phải nằm TRONG nhánh if (FeatureFlags.Mcp(cfg)) — map ngoài cờ là mở cửa dữ liệu công ty khi cờ tắt.");

        // Nhánh TẮT: /mcp phải trả 404 JSON tường minh, không rơi xuống SPA fallback (200 + index.html).
        var tat = body.Substring(ret);
        Assert.Contains("McpEndpoints.McpPath", tat);
        Assert.Contains("McpEndpoints.TokensPrefix", tat);
        Assert.Equal("/mcp", McpEndpoints.McpPath);
    }

    [Fact]
    public void Dang_ky_may_chu_MCP_chi_khi_bat_co()
    {
        var src = Src("TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("if (FeatureFlags.Mcp(cfg)) s.AddMcp(FeatureFlags.McpOAuth(cfg));", src);
        // Đăng ký SDK + kho khoá chỉ nằm trong AddMcp — không còn lẫn trong cụm widget.
        Assert.DoesNotContain("IMcpTokenStore", Body(src, "private static void AddWidgetChat(this IServiceCollection s)"));
        var addMcp = Body(src, "private static void AddMcp(this IServiceCollection s, bool oauthOn)");
        // Cổng /mcp chỉ nhận tkoat_ khi cờ OAuth có hiệu lực: kho OAuth + cấu hình chỉ truyền vào authenticator khi oauthOn.
        Assert.Contains("oauthStore: oauthOn ?", addMcp);
        Assert.Contains("oauth: oauthOn ?", addMcp);
        Assert.Contains("AddMcpServer(", addMcp);
        Assert.Contains("IMcpTokenStore", addMcp);
        // Thiếu dòng này thì tools/list trả -32601 "not available" (đã dính lúc kiểm thật ngày 10/11).
        Assert.Contains("o.ToolCollection = new();", addMcp);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"AddMcpServer\(").Count);
    }

    [Fact]
    public void Co_McpActions_thieu_key_la_tat()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Assert.False(FeatureFlags.McpActions(cfg));
        var on = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Features:McpActions"] = "true" }).Build();
        Assert.True(FeatureFlags.McpActions(on));
    }

    // ── Xác thực ──────────────────────────────────────────────────────────

    private sealed record Ctx(McpAuthMiddleware Mw, McpAuthenticator Auth, CountingStore Store, Func<bool> NextCalled);

    private static Ctx Build(IMcpTokenStore inner, Func<string, McpSessionOwner?> sessions, ManualClock? clock = null)
    {
        var store = new CountingStore(inner);
        var auth = new McpAuthenticator(store, sessions, NullLogger<McpAuthenticator>.Instance, clock ?? new ManualClock());
        var called = false;
        var mw = new McpAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        return new Ctx(mw, auth, store, () => called);
    }

    private static DefaultHttpContext Req(string? authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/mcp";
        if (authorization != null) ctx.Request.Headers.Authorization = authorization;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Error(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    private static readonly Func<string, McpSessionOwner?> PhienCon = sid => sid switch
    {
        "sid-a" => new McpSessionOwner("t1", "a"),
        "sid-b" => new McpSessionOwner("t2", "b"),
        _ => null,
    };

    [Fact]
    public async Task Khong_co_Bearer_tra_401_tieng_Viet()
    {
        var c = Build(new FakeMcpTokenStore(), PhienCon);
        foreach (var h in new string?[] { null, "", "Basic abc", "Bearer ", "tkmcp_" + new string('a', 32) })
        {
            var ctx = Req(h);
            await c.Mw.InvokeAsync(ctx, c.Auth);
            Assert.Equal(401, ctx.Response.StatusCode);
            Assert.Equal("Bearer", ctx.Response.Headers.WWWAuthenticate.ToString());
            Assert.StartsWith("application/json", ctx.Response.ContentType);
            Assert.Contains("Thiếu khoá MCP", Error(ctx));
            Assert.False(c.NextCalled());
        }
        Assert.Equal(0, c.Store.FindCalls);   // không có khoá thì không chạm CSDL
    }

    [Fact]
    public async Task Khoa_sai_dinh_dang_hoac_khong_ton_tai_tra_401()
    {
        var c = Build(new FakeMcpTokenStore(), PhienCon);
        foreach (var k in new[] { "trav_abc", "tkmcp_xyz", McpTokens.New() })
        {
            var ctx = Req("Bearer " + k);
            await c.Mw.InvokeAsync(ctx, c.Auth);
            Assert.Equal(401, ctx.Response.StatusCode);
            Assert.Equal("Bearer", ctx.Response.Headers.WWWAuthenticate.ToString());
            Assert.Contains("không hợp lệ", Error(ctx));
            Assert.False(c.NextCalled());
        }
        Assert.Equal(1, c.Store.FindCalls);   // sai định dạng bị loại trước, không tra CSDL
    }

    [Fact]
    public async Task Khoa_dung_dat_McpCaller_dung_tenant()
    {
        var inner = new FakeMcpTokenStore();
        var (_, keyA) = await inner.CreateAsync("t1", "a", "sid-a", "may A", default);
        var (idB, keyB) = await inner.CreateAsync("t2", "b", "sid-b", "may B", default);
        var c = Build(inner, PhienCon);

        var ctx = Req("Bearer " + keyB);
        await c.Mw.InvokeAsync(ctx, c.Auth);
        Assert.True(c.NextCalled());
        var caller = Assert.IsType<McpCaller>(ctx.Items[McpAuthenticator.ItemKey]);
        Assert.Equal(new McpCaller(idB, "sid-b", "t2", "b"), caller);

        // "bearer" chữ thường cũng nhận (RFC 6750: scheme không phân biệt hoa thường).
        var ctxA = Req("bearer " + keyA);
        await c.Mw.InvokeAsync(ctxA, c.Auth);
        Assert.Equal("t1", ((McpCaller)ctxA.Items[McpAuthenticator.ItemKey]!).TenantId);
    }

    [Fact]
    public async Task Phien_goc_het_tra_401_noi_ro_tao_khoa_moi()
    {
        var inner = new FakeMcpTokenStore();
        var (_, key) = await inner.CreateAsync("t1", "a", "sid-da-het", "may", default);
        var c = Build(inner, PhienCon);

        var ctx = Req("Bearer " + key);
        await c.Mw.InvokeAsync(ctx, c.Auth);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal("Phiên gốc đã hết — tạo khoá mới", Error(ctx));
        Assert.False(c.NextCalled());
    }

    [Fact]
    public async Task Phien_goc_cua_nguoi_khac_thi_tu_choi()
    {
        // Khoá ghi (t1, a) nhưng sessionId lại trỏ phiên của (t2, b) — dữ liệu lệch thì đóng cửa,
        // tuyệt đối không cho khoá của A chạy bằng quyền của B.
        var inner = new FakeMcpTokenStore();
        var (_, key) = await inner.CreateAsync("t1", "a", "sid-b", "may", default);
        var c = Build(inner, PhienCon);

        var ctx = Req("Bearer " + key);
        await c.Mw.InvokeAsync(ctx, c.Auth);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.False(c.NextCalled());
    }

    [Fact]
    public async Task CSDL_loi_thi_dong_cua_khong_cho_qua()
    {
        var c = Build(new ThrowingStore(), PhienCon);
        var ctx = Req("Bearer " + McpTokens.New());
        await c.Mw.InvokeAsync(ctx, c.Auth);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Contains("thử lại", Error(ctx));
        Assert.False(c.NextCalled());

        // Phiên lỗi (TkSessionStore đọc SQL đồng bộ và ném) cũng đóng cửa.
        var inner = new FakeMcpTokenStore();
        var (_, key) = await inner.CreateAsync("t1", "a", "sid-a", "may", default);
        var c2 = Build(inner, _ => throw new InvalidOperationException("SQL chết"));
        var ctx2 = Req("Bearer " + key);
        await c2.Mw.InvokeAsync(ctx2, c2.Auth);
        Assert.Equal(503, ctx2.Response.StatusCode);
        Assert.False(c2.NextCalled());
    }

    [Fact]
    public async Task Dem_60_giay_roi_tra_lai()
    {
        var inner = new FakeMcpTokenStore();
        var (_, key) = await inner.CreateAsync("t1", "a", "sid-a", "may", default);
        var clock = new ManualClock();
        var c = Build(inner, PhienCon, clock);

        for (var i = 0; i < 5; i++) await c.Mw.InvokeAsync(Req("Bearer " + key), c.Auth);
        Assert.Equal(1, c.Store.FindCalls);

        clock.Advance(TimeSpan.FromSeconds(61));
        await c.Mw.InvokeAsync(Req("Bearer " + key), c.Auth);
        Assert.Equal(2, c.Store.FindCalls);
    }

    [Fact]
    public async Task Thu_hoi_xoa_dem_ngay_khong_cho_60_giay()
    {
        var inner = new FakeMcpTokenStore();
        var (id, key) = await inner.CreateAsync("t1", "a", "sid-a", "may", default);
        var c = Build(inner, PhienCon);

        var ok = Req("Bearer " + key);
        await c.Mw.InvokeAsync(ok, c.Auth);
        Assert.NotNull(ok.Items[McpAuthenticator.ItemKey]);

        Assert.True(await inner.RevokeAsync(id, "t1", "a", default));
        c.Auth.Evict(id);

        var after = Req("Bearer " + key);
        await c.Mw.InvokeAsync(after, c.Auth);
        Assert.Equal(401, after.Response.StatusCode);
    }

    [Fact]
    public void Endpoint_thu_hoi_goi_Evict()
    {
        var del = Src("TourkitAiProxy.Endpoints/McpEndpoints.cs");
        var i = del.IndexOf("g.MapDelete(", StringComparison.Ordinal);
        Assert.True(i >= 0);
        Assert.Contains(".Evict(id)", del.Substring(i, Math.Min(800, del.Length - i)));
    }

    // ── Giới hạn tần suất ─────────────────────────────────────────────────

    [Fact]
    public void Qua_60_lan_mot_phut_bi_chan_va_bao_so_giay()
    {
        var clock = new ManualClock();
        var lim = new McpRateLimiter(clock);
        for (var i = 0; i < 60; i++)
        {
            Assert.True(lim.TryAcquire(7, out _));
            clock.Advance(TimeSpan.FromMilliseconds(500));   // 60 lần trong 30 giây
        }
        Assert.False(lim.TryAcquire(7, out var wait));
        Assert.InRange(wait, 29, 31);
        Assert.True(lim.TryAcquire(8, out _));               // khoá khác không bị vạ lây

        clock.Advance(TimeSpan.FromSeconds(wait));
        Assert.True(lim.TryAcquire(7, out _));               // cửa sổ trượt: lần cũ nhất đã rơi ra
        Assert.Equal("Bạn gọi quá nhanh, thử lại sau 12 giây", McpRateLimiter.Message(12));
    }

    // ── Chỉ dẫn máy chủ ───────────────────────────────────────────────────

    [Fact]
    public void Chi_dan_co_ngay_hom_nay_gio_VN_va_luat_planner()
    {
        // 18:30 UTC ngày 30/11 = 01:30 sáng 01/12 giờ VN — lấy ngày UTC là sai ngay ca này.
        var text = McpServerInstructions.Build(new DateTime(2026, 11, 30, 18, 30, 0, DateTimeKind.Utc));
        Assert.Contains("2026-12-01", text);
        Assert.Contains("2026-12-31", text);                 // "tháng này" = đầu → cuối tháng
        Assert.Contains("yyyy-MM-dd", text);
        Assert.Contains("tour_catalog", text);
        Assert.Contains("tour_detail", text);
        Assert.Contains("employee_performance", text);
        Assert.Contains("top_sellers", text);
        Assert.Contains("marketName", text);
        Assert.Contains("employeeName", text);
    }

    // ── Đồ giả ────────────────────────────────────────────────────────────

    private sealed class CountingStore(IMcpTokenStore inner) : IMcpTokenStore
    {
        public int FindCalls;
        public Task<(long Id, string Token)> CreateAsync(string tenant, string username, string sessionId, string label, CancellationToken ct)
            => inner.CreateAsync(tenant, username, sessionId, label, ct);
        public Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct)
        { Interlocked.Increment(ref FindCalls); return inner.FindActiveAsync(tokenHash, ct); }
        public Task<IReadOnlyList<McpTokenRow>> ListAsync(string tenant, string username, CancellationToken ct) => inner.ListAsync(tenant, username, ct);
        public Task<bool> RevokeAsync(long id, string tenant, string username, CancellationToken ct) => inner.RevokeAsync(id, tenant, username, ct);
        public Task TouchAsync(long id, CancellationToken ct) => inner.TouchAsync(id, ct);
    }

    private sealed class ThrowingStore : IMcpTokenStore
    {
        public Task<(long Id, string Token)> CreateAsync(string t, string u, string s, string l, CancellationToken ct) => throw new InvalidOperationException();
        public Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct) => throw new InvalidOperationException("SQL chết");
        public Task<IReadOnlyList<McpTokenRow>> ListAsync(string t, string u, CancellationToken ct) => throw new InvalidOperationException();
        public Task<bool> RevokeAsync(long id, string t, string u, CancellationToken ct) => throw new InvalidOperationException();
        public Task TouchAsync(long id, CancellationToken ct) => throw new InvalidOperationException();
    }
}

/// <summary>Đồng hồ tay cho test cửa sổ thời gian — không ngủ thật.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
