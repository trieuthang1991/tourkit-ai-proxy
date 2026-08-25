using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Quota AI per-tenant.
///   GET  /api/v1/quota                       — snapshot tenant hiện tại (cần X-Session-Id)
///   GET  /api/v1/admin/quota                 — toàn bộ tenant (admin)
///   POST /api/v1/admin/quota/{tenant}/topup  — cộng thêm lượt cho tenant (admin)
///
/// Admin protect: chấp nhận phiên đăng nhập quản trị (`X-Admin-Session`) HOẶC chuỗi tĩnh
/// `X-Admin-Token` khớp `Admin:Token`. Không có gì → 403. Xem `AdminOk`.
public static class QuotaEndpoints
{
    public static void MapQuotaEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── User: tenant của mình ───────────────────────────────────────────────
        v1.MapGet("/quota", (HttpContext ctx, TenantQuotaStore store, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            return Results.Json(store.Snapshot(sess.TenantId));
        });

        // ─── Admin: liệt kê toàn bộ tenant ───────────────────────────────────────
        v1.MapGet("/admin/quota", (HttpContext ctx, TenantQuotaStore store, IConfiguration cfg) =>
        {
            if (!AdminOk(ctx, cfg)) return Results.Json(new { error = "Admin token sai/thiếu" }, statusCode: 403);
            return Results.Json(new { items = store.ListAll() });
        });

        // ─── Admin: top-up cho 1 tenant ──────────────────────────────────────────
        v1.MapPost("/admin/quota/{tenant}/topup", (string tenant, TopUpReq req, HttpContext ctx,
            TenantQuotaStore store, IConfiguration cfg) =>
        {
            if (!AdminOk(ctx, cfg)) return Results.Json(new { error = "Admin token sai/thiếu" }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(tenant)) return Results.BadRequest(new { error = "tenant trống" });
            if (req.Amount <= 0) return Results.BadRequest(new { error = "amount phải > 0" });
            try
            {
                var snap = store.TopUp(tenant, req.Amount);
                return Results.Json(snap);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });
    }

    public record TopUpReq(int Amount);

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
        ?? ctx.Request.Query["sessionId"].FirstOrDefault();

    /// <summary>
    /// Cổng của hai đường quản trị quota. Chấp nhận <b>một trong hai</b> giấy tờ:
    ///
    /// <list type="number">
    /// <item><b>Phiên đăng nhập quản trị</b> (<c>X-Admin-Session</c>) — thứ trang
    /// <c>/admin-trav-ai</c> vẫn dùng, lấy từ <c>POST /api/v1/admin/auth/login</c>.</item>
    /// <item><b>Chuỗi tĩnh</b> <c>X-Admin-Token</c> khớp <c>Admin:Token</c> — giữ cho script/ops
    /// bên ngoài đang gọi sẵn, để không phải sửa gì bên đó.</item>
    /// </list>
    ///
    /// <para>⚠️ <b>Không giấy tờ nào thì TỪ CHỐI.</b> Bản cũ trả <c>true</c> khi <c>Admin:Token</c>
    /// để trống, kèm chú thích "dev mode" — nhưng không có gì bảo đảm đó là máy dev. Mà
    /// <c>POST /admin/quota/{tenant}/topup</c> thì <b>cộng thẳng lượt AI</b>: ai biết đường dẫn là
    /// tự cấp vô hạn cho tenant bất kỳ, không lỗi, không log. Thử thật trên staging 25/08/2026:
    /// phiên người dùng THƯỜNG mở được <c>GET /admin/quota</c> của mọi công ty.</para>
    ///
    /// <para><b>Vì sao cách này không làm hỏng gì.</b> Nút "Nạp lượt" trong trang quản trị KHÔNG đi
    /// qua đây — nó gọi <c>/api/v1/admin/ui/quota/{tenant}/topup</c>, vốn đã có
    /// <c>RequireAdminSession()</c>. Tài liệu cũ dặn giữ nguyên chỗ này "để khỏi vỡ Tingee", nhưng
    /// Tingee thật bắn IPN vào stack CŨ (<c>tourkit</c>, <c>/api/hooks/tingee</c>, HMAC SHA512) chứ
    /// không hề gọi proxy — nên căn cứ đó không còn đúng. Ai vẫn muốn dùng chuỗi tĩnh thì chỉ cần
    /// đặt <c>Admin:Token</c>, đúng như trước.</para>
    /// </summary>
    private static bool AdminOk(HttpContext ctx, IConfiguration cfg)
    {
        // 1) Phiên đăng nhập quản trị — không cần thêm cấu hình nào.
        var phien = ctx.Request.Headers["X-Admin-Session"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(phien))
        {
            var store = ctx.RequestServices.GetService<Services.Admin.AdminSessionStore>();
            if (store?.Get(phien) is not null) return true;
        }

        // 2) Chuỗi tĩnh, CHỈ khi đã cấu hình. Trống → đóng, không mở như trước.
        var expected = cfg["Admin:Token"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var got = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        return string.Equals(expected, got, StringComparison.Ordinal);
    }
}
