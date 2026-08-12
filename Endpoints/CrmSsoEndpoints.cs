using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// SSO HAI CHIỀU giữa Trav-ai và CRM (tourkit web) — HMAC-SHA256, ZERO password.
/// Chung secret "CrmSso:Secret" (⇔ AppSettings "CrmSsoSecret" bên CRM) và chung hàm HmacHex.
///
/// CHIỀU 1 — Trav-ai → CRM (proxy PHÁT, CRM NHẬN ở PublicAPI/SsoController.cs):
///   POST /api/v1/crm-sso-ticket → ký danh tính phiên hiện tại → CRM /api/sso/register-code → { url }.
///
/// CHIỀU 2 — CRM → Trav-ai (CRM PHÁT ở SsoController.TravAiGo, proxy NHẬN):
///   CRM 302 tới {proxy}/sso?t=&lt;vé&gt; — KHÔNG có vòng server-to-server nào.
///   GET /sso — verify vé → tra phiên → Set-Cookie tk_sso (30s) → 302 tiếp về đích.
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

        // ─── CHIỀU 2: GET /sso?t=<vé> — CRM 302 tới đây, ta 302 tiếp về đích ───
        // Vé tự chứng minh danh tính (payload + HMAC) nên KHÔNG cần đăng ký trước: bỏ được cả vòng
        // server-to-server lẫn kho code 1-lần (thứ vốn không chạy đúng khi proxy scale nhiều instance).
        // Hạn dùng nằm trong chính vé qua `iat`.
        //
        // Trao phiên bằng COOKIE ngắn hạn rồi 302, KHÔNG nhét lên URL đích: vé và sessionId chỉ tồn tại
        // trong các nhịp 302 → không thành URL của trang được render, không vào history/access log.
        // SPA nhặt cookie lúc boot (core/auth.jsx adoptSso) rồi để refresh() lấy tên + quyền.
        // MapGet tường minh thắng app.MapFallback(ServeIndex) nên không bị SPA nuốt.
        routes.MapGet("/sso", async (HttpContext ctx, TkSessionStore sessions, IConfiguration cfg, string? t) =>
        {
            var secret = cfg["CrmSso:Secret"];
            if (!string.IsNullOrEmpty(secret) && secret.StartsWith("ENC:", StringComparison.Ordinal))
                secret = Crypton.Decrypt(secret.Substring(4));
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
                return Results.Json(new { error = "SSO chưa cấu hình (CrmSso:Secret)" }, statusCode: 500);

            // Vé = "<payload-base64url>.<hmac>". CRM ký trên CHÍNH chuỗi base64url nên verify đúng thứ
            // nhận được, không phụ thuộc hai bên serialize JSON giống nhau tới từng dấu cách.
            var dot = t?.LastIndexOf('.') ?? -1;
            if (dot <= 0 || !VerifyHmac(t!.Substring(0, dot), t.Substring(dot + 1), secret))
                return Results.Redirect("/");

            string tenant, username, next;
            long iat;
            try
            {
                var root = JsonDocument.Parse(FromBase64Url(t.Substring(0, dot))).RootElement;
                tenant = root.GetProperty("tenant").GetString() ?? "";
                username = root.GetProperty("username").GetString() ?? "";
                next = root.TryGetProperty("redirect", out var r) ? r.GetString() ?? "" : "";
                iat = root.TryGetProperty("iat", out var i) && i.TryGetInt64(out var n) ? n : 0;
            }
            catch { return Results.Redirect("/"); }

            if (tenant.Length == 0 || username.Length == 0) return Results.Redirect("/");

            // Chữ ký HMAC không tự hết hạn — `iat` chính là hạn dùng của vé. 60s đủ rộng cho lệch giờ
            // nhẹ giữa 2 máy mà vẫn rất hẹp so với thời gian một URL nằm lại đâu đó.
            if (iat <= 0 || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(iat)).Duration() > TimeSpan.FromSeconds(60))
                return Results.Redirect("/");

            // `redirect` nằm trong payload ĐÃ KÝ nên client không sửa được; vẫn chặn lần nữa phòng CRM
            // bị sửa. "//host" là URL tuyệt đối protocol-relative.
            if (next.Length == 0 || !next.StartsWith('/') || next.StartsWith("//")) next = "/";

            var s = await sessions.FindByUserAsync(tenant, username, ctx.RequestAborted);
            if (s == null)
            {
                // Chưa có phiên Trav-ai cho người vừa SSO → gửi danh tính qua cookie để LoginGate prefill.
                // Vẫn 302 về ĐÍCH chứ KHÔNG về "/": app.jsx render LandingPage cho "/" TRƯỚC cả cổng
                // đăng nhập, nên về "/" thì người dùng chỉ thấy trang giới thiệu, không hề được hỏi
                // đăng nhập. Về đúng đích thì cổng bật lên, gõ mật khẩu xong là vào thẳng trang cần tới.
                SetShortCookie(ctx, "tk_sso_hint", JsonSerializer.Serialize(new { tenantId = tenant, username }));
                return Results.Redirect(next);
            }

            // Phiên cũ có thể chưa nạp Permissions → nạp trước khi vào app, tránh ẩn oan nút/sidebar.
            await sessions.EnsurePermissionsAsync(s.Id, ctx.RequestAborted);
            SetShortCookie(ctx, "tk_sso", s.Id);
            return Results.Redirect(next);
        });
    }

    /// Cookie bàn giao SSO: sống 30 giây, SPA đọc xong là xoá ngay. KHÔNG HttpOnly vì JS phải đọc được
    /// (SPA giữ phiên ở localStorage, không dùng cookie để gọi API) — nhưng cũng không mở thêm rủi ro
    /// nào so với hiện trạng, vì sessionId vốn đã nằm sẵn trong localStorage.
    private static void SetShortCookie(HttpContext ctx, string name, string value)
    {
        // Response mang Set-Cookie thì TUYỆT ĐỐI không được cache lại, nếu không lần điều hướng sau
        ctx.Response.Cookies.Append(name, value, new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(30),
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
        });
    }

    /// Base64url ("-_" thay "+/", bỏ '=' đệm) → chuỗi gốc. Khớp SsoController.Base64Url bên CRM.
    private static string FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=')));
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
