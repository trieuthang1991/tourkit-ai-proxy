using System.Text.Json.Nodes;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>
/// Hai tài liệu khám phá mà Claude.ai / ChatGPT đọc để tự cấu hình: tài nguyên được bảo vệ (RFC 9728)
/// và máy chủ cấp quyền (RFC 8414). Địa chỉ gốc do cấu hình truyền vào, KHÔNG lấy từ header Host —
/// nếu không, ai gửi Host giả sẽ khiến ta quảng cáo endpoint của họ.
/// </summary>
public static class OAuthMetadata
{
    /// <summary>Chỉ quảng cáo hai scope ta thật sự hiểu; không có openid/email/profile.</summary>
    private static JsonArray Scopes() => new("tourkit.read", "offline_access");

    private static string Base(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    public static JsonObject ProtectedResource(string baseUrl)
    {
        var b = Base(baseUrl);
        return new JsonObject
        {
            ["resource"] = b + "/mcp",
            ["authorization_servers"] = new JsonArray(b),
            ["scopes_supported"] = Scopes(),
            ["bearer_methods_supported"] = new JsonArray("header"),
            ["resource_name"] = "TourKit",
        };
    }

    public static JsonObject AuthorizationServer(string baseUrl)
    {
        var b = Base(baseUrl);
        return new JsonObject
        {
            ["issuer"] = b,
            ["authorization_endpoint"] = b + "/oauth/authorize",
            ["token_endpoint"] = b + "/oauth/token",
            ["registration_endpoint"] = b + "/oauth/register",
            ["response_types_supported"] = new JsonArray("code"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
            ["client_id_metadata_document_supported"] = true,
            ["authorization_response_iss_parameter_supported"] = true,
            ["scopes_supported"] = Scopes(),
        };
    }
}
