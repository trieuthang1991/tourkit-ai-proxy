using System.Globalization;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Counts cho sidebar: tổng + chưa đọc + theo trạng thái + theo nhóm.
public record MailCounts(int Total, int Unread, Dictionary<string, int> ByStatus, Dictionary<string, int> ByCategory);

/// File-backed store: mailId → MailItem. Persist data/mails.json. Threadsafe qua lock.
/// Mẫu ReviewRepository. Production: thay SQLite/Postgres.
public class MailRepository
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, MailItem> _map;
    private readonly ILogger<MailRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MailRepository(IWebHostEnvironment env, ILogger<MailRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mails.json");

        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                _map = JsonSerializer.Deserialize<Dictionary<string, MailItem>>(json, _jsonOpts) ?? new();
                _log.LogInformation("Loaded {N} mails", _map.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Parse mails.json failed — reset rỗng");
                _map = new();
            }
        }
        else
        {
            _map = new();
            File.WriteAllText(_path, "{}");
        }
    }

    public MailItem? Get(string id)
    {
        lock (_lock) return _map.TryGetValue(id, out var m) ? m : null;
    }

    public bool Has(string id)
    {
        lock (_lock) return _map.ContainsKey(id);
    }

    public void Upsert(MailItem item)
    {
        lock (_lock) { _map[item.Id] = item; Persist(); }
    }

    public bool SetStatus(string id, string status)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var m)) return false;
            _map[id] = m with { Status = status };
            Persist();
            return true;
        }
    }

    public bool SetRead(string id, bool isRead = true)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var m)) return false;
            if (m.IsRead == isRead) return true;
            _map[id] = m with { IsRead = isRead };
            Persist();
            return true;
        }
    }

    public bool SetDraft(string id, MailDraft draft, string status)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var m)) return false;
            _map[id] = m with { Draft = draft, Status = status };
            Persist();
            return true;
        }
    }

    /// Lọc theo status/category/search (search khớp subject+from+body, bỏ dấu, không phân biệt hoa).
    /// Sắp xếp mới nhất trước.
    public IReadOnlyList<MailItem> Filter(string? status, string? category, string? search)
    {
        lock (_lock)
        {
            IEnumerable<MailItem> q = _map.Values;
            if (!string.IsNullOrWhiteSpace(status))   q = q.Where(m => m.Status == status);
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m.Category == category);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = Norm(search);
                q = q.Where(m => Norm($"{m.Subject} {m.From.Name} {m.From.Email} {m.Body}").Contains(s));
            }
            return q.OrderByDescending(m => m.ReceivedAt, StringComparer.Ordinal).ToList();
        }
    }

    public MailCounts Counts()
    {
        lock (_lock)
        {
            var byStatus = new Dictionary<string, int>();
            var byCat = new Dictionary<string, int>();
            int unread = 0;
            foreach (var m in _map.Values)
            {
                byStatus[m.Status] = byStatus.GetValueOrDefault(m.Status) + 1;
                var c = m.Category ?? "khac";
                byCat[c] = byCat.GetValueOrDefault(c) + 1;
                if (!m.IsRead) unread++;
            }
            return new MailCounts(_map.Count, unread, byStatus, byCat);
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, _jsonOpts)); }
        catch (Exception ex) { _log.LogError(ex, "Write mails.json failed"); }
    }

    /// Chuẩn hóa search: lowercase + bỏ dấu tiếng Việt + đ→d.
    private static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
