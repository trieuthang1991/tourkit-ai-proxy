namespace TourkitAiProxy.Models;

/// <summary>Single source of truth cho model routing + listing.</summary>
/// Thêm model mới: chỉ cần append 1 entry vào <see cref="All"/>.
/// Frontend fetch /api/ai/models để biết list — không hardcode bảng nào ở client nữa.
public static class ModelRegistry
{
    public record Entry(string Id, string Label, string Path, string Fmt, bool Recommended = false);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("deepseek-v4-flash", "DeepSeek V4 Flash", "zen/go/v1/chat/completions", "openai",    Recommended: true),
        new Entry("deepseek-v4-pro",   "DeepSeek V4 Pro",   "zen/go/v1/chat/completions", "openai"),
        new Entry("minimax-m2.5",      "MiniMax M2.5",      "zen/go/v1/messages",         "anthropic"),
        new Entry("minimax-m2.7",      "MiniMax M2.7",      "zen/go/v1/messages",         "anthropic"),
        new Entry("kimi-k2.6",         "Kimi K2.6",         "zen/go/v1/chat/completions", "openai"),
        new Entry("glm-5.1",           "GLM 5.1",           "zen/go/v1/chat/completions", "openai"),
        new Entry("qwen-3.6",          "Qwen 3.6",          "zen/go/v1/chat/completions", "openai"),
    };

    /// Default cho model không có trong registry — giữ tương thích với hành vi cũ
    /// (Program.cs gốc: switch ... _ => chat/completions/openai).
    private static readonly Entry Fallback =
        new("__fallback__", "Fallback", "zen/go/v1/chat/completions", "openai");

    public static Entry Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return All[0];
        foreach (var e in All)
            if (string.Equals(e.Id, model, StringComparison.OrdinalIgnoreCase))
                return e;
        return Fallback with { Id = model };
    }

    /// Shape cho /api/ai/models response.
    public static object PublicList() => All.Select(e => new
    {
        id          = e.Id,
        label       = e.Label,
        recommended = e.Recommended,
        format      = e.Fmt   // dùng cho future UI hint (vd "anthropic" model cần header riêng)
    });
}
