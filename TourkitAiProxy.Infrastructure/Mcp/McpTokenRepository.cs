using Dapper;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Db;

namespace TourkitAiProxy.Infrastructure.Mcp;

/// <summary>Kho khoá MCP. Đứng sau giao diện để middleware xác thực test được bằng kho giả.</summary>
public interface IMcpTokenStore
{
    Task<(long Id, string Token)> CreateAsync(string tenant, string username, string sessionId, string label, CancellationToken ct);
    Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<McpTokenRow>> ListAsync(string tenant, string username, CancellationToken ct);
    Task<bool> RevokeAsync(long id, string tenant, string username, CancellationToken ct);
    Task TouchAsync(long id, CancellationToken ct);
}

/// <summary>
/// Dapper repo cho dbo.McpTokens. <b>Khác WidgetTokenRepository ở chỗ KHÔNG có fallback in-memory:</b>
/// SQL sập thì ném lỗi và xác thực thất bại (đóng cửa), vì một kho khoá trong RAM sẽ nhận khoá đã
/// thu hồi ở bản chạy khác.
/// </summary>
public class McpTokenRepository : IMcpTokenStore
{
    private readonly TourkitAiDb _db;
    public McpTokenRepository(TourkitAiDb db) { _db = db; }

    public async Task<(long Id, string Token)> CreateAsync(string tenant, string username, string sessionId, string label, CancellationToken ct)
    {
        var token = McpTokens.New();
        const string sql = @"
INSERT INTO dbo.McpTokens (TokenHash, TenantId, Username, SessionId, Label, CreatedUtc)
OUTPUT INSERTED.Id
VALUES (@hash, @tenant, @username, @sessionId, @label, @now);";
        await using var c = await _db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(sql,
            new { hash = McpTokens.Hash(token), tenant, username, sessionId, label, now = DateTime.UtcNow }, cancellationToken: ct));
        return (id, token);
    }

    public async Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct)
    {
        // -- FIND: hot path của mọi request /mcp; TokenHash có chỉ mục UNIQUE nên là Index Seek.
        const string sql = @"
SELECT Id, TenantId, Username, SessionId, Label, CreatedUtc, LastUsedUtc, RevokedUtc
  FROM dbo.McpTokens WHERE TokenHash = @tokenHash AND RevokedUtc IS NULL;";
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<McpTokenRow>(new CommandDefinition(sql, new { tokenHash }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<McpTokenRow>> ListAsync(string tenant, string username, CancellationToken ct)
    {
        // -- LIST: chỉ khoá của chính người gọi (tenant + username); gồm cả khoá đã thu hồi để họ thấy lịch sử.
        const string sql = @"
SELECT Id, TenantId, Username, SessionId, Label, CreatedUtc, LastUsedUtc, RevokedUtc
  FROM dbo.McpTokens WHERE TenantId = @tenant AND Username = @username ORDER BY Id DESC;";
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<McpTokenRow>(new CommandDefinition(sql, new { tenant, username }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> RevokeAsync(long id, string tenant, string username, CancellationToken ct)
    {
        // -- REVOKE: thu hồi mềm (giữ dòng để còn lịch sử). Khoá người khác / đã thu hồi → 0 dòng → 404.
        const string sql = @"
UPDATE dbo.McpTokens SET RevokedUtc = @now
 WHERE Id = @id AND TenantId = @tenant AND Username = @username AND RevokedUtc IS NULL;";
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(sql, new { id, tenant, username, now = DateTime.UtcNow }, cancellationToken: ct)) > 0;
    }

    public async Task TouchAsync(long id, CancellationToken ct)
    {
        // Tối đa 1 lần/phút/khoá ngay trong SQL: request dồn dập không biến thành ghi dồn dập.
        const string sql = @"
UPDATE dbo.McpTokens SET LastUsedUtc = @now
 WHERE Id = @id AND (LastUsedUtc IS NULL OR LastUsedUtc < DATEADD(minute, -1, @now));";
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, new { id, now = DateTime.UtcNow }, cancellationToken: ct));
    }
}
