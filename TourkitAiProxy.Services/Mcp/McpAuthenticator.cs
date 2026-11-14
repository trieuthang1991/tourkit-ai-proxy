using System.Collections.Concurrent;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;
using TourkitAiProxy.Services.Mcp.OAuth;

namespace TourkitAiProxy.Services.Mcp;

/// <summary>Kết quả xác thực: có <see cref="Caller"/> là cho qua; không thì <see cref="Status"/> + câu lỗi tiếng Việt.</summary>
public sealed record McpAuthResult(McpCaller? Caller, int Status, string? Error)
{
    public static McpAuthResult Ok(McpCaller c) => new(c, StatusCodes.Status200OK, null);
    public static McpAuthResult Deny(string error) => new(null, StatusCodes.Status401Unauthorized, error);
    public static McpAuthResult Unavailable(string error) => new(null, StatusCodes.Status503ServiceUnavailable, error);
}

/// <summary>
/// Đổi <c>Authorization: Bearer …</c> thành <see cref="McpCaller"/>. Hai loại thông tin xác thực:
/// <list type="bullet">
/// <item><b>Khoá tay <c>tkmcp_</c></b> (giai đoạn 1): định dạng → băm → <see cref="IMcpTokenStore.FindActiveAsync"/>
/// → phiên gốc còn sống và đúng chủ.</item>
/// <item><b>Access token OAuth <c>tkoat_</c></b> (Claude.ai / ChatGPT) — CHỈ khi dựng kèm kho OAuth + cấu hình, tức
/// cờ <c>Features:McpOAuth</c> có hiệu lực: băm → <see cref="IMcpOAuthStore.FindGrantByAccessAsync"/> (chỉ lọc đã
/// thu hồi) → TỰ kiểm còn hạn (UTC), <c>Resource</c> đúng bằng resource của mình (audience), phiên TourKit còn và
/// đúng chủ. Cờ tắt thì <c>tkoat_</c> rơi vào nhánh "khoá không hợp lệ" như mọi chuỗi rác.</item>
/// </list>
///
/// <para><b>Đệm 60 giây theo bản băm.</b> Claude/Cursor bắn vài lời gọi mỗi câu hỏi; tra SQL mỗi lần là
/// thừa. Đổi lại, thu hồi phải có hiệu lực NGAY trong tiến trình này — <see cref="Evict"/> /
/// <see cref="EvictGrant"/> được endpoint thu hồi gọi. Token OAuth không bao giờ được đệm quá
/// <c>AccessExpiresUtc</c>. Chạy NHIỀU instance thì instance khác vẫn có thể cho khoá đã thu hồi qua tối đa
/// 60 giây (chưa có kênh báo chéo) — nhận có chủ đích, ghi ở docs/api-surface.md.</para>
///
/// <para><b>Không gian Id tách theo loại.</b> Id khoá tay (<c>dbo.McpTokens</c>) và Id grant
/// (<c>dbo.McpOAuthGrants</c>) đánh số độc lập; mục đệm mang <see cref="McpCaller.Kind"/> nên
/// <see cref="Evict"/> chỉ xoá khoá tay, <see cref="EvictGrant"/> chỉ xoá grant. Bản băm của hai loại khác
/// tiền tố nên khoá đệm (bản băm) không bao giờ trùng.</para>
///
/// <para><b>Chỉ đệm kết quả ĐẠT.</b> Khoá sai không đệm: đệm lỗi thì khoá vừa tạo xong bị từ chối oan
/// suốt 60 giây; còn khoá sai đúng định dạng đi tra SQL bằng chỉ mục bản băm, rẻ.</para>
///
/// <para><b>Không kiểm được = đóng cửa.</b> CSDL chết lúc tra khoá hay tra phiên → 503, không bao giờ
/// cho qua. Đây là cửa vào dữ liệu công ty từ ngoài trình duyệt.</para>
/// </summary>
public sealed class McpAuthenticator
{
    public const string ItemKey = "mcp.caller";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public const string MissingKey = "Thiếu khoá MCP — gửi header 'Authorization: Bearer tkmcp_…' (tạo khoá ở trang Kết nối AI)";
    public const string InvalidKey = "Khoá MCP không hợp lệ hoặc đã bị thu hồi — tạo khoá mới ở trang Kết nối AI";
    public const string SessionGone = "Phiên gốc đã hết — tạo khoá mới";
    public const string CannotVerify = "Chưa kiểm được khoá MCP lúc này (máy chủ dữ liệu không phản hồi) — thử lại sau ít phút";
    public const string OAuthInvalid = "Kết nối đã hết hạn hoặc đã bị thu hồi — hãy kết nối lại TourKit trong ứng dụng AI";
    public const string OAuthSessionGone = "Phiên đăng nhập TourKit của kết nối này đã hết — hãy kết nối lại trong ứng dụng AI";

    /// <summary>Header 401 của giai đoạn 1 (cờ OAuth tắt).</summary>
    public const string PlainChallenge = "Bearer";

    // Đệm chặn trên để khoá rác không làm phình bộ nhớ vô hạn; vượt thì dọn mục hết hạn.
    private const int PruneAbove = 2000;

    private readonly IMcpTokenStore _store;
    private readonly Func<string, McpSessionOwner?> _findSession;
    private readonly ILogger<McpAuthenticator> _log;
    private readonly TimeProvider _clock;
    private readonly IMcpOAuthStore? _oauthStore;
    private readonly string? _resource;
    private readonly ConcurrentDictionary<string, (McpCaller Caller, DateTimeOffset Expires)> _cache = new();

    /// <param name="findSession">Tra chủ phiên TourKit theo sessionId; null = phiên đã hết/không còn.
    /// Là hàm chứ không phải <c>TkSessionStore</c> để test dựng được mà không cần CSDL.</param>
    /// <param name="oauthStore">Kho OAuth — chỉ truyền khi <c>FeatureFlags.McpOAuth</c> có hiệu lực.</param>
    /// <param name="oauth">Cấu hình OAuth (resource, địa chỉ metadata) — truyền cùng <paramref name="oauthStore"/>.
    /// Thiếu một trong hai = OAuth tắt: không nhận <c>tkoat_</c>, header 401 như giai đoạn 1.</param>
    public McpAuthenticator(IMcpTokenStore store, Func<string, McpSessionOwner?> findSession,
        ILogger<McpAuthenticator> log, TimeProvider? clock = null,
        IMcpOAuthStore? oauthStore = null, McpOAuthOptions? oauth = null)
    {
        _store = store; _findSession = findSession; _log = log; _clock = clock ?? TimeProvider.System;
        if (oauthStore != null && oauth != null)
        {
            _oauthStore = oauthStore;
            _resource = OAuthRules.NormalizeResource(oauth.Resource);
            // Claude chỉ bắt đầu đăng nhập OAuth khi gặp HTTP 401 kèm đúng header này (RFC 9728 §5.1).
            WwwAuthenticate = $"Bearer resource_metadata=\"{oauth.BaseUrl}/.well-known/oauth-protected-resource\", scope=\"tourkit.read\"";
        }
        else WwwAuthenticate = PlainChallenge;
    }

    /// <summary>Giá trị <c>WWW-Authenticate</c> cho MỌI 401 của <c>/mcp</c>.</summary>
    public string WwwAuthenticate { get; }

    public async Task<McpAuthResult> AuthenticateAsync(string? authorization, CancellationToken ct)
    {
        var token = ReadBearer(authorization);
        if (token == null) return McpAuthResult.Deny(MissingKey);
        if (_oauthStore != null && OAuthRules.LooksLikeAccessToken(token)) return await AuthenticateOAuthAsync(token, ct);
        if (!McpTokens.LooksValid(token)) return McpAuthResult.Deny(InvalidKey);

        var hash = McpTokens.Hash(token);
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(hash, out var hit) && hit.Expires > now) return McpAuthResult.Ok(hit.Caller);

        McpTokenRow? row;
        McpSessionOwner? owner;
        try
        {
            row = await _store.FindActiveAsync(hash, ct);
            if (row == null) { _cache.TryRemove(hash, out _); return McpAuthResult.Deny(InvalidKey); }
            owner = _findSession(row.SessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mcp] tra khoá/phiên lỗi — đóng cửa (503), không cho qua");
            return McpAuthResult.Unavailable(CannotVerify);
        }

        // Phiên đã bị dọn (30 ngày không dùng) hoặc — phòng dữ liệu lệch — thuộc người khác: khoá của A
        // tuyệt đối không được chạy bằng quyền CRM của B.
        if (!SameOwner(owner, row.TenantId, row.Username))
        {
            _cache.TryRemove(hash, out _);
            return McpAuthResult.Deny(SessionGone);
        }

        var caller = new McpCaller(row.Id, row.SessionId, row.TenantId, row.Username);
        Remember(hash, caller, now + CacheTtl, now);
        Touch(row.Id);
        return McpAuthResult.Ok(caller);
    }

    /// <summary>
    /// Nhánh <c>tkoat_</c>. Kho chỉ lọc RevokedUtc — hạn và audience PHẢI kiểm ở đây (nghĩa vụ ghi ở
    /// <see cref="IMcpOAuthStore.FindGrantByAccessAsync"/>). Mọi trượt đều 401, kể cả token cấp cho resource khác.
    /// </summary>
    private async Task<McpAuthResult> AuthenticateOAuthAsync(string token, CancellationToken ct)
    {
        var hash = OAuthRules.Hash(token);
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(hash, out var hit) && hit.Expires > now) return McpAuthResult.Ok(hit.Caller);

        McpOAuthGrant? g;
        McpSessionOwner? owner = null;
        try
        {
            g = await _oauthStore!.FindGrantByAccessAsync(hash, ct);
            if (g != null) owner = _findSession(g.SessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mcp] tra token OAuth/phiên lỗi — đóng cửa (503), không cho qua");
            return McpAuthResult.Unavailable(CannotVerify);
        }

        _cache.TryRemove(hash, out _);
        if (g == null || g.RevokedUtc != null) return McpAuthResult.Deny(OAuthInvalid);

        // Kho trả DateTime Kind=Utc; vẫn ép Utc phòng dữ liệu Unspecified (docs/datetime-convention.md).
        var expires = new DateTimeOffset(DateTime.SpecifyKind(g.AccessExpiresUtc, DateTimeKind.Utc));
        if (now >= expires) return McpAuthResult.Deny(OAuthInvalid);

        if (_resource == null || !string.Equals(OAuthRules.NormalizeResource(g.Resource), _resource, StringComparison.Ordinal))
        {
            _log.LogWarning("[mcp] token OAuth của grant {Id} cấp cho resource khác — từ chối", g.Id);
            return McpAuthResult.Deny(OAuthInvalid);
        }

        if (!SameOwner(owner, g.TenantId, g.Username)) return McpAuthResult.Deny(OAuthSessionGone);

        var caller = new McpCaller(g.Id, g.SessionId, g.TenantId, g.Username, McpCallerKind.OAuthGrant);
        var until = now + CacheTtl;
        Remember(hash, caller, until < expires ? until : expires, now);
        TouchGrant(g.Id);
        return McpAuthResult.Ok(caller);
    }

    /// <summary>Bỏ KHOÁ TAY khỏi đệm NGAY — gọi sau khi thu hồi thành công. Đệm nhỏ nên quét tuyến tính là đủ.</summary>
    public void Evict(long tokenId) => EvictWhere(McpCallerKind.ManualKey, tokenId);

    /// <summary>Bỏ KẾT NỐI OAUTH (grant) khỏi đệm NGAY — gọi sau khi thu hồi kết nối thành công.</summary>
    public void EvictGrant(long grantId) => EvictWhere(McpCallerKind.OAuthGrant, grantId);

    private void EvictWhere(McpCallerKind kind, long id)
    {
        foreach (var kv in _cache)
            if (kv.Value.Caller.Kind == kind && kv.Value.Caller.TokenId == id) _cache.TryRemove(kv.Key, out _);
    }

    private static bool SameOwner(McpSessionOwner? owner, string tenant, string username)
        => owner != null
           && string.Equals(owner.TenantId, tenant, StringComparison.OrdinalIgnoreCase)
           && string.Equals(owner.Username, username, StringComparison.OrdinalIgnoreCase);

    private void Remember(string hash, McpCaller caller, DateTimeOffset expires, DateTimeOffset now)
    {
        if (_cache.Count > PruneAbove) Prune(now);
        _cache[hash] = (caller, expires);
    }

    /// "Bearer xxx" (scheme không phân biệt hoa thường — RFC 6750). Rỗng/khác scheme → null.
    private static string? ReadBearer(string? authorization)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var t = authorization[scheme.Length..].Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// Ghi "lần dùng cuối" — chỉ khi đệm hụt (≤ 1 lần/60 giây/khoá), chạy nền. Lỗi chỉ ghi log: hiển thị
    /// "dùng lần cuối" trễ vài phút vô hại, còn làm hỏng lời gọi của người dùng vì nó thì không đáng.
    /// </summary>
    private void Touch(long id) => _ = Task.Run(async () =>
    {
        try { await _store.TouchAsync(id, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "[mcp] ghi LastUsedUtc cho khoá {Id} lỗi (bỏ qua)", id); }
    });

    /// <summary>Như <see cref="Touch"/> nhưng cho grant OAuth (kho cũng tự chặn ≤ 1 lần/phút trong SQL).</summary>
    private void TouchGrant(long id) => _ = Task.Run(async () =>
    {
        try { await _oauthStore!.TouchGrantAsync(id, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "[mcp] ghi LastUsedUtc cho kết nối OAuth {Id} lỗi (bỏ qua)", id); }
    });

    private void Prune(DateTimeOffset now)
    {
        foreach (var kv in _cache)
            if (kv.Value.Expires <= now) _cache.TryRemove(kv.Key, out _);
    }
}
