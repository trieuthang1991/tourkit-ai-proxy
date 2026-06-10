using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// DeepSeek trực tiếp (api.deepseek.com) — OpenAI-compatible /chat/completions.
/// Khác OpenCode (qua gateway zen): gọi DeepSeek HUB nguyên gốc → rẻ + ít hop.
/// Pricing chính thức (tháng 6/2026): chat $0.27/$1.10 per Mtok in/out.
///
/// Auth: Bearer sk-... (đăng ký https://platform.deepseek.com).
/// Body OpenAI-format: { model, messages: [{role, content}], max_tokens, temperature }.
/// Response OpenAI-format: choices[0].message.content + usage.prompt_tokens/completion_tokens.
/// </summary>
public class DeepSeekProvider : IAiProvider
{
    public string Id => "deepseek";
    public string Label => "DeepSeek (trực tiếp)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("deepseek-chat",     "DeepSeek Chat",     Recommended: true),
        new ProviderModel("deepseek-reasoner", "DeepSeek Reasoner"),   // có thinking, chậm + đắt hơn
    };

    private const string BaseUrl = "https://api.deepseek.com";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly IConfiguration _cfg;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly ILogger<DeepSeekProvider> _log;

    public DeepSeekProvider(IHttpClientFactory http, ProviderKeyStore keys, IConfiguration cfg,
        AiUsageLog usage, AiCallContext ctx, ILogger<DeepSeekProvider> log)
    {
        _http = http; _keys = keys; _cfg = cfg; _usage = usage; _ctx = ctx; _log = log;
    }

    /// Default model: Models:Primary:Model nếu provider match deepseek; Models:Review tương tự cho path review.
    private string DefaultModel()
    {
        foreach (var section in new[] { "Primary", "Review" })
        {
            var prov = _cfg[$"Models:{section}:Provider"];
            var mod  = _cfg[$"Models:{section}:Model"];
            if (!string.IsNullOrWhiteSpace(prov) && prov.Equals(Id, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(mod)) return mod;
        }
        return Models[0].Id;
    }

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Chưa cấu hình API key cho DeepSeek. Thêm Models:Primary:ApiKey " +
                "hoặc Providers:DeepSeek:ApiKey trong appsettings.json.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

        // OpenAI-compat chat/completions: messages[] với system + user
        var body = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = req.Prompt }
            },
            max_tokens = maxTokens,
            temperature,
            stream = false
        };

        var client = _http.CreateClient("deepseek");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {key}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.SendAsync(msg, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[deepseek] {Status}: {Body}", resp.StatusCode, raw.Length > 500 ? raw[..500] : raw);
            throw new UpstreamException((int)resp.StatusCode, "DeepSeek error", raw[..Math.Min(raw.Length, 800)]);
        }

        // Parse OpenAI-compat response
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        string finish = "stop";
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var ch in choices.EnumerateArray())
            {
                if (ch.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var cnt))
                    sb.Append(cnt.GetString());
                if (ch.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finish = fr.GetString() ?? "stop";
            }
        }
        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) inTok = ptv;
            if (u.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var otv)) outTok = otv;
        }

        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, Id, model, inTok, outTok, sw.ElapsedMilliseconds);
        return new CompleteResult(sb.ToString(), model, inTok, outTok, sw.ElapsedMilliseconds, finish);
    }

    /// Stream: buffered fallback (DeepSeek hỗ trợ SSE nhưng tính năng hiện tại không cần streaming).
    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var r = await CompleteAsync(req, ct);
        if (!string.IsNullOrEmpty(r.Text)) await onDelta(r.Text);
        return r;
    }
}
