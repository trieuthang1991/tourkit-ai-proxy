namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Đọc API key per-provider từ <c>Providers:{X}:ApiKey</c> hoặc env var.
///
/// Logic giống <see cref="AiModelRegistry.KeyFor"/> NHƯNG đứng độc lập trên <see cref="IConfiguration"/>
/// để TRÁNH DI CYCLE: providers (Anthropic/OpenAI/DeepSeek) phụ thuộc ProviderKeyStore và lại nằm trong
/// ProviderRegistry — nếu ProviderKeyStore phụ thuộc AiModelRegistry (mà AiModelRegistry phụ thuộc
/// ProviderRegistry để validate provider đăng ký), DI graph sẽ vòng. Duplication có chủ ý, cùng key
/// nguồn (<c>Providers:{X}:ApiKey</c> + env), không drift.
/// </summary>
public class ProviderKeyStore
{
    private static readonly Dictionary<string, (string Section, string Env)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"]   = ("Anthropic",  "ANTHROPIC_API_KEY"),
            ["deepseek"]    = ("DeepSeek",   "DEEPSEEK_API_KEY"),
            ["openai"]      = ("OpenAI",     "OPENAI_API_KEY"),
            ["opencode-go"] = ("OpenCode",   "OPENCODE_API_KEY"),
            ["nine-routes"] = ("NineRoutes", "NINE_ROUTES_API_KEY"),
        };

    private readonly IConfiguration _cfg;
    public ProviderKeyStore(IConfiguration cfg) => _cfg = cfg;

    public string? Get(string providerId)
    {
        if (!Map.TryGetValue(providerId, out var m)) return null;
        var fromCfg = _cfg[$"Providers:{m.Section}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(fromCfg)) return fromCfg;
        var fromEnv = Environment.GetEnvironmentVariable(m.Env);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(Get(providerId));
}
