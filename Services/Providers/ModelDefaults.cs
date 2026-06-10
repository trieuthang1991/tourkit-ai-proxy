// Services/Providers/ModelDefaults.cs
namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Đọc cấu hình model mặc định cho 2 nhóm feature từ appsettings.json:
///   "Models": {
///     "Primary": { "Provider": "anthropic", "Model": "claude-sonnet-4-5", "ApiKey": "sk-ant-..." },
///     "Review":  { "Provider": "opencode-go", "Model": "deepseek-v4-flash", "ApiKey": "sk-..." }
///   }
///
/// - Primary: dùng cho các tính năng MẠNH (Chat, Wizard, Visa, Deal, Tour, Mail).
/// - Review: dùng cho Customer Review — model phụ rẻ + nhanh, chạy nhiều, không cần Claude/GPT.
///
/// User KHÔNG cần cấu hình ở UI nữa — mọi key đọc từ appsettings.
/// Frontend gửi apiKey rỗng → service tự fallback vào config này.
/// </summary>
public class ModelDefaults
{
    private readonly IConfiguration _cfg;

    public ModelDefaults(IConfiguration cfg) => _cfg = cfg;

    public ModelConfig Primary => Read("Primary");
    public ModelConfig Review  => Read("Review");

    private ModelConfig Read(string section) => new(
        Provider: _cfg[$"Models:{section}:Provider"],
        Model:    _cfg[$"Models:{section}:Model"],
        ApiKey:   _cfg[$"Models:{section}:ApiKey"]
    );
}

public record ModelConfig(string? Provider, string? Model, string? ApiKey);
