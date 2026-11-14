using System.Security.Cryptography;
using System.Text;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>
/// Luật thuần của máy chủ OAuth 2.1 (để Claude.ai / ChatGPT kết nối vào <c>/mcp</c>): PKCE, danh sách
/// trắng redirect, chuẩn hoá resource, sinh mã/token. Không chạm mạng, không chạm CSDL.
///
/// <para><b>Vì sao danh sách trắng redirect cứng:</b> redirect_uri là chỗ mã cấp quyền bị gửi tới. Nếu
/// nhận bừa, kẻ xấu đăng ký client với redirect của họ rồi lừa người dùng bấm "Cho phép" để lấy mã.
/// Nên chỉ nhận đúng các địa chỉ của Claude, ChatGPT, loopback (app máy tính) và mục cấu hình thêm.</para>
/// </summary>
public static class OAuthRules
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";
    private const string ChatGptCallback = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string ChatGptConnectorPrefix = "https://chatgpt.com/connector/oauth/";

    /// <summary>PKCE S256: base64url(SHA256(verifier)) phải đúng bằng challenge đã gửi lúc xin mã.</summary>
    public static bool VerifyPkceS256(string? codeVerifier, string? codeChallenge)
    {
        // RFC 7636 §4.1: verifier dài 43–128, chỉ gồm ký tự "unreserved". Loại sớm để khỏi băm rác.
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge)) return false;
        if (codeVerifier.Length is < 43 or > 128) return false;
        foreach (var c in codeVerifier)
        {
            var ok = c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '.' or '_' or '~';
            if (!ok) return false;
        }
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var expected = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // So sánh thời gian cố định — challenge do bên ngoài gửi tới.
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(codeChallenge));
    }

    /// <summary>Redirect có nằm trong danh sách trắng không. So khớp chính xác từng ký tự (kể cả hoa/thường).</summary>
    public static bool IsAllowedRedirectUri(string? uri, IReadOnlyCollection<string> extra)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (uri == ClaudeCallback || uri == ChatGptCallback) return true;
        if (uri.StartsWith(ChatGptConnectorPrefix, StringComparison.Ordinal) && IsSafeSegment(uri[ChatGptConnectorPrefix.Length..]))
            return true;
        if (IsLoopback(uri, out _, out _, out _)) return true;
        foreach (var e in extra)
            if (string.Equals(e, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Redirect lúc xin mã phải khớp redirect đã đăng ký. Loopback được đổi cổng (app máy tính chọn cổng ngẫu nhiên, RFC 8252 §7.3).</summary>
    public static bool RedirectMatches(string? registered, string? requested)
    {
        if (registered is null || requested is null) return false;
        if (string.Equals(registered, requested, StringComparison.Ordinal)) return true;
        if (!IsLoopback(registered, out var h1, out var p1, out _) || !IsLoopback(requested, out var h2, out var p2, out _)) return false;
        return h1 == h2 && p1 == p2;
    }

    /// <summary>Chuẩn hoá resource để so khớp: chữ thường scheme+host, bỏ '/' cuối. Không phải http(s) thì null.</summary>
    public static string? NormalizeResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;
        if (!Uri.TryCreate(resource.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        // Uri.Scheme/Host đã là chữ thường; đường dẫn giữ nguyên hoa/thường vì có phân biệt.
        return $"{u.Scheme}://{u.Host}{port}{path}";
    }

    public static string NewAccessToken() => "tkoat_" + Random32Hex();
    public static string NewRefreshToken() => "tkort_" + Random32Hex();
    public static string NewAuthCode() => "tkoac_" + Random32Hex();

    /// <summary>Đúng dạng access token (<c>tkoat_</c> + 32 hex thường)? Chỉ để loại sớm chuỗi rác trước khi chạm CSDL.</summary>
    public static bool LooksLikeAccessToken(string? t)
    {
        const string prefix = "tkoat_";
        if (t is null || t.Length != prefix.Length + 32 || !t.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (var i = prefix.Length; i < t.Length; i++)
            if (!(t[i] is >= '0' and <= '9' || t[i] is >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>Chỉ lưu bản băm. Cùng thuật toán với khoá MCP nên dùng lại, khỏi có hai bản băm.</summary>
    public static string Hash(string token) => McpTokens.Hash(token);

    private static string Random32Hex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsSafeSegment(string s)
    {
        if (s.Length == 0 || s.Length > 128) return false;
        foreach (var c in s)
            if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '_')) return false;
        return true;
    }

    /// <summary>http://localhost:{cổng}/… hoặc http://127.0.0.1:{cổng}/… — không query, không fragment, không userinfo.</summary>
    private static bool IsLoopback(string uri, out string host, out string path, out int port)
    {
        host = path = ""; port = 0;
        string rest;
        if (uri.StartsWith("http://localhost:", StringComparison.Ordinal)) { host = "localhost"; rest = uri["http://localhost:".Length..]; }
        else if (uri.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) { host = "127.0.0.1"; rest = uri["http://127.0.0.1:".Length..]; }
        else return false;
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        if (!int.TryParse(rest[..slash], out port) || port is < 1 or > 65535) return false;
        path = rest[slash..];
        return path.IndexOfAny(new[] { '?', '#', '@', '\\' }) < 0;
    }
}
