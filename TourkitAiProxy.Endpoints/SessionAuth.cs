using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Infrastructure.TourKit;

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

    /// <summary>
    /// Đọc phiên + tính luôn phạm vi xem hộp thư chat.
    ///
    /// <para>Trả <c>null</c> khi phiên hỏng — chỗ gọi trả <see cref="Unauthorized"/> như cũ.</para>
    ///
    /// <para><b>Phạm vi do QUYỀN CRM quyết, không do cấu hình trong app.</b> Có
    /// <c>CHAT_XEM_ALL</c> thì xem tất cả; chỉ có <c>CHAT_XEM</c> thì xem phần được giao cho
    /// mình. Ai không có mã nào thì không vào tới đây — bộ lọc nhóm đã chặn ở cửa.</para>
    ///
    /// <para><b>Vì sao bỏ <c>scope_own_only</c> khỏi đường này.</b> Nó ra đời khi chưa có mã
    /// quyền chat, làm công tắc tạm ở mức công ty để khỏi đột ngột kẹp khách đang chạy. Nay CRM
    /// đã có <c>CHAT_XEM</c>/<c>CHAT_XEM_ALL</c> cấp theo vai, nên giữ cả hai là hai nguồn sự
    /// thật cho cùng một câu hỏi — mà hai nguồn lệch nhau ở chuyện quyền thì thành lỗ hổng. Cột
    /// vẫn nằm trong CSDL (không xoá), chỉ thôi được đọc ở đây.</para>
    /// </summary>
    public static async Task<(Ctx Phien, NguoiXem Xem)?> ReadNguoiXemAsync(
        HttpContext ctx, TkSessionStore sessions, CancellationToken ct = default)
    {
        var a = Read(ctx, sessions);
        if (a == null) return null;

        if (await IsQuanTriChatAsync(a.SessionId, sessions, ct))
            return (a, new NguoiXem(null, XemTatCa: true));

        // Tự lấp CrmUserId nếu phiên cũ chưa có — KHÔNG bắt người dùng đăng nhập lại.
        var maNguoi = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
        return (a, new NguoiXem(maNguoi, XemTatCa: false));
    }

    /// <summary>
    /// Tài khoản này được xem <b>TẤT CẢ</b> hội thoại của công ty không — tức "quản trị chat".
    ///
    /// <para>Đọc quyền <c>CHAT_XEM_ALL</c> của CRM. Bản trước chốt bằng
    /// <c>Equals(Username, "admin")</c> và sai theo CẢ HAI chiều — xem
    /// <see cref="TkPermissionCodes.ChatXemTatCa"/> để biết số đo thật.</para>
    ///
    /// <para><b>Vẫn là MỘT hàm cho cả cụm.</b> Cùng câu hỏi này được hỏi ở ba cửa: phạm vi xem,
    /// cờ <c>isAdmin</c> phát ra giao diện, và cửa giao việc cho người khác. Ba bản chép tay mà
    /// lệch nhau thì thành lỗ hổng chứ không phải bất tiện.</para>
    /// </summary>
    public static async Task<bool> IsQuanTriChatAsync(string sid, TkSessionStore sessions,
                                                     CancellationToken ct = default)
    {
        await sessions.EnsurePermissionsAsync(sid, ct);
        return sessions.HasPermission(sid, TkPermissionCodes.ChatXemTatCa);
    }

    /// <summary>
    /// Tài khoản này có quyền <b>Cấu hình hệ thống</b> (<c>CH_HT_XEM</c>) không.
    ///
    /// <para>Trước 20/08/2026 hàm này được chép trong DigestEndpoints và WorkflowEndpoints. Bản thứ
    /// ba (InsightEndpoints) là lúc phải gom lại: đây là câu hỏi "ai được xem thứ cấp công ty",
    /// trả lời khác nhau ở hai chỗ thì thành lỗ hổng chứ không phải bất tiện.</para>
    /// </summary>
    public static async Task<bool> CanConfigSystemAsync(string sid, TkSessionStore sessions,
                                                        CancellationToken ct = default)
    {
        await sessions.EnsurePermissionsAsync(sid, ct);
        return sessions.HasPermission(sid, TkPermissionCodes.CauHinhHeThong);
    }

    public static IResult ForbiddenConfigSystem()
        => Results.Json(new { error = "Bạn không có quyền Cấu hình hệ thống (CH_HT_XEM)." }, statusCode: 403);

    /// <summary>
    /// Tài khoản có được TẠO TOUR không — điều kiện vào màn "Tính giá Tour" và mọi thao tác ghi
    /// nháp/báo giá của nó.
    ///
    /// <para>Đủ MỘT trong hai quyền là được: <c>TR_TD_TAOMOI</c> (tour đoàn GIT) hoặc
    /// <c>TR_TM_TAOMOI</c> (tour khách lẻ FIT) — vì sản phẩm cuối của màn này là một đơn GIT hoặc
    /// FIT trên CRM, ai tạo được loại nào thì làm giá loại đó.</para>
    ///
    /// <para>Sheet bug dòng 105: tài khoản đại lý/CTV chỉ có quyền đặt chỗ nhưng vẫn dựng được
    /// báo giá vì proxy trước đây KHÔNG kiểm quyền gì ở nhóm endpoint này. Quyền lấy từ chính CRM
    /// (SP <c>uspGetAllRoleFunctionByDepartmentId</c> theo phòng ban) nên "chỉ có quyền như trên
    /// CRM" là đúng nghĩa đen — không có admin auto-grant ở đây, y như web.</para>
    /// </summary>
    public static async Task<bool> CanCreateTourAsync(string sid, TkSessionStore sessions,
                                                      CancellationToken ct = default)
    {
        await sessions.EnsurePermissionsAsync(sid, ct);
        return sessions.HasPermission(sid, TkPermissionCodes.TaoTourGit)
            || sessions.HasPermission(sid, TkPermissionCodes.TaoTourFit);
    }

    public static IResult ForbiddenCreateTour()
        => Results.Json(new { error = "Bạn không có quyền tạo tour (TR_TD_TAOMOI / TR_TM_TAOMOI)." }, statusCode: 403);
}
