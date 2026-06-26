using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Nạp quota AI qua Tingee VietQR.
///   GET  /api/v1/quota/tiers                       — catalog 3 gói
///   POST /api/v1/quota/order                       — tạo order pending + QR (auth)
///   GET  /api/v1/quota/order/{id}/status           — poll status (auth + ownership)
///   POST /api/v1/quota/order/{id}/cancel           — hủy order pending (auth + ownership)
///   GET  /api/v1/quota/orders                      — lịch sử của tenant (auth)
///   POST /api/v1/quota/webhook/tingee              — IPN từ Tingee (no auth, HMAC verify)
///   POST /api/v1/quota/dev/simulate-paid/{id}      — DEV only (Tingee:Mock=true) simulate IPN
///   GET  /api/v1/admin/quota/orders                — admin list all (admin gate)
/// </summary>
public static class QuotaOrderEndpoints
{
    public static void MapQuotaOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1/quota");

        // ─── Catalog ─────────────────────────────────────────────────────────────
        v1.MapGet("/tiers", (ITingeeClient tingee) =>
        {
            return Results.Json(new
            {
                tiers = QuotaTierCatalog.All,
                mock  = tingee.IsMock,
                account = tingee.Account,        // FE hiện STK + tên khi user CK tay (fallback)
            });
        });

        // ─── Tạo order ───────────────────────────────────────────────────────────
        v1.MapPost("/order", async (CreateOrderReq req, HttpContext ctx,
            QuotaOrderRepository orders, ITingeeClient tingee, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            var tier = QuotaTierCatalog.Find(req.TierId);
            if (tier == null) return Results.BadRequest(new { error = "Gói không hợp lệ" });

            var orderId = BuildOrderId(sess.TenantId);
            var memo    = orderId;       // nội dung CK = OrderId → webhook match
            var now     = DateTime.UtcNow;
            var expires = now.AddMinutes(15);     // 15 phút đủ user mở app banking + chuyển

            var qr = await tingee.CreateQrAsync(orderId, tier.AmountVnd, memo);

            var row = new QuotaOrderRepository.OrderRow(
                Id: orderId, TenantId: sess.TenantId, TierId: tier.Id,
                AmountVnd: tier.AmountVnd, QuotaUnits: tier.QuotaUnits, Status: "pending",
                QrPayload: qr.QrPayload,
                BankBin: tingee.Account.BankBin, AccountNumber: tingee.Account.AccountNumber,
                AccountName: tingee.Account.AccountName,
                Memo: memo, ExpiresAt: expires, CreatedAt: now, PaidAt: null,
                TingeeRefId: null, TingeeRaw: null, CreatedBy: sess.Username);
            await orders.InsertAsync(row);

            return Results.Json(new CreateOrderResp(
                OrderId: orderId, TierId: tier.Id, TierName: tier.Name,
                AmountVnd: tier.AmountVnd, QuotaUnits: tier.QuotaUnits, Memo: memo,
                QrPayload: qr.QrPayload, BankBin: tingee.Account.BankBin,
                AccountNumber: tingee.Account.AccountNumber, AccountName: tingee.Account.AccountName,
                ExpiresAt: expires.ToString("o"), ExpiresInSeconds: (int)(expires - now).TotalSeconds));
        });

        // ─── Status poll ─────────────────────────────────────────────────────────
        v1.MapGet("/order/{id}/status", async (string id, HttpContext ctx,
            QuotaOrderRepository orders, TenantQuotaStore quota, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);

            // Mỗi lần poll cũng tiện flip những order quá hạn → expired (cron-less).
            await orders.ExpireStaleAsync();

            var row = await orders.GetByIdAsync(id);
            if (row == null) return Results.NotFound(new { error = "Không tìm thấy đơn" });
            if (row.TenantId != sess.TenantId)
                return Results.Json(new { error = "Không có quyền xem đơn này" }, statusCode: 403);

            QuotaSnapshot? snap = row.Status == "paid" ? quota.Snapshot(sess.TenantId) : null;

            return Results.Json(new OrderStatusResp(
                OrderId: row.Id, Status: row.Status,
                PaidAt: row.PaidAt.HasValue ? DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc).ToString("o") : null,
                AddedUnits: row.Status == "paid" ? row.QuotaUnits : null,
                QuotaUsed:      snap?.Used,
                QuotaLimit:     snap?.Limit,
                QuotaRemaining: snap?.Remaining));
        });

        // ─── Cancel ──────────────────────────────────────────────────────────────
        v1.MapPost("/order/{id}/cancel", async (string id, HttpContext ctx,
            QuotaOrderRepository orders, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            var row = await orders.GetByIdAsync(id);
            if (row == null) return Results.NotFound(new { error = "Không tìm thấy đơn" });
            if (row.TenantId != sess.TenantId)
                return Results.Json(new { error = "Không có quyền" }, statusCode: 403);
            await orders.CancelAsync(id);
            return Results.Ok(new { ok = true });
        });

        // ─── Lịch sử tenant ─────────────────────────────────────────────────────
        v1.MapGet("/orders", async (HttpContext ctx, QuotaOrderRepository orders, TkSessionStore sessions) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ" }, statusCode: 401);
            var list = await orders.ListByTenantAsync(sess.TenantId);
            return Results.Json(new { items = list });
        });

        // ─── Webhook IPN (Tingee gửi về) ─────────────────────────────────────────
        // KHÔNG có auth header (Tingee không gửi session). Verify bằng HMAC X-Tingee-Signature.
        v1.MapPost("/webhook/tingee", async (HttpContext ctx,
            QuotaOrderRepository orders, TenantQuotaStore quota, ITingeeClient tingee,
            ILogger<Program> log) =>
        {
            string raw;
            using (var rd = new StreamReader(ctx.Request.Body))
                raw = await rd.ReadToEndAsync();

            var sig = ctx.Request.Headers["X-Tingee-Signature"].FirstOrDefault();
            if (!tingee.VerifyWebhookSignature(raw, sig))
            {
                log.LogWarning("[Tingee webhook] signature sai — reject");
                return Results.Json(new { error = "invalid signature" }, statusCode: 401);
            }

            TingeeWebhookPayload? payload;
            try { payload = JsonSerializer.Deserialize<TingeeWebhookPayload>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (Exception ex) { log.LogWarning(ex, "[Tingee webhook] parse body fail"); return Results.BadRequest(new { error = "bad body" }); }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Description))
                return Results.BadRequest(new { error = "thiếu description (memo)" });

            // Memo có thể có ký tự thừa (vd VCB tự prepend "TT CK QR"). Extract token TKAI-xxx.
            var orderId = ExtractOrderId(payload.Description);
            if (orderId == null)
            {
                log.LogWarning("[Tingee webhook] memo '{Memo}' không chứa OrderId — skip", payload.Description);
                return Results.Ok(new { ok = false, reason = "no order id in memo" });
            }

            var row = await orders.GetByIdAsync(orderId);
            if (row == null)
            {
                log.LogWarning("[Tingee webhook] order {Id} không tồn tại — skip", orderId);
                return Results.Ok(new { ok = false, reason = "order not found" });
            }
            if (row.Status == "paid")
            {
                log.LogInformation("[Tingee webhook] order {Id} đã paid — idempotent skip", orderId);
                return Results.Ok(new { ok = true, idempotent = true });
            }

            // Check amount khớp — tránh khách gõ nhầm số tiền.
            if (payload.Amount.HasValue && payload.Amount.Value != row.AmountVnd)
            {
                log.LogWarning("[Tingee webhook] order {Id} amount mismatch: paid {Paid} vs expected {Expected}",
                    orderId, payload.Amount, row.AmountVnd);
                // Vẫn approve nhưng note vào log — anh có thể quyết tự refund tay nếu cần.
            }

            var paid = await orders.TryMarkPaidAsync(orderId, payload.TransactionId, raw);
            if (paid == null)
            {
                // Race: webhook khác đã mark paid trước. Idempotent.
                return Results.Ok(new { ok = true, idempotent = true });
            }

            // Topup quota tenant.
            try
            {
                var snap = quota.TopUp(row.TenantId, row.QuotaUnits);
                log.LogInformation("[Tingee webhook] order {Id} paid → tenant {T} +{N} lượt → {Used}/{Limit}",
                    orderId, row.TenantId, row.QuotaUnits, snap.Used, snap.Limit);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[Tingee webhook] TopUp tenant {T} fail (order {Id}) — đơn đã paid nhưng quota CHƯA cộng!",
                    row.TenantId, orderId);
                return Results.Json(new { ok = false, error = "topup fail" }, statusCode: 500);
            }

            return Results.Ok(new { ok = true });
        });

        // ─── DEV simulate-paid (chỉ bật khi Tingee:Mock=true) ────────────────────
        v1.MapPost("/dev/simulate-paid/{id}", async (string id, HttpContext ctx,
            QuotaOrderRepository orders, TenantQuotaStore quota, ITingeeClient tingee,
            IConfiguration cfg, ILogger<Program> log) =>
        {
            if (!tingee.IsMock) return Results.Json(new { error = "Endpoint chỉ bật ở Tingee:Mock=true" }, statusCode: 403);
            var row = await orders.GetByIdAsync(id);
            if (row == null) return Results.NotFound(new { error = "không tìm thấy đơn" });
            if (row.Status == "paid") return Results.Ok(new { ok = true, idempotent = true });
            var paid = await orders.TryMarkPaidAsync(id, $"MOCK-{Guid.NewGuid().ToString("N")[..8]}", "{\"mock\":true}");
            if (paid == null) return Results.Ok(new { ok = true, idempotent = true });
            quota.TopUp(row.TenantId, row.QuotaUnits);
            log.LogInformation("[Tingee MOCK] simulate paid order {Id} → tenant {T} +{N} lượt", id, row.TenantId, row.QuotaUnits);
            return Results.Ok(new { ok = true });
        });

        // ─── Admin: liệt kê tất cả đơn ───────────────────────────────────────────
        v1.MapGet("/admin/orders", async (HttpContext ctx, QuotaOrderRepository orders,
            IConfiguration cfg) =>
        {
            if (!AdminOk(ctx, cfg)) return Results.Json(new { error = "Admin token sai/thiếu" }, statusCode: 403);
            var list = await orders.ListAllAsync();
            return Results.Json(new { items = list });
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────
    private static string Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
        ?? ctx.Request.Query["sessionId"].FirstOrDefault()
        ?? "";

    private static bool AdminOk(HttpContext ctx, IConfiguration cfg)
    {
        var expected = cfg["Admin:Token"];
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var got = ctx.Request.Headers["X-Admin-Token"].FirstOrDefault();
        return string.Equals(expected, got, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sinh OrderId dạng `TKAI-{tenantHash6}-{ts}-{rand4}` — đủ unique, ngắn để memo VCB không cắt.
    /// VCB giới hạn nội dung CK 50 ký tự, format này ~24 ký tự an toàn.
    /// </summary>
    private static string BuildOrderId(string tenantId)
    {
        var hash6 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tenantId)))
            [..6].ToUpperInvariant();
        var ts    = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("X");
        var rand4 = Random.Shared.Next(0, 0xFFFF).ToString("X4");
        return $"TKAI-{hash6}-{ts}-{rand4}";
    }

    /// <summary>
    /// Bóc OrderId TKAI-XXXXXX-XXXXXXXX-XXXX khỏi memo (VCB thường prepend "TT CK QR" hoặc tương tự).
    /// </summary>
    private static string? ExtractOrderId(string memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(memo, @"TKAI-[A-F0-9]{6}-[A-F0-9]+-[A-F0-9]{4}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }
}
