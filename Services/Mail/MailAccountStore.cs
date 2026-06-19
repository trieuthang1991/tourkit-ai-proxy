using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Lưu/đọc creds hộp thư Gmail + chữ ký, SCOPED THEO (TenantId, Username).
/// Mỗi user trong cùng tenant có 1 mailbox riêng (1 dòng /user). 2 user có thể cùng cấu hình
/// trỏ về 1 địa chỉ Gmail — vẫn lưu 2 dòng, 2 App Password (có thể trùng hoặc khác).
/// App Password mã hóa Crypton, không trả về client.
/// </summary>
public class MailAccountStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<MailAccountStore> _log;

    public MailAccountStore(TourkitAiDb db, ILogger<MailAccountStore> log)
    {
        _db = db; _log = log;
    }

    /// Lấy creds của (tenant, user). null nếu chưa cấu hình.
    public (string Address, string AppPassword)? Get(string tenantId, string username)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(username)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<MailAccountRow>(
            @"SELECT Address, AppPasswordEnc FROM dbo.MailAccounts WHERE TenantId = @t AND Username = @u",
            new { t = tenantId, u = username });
        if (row == null || row.Address == null) return null;
        var pwd = string.IsNullOrEmpty(row.AppPasswordEnc) ? "" : Crypton.Decrypt(row.AppPasswordEnc);
        return (row.Address, pwd);
    }

    /// Upsert creds + chữ ký cho (tenant, user). MERGE theo composite key.
    public void Set(string tenantId, string username, string address, string appPassword, string? signature)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId rỗng", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("username rỗng", nameof(username));

        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.MailAccounts AS T
USING (SELECT @t AS TenantId, @u AS Username) AS S
   ON T.TenantId = S.TenantId AND T.Username = S.Username
WHEN MATCHED THEN UPDATE SET Address=@a, AppPasswordEnc=@p, Signature=@s, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Username, Address, AppPasswordEnc, Signature, UpdatedAt)
                       VALUES (@t, @u, @a, @p, @s, SYSUTCDATETIME());",
            new
            {
                t = tenantId,
                u = username,
                a = address.Trim(),
                p = Crypton.Encrypt(appPassword.Trim()),
                s = (signature ?? "").Trim()
            });
        _log.LogInformation("[MailAccount] Set tenant={Tenant} user={User} address={Addr}", tenantId, username, address.Trim());
    }

    /// Xoá cấu hình hộp thư của (tenant, user). Idempotent — gọi nhiều lần OK.
    /// Trả số dòng bị xoá (0 = chưa từng cấu hình).
    public int Clear(string tenantId, string username)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(username)) return 0;
        using var c = _db.Open();
        var n = c.Execute(
            @"DELETE FROM dbo.MailAccounts WHERE TenantId = @t AND Username = @u",
            new { t = tenantId, u = username });
        if (n > 0) _log.LogInformation("[MailAccount] Clear tenant={Tenant} user={User} rows={N}", tenantId, username, n);
        return n;
    }

    public bool IsConfigured(string tenantId, string username)
        => Get(tenantId, username) is { } x && !string.IsNullOrWhiteSpace(x.Address);

    /// Địa chỉ đang cấu hình (cho UI hiển thị) — KHÔNG trả App Password. Empty nếu chưa setup.
    public string CurrentAddress(string tenantId, string username) => Get(tenantId, username)?.Address ?? "";

    /// Chữ ký của user. Empty nếu chưa setup hoặc chưa đặt.
    public string Signature(string tenantId, string username)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(username)) return "";
        using var c = _db.Open();
        return c.QueryFirstOrDefault<string?>(
            @"SELECT Signature FROM dbo.MailAccounts WHERE TenantId = @t AND Username = @u",
            new { t = tenantId, u = username }) ?? "";
    }

    public bool HasSignature(string tenantId, string username)
        => !string.IsNullOrWhiteSpace(Signature(tenantId, username));

    /// <summary>Dapper row mapper cho dbo.MailAccounts — bind theo NAME, không phải ordinal.</summary>
    private sealed class MailAccountRow
    {
        public string Address { get; set; } = "";
        public string AppPasswordEnc { get; set; } = "";
    }
}
