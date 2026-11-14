using Dapper;
using TourkitAiProxy.Infrastructure.Db;

namespace TourkitAiProxy.Infrastructure.Mcp;

/// <summary>Client OAuth đăng ký động (DCR). Client CIMD không lưu ở đây, chỉ đệm trong RAM.</summary>
public sealed record McpOAuthClient(string ClientId, string ClientName, string RedirectUrisJson, DateTime CreatedUtc, string? CreatedIp);

/// <summary>Mã uỷ quyền dùng một lần. Chỉ giữ bản băm của mã.</summary>
public sealed record McpOAuthCode(string CodeHash, string ClientId, string RedirectUri, string CodeChallenge, string Resource,
    string Scope, string TenantId, string Username, string SessionId, DateTime ExpiresUtc, DateTime? UsedUtc);

/// <summary>Một lần người dùng cho phép một ứng dụng AI. Cố ý KHÔNG chứa băm token ra ngoài kho.</summary>
public sealed record McpOAuthGrant(long Id, string ClientId, string ClientName, string TenantId, string Username, string SessionId,
    string Resource, string Scope, DateTime AccessExpiresUtc, DateTime RefreshExpiresUtc, DateTime CreatedUtc,
    DateTime? LastUsedUtc, DateTime? RevokedUtc);

/// <summary>Dữ liệu tạo grant. Mọi token ở dạng băm SHA-256 hex (OAuthRules.Hash).</summary>
public sealed record McpOAuthNewGrant(string ClientId, string ClientName, string TenantId, string Username, string SessionId,
    string Resource, string Scope, string AccessHash, DateTime AccessExpiresUtc, string RefreshHash, DateTime RefreshExpiresUtc);

public enum McpRefreshStatus { NotFound, Rotated, Reused }

/// <summary>Kết quả xoay refresh. <c>Reused</c> kèm grant để chỗ gọi thu hồi (RevokeGrantAsync).</summary>
public sealed record McpRefreshResult(McpRefreshStatus Status, McpOAuthGrant? Grant);

/// <summary>Kho OAuth cho MCP. Đứng sau giao diện để dịch vụ OAuth test được bằng kho giả.</summary>
public interface IMcpOAuthStore
{
    Task CreateClientAsync(McpOAuthClient client, CancellationToken ct);
    Task<McpOAuthClient?> GetClientAsync(string clientId, CancellationToken ct);
    Task<int> CountClientsByIpSinceAsync(string ip, DateTime sinceUtc, CancellationToken ct);

    Task SaveCodeAsync(McpOAuthCode code, CancellationToken ct);
    /// <summary>Đổi mã: trả dòng đúng một lần (còn hạn, chưa dùng); lần hai trả null.</summary>
    Task<McpOAuthCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct);

    Task<long> CreateGrantAsync(McpOAuthNewGrant grant, CancellationToken ct);
    /// <summary>Tìm grant còn hiệu lực theo bản băm access. CHỈ lọc RevokedUtc, KHÔNG kiểm AccessExpiresUtc: chỗ gọi PHẢI tự kiểm hạn.</summary>
    Task<McpOAuthGrant?> FindGrantByAccessAsync(string accessHash, CancellationToken ct);
    /// <summary>Xoay refresh nguyên tử. Khi trả <c>Reused</c> chỗ gọi PHẢI thu hồi grant (RevokeGrantAsync) — kho không tự thu hồi.</summary>
    Task<McpRefreshResult> RotateRefreshAsync(string oldRefreshHash, string newAccessHash, DateTime newAccessExpiresUtc,
        string newRefreshHash, DateTime newRefreshExpiresUtc, CancellationToken ct);
    Task<bool> RevokeGrantAsync(long id, string tenant, string username, CancellationToken ct);
    Task<IReadOnlyList<McpOAuthGrant>> ListGrantsAsync(string tenant, string username, CancellationToken ct);
    Task TouchGrantAsync(long id, CancellationToken ct);
}

/// <summary>
/// Dapper repo cho dbo.McpOAuthClients/Codes/Grants. Giống McpTokenRepository: KHÔNG fallback in-memory —
/// SQL sập thì ném lỗi (đóng cửa), vì kho trong RAM sẽ chấp nhận mã/token đã thu hồi ở bản chạy khác.
/// Kho chỉ nhận BẢN BĂM, không bao giờ thấy mã/token thô.
/// </summary>
public class McpOAuthRepository : IMcpOAuthStore
{
    private readonly TourkitAiDb _db;
    public McpOAuthRepository(TourkitAiDb db) { _db = db; }

    // Dapper đọc datetime2 thành Kind=Unspecified; mọi giá trị trong bảng là UTC nên gắn Kind=Utc để
    // ToString("o") ra "Z" (xem docs/datetime-convention.md).
    // Cột băm là CHAR(64): gửi kiểu ANSI cố định 64 để SQL không CONVERT_IMPLICIT (nvarchar > char làm mất Index Seek).
    private static DbString H(string h) => new() { Value = h, IsAnsi = true, IsFixedLength = true, Length = 64 };

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d.HasValue ? Utc(d.Value) : null;
    private static McpOAuthGrant? Fix(McpOAuthGrant? g) => g == null ? null : g with
    {
        AccessExpiresUtc = Utc(g.AccessExpiresUtc), RefreshExpiresUtc = Utc(g.RefreshExpiresUtc),
        CreatedUtc = Utc(g.CreatedUtc), LastUsedUtc = Utc(g.LastUsedUtc), RevokedUtc = Utc(g.RevokedUtc)
    };

    private const string GrantCols = "Id, ClientId, ClientName, TenantId, Username, SessionId, Resource, Scope, AccessExpiresUtc, RefreshExpiresUtc, CreatedUtc, LastUsedUtc, RevokedUtc";
    private const string GrantColsInserted = "inserted.Id, inserted.ClientId, inserted.ClientName, inserted.TenantId, inserted.Username, inserted.SessionId, inserted.Resource, inserted.Scope, inserted.AccessExpiresUtc, inserted.RefreshExpiresUtc, inserted.CreatedUtc, inserted.LastUsedUtc, inserted.RevokedUtc";

    public async Task CreateClientAsync(McpOAuthClient client, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.McpOAuthClients (ClientId, ClientName, RedirectUrisJson, CreatedUtc, CreatedIp)
VALUES (@ClientId, @ClientName, @RedirectUrisJson, @now, @CreatedIp);";
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, new { client.ClientId, client.ClientName, client.RedirectUrisJson, client.CreatedIp, now = DateTime.UtcNow }, cancellationToken: ct));
    }

    public async Task<McpOAuthClient?> GetClientAsync(string clientId, CancellationToken ct)
    {
        const string sql = @"
SELECT ClientId, ClientName, RedirectUrisJson, CreatedUtc, CreatedIp FROM dbo.McpOAuthClients WHERE ClientId = @clientId;";
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<McpOAuthClient>(new CommandDefinition(sql, new { clientId }, cancellationToken: ct));
        return r == null ? null : r with { CreatedUtc = Utc(r.CreatedUtc) };
    }

    public async Task<int> CountClientsByIpSinceAsync(string ip, DateTime sinceUtc, CancellationToken ct)
    {
        // Chống tràn bảng client: đếm số lần đăng ký từ một IP trong cửa sổ gần đây (chỉ mục CreatedIp+CreatedUtc).
        const string sql = @"
SELECT COUNT(*) FROM dbo.McpOAuthClients WHERE CreatedIp = @ip AND CreatedUtc >= @sinceUtc;";
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { ip, sinceUtc }, cancellationToken: ct));
    }

    public async Task SaveCodeAsync(McpOAuthCode code, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.McpOAuthCodes (CodeHash, ClientId, RedirectUri, CodeChallenge, Resource, Scope, TenantId, Username, SessionId, ExpiresUtc)
VALUES (@CodeHash, @ClientId, @RedirectUri, @CodeChallenge, @Resource, @Scope, @TenantId, @Username, @SessionId, @ExpiresUtc);";
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, new { CodeHash = H(code.CodeHash), code.ClientId, code.RedirectUri, code.CodeChallenge, code.Resource, code.Scope, code.TenantId, code.Username, code.SessionId, code.ExpiresUtc }, cancellationToken: ct));
    }

    public async Task<McpOAuthCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct)
    {
        // -- CONSUME: MỘT câu UPDATE…OUTPUT nguyên tử. Hai request đổi cùng một mã cùng lúc: chỉ một bên
        // thấy UsedUtc IS NULL và nhận dòng; bên kia 0 dòng → null. Không có bước "đọc rồi mới đánh dấu".
        const string sql = @"
UPDATE dbo.McpOAuthCodes SET UsedUtc = SYSUTCDATETIME()
OUTPUT inserted.CodeHash, inserted.ClientId, inserted.RedirectUri, inserted.CodeChallenge, inserted.Resource,
       inserted.Scope, inserted.TenantId, inserted.Username, inserted.SessionId, inserted.ExpiresUtc, inserted.UsedUtc
 WHERE CodeHash = @codeHash AND UsedUtc IS NULL AND ExpiresUtc > SYSUTCDATETIME();";
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<McpOAuthCode>(new CommandDefinition(sql, new { codeHash = H(codeHash) }, cancellationToken: ct));
        return r == null ? null : r with { ExpiresUtc = Utc(r.ExpiresUtc), UsedUtc = Utc(r.UsedUtc) };
    }

    public async Task<long> CreateGrantAsync(McpOAuthNewGrant g, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.McpOAuthGrants (ClientId, ClientName, TenantId, Username, SessionId, Resource, Scope,
                                AccessHash, AccessExpiresUtc, RefreshHash, RefreshExpiresUtc, CreatedUtc)
OUTPUT INSERTED.Id
VALUES (@ClientId, @ClientName, @TenantId, @Username, @SessionId, @Resource, @Scope,
        @AccessHash, @AccessExpiresUtc, @RefreshHash, @RefreshExpiresUtc, @now);";
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(sql,
            new { g.ClientId, g.ClientName, g.TenantId, g.Username, g.SessionId, g.Resource, g.Scope, AccessHash = H(g.AccessHash), g.AccessExpiresUtc, RefreshHash = H(g.RefreshHash), g.RefreshExpiresUtc, now = DateTime.UtcNow },
            cancellationToken: ct));
    }

    public async Task<McpOAuthGrant?> FindGrantByAccessAsync(string accessHash, CancellationToken ct)
    {
        // -- FIND: hot path của mọi request /mcp dùng token OAuth; AccessHash có chỉ mục UNIQUE lọc → Index Seek.
        // Hạn dùng (AccessExpiresUtc) do chỗ gọi kiểm, vì cần phân biệt "hết hạn" với "không có" khi trả lỗi.
        const string sql = "SELECT " + GrantCols + @"
  FROM dbo.McpOAuthGrants WHERE AccessHash = @accessHash AND RevokedUtc IS NULL;";
        await using var c = await _db.OpenAsync(ct);
        return Fix(await c.QuerySingleOrDefaultAsync<McpOAuthGrant>(new CommandDefinition(sql, new { accessHash = H(accessHash) }, cancellationToken: ct)));
    }

    public async Task<McpRefreshResult> RotateRefreshAsync(string oldRefreshHash, string newAccessHash, DateTime newAccessExpiresUtc,
        string newRefreshHash, DateTime newRefreshExpiresUtc, CancellationToken ct)
    {
        // -- ROTATE: nguyên tử trong MỘT câu UPDATE…OUTPUT. Hai request xoay cùng refresh cùng lúc: chỉ một
        // bên khớp RefreshHash hiện tại; bên kia 0 dòng rồi rơi xuống nhánh dò PrevRefreshHash → "tái sử dụng".
        const string rotate = @"
UPDATE dbo.McpOAuthGrants
   SET PrevRefreshHash = RefreshHash, RefreshHash = @newRefreshHash, RefreshExpiresUtc = @newRefreshExpiresUtc,
       AccessHash = @newAccessHash, AccessExpiresUtc = @newAccessExpiresUtc, LastUsedUtc = SYSUTCDATETIME()
OUTPUT " + GrantColsInserted + @"
 WHERE RefreshHash = @oldRefreshHash AND RevokedUtc IS NULL AND RefreshExpiresUtc > SYSUTCDATETIME();";
        const string reuse = "SELECT " + GrantCols + @"
  FROM dbo.McpOAuthGrants WHERE PrevRefreshHash = @oldRefreshHash AND RevokedUtc IS NULL;";
        await using var c = await _db.OpenAsync(ct);
        var p = new { oldRefreshHash = H(oldRefreshHash), newAccessHash = H(newAccessHash), newAccessExpiresUtc, newRefreshHash = H(newRefreshHash), newRefreshExpiresUtc };
        var rotated = Fix(await c.QuerySingleOrDefaultAsync<McpOAuthGrant>(new CommandDefinition(rotate, p, cancellationToken: ct)));
        if (rotated != null) return new McpRefreshResult(McpRefreshStatus.Rotated, rotated);
        var reused = Fix(await c.QuerySingleOrDefaultAsync<McpOAuthGrant>(new CommandDefinition(reuse, new { oldRefreshHash = H(oldRefreshHash) }, cancellationToken: ct)));
        return reused != null ? new McpRefreshResult(McpRefreshStatus.Reused, reused) : new McpRefreshResult(McpRefreshStatus.NotFound, null);
    }

    public async Task<bool> RevokeGrantAsync(long id, string tenant, string username, CancellationToken ct)
    {
        // -- REVOKE: thu hồi mềm; grant của người khác / đã thu hồi → 0 dòng.
        const string sql = @"
UPDATE dbo.McpOAuthGrants SET RevokedUtc = @now
 WHERE Id = @id AND TenantId = @tenant AND Username = @username AND RevokedUtc IS NULL;";
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(sql, new { id, tenant, username, now = DateTime.UtcNow }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<McpOAuthGrant>> ListGrantsAsync(string tenant, string username, CancellationToken ct)
    {
        // -- LIST: chỉ grant của chính người gọi; gồm cả đã thu hồi để thấy lịch sử.
        const string sql = "SELECT " + GrantCols + @"
  FROM dbo.McpOAuthGrants WHERE TenantId = @tenant AND Username = @username ORDER BY Id DESC;";
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<McpOAuthGrant>(new CommandDefinition(sql, new { tenant, username }, cancellationToken: ct)))
            .Select(g => Fix(g)!).ToList();
    }

    public async Task TouchGrantAsync(long id, CancellationToken ct)
    {
        // Tối đa 1 lần/phút/grant ngay trong SQL: request dồn dập không thành ghi dồn dập.
        const string sql = @"
UPDATE dbo.McpOAuthGrants SET LastUsedUtc = @now
 WHERE Id = @id AND (LastUsedUtc IS NULL OR LastUsedUtc < DATEADD(minute, -1, @now));";
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, new { id, now = DateTime.UtcNow }, cancellationToken: ct));
    }
}
