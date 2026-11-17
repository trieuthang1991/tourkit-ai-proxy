using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Services.Mcp;
using TourkitAiProxy.Services.Mcp.OAuth;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// <c>/mcp</c> nhận access token OAuth (<c>tkoat_</c>) — Review Focus 4: token sai đối tượng, hết hạn, grant đã
/// thu hồi, phiên TourKit đã hết → 401 kèm <c>WWW-Authenticate: Bearer resource_metadata="…"</c>, không bao giờ 200.
/// Khoá tay <c>tkmcp_</c> giữ nguyên hành vi; cờ tắt thì <c>tkoat_</c> bị từ chối và header như giai đoạn 1.
/// </summary>
public class McpOAuthBearerTests
{
    private const string Base = "https://tourkit-ai.example.vn";
    private const string Resource = Base + "/mcp";
    private const string ExpectedHeader =
        "Bearer resource_metadata=\"https://tourkit-ai.example.vn/.well-known/oauth-protected-resource\", scope=\"tourkit.read\"";

    private static readonly Func<string, McpSessionOwner?> PhienCon = sid => sid switch
    {
        "sid-a" => new McpSessionOwner("t1", "a"),
        "sid-b" => new McpSessionOwner("t2", "b"),
        _ => null,
    };

    private sealed record Ctx(McpAuthMiddleware Mw, McpAuthenticator Auth, FakeMcpTokenStore Keys, FakeMcpOAuthStore OAuth,
        ManualClock Clock, Func<bool> NextCalled);

    private static Ctx Build(bool oauthOn = true, Func<string, McpSessionOwner?>? sessions = null, IMcpOAuthStore? oauthStore = null)
    {
        var keys = new FakeMcpTokenStore();
        var oauth = new FakeMcpOAuthStore();
        var clock = new ManualClock();
        var auth = oauthOn
            ? new McpAuthenticator(keys, sessions ?? PhienCon, NullLogger<McpAuthenticator>.Instance, clock, oauthStore ?? oauth, new McpOAuthOptions(Base))
            : new McpAuthenticator(keys, sessions ?? PhienCon, NullLogger<McpAuthenticator>.Instance, clock);
        var called = false;
        var mw = new McpAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        return new Ctx(mw, auth, keys, oauth, clock, () => called);
    }

    /// Tạo grant trong kho giả, trả (id, access token thô). Hạn tính theo đồng hồ tay của test.
    private static async Task<(long Id, string Access)> Grant(Ctx c, string tenant = "t1", string user = "a", string sid = "sid-a",
        string resource = Resource, TimeSpan? ttl = null)
    {
        var access = OAuthRules.NewAccessToken();
        var now = c.Clock.GetUtcNow().UtcDateTime;
        var id = await c.OAuth.CreateGrantAsync(new McpOAuthNewGrant("https://claude.ai/oauth/mcp-oauth-client-metadata", "Claude",
            tenant, user, sid, resource, "tourkit.read offline_access", OAuthRules.Hash(access), now + (ttl ?? TimeSpan.FromHours(1)),
            OAuthRules.Hash(OAuthRules.NewRefreshToken()), now.AddDays(30)), default);
        return (id, access);
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

    private static async Task<DefaultHttpContext> Call(Ctx c, string token)
    {
        var ctx = Req("Bearer " + token);
        await c.Mw.InvokeAsync(ctx, c.Auth);
        return ctx;
    }

    private static void Assert401(DefaultHttpContext ctx, string header = ExpectedHeader)
    {
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal(header, ctx.Response.Headers.WWWAuthenticate.ToString());
        Assert.Null(ctx.Items[McpAuthenticator.ItemKey]);
        Assert.False(string.IsNullOrWhiteSpace(Error(ctx)));
    }

    // ── Token OAuth hợp lệ ────────────────────────────────────────────────

    [Fact]
    public async Task Tkoat_hop_le_cho_qua_va_caller_lay_tu_grant()
    {
        var c = Build();
        var (id, access) = await Grant(c);
        var ctx = await Call(c, access);
        Assert.True(c.NextCalled());
        var caller = Assert.IsType<McpCaller>(ctx.Items[McpAuthenticator.ItemKey]);
        Assert.Equal(new McpCaller(id, "sid-a", "t1", "a", McpCallerKind.OAuthGrant), caller);
    }

    [Fact]
    public async Task Tkoat_dung_den_resource_co_dau_gach_cuoi_van_khop_sau_chuan_hoa()
    {
        // Resource lưu từ lúc cấp đã được chuẩn hoá; nếu dữ liệu cũ có '/' cuối hoặc chữ hoa host thì vẫn là cùng một đối tượng.
        var c = Build();
        var (_, access) = await Grant(c, resource: "https://TOURKIT-AI.example.vn/mcp/");
        var ctx = await Call(c, access);
        Assert.True(c.NextCalled());
    }

    // ── Review Focus 4: luôn 401 + header resource_metadata ───────────────

    [Fact]
    public async Task Tkoat_het_han_tra_401_kem_header()
    {
        var c = Build();
        var (_, access) = await Grant(c, ttl: TimeSpan.Zero);          // hết hạn đúng lúc này: now ≥ AccessExpiresUtc
        Assert401(await Call(c, access));
        var (_, access2) = await Grant(c, ttl: TimeSpan.FromSeconds(-1));
        Assert401(await Call(c, access2));
        Assert.False(c.NextCalled());
    }

    [Fact]
    public async Task Tkoat_cap_cho_resource_khac_tra_401()
    {
        var c = Build();
        foreach (var r in new[] { "https://other.example.vn/mcp", Base + "/mcp-khac", Base, "khong-phai-url" })
        {
            var (_, access) = await Grant(c, resource: r);
            Assert401(await Call(c, access));
        }
        Assert.False(c.NextCalled());
    }

    [Fact]
    public async Task Tkoat_grant_da_thu_hoi_tra_401()
    {
        var c = Build();
        var (id, access) = await Grant(c);
        Assert.True(await c.OAuth.RevokeGrantAsync(id, "t1", "a", default));
        Assert401(await Call(c, access));
    }

    [Fact]
    public async Task Tkoat_phien_TourKit_het_hoac_lech_chu_tra_401()
    {
        var c = Build();
        var (_, gone) = await Grant(c, sid: "sid-da-het");
        Assert401(await Call(c, gone));
        // Grant ghi (t1, a) nhưng phiên lại của (t2, b): không bao giờ chạy bằng quyền người khác.
        var (_, mismatch) = await Grant(c, sid: "sid-b");
        Assert401(await Call(c, mismatch));
        Assert.False(c.NextCalled());
    }

    [Fact]
    public async Task Tkoat_khong_dung_dinh_dang_tra_401()
    {
        var c = Build();
        foreach (var t in new[] { "tkoat_", "tkoat_" + new string('A', 32), "tkoat_" + new string('a', 31), "tkort_" + new string('a', 32) })
            Assert401(await Call(c, t));
    }

    [Fact]
    public async Task Moi_401_khi_bat_co_deu_kem_resource_metadata()
    {
        var c = Build();
        foreach (var h in new string?[] { null, "", "Basic abc", "Bearer ", "Bearer rac", "Bearer tkmcp_" + new string('a', 32) })
        {
            var ctx = Req(h);
            await c.Mw.InvokeAsync(ctx, c.Auth);
            Assert401(ctx);
        }
    }

    [Fact]
    public async Task Kho_OAuth_loi_thi_dong_cua_503()
    {
        var c = Build(oauthStore: new ThrowingOAuthStore());
        var ctx = await Call(c, OAuthRules.NewAccessToken());
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.False(c.NextCalled());
    }

    // ── Cờ tắt: như giai đoạn 1 ───────────────────────────────────────────

    [Fact]
    public async Task Co_tat_tkoat_bi_tu_choi_va_header_kieu_giai_doan_1()
    {
        var c = Build(oauthOn: false);
        var (_, access) = await Grant(c);                               // grant có thật trong kho, nhưng cờ tắt
        var ctx = await Call(c, access);
        Assert401(ctx, header: "Bearer");
        Assert.Equal(McpAuthenticator.InvalidKey, Error(ctx));
        Assert.Equal("Bearer", c.Auth.WwwAuthenticate);
    }

    // ── Khoá tay tkmcp_ không đổi ─────────────────────────────────────────

    [Fact]
    public async Task Khoa_tkmcp_van_chay_khi_bat_co_va_caller_kieu_khoa_tay()
    {
        var c = Build();
        var (id, key) = await c.Keys.CreateAsync("t2", "b", "sid-b", "may", default);
        var ctx = await Call(c, key);
        Assert.True(c.NextCalled());
        var caller = Assert.IsType<McpCaller>(ctx.Items[McpAuthenticator.ItemKey]);
        Assert.Equal(new McpCaller(id, "sid-b", "t2", "b"), caller);
        Assert.Equal(McpCallerKind.ManualKey, caller.Kind);
    }

    // ── Đệm ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dem_khong_bao_gio_vuot_qua_han_access()
    {
        var c = Build();
        var (_, access) = await Grant(c, ttl: TimeSpan.FromSeconds(30));
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);
        c.Clock.Advance(TimeSpan.FromSeconds(31));                      // đệm 60 s nhưng access chỉ còn 30 s
        Assert401(await Call(c, access));
    }

    [Fact]
    public async Task Thu_hoi_ket_noi_xoa_dem_ngay()
    {
        var c = Build();
        var (id, access) = await Grant(c);
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);  // đã vào đệm
        Assert.True(await c.OAuth.RevokeGrantAsync(id, "t1", "a", default));
        c.Auth.EvictGrant(id);
        Assert401(await Call(c, access));
    }

    [Fact]
    public async Task Khoa_tay_va_grant_cung_so_id_khong_xoa_dem_lan_nhau()
    {
        var c = Build();
        var (keyId, key) = await c.Keys.CreateAsync("t1", "a", "sid-a", "may", default);
        var (grantId, access) = await Grant(c);
        Assert.Equal(keyId, grantId);                                   // cả hai kho đánh số từ 1
        await Call(c, key);
        await Call(c, access);

        // Thu hồi thẳng trong kho (không Evict) — còn chạy được nghĩa là còn trong đệm.
        await c.Keys.RevokeAsync(keyId, "t1", "a", default);
        await c.OAuth.RevokeGrantAsync(grantId, "t1", "a", default);

        c.Auth.EvictGrant(grantId);
        Assert.Equal(200, (await Call(c, key)).Response.StatusCode);    // khoá tay KHÔNG bị xoá đệm oan
        Assert401(await Call(c, access));

        var (k2, key2) = await c.Keys.CreateAsync("t1", "a", "sid-a", "may2", default);
        var (g2, access2) = await Grant(c);
        Assert.Equal(k2, g2);
        await Call(c, key2);
        await Call(c, access2);
        await c.Keys.RevokeAsync(k2, "t1", "a", default);
        await c.OAuth.RevokeGrantAsync(g2, "t1", "a", default);
        c.Auth.Evict(k2);
        Assert.Equal(200, (await Call(c, access2)).Response.StatusCode); // grant KHÔNG bị xoá đệm oan
        Assert401(await Call(c, key2));
    }

    [Fact]
    public void Gioi_han_tan_suat_tach_khoa_tay_va_grant()
    {
        var lim = new McpRateLimiter(new ManualClock());
        var key = new McpCaller(7, "s", "t", "u");
        var grant = new McpCaller(7, "s", "t", "u", McpCallerKind.OAuthGrant);
        for (var i = 0; i < McpRateLimiter.PerMinute; i++) Assert.True(lim.TryAcquire(key, out _));
        Assert.False(lim.TryAcquire(key, out _));
        Assert.True(lim.TryAcquire(grant, out _));                      // cùng số 7 nhưng khác loại → khác ngăn
    }

    // ── Endpoint "Ứng dụng đã kết nối" ────────────────────────────────────

    private static JsonObject Body(IResult r) => JsonNode.Parse(Assert.IsType<ContentHttpResult>(r).ResponseContent!)!.AsObject();
    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode!.Value;

    [Fact]
    public async Task Ket_noi_chi_liet_ke_cua_chinh_minh_khong_lo_bam_hay_phien()
    {
        var c = Build();
        var (mine, _) = await Grant(c);
        await Grant(c, tenant: "t2", user: "b", sid: "sid-b");
        await Grant(c, tenant: "t1", user: "khac", sid: "sid-a");      // cùng công ty, người khác

        var r = await McpOAuthEndpoints.ListConnectionsAsync(new SessionAuth.Ctx("sid-a", "t1", "a"), c.OAuth, default);
        Assert.Equal(200, Status(r));
        var items = Body(r)["items"]!.AsArray();
        var one = Assert.Single(items)!.AsObject();
        Assert.Equal(mine, one["id"]!.GetValue<long>());
        Assert.Equal("Claude", one["clientName"]!.GetValue<string>());
        Assert.EndsWith("Z", one["createdUtc"]!.GetValue<string>());
        var json = one.ToJsonString();
        Assert.DoesNotContain("sid-a", json);
        Assert.DoesNotContain("ession", json);
        Assert.DoesNotContain("ash", json);
    }

    [Fact]
    public async Task Ket_noi_khong_phien_tra_401()
    {
        var c = Build();
        Assert.Equal(401, Status(await McpOAuthEndpoints.ListConnectionsAsync(null, c.OAuth, default)));
        Assert.Equal(401, Status(await McpOAuthEndpoints.RevokeConnectionAsync(1, null, c.OAuth, c.Auth, default)));
    }

    [Fact]
    public async Task Thu_hoi_ket_noi_cua_nguoi_khac_tra_404_va_van_chay()
    {
        var c = Build();
        var (id, access) = await Grant(c);
        var r = await McpOAuthEndpoints.RevokeConnectionAsync(id, new SessionAuth.Ctx("sid-b", "t2", "b"), c.OAuth, c.Auth, default);
        Assert.Equal(404, Status(r));
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);
    }

    [Fact]
    public async Task Thu_hoi_ket_noi_cua_minh_tra_204_va_mcp_401_ngay()
    {
        var c = Build();
        var (id, access) = await Grant(c);
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);  // đã vào đệm
        var r = await McpOAuthEndpoints.RevokeConnectionAsync(id, new SessionAuth.Ctx("sid-a", "t1", "a"), c.OAuth, c.Auth, default);
        Assert.Equal(204, Status(r));
        Assert401(await Call(c, access));
        // Thu hồi lần hai: không còn gì để thu hồi.
        Assert.Equal(404, Status(await McpOAuthEndpoints.RevokeConnectionAsync(id, new SessionAuth.Ctx("sid-a", "t1", "a"), c.OAuth, c.Auth, default)));
    }

    // ── Tombstone: grant vừa thu hồi không được quay lại đệm (cuộc đua đọc–thu hồi–ghi đệm) ──

    /// Dựng cuộc đua: lời gọi /mcp đã đọc grant CÒN SỐNG từ CSDL, rồi — trước khi kịp ghi đệm — nơi khác
    /// thu hồi + xoá đệm. Không có tombstone thì token đã thu hồi được đệm thêm 60 giây.
    private static async Task RaceRevokeBetweenReadAndRemember(Ctx c, RacyOAuthStore racy, long id, string access, Func<Task> revoke)
    {
        racy.AfterRead = revoke;
        Assert401(await Call(c, access));                                // chính lời gọi đang đua cũng bị chặn
        Assert401(await Call(c, access));                                // và KHÔNG có gì nằm lại trong đệm
    }

    [Fact]
    public async Task Tombstone_chan_ghi_dem_sau_khi_da_xoa_dem()
    {
        var racy = new RacyOAuthStore();
        var c = Build(oauthStore: racy);
        var (id, access) = await GrantIn(racy.Inner, c);
        await RaceRevokeBetweenReadAndRemember(c, racy, id, access, async () =>
        {
            await racy.Inner.RevokeGrantAsync(id, "t1", "a", default);
            c.Auth.EvictGrant(id);
        });
    }

    [Fact]
    public async Task Tombstone_ap_ca_cho_DELETE_ket_noi()
    {
        var racy = new RacyOAuthStore();
        var c = Build(oauthStore: racy);
        var (id, access) = await GrantIn(racy.Inner, c);
        await RaceRevokeBetweenReadAndRemember(c, racy, id, access, async () =>
            Assert.Equal(204, Status(await McpOAuthEndpoints.RevokeConnectionAsync(id, new SessionAuth.Ctx("sid-a", "t1", "a"), racy.Inner, c.Auth, default))));
    }

    [Fact]
    public async Task Trung_dem_cua_grant_da_tombstone_thi_401()
    {
        // Mục đệm đã nằm sẵn (lọt khỏi lượt quét của EvictGrant vì ghi đồng thời) → lần trúng đệm phải tự kiểm tombstone.
        var c = Build();
        var (id, access) = await Grant(c);
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);  // đã vào đệm
        c.Auth.MarkGrantRevoked(id);                                     // chỉ đánh dấu, KHÔNG quét đệm
        Assert401(await Call(c, access));
    }

    [Fact]
    public async Task Tombstone_het_han_sau_hai_lan_TTL_dem()
    {
        var c = Build();
        var (id, access) = await Grant(c, ttl: TimeSpan.FromHours(2));
        c.Auth.MarkGrantRevoked(id);
        c.Clock.Advance(McpAuthenticator.CacheTtl * 2 - TimeSpan.FromSeconds(1));
        Assert401(await Call(c, access));                                // còn trong hạn tombstone
        c.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(200, (await Call(c, access)).Response.StatusCode);  // CSDL vẫn nói grant sống → tombstone đã hết, cho qua
    }

    [Fact]
    public async Task Tombstone_khong_anh_huong_khoa_tay_cung_so_id()
    {
        var c = Build();
        var (keyId, key) = await c.Keys.CreateAsync("t1", "a", "sid-a", "may", default);
        Assert.Equal(200, (await Call(c, key)).Response.StatusCode);    // khoá tay vào đệm
        c.Auth.EvictGrant(keyId);                                        // grant cùng số id bị thu hồi
        Assert.Equal(200, (await Call(c, key)).Response.StatusCode);    // trúng đệm
        c.Clock.Advance(McpAuthenticator.CacheTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(200, (await Call(c, key)).Response.StatusCode);    // tra lại CSDL, ghi đệm lại
    }

    [Fact]
    public void Tombstone_co_tran_va_khong_bo_muc_moi_nhat()
    {
        var c = Build();
        for (long i = 1; i <= McpAuthenticator.MaxTombstones + 500; i++)
        {
            c.Auth.MarkGrantRevoked(i);
            if (i % 1000 == 0) c.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.InRange(c.Auth.TombstoneCount, 1, McpAuthenticator.MaxTombstones + 1);
        Assert.True(c.Auth.IsGrantTombstoned(McpAuthenticator.MaxTombstones + 500));
    }

    // ── Đồ giả ────────────────────────────────────────────────────────────

    private static async Task<(long Id, string Access)> GrantIn(FakeMcpOAuthStore store, Ctx c)
    {
        var access = OAuthRules.NewAccessToken();
        var now = c.Clock.GetUtcNow().UtcDateTime;
        var id = await store.CreateGrantAsync(new McpOAuthNewGrant("https://chatgpt.com/oauth/client.json", "ChatGPT",
            "t1", "a", "sid-a", Resource, "tourkit.read offline_access", OAuthRules.Hash(access), now.AddHours(1),
            OAuthRules.Hash(OAuthRules.NewRefreshToken()), now.AddDays(30)), default);
        return (id, access);
    }

    /// Kho bọc kho giả: đọc grant xong thì chạy <see cref="AfterRead"/> (một lần) rồi mới trả bản đã đọc — tức bản CŨ.
    private sealed class RacyOAuthStore : IMcpOAuthStore
    {
        public readonly FakeMcpOAuthStore Inner = new();
        public Func<Task>? AfterRead;
        public async Task<McpOAuthGrant?> FindGrantByAccessAsync(string accessHash, CancellationToken ct)
        {
            var g = await Inner.FindGrantByAccessAsync(accessHash, ct);
            var hook = AfterRead; AfterRead = null;
            if (hook != null) await hook();
            return g;
        }
        public Task CreateClientAsync(McpOAuthClient client, CancellationToken ct) => Inner.CreateClientAsync(client, ct);
        public Task<McpOAuthClient?> GetClientAsync(string clientId, CancellationToken ct) => Inner.GetClientAsync(clientId, ct);
        public Task<int> CountClientsByIpSinceAsync(string ip, DateTime sinceUtc, CancellationToken ct) => Inner.CountClientsByIpSinceAsync(ip, sinceUtc, ct);
        public Task SaveCodeAsync(McpOAuthCode code, CancellationToken ct) => Inner.SaveCodeAsync(code, ct);
        public Task<McpOAuthCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct) => Inner.ConsumeCodeAsync(codeHash, ct);
        public Task<long> CreateGrantAsync(McpOAuthNewGrant grant, CancellationToken ct) => Inner.CreateGrantAsync(grant, ct);
        public Task<McpRefreshResult> RotateRefreshAsync(string o, string a, DateTime ae, string r, DateTime re, CancellationToken ct) => Inner.RotateRefreshAsync(o, a, ae, r, re, ct);
        public Task<bool> RevokeGrantAsync(long id, string tenant, string username, CancellationToken ct) => Inner.RevokeGrantAsync(id, tenant, username, ct);
        public Task<IReadOnlyList<McpOAuthGrant>> ListGrantsAsync(string tenant, string username, CancellationToken ct) => Inner.ListGrantsAsync(tenant, username, ct);
        public Task TouchGrantAsync(long id, CancellationToken ct) => Inner.TouchGrantAsync(id, ct);
    }

    private sealed class ThrowingOAuthStore : IMcpOAuthStore
    {
        private static Exception Dead() => new InvalidOperationException("SQL chết");
        public Task CreateClientAsync(McpOAuthClient client, CancellationToken ct) => throw Dead();
        public Task<McpOAuthClient?> GetClientAsync(string clientId, CancellationToken ct) => throw Dead();
        public Task<int> CountClientsByIpSinceAsync(string ip, DateTime sinceUtc, CancellationToken ct) => throw Dead();
        public Task SaveCodeAsync(McpOAuthCode code, CancellationToken ct) => throw Dead();
        public Task<McpOAuthCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct) => throw Dead();
        public Task<long> CreateGrantAsync(McpOAuthNewGrant grant, CancellationToken ct) => throw Dead();
        public Task<McpOAuthGrant?> FindGrantByAccessAsync(string accessHash, CancellationToken ct) => throw Dead();
        public Task<McpRefreshResult> RotateRefreshAsync(string o, string a, DateTime ae, string r, DateTime re, CancellationToken ct) => throw Dead();
        public Task<bool> RevokeGrantAsync(long id, string tenant, string username, CancellationToken ct) => throw Dead();
        public Task<IReadOnlyList<McpOAuthGrant>> ListGrantsAsync(string tenant, string username, CancellationToken ct) => throw Dead();
        public Task TouchGrantAsync(long id, CancellationToken ct) => throw Dead();
    }
}
