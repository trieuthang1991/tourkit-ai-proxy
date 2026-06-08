using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Lưu/đọc creds hộp thư Gmail + chữ ký, SCOPED THEO TenantId.
/// DB-backed (dbo.MailAccounts). App Password mã hóa Crypton; tenant khác KHÔNG thấy creds nhau.
/// Không có fallback file — DB lỗi → throw, endpoint trả 503.
/// </summary>
public class MailAccountStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailAccountStore> _log;

    public MailAccountStore(TourkitAiDb db, ILogger<MailAccountStore> log)
    {
        _db = db; _log = log;
    }

    /// Lấy creds của tenant. null nếu chưa cấu hình.
    public (string Address, string AppPassword)? Get(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<MailAccountRow>(
            @"SELECT Address, AppPasswordEnc FROM dbo.MailAccounts WHERE TenantId = @t",
            new { t = tenantId });
        if (row == null || row.Address == null) return null;
        var pwd = string.IsNullOrEmpty(row.AppPasswordEnc) ? "" : Crypton.Decrypt(row.AppPasswordEnc);
        return (row.Address, pwd);
    }

    /// Upsert creds + chữ ký cho tenant.
    public void Set(string tenantId, string address, string appPassword, string? signature)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));

        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.MailAccounts AS T
USING (SELECT @t AS TenantId) AS S
   ON T.TenantId = S.TenantId
WHEN MATCHED THEN UPDATE SET Address=@a, AppPasswordEnc=@p, Signature=@s, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Address, AppPasswordEnc, Signature, UpdatedAt)
                       VALUES (@t, @a, @p, @s, SYSUTCDATETIME());",
            new
            {
                t = tenantId,
                a = address.Trim(),
                p = Crypton.Encrypt(appPassword.Trim()),
                s = (signature ?? "").Trim()
            });
        _log.LogInformation("[MailAccount] Set tenant={Tenant} address={Addr}", tenantId, address.Trim());
    }

    public bool IsConfigured(string tenantId) => Get(tenantId) is { } x && !string.IsNullOrWhiteSpace(x.Address);

    /// Địa chỉ đang cấu hình (cho UI hiển thị) — KHÔNG trả App Password. Empty nếu chưa setup.
    public string CurrentAddress(string tenantId) => Get(tenantId)?.Address ?? "";

    /// Chữ ký công ty. Empty nếu chưa setup hoặc chưa đặt.
    public string Signature(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return "";
        using var c = _db.Open();
        return c.QueryFirstOrDefault<string?>(
            @"SELECT Signature FROM dbo.MailAccounts WHERE TenantId = @t",
            new { t = tenantId }) ?? "";
    }

    public bool HasSignature(string tenantId) => !string.IsNullOrWhiteSpace(Signature(tenantId));

    /// <summary>Dapper row mapper cho dbo.MailAccounts — bind theo NAME, không phải ordinal.</summary>
    private sealed class MailAccountRow
    {
        public string Address { get; set; } = "";
        public string AppPasswordEnc { get; set; } = "";
    }
}
