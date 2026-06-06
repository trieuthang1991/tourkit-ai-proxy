using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Providers;

/// Claude (Anthropic trực tiếp, api.anthropic.com/v1/messages). API key user nhập ở UI → ProviderKeyStore.
/// Anthropic format: system top-level, headers x-api-key + anthropic-version; parse qua UpstreamParser ("anthropic").
public class AnthropicProvider : IAiProvider
{
    public string Id => "anthropic";
    public string Label => "Claude (Anthropic)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("claude-3-5-sonnet-latest", "Claude 3.5 Sonnet", Recommended: true),
        new ProviderModel("claude-3-5-haiku-latest", "Claude 3.5 Haiku"),
        new ProviderModel("claude-sonnet-4-5", "Claude Sonnet 4.5"),
        new ProviderModel("claude-opus-4-1", "Claude Opus 4.1"),
        new ProviderModel("claude-3-opus-latest", "Claude 3 Opus"),
    };

    private const string BaseUrl = "https://api.anthropic.com";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;

    public AnthropicProvider(IHttpClientFactory http, ProviderKeyStore keys) { _http = http; _keys = keys; }

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Chưa nhập API key cho Claude (Anthropic). Mở 'Cấu hình AI' để nhập key.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? Models[0].Id : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

        var body = new
        {
            model,
            max_tokens = maxTokens,
            temperature,
            system,
            messages = new object[] { new { role = "user", content = BuildUserContent(req) } }
        };

        var client = _http.CreateClient("anthropic");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("x-api-key", key);
        msg.Headers.Add("anthropic-version", "2023-06-01");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.SendAsync(msg, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException((int)resp.StatusCode, "Anthropic error", raw[..Math.Min(raw.Length, 800)]);

        var p = UpstreamParser.Parse(raw, "anthropic");
        return new CompleteResult(p.Text, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds, p.FinishReason);
    }

    // Text thuần → string; có ảnh → mảng content {type:text}+{type:image, source:base64} (Claude vision).
    private static object BuildUserContent(CompleteRequest req)
    {
        if (req.Images is not { Count: > 0 }) return req.Prompt;
        var parts = new List<object> { new { type = "text", text = req.Prompt } };
        foreach (var url in req.Images)
        {
            var img = ImagePart.FromDataUrl(url);
            if (img is null) continue;
            parts.Add(new { type = "image", source = new { type = "base64", media_type = img.MediaType, data = img.Base64 } });
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
