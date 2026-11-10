using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Kho khoá MCP trong bộ nhớ, đúng giao diện — dùng cho test của middleware (Task 3) và test này.</summary>
public sealed class FakeMcpTokenStore : IMcpTokenStore
{
    private readonly List<McpTokenRow> _rows = new();
    private readonly Dictionary<long, string> _hashes = new();
    private long _next = 1;

    public Task<(long Id, string Token)> CreateAsync(string tenant, string username, string sessionId, string label, CancellationToken ct)
    {
        var token = McpTokens.New();
        var id = _next++;
        _rows.Add(new McpTokenRow(id, tenant, username, sessionId, label, DateTime.UtcNow, null, null));
        _hashes[id] = McpTokens.Hash(token);
        return Task.FromResult((id, token));
    }

    public Task<McpTokenRow?> FindActiveAsync(string tokenHash, CancellationToken ct)
        => Task.FromResult(_rows.FirstOrDefault(r => r.RevokedUtc == null && _hashes[r.Id] == tokenHash));

    public Task<IReadOnlyList<McpTokenRow>> ListAsync(string tenant, string username, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpTokenRow>>(
            _rows.Where(r => r.TenantId == tenant && r.Username == username).OrderByDescending(r => r.Id).ToList());

    public Task<bool> RevokeAsync(long id, string tenant, string username, CancellationToken ct)
    {
        var i = _rows.FindIndex(r => r.Id == id && r.TenantId == tenant && r.Username == username && r.RevokedUtc == null);
        if (i < 0) return Task.FromResult(false);
        _rows[i] = _rows[i] with { RevokedUtc = DateTime.UtcNow };
        return Task.FromResult(true);
    }

    public Task TouchAsync(long id, CancellationToken ct)
    {
        var i = _rows.FindIndex(r => r.Id == id);
        if (i >= 0) _rows[i] = _rows[i] with { LastUsedUtc = DateTime.UtcNow };
        return Task.CompletedTask;
    }
}
