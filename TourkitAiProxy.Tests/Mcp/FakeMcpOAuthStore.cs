using TourkitAiProxy.Infrastructure.Mcp;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Kho OAuth MCP trong bộ nhớ, đúng giao diện và đúng ngữ nghĩa SQL (dùng một lần, xoay refresh, kẹp chủ sở hữu).</summary>
public sealed class FakeMcpOAuthStore : IMcpOAuthStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (McpOAuthClient C, string? Ip)> _clients = new();
    private readonly Dictionary<string, McpOAuthCode> _codes = new();
    private readonly List<G> _grants = new();
    private long _next = 1;

    /// <summary>Số mã uỷ quyền đã lưu — để test khẳng định không cấp mã.</summary>
    public int CodeCount { get { lock (_lock) return _codes.Count; } }

    private sealed class G
    {
        public long Id; public McpOAuthNewGrant N = null!; public string? Access, Refresh, Prev;
        public DateTime AccessExp, RefreshExp, Created; public DateTime? LastUsed, Revoked;
        public McpOAuthGrant View() => new(Id, N.ClientId, N.ClientName, N.TenantId, N.Username, N.SessionId, N.Resource, N.Scope,
            AccessExp, RefreshExp, Created, LastUsed, Revoked);
    }

    public Task CreateClientAsync(McpOAuthClient client, CancellationToken ct)
    { lock (_lock) { _clients[client.ClientId] = (client with { CreatedUtc = DateTime.UtcNow }, client.CreatedIp); } return Task.CompletedTask; }

    public Task<McpOAuthClient?> GetClientAsync(string clientId, CancellationToken ct)
    { lock (_lock) return Task.FromResult(_clients.TryGetValue(clientId, out var v) ? v.C : null); }

    public Task<int> CountClientsByIpSinceAsync(string ip, DateTime sinceUtc, CancellationToken ct)
    { lock (_lock) return Task.FromResult(_clients.Values.Count(v => v.Ip == ip && v.C.CreatedUtc >= sinceUtc)); }

    public Task SaveCodeAsync(McpOAuthCode code, CancellationToken ct)
    { lock (_lock) { _codes[code.CodeHash] = code; } return Task.CompletedTask; }

    public Task<McpOAuthCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_codes.TryGetValue(codeHash, out var c) || c.UsedUtc != null || c.ExpiresUtc <= DateTime.UtcNow) return Task.FromResult<McpOAuthCode?>(null);
            _codes[codeHash] = c with { UsedUtc = DateTime.UtcNow };
            return Task.FromResult<McpOAuthCode?>(c with { UsedUtc = DateTime.UtcNow });
        }
    }

    public Task<long> CreateGrantAsync(McpOAuthNewGrant n, CancellationToken ct)
    {
        lock (_lock)
        {
            var g = new G { Id = _next++, N = n, Access = n.AccessHash, Refresh = n.RefreshHash, AccessExp = n.AccessExpiresUtc, RefreshExp = n.RefreshExpiresUtc, Created = DateTime.UtcNow };
            _grants.Add(g);
            return Task.FromResult(g.Id);
        }
    }

    public Task<McpOAuthGrant?> FindGrantByAccessAsync(string accessHash, CancellationToken ct)
    { lock (_lock) return Task.FromResult(_grants.FirstOrDefault(g => g.Access == accessHash && g.Revoked == null)?.View()); }

    public Task<McpRefreshResult> RotateRefreshAsync(string oldRefreshHash, string newAccessHash, DateTime newAccessExpiresUtc,
        string newRefreshHash, DateTime newRefreshExpiresUtc, CancellationToken ct)
    {
        lock (_lock)
        {
            var g = _grants.FirstOrDefault(x => x.Refresh == oldRefreshHash && x.Revoked == null && x.RefreshExp > DateTime.UtcNow);
            if (g != null)
            {
                g.Prev = g.Refresh; g.Refresh = newRefreshHash; g.RefreshExp = newRefreshExpiresUtc;
                g.Access = newAccessHash; g.AccessExp = newAccessExpiresUtc; g.LastUsed = DateTime.UtcNow;
                return Task.FromResult(new McpRefreshResult(McpRefreshStatus.Rotated, g.View()));
            }
            var r = _grants.FirstOrDefault(x => x.Prev == oldRefreshHash && x.Revoked == null);
            return Task.FromResult(r != null ? new McpRefreshResult(McpRefreshStatus.Reused, r.View()) : new McpRefreshResult(McpRefreshStatus.NotFound, null));
        }
    }

    public Task<bool> RevokeGrantAsync(long id, string tenant, string username, CancellationToken ct)
    {
        lock (_lock)
        {
            var g = _grants.FirstOrDefault(x => x.Id == id && x.N.TenantId == tenant && x.N.Username == username && x.Revoked == null);
            if (g == null) return Task.FromResult(false);
            g.Revoked = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<McpOAuthGrant>> ListGrantsAsync(string tenant, string username, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<McpOAuthGrant>>(_grants.Where(g => g.N.TenantId == tenant && g.N.Username == username)
                .OrderByDescending(g => g.Id).Select(g => g.View()).ToList());
    }

    public Task TouchGrantAsync(long id, CancellationToken ct)
    { lock (_lock) { var g = _grants.FirstOrDefault(x => x.Id == id); if (g != null) g.LastUsed = DateTime.UtcNow; } return Task.CompletedTask; }
}
