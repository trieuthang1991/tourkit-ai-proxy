using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Mcp.OAuth;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Máy chủ cấp quyền OAuth 2.1 cho <c>/mcp</c> — phần HTTP thuần, luật nằm ở <see cref="McpOAuthService"/>:
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-protected-resource</c> (+ <c>/mcp</c>) — RFC 9728.</item>
/// <item><c>GET /.well-known/oauth-authorization-server</c> và <c>/.well-known/openid-configuration</c> — RFC 8414 (cùng nội dung).</item>
/// <item><c>POST /oauth/register</c> (JSON) — đăng ký client động RFC 7591, chỉ client công khai.</item>
/// <item><c>POST /oauth/token</c> (<c>application/x-www-form-urlencoded</c>) — đổi mã + PKCE, xoay refresh.</item>
/// <item><c>GET /oauth/authorize</c> — trang đồng ý (SPA; map ở <c>StaticFilesSetup</c> vì cần <c>ServeIndex</c>).
/// Trang gọi <c>GET /api/v1/mcp/oauth/authorize-info</c> (không cần phiên) rồi <c>POST .../approve|deny</c> (cần phiên, header
/// <c>X-Session-Id</c>); hai lệnh POST nhận lại NGUYÊN query gốc và kiểm lại toàn bộ.</item>
/// </list>
/// Chỉ map khi <c>FeatureFlags.McpOAuth</c> có hiệu lực (xem <c>EndpointRegistration.MapMcp</c>).
/// Địa chỉ quảng cáo lấy từ <see cref="McpOAuthOptions"/> (cấu hình), không bao giờ từ header Host.
/// </summary>
public static class McpOAuthEndpoints
{
    public const string ProtectedResourcePath = "/.well-known/oauth-protected-resource";
    public const string AuthorizationServerPath = "/.well-known/oauth-authorization-server";
    public const string OpenIdConfigurationPath = "/.well-known/openid-configuration";
    public const string OAuthPrefix = "/oauth";
    public const string RegisterPath = OAuthPrefix + "/register";
    public const string TokenPath = OAuthPrefix + "/token";
    public const string AuthorizePath = OAuthPrefix + "/authorize";
    public const string ApiPrefix = "/api/v1/mcp/oauth";
    public const string AuthorizeInfoPath = ApiPrefix + "/authorize-info";
    public const string ApprovePath = ApiPrefix + "/approve";
    public const string DenyPath = ApiPrefix + "/deny";

    /// <summary>Các tiền tố phải trả 404 JSON khi tính năng tắt (không để rơi xuống SPA fallback).</summary>
    public static readonly string[] Prefixes = { ProtectedResourcePath, AuthorizationServerPath, OpenIdConfigurationPath, OAuthPrefix, ApiPrefix };

    // Thân yêu cầu của hai endpoint này chỉ vài trăm byte; chặn sớm để khỏi đọc bừa thân lớn.
    private const int MaxBodyBytes = 16 * 1024;
    private const string FormType = "application/x-www-form-urlencoded";

    public static void MapMcpOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ProtectedResourcePath, (HttpContext ctx, McpOAuthOptions o) => Metadata(ctx, OAuthMetadata.ProtectedResource(o.BaseUrl)));
        routes.MapGet(ProtectedResourcePath + "/mcp", (HttpContext ctx, McpOAuthOptions o) => Metadata(ctx, OAuthMetadata.ProtectedResource(o.BaseUrl)));
        routes.MapGet(AuthorizationServerPath, (HttpContext ctx, McpOAuthOptions o) => Metadata(ctx, OAuthMetadata.AuthorizationServer(o.BaseUrl)));
        routes.MapGet(OpenIdConfigurationPath, (HttpContext ctx, McpOAuthOptions o) => Metadata(ctx, OAuthMetadata.AuthorizationServer(o.BaseUrl)));

        routes.MapPost(RegisterPath, RegisterAsync);

        // Trang đồng ý. authorize-info KHÔNG cần phiên (trang phải biết lỗi/redirect trước cả khi đăng nhập);
        // approve/deny cần phiên TourKit qua X-Session-Id (KHÔNG cookie → không dính CSRF).
        routes.MapGet(AuthorizeInfoPath, AuthorizeInfoAsync);
        routes.MapPost(ApprovePath, (HttpContext ctx, TkSessionStore sessions, McpOAuthService svc, CancellationToken ct)
            => DecideAsync(ctx, SessionAuth.Read(ctx, sessions), svc, deny: false, ct));
        routes.MapPost(DenyPath, (HttpContext ctx, TkSessionStore sessions, McpOAuthService svc, CancellationToken ct)
            => DecideAsync(ctx, SessionAuth.Read(ctx, sessions), svc, deny: true, ct));
        routes.MapPost(TokenPath, async (HttpContext ctx, McpOAuthService svc, CancellationToken ct) =>
        {
            NoStore(ctx);
            if (!IsFormUrlEncoded(ctx.Request))
                return Json(400, OAuthError("invalid_request", "Chi nhan Content-Type: application/x-www-form-urlencoded"));
            if (ctx.Request.ContentLength is > MaxBodyBytes)
                return Json(400, OAuthError("invalid_request", "Noi dung qua lon"));
            IFormCollection form;
            try
            {
                // Giới hạn riêng: cấu hình chung của app cho phép thân rất lớn (tải file), không áp cho đây.
                var reader = new FormFeature(ctx.Request, new FormOptions
                {
                    BufferBodyLengthLimit = MaxBodyBytes, ValueLengthLimit = 4096, KeyLengthLimit = 64, ValueCountLimit = 32,
                });
                form = await reader.ReadFormAsync(ct);
            }
            catch (InvalidDataException) { return Json(400, OAuthError("invalid_request", "Form khong hop le")); }
            catch (IOException) { return Json(400, OAuthError("invalid_request", "Form khong hop le")); }
            var r = await svc.TokenAsync(form, ct);
            return Json(r.Status, r.Body);
        });
    }

    internal static async Task<IResult> RegisterAsync(HttpContext ctx, McpOAuthService svc, CancellationToken ct)
    {
        NoStore(ctx);
        if (!ctx.Request.HasJsonContentType())
            return Json(400, OAuthError("invalid_client_metadata", "Can gui JSON (Content-Type: application/json)"));
        string? json;
        try { json = await ReadBodyAsync(ctx.Request, ct); }
        // Client ngắt giữa chừng / Kestrel từ chối thân (BadHttpRequestException là một IOException):
        // lỗi của request, không phải của máy chủ → 400 như token endpoint, không để thành 500.
        catch (BadHttpRequestException) { return Json(400, OAuthError("invalid_client_metadata", "Khong doc duoc noi dung gui len")); }
        catch (IOException) { return Json(400, OAuthError("invalid_client_metadata", "Khong doc duoc noi dung gui len")); }
        if (json == null) return Json(413, OAuthError("invalid_client_metadata", "Noi dung qua lon"));
        // IP lấy từ kết nối — đã qua UseForwardedHeaders trong Program.cs, không tự đọc header ở đây.
        var r = await svc.RegisterAsync(json, ctx.Connection.RemoteIpAddress?.ToString(), ct);
        return Json(r.Status, r.Body);
    }

    private const string InvalidRequestVi = "Yêu cầu kết nối không hợp lệ — hãy bắt đầu lại từ ứng dụng AI.";

    /// <summary>
    /// Trang đồng ý hỏi: query này nên làm gì? <c>consent</c> (hiện thẻ đồng ý), <c>error</c> (hiện lỗi, KHÔNG chuyển hướng —
    /// client/redirect không tin được) hoặc <c>redirect</c> (client + redirect đúng, tham số khác sai → báo lỗi về client).
    /// Không cần phiên và không ghi gì. Mọi chuỗi trả về là dữ liệu thuần — trang không được render thành HTML.
    /// </summary>
    internal static async Task<IResult> AuthorizeInfoAsync(HttpContext ctx, McpOAuthService svc, CancellationToken ct)
    {
        NoStore(ctx);
        var v = await svc.ValidateAuthorizeRequestAsync(ctx.Request.Query, ct);
        switch (v.Outcome)
        {
            case McpAuthorizeOutcome.Valid when v.Request is { } r:
                return Json(200, new JsonObject
                {
                    ["kind"] = "consent",
                    ["clientName"] = r.ClientName,
                    // Chỉ logo https: http/data:/javascript: bị bỏ — thẻ đồng ý không được là đường tải nội dung lạ.
                    ["logoUri"] = HttpsOrNull(r.LogoUri),
                    ["redirectHost"] = HostOf(r.RedirectUri),
                    ["scope"] = r.Scope,
                });
            case McpAuthorizeOutcome.RedirectError when !string.IsNullOrEmpty(v.RedirectUrl):
                return Json(200, new JsonObject { ["kind"] = "redirect", ["url"] = v.RedirectUrl });
            default:
                return Json(200, new JsonObject { ["kind"] = "error", ["message"] = v.Error ?? InvalidRequestVi });
        }
    }

    /// <summary>
    /// "Cho phép" / "Từ chối". Thân JSON <c>{"query":"?client_id=…&amp;redirect_uri=…"}</c> là NGUYÊN query gốc của trang
    /// (không phải đối tượng đã kiểm): ta kiểm lại từ đầu, vì giữa lúc hiện trang và lúc bấm nút ai cũng sửa được request.
    /// Không phiên → 401. Chỉ trả URL chuyển hướng khi kiểm lại vẫn <c>Valid</c>; mọi trường hợp khác 400 và không URL.
    /// </summary>
    internal static async Task<IResult> DecideAsync(HttpContext ctx, SessionAuth.Ctx? session, McpOAuthService svc, bool deny, CancellationToken ct)
    {
        NoStore(ctx);
        if (session == null) return SessionAuth.Unauthorized();
        if (!ctx.Request.HasJsonContentType()) return Json(400, new JsonObject { ["error"] = "Cần gửi JSON (Content-Type: application/json)" });

        string? body;
        try { body = await ReadBodyAsync(ctx.Request, ct); }
        catch (BadHttpRequestException) { return Json(400, new JsonObject { ["error"] = "Không đọc được nội dung gửi lên" }); }
        catch (IOException) { return Json(400, new JsonObject { ["error"] = "Không đọc được nội dung gửi lên" }); }
        if (body == null) return Json(413, new JsonObject { ["error"] = "Nội dung quá lớn" });

        string? raw;
        try { raw = (JsonNode.Parse(body) as JsonObject)?["query"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { return Json(400, new JsonObject { ["error"] = InvalidRequestVi }); }
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192) return Json(400, new JsonObject { ["error"] = InvalidRequestVi });

        var query = QueryHelpers.ParseNullableQuery(raw) ?? new Dictionary<string, StringValues>();
        // Kiểm một lần ở đây để loại sớm; IssueCodeAsync/DenyAsync còn kiểm LẠI nữa (không tin kết quả này).
        var v = await svc.ValidateAuthorizeRequestAsync(query, ct);
        if (v.Outcome != McpAuthorizeOutcome.Valid || v.Request == null)
            return Json(400, new JsonObject { ["error"] = v.Error ?? InvalidRequestVi });

        var res = deny
            ? await svc.DenyAsync(v.Request, ct)
            : await svc.IssueCodeAsync(v.Request, new McpOAuthApprover(session.SessionId, session.TenantId, session.Username), ct);
        return string.IsNullOrEmpty(res.RedirectUrl)
            ? Json(400, new JsonObject { ["error"] = res.Error ?? InvalidRequestVi })
            : Json(200, new JsonObject { ["redirectUrl"] = res.RedirectUrl });
    }

    private static string? HttpsOrNull(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url!.Length <= 2000 ? u.AbsoluteUri : null;

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    private static IResult Metadata(HttpContext ctx, JsonObject body)
    {
        // Tài liệu khám phá ít đổi — cho đệm vài phút; đổi PublicBaseUrl thì chậm nhất 5 phút client thấy.
        ctx.Response.Headers.CacheControl = "public, max-age=300";
        return Json(200, body);
    }

    private static void NoStore(HttpContext ctx)
    {
        // RFC 6749 §5.1: phản hồi chứa token không được đệm ở bất kỳ đâu.
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
    }

    private static bool IsFormUrlEncoded(HttpRequest req)
        => MediaTypeHeaderValue.TryParse(req.ContentType, out var mt)
           && mt.MediaType.Equals(FormType, StringComparison.OrdinalIgnoreCase);

    private static JsonObject OAuthError(string error, string description) => new() { ["error"] = error, ["error_description"] = description };

    private static IResult Json(int status, JsonObject body)
        => Results.Text(body.ToJsonString(), "application/json; charset=utf-8", Encoding.UTF8, status);

    /// <summary>Đọc thân tối đa <see cref="MaxBodyBytes"/>; vượt thì null (dừng đọc, không tải nốt).</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is > MaxBodyBytes) return null;
        var buf = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await req.Body.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total > MaxBodyBytes ? null : Encoding.UTF8.GetString(buf, 0, total);
    }
}
