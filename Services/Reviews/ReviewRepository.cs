using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews;

/// File-backed key-value store: customerId → CustomerReview.
/// Persist tại data/reviews.json. Threadsafe qua lock chung — đủ cho scale <1k KH spec.
/// Production: thay bằng SQLite/Postgres, drop-in replace giữ interface.
public class ReviewRepository
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, CustomerReview> _map;
    private readonly ILogger<ReviewRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase   // tránh property name mismatch giữa C# PascalCase và JS camelCase
    };

    public ReviewRepository(IWebHostEnvironment env, ILogger<ReviewRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "reviews.json");

        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                _map = JsonSerializer.Deserialize<Dictionary<string, CustomerReview>>(json) ?? new();
                _log.LogInformation("Loaded {N} reviews", _map.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Parse reviews.json failed — reset rỗng");
                _map = new();
            }
        }
        else
        {
            _map = new();
            File.WriteAllText(_path, "{}");
        }
    }

    public CustomerReview? Get(string customerId)
    {
        lock (_lock) return _map.TryGetValue(customerId, out var r) ? r : null;
    }

    public IReadOnlyDictionary<string, CustomerReview> All()
    {
        lock (_lock) return new Dictionary<string, CustomerReview>(_map);
    }

    public void Save(CustomerReview review)
    {
        lock (_lock)
        {
            _map[review.CustomerId] = review;
            Persist();
        }
    }

    public bool SetFeedback(string customerId, ReviewFeedback fb)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(customerId, out var existing)) return false;
            _map[customerId] = existing with { Feedback = fb };
            Persist();
            return true;
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, _jsonOpts)); }
        catch (Exception ex) { _log.LogError(ex, "Write reviews.json failed"); }
    }

    /// SHA256 hex của customer JSON canonical — đổi tức là data đã thay đổi → review stale.
    /// Dùng JsonSerializer options nhất quán nên cùng customer cho cùng fingerprint qua các process.
    public static string FingerprintFor(Customer c)
    {
        var json = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = false });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 32);
    }
}
