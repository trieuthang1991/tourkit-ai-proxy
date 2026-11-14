using System.Text.Json;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>Thông tin client OAuth sau khi đã kiểm tra hợp lệ (từ tài liệu CIMD hoặc yêu cầu DCR).</summary>
public sealed record ClientMetadata(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, string? LogoUri);

/// <summary>
/// Đọc + kiểm tra tài liệu client. Chỉ nhận client CÔNG KHAI (không bí mật) và redirect trong danh sách
/// trắng — vì không có người duyệt từng client, danh sách trắng là hàng rào duy nhất.
/// </summary>
public static class ClientMetadataDocument
{
    /// <summary>Đường danh sách trắng bổ sung (từ cấu hình) — mặc định rỗng; tầng gọi truyền vào nếu cần.</summary>
    public static (ClientMetadata? Doc, string? Error) ParseCimd(string fetchedUrl, string json, IReadOnlyCollection<string>? extraRedirects = null)
    {
        var root = ParseObject(json);
        if (root is null) return (null, "invalid_client_metadata");
        var el = root.Value;

        // client_id trong tài liệu phải đúng bằng URL vừa tải — chống một trang tự nhận là client khác.
        if (Str(el, "client_id") != fetchedUrl) return (null, "invalid_client_metadata");
        return Build(el, fetchedUrl, extraRedirects ?? Array.Empty<string>());
    }

    /// <summary>Lỗi trả đúng mã RFC 7591: <c>invalid_redirect_uri</c> / <c>invalid_client_metadata</c>. ClientId để trống — tầng lưu trữ cấp.</summary>
    public static (ClientMetadata? Doc, string? Error) ParseDcrRequest(string json, IReadOnlyCollection<string>? extraRedirects = null)
    {
        var root = ParseObject(json);
        if (root is null) return (null, "invalid_client_metadata");
        return Build(root.Value, "", extraRedirects ?? Array.Empty<string>());
    }

    private static (ClientMetadata? Doc, string? Error) Build(JsonElement el, string clientId, IReadOnlyCollection<string> extra)
    {
        // Thiếu hoặc "none" = client công khai. Mọi kiểu có bí mật đều bị từ chối.
        var method = Str(el, "token_endpoint_auth_method");
        if (method is not null && method != "none") return (null, "invalid_client_metadata");

        if (!el.TryGetProperty("redirect_uris", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return (null, "invalid_redirect_uri");
        var uris = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var u = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!OAuthRules.IsAllowedRedirectUri(u, extra)) return (null, "invalid_redirect_uri");
            uris.Add(u!);
        }
        return (new ClientMetadata(clientId, Str(el, "client_name"), uris, Str(el, "logo_uri")), null);
    }

    private static JsonElement? ParseObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
