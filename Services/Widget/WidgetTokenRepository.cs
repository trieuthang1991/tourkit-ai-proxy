using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Widget;

/// <summary>
/// Dapper repo cho dbo.WidgetTokens + fallback in-memory sticky 60s (cùng pattern QuotaOrderRepository).
/// Token format: `trav_` + 32 hex char (16 random bytes). Đủ entropy chống brute-force, dễ nhận dạng.
/// </summary>
public class WidgetTokenRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<WidgetTokenRepository> _log;

    private readonly ConcurrentDictionary<string, WidgetToken> _mem = new();
    private readonly object _stickyLock = new();
    private DateTime _sqlDownUntil = DateTime.MinValue;

    public WidgetTokenRepository(TourkitAiDb db, ILogger<WidgetTokenRepository> log)
    { _db = db; _log = log; }

    private bool ShouldUseSql()
    {
        lock (_stickyLock) return DateTime.UtcNow > _sqlDownUntil;
    }

    private void MarkSqlDown(Exception ex)
    {
        lock (_stickyLock) _sqlDownUntil = DateTime.UtcNow.AddSeconds(60);
        _log.LogWarning(ex, "[WidgetTokens] SQL không kết nối được — fallback in-memory 60s.");
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return "trav_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ─── Insert ─────────────────────────────────────────────────────────────────
    public async Task InsertAsync(WidgetToken row, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
INSERT INTO dbo.WidgetTokens
    (Token, TenantId, BotName, Greeting, SystemPrompt, Color, Enabled,
     AllowedOrigins, TotalMessages, CreatedAt, UpdatedAt,
     TourKitSessionId, AllowedTools, CacheTtlSeconds)
VALUES
    (@Token, @TenantId, @BotName, @Greeting, @SystemPrompt, @Color, @Enabled,
     @AllowedOrigins, @TotalMessages, @CreatedAt, @UpdatedAt,
     @TourKitSessionId, @AllowedTools, @CacheTtlSeconds);";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, row);
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        _mem[row.Token] = row;
    }

    // ─── GetByToken (hot path: widget.js mount + chat) ──────────────────────────
    public async Task<WidgetToken?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = "SELECT * FROM dbo.WidgetTokens WHERE Token = @token;";
                await using var c = await _db.OpenAsync(ct);
                return await c.QuerySingleOrDefaultAsync<WidgetToken>(sql, new { token });
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        return _mem.GetValueOrDefault(token);
    }

    // ─── ListByTenant (admin) ───────────────────────────────────────────────────
    public async Task<List<WidgetToken>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
SELECT * FROM dbo.WidgetTokens WHERE TenantId = @tenantId ORDER BY CreatedAt DESC;";
                await using var c = await _db.OpenAsync(ct);
                var rows = await c.QueryAsync<WidgetToken>(sql, new { tenantId });
                return rows.ToList();
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        return _mem.Values.Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt).ToList();
    }

    // ─── Update ─────────────────────────────────────────────────────────────────
    public async Task UpdateAsync(WidgetToken row, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
UPDATE dbo.WidgetTokens
   SET BotName = @BotName, Greeting = @Greeting, SystemPrompt = @SystemPrompt,
       Color = @Color, Enabled = @Enabled, AllowedOrigins = @AllowedOrigins,
       UpdatedAt = @UpdatedAt,
       TourKitSessionId = @TourKitSessionId,
       AllowedTools = @AllowedTools,
       CacheTtlSeconds = @CacheTtlSeconds
 WHERE Token = @Token;";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, row);
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        _mem[row.Token] = row;
    }

    // ─── Delete ─────────────────────────────────────────────────────────────────
    public async Task DeleteAsync(string token, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = "DELETE FROM dbo.WidgetTokens WHERE Token = @token;";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, new { token });
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        _mem.TryRemove(token, out _);
    }

    // ─── IncrementMessageCount ──────────────────────────────────────────────────
    // Lightweight counter sau mỗi chat thành công. Failure không block reply → fire-and-forget OK.
    public async Task IncrementMessagesAsync(string token, CancellationToken ct = default)
    {
        if (ShouldUseSql())
        {
            try
            {
                const string sql = @"
UPDATE dbo.WidgetTokens
   SET TotalMessages = TotalMessages + 1, UpdatedAt = SYSUTCDATETIME()
 WHERE Token = @token;";
                await using var c = await _db.OpenAsync(ct);
                await c.ExecuteAsync(sql, new { token });
                return;
            }
            catch (SqlException ex) { MarkSqlDown(ex); }
        }
        if (_mem.TryGetValue(token, out var row))
            _mem[token] = row with { TotalMessages = row.TotalMessages + 1, UpdatedAt = DateTime.UtcNow };
    }
}
