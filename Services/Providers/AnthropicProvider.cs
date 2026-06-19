using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Providers;

/// Claude (Anthropic trực tiếp, api.anthropic.com/v1/messages). API key user nhập ở UI → ProviderKeyStore.
/// Anthropic format: system top-level, headers x-api-key + anthropic-version; parse qua UpstreamParser ("anthropic").
public class AnthropicProvider : IAiProvider
{
    public string Id => "anthropic";
    public string Label => "Claude (Anthropic)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("claude-sonnet-4-5", "Claude Sonnet 4.5", Recommended: true),
        new ProviderModel("claude-haiku-4-5", "Claude Haiku 4.5"),
        new ProviderModel("claude-opus-4-1", "Claude Opus 4.1"),
    };

    private const string BaseUrl = "https://api.anthropic.com";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly ILogger<AnthropicProvider> _log;
    private readonly TenantQuotaStore _quota;

    public AnthropicProvider(IHttpClientFactory http, ProviderKeyStore keys, AiUsageLog usage, AiCallContext ctx, ILogger<AnthropicProvider> log, TenantQuotaStore quota)
    { _http = http; _keys = keys; _usage = usage; _ctx = ctx; _log = log; _quota = quota; }

    private void EnsureQuota()
    {
        var t = _ctx.Resolve().Tenant;
        if (string.IsNullOrEmpty(t)) return;
        if (!_quota.IsAvailable(t)) { var s = _quota.Snapshot(t); throw new QuotaExhaustedException(t, s.Limit, s.Used); }
    }

    // Default model: chọn model có Recommended:true trong catalog local, fallback Models[0].
    private string DefaultModel()
        => Models.FirstOrDefault(m => m.Recommended)?.Id ?? Models[0].Id;

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        EnsureQuota();
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Chưa nhập API key cho Claude (Anthropic). Mở 'Cấu hình AI' để nhập key.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

        // Anthropic cache yêu cầu prefix >=1024 tokens. <200 chars (~50 tok) chắc chắn không qua.
        if (req.CacheSystem && system.Length < 200)
            _log.LogWarning("[anthropic] CacheSystem=true nhưng system chỉ {Len} chars -- quá ngắn, Anthropic sẽ KHÔNG cache (cần >=1024 tok ~= 4000 chars)", system.Length);

        // Khi CacheSystem=true → gửi system dạng content blocks với cache_control.
        // Anthropic prompt caching: 90% off cho phần cached, TTL 5 phút mặc định.
        object systemField = req.CacheSystem
            ? (object)new object[]
            {
                new
                {
                    type = "text",
                    text = system,
                    cache_control = new { type = "ephemeral" }
                }
            }
            : system;

        var body = new
        {
            model,
            max_tokens = maxTokens,
            temperature,
            system = systemField,
            messages = new object[] { new { role = "user", content = BuildUserContent(req) } }
        };

        var client = _http.CreateClient("anthropic");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("x-api-key", key);
        msg.Headers.Add("anthropic-version", "2023-06-01");
        // Bật prompt caching khi CacheSystem flag bật. Anthropic đã GA nhưng giữ header
        // cho future-proof + compat với older API version.
        if (req.CacheSystem)
            msg.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.SendAsync(msg, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException((int)resp.StatusCode, "Anthropic error", raw[..Math.Min(raw.Length, 800)]);

        var p = UpstreamParser.Parse(raw, "anthropic");
        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, Id, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds);
        if (!string.IsNullOrEmpty(c.Tenant)) _quota.Consume(c.Tenant);
        return new CompleteResult(p.Text, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds, p.FinishReason);
    }

    // Text thuần → string; có ảnh/PDF → mảng content blocks (Claude vision + document):
    //   {type:"text", text}
    //   {type:"image", source:{type:base64, media_type, data}}
    //   {type:"document", source:{type:base64, media_type:"application/pdf", data}}
    private static object BuildUserContent(CompleteRequest req)
    {
        bool hasImg = req.Images is { Count: > 0 };
        bool hasDoc = req.Documents is { Count: > 0 };
        if (!hasImg && !hasDoc) return req.Prompt;

        var parts = new List<object> { new { type = "text", text = req.Prompt } };
        if (hasImg)
            foreach (var url in req.Images!)
            {
                var img = ImagePart.FromDataUrl(url);
                if (img is null) continue;
                parts.Add(new { type = "image", source = new { type = "base64", media_type = img.MediaType, data = img.Base64 } });
            }
        if (hasDoc)
            foreach (var url in req.Documents!)
            {
                var doc = ImagePart.FromDataUrl(url);   // tái dùng parser (chỉ tách media_type + base64)
                if (doc is null) continue;
                parts.Add(new { type = "document", source = new { type = "base64", media_type = doc.MediaType, data = doc.Base64 } });
            }
        return parts;
    }

    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var r = await CompleteAsync(req, ct);
        if (!string.IsNullOrEmpty(r.Text)) await onDelta(r.Text);
        return r;
    }
}
