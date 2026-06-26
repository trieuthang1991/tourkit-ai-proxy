using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Admin;

/// <summary>
/// Đọc data/consult-leads.jsonl (append-only) cho admin UI + lưu trạng thái "đã liên hệ"
/// vào side-car JSON (data/consult-leads-status.json) để KHÔNG phải sửa file JSONL gốc.
///
/// Mỗi dòng JSONL: { ts, ip, ua, fullName, phone, email?, company?, feature?, note? }.
/// Id được derive deterministically từ (ts|phone|fullName) — SHA-256 first 16 hex char →
/// stable giữa các lần restart (vì file JSONL append-only, không re-shuffle).
/// </summary>
public class ConsultLeadRepository
{
    private readonly ILogger<ConsultLeadRepository> _log;
    private readonly string _jsonlPath;
    private readonly string _statusPath;
    private readonly object _statusLock = new();

    public ConsultLeadRepository(IHostEnvironment env, ILogger<ConsultLeadRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        _jsonlPath  = Path.Combine(dir, "consult-leads.jsonl");
        _statusPath = Path.Combine(dir, "consult-leads-status.json");
    }

    public sealed record ConsultLeadRow(
        string Id,
        DateTime CreatedUtc,
        string FullName,
        string Phone,
        string? Email,
        string? Company,
        string? Feature,
        string? Note,
        string? Ip,
        bool Contacted,
        DateTime? ContactedUtc,
        string? ContactedBy);

    public sealed record StatusEntry(bool Contacted, DateTime ContactedUtc, string By);

    /// <summary>Read all leads (newest first). Empty list nếu file chưa tồn tại.</summary>
    public async Task<List<ConsultLeadRow>> ListAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_jsonlPath)) return new();
        var status = LoadStatus();
        var rows = new List<ConsultLeadRow>(capacity: 64);

        try
        {
            await using var fs = new FileStream(_jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = await sr.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ConsultLeadRow? row;
                try { row = Parse(line, status); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[ConsultLead] parse fail dòng: {Line}", line[..Math.Min(line.Length, 120)]);
                    continue;
                }
                if (row is not null) rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ConsultLead] đọc {Path} fail", _jsonlPath);
        }

        rows.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return rows;
    }

    /// <summary>Đánh dấu lead = đã liên hệ. Idempotent (set lại = noop).</summary>
    public void MarkContacted(string id, string by)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_statusLock)
        {
            var status = LoadStatus();
            status[id] = new StatusEntry(true, DateTime.UtcNow, by ?? "");
            SaveStatus(status);
        }
    }

    /// <summary>Bỏ đánh dấu (undo). Để UI có thể "lỡ tay nhấn".</summary>
    public void MarkUncontacted(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_statusLock)
        {
            var status = LoadStatus();
            if (status.Remove(id)) SaveStatus(status);
        }
    }

    private ConsultLeadRow? Parse(string line, Dictionary<string, StatusEntry> status)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var ts = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                 && DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                 ? t.ToUniversalTime() : DateTime.UtcNow;
        var phone = Str(root, "phone") ?? "";
        var name  = Str(root, "fullName") ?? "";
        var id    = DeriveId(ts, phone, name);

        StatusEntry? entry = status.TryGetValue(id, out var e) ? e : null;

        return new ConsultLeadRow(
            Id:           id,
            CreatedUtc:   ts,
            FullName:     name,
            Phone:        phone,
            Email:        Str(root, "email"),
            Company:      Str(root, "company"),
            Feature:      Str(root, "feature"),
            Note:         Str(root, "note"),
            Ip:           Str(root, "ip"),
            Contacted:    entry?.Contacted ?? false,
            ContactedUtc: entry?.ContactedUtc,
            ContactedBy:  entry?.By);
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;

    private static string DeriveId(DateTime ts, string phone, string name)
    {
        var raw = $"{ts:o}|{phone}|{name}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex
    }

    // ── Side-car status file ────────────────────────────────────────────────
    private Dictionary<string, StatusEntry> LoadStatus()
    {
        if (!File.Exists(_statusPath)) return new();
        try
        {
            var json = File.ReadAllText(_statusPath);
            return JsonSerializer.Deserialize<Dictionary<string, StatusEntry>>(json,
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ConsultLead] đọc status file fail — coi như chưa ai contact");
            return new();
        }
    }

    private void SaveStatus(Dictionary<string, StatusEntry> status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statusPath)!);
            var json = JsonSerializer.Serialize(status,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(_statusPath, json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ConsultLead] ghi status file fail");
        }
    }
}
