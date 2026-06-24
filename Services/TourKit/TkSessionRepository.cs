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

    /// Lookup 1 session by id (nullable). Dùng khi cache miss.
    public async Task<TkSession?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var row = await c.QueryFirstOrDefaultAsync<Row>(
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, LastUsedUtc " +
                "FROM dbo.TkSessions WHERE Id = @id",
                new { id });
            return row == null ? null : TryHydrate(row);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TkSessionRepo] Get {Id} lỗi", id);
            return null;
        }
    }

    /// UPSERT session. Crypton-encrypt password, serialize ChatMemory. Không lưu JWT.
    public async Task UpsertAsync(TkSession s, CancellationToken ct = default)
    {
        var pwdEnc = Crypton.Encrypt(s.Password);
        var memJson = s.ChatMemory == null ? null : JsonSerializer.Serialize(s.ChatMemory, _jsonOpts);
        try
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
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TkSessionRepo] Upsert {Id} lỗi", s.Id);
            throw;
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
