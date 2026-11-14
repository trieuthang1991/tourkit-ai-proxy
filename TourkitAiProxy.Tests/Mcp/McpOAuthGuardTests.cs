using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Mcp.OAuth;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Cờ <c>Features:McpOAuth</c> + địa chỉ gốc: tắt/thiếu cấu hình thì không một đường OAuth nào được map.</summary>
public class McpOAuthGuardTests
{
    private static IConfiguration Cfg(params (string K, string V)[] kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.K, x => (string?)x.V)).Build();

    private static string Src(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    [Fact]
    public void Co_McpOAuth_chi_bat_khi_du_Mcp_va_PublicBaseUrl()
    {
        Assert.False(FeatureFlags.McpOAuth(Cfg()));
        Assert.False(FeatureFlags.McpOAuth(Cfg(("Features:McpOAuth", "true"), ("Mcp:PublicBaseUrl", "https://a.vn"))));
        Assert.False(FeatureFlags.McpOAuth(Cfg(("Features:Mcp", "true"), ("Mcp:PublicBaseUrl", "https://a.vn"))));
        Assert.False(FeatureFlags.McpOAuth(Cfg(("Features:Mcp", "true"), ("Features:McpOAuth", "true"))));
        Assert.False(FeatureFlags.McpOAuth(Cfg(("Features:Mcp", "true"), ("Features:McpOAuth", "true"), ("Mcp:PublicBaseUrl", "khong-phai-url"))));
        Assert.True(FeatureFlags.McpOAuth(Cfg(("Features:Mcp", "true"), ("Features:McpOAuth", "true"), ("Mcp:PublicBaseUrl", "https://a.vn"))));
    }

    [Theory]
    [InlineData("https://Tourkit-AI.TourKit.vn/", "https://tourkit-ai.tourkit.vn")]
    [InlineData("HTTPS://a.vn", "https://a.vn")]
    [InlineData("http://localhost:5092", "http://localhost:5092")]
    [InlineData("https://a.vn:443", "https://a.vn")]
    [InlineData("https://a.vn/sub", null)]
    [InlineData("https://a.vn/?x=1", null)]
    [InlineData("https://u:p@a.vn", null)]
    [InlineData("ftp://a.vn", null)]
    [InlineData("", null)]
    public void Chuan_hoa_PublicBaseUrl(string raw, string? expected)
        => Assert.Equal(expected, McpOAuthOptions.NormalizeBaseUrl(raw));

    [Fact]
    public void Doc_cau_hinh_redirect_them_va_host_CIMD_them()
    {
        var o = McpOAuthOptions.FromConfig(Cfg(("Mcp:PublicBaseUrl", "https://A.vn/"),
            ("Mcp:OAuth:ExtraRedirectUris:0", "https://app.example.org/cb"),
            ("Mcp:OAuth:CimdHosts:0", "MCP.Example.org")))!;
        Assert.Equal("https://a.vn", o.Issuer);
        Assert.Equal("https://a.vn/mcp", o.Resource);
        Assert.Equal(new[] { "https://app.example.org/cb" }, o.ExtraRedirectUris);
        Assert.Equal(new[] { "claude.ai", "chatgpt.com", "mcp.example.org" }, o.CimdHosts);
        Assert.Null(McpOAuthOptions.FromConfig(Cfg()));
    }

    [Fact]
    public void Duong_OAuth_chi_map_khi_co_hieu_luc_tat_thi_404_JSON()
    {
        var src = Src("TourkitAiProxy.Endpoints/EndpointRegistration.cs");
        var i = src.IndexOf("private static void MapMcp(WebApplication app, IConfiguration cfg)", StringComparison.Ordinal);
        Assert.True(i >= 0);
        var body = src.Substring(i, src.IndexOf("private static void ChanTuongMinh", i, StringComparison.Ordinal) - i);
        var co = body.IndexOf("if (FeatureFlags.McpOAuth(cfg))", StringComparison.Ordinal);
        var map = body.IndexOf("MapMcpOAuthEndpoints()", StringComparison.Ordinal);
        Assert.True(co >= 0 && map > co, "MapMcpOAuthEndpoints() phải nằm sau if (FeatureFlags.McpOAuth(cfg)).");
        // Hai nhánh tắt (McpOAuth tắt; cả Mcp tắt) đều chặn tường minh các tiền tố OAuth.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, @"McpOAuthEndpoints\.Prefixes").Count);
        Assert.Contains("/.well-known/oauth-protected-resource", McpOAuthEndpoints.Prefixes);
        Assert.Contains("/.well-known/oauth-authorization-server", McpOAuthEndpoints.Prefixes);
        Assert.Contains("/.well-known/openid-configuration", McpOAuthEndpoints.Prefixes);
        Assert.Contains("/oauth", McpOAuthEndpoints.Prefixes);
    }

    [Fact]
    public void Token_endpoint_khong_cho_dem_va_chi_nhan_form()
    {
        var src = Src("TourkitAiProxy.Endpoints/McpOAuthEndpoints.cs");
        Assert.Contains("no-store", src);
        Assert.Contains("Pragma", src);
        Assert.Contains("application/x-www-form-urlencoded", src);
        // IP lấy từ kết nối (sau UseForwardedHeaders), không tự đọc header X-Forwarded-For.
        Assert.Contains("Connection.RemoteIpAddress", src);
        Assert.DoesNotContain("X-Forwarded-For", src);
    }

    [Fact]
    public void Client_tai_CIMD_khong_theo_chuyen_huong()
    {
        var src = Src("TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("ClientMetadataFetcher.HttpClientName", src);
        Assert.Contains("AllowAutoRedirect = false", src);
    }
}
