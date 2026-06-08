using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// State đồng bộ IMAP per-(TenantId, Address) — để kéo INCREMENTAL chỉ email UID mới hơn lần trước.
/// DB-backed dbo.MailSyncState. UidValidity đổi (server reset) → coi như mới, kéo lại từ đầu.
/// </summary>
public class MailSyncStore
{
    public record SyncState(uint UidValidity, uint LastUid);

    private readonly TourkitAiDb _db;
    private readonly ILogger<MailSyncStore> _log;

    public MailSyncStore(TourkitAiDb db, ILogger<MailSyncStore> log)
    {
        _db = db; _log = log;
    }

    public SyncState? Get(string tenantId, string address)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(address)) return null;
        using var c = _db.Open();
        var row = c.QueryFirstOrDefault<MailSyncRow>(
            @"SELECT UidValidity, LastUid FROM dbo.MailSyncState
              WHERE TenantId=@t AND Address=@a",
            new { t = tenantId, a = address });
        return row == null ? null : new SyncState((uint)row.UidValidity, (uint)row.LastUid);
    }

    public void Set(string tenantId, string address, uint uidValidity, uint lastUid)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("tenantId / address rỗng");
        using var c = _db.Open();
        c.Execute(@"
MERGE dbo.MailSyncState AS T
USING (SELECT @t AS TenantId, @a AS Address) AS S
   ON T.TenantId = S.TenantId AND T.Address = S.Address
WHEN MATCHED THEN UPDATE SET UidValidity=@uv, LastUid=@lu, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Address, UidValidity, LastUid, UpdatedAt)
                       VALUES (@t, @a, @uv, @lu, SYSUTCDATETIME());",
            new { t = tenantId, a = address, uv = (long)uidValidity, lu = (long)lastUid });
    }

    /// <summary>Dapper row mapper — bind theo NAME, không phải ordinal.</summary>
    private sealed class MailSyncRow
    {
        public long UidValidity { get; set; }
        public long LastUid { get; set; }
    }
}
