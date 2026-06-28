using System.Collections.Concurrent;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Services.TourKit;

/// 1 phiên đăng nhập TourKit. Giữ credentials đã giải mã (in-memory) để tự re-login khi JWT hết hạn.
public class TkSession
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Username { get; init; }
    // Password có thể đổi khi user đổi mật khẩu → mutable để CreateAsync (de-dup re-login) refresh.
    public required string Password { get; set; }

    public string Jwt { get; set; } = "";
    public string? FullName { get; set; }
    public string? CompanyName { get; set; }
    public DateTime JwtExpiresAt { get; set; }   // soft TTL — re-login khi quá hạn
    public DateTime LastUsed { get; set; }

    // RUNTIME-ONLY (không persist): lần cuối đã GHI LastUsed xuống DB. Dùng để throttle write-through
    // → KHÔNG ghi DB mỗi request (chỉ ~mỗi vài phút/session) → giảm tải DB mạnh dưới web-garden.
    public DateTime LastUsedPersistedUtc { get; set; } = DateTime.MinValue;

    // BỘ NHỚ CHAT — persist cùng session xuống SQL (cột ChatMemoryJson).
    public SessionChatMemory ChatMemory { get; set; } = SessionChatMemory.Empty();
}

/// <summary>
/// Lưu phiên TourKit. JWT KHÔNG ra client; client chỉ giữ sessionId. Tự re-login bằng credentials
/// trong phiên khi JWT soft-expire hoặc 401.
///
/// Persistence: SQL `dbo.TkSessions` (mật khẩu Crypton-encrypted, JWT KHÔNG lưu).
/// Cache: in-mem `ConcurrentDictionary` cho hot path Get; Get cache-miss → load từ SQL.
/// Cross-process: 2 instance cùng SQL share state; write-through đảm bảo nhất quán.
/// </summary>
public class TkSessionStore
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionRepository _repo;
    private readonly ILogger<TkSessionStore> _log;
    private readonly ConcurrentDictionary<string, TkSession> _cache = new();

    // JWT TourKit sống vài giờ; refresh chủ động sau 50 phút cho an toàn (re-login rẻ).
    private static readonly TimeSpan SoftTtl = TimeSpan.FromMinutes(50);
    // Dọn phiên không dùng quá 30 ngày.
    private static readonly TimeSpan IdleTtl = TimeSpan.FromDays(30);
    // Throttle ghi LastUsed xuống DB: chỉ write-through mỗi N phút/session (IdleTtl=30 ngày → độ trễ
    // vài phút của LastUsed hoàn toàn vô hại). Giảm từ "1 WRITE/request" xuống "~1 WRITE/3 phút/session".
    private static readonly TimeSpan LastUsedWriteThrottle = TimeSpan.FromMinutes(3);

    public TkSessionStore(TourKitApiClient api, TkSessionRepository repo, ILogger<TkSessionStore> log)
    {
        _api = api; _repo = repo; _log = log;
        LoadActiveFromSql();
    }

    /// Khởi động: load mọi session chưa idle expire vào cache.
    /// Nếu DB lỗi → cache rỗng, Get sẽ thử lại từ SQL khi cần.
    private void LoadActiveFromSql()
    {
        try
        {
            var list = _repo.ListActiveAsync(DateTime.UtcNow - IdleTtl).GetAwaiter().GetResult();
            foreach (var s in list) _cache[s.Id] = s;
            _log.LogInformation("Loaded {N} TourKit sessions từ SQL vào cache", list.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Load sessions từ SQL fail — cache rỗng, sẽ lazy-load khi Get");
        }
    }

    /// Login. De-dup theo (TenantId, Username): nếu user đã có session → reuse Id + update
    /// JWT/credentials/timestamp, giữ nguyên ChatMemory; xóa các session trùng cũ trong SQL.
    /// Nếu chưa có → tạo mới với Guid.NewGuid().
    public async Task<TkSession> CreateAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        _ = PruneIdleAsync(ct);   // fire-and-forget, không block login
        var login = await _api.LoginAsync(tenantId, username, password, ct);

        // Reuse session sẵn có (most recent) cho cùng (tenant, user) → tránh sinh row mới mỗi lần F5.
        var existing = await _repo.GetByUserAsync(tenantId, username, ct);

        TkSession session;
        if (existing != null)
        {
            // Reuse Id + ChatMemory; refresh JWT + credentials (phòng user đổi password) + LastUsed.
            existing.Password    = password;
            existing.Jwt         = login.Token;
            existing.FullName    = login.FullName;
            existing.CompanyName = login.CompanyName;
            existing.JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl);
            existing.LastUsed     = DateTime.UtcNow;
            session = existing;

            await _repo.UpsertAsync(session, ct);
            // Dọn các session dupe cũ (tích lũy trước khi có de-dup).
            var removed = await _repo.DeleteOtherForUserAsync(tenantId, username, session.Id, ct);
            // Đồng bộ cache: xóa các sessionId khác cùng (tenant,user) ra khỏi in-mem.
            foreach (var kv in _cache)
            {
                if (kv.Key != session.Id &&
                    string.Equals(kv.Value.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(kv.Value.Username, username, StringComparison.Ordinal))
                    _cache.TryRemove(kv.Key, out _);
            }
            _cache[session.Id] = session;
            _log.LogInformation("TourKit session {Id} REUSED cho tenant={Tenant} user={User} (dọn {N} dupe)",
                session.Id, tenantId, username, removed);
        }
        else
        {
            session = new TkSession
            {
                Id          = Guid.NewGuid().ToString("N"),
                TenantId    = tenantId,
                Username    = username,
                Password    = password,
                Jwt         = login.Token,
                FullName    = login.FullName,
                CompanyName = login.CompanyName,
                JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl),
                LastUsed    = DateTime.UtcNow
            };
            _cache[session.Id] = session;
            await _repo.UpsertAsync(session, ct);
            _log.LogInformation("TourKit session {Id} TẠO MỚI cho tenant={Tenant} user={User}",
                session.Id, tenantId, username);
        }

        session.LastUsedPersistedUtc = DateTime.UtcNow;   // vừa upsert ở trên → reset throttle
        return session;
    }

    /// Get cache trước; cache miss → load SQL (đồng bộ — chỉ xảy ra lần đầu hoặc sau restart).
    public TkSession? Get(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (_cache.TryGetValue(sessionId, out var s)) return s;
        var fromDb = _repo.GetAsync(sessionId).GetAwaiter().GetResult();
        if (fromDb != null) _cache[sessionId] = fromDb;
        return fromDb;
    }

    /// Lấy (hoặc tạo) sessionId cho 1 SERVICE ACCOUNT (tenant, username) — dùng cho workflow nền,
    /// KHÔNG cần user online. Tái dùng session sẵn có (JWT tự re-login lazy khi GetValidJwt);
    /// chưa có ở cache lẫn SQL → login mới + persist. Trả sessionId để DealOpportunityClient dùng.
    public async Task<string> GetOrCreateServiceSessionAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        // 1) Cache: session sẵn của (tenant, user)?
        foreach (var kv in _cache)
        {
            var s = kv.Value;
            if (string.Equals(s.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(s.Username, username, StringComparison.Ordinal))
            {
                s.Password = password;   // đồng bộ phòng đổi pass
                return s.Id;
            }
        }
        // 2) SQL: session sẵn (instance khác tạo / sau restart)?
        var fromDb = await _repo.GetByUserAsync(tenantId, username, ct);
        if (fromDb != null)
        {
            fromDb.Password = password;
            _cache[fromDb.Id] = fromDb;
            return fromDb.Id;
        }
        // 3) Chưa có → login mới (persist SQL). CreateAsync de-dup + ghi DB.
        var created = await CreateAsync(tenantId, username, password, ct);
        return created.Id;
    }

    /// JWT còn hạn (soft TTL); tự re-login nếu hết. Throw nếu phiên không tồn tại.
    public async Task<string> GetValidJwtAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        s.LastUsed = DateTime.UtcNow;   // cập nhật CACHE in-mem mỗi request (rẻ)
        if (string.IsNullOrEmpty(s.Jwt) || DateTime.UtcNow >= s.JwtExpiresAt)
        {
            await ReloginAsync(s, ct);   // re-login đã write-through (set LastUsedPersistedUtc bên trong)
        }
        else if (DateTime.UtcNow - s.LastUsedPersistedUtc > LastUsedWriteThrottle)
        {
            // THROTTLE: chỉ write-through LastUsed mỗi vài phút/session (không phải mỗi request)
            // → giảm tải DB lớn. Cross-process vẫn thấy session active (sai số phút là vô hại với IdleTtl 30 ngày).
            s.LastUsedPersistedUtc = DateTime.UtcNow;
            await _repo.UpsertAsync(s, ct);
        }
        return s.Jwt;
    }

    /// Liệt kê tất cả phiên đang giữ trong cache (in-mem). Dùng cho admin UI.
    /// KHÔNG hit SQL — cache đã được nạp từ SQL lúc startup + write-through ở mọi mutation,
    /// nên tin được. Trả snapshot, không enumerate live cache.
    public IReadOnlyList<TkSession> ListActive() => _cache.Values.ToList();

    /// Kick 1 phiên: xóa khỏi cache + xóa khỏi SQL. Trả true nếu phiên tồn tại trước đó.
    /// Idempotent (kick lần 2 trả false).
    public async Task<bool> KickAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        var removed = _cache.TryRemove(sessionId, out var s);
        try
        {
            await _repo.DeleteAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionStore] Kick {Id} — DB delete lỗi (cache đã xóa)", sessionId);
        }
        if (removed && s != null)
            _log.LogInformation("[TkSessionStore] KICKED session {Id} tenant={Tenant} user={User}",
                sessionId, s.TenantId, s.Username);
        return removed;
    }

    /// Buộc re-login (gọi khi TourKit trả 401 giữa chừng).
    public async Task<string> ForceReloginAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        await ReloginAsync(s, ct);
        return s.Jwt;
    }

    private async Task ReloginAsync(TkSession s, CancellationToken ct)
    {
        var login = await _api.LoginAsync(s.TenantId, s.Username, s.Password, ct);
        s.Jwt = login.Token;
        s.FullName = login.FullName;
        s.CompanyName = login.CompanyName;
        s.JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl);
        s.LastUsed = DateTime.UtcNow;
        await _repo.UpsertAsync(s, ct);
        s.LastUsedPersistedUtc = DateTime.UtcNow;   // vừa write-through → reset throttle
        _log.LogInformation("TourKit session {Id} re-login (JWT refreshed)", s.Id);
    }

    private async Task PruneIdleAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow - IdleTtl;
            foreach (var kv in _cache)
                if (kv.Value.LastUsed < cutoff) _cache.TryRemove(kv.Key, out _);
            var removed = await _repo.PruneIdleAsync(cutoff, ct);
            if (removed > 0)
                _log.LogInformation("[TkSessionStore] Pruned {N} idle sessions", removed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionStore] PruneIdle lỗi");
        }
    }

    /// One-shot migration: đọc file legacy `data/tk-sessions.json` (nếu còn) → import vào SQL.
    /// Chạy ở startup. Sau khi import xong → rename file thành `.migrated` để khỏi chạy lại.
    /// Idempotent: nếu file không tồn tại HOẶC đã rename → no-op.
    public async Task MigrateFromLegacyFileAsync(string dataDir, CancellationToken ct = default)
    {
        var path = Path.Combine(dataDir, "tk-sessions.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var legacyList = System.Text.Json.JsonSerializer.Deserialize<List<LegacyPersisted>>(json, opts) ?? new();
            int ok = 0, skip = 0;
            foreach (var p in legacyList)
            {
                var pwd = TourkitAiProxy.Services.Security.Crypton.Decrypt(p.EncPassword);
                if (string.IsNullOrEmpty(pwd)) { skip++; continue; }
                var existing = await _repo.GetAsync(p.Id, ct);
                if (existing != null) { skip++; continue; }
                var s = new TkSession
                {
                    Id = p.Id, TenantId = p.TenantId, Username = p.Username, Password = pwd,
                    FullName = p.FullName, CompanyName = p.CompanyName,
                    Jwt = "", JwtExpiresAt = DateTime.MinValue,
                    LastUsed = DateTime.TryParse(p.LastUsedIso, out var d) ? d.ToUniversalTime() : DateTime.UtcNow,
                    ChatMemory = p.ChatMemory ?? SessionChatMemory.Empty()
                };
                await _repo.UpsertAsync(s, ct);
                _cache[s.Id] = s;
                ok++;
            }
            File.Move(path, path + ".migrated", overwrite: true);
            _log.LogInformation("[TkSessionStore] Migrated {Ok} sessions từ file legacy (skip {Skip}), file → .migrated", ok, skip);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TkSessionStore] Migrate file legacy lỗi — giữ file nguyên để retry");
        }
    }

    private sealed record LegacyPersisted(
        string Id, string TenantId, string Username, string EncPassword,
        string? FullName, string? CompanyName, string LastUsedIso,
        SessionChatMemory? ChatMemory = null);

    // ─── Chat memory helpers ────────────────────────────────────────────────────

    /// Lấy bộ nhớ chat của phiên. Trả null nếu không tìm thấy phiên.
    public SessionChatMemory? GetMemory(string sessionId) => Get(sessionId)?.ChatMemory;

    /// Cập nhật bộ nhớ chat, tự gán LastUpdated = UtcNow, write-through SQL.
    public void UpdateMemory(string sessionId, SessionChatMemory memory)
    {
        var s = Get(sessionId);
        if (s == null) return;
        s.ChatMemory = memory with { LastUpdated = DateTime.UtcNow };
        s.LastUsed = DateTime.UtcNow;
        s.LastUsedPersistedUtc = DateTime.UtcNow;   // chat memory upsert cũng đẩy LastUsed → reset throttle
        // Fire-and-forget: chat memory update là hot path, không block agent loop
        _ = _repo.UpsertAsync(s);
    }

    /// Xóa bộ nhớ chat về Empty (khi user yêu cầu reset hội thoại).
    public void ClearMemory(string sessionId)
    {
        var s = Get(sessionId);
        if (s == null) return;
        s.ChatMemory = SessionChatMemory.Empty();
        s.LastUsed = DateTime.UtcNow;
        _ = _repo.UpsertAsync(s);
    }
}
