using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Infrastructure.Security;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// SSO HAI CHIỀU giữa Trav-ai và CRM (tourkit web) — HMAC-SHA256, ZERO password.
/// Chung secret "Sso:Secret" (⇔ AppSettings "SsoSecret" bên CRM) và chung hàm HmacHex.
///
/// CHIỀU 1 — Trav-ai → CRM (proxy PHÁT, CRM NHẬN ở PublicAPI/SsoController.cs):
///   POST /api/v1/crm-sso-ticket → ký danh tính phiên hiện tại → CRM /api/sso/register-code → { url }.
///
/// CHIỀU 2 — CRM → Trav-ai (CRM PHÁT ở SsoController.TravAiSsoTicket, proxy NHẬN). MIRROR y hệt chiều 1,
/// chỉ đổi vai — 2 endpoint, cùng cơ chế HMAC + code 1-lần:
///   POST /api/v1/sso/register-code — verify X-Sign → code 1-lần TTL 60s → { exchangeUrl }.
///   GET  /api/v1/sso/exchange?code= — đọc+xoá code → Set-Cookie tk_sso (30s) → 302 về đích.
///   Khác CRM đúng một chỗ: CRM thiết lập đăng nhập bằng Forms-auth cookie do chính nó đọc lại, còn
///   SPA Trav-ai giữ phiên ở localStorage nên phải trao qua cookie ngắn hạn cho core/auth.jsx nhặt.
///
public static class CrmSsoEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void MapCrmSsoEndpoints(this IEndpointRouteBuilder routes)
    {
        // ─── CHIỀU 1: POST /api/v1/crm-sso-ticket → { url }  (require X-Session-Id). Frontend mở url ở tab mới.
        routes.MapPost("/api/v1/crm-sso-ticket", async (HttpContext ctx, TkSessionStore sessions, IConfiguration cfg) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                      ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var s = sessions.Get(sid);
            if (s == null) return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

            var secret = cfg["Sso:Secret"];
            // Hỗ trợ ENC: (config chứa ciphertext Crypton → giải ra secret thật). Giống Redis conn string.
            if (!string.IsNullOrEmpty(secret) && secret.StartsWith("ENC:", StringComparison.Ordinal))
                secret = Crypton.Decrypt(secret.Substring(4));
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
                return Results.Json(new { error = "SSO CRM chưa cấu hình (Sso:Secret)" }, statusCode: 500);

            // Đích redirect trong CRM — CHỈ path nội bộ "/..." (chống open-redirect), mặc định /customer-data.
            var redirect = ctx.Request.Query["redirect"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(redirect) || !redirect.StartsWith('/') || redirect.StartsWith("//"))
                redirect = "/customer-data";

            var t = (s.TenantId ?? "").Trim();
            if (string.IsNullOrEmpty(t)) return Results.Json(new { error = "Phiên thiếu tenant" }, statusCode: 400);

            // Host CRM: mặc định {tenant}.tourkit.vn (prod, khớp crmUrl phía JS — có '.' = host đầy đủ).
            // LOCAL DEV: override qua Sso:BaseUrl (vd "https://localhost:44300" hoặc "http://localhost:5001")
            // để proxy trỏ CRM chạy tại máy thay vì domain thật. Trống ở prod = giữ nguyên hành vi cũ.
            string scheme = "https";
            string host;
            var baseUrl = cfg["Sso:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var bu))
            {
                scheme = bu.Scheme;
                host = bu.Authority;              // gồm cả :port nếu có
            }
            else
            {
                host = t.Contains('.') ? t : $"{t}.tourkit.vn";
            }

            // Body danh tính (KHÔNG password/hash). CRM verify X-Sign trên ĐÚNG chuỗi bytes này.
            var body = JsonSerializer.Serialize(new
            {
                tenant = t,
                username = s.Username,
                host,
                scheme,
                redirect,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });

            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, $"{scheme}://{host}/api/sso/register-code")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                msg.Headers.Add("X-Sign", HmacHex(body, secret));
                using var resp = await Http.SendAsync(msg, ctx.RequestAborted);
                var respBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                {
                    // Lộ message lỗi thật của CRM (body { error } từ SsoController) để chẩn đoán, không nuốt.
                    // TUYỆT ĐỐI KHÔNG trả 502/504: travelai.vn đứng sau Cloudflare, mà Cloudflare THAY body
                    // origin bằng trang HTML "Bad gateway Error code 502" của nó → message dưới đây bay sạch,
                    // browser nhận <!DOCTYPE → "Unexpected token '<'". 424 (Failed Dependency) không bị chặn.
                    var snippet = (respBody ?? "").Trim();
                    if (snippet.Length > 400) snippet = snippet.Substring(0, 400);
                    return Results.Json(new { error = $"CRM từ chối SSO (HTTP {(int)resp.StatusCode}) @ {scheme}://{host} — {snippet}" }, statusCode: 424);
                }
                using var doc = JsonDocument.Parse(respBody);
                if (!doc.RootElement.TryGetProperty("exchangeUrl", out var ex) || ex.ValueKind != JsonValueKind.String)
                    return Results.Json(new { error = "CRM trả response không hợp lệ" }, statusCode: 424);
                return Results.Json(new { url = ex.GetString() });
            }
            catch (Exception e)
            {
                return Results.Json(new { error = "Không kết nối được CRM: " + e.Message }, statusCode: 424);
            }
        }).DisableAntiforgery();

        // ─── CHIỀU 2a: POST /api/v1/sso/register-code — MIRROR SsoController.RegisterCode bên CRM ───
        // Auth: HMAC (header X-Sign). CRM POST body danh tính (không password). Verify chữ ký → sinh
        // code 1-lần TTL 60s → trả { exchangeUrl }.
        routes.MapPost("/api/v1/sso/register-code", async (HttpContext ctx, IConfiguration cfg, SsoCodeStore store) =>
        {
            var secret = cfg["Sso:Secret"];
            if (!string.IsNullOrEmpty(secret) && secret.StartsWith("ENC:", StringComparison.Ordinal))
                secret = Crypton.Decrypt(secret.Substring(4));
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
                return Results.Json(new { error = "SSO chưa cấu hình (Sso:Secret)" }, statusCode: 500);

            // Đọc RAW body để verify HMAC trên đúng bytes CRM đã ký (KHÔNG dùng model-binding).
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            var sign = ctx.Request.Headers["X-Sign"].FirstOrDefault();
            if (string.IsNullOrEmpty(sign) || !VerifyHmac(body, sign, secret))
                return Results.Json(new { error = "Sai chữ ký" }, statusCode: 401);

            string tenant, username;
            try
            {
                var root = JsonDocument.Parse(body).RootElement;
                tenant = root.GetProperty("tenant").GetString() ?? "";
                username = root.GetProperty("username").GetString() ?? "";
            }
            catch { return Results.Json(new { error = "Payload không hợp lệ" }, statusCode: 400); }

            if (tenant.Length == 0 || username.Length == 0)
                return Results.Json(new { error = "Payload thiếu" }, statusCode: 400);

            // Sinh code random 256-bit + lưu TTL 60s (giá trị = nguyên body đã verify).
            var code = store.GenCode();
            if (!store.Save(code, body, TimeSpan.FromSeconds(60)))
                return Results.Json(new { error = "Không lưu được code" }, statusCode: 424);

            var self = (cfg["TravAiSso:BaseUrl"] ?? "").Trim().TrimEnd('/');
            if (self.Length == 0) self = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new { exchangeUrl = $"{self}/api/v1/sso/exchange?code={Uri.EscapeDataString(code)}" });
        }).DisableAntiforgery();

        // ─── CHIỀU 2b: GET /api/v1/sso/exchange?code= — MIRROR SsoController.Exchange bên CRM ───
        // Browser mở. Đọc+XOÁ code (one-time, chống replay) → thiết lập phiên → 302 redirect.
        // Trên URL chỉ có 1 code random dùng-một-lần.
        //
        // KHÁC CRM đúng một chỗ, do bản chất hai bên: CRM thiết lập đăng nhập bằng Forms-auth cookie mà
        // chính nó đọc lại; SPA Trav-ai giữ phiên ở localStorage nên server không ghi thẳng được — phải
        // trao qua cookie ngắn hạn `tk_sso` để core/auth.jsx nhặt lúc boot (adoptSso).
        // MapGet tường minh thắng app.MapFallback(ServeIndex) nên không bị SPA nuốt.
        routes.MapGet("/api/v1/sso/exchange", async (HttpContext ctx, TkSessionStore sessions, SsoCodeStore store, string? code) =>
        {
            if (string.IsNullOrEmpty(code)) return Results.Redirect("/");

            var payloadJson = store.TakeOnce(code);
            if (string.IsNullOrEmpty(payloadJson)) return Results.Redirect("/");

            string tenant, username, next;
            try
            {
                var root = JsonDocument.Parse(payloadJson).RootElement;
                tenant = root.GetProperty("tenant").GetString() ?? "";
                username = root.GetProperty("username").GetString() ?? "";
                next = root.TryGetProperty("redirect", out var r) ? r.GetString() ?? "" : "";
            }
            catch { return Results.Redirect("/"); }

            if (tenant.Length == 0 || username.Length == 0) return Results.Redirect("/");
            if (next.Length == 0 || !next.StartsWith('/') || next.StartsWith("//")) next = "/";

            // Phiên cũ có thể chưa nạp Permissions → nạp trước khi vào app, tránh ẩn oan nút/sidebar.
            // Không tra ra phiên (user chưa từng đăng nhập Trav-ai) → cookie rỗng: SPA hiểu là "bỏ phiên
            // đang có rồi hiện màn đăng nhập", tránh để người vừa sang dùng nhầm danh tính người trước.
            var s = await sessions.FindByUserAsync(tenant, username, ctx.RequestAborted);
            if (s == null)
            {
                // Chưa từng đăng nhập TravAi → TỰ TẠO phiên qua sso-token (JWT không password) để đăng nhập
                // luôn kể cả lần đầu (giống chiều AI→web). sso-token lỗi / chưa cấu hình / user không tồn tại
                // → nuốt (client đã log), cookie rỗng → về màn login như cũ (không chặn luồng).
                try { s = await sessions.CreateFromSsoAsync(tenant, username, ctx.RequestAborted); }
                catch { /* fallback: cookie rỗng → login */ }
            }
            if (s != null) await sessions.EnsurePermissionsAsync(s.Id, ctx.RequestAborted);

            // Response mang Set-Cookie thì tuyệt đối không được cache lại.
            ctx.Response.Headers.CacheControl = "no-store, no-cache";
            ctx.Response.Cookies.Append("tk_sso", s?.Id ?? "", new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.FromSeconds(30),
                HttpOnly = false,   // SPA phải đọc được bằng JS để nạp vào localStorage
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
            });
            return Results.Redirect(next);
        });
    }

    // HMAC-SHA256 hex lowercase — KHỚP TourKitHrm.Common.Security.HmacHelper.Sign và CRM SsoController.
    private static string HmacHex(string body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body ?? ""))).ToLowerInvariant();
    }

    private static bool VerifyHmac(string body, string sign, string secret) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HmacHex(body, secret)), Encoding.ASCII.GetBytes(sign));
}
