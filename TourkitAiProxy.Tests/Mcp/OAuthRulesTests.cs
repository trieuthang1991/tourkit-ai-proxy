using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Luật OAuth thuần: PKCE, danh sách trắng redirect, chuẩn hoá resource, CIMD/DCR.</summary>
public class OAuthRulesTests
{
    private static readonly string[] None = Array.Empty<string>();
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string ClaudeCb = "https://claude.ai/api/mcp/auth_callback";

    [Fact]
    public void Pkce_S256_dung_vector_RFC7636()
        => Assert.True(OAuthRules.VerifyPkceS256(Verifier, Challenge));

    [Theory]
    [InlineData("")]
    [InlineData("ngan")]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEj!k")]   // ký tự ngoài bảng cho phép
    public void Pkce_sai_verifier_bi_tu_choi(string verifier)
    {
        Assert.False(OAuthRules.VerifyPkceS256(verifier, Challenge));
        Assert.False(OAuthRules.VerifyPkceS256(new string('a', 129), "x"));
        Assert.False(OAuthRules.VerifyPkceS256(Verifier, "challenge-sai"));
    }

    [Theory]
    [InlineData(ClaudeCb)]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    [InlineData("https://chatgpt.com/connector/oauth/abc123_-X")]
    public void Redirect_Claude_va_ChatGPT_duoc_nhan(string uri)
        => Assert.True(OAuthRules.IsAllowedRedirectUri(uri, None));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/other")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?x=1")]
    [InlineData("http://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://Claude.ai/api/mcp/auth_callback")]
    [InlineData("https://chatgpt.com/connector/oauth/a/b")]
    [InlineData("https://chatgpt.com/connector/oauth/")]
    [InlineData("http://evil.com/cb")]
    [InlineData("https://evil.com/cb")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Redirect_la_bi_tu_choi(string? uri)
        => Assert.False(OAuthRules.IsAllowedRedirectUri(uri, None));

    [Fact]
    public void Redirect_extra_khop_chinh_xac_moi_duoc_nhan()
    {
        var extra = new[] { "https://app.example.com/cb" };
        Assert.True(OAuthRules.IsAllowedRedirectUri("https://app.example.com/cb", extra));
        Assert.False(OAuthRules.IsAllowedRedirectUri("https://app.example.com/cb2", extra));
    }

    [Fact]
    public void Loopback_bo_qua_cong()
    {
        Assert.True(OAuthRules.IsAllowedRedirectUri("http://localhost:54321/callback", None));
        Assert.True(OAuthRules.IsAllowedRedirectUri("http://127.0.0.1:8080/cb", None));
        Assert.False(OAuthRules.IsAllowedRedirectUri("http://localhost:80/cb?x=1", None));
        Assert.True(OAuthRules.RedirectMatches("http://localhost:1111/cb", "http://localhost:2222/cb"));
        Assert.False(OAuthRules.RedirectMatches("http://localhost:1111/cb", "http://localhost:2222/khac"));
        Assert.False(OAuthRules.RedirectMatches("http://localhost:1111/cb", "http://127.0.0.1:1111/cb"));
        Assert.False(OAuthRules.RedirectMatches(ClaudeCb, ClaudeCb + "2"));
        Assert.True(OAuthRules.RedirectMatches(ClaudeCb, ClaudeCb));
    }

    [Fact]
    public void Resource_chuan_hoa()
    {
        Assert.Equal("https://app.tourkit.vn/mcp", OAuthRules.NormalizeResource("HTTPS://App.TourKit.vn/mcp/"));
        Assert.Equal("https://app.tourkit.vn", OAuthRules.NormalizeResource("https://app.tourkit.vn/"));
        Assert.Null(OAuthRules.NormalizeResource("ftp://x/y"));
        Assert.Null(OAuthRules.NormalizeResource("khong-phai-url"));
        Assert.Null(OAuthRules.NormalizeResource(null));
    }

    [Fact]
    public void Token_dung_tien_to_va_hash_trung_McpTokens()
    {
        Assert.Matches("^tkoat_[0-9a-f]{32}$", OAuthRules.NewAccessToken());
        Assert.Matches("^tkort_[0-9a-f]{32}$", OAuthRules.NewRefreshToken());
        Assert.Matches("^tkoac_[0-9a-f]{32}$", OAuthRules.NewAuthCode());
        Assert.Equal(McpTokens.Hash("abc"), OAuthRules.Hash("abc"));
    }

    private const string Url = "https://client.example/meta.json";

    [Fact]
    public void Cimd_client_id_phai_bang_URL()
    {
        var ok = ClientMetadataDocument.ParseCimd(Url,
            "{\"client_id\":\"" + Url + "\",\"client_name\":\"X\",\"redirect_uris\":[\"" + ClaudeCb + "\"]}");
        Assert.Null(ok.Error);
        Assert.Equal("X", ok.Doc!.ClientName);
        var bad = ClientMetadataDocument.ParseCimd(Url,
            "{\"client_id\":\"https://khac/meta.json\",\"redirect_uris\":[\"" + ClaudeCb + "\"]}");
        Assert.Null(bad.Doc);
        Assert.NotNull(bad.Error);
        Assert.Null(ClientMetadataDocument.ParseCimd(Url, "{rac").Doc);
    }

    [Fact]
    public void Cimd_redirect_ngoai_danh_sach_bi_tu_choi()
    {
        var r = ClientMetadataDocument.ParseCimd(Url,
            "{\"client_id\":\"" + Url + "\",\"redirect_uris\":[\"" + ClaudeCb + "\",\"https://evil.com/cb\"]}");
        Assert.Null(r.Doc);
        Assert.NotNull(r.Error);
        Assert.Null(ClientMetadataDocument.ParseCimd(Url, "{\"client_id\":\"" + Url + "\",\"redirect_uris\":[]}").Doc);
        var secret = ClientMetadataDocument.ParseCimd(Url,
            "{\"client_id\":\"" + Url + "\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"redirect_uris\":[\"" + ClaudeCb + "\"]}");
        Assert.Null(secret.Doc);
    }

    [Fact]
    public void Dcr_client_bi_mat_bi_tu_choi()
    {
        var secret = ClientMetadataDocument.ParseDcrRequest(
            "{\"token_endpoint_auth_method\":\"client_secret_post\",\"redirect_uris\":[\"" + ClaudeCb + "\"]}");
        Assert.Null(secret.Doc);
        Assert.Equal("invalid_client_metadata", secret.Error);
        var badUri = ClientMetadataDocument.ParseDcrRequest("{\"redirect_uris\":[\"https://evil.com/cb\"]}");
        Assert.Equal("invalid_redirect_uri", badUri.Error);
        var ok = ClientMetadataDocument.ParseDcrRequest(
            "{\"client_name\":\"Claude\",\"token_endpoint_auth_method\":\"none\",\"redirect_uris\":[\"" + ClaudeCb + "\"]}");
        Assert.Null(ok.Error);
        Assert.Equal("Claude", ok.Doc!.ClientName);
    }
}
