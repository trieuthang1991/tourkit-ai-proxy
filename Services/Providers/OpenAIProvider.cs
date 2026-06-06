using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Providers;

/// ChatGPT (OpenAI trực tiếp, api.openai.com). API key do user nhập ở UI → ProviderKeyStore (server-side).
/// OpenAI-compatible chat/completions; parse qua UpstreamParser ("openai").
public class OpenAIProvider : IAiProvider
{
    public string Id => "openai";
    public string Label => "ChatGPT (OpenAI)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("gpt-4o-mini", "GPT-4o mini", Recommended: true),
        new ProviderModel("gpt-4o", "GPT-4o"),
        new ProviderModel("gpt-4.1-mini", "GPT-4.1 mini"),
        new ProviderModel("gpt-4.1", "GPT-4.1"),
    };

    private const string BaseUrl = "https://api.openai.com/v1";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;

    public OpenAIProvider(IHttpClientFactory http, ProviderKeyStore keys) { _http = http; _keys = keys; }

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Chưa nhập API key cho ChatGPT (OpenAI). Mở 'Cấu hình AI' để nhập key.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? Models[0].Id : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

        // User content: text-only (string) hoặc multimodal (mảng text + image_url) khi req.Images có.
        object userContent = BuildUserContent(req);
        var body = new
        {
            model,
            max_tokens = maxTokens,
            temperature,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = userContent }
            }
        };

        var client = _http.CreateClient("openai");
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
            throw new UpstreamException((int)resp.StatusCode, "OpenAI error", raw[..Math.Min(raw.Length, 800)]);

        var p = UpstreamParser.Parse(raw, "openai");
        return new CompleteResult(p.Text, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds, p.FinishReason);
    }

    // Text thuần → trả string; có ảnh → mảng content {type:text}+{type:image_url} (OpenAI vision).
    private static object BuildUserContent(CompleteRequest req)
    {
        if (req.Images is not { Count: > 0 }) return req.Prompt;
        var parts = new List<object> { new { type = "text", text = req.Prompt } };
        foreach (var url in req.Images)
        {
            var img = ImagePart.FromDataUrl(url);
            if (img is null) continue;
            parts.Add(new { type = "image_url", image_url = new { url = $"data:{img.MediaType};base64,{img.Base64}" } });
        }
        return parts;
    }

    // Stream: buffered (gọi xong phát 1 lần). Đủ cho tính năng hiện tại; tránh phức tạp SSE riêng.
    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var r = await CompleteAsync(req, ct);
        if (!string.IsNullOrEmpty(r.Text)) await onDelta(r.Text);
        return r;
    }
}
