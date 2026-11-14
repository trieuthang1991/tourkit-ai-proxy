using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Hai tài liệu metadata (RFC 9728 / RFC 8414) mà Claude.ai và ChatGPT đọc để tự cấu hình.</summary>
public class OAuthMetadataTests
{
    private static string[] Arr(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Fact]
    public void Metadata_ProtectedResource_dung_truong()
    {
        var m = OAuthMetadata.ProtectedResource("https://app.tourkit.vn/");
        Assert.Equal("https://app.tourkit.vn/mcp", (string?)m["resource"]);
        Assert.Equal(new[] { "https://app.tourkit.vn" }, Arr(m["authorization_servers"]));
        Assert.Equal(new[] { "tourkit.read", "offline_access" }, Arr(m["scopes_supported"]));
        Assert.Equal(new[] { "header" }, Arr(m["bearer_methods_supported"]));
        Assert.Equal("TourKit", (string?)m["resource_name"]);
    }

    [Fact]
    public void Metadata_AuthServer_co_CIMD_va_iss()
    {
        var m = OAuthMetadata.AuthorizationServer("https://app.tourkit.vn");
        Assert.Equal("https://app.tourkit.vn", (string?)m["issuer"]);
        Assert.Equal("https://app.tourkit.vn/oauth/authorize", (string?)m["authorization_endpoint"]);
        Assert.Equal("https://app.tourkit.vn/oauth/token", (string?)m["token_endpoint"]);
        Assert.Equal("https://app.tourkit.vn/oauth/register", (string?)m["registration_endpoint"]);
        Assert.Equal(new[] { "code" }, Arr(m["response_types_supported"]));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, Arr(m["grant_types_supported"]));
        Assert.Equal(new[] { "S256" }, Arr(m["code_challenge_methods_supported"]));
        Assert.Equal(new[] { "none" }, Arr(m["token_endpoint_auth_methods_supported"]));
        Assert.True((bool)m["client_id_metadata_document_supported"]!);
        Assert.True((bool)m["authorization_response_iss_parameter_supported"]!);
        Assert.Equal(new[] { "tourkit.read", "offline_access" }, Arr(m["scopes_supported"]));
    }
}
