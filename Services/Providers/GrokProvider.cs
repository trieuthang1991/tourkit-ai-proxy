using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Grok (xAI trực tiếp, api.x.ai) — OpenAI-compatible /chat/completions.
/// Auth: Bearer xai-... (đăng ký https://console.x.ai).
/// Body/response: OpenAI-format chuẩn — clone của <see cref="DeepSeekProvider"/>.
/// </summary>
public class GrokProvider : IAiProvider
{
    public string Id => "grok";
    public string Label => "Grok (xAI)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("grok-4.3", "Grok 4.3", Recommended: true),
    };

    private const string BaseUrl = "https://api.x.ai/v1";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly ILogger<GrokProvider> _log;
    private readonly TenantQuotaStore _quota;

    public GrokProvider(IHttpClientFactory http, ProviderKeyStore keys,
        AiUsageLog usage, AiCallContext ctx, ILogger<GrokProvider> log, TenantQuotaStore quota)
    {
        _http = http; _keys = keys; _usage = usage; _ctx = ctx; _log = log; _quota = quota;
    }

    private void EnsureQuota()
    {
        var t = _ctx.Resolve().Tenant;
        if (string.IsNullOrEmpty(t)) return;
        if (!_quota.IsAvailable(t)) { var s = _quota.Snapshot(t); throw new QuotaExhaustedException(t, s.Limit, s.Used); }
    }

    private string DefaultModel()
        => Models.FirstOrDefault(m => m.Recommended)?.Id ?? Models[0].Id;

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        EnsureQuota();
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Chưa cấu hình API key cho Grok. Thêm Providers:Grok:ApiKey trong appsettings.json " +
                "hoặc env GROK_API_KEY.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

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

        var client = _http.CreateClient("grok");
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
            _log.LogWarning("[grok] {Status}: {Body}", resp.StatusCode, raw.Length > 500 ? raw[..500] : raw);
            throw new UpstreamException((int)resp.StatusCode, "Grok error", raw[..Math.Min(raw.Length, 800)]);
        }

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
        if (!string.IsNullOrEmpty(c.Tenant)) _quota.Consume(c.Tenant);
        return new CompleteResult(sb.ToString(), model, inTok, outTok, sw.ElapsedMilliseconds, finish);
    }

    /// Stream: buffered fallback (Grok hỗ trợ SSE; bật khi có nhu cầu streaming thật sự).
    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var r = await CompleteAsync(req, ct);
        if (!string.IsNullOrEmpty(r.Text)) await onDelta(r.Text);
        return r;
    }
}
