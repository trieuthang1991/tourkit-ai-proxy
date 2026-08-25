using System.Text.Json;

namespace TourkitAiProxy.Infrastructure.TourKit;

/// <summary>
/// Đọc claim từ JWT TourKit — KHÔNG verify chữ ký.
///
/// An toàn vì chỉ dùng cho JWT do CHÍNH proxy vừa lấy được sau khi login thành công
/// (TkSessionStore giữ), không phải token do client gửi lên. Mục đích duy nhất: lấy
/// <c>user_id</c> của CRM để lọc dữ liệu "của riêng người này" khi dựng bản tin.
/// KHÔNG dùng hàm này để quyết định quyền truy cập.
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
}
