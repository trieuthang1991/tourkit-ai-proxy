using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Services.Mcp;
using TourkitAiProxy.Services.Mcp.OAuth;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Ba endpoint của trang đồng ý (<c>authorize-info</c>, <c>approve</c>, <c>deny</c>): không phiên → 401;
/// tham số bị sửa giữa lúc hiện trang và lúc bấm nút → 400 và KHÔNG cấp mã / KHÔNG có URL chuyển hướng.
/// </summary>
public class McpOAuthConsentEndpointTests
{
    private const string Base = "https://tourkit-ai.example.vn";
    private const string ClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private const string Cb = "https://claude.ai/api/mcp/auth_callback";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private static readonly SessionAuth.Ctx Session = new("sid-a", "t1", "a");

    private sealed class Fetcher(string? logo = "https://claude.ai/logo.png") : IClientMetadataFetcher
    {
        public bool CanFetch(string? url) => url == ClientId;
        public Task<ClientMetadata?> FetchAsync(string url, CancellationToken ct)
            => Task.FromResult<ClientMetadata?>(new ClientMetadata(ClientId, "Claude", new[] { Cb }, logo));
    }

    private static (McpOAuthService Svc, FakeMcpOAuthStore Store) Make(string? logo = "https://claude.ai/logo.png")
    {
        var store = new FakeMcpOAuthStore();
        var svc = new McpOAuthService(store, new Fetcher(logo), sid => sid == "sid-a" ? new McpSessionOwner("t1", "a") : null,
            new McpOAuthOptions(Base), NullLogger<McpOAuthService>.Instance);
        return (svc, store);
    }

    private static string Query(string redirect = Cb, string clientId = ClientId, string state = "xyz")
        => "?response_type=code&client_id=" + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(redirect)
           + "&code_challenge=" + Challenge + "&code_challenge_method=S256&state=" + state
           + "&resource=" + Uri.EscapeDataString(Base + "/mcp") + "&scope=tourkit.read%20offline_access";

    private static HttpContext PostCtx(string? query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        var body = query == null ? "{}" : new JsonObject { ["query"] = query }.ToJsonString();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    private static HttpContext GetCtx(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        return ctx;
    }

    private static JsonObject Body(IResult r) => JsonNode.Parse(Assert.IsType<ContentHttpResult>(r).ResponseContent!)!.AsObject();
    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode!.Value;

    [Fact]
    public async Task Approve_khong_phien_thi_401_va_khong_cap_ma()
    {
        var (svc, store) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query()), null, svc, deny: false, default);
        Assert.Equal(401, Status(r));
        Assert.Equal(0, store.CodeCount);
    }

    [Fact]
    public async Task Deny_khong_phien_thi_401()
    {
        var (svc, _) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query()), null, svc, deny: true, default);
        Assert.Equal(401, Status(r));
    }

    [Fact]
    public async Task Approve_dung_thi_tra_redirectUrl_co_ma_state_iss_va_luu_dung_mot_ma()
    {
        var (svc, store) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query()), Session, svc, deny: false, default);
        Assert.Equal(200, Status(r));
        var url = Body(r)["redirectUrl"]!.GetValue<string>();
        Assert.StartsWith(Cb + "?code=tkoac_", url);
        Assert.Contains("state=xyz", url);
        Assert.Contains("iss=" + Uri.EscapeDataString(Base), url);
        Assert.Equal(1, store.CodeCount);
    }

    [Fact]
    public async Task Approve_redirect_uri_gia_thi_400_khong_ma_khong_url()
    {
        var (svc, store) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query(redirect: "https://evil.com/cb")), Session, svc, deny: false, default);
        Assert.Equal(400, Status(r));
        var b = Body(r);
        Assert.Null(b["redirectUrl"]);
        Assert.False(string.IsNullOrEmpty(b["error"]?.GetValue<string>()));
        Assert.Equal(0, store.CodeCount);
    }

    [Fact]
    public async Task Deny_redirect_uri_gia_thi_400_khong_url()
    {
        var (svc, _) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query(redirect: "https://evil.com/cb")), Session, svc, deny: true, default);
        Assert.Equal(400, Status(r));
        Assert.Null(Body(r)["redirectUrl"]);
    }

    [Fact]
    public async Task Deny_dung_thi_access_denied_kem_state_va_khong_cap_ma()
    {
        var (svc, store) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(Query()), Session, svc, deny: true, default);
        Assert.Equal(200, Status(r));
        var url = Body(r)["redirectUrl"]!.GetValue<string>();
        Assert.Contains("error=access_denied", url);
        Assert.Contains("state=xyz", url);
        Assert.Equal(0, store.CodeCount);
    }

    [Fact]
    public async Task Approve_tham_so_khac_sai_thi_400_khong_redirect_ve_client()
    {
        var (svc, store) = Make();
        // Thiếu PKCE: ở trang thì báo lỗi qua redirect, nhưng ở lệnh approve KHÔNG redirect — chỉ 400.
        var q = Query().Replace("&code_challenge=" + Challenge, "");
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(q), Session, svc, deny: false, default);
        Assert.Equal(400, Status(r));
        Assert.Null(Body(r)["redirectUrl"]);
        Assert.Equal(0, store.CodeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Approve_than_rong_thi_400(string? query)
    {
        var (svc, store) = Make();
        var r = await McpOAuthEndpoints.DecideAsync(PostCtx(query), Session, svc, deny: false, default);
        Assert.Equal(400, Status(r));
        Assert.Equal(0, store.CodeCount);
    }

    [Fact]
    public async Task Approve_than_khong_phai_JSON_thi_400()
    {
        var (svc, _) = Make();
        var ctx = PostCtx(null);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("khong phai json"));
        Assert.Equal(400, Status(await McpOAuthEndpoints.DecideAsync(ctx, Session, svc, deny: false, default)));
    }

    [Fact]
    public async Task Authorize_info_hop_le_tra_consent_voi_ten_logo_https_va_khong_can_phien()
    {
        var (svc, _) = Make();
        var b = Body(await McpOAuthEndpoints.AuthorizeInfoAsync(GetCtx(Query()), svc, default));
        Assert.Equal("consent", b["kind"]!.GetValue<string>());
        Assert.Equal("Claude", b["clientName"]!.GetValue<string>());
        Assert.Equal("https://claude.ai/logo.png", b["logoUri"]!.GetValue<string>());
        Assert.Equal("claude.ai", b["redirectHost"]!.GetValue<string>());
    }

    [Fact]
    public async Task Authorize_info_redirect_lech_thi_kind_error_khong_co_url()
    {
        var (svc, _) = Make();
        var b = Body(await McpOAuthEndpoints.AuthorizeInfoAsync(GetCtx(Query(redirect: "https://evil.com/cb")), svc, default));
        Assert.Equal("error", b["kind"]!.GetValue<string>());
        Assert.Null(b["url"]);
        Assert.False(string.IsNullOrEmpty(b["message"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Authorize_info_sai_PKCE_thi_kind_redirect_ve_client_dung()
    {
        var (svc, _) = Make();
        var b = Body(await McpOAuthEndpoints.AuthorizeInfoAsync(GetCtx(Query().Replace("S256", "plain")), svc, default));
        Assert.Equal("redirect", b["kind"]!.GetValue<string>());
        Assert.StartsWith(Cb + "?error=invalid_request", b["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Authorize_info_logo_http_thi_khong_hien_logo()
    {
        var (svc, _) = Make("http://claude.ai/logo.png");
        var b = Body(await McpOAuthEndpoints.AuthorizeInfoAsync(GetCtx(Query()), svc, default));
        Assert.Equal("consent", b["kind"]!.GetValue<string>());
        Assert.Null(b["logoUri"]);
    }

    [Fact]
    public async Task Authorize_info_khong_query_thi_kind_error()
    {
        var (svc, _) = Make();
        var b = Body(await McpOAuthEndpoints.AuthorizeInfoAsync(GetCtx(""), svc, default));
        Assert.Equal("error", b["kind"]!.GetValue<string>());
    }
}
