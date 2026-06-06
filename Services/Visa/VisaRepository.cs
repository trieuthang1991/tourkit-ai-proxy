using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Visa;

/// File-backed store: assessmentId → VisaAssessment. Persist data/visa-assessments.json.
/// Lock-guarded, camelCase JSON (khớp frontend). Mẫu ReviewRepository — MVP, thay DB để scale.
public class VisaRepository
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, VisaAssessment> _map;
    private readonly ILogger<VisaRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VisaRepository(IWebHostEnvironment env, ILogger<VisaRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "visa-assessments.json");

        if (File.Exists(_path))
        {
            try
            {
                _map = JsonSerializer.Deserialize<Dictionary<string, VisaAssessment>>(File.ReadAllText(_path)) ?? new();
                _log.LogInformation("Loaded {N} visa assessments", _map.Count);
            }
            catch (Exception ex) { _log.LogError(ex, "Parse visa-assessments.json lỗi — reset rỗng"); _map = new(); }
        }
        else { _map = new(); File.WriteAllText(_path, "{}"); }
    }

    public VisaAssessment? Get(string id)
    {
        lock (_lock) return _map.TryGetValue(id, out var a) ? a : null;
    }

    /// Toàn bộ, sắp xếp mới nhất trước (cho list lịch sử).
    public List<VisaAssessment> All()
    {
        lock (_lock)
            return _map.Values.OrderByDescending(a => a.CreatedAt, StringComparer.Ordinal).ToList();
    }

    public void Save(VisaAssessment a)
    {
        lock (_lock) { _map[a.Id] = a; Persist(); }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_map.Remove(id)) return false;
            Persist();
            return true;
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, _jsonOpts)); }
        catch (Exception ex) { _log.LogError(ex, "Write visa-assessments.json lỗi"); }
    }
}
