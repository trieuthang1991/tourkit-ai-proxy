namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Legacy wrapper quanh AiModelRegistry.KeyFor — chỉ còn để code cũ (chưa qua AiCallGateway phase 2)
/// vẫn build được. Sẽ xóa hẳn ở phase 2.
/// </summary>
public class ProviderKeyStore
{
    private readonly AiModelRegistry _registry;
    public ProviderKeyStore(AiModelRegistry registry) => _registry = registry;

    public string? Get(string providerId) => _registry.KeyFor(providerId);
    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(_registry.KeyFor(providerId));
}
