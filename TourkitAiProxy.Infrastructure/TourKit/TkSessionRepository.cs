using System.Text.Json;
using Dapper;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Db;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.TourKit;

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
        public string? PermissionsJson { get; set; }
    public int? CrmUserId { get; set; }
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
                "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, PermissionsJson, CrmUserId, LastUsedUtc " +
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
                    "SELECT Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, PermissionsJson, CrmUserId, LastUsedUtc " +
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
                    "SELECT TOP 1 Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, PermissionsJson, CrmUserId, LastUsedUtc " +
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
        var permJson = s.PermissionsLoaded ? JsonSerializer.Serialize(s.Permissions, _jsonOpts) : null;
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
    PermissionsJson = @PermissionsJson,
    CrmUserId      = @CrmUserId,
    LastUsedUtc    = @LastUsedUtc
WHEN NOT MATCHED THEN INSERT
    (Id, TenantId, Username, PasswordEnc, FullName, CompanyName, ChatMemoryJson, PermissionsJson, CrmUserId, LastUsedUtc)
VALUES
    (@Id, @TenantId, @Username, @PasswordEnc, @FullName, @CompanyName, @ChatMemoryJson, @PermissionsJson, @CrmUserId, @LastUsedUtc);",
                new {
                    s.Id, s.TenantId, s.Username,
                    PasswordEnc    = pwdEnc,
                    s.FullName, s.CompanyName,
                    ChatMemoryJson = memJson,
                    PermissionsJson = permJson,
                    s.CrmUserId,
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

    /// <summary>Kết quả đọc cột <c>PasswordEnc</c> của một phiên.</summary>
    public enum PasswordState
    {
        /// Phiên thường: có mật khẩu, đăng nhập lại được bằng chính mật khẩu đó.
        Ok,

        /// <summary>
        /// Phiên SSO: mã hoá đàng hoàng nhưng nội dung RỖNG — đăng nhập một chạm từ CRM ký bằng
        /// HMAC, không hề có mật khẩu.
        ///
        /// <para><b>Phải GIỮ, không được vứt.</b> Phần còn lại của hệ đã hỗ trợ sẵn: khi hết hạn,
        /// <c>TkSessionStore.ReloginAsync</c> thấy mật khẩu rỗng thì xin JWT mới qua
        /// <c>IssueSsoTokenAsync</c> — một lượt gọi máy-tới-máy sang CRM, không cần người dùng làm
        /// gì. Vứt phiên ở đây là chặn đúng cái đường đó.</para>
        /// </summary>
        Sso,

        /// Cột rỗng hẳn — dòng ghi thiếu, không suy ra được người này đăng nhập kiểu gì.
        MissingColumn,

        /// Chuỗi không giải mã được (không phải base64, hoặc sai khối/đệm). Dữ liệu hỏng thật.
        Corrupt,
    }

    /// <summary>
    /// Đọc mật khẩu đã mã hoá và cho biết <b>vì sao</b> nếu không ra được.
    ///
    /// <para>⚠️ <b>Ba ca này trước 27/08/2026 bị gộp làm một.</b> Cả ba đều cho ra chuỗi rỗng, và
    /// lớp đọc coi hết là "decrypt fail" rồi bỏ phiên. Nhưng <b>phiên SSO vốn không có mật khẩu</b>
    /// nên cũng ra rỗng — và bị vứt oan. Hậu quả: mỗi lần khởi động lại, mọi phiên SSO biến mất và
    /// không khôi phục được, kể cả tra thẳng theo id.</para>
    ///
    /// <para>Nặng nhất là bản tin sáng: <c>SaleBriefWorkflow</c> hỏi CSDL theo tên người dùng,
    /// không thấy gì, rồi ghi nhật ký <i>"chưa đăng nhập lần nào"</i> — sai sự thật, và người đó
    /// <b>không bao giờ nhận được bản tin</b>. Không lỗi, không cảnh báo, không ai biết.</para>
    ///
    /// <para>Hàm THUẦN, có test cho cả năm ca.</para>
    /// </summary>
    public static PasswordState ReadPassword(string? enc, out string password)
    {
        password = "";
        if (string.IsNullOrWhiteSpace(enc)) return PasswordState.MissingColumn;

        // Kiểm base64 TRƯỚC: Crypton.Decrypt nuốt lỗi base64 và trả chuỗi rỗng, tức là chuỗi rác
        // sẽ trông y hệt phiên SSO. Không tách ở đây thì bản sửa này vô nghĩa.
        if (!LooksBase64(enc!)) return PasswordState.Corrupt;

        try { password = Crypton.Decrypt(enc!); }
        catch { return PasswordState.Corrupt; }   // sai khối / sai đệm

        // Giải được nhưng rỗng = phiên SSO. Đây là ca ĐÚNG, không phải lỗi.
        return password.Length == 0 ? PasswordState.Sso : PasswordState.Ok;
    }

    /// <summary>Chuỗi có phải base64 hợp lệ không — tách riêng vì <c>Crypton.Decrypt</c>
    /// nuốt lỗi base64 và trả chuỗi rỗng, tức chuỗi rác trông y hệt phiên SSO.</summary>
    private static bool LooksBase64(string s)
    {
        var dem = new byte[((s.Length + 3) / 4) * 3];
        return Convert.TryFromBase64String(s, dem, out _);
    }

    private TkSession? TryHydrate(Row r)
    {
        var trangThai = ReadPassword(r.PasswordEnc, out var pwd);
        if (trangThai is PasswordState.MissingColumn or PasswordState.Corrupt)
        {
            // Nói ĐÚNG bệnh: câu "decrypt fail" cũ chỉ người đọc đi soi khoá mã hoá, trong khi
            // khoá là hằng số biên dịch cứng và chưa bao giờ là nguyên nhân.
            _log.LogWarning("[TkSessionRepo] Phiên {Id} bỏ qua — mật khẩu {ViSao}", r.Id,
                trangThai == PasswordState.MissingColumn ? "thiếu trong CSDL" : "lưu hỏng, không giải được");
            return null;
        }

        // trangThai == Sso: mật khẩu rỗng là ĐÚNG, phiên vẫn dùng được — ReloginAsync tự xin JWT
        // mới qua đường sso-token. Xem chú thích ở PasswordState.Sso.
        SessionChatMemory? mem = null;
        if (!string.IsNullOrWhiteSpace(r.ChatMemoryJson))
        {
            try { mem = JsonSerializer.Deserialize<SessionChatMemory>(r.ChatMemoryJson, _jsonOpts); }
            catch (Exception ex) { _log.LogWarning(ex, "[TkSessionRepo] ChatMemory parse fail {Id}", r.Id); }
        }
        List<string> perms = new();
        bool permsLoaded = r.PermissionsJson != null;
        if (!string.IsNullOrWhiteSpace(r.PermissionsJson))
        {
            try { perms = JsonSerializer.Deserialize<List<string>>(r.PermissionsJson, _jsonOpts) ?? new(); }
            catch (Exception ex) { _log.LogWarning(ex, "[TkSessionRepo] Permissions parse fail {Id}", r.Id); }
        }
        return new TkSession
        {
            Id = r.Id, TenantId = r.TenantId, Username = r.Username, Password = pwd,
            FullName = r.FullName, CompanyName = r.CompanyName,
            Jwt = "", JwtExpiresAt = DateTime.MinValue,
            LastUsed = r.LastUsedUtc,
            ChatMemory = mem ?? SessionChatMemory.Empty(),
            Permissions = perms, PermissionsLoaded = permsLoaded,
            // Đọc lại từ DB: phiên load từ SQL chưa có JWT (không persist) nên KHÔNG decode lại được
            // — phải lấy giá trị đã lưu, nếu không thì mỗi lần restart là mất id, bản tin lọc hụt.
            CrmUserId = r.CrmUserId,
        };
    }
}
