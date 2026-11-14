using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Services.Mcp.OAuth;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Máy chủ cấp quyền OAuth 2.1 cho <c>/mcp</c> — phần HTTP thuần, luật nằm ở <see cref="McpOAuthService"/>:
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-protected-resource</c> (+ <c>/mcp</c>) — RFC 9728.</item>
/// <item><c>GET /.well-known/oauth-authorization-server</c> và <c>/.well-known/openid-configuration</c> — RFC 8414 (cùng nội dung).</item>
/// <item><c>POST /oauth/register</c> (JSON) — đăng ký client động RFC 7591, chỉ client công khai.</item>
/// <item><c>POST /oauth/token</c> (<c>application/x-www-form-urlencoded</c>) — đổi mã + PKCE, xoay refresh.</item>
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

    /// <summary>Các tiền tố phải trả 404 JSON khi tính năng tắt (không để rơi xuống SPA fallback).</summary>
    public static readonly string[] Prefixes = { ProtectedResourcePath, AuthorizationServerPath, OpenIdConfigurationPath, OAuthPrefix };

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
