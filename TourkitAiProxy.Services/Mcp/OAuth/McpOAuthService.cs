using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;

namespace TourkitAiProxy.Services.Mcp.OAuth;

/// <summary>
/// Kết quả kiểm tham số <c>/oauth/authorize</c>:
/// <list type="bullet">
/// <item><see cref="McpAuthorizeOutcome.Valid"/> — hiện trang đồng ý với <see cref="McpAuthorizeValidation.Request"/>.</item>
/// <item><see cref="McpAuthorizeOutcome.ShowError"/> — client hoặc redirect_uri không tin được: hiện
/// <see cref="McpAuthorizeValidation.Error"/> trên trang, TUYỆT ĐỐI không redirect (RFC 6749 §4.1.2.1 —
/// redirect lỗi về URI lạ là biến máy chủ ta thành cầu chuyển hướng).</item>
/// <item><see cref="McpAuthorizeOutcome.RedirectError"/> — client + redirect đã đúng, tham số khác sai:
/// chuyển trình duyệt về <see cref="McpAuthorizeValidation.RedirectUrl"/> (có error, state, iss).</item>
/// </list>
/// </summary>
public enum McpAuthorizeOutcome { Valid, ShowError, RedirectError }

/// <summary>Yêu cầu authorize đã kiểm. Mọi trường đã chuẩn hoá (resource, scope).</summary>
public sealed record McpAuthorizeRequest(string ClientId, string ClientName, string? LogoUri, string RedirectUri, string? State,
    string CodeChallenge, string Resource, string Scope);

public sealed record McpAuthorizeValidation(McpAuthorizeOutcome Outcome, McpAuthorizeRequest? Request, string? Error, string? RedirectUrl);

/// <summary>Người bấm "Cho phép": phiên TRAV-AI của họ (lấy từ <c>X-Session-Id</c>).</summary>
public sealed record McpOAuthApprover(string SessionId, string TenantId, string Username);

/// <summary>Có <see cref="RedirectUrl"/> là thành công; không thì <see cref="Error"/> tiếng Việt để hiện trên trang.</summary>
public sealed record McpIssueCodeResult(string? RedirectUrl, string? Error);

/// <summary>Phản hồi HTTP cho <c>/oauth/token</c> và <c>/oauth/register</c>: mã trạng thái + thân JSON.</summary>
public sealed record McpOAuthResponse(int Status, JsonObject Body);

/// <summary>
/// Máy chủ cấp quyền OAuth 2.1 cho <c>/mcp</c> (Claude.ai, ChatGPT, Claude Code, MCP Inspector).
/// Tầng Endpoints chỉ đọc request/ghi response; mọi luật nằm ở đây để test được bằng kho giả.
///
/// <para><b>Mã uỷ quyền</b>: một lần, sống 5 phút, gắn client_id + redirect_uri + code_challenge + resource
/// + scope + phiên TourKit của người đồng ý. Đổi mã sai BẤT KỲ điều kiện nào → <c>invalid_grant</c>, và
/// mã đã bị đốt (đoán sai verifier một lần là mất mã — không cho dò).</para>
///
/// <para><b>Refresh xoay vòng</b>: mỗi lần dùng cấp refresh mới; ai trình lại refresh CŨ nghĩa là token đã
/// lọt sang người thứ hai → thu hồi cả grant (OAuth 2.1 §4.3.1). Grant mà phiên TourKit đã hết cũng thu hồi:
/// mọi lời gọi CRM chạy bằng phiên đó, phiên mất thì token vô nghĩa.</para>
///
/// <para><b>error_description</b> viết tiếng Việt KHÔNG DẤU: RFC 6749 §5.2 chỉ cho ký tự ASCII in được
/// (trừ <c>"</c> và <c>\</c>), client chặt chẽ sẽ vứt cả phản hồi nếu có chữ có dấu.</para>
/// </summary>
public sealed class McpOAuthService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    public const int MaxRegistrationsPerHourPerIp = 20;

    public const string ScopeRead = "tourkit.read";
    public const string ScopeOffline = "offline_access";
    private static readonly string[] SupportedScopes = { ScopeRead, ScopeOffline };

    private const string DcrPrefix = "tkdcr_";
    private const int MaxClientId = 200;
    private const int MaxRedirect = 2000;
    private const int MaxState = 1024;
    private const int MaxClientName = 100;

    private readonly IMcpOAuthStore _store;
    private readonly IClientMetadataFetcher _fetcher;
    private readonly Func<string, McpSessionOwner?> _findSession;
    private readonly McpOAuthOptions _opts;
    private readonly ILogger<McpOAuthService> _log;
    private readonly TimeProvider _clock;

    /// <param name="findSession">Tra chủ phiên TourKit; null = phiên đã hết. Hàm thay vì <c>TkSessionStore</c> để test không cần CSDL.</param>
    public McpOAuthService(IMcpOAuthStore store, IClientMetadataFetcher fetcher, Func<string, McpSessionOwner?> findSession,
        McpOAuthOptions options, ILogger<McpOAuthService> log, TimeProvider? clock = null)
    {
        _store = store; _fetcher = fetcher; _findSession = findSession; _opts = options; _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ═══ Authorize ═════════════════════════════════════════════════════════

    public async Task<McpAuthorizeValidation> ValidateAuthorizeRequestAsync(IEnumerable<KeyValuePair<string, StringValues>> query, CancellationToken ct)
    {
        var p = new Params(query);

        // ── Bước 1: client + redirect. Sai ở đây thì KHÔNG redirect — chưa biết redirect nào đáng tin.
        if (p.Duplicated("client_id") || p.Duplicated("redirect_uri"))
            return Show("Yêu cầu kết nối không hợp lệ (tham số bị lặp).");
        var clientId = p["client_id"];
        if (string.IsNullOrEmpty(clientId) || clientId.Length > MaxClientId)
            return Show("Thiếu hoặc sai mã ứng dụng (client_id).");

        ClientMetadata? client;
        try { client = await ResolveClientAsync(clientId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Không tra được client OAuth {ClientId}", clientId);
            return Show("Chưa kiểm được ứng dụng lúc này — thử lại sau ít phút.");
        }
        if (client == null) return Show("Ứng dụng này chưa được đăng ký hoặc không được phép kết nối với TourKit.");

        var redirect = p["redirect_uri"];
        if (string.IsNullOrEmpty(redirect) || redirect.Length > MaxRedirect
            || !OAuthRules.IsAllowedRedirectUri(redirect, _opts.ExtraRedirectUris)
            || !client.RedirectUris.Any(r => OAuthRules.RedirectMatches(r, redirect)))
            return Show("Địa chỉ quay về (redirect_uri) không khớp với ứng dụng — đã chặn để bảo vệ tài khoản của bạn.");

        // ── Bước 2: tham số còn lại. Client + redirect đã tin được → trả lỗi về cho client qua redirect.
        var state = p.Duplicated("state") ? null : p["state"];
        McpAuthorizeValidation Back(string error, string desc) => new(McpAuthorizeOutcome.RedirectError, null, null,
            AppendQuery(redirect, ("error", error), ("error_description", desc), ("state", state), ("iss", _opts.Issuer)));

        foreach (var k in new[] { "response_type", "state", "code_challenge", "code_challenge_method", "resource", "scope" })
            if (p.Duplicated(k)) return Back("invalid_request", $"Tham so {k} bi lap");
        if (state is { Length: > MaxState }) return Back("invalid_request", "state qua dai");

        var responseType = p["response_type"];
        if (string.IsNullOrEmpty(responseType)) return Back("invalid_request", "Thieu response_type");
        if (responseType != "code") return Back("unsupported_response_type", "Chi ho tro response_type=code");

        // PKCE bắt buộc và CHỈ S256. Thiếu method nghĩa là "plain" (RFC 7636 §4.3) → cũng từ chối.
        var challenge = p["code_challenge"];
        if (!IsS256Challenge(challenge)) return Back("invalid_request", "Thieu hoac sai code_challenge (PKCE S256)");
        if (p["code_challenge_method"] != "S256") return Back("invalid_request", "Chi ho tro code_challenge_method=S256");

        var resource = _opts.Resource;
        if (p["resource"] is { Length: > 0 } rawResource && OAuthRules.NormalizeResource(rawResource) != _opts.Resource)
            return Back("invalid_target", "resource khong phai may chu MCP nay");

        var scope = NormalizeScope(p["scope"]);
        if (scope == null) return Back("invalid_scope", "Chi ho tro scope tourkit.read va offline_access");

        return new McpAuthorizeValidation(McpAuthorizeOutcome.Valid,
            new McpAuthorizeRequest(client.ClientId, ClientNameOf(client), client.LogoUri, redirect, state, challenge!, resource, scope),
            null, null);

        static McpAuthorizeValidation Show(string error) => new(McpAuthorizeOutcome.ShowError, null, error, null);
    }

    /// <summary>
    /// Người dùng bấm "Cho phép". KIỂM LẠI toàn bộ tham số (không tin dữ liệu đã kiểm lúc hiện trang —
    /// trang và lệnh approve là hai request, giữa chúng ai cũng sửa được) rồi mới cấp mã.
    /// </summary>
    public async Task<McpIssueCodeResult> IssueCodeAsync(McpAuthorizeRequest request, McpOAuthApprover approver, CancellationToken ct)
    {
        var v = await ValidateAuthorizeRequestAsync(ToQuery(request), ct);
        if (v.Outcome != McpAuthorizeOutcome.Valid || v.Request == null)
            return new McpIssueCodeResult(null, v.Error ?? "Yêu cầu kết nối không hợp lệ — hãy bắt đầu lại từ ứng dụng AI.");
        var r = v.Request;

        if (!SessionAlive(approver.SessionId, approver.TenantId, approver.Username))
            return new McpIssueCodeResult(null, "Phiên đăng nhập đã hết — đăng nhập lại rồi thử kết nối lại.");

        var code = OAuthRules.NewAuthCode();
        await _store.SaveCodeAsync(new McpOAuthCode(OAuthRules.Hash(code), r.ClientId, r.RedirectUri, r.CodeChallenge, r.Resource, r.Scope,
            approver.TenantId, approver.Username, approver.SessionId, Now + CodeLifetime, null), ct);
        _log.LogInformation("OAuth MCP: {User}@{Tenant} cho phép ứng dụng {Client} ({ClientId})", approver.Username, approver.TenantId, r.ClientName, r.ClientId);
        return new McpIssueCodeResult(AppendQuery(r.RedirectUri, ("code", code), ("state", r.State), ("iss", _opts.Issuer)), null);
    }

    /// <summary>Người dùng bấm "Từ chối" → báo client <c>access_denied</c>. Chỉ gọi với yêu cầu đã kiểm (Valid).</summary>
    public string Deny(McpAuthorizeRequest request)
        => AppendQuery(request.RedirectUri, ("error", "access_denied"), ("error_description", "Nguoi dung tu choi ket noi"),
            ("state", request.State), ("iss", _opts.Issuer));

    // ═══ Token ═════════════════════════════════════════════════════════════

    public async Task<McpOAuthResponse> TokenAsync(IEnumerable<KeyValuePair<string, StringValues>> form, CancellationToken ct)
    {
        var p = new Params(form);
        foreach (var k in p.Keys)
            if (p.Duplicated(k)) return Error("invalid_request", $"Tham so {k} bi lap");

        try
        {
            return p["grant_type"] switch
            {
                null or "" => Error("invalid_request", "Thieu grant_type"),
                "authorization_code" => await ExchangeCodeAsync(p, ct),
                "refresh_token" => await RefreshAsync(p, ct),
                _ => Error("unsupported_grant_type", "Chi ho tro authorization_code va refresh_token"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // CSDL/phiên lỗi: đóng cửa, không cấp gì. 503 để client thử lại thay vì bắt người dùng đăng nhập lại.
            _log.LogError(ex, "OAuth MCP: lỗi khi cấp token");
            return new McpOAuthResponse(503, Body("temporarily_unavailable", "May chu tam thoi khong cap duoc token, thu lai sau"));
        }
    }

    private async Task<McpOAuthResponse> ExchangeCodeAsync(Params p, CancellationToken ct)
    {
        string? code = p["code"], verifier = p["code_verifier"], redirect = p["redirect_uri"], clientId = p["client_id"];
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(redirect) || string.IsNullOrEmpty(clientId))
            return Error("invalid_request", "Can code, code_verifier, redirect_uri va client_id");

        // Client lạ bị loại TRƯỚC khi đụng mã — không để ai đốt mã của người khác bằng client_id bịa.
        if (!await ClientExistsAsync(clientId, ct)) return Error("invalid_client", "client_id khong ton tai");
        if (!IsToken(code, "tkoac_")) return Error("invalid_grant", "Ma uy quyen khong hop le");

        // Đốt mã NGAY (một câu UPDATE nguyên tử): sai điều kiện nào ở dưới thì mã cũng đã chết.
        var c = await _store.ConsumeCodeAsync(OAuthRules.Hash(code), ct);
        if (c == null || c.ExpiresUtc <= Now) return Error("invalid_grant", "Ma uy quyen khong hop le, da dung hoac da het han");
        if (!string.Equals(c.ClientId, clientId, StringComparison.Ordinal))
        {
            _log.LogWarning("OAuth MCP: mã của {Bound} bị đem đổi ở client {ClientId}", c.ClientId, clientId);
            return Error("invalid_grant", "Ma uy quyen khong cap cho client nay");
        }
        if (!string.Equals(c.RedirectUri, redirect, StringComparison.Ordinal)) return Error("invalid_grant", "redirect_uri khong khop");
        if (!OAuthRules.VerifyPkceS256(verifier, c.CodeChallenge)) return Error("invalid_grant", "code_verifier sai");
        if (p["resource"] is { Length: > 0 } res && OAuthRules.NormalizeResource(res) != c.Resource)
            return Error("invalid_grant", "resource khong khop");
        if (!SessionAlive(c.SessionId, c.TenantId, c.Username)) return Error("invalid_grant", "Phien dang nhap TourKit da het");

        var client = await TryResolveClientAsync(clientId, ct);
        var (access, refresh, accessExp, refreshExp) = NewTokens();
        await _store.CreateGrantAsync(new McpOAuthNewGrant(c.ClientId, Truncate(client != null ? ClientNameOf(client) : c.ClientId, 200),
            c.TenantId, c.Username, c.SessionId, c.Resource, c.Scope,
            OAuthRules.Hash(access), accessExp, OAuthRules.Hash(refresh), refreshExp), ct);
        return Tokens(access, refresh, c.Scope);
    }

    private async Task<McpOAuthResponse> RefreshAsync(Params p, CancellationToken ct)
    {
        string? refresh = p["refresh_token"], clientId = p["client_id"];
        if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(clientId))
            return Error("invalid_request", "Can refresh_token va client_id");
        if (!IsToken(refresh, "tkort_")) return Error("invalid_grant", "refresh_token khong hop le");

        // Kiểm những gì KHÔNG cần grant trước khi xoay — xoay rồi mới báo lỗi là làm chết refresh hợp lệ.
        if (p["resource"] is { Length: > 0 } res && OAuthRules.NormalizeResource(res) != _opts.Resource)
            return Error("invalid_grant", "resource khong khop");
        string[]? requested = null;
        if (p["scope"] is { Length: > 0 } rawScope)
        {
            requested = SplitScope(rawScope);
            if (requested.Any(s => !SupportedScopes.Contains(s))) return Error("invalid_scope", "Scope khong duoc ho tro");
        }

        var (access, newRefresh, accessExp, refreshExp) = NewTokens();
        var rot = await _store.RotateRefreshAsync(OAuthRules.Hash(refresh), OAuthRules.Hash(access), accessExp, OAuthRules.Hash(newRefresh), refreshExp, ct);
        switch (rot.Status)
        {
            case McpRefreshStatus.NotFound:
                return Error("invalid_grant", "refresh_token khong hop le hoac da het han");
            case McpRefreshStatus.Reused:
                var reused = rot.Grant!;
                await _store.RevokeGrantAsync(reused.Id, reused.TenantId, reused.Username, ct);
                _log.LogWarning("OAuth MCP: refresh cũ của grant {Id} ({Client}, {User}@{Tenant}) bị dùng lại — đã thu hồi cả grant",
                    reused.Id, reused.ClientId, reused.Username, reused.TenantId);
                return Error("invalid_grant", "refresh_token da dung roi - ket noi da bi thu hoi, hay ket noi lai");
        }

        var g = rot.Grant!;
        if (!string.Equals(g.ClientId, clientId, StringComparison.Ordinal))
        {
            await _store.RevokeGrantAsync(g.Id, g.TenantId, g.Username, ct);
            _log.LogWarning("OAuth MCP: refresh của grant {Id} ({Bound}) bị trình ở client {ClientId} — đã thu hồi", g.Id, g.ClientId, clientId);
            return Error("invalid_grant", "refresh_token khong cap cho client nay");
        }
        if (!SessionAlive(g.SessionId, g.TenantId, g.Username))
        {
            await _store.RevokeGrantAsync(g.Id, g.TenantId, g.Username, ct);
            return Error("invalid_grant", "Phien dang nhap TourKit da het - hay ket noi lai");
        }

        var granted = SplitScope(g.Scope);
        var scope = g.Scope;
        if (requested != null)
        {
            // Chỉ được thu hẹp. Phạm vi thu hẹp áp cho phản hồi này; grant giữ phạm vi gốc (kho chưa lưu thu hẹp).
            if (requested.Any(s => !granted.Contains(s))) return Error("invalid_scope", "Chi duoc thu hep scope da cap");
            scope = string.Join(' ', SupportedScopes.Where(requested.Contains));
        }
        return Tokens(access, newRefresh, scope);
    }

    // ═══ Đăng ký client động (RFC 7591) ════════════════════════════════════

    public async Task<McpOAuthResponse> RegisterAsync(string json, string? ip, CancellationToken ct)
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(ip) ? "?" : ip.Trim();
            if (await _store.CountClientsByIpSinceAsync(key, Now.AddHours(-1), ct) >= MaxRegistrationsPerHourPerIp)
                return new McpOAuthResponse(429, Body("too_many_requests", "Dang ky qua nhieu client tu dia chi nay, thu lai sau 1 gio"));

            var (doc, err) = ClientMetadataDocument.ParseDcrRequest(json, _opts.ExtraRedirectUris);
            if (doc == null) return Error(err ?? "invalid_client_metadata", "Thong tin client khong hop le");
            if (doc.RedirectUris.Any(u => u.Length > MaxRedirect)) return Error("invalid_redirect_uri", "redirect_uri qua dai");
            if (!OnlyAllowed(json, "grant_types", "authorization_code", "refresh_token")
                || !OnlyAllowed(json, "response_types", "code"))
                return Error("invalid_client_metadata", "Chi ho tro grant authorization_code/refresh_token va response_type code");

            var name = Truncate(string.IsNullOrWhiteSpace(doc.ClientName) ? "Ứng dụng không tên" : doc.ClientName.Trim(), MaxClientName);
            var clientId = DcrPrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await _store.CreateClientAsync(new McpOAuthClient(clientId, name, JsonSerializer.Serialize(doc.RedirectUris), Now, key), ct);
            _log.LogInformation("OAuth MCP: đăng ký client động {ClientId} '{Name}' từ {Ip}", clientId, name, key);

            var uris = new JsonArray();
            foreach (var u in doc.RedirectUris) uris.Add(u);
            return new McpOAuthResponse(201, new JsonObject
            {
                ["client_id"] = clientId,
                ["client_id_issued_at"] = _clock.GetUtcNow().ToUnixTimeSeconds(),
                ["client_name"] = name,
                ["redirect_uris"] = uris,
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "OAuth MCP: lỗi khi đăng ký client");
            return new McpOAuthResponse(503, Body("temporarily_unavailable", "May chu tam thoi khong dang ky duoc client, thu lai sau"));
        }
    }

    // ═══ Phụ trợ ═══════════════════════════════════════════════════════════

    /// <summary>URL https → CIMD (tải qua fetcher, đã chặn SSRF); còn lại → client DCR trong kho.</summary>
    private async Task<ClientMetadata?> ResolveClientAsync(string clientId, CancellationToken ct)
    {
        if (clientId.Contains("://", StringComparison.Ordinal))
            return _fetcher.CanFetch(clientId) ? await _fetcher.FetchAsync(clientId, ct) : null;
        if (!clientId.StartsWith(DcrPrefix, StringComparison.Ordinal)) return null;
        var row = await _store.GetClientAsync(clientId, ct);
        if (row == null) return null;
        var uris = JsonSerializer.Deserialize<string[]>(row.RedirectUrisJson) ?? Array.Empty<string>();
        return new ClientMetadata(row.ClientId, row.ClientName, uris, null);
    }

    private async Task<ClientMetadata?> TryResolveClientAsync(string clientId, CancellationToken ct)
    {
        try { return await ResolveClientAsync(clientId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Không tra được tên client {ClientId}", clientId); return null; }
    }

    /// <summary>Ở token endpoint chỉ cần biết client có thật không — KHÔNG tải CIMD (client đã được kiểm lúc authorize).</summary>
    private async Task<bool> ClientExistsAsync(string clientId, CancellationToken ct)
    {
        if (clientId.Length > MaxClientId) return false;
        if (clientId.Contains("://", StringComparison.Ordinal)) return _fetcher.CanFetch(clientId);
        return clientId.StartsWith(DcrPrefix, StringComparison.Ordinal) && await _store.GetClientAsync(clientId, ct) != null;
    }

    private bool SessionAlive(string sessionId, string tenantId, string username)
        => _findSession(sessionId) is { } o
           && string.Equals(o.TenantId, tenantId, StringComparison.Ordinal)
           && string.Equals(o.Username, username, StringComparison.Ordinal);

    private (string Access, string Refresh, DateTime AccessExp, DateTime RefreshExp) NewTokens()
        => (OAuthRules.NewAccessToken(), OAuthRules.NewRefreshToken(), Now + AccessLifetime, Now + RefreshLifetime);

    private static McpOAuthResponse Tokens(string access, string refresh, string scope) => new(200, new JsonObject
    {
        ["access_token"] = access,
        ["token_type"] = "Bearer",
        ["expires_in"] = (int)AccessLifetime.TotalSeconds,
        ["refresh_token"] = refresh,
        ["scope"] = scope,
    });

    private static McpOAuthResponse Error(string error, string description) => new(400, Body(error, description));
    private static JsonObject Body(string error, string description) => new() { ["error"] = error, ["error_description"] = description };

    /// <summary>Scope rỗng → mặc định <c>tourkit.read</c>; có scope lạ → null. Luôn kèm tourkit.read (scope duy nhất cho phép đọc).</summary>
    private static string? NormalizeScope(string? raw)
    {
        var parts = SplitScope(raw);
        if (parts.Any(s => !SupportedScopes.Contains(s))) return null;
        return string.Join(' ', SupportedScopes.Where(s => s == ScopeRead || parts.Contains(s)));
    }

    private static string[] SplitScope(string? raw)
        => (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>S256 luôn ra đúng 43 ký tự base64url — dài/ngắn khác là client gửi sai hoặc dùng plain.</summary>
    private static bool IsS256Challenge(string? c)
        => c is { Length: 43 } && c.All(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsToken(string s, string prefix)
        => s.Length == prefix.Length + 32 && s.StartsWith(prefix, StringComparison.Ordinal)
           && s.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string ClientNameOf(ClientMetadata c)
        => Truncate(string.IsNullOrWhiteSpace(c.ClientName) ? c.ClientId : c.ClientName.Trim(), MaxClientName);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Trường mảng chuỗi (nếu có) chỉ được chứa giá trị cho phép.</summary>
    private static bool OnlyAllowed(string json, string field, params string[] allowed)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(field, out var arr)) return true;
        if (arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind != JsonValueKind.String || !allowed.Contains(x.GetString())) return false;
        return true;
    }

    private static string AppendQuery(string url, params (string Key, string? Value)[] ps)
    {
        var sb = new StringBuilder(url);
        var sep = url.Contains('?') ? '&' : '?';
        foreach (var (k, v) in ps)
        {
            if (v == null) continue;
            sb.Append(sep).Append(k).Append('=').Append(Uri.EscapeDataString(v));
            sep = '&';
        }
        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<string, StringValues>> ToQuery(McpAuthorizeRequest r)
    {
        yield return new("client_id", r.ClientId);
        yield return new("redirect_uri", r.RedirectUri);
        yield return new("response_type", "code");
        if (r.State != null) yield return new("state", r.State);
        yield return new("code_challenge", r.CodeChallenge);
        yield return new("code_challenge_method", "S256");
        yield return new("resource", r.Resource);
        yield return new("scope", r.Scope);
    }

    /// <summary>Đọc tham số query/form; phân biệt "thiếu" với "bị lặp" (RFC 6749 §3.1: không được lặp).</summary>
    private sealed class Params
    {
        private readonly Dictionary<string, StringValues> _d = new(StringComparer.Ordinal);
        public Params(IEnumerable<KeyValuePair<string, StringValues>> src)
        {
            foreach (var kv in src)
                _d[kv.Key] = _d.TryGetValue(kv.Key, out var prev) ? StringValues.Concat(prev, kv.Value) : kv.Value;
        }
        public IEnumerable<string> Keys => _d.Keys;
        public bool Duplicated(string k) => _d.TryGetValue(k, out var v) && v.Count > 1;
        public string? this[string k] => _d.TryGetValue(k, out var v) && v.Count == 1 ? v[0] : null;
    }
}
