using System.Collections.Concurrent;
using System.Text.Json;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.TourKit;

/// 1 phiên đăng nhập TourKit. Giữ credentials đã giải mã (in-memory) để tự re-login khi JWT hết hạn.
public class TkSession
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }

    public string Jwt { get; set; } = "";
    public string? FullName { get; set; }
    public string? CompanyName { get; set; }
    public DateTime JwtExpiresAt { get; set; }   // soft TTL — re-login khi quá hạn
    public DateTime LastUsed { get; set; }

    // BỘ NHỚ CHAT — load/save cùng session xuống đĩa.
    public SessionChatMemory ChatMemory { get; set; } = SessionChatMemory.Empty();
}

/// <summary>
/// Lưu phiên TourKit. JWT KHÔNG ra client; client chỉ giữ sessionId. Tự re-login bằng credentials
/// trong phiên khi JWT soft-expire hoặc 401.
///
/// **Persist xuống đĩa** (data/tk-sessions.json, gitignored) để phiên SỐNG SÓT qua restart —
/// không bắt user đăng nhập lại mỗi lần deploy/khởi động lại. Mật khẩu được mã hóa bằng Crypton
/// (KHÔNG lưu plaintext); JWT không persist (sẽ tự re-login khi dùng).
/// </summary>
public class TkSessionStore
{
    private readonly TourKitApiClient _api;
    private readonly ILogger<TkSessionStore> _log;
    private readonly ConcurrentDictionary<string, TkSession> _sessions = new();
    private readonly string _path;
    private readonly object _ioLock = new();

    // JWT TourKit sống vài giờ; refresh chủ động sau 50 phút cho an toàn (re-login rẻ).
    private static readonly TimeSpan SoftTtl = TimeSpan.FromMinutes(50);
    // Dọn phiên không dùng quá 30 ngày.
    private static readonly TimeSpan IdleTtl = TimeSpan.FromDays(30);

    // ChatMemory nullable để tương thích ngược: session cũ không có trường này → Empty().
    private record Persisted(string Id, string TenantId, string Username, string EncPassword,
        string? FullName, string? CompanyName, string LastUsedIso,
        SessionChatMemory? ChatMemory = null);

    public TkSessionStore(TourKitApiClient api, IWebHostEnvironment env, ILogger<TkSessionStore> log)
    {
        _api = api; _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "tk-sessions.json");
        Load();
    }

    /// Login lần đầu từ credentials đã giải mã → tạo phiên, persist, trả về phiên.
    public async Task<TkSession> CreateAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        PruneIdle();
        var login = await _api.LoginAsync(tenantId, username, password, ct);

        var session = new TkSession
        {
            Id          = Guid.NewGuid().ToString("N"),
            TenantId    = tenantId,
            Username    = username,
            Password    = password,
            Jwt         = login.Token,
            FullName    = login.FullName,
            CompanyName = login.CompanyName,
            JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl),
            LastUsed    = DateTime.UtcNow
        };
        _sessions[session.Id] = session;
        Persist();
        _log.LogInformation("TourKit session {Id} tạo cho tenant={Tenant} user={User}", session.Id, tenantId, username);
        return session;
    }

    public TkSession? Get(string? sessionId)
        => string.IsNullOrEmpty(sessionId) ? null : (_sessions.TryGetValue(sessionId, out var s) ? s : null);

    /// JWT còn hạn (soft TTL); tự re-login nếu hết. Throw nếu phiên không tồn tại.
    public async Task<string> GetValidJwtAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        s.LastUsed = DateTime.UtcNow;
        if (string.IsNullOrEmpty(s.Jwt) || DateTime.UtcNow >= s.JwtExpiresAt)
            await ReloginAsync(s, ct);
        return s.Jwt;
    }

    /// Buộc re-login (gọi khi TourKit trả 401 giữa chừng).
    public async Task<string> ForceReloginAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId) ?? throw new TourKitApiException("Phiên không tồn tại — vui lòng đăng nhập lại", 401);
        await ReloginAsync(s, ct);
        return s.Jwt;
    }

    private async Task ReloginAsync(TkSession s, CancellationToken ct)
    {
        var login = await _api.LoginAsync(s.TenantId, s.Username, s.Password, ct);
        s.Jwt = login.Token;
        s.FullName = login.FullName;
        s.CompanyName = login.CompanyName;
        s.JwtExpiresAt = DateTime.UtcNow.Add(SoftTtl);
        _log.LogInformation("TourKit session {Id} re-login (JWT refreshed)", s.Id);
    }

    private void PruneIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTtl;
        var removed = false;
        foreach (var kv in _sessions)
            if (kv.Value.LastUsed < cutoff)
                removed |= _sessions.TryRemove(kv.Key, out _);
        if (removed) Persist();
    }

    // ─── Persistence (mật khẩu mã hóa Crypton, KHÔNG lưu JWT) ──────────────────────
    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var list = JsonSerializer.Deserialize<List<Persisted>>(json, opts) ?? new();
            foreach (var p in list)
            {
                var pwd = Crypton.Decrypt(p.EncPassword);
                if (string.IsNullOrEmpty(pwd)) continue;
                _sessions[p.Id] = new TkSession
                {
                    Id = p.Id, TenantId = p.TenantId, Username = p.Username, Password = pwd,
                    FullName = p.FullName, CompanyName = p.CompanyName,
                    Jwt = "", JwtExpiresAt = DateTime.MinValue,    // ép re-login lần dùng đầu
                    LastUsed = DateTime.TryParse(p.LastUsedIso, out var d) ? d.ToUniversalTime() : DateTime.UtcNow,
                    // Tương thích ngược: session cũ không có ChatMemory → dùng Empty()
                    ChatMemory = p.ChatMemory ?? SessionChatMemory.Empty()
                };
            }
            _log.LogInformation("Loaded {N} TourKit sessions từ đĩa", _sessions.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Load tk-sessions.json fail — bỏ qua"); }
    }

    private static readonly JsonSerializerOptions _persistOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private void Persist()
    {
        try
        {
            var list = _sessions.Values.Select(s => new Persisted(
                s.Id, s.TenantId, s.Username, Crypton.Encrypt(s.Password),
                s.FullName, s.CompanyName, s.LastUsed.ToString("o"),
                s.ChatMemory)).ToList();
            lock (_ioLock)
                File.WriteAllText(_path, JsonSerializer.Serialize(list, _persistOpts));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Persist tk-sessions.json fail"); }
    }

    // ─── Chat memory helpers ────────────────────────────────────────────────────

    /// Lấy bộ nhớ chat của phiên. Trả null nếu không tìm thấy phiên.
    public SessionChatMemory? GetMemory(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? s.ChatMemory : null;

    /// Cập nhật bộ nhớ chat, tự gán LastUpdated = UtcNow, persist xuống đĩa.
    public void UpdateMemory(string sessionId, SessionChatMemory memory)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.ChatMemory = memory with { LastUpdated = DateTime.UtcNow };
            Persist();
        }
    }

    /// Xóa bộ nhớ chat về Empty (khi user yêu cầu reset hội thoại).
    public void ClearMemory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.ChatMemory = SessionChatMemory.Empty();
            Persist();
        }
    }
}
