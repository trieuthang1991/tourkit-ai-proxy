using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Đọc phiên TourKit từ request (header <c>X-Session-Id</c>, hoặc query/body <c>sessionId</c>).
///
/// <para>Trước file này mỗi nhóm endpoint tự khai một bản <c>RequireSession</c> riêng (Ai, Mail,
/// Visa, Workflow…). Bốn bản giống nhau từng dòng nên chưa lệch, nhưng thêm bản thứ năm thì rủi ro
/// lệch bắt đầu thật: sửa cách đọc phiên ở một chỗ mà quên chỗ khác thì có endpoint nhận sai tenant.
/// Nhóm mới dùng chung file này; các nhóm cũ để nguyên (đổi hết = churn không cần thiết ngay).</para>
/// </summary>
public static class SessionAuth
{
    public record Ctx(string SessionId, string TenantId, string Username);

    public static Ctx? Read(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                  ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = sessions.Get(sid);
        return s == null ? null : new Ctx(sid!, s.TenantId, s.Username);
    }

    public static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
