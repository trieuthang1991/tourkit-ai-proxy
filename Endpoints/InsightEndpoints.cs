using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Bảng tin trong app (<c>dbo.AgentInsights</c>): bản tin sáng + cảnh báo do workflow sinh ra.
///
/// <para>Đây là kênh gửi chắc chắn nhất — không phụ thuộc email/Telegram/Zalo — nên cũng là chỗ
/// xem lại khi các kênh ngoài hỏng.</para>
///
/// <para>Mọi endpoint yêu cầu <c>X-Session-Id</c>; tenant + user lấy từ phiên chứ KHÔNG nhận từ
/// client, nếu không thì ai cũng đọc được thông báo của công ty khác bằng cách đổi tham số.</para>
///
/// <para><b>Dòng cấp công ty cần quyền (20/08/2026).</b> Thẻ có người phụ trách thì chỉ người đó
/// thấy — vốn đã đúng. Nhưng thẻ KHÔNG có người phụ trách (<c>Username=''</c>, vd cảnh báo doanh
/// thu bất thường) trước đây hiện cho MỌI tài khoản trong công ty: nhân viên bán tour cũng đọc
/// được doanh thu và mức lệch của cả công ty. Nay chỉ tài khoản có <c>CH_HT_XEM</c> mới thấy —
/// cùng luật với phần cấu hình cấp công ty ở trang Tự động hoá.</para>
/// </summary>
public static class InsightEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapInsightEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/insights");

        g.MapGet("", async (HttpContext ctx, TkSessionStore sessions, InsightRepository repo,
            string? kind, bool? unread, int? offset, int? limit, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Số liệu cấp công ty (doanh thu bất thường…) chỉ dành cho người có quyền cấu hình.
            var caCongTy = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            var items = await repo.ListAsync(a.TenantId, a.Username, kind,
                unread == true, Math.Max(0, offset ?? 0), limit ?? 30, ct, companyWide: caCongTy);
            // Chỉ bản tin (sale/ceo) mới có nút "Nghe" → tính speakText tại chỗ; loại khác để null.
            var shaped = items.Select(it => new
            {
                it.Id, it.TenantId, it.Username, it.Kind, it.Severity, it.Title, it.Body,
                it.DataJson, it.AlertKey, it.IsRead, it.CreatedUtc,
                speakText = BriefTypes.IsValid(it.Kind) ? BriefNarration.ToSpeakable(it.Body) : null,
            });
            return Results.Json(new { items = shaped }, Web);
        });

        // Số chưa đọc cho badge chuông — tách riêng vì frontend gọi định kỳ, không cần kéo cả danh sách.
        g.MapGet("/unread-count", async (HttpContext ctx, TkSessionStore sessions,
            InsightRepository repo, string? kind, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Phải hỏi quyền y như lúc liệt kê: đếm rộng hơn danh sách thì chuông báo có tin
            // mới mà mở ra chẳng thấy gì — người dùng tưởng hỏng.
            var caCongTy = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            return Results.Json(new
            {
                count = await repo.UnreadCountAsync(a.TenantId, a.Username, ct, kind, companyWide: caCongTy),
            }, Web);
        });

        g.MapPost("/{id:long}/read", async (long id, HttpContext ctx, TkSessionStore sessions,
            InsightRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Repo tự kẹp theo tenant/user → id của công ty khác không đánh dấu được. Dòng cấp
            // công ty cũng vậy: không được xem thì cũng không đánh dấu đã đọc hộ người khác.
            var caCongTy = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            await repo.MarkReadAsync(a.TenantId, a.Username, id, ct, companyWide: caCongTy);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapPost("/read-all", async (HttpContext ctx, TkSessionStore sessions,
            InsightRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            var caCongTy = await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct);
            await repo.MarkAllReadAsync(a.TenantId, a.Username, ct, companyWide: caCongTy);
            return Results.Json(new { ok = true }, Web);
        });

        return routes;
    }
}
