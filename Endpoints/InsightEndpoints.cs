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

            var items = await repo.ListAsync(a.TenantId, a.Username, kind,
                unread == true, Math.Max(0, offset ?? 0), limit ?? 30, ct);
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

            return Results.Json(new { count = await repo.UnreadCountAsync(a.TenantId, a.Username, ct, kind) }, Web);
        });

        g.MapPost("/{id:long}/read", async (long id, HttpContext ctx, TkSessionStore sessions,
            InsightRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            // Repo tự kẹp theo tenant/user → id của công ty khác không đánh dấu được.
            await repo.MarkReadAsync(a.TenantId, a.Username, id, ct);
            return Results.Json(new { ok = true }, Web);
        });

        g.MapPost("/read-all", async (HttpContext ctx, TkSessionStore sessions,
            InsightRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            await repo.MarkAllReadAsync(a.TenantId, a.Username, ct);
            return Results.Json(new { ok = true }, Web);
        });

        return routes;
    }
}
