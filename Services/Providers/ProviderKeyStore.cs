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

    /// Key fallback từ config/env (null nếu không có).
    /// Thứ tự ưu tiên:
    ///   1. Providers:{X}:ApiKey  (config legacy per-provider)
    ///   2. Models:Primary:ApiKey  (nếu providerId match Models:Primary:Provider)
    ///   3. Models:Review:ApiKey   (nếu providerId match Models:Review:Provider)
    ///   4. Env var (vd ANTHROPIC_API_KEY)
    /// Key client gửi per-request luôn ưu tiên CAO HƠN tất cả (provider check trước).
    public string? Get(string providerId)
    {
        if (!Map.TryGetValue(providerId, out var m)) return null;
        var ck = _cfg[$"Providers:{m.Section}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(ck)) return ck;

        // Mới: fallback Models:Primary / Models:Review nếu provider id match.
        // Pattern này cho phép admin chỉ cần khai báo Models:* trong appsettings
        // mà không cần lặp lại Providers:{X}:ApiKey.
        var primaryProv = _cfg["Models:Primary:Provider"];
        if (!string.IsNullOrWhiteSpace(primaryProv) &&
            string.Equals(primaryProv, providerId, StringComparison.OrdinalIgnoreCase))
        {
            var pk = _cfg["Models:Primary:ApiKey"];
            if (!string.IsNullOrWhiteSpace(pk)) return pk;
        }
        var reviewProv = _cfg["Models:Review:Provider"];
        if (!string.IsNullOrWhiteSpace(reviewProv) &&
            string.Equals(reviewProv, providerId, StringComparison.OrdinalIgnoreCase))
        {
            var rk = _cfg["Models:Review:ApiKey"];
            if (!string.IsNullOrWhiteSpace(rk)) return rk;
        }

        var ek = Environment.GetEnvironmentVariable(m.Env);
        return string.IsNullOrWhiteSpace(ek) ? null : ek;
    }

    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(Get(providerId));
}
