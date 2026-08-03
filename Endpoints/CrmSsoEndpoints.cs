using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// SSO Trav-ai → CRM (tourkit web) — MIRROR luồng SSO tourkit-web → HRM (HMAC + code 1-lần, ZERO password).
/// Proxy = bên PHÁT: ký HMAC-SHA256 body danh tính (không password) → POST {crm}/api/sso/register-code
/// (header X-Sign) → CRM verify, sinh code 1-lần lưu Redis TTL 60s, trả exchangeUrl → browser mở
/// /api/sso/exchange?code=X → CRM đổi code (one-time GETDEL) → set Forms-auth cookie ĐÚNG account →
/// redirect. Mật khẩu KHÔNG rời
/// server; trên URL chỉ có 1 code random dùng-một-lần. Khớp TourKitHrm.Common.Security.HmacHelper.
public static class CrmSsoEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void MapCrmSsoEndpoints(this IEndpointRouteBuilder routes)
    {
        // POST /api/v1/crm-sso-ticket → { url }  (require X-Session-Id). Frontend mở url ở tab mới.
        routes.MapPost("/api/v1/crm-sso-ticket", async (HttpContext ctx, TkSessionStore sessions, IConfiguration cfg) =>
        {
            var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                      ?? ctx.Request.Query["sessionId"].FirstOrDefault();
            var s = sessions.Get(sid);
            if (s == null) return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

            var secret = cfg["CrmSso:Secret"];
            // Hỗ trợ ENC: (config chứa ciphertext Crypton → giải ra secret thật). Giống Redis conn string.
            if (!string.IsNullOrEmpty(secret) && secret.StartsWith("ENC:", StringComparison.Ordinal))
                secret = Crypton.Decrypt(secret.Substring(4));
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
                return Results.Json(new { error = "SSO CRM chưa cấu hình (CrmSso:Secret)" }, statusCode: 500);

            // Đích redirect trong CRM — CHỈ path nội bộ "/..." (chống open-redirect), mặc định /customer-data.
            var redirect = ctx.Request.Query["redirect"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(redirect) || !redirect.StartsWith('/') || redirect.StartsWith("//"))
                redirect = "/customer-data";

            var t = (s.TenantId ?? "").Trim();
            if (string.IsNullOrEmpty(t)) return Results.Json(new { error = "Phiên thiếu tenant" }, statusCode: 400);

            // Host CRM: mặc định {tenant}.tourkit.vn (prod, khớp crmUrl phía JS — có '.' = host đầy đủ).
            // LOCAL DEV: override qua CrmSso:BaseUrl (vd "https://localhost:44300" hoặc "http://localhost:5001")
            // để proxy trỏ CRM chạy tại máy thay vì domain thật. Trống ở prod = giữ nguyên hành vi cũ.
            string scheme = "https";
            string host;
            var baseUrl = cfg["CrmSso:BaseUrl"];
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
            // `scheme` để CRM dựng exchangeUrl đúng http/https khi test local (prod bỏ qua = https).
            var body = JsonSerializer.Serialize(new
            {
                tenant = t,
                username = s.Username,
                host,
                scheme,
                redirect,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            var sign = HmacHex(body, secret!);

            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, $"{scheme}://{host}/api/sso/register-code")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                msg.Headers.Add("X-Sign", sign);
                using var resp = await Http.SendAsync(msg, ctx.RequestAborted);
                var respBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                {
                    // Lộ message lỗi thật của CRM (body { error } từ SsoController) để chẩn đoán, không nuốt.
                    var snippet = (respBody ?? "").Trim();
                    if (snippet.Length > 400) snippet = snippet.Substring(0, 400);
                    return Results.Json(new { error = $"CRM từ chối SSO (HTTP {(int)resp.StatusCode}) @ {scheme}://{host} — {snippet}" }, statusCode: 502);
                }
                using var doc = JsonDocument.Parse(respBody);
                if (!doc.RootElement.TryGetProperty("exchangeUrl", out var ex) || ex.ValueKind != JsonValueKind.String)
                    return Results.Json(new { error = "CRM trả response không hợp lệ" }, statusCode: 502);
                return Results.Json(new { url = ex.GetString() });
            }
            catch (Exception e)
            {
                return Results.Json(new { error = "Không kết nối được CRM: " + e.Message }, statusCode: 502);
            }
        }).DisableAntiforgery();
    }

    // HMAC-SHA256 hex lowercase — KHỚP TourKitHrm.Common.Security.HmacHelper.Sign (chuẩn SSO của bản web).
    private static string HmacHex(string body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
