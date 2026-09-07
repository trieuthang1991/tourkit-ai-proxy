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
    /// <para><b>Chưa có dòng cấu hình, hoặc <c>scope_own_only</c> tắt ⇒ xem tất cả</b> — giữ
    /// nguyên hành vi của khách đang chạy. Bật rồi thì admin xem tất cả, còn lại chỉ xem hội
    /// thoại đã giao cho mình.</para>
    /// </summary>
    public static async Task<(Ctx Phien, NguoiXem Xem)?> ReadNguoiXemAsync(
        HttpContext ctx, TkSessionStore sessions, ChatAssignRepository assign,
        CancellationToken ct = default)
    {
        var a = Read(ctx, sessions);
        if (a == null) return null;

        var cauHinh = assign.Configured ? await assign.LayCauHinhAsync(a.TenantId, ct) : null;
        if (cauHinh is null or { ScopeOwnOnly: false })
            return (a, new NguoiXem(null, XemTatCa: true));

        // Tự lấp CrmUserId nếu phiên cũ chưa có — KHÔNG bắt người dùng đăng nhập lại.
        var maNguoi = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
        // TẠM: quản trị viên chat chốt theo TÊN ĐĂNG NHẬP, không đọc claim/cột IsAdmin nữa —
        // yêu cầu của chủ dự án (07/09/2026): "đừng xoá tránh lỗi, cứ để tạm đấy". Cột
        // dbo.TkSessions.IsAdmin, TkSession.IsAdmin, JwtClaims.TryGetIsAdmin vẫn nạp và ghi
        // bình thường — chat chỉ THÔI ĐỌC tới chúng. Khi hệ quyền thật (theo permission, không
        // theo tên) vào, đổi đúng dòng này lại, đừng đổi cả cụm.
        var xemTatCa = string.Equals(a.Username, "admin", StringComparison.OrdinalIgnoreCase);
        return (a, new NguoiXem(maNguoi, XemTatCa: xemTatCa));
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
