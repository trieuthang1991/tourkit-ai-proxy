namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Đọc API key provider TỪ SERVER CONFIG (appsettings Providers:{X}:ApiKey) hoặc env var — chỉ là
/// FALLBACK tùy chọn. Key chính của OpenAI/Anthropic do CLIENT gửi kèm mỗi request (CompleteRequest.ApiKey),
/// KHÔNG lưu trên server (theo yêu cầu: lưu client-side). Lớp này không persist gì.
/// </summary>
public class ProviderKeyStore
{
    private readonly IConfiguration _cfg;

    private static readonly Dictionary<string, (string Section, string Env)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"]      = ("OpenAI", "OPENAI_API_KEY"),
        ["anthropic"]   = ("Anthropic", "ANTHROPIC_API_KEY"),
        ["opencode-go"] = ("OpenCode", "OPENCODE_API_KEY"),
        ["nine-routes"] = ("NineRoutes", "NINE_ROUTES_API_KEY"),
    };

    public ProviderKeyStore(IConfiguration cfg) => _cfg = cfg;

    /// Key fallback từ config/env (null nếu không có). Key client gửi per-request được ưu tiên ở provider.
    public string? Get(string providerId)
    {
        if (!Map.TryGetValue(providerId, out var m)) return null;
        var ck = _cfg[$"Providers:{m.Section}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(ck)) return ck;
        var ek = Environment.GetEnvironmentVariable(m.Env);
        return string.IsNullOrWhiteSpace(ek) ? null : ek;
    }

    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(Get(providerId));
}
