using Microsoft.Extensions.Configuration;

namespace TourkitAiProxy.Services.Mcp.OAuth;

/// <summary>
/// Cấu hình máy chủ OAuth của MCP, đọc MỘT lần lúc khởi động.
///
/// <para><b>Issuer / resource chỉ lấy từ <c>Mcp:PublicBaseUrl</c>, không bao giờ từ header Host:</b>
/// sau Cloudflare/IIS host có thể sai, và tin header là để kẻ gửi Host giả khiến ta quảng cáo
/// endpoint của họ. Thiếu hoặc sai cấu hình thì <see cref="FromConfig"/> trả null và cờ
/// <c>FeatureFlags.McpOAuth</c> coi như tắt.</para>
/// </summary>
public sealed class McpOAuthOptions
{
    /// <summary>Host được phép tải tài liệu client (CIMD). Cấu hình <c>Mcp:OAuth:CimdHosts</c> chỉ THÊM vào, không thay.</summary>
    public static readonly string[] DefaultCimdHosts = { "claude.ai", "chatgpt.com" };

    /// <summary>Địa chỉ gốc đã chuẩn hoá: chữ thường scheme+host, không '/' cuối, không đường dẫn.</summary>
    public string BaseUrl { get; }
    public string Issuer => BaseUrl;
    /// <summary>Audience của mọi token — đúng bằng URL máy chủ MCP.</summary>
    public string Resource => BaseUrl + "/mcp";
    public IReadOnlyCollection<string> ExtraRedirectUris { get; }
    public IReadOnlyCollection<string> CimdHosts { get; }

    public McpOAuthOptions(string baseUrl, IEnumerable<string>? extraRedirectUris = null, IEnumerable<string>? cimdHosts = null)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl) ?? throw new ArgumentException("Mcp:PublicBaseUrl không hợp lệ", nameof(baseUrl));
        ExtraRedirectUris = (extraRedirectUris ?? Array.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        CimdHosts = DefaultCimdHosts.Concat(cimdHosts ?? Array.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Chuẩn hoá địa chỉ gốc. Chỉ nhận http/https tuyệt đối, KHÔNG đường dẫn con, query, fragment hay
    /// userinfo — <c>/mcp</c> và <c>/oauth/*</c> được map ở gốc app, gốc có đường dẫn con là quảng cáo sai.
    /// </summary>
    public static string? NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Contains('?') || s.Contains('#')) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(u.UserInfo)) return null;
        if (u.AbsolutePath != "/") return null;
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        return $"{u.Scheme}://{u.Host.ToLowerInvariant()}{port}";
    }

    /// <summary>Null khi thiếu/sai <c>Mcp:PublicBaseUrl</c>.</summary>
    public static McpOAuthOptions? FromConfig(IConfiguration cfg)
    {
        var baseUrl = NormalizeBaseUrl(cfg["Mcp:PublicBaseUrl"]);
        if (baseUrl == null) return null;
        return new McpOAuthOptions(baseUrl,
            cfg.GetSection("Mcp:OAuth:ExtraRedirectUris").Get<string[]>(),
            cfg.GetSection("Mcp:OAuth:CimdHosts").Get<string[]>());
    }
}
