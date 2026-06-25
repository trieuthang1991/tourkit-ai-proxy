using System.Collections.Concurrent;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// In-mem session store cho admin. Key = random GUID. Idle timeout 12h (check ở Get).
/// KHÔNG persist — admin login lại sau restart (admin pool nhỏ, restart hiếm).
/// </summary>
public class AdminSessionStore
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new();

    public AdminSession Create(string username)
    {
        var token = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var s = new AdminSession(token, username, now, now);
        _sessions[token] = s;
        return s;
    }

    /// Lookup + touch LastAccessAt. Expired → remove + null.
    public AdminSession? Get(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var s)) return null;
        if (DateTime.UtcNow - s.LastAccessAt > IdleTimeout)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        var touched = s with { LastAccessAt = DateTime.UtcNow };
        _sessions[token] = touched;
        return touched;
    }

    public bool Remove(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return _sessions.TryRemove(token, out _);
    }

    public DateTime ExpiresAt(AdminSession s) => s.LastAccessAt + IdleTimeout;
}

public sealed record AdminSession(
    string Token,
    string Username,
    DateTime CreatedAt,
    DateTime LastAccessAt);
