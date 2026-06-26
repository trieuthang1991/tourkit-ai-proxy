using System.Text.Json;
using Dapper;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// Dapper repo cho dbo.TkSessions. Mật khẩu Crypton-encrypted; JWT KHÔNG persist (re-login khi cần).
/// SessionChatMemory serialize vào cột ChatMemoryJson (nullable).
///
/// CHỈ làm CRUD thuần — không cache, không retry. Caller (TkSessionStore) lo cache + side-effect.
/// </summary>
public class TkSessionRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TkSessionRepository> _log;

    private static readonly JsonSerializerOptions _jsonOpts =
        new(JsonSerializerDefaults.Web);

    // Circuit breaker cho GetTenantNamesAsync: khi SQL down, mọi call sẽ vượt deadline.
    // Cache "vừa fail" trong 30s để page admin không stall thêm 2s × N call.
    private static DateTime _tenantLookupFailedUntil = DateTime.MinValue;

    public TkSessionRepository(TourkitAiDb db, ILogger<TkSessionRepository> log)
    {
        _db = db; _log = log;
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string PasswordEnc { get; set; } = "";
        public string? FullName { get; set; }
        public string? CompanyName { get; set; }
        public string? ChatMemoryJson { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    /// Load TẤT CẢ session chưa idle expire (caller pass cutoff = UtcNow - IdleTtl).
    /// Trả về list TkSession (JWT rỗng, ép re-login lần dùng đầu).
    public async Task<List<TkSession>> ListActiveAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<Row>(
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                "FROM dbo.TkSessions WHERE LastUsedUtc >= @cut",
                new { cut = cutoffUtc });
            var list = new List<TkSession>();
            foreach (var r in rows)
            {
                var s = TryHydrate(r);
                if (s != null) list.Add(s);
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] ListActive lỗi");
            return new List<TkSession>();
        }
    }

    /// <summary>
    /// Retry transient cho thao tác DB session (read/write). KHÔNG nuốt lỗi thành null:
    /// lỗi DB tạm thời (timeout / deadlock / pool cạn dưới web-garden tải cao) ≠ "session không tồn tại".
    /// Nuốt → null sẽ làm tầng trên trả 401 → client tự logout OAN. Thay vào đó: retry vài lần,
    /// cạn thì THROW để thành 500/503 (client KHÔNG logout vì chỉ logout ở 401).
    /// "Không tìm thấy" thật = query trả null (không exception) → vẫn trả null bình thường.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> op, string label, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try { return await op(); }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning("[TkSessionRepo] {Label} lỗi DB (lần {N}/{Max}) — retry: {Err}",
                    label, attempt, maxAttempts, ex.Message);
                await Task.Delay(150 * attempt, ct);   // backoff 150ms, 300ms
            }
        }
    }

    /// Lookup 1 session by id (nullable). Dùng khi cache miss.
    /// RETRY transient để blip DB thoáng qua tự hồi → session vẫn tìm thấy → KHÔNG đá user oan (case thường gặp).
    /// Cạn retry (DB down kéo dài) → trả null NHƯ CŨ: GIỮ NGUYÊN contract 30+ caller (không đổi hành vi,
    /// không sinh lỗi mới ở caller best-effort như chat-memory). Lúc đó DB sập nên mọi thứ fail là đúng.
    public async Task<TkSession?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            return await WithRetryAsync(async () =>
            {
                await using var c = await _db.OpenAsync(ct);
                var row = await c.QueryFirstOrDefaultAsync<Row>(
                    "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                    "FROM dbo.TkSessions WHERE Id = @id",
                    new { id });
                return row == null ? null : TryHydrate(row);
            }, $"Get {id}", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] Get {Id} cạn retry — trả null", id);
            return null;
        }
    }

    /// Lookup session mới nhất theo (TenantId, Username). Dùng cho de-dup khi user login lại
    /// → reuse Id thay vì tạo row mới. Retry transient; cạn → null (đi nhánh tạo mới, an toàn).
    public async Task<TkSession?> GetByUserAsync(string tenantId, string username, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(username)) return null;
        try
        {
            return await WithRetryAsync(async () =>
            {
                await using var c = await _db.OpenAsync(ct);
                var row = await c.QueryFirstOrDefaultAsync<Row>(
                    "SELECT TOP 1 Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                    "FROM dbo.TkSessions WHERE TenantId = @tenantId AND Username = @username " +
                    "ORDER BY LastUsedUtc DESC",
                    new { tenantId, username });
                return row == null ? null : TryHydrate(row);
            }, $"GetByUser {tenantId}/{username}", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] GetByUser {Tenant}/{User} cạn retry — trả null", tenantId, username);
            return null;
        }
    }

    /// <summary>
    /// Resolve display name cho 1 batch tenantId. Trả Dictionary&lt;tenantId, displayName&gt;.
    /// SELECT TOP 1 CompanyName/FullName per tenant ORDER BY LastUsedUtc DESC.
    /// Display = CompanyName ?? FullName ?? tenantId (caller tự fallback).
    /// Tenant nào không có session → KHÔNG có entry trong dict.
    ///
    /// **Best-effort 2s deadline**: tenant name resolution là nice-to-have cho admin UI;
    /// nếu SQL unreachable (dev offline / network glitch) thì return dict rỗng thay vì
    /// block page render 15s mặc định Connect Timeout.
    /// </summary>
    public async Task<Dictionary<string, string>> GetTenantNamesAsync(
        IEnumerable<string> tenantIds, CancellationToken ct = default)
    {
        var ids = tenantIds.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (ids.Count == 0) return new();
        if (DateTime.UtcNow < _tenantLookupFailedUntil) return new(); // SQL vừa fail → skip
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var c = await _db.OpenAsync(cts.Token);
            // Subquery ROW_NUMBER để lấy row mới nhất per TenantId (idiom SQL Server)
            var rows = await c.QueryAsync<(string TenantId, string? CompanyName, string? FullName)>(
                new CommandDefinition(@"
SELECT TenantId, CompanyName, FullName FROM (
    SELECT TenantId, CompanyName, FullName,
           ROW_NUMBER() OVER (PARTITION BY TenantId ORDER BY LastUsedUtc DESC) AS rn
    FROM dbo.TkSessions
    WHERE TenantId IN @ids
) t WHERE rn = 1;",
                    parameters: new { ids },
                    cancellationToken: cts.Token));
            var dict = new Dictionary<string, string>();
            foreach (var r in rows)
            {
                var name = !string.IsNullOrWhiteSpace(r.CompanyName) ? r.CompanyName!
                         : !string.IsNullOrWhiteSpace(r.FullName)    ? r.FullName!
                         : r.TenantId;
                dict[r.TenantId] = name;
            }
            return dict;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _tenantLookupFailedUntil = DateTime.UtcNow.AddSeconds(30);
            _log.LogWarning("[TkSessionRepo] GetTenantNames vượt deadline 2s — SQL không sẵn sàng, circuit-break 30s");
            return new();
        }
        catch (Exception ex)
        {
            _tenantLookupFailedUntil = DateTime.UtcNow.AddSeconds(30);
            _log.LogWarning(ex, "[TkSessionRepo] GetTenantNames lỗi — circuit-break 30s");
            return new();
        }
    }

    /// Xoá mọi session khác (cùng TenantId+Username) ngoại trừ keepId. Trả số rows xoá.
    /// Dùng sau khi reuse session: dọn các bản ghi trùng tích lũy từ trước khi có de-dup.
    public async Task<int> DeleteOtherForUserAsync(string tenantId, string username, string keepId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(keepId)) return 0;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.ExecuteAsync(
                "DELETE FROM dbo.TkSessions WHERE TenantId = @tenantId AND Username = @username AND Id <> @keepId",
                new { tenantId, username, keepId });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] DeleteOtherForUser {Tenant}/{User} lỗi", tenantId, username);
            return 0;
        }
    }

    /// UPSERT session. Crypton-encrypt password, serialize ChatMemory. Không lưu JWT.
    public async Task UpsertAsync(TkSession s, CancellationToken ct = default)
    {
        var pwdEnc = Crypton.Encrypt(s.Password);
        var memJson = s.ChatMemory == null ? null : JsonSerializer.Serialize(s.ChatMemory, _jsonOpts);
        try
        {
            // Retry transient (deadlock/timeout dưới tải web-garden) → write session/login KHÔNG fail oan vì 1 cú lock.
            await WithRetryAsync<int>(async () =>
            {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(@"
MERGE dbo.TkSessions AS T
USING (SELECT @Id AS Id) AS S ON T.Id = S.Id
WHEN MATCHED THEN UPDATE SET
    TenantId       = @TenantId,
    Username       = @Username,
    PasswordEnc    = @PasswordEnc,
    FullName       = @FullName,
    CompanyName    = @CompanyName,
    ChatMemoryJson = @ChatMemoryJson,
    LastUsedUtc    = @LastUsedUtc
WHEN NOT MATCHED THEN INSERT
    (Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc)
VALUES
    (@Id, @TenantId, @Username, @PasswordEnc, @FullName, @CompanyName, @ChatMemoryJson, @LastUsedUtc);",
                new {
                    s.Id, s.TenantId, s.Username,
                    PasswordEnc    = pwdEnc,
                    s.FullName, s.CompanyName,
                    ChatMemoryJson = memJson,
                    LastUsedUtc    = s.LastUsed
                });
                return 0;
            }, $"Upsert {s.Id}", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TkSessionRepo] Upsert {Id} lỗi (cạn retry)", s.Id);
            throw;
        }
    }

    /// Xoá 1 session theo id. No-op nếu không tồn tại. Trả số rows xoá (0 hoặc 1).
    public async Task<int> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.ExecuteAsync(
                "DELETE FROM dbo.TkSessions WHERE Id = @id",
                new { id });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] Delete {Id} lỗi", id);
            return 0;
        }
    }

    /// Xoá tất cả session idle quá cutoff. Trả số rows xoá.
    public async Task<int> PruneIdleAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.ExecuteAsync(
                "DELETE FROM dbo.TkSessions WHERE LastUsedUtc < @cut",
                new { cut = cutoffUtc });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] PruneIdle lỗi");
            return 0;
        }
    }

    private TkSession? TryHydrate(Row r)
    {
        var pwd = Crypton.Decrypt(r.PasswordEnc);
        if (string.IsNullOrEmpty(pwd))
        {
            _log.LogWarning("[TkSessionRepo] Session {Id} decrypt fail — skip", r.Id);
            return null;
        }
        SessionChatMemory? mem = null;
        if (!string.IsNullOrWhiteSpace(r.ChatMemoryJson))
        {
            try { mem = JsonSerializer.Deserialize<SessionChatMemory>(r.ChatMemoryJson, _jsonOpts); }
            catch (Exception ex) { _log.LogWarning(ex, "[TkSessionRepo] ChatMemory parse fail {Id}", r.Id); }
        }
        return new TkSession
        {
            Id = r.Id, TenantId = r.TenantId, Username = r.Username, Password = pwd,
            FullName = r.FullName, CompanyName = r.CompanyName,
            Jwt = "", JwtExpiresAt = DateTime.MinValue,
            LastUsed = r.LastUsedUtc,
            ChatMemory = mem ?? SessionChatMemory.Empty()
        };
    }
}
