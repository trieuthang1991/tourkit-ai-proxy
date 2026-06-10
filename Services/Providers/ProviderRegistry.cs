namespace TourkitAiProxy.Services.Providers;

/// Resolve provider theo id (case-insensitive). Default = first registered nếu request không gọi id.
public class ProviderRegistry
{
    private readonly Dictionary<string, IAiProvider> _byId;
    private readonly IAiProvider _default;

    public ProviderRegistry(IEnumerable<IAiProvider> providers, IConfiguration cfg)
    {
        _byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (_byId.Count == 0)
            throw new InvalidOperationException("Chưa đăng ký provider nào — kiểm tra Program.cs DI.");

        // Default provider lookup ưu tiên:
        //   1. Providers:Default (legacy)
        //   2. Models:Primary:Provider (mới — admin chỉ cần khai Models:* là đủ)
        //   3. Provider đầu tiên đăng ký
        var defaultId = cfg["Providers:Default"] ?? cfg["Models:Primary:Provider"];
        _default = (defaultId != null && _byId.TryGetValue(defaultId, out var d))
            ? d
            : _byId.Values.First();
    }

    public IAiProvider Resolve(string? id)
        => string.IsNullOrWhiteSpace(id) ? _default
         : _byId.TryGetValue(id, out var p) ? p
         : _default;

    public IEnumerable<IAiProvider> All => _byId.Values;
}
