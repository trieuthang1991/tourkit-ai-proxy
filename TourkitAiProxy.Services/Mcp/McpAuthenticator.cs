using System.Collections.Concurrent;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;

namespace TourkitAiProxy.Services.Mcp;

/// <summary>Kết quả xác thực: có <see cref="Caller"/> là cho qua; không thì <see cref="Status"/> + câu lỗi tiếng Việt.</summary>
public sealed record McpAuthResult(McpCaller? Caller, int Status, string? Error)
{
    public static McpAuthResult Ok(McpCaller c) => new(c, StatusCodes.Status200OK, null);
    public static McpAuthResult Deny(string error) => new(null, StatusCodes.Status401Unauthorized, error);
    public static McpAuthResult Unavailable(string error) => new(null, StatusCodes.Status503ServiceUnavailable, error);
}

/// <summary>
/// Đổi <c>Authorization: Bearer tkmcp_…</c> thành <see cref="McpCaller"/>:
/// định dạng → băm → <see cref="IMcpTokenStore.FindActiveAsync"/> → phiên gốc còn sống và đúng chủ.
///
/// <para><b>Đệm 60 giây theo bản băm.</b> Claude/Cursor bắn vài lời gọi mỗi câu hỏi; tra SQL mỗi lần là
/// thừa. Đổi lại, thu hồi phải có hiệu lực NGAY trong tiến trình này — <see cref="Evict"/> được endpoint
/// thu hồi gọi. Chạy NHIỀU instance thì instance khác vẫn có thể cho khoá đã thu hồi qua tối đa 60 giây
/// (chưa có kênh báo chéo) — nhận có chủ đích, ghi ở docs/api-surface.md.</para>
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

    // Đệm chặn trên để khoá rác không làm phình bộ nhớ vô hạn; vượt thì dọn mục hết hạn.
    private const int PruneAbove = 2000;

    private readonly IMcpTokenStore _store;
    private readonly Func<string, McpSessionOwner?> _findSession;
    private readonly ILogger<McpAuthenticator> _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (McpCaller Caller, DateTimeOffset Expires)> _cache = new();

    /// <param name="findSession">Tra chủ phiên TourKit theo sessionId; null = phiên đã hết/không còn.
    /// Là hàm chứ không phải <c>TkSessionStore</c> để test dựng được mà không cần CSDL.</param>
    public McpAuthenticator(IMcpTokenStore store, Func<string, McpSessionOwner?> findSession,
        ILogger<McpAuthenticator> log, TimeProvider? clock = null)
    {
        _store = store; _findSession = findSession; _log = log; _clock = clock ?? TimeProvider.System;
    }

    public async Task<McpAuthResult> AuthenticateAsync(string? authorization, CancellationToken ct)
    {
        var token = ReadBearer(authorization);
        if (token == null) return McpAuthResult.Deny(MissingKey);
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
        if (owner == null
            || !string.Equals(owner.TenantId, row.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(owner.Username, row.Username, StringComparison.OrdinalIgnoreCase))
        {
            _cache.TryRemove(hash, out _);
            return McpAuthResult.Deny(SessionGone);
        }

        var caller = new McpCaller(row.Id, row.SessionId, row.TenantId, row.Username);
        if (_cache.Count > PruneAbove) Prune(now);
        _cache[hash] = (caller, now + CacheTtl);
        Touch(row.Id);
        return McpAuthResult.Ok(caller);
    }

    /// <summary>Bỏ khoá khỏi đệm NGAY — gọi sau khi thu hồi thành công. Đệm nhỏ nên quét tuyến tính là đủ.</summary>
    public void Evict(long tokenId)
    {
        foreach (var kv in _cache)
            if (kv.Value.Caller.TokenId == tokenId) _cache.TryRemove(kv.Key, out _);
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

    private void Prune(DateTimeOffset now)
    {
        foreach (var kv in _cache)
            if (kv.Value.Expires <= now) _cache.TryRemove(kv.Key, out _);
    }
}
