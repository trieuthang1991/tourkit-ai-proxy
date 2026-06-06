using System.Text.Json;

namespace TourkitAiProxy.Services.Mail;

/// State đồng bộ IMAP theo UID — để KÉO INCREMENTAL (chỉ email mới hơn lần trước) → không sót.
/// Lưu data/mail-sync.json: per-address { uidValidity, lastUid }. UidValidity đổi (server reset)
/// → coi như mới hoàn toàn, kéo lại từ đầu (newest N).
public class MailSyncStore
{
    public record SyncState(uint UidValidity, uint LastUid);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, SyncState> _map;
    private readonly ILogger<MailSyncStore> _log;
    private static readonly JsonSerializerOptions _opts = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MailSyncStore(IWebHostEnvironment env, ILogger<MailSyncStore> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mail-sync.json");
        _map = new();
        if (File.Exists(_path))
        {
            try { _map = JsonSerializer.Deserialize<Dictionary<string, SyncState>>(File.ReadAllText(_path), _opts) ?? new(); }
            catch (Exception ex) { _log.LogWarning(ex, "Đọc mail-sync.json lỗi"); }
        }
    }

    public SyncState? Get(string address)
    {
        lock (_lock) return _map.TryGetValue(address, out var s) ? s : null;
    }

    public void Set(string address, uint uidValidity, uint lastUid)
    {
        lock (_lock)
        {
            _map[address] = new SyncState(uidValidity, lastUid);
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, _opts)); }
            catch (Exception ex) { _log.LogError(ex, "Ghi mail-sync.json lỗi"); }
        }
    }
}
