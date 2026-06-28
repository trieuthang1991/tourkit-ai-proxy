using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// Tài khoản dịch vụ (service account) per-tenant cho workflow nền tự đăng nhập TourKit —
/// KHÔNG cần user online. Lưu `dbo.TenantServiceAccounts`, mật khẩu Crypton-encrypted (không plaintext,
/// KHÔNG trả ra client). 1 tài khoản / tenant (PK TenantId).
///
/// Repo thuần Dapper. Validate login (thử LoginAsync) do endpoint lo trước khi gọi Upsert.
/// </summary>
public class TenantServiceAccountStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantServiceAccountStore> _log;

    public TenantServiceAccountStore(TourkitAiDb db, ILogger<TenantServiceAccountStore> log)
    {
        _db = db; _log = log;
    }

    /// Credentials đã giải mã (chỉ dùng nội bộ — KHÔNG serialize ra client).
    public record ServiceAccount(string TenantId, string Username, string Password, bool Enabled);

    private sealed class Row
    {
        public string TenantId { get; set; } = "";
        public string Username { get; set; } = "";
        public string PasswordEnc { get; set; } = "";
        public bool Enabled { get; set; }
    }

    /// Lấy service account của tenant (password đã decrypt). null nếu chưa cấu hình / decrypt fail.
    public ServiceAccount? Get(string tenantId)
    {
        try
        {
            using var c = _db.Open();
            var row = c.QueryFirstOrDefault<Row>(
                "SELECT TenantId, Username, PasswordEnc, Enabled FROM dbo.TenantServiceAccounts WHERE TenantId = @t",
                new { t = tenantId });
            if (row == null) return null;
            var pwd = Crypton.Decrypt(row.PasswordEnc);
            if (string.IsNullOrEmpty(pwd))
            {
                _log.LogWarning("[TenantSvcAcc] decrypt fail cho tenant {Tenant}", tenantId);
                return null;
            }
            return new ServiceAccount(row.TenantId, row.Username, pwd, row.Enabled);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantSvcAcc] Get lỗi tenant {Tenant}", tenantId);
            return null;
        }
    }

    /// Trạng thái cấu hình (cho GET endpoint — KHÔNG trả password).
    public (bool Configured, string? Username) Status(string tenantId)
    {
        try
        {
            using var c = _db.Open();
            var username = c.QueryFirstOrDefault<string?>(
                "SELECT Username FROM dbo.TenantServiceAccounts WHERE TenantId = @t",
                new { t = tenantId });
            return (username != null, username);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantSvcAcc] Status lỗi tenant {Tenant}", tenantId);
            return (false, null);
        }
    }

    /// Lưu/ghi đè service account (password Crypton-encrypt). Caller PHẢI validate login trước.
    /// Luôn set Enabled=1 (lưu lại = bật lại).
    public async Task UpsertAsync(string tenantId, string username, string password, string? updatedBy, CancellationToken ct = default)
    {
        var enc = Crypton.Encrypt(password);
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.TenantServiceAccounts AS T
USING (SELECT @TenantId AS TenantId) AS S ON T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET
    Username = @Username, PasswordEnc = @PasswordEnc,
    Enabled = 1, UpdatedBy = @UpdatedBy, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Username, PasswordEnc, Enabled, UpdatedBy, UpdatedUtc)
VALUES
    (@TenantId, @Username, @PasswordEnc, 1, @UpdatedBy, SYSUTCDATETIME());",
            new { TenantId = tenantId, Username = username, PasswordEnc = enc, UpdatedBy = updatedBy });
    }

    /// Xóa hẳn service account của tenant (workflow sẽ fail "chưa cấu hình" → ngừng tự login). Trả true nếu có xóa.
    public async Task<bool> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var n = await c.ExecuteAsync("DELETE FROM dbo.TenantServiceAccounts WHERE TenantId = @t", new { t = tenantId });
        return n > 0;
    }
}
