using TourkitAiProxy.Infrastructure.AiKeys;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.AiKeys;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Key AI riêng của công ty (BYO — mở lại 03/10/2026).
/// <list type="bullet">
/// <item><c>GET    /api/v1/ai-key</c> — cấu hình hiện tại. KHÔNG BAO GIỜ trả key thô, chỉ bản che.</item>
/// <item><c>PUT    /api/v1/ai-key</c> — kiểm bằng một lệnh gọi thật, qua mới lưu.</item>
/// <item><c>POST   /api/v1/ai-key/enabled</c> — bật/tắt mà không phải nhập lại key.</item>
/// <item><c>DELETE /api/v1/ai-key</c> — xoá, công ty quay về dùng key hệ thống + trừ lượt.</item>
/// </list>
///
/// <para>Cả bốn đòi quyền <b>Cấu hình hệ thống</b> (<c>CH_HT_XEM</c>) — key này quyết định mọi lệnh AI
/// của cả công ty chạy bằng tài khoản nào, và ai trả tiền.</para>
///
/// <para>Cờ <c>Features:ByoAiKey</c> tắt: GET trả <c>featureOn:false</c> để giao diện nói ra, ghi/xoá
/// trả 404. Không để người dùng lưu được một thứ không có tác dụng gì.</para>
/// </summary>
public static class AiKeyEndpoints
{
    public record SaveReq(string? Provider, string? Model, string? ApiKey, bool? Enabled);
    public record EnabledReq(bool Enabled);

    public static IEndpointRouteBuilder MapAiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1/ai-key");

        v1.MapGet("", async (HttpContext ctx, TkSessionStore sessions, TenantAiKeyRepository repo,
            TenantAiKeyStore store) =>
        {
            var a = await AuthAsync(ctx, sessions);
            if (a.Error is not null) return a.Error;
            if (!store.FeatureOn) return Results.Json(new { featureOn = false });

            var k = await repo.GetAsync(a.Tenant!, ctx.RequestAborted);
            if (k is null) return Results.Json(new { featureOn = true, configured = false });
            return Results.Json(new
            {
                featureOn = true,
                configured = true,
                provider = k.Provider,
                model = k.Model,
                masked = k.Masked,          // KHÔNG BAO GIỜ trả ApiKeyEnc hay key thô
                enabled = k.Enabled,
                validatedAtUtc = k.ValidatedAtUtc,
                updatedAtUtc = k.UpdatedAtUtc,
                updatedBy = k.UpdatedBy,
                // Key riêng đang hỏng: lượt AI đang chạy bằng key chung và BỊ TRỪ LƯỢT — phải nói ra.
                failing = k.IsFailing,
                failCount = k.FailCountSinceOk,
                lastFailAtUtc = k.LastFailAtUtc,
                lastFailReason = k.LastFailReason,
            });
        });

        v1.MapPut("", async (SaveReq body, HttpContext ctx, TkSessionStore sessions,
            TenantAiKeyRepository repo, TenantAiKeyStore store, TenantAiKeyValidator validator) =>
        {
            var a = await AuthAsync(ctx, sessions);
            if (a.Error is not null) return a.Error;
            if (!store.FeatureOn) return Results.NotFound();

            var provider = body.Provider?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(provider))
                return Results.Json(new { ok = false, error = "Chưa chọn nhà cung cấp" }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(body.ApiKey))
                return Results.Json(new { ok = false, error = "Chưa nhập key" }, statusCode: 400);

            // Kiểm bằng một lệnh gọi thật TRƯỚC khi lưu. Không qua → không lưu gì cả.
            var kq = await validator.ValidateAsync(a.Tenant!, provider, body.Model, body.ApiKey,
                ctx.RequestAborted);
            if (!kq.Ok) return Results.Json(new { ok = false, error = kq.Error }, statusCode: 400);

            await repo.UpsertAsync(a.Tenant!, provider, body.Model, body.ApiKey.Trim(),
                enabled: body.Enabled ?? true, validatedAtUtc: DateTime.UtcNow, updatedBy: a.User,
                ctx.RequestAborted);
            // Xoá đệm Redis + báo mọi máy chủ nạp lại NGAY — lệnh AI kế tiếp đã chạy bằng key mới.
            await store.InvalidateAsync(ctx.RequestAborted);

            return Results.Json(new
            {
                ok = true,
                masked = Domain.AiKeys.TenantAiKey.MaskOf(body.ApiKey.Trim()),
                model = kq.Model,
            });
        });

        v1.MapPost("/enabled", async (EnabledReq body, HttpContext ctx, TkSessionStore sessions,
            TenantAiKeyRepository repo, TenantAiKeyStore store) =>
        {
            var a = await AuthAsync(ctx, sessions);
            if (a.Error is not null) return a.Error;
            if (!store.FeatureOn) return Results.NotFound();

            var doi = await repo.SetEnabledAsync(a.Tenant!, body.Enabled, a.User, ctx.RequestAborted);
            if (!doi) return Results.Json(new { ok = false, error = "Công ty chưa khai key" }, statusCode: 404);
            await store.InvalidateAsync(ctx.RequestAborted);
            return Results.Json(new { ok = true, enabled = body.Enabled });
        });

        v1.MapDelete("", async (HttpContext ctx, TkSessionStore sessions, TenantAiKeyRepository repo,
            TenantAiKeyStore store) =>
        {
            var a = await AuthAsync(ctx, sessions);
            if (a.Error is not null) return a.Error;
            if (!store.FeatureOn) return Results.NotFound();

            var xoa = await repo.DeleteAsync(a.Tenant!, ctx.RequestAborted);
            await store.InvalidateAsync(ctx.RequestAborted);
            return Results.Json(new { ok = true, removed = xoa });
        });

        return routes;
    }

    private record Auth(string? Tenant, string? User, IResult? Error);

    /// Phiên hợp lệ + quyền Cấu hình hệ thống. Cùng luật với tài khoản dịch vụ ở WorkflowEndpoints.
    private static async Task<Auth> AuthAsync(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault();
        var s = sessions.Get(sid);
        if (s is null)
            return new(null, null, Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401));

        await sessions.EnsurePermissionsAsync(sid!, ctx.RequestAborted);
        if (!sessions.HasPermission(sid!, TkPermissionCodes.CauHinhHeThong))
            return new(null, null, Results.Json(
                new { error = "Bạn không có quyền Cấu hình hệ thống (CH_HT_XEM)." }, statusCode: 403));

        return new(s.TenantId, s.Username, null);
    }
}
