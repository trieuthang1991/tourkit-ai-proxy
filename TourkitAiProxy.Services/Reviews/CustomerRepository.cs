using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews;

/// Read-only loader cho data/customers.seed.json. In production thay bằng EF Core / Dapper
/// đọc từ CRM database. Load 1 lần lúc startup, cache in-memory.
public class CustomerRepository
{
    private readonly Dictionary<string, Customer> _byId;
    private readonly List<Customer> _list;
    private readonly ILogger<CustomerRepository> _log;

    public CustomerRepository(IWebHostEnvironment env, ILogger<CustomerRepository> log)
    {
        _log = log;
        var path = Path.Combine(env.ContentRootPath, "data", "customers.seed.json");
        if (!File.Exists(path))
        {
            _log.LogWarning("Customer seed không tồn tại tại {Path} — list rỗng", path);
            _byId = new(); _list = new();
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var customers = JsonSerializer.Deserialize<List<Customer>>(json) ?? new List<Customer>();
            _list = customers;
            _byId = customers.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            _log.LogInformation("Loaded {N} customers từ seed", _list.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Parse customer seed failed");
            _byId = new(); _list = new();
        }
    }

    public IReadOnlyList<Customer> All() => _list;

    public Customer? Get(string id) => _byId.TryGetValue(id, out var c) ? c : null;

    public IEnumerable<Customer> Filter(string? segment = null, string? search = null, int? maxDaysSinceLast = null)
    {
        IEnumerable<Customer> q = _list;
        if (!string.IsNullOrWhiteSpace(segment) && segment != "all")
            q = q.Where(c => string.Equals(c.Segment, segment, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            q = q.Where(c =>
                (c.Name ?? "").ToLowerInvariant().Contains(s) ||
                (c.Phone ?? "").Contains(s) ||
                (c.Email ?? "").ToLowerInvariant().Contains(s) ||
                (c.Id ?? "").ToLowerInvariant().Contains(s));
        }

        if (maxDaysSinceLast.HasValue)
            q = q.Where(c => c.Metrics.LastPurchaseDaysAgo.HasValue
                          && c.Metrics.LastPurchaseDaysAgo.Value <= maxDaysSinceLast.Value);

        return q;
    }
}
