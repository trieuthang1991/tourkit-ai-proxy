namespace TourkitAiProxy.Services.Providers;

/// 12 feature dùng AI trong proxy. Mỗi enum value 1-1 với key `Models:{Name}` trong appsettings.
/// "Primary" KHÔNG có ở đây — nó chỉ là root fallback internal Registry.
public enum AiFeature
{
    ChatAnalytics,
    Wizard,
    TourBuilder,
    VisaScoring,
    VisaExtraction,
    DealScoring,
    MailDraft,
    MailCompose,
    MailClassify,
    CustomerReview,
    Widget,
    NccImport
}

public record ResolvedModel(string Provider, string Model, string? ApiKey);

/// <summary>
/// Single source of truth cho cấu hình AI model per-feature.
///
/// Resolution chain:
///   Provider: override → Models:{F}:Provider → Models:Primary:Provider → throw
///   Model:    override → Models:{F}:Model    → Models:Primary:Model    → provider.DefaultModel
///   ApiKey:   Models:{F}:ApiKey → Models:Primary:ApiKey (nếu provider == Primary's) →
///             Providers:{Provider}:ApiKey → env var → null (provider tự throw khi gọi upstream)
/// </summary>
public class AiModelRegistry
{
    private readonly IConfiguration _cfg;
    private readonly ProviderRegistry _providers;

    private static readonly Dictionary<string, (string Section, string Env)> ProviderKeyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"]   = ("Anthropic",  "ANTHROPIC_API_KEY"),
            ["deepseek"]    = ("DeepSeek",   "DEEPSEEK_API_KEY"),
            ["openai"]      = ("OpenAI",     "OPENAI_API_KEY"),
            ["opencode-go"] = ("OpenCode",   "OPENCODE_API_KEY"),
            ["nine-routes"] = ("NineRoutes", "NINE_ROUTES_API_KEY"),
            ["grok"]        = ("Grok",       "GROK_API_KEY"),
        };

    public AiModelRegistry(IConfiguration cfg, ProviderRegistry providers)
    {
        _cfg = cfg;
        _providers = providers;

        // Fail-fast nếu Primary thiếu — đây là invariant.
        if (string.IsNullOrWhiteSpace(_cfg["Models:Primary:Provider"]))
            throw new InvalidOperationException(
                "Cấu hình thiếu Models:Primary:Provider — đây là root fallback bắt buộc của AiModelRegistry.");
    }

    public ResolvedModel Resolve(AiFeature feature, string? overrideProvider = null, string? overrideModel = null)
    {
        var section = feature.ToString();

        var provider = NotEmpty(overrideProvider)
            ?? NotEmpty(_cfg[$"Models:{section}:Provider"])
            ?? NotEmpty(_cfg["Models:Primary:Provider"])
            ?? throw new InvalidOperationException(
                $"Không resolve được provider cho feature {feature} — Models:Primary:Provider rỗng?");

        // Validate provider đã đăng ký trong DI (case-insensitive)
        var providerInstance = _providers.All.FirstOrDefault(p =>
            string.Equals(p.Id, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Provider '{provider}' (feature {feature}) chưa đăng ký trong DI — kiểm tra Program.cs.");

        var model = NotEmpty(overrideModel)
            ?? NotEmpty(_cfg[$"Models:{section}:Model"])
            ?? NotEmpty(_cfg["Models:Primary:Model"])
            ?? providerInstance.Models.FirstOrDefault(m => m.Recommended)?.Id
            ?? providerInstance.Models.First().Id;

        // ApiKey resolution
        var apiKey = NotEmpty(_cfg[$"Models:{section}:ApiKey"]);
        if (apiKey == null)
        {
            var primaryProv = _cfg["Models:Primary:Provider"];
            if (string.Equals(primaryProv, provider, StringComparison.OrdinalIgnoreCase))
                apiKey = NotEmpty(_cfg["Models:Primary:ApiKey"]);
        }
        apiKey ??= KeyFor(provider);

        return new ResolvedModel(provider, model, apiKey);
    }

    /// Key fallback theo TÊN PROVIDER (không qua feature). Dùng cho code không có feature context.
    /// Thứ tự: Providers:{X}:ApiKey → env var.
    public string? KeyFor(string providerId)
    {
        if (!ProviderKeyMap.TryGetValue(providerId, out var m)) return null;
        return NotEmpty(_cfg[$"Providers:{m.Section}:ApiKey"])
            ?? NotEmpty(Environment.GetEnvironmentVariable(m.Env));
    }

    /// Dump toàn bộ resolution — dùng cho admin endpoint debug.
    public IReadOnlyDictionary<AiFeature, ResolvedModel> Snapshot()
        => Enum.GetValues<AiFeature>().ToDictionary(f => f, f => Resolve(f));

    private static string? NotEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
