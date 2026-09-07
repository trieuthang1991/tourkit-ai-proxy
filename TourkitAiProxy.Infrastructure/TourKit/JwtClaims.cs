using System.Text.Json;

namespace TourkitAiProxy.Infrastructure.TourKit;

/// <summary>
/// Đọc claim từ JWT TourKit — KHÔNG verify chữ ký.
///
/// <para>An toàn vì chỉ dùng cho JWT do CHÍNH proxy vừa lấy được sau khi login thành công
/// (TkSessionStore giữ), không phải token do client gửi lên.</para>
///
/// <para><b>Ranh giới:</b> tuyệt đối KHÔNG gọi mấy hàm này trên token lấy từ thân yêu cầu hay
/// header của trình duyệt — không kiểm chữ ký thì ai cũng tự khai mình là admin. Quyết định
/// quyền phải đọc từ <c>TkSession.IsAdmin</c> / <c>TkSession.CrmUserId</c>, tức giá trị đã chốt
/// trên máy chủ lúc đăng nhập.</para>
/// </summary>
public static class JwtClaims
{
    /// Lấy claim user_id (số hoặc chuỗi số). Trả null nếu JWT rác/thiếu claim.
    public static int? TryGetUserId(string jwt)
    {
        try
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length < 2) return null;
            // base64url → base64 chuẩn rồi pad cho đủ bội số 4 (JWT bỏ dấu '=' ở cuối).
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("user_id", out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Claim <c>is_admin</c> của CRM (AuthService.cs: <c>user.IsFullPermission</c>).
    ///
    /// <para><b>Thiếu claim = KHÔNG phải admin.</b> Sai theo hướng an toàn: đoán nhầm thành
    /// admin thì cả công ty đọc được hộp thư của nhau.</para>
    ///
    /// <para>ERP ghi bằng <c>bool.ToString()</c> nên giá trị là <c>"True"</c>/<c>"False"</c> —
    /// so sánh phân biệt hoa thường là admin âm thầm rớt xuống nhân viên thường.</para>
    /// </summary>
    public static bool TryGetIsAdmin(string jwt)
    {
        try
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length < 2) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("is_admin", out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false
            };
        }
        catch { return false; }
    }
}
