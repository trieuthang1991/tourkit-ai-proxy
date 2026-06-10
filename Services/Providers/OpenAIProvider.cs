using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Providers;

/// ChatGPT (OpenAI Responses API mới — POST /v1/responses).
/// API key user nhập ở UI → ProviderKeyStore (transient, không persist).
/// Body: instructions (system) + input (string hoặc mảng items). Parse inline:
/// output[*].content[*].text (type=output_text) + usage.input_tokens/output_tokens.
/// Lưu ý: UpstreamParser "openai" KHÔNG xài cho provider này vì shape khác hẳn
/// chat/completions; NineRoutesProvider vẫn dùng parser đó cho schema cũ.
public class OpenAIProvider : IAiProvider
{
    public string Id => "openai";
    public string Label => "ChatGPT (OpenAI)";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("gpt-5.4-mini", "GPT-5.4 mini", Recommended: true),
        new ProviderModel("gpt-5.4", "GPT-5.4"),
        new ProviderModel("gpt-5-mini", "GPT-5 mini"),
        new ProviderModel("gpt-5", "GPT-5"),
        new ProviderModel("gpt-4.1-mini", "GPT-4.1 mini"),
        new ProviderModel("gpt-4.1", "GPT-4.1"),
    };

    private const string BaseUrl = "https://api.openai.com/v1";
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly TenantQuotaStore _quota;

    public OpenAIProvider(IHttpClientFactory http, ProviderKeyStore keys, AiUsageLog usage, AiCallContext ctx, TenantQuotaStore quota)
    { _http = http; _keys = keys; _usage = usage; _ctx = ctx; _quota = quota; }

    private void EnsureQuota()
    {
        var t = _ctx.Resolve().Tenant;
        if (string.IsNullOrEmpty(t)) return;
        if (!_quota.IsAvailable(t)) { var s = _quota.Snapshot(t); throw new QuotaExhaustedException(t, s.Limit, s.Used); }
    }

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        EnsureQuota();
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Chưa nhập API key cho ChatGPT (OpenAI). Mở 'Cấu hình AI' để nhập key.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? Models[0].Id : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
        var temperature = req.Temperature ?? 0.3;
        var system = string.IsNullOrWhiteSpace(req.System) ? OpenCodeClient.DefaultSystem : req.System!;

        // Schema Responses API:
        // - instructions: system prompt (top-level)
        // - input: string khi text thuần; array {role,content[]} khi có ảnh/PDF/multimodal
        // - max_output_tokens / temperature như chat completions
        // - store: false → không lưu state phía OpenAI (privacy + đỡ tốn)
        bool hasMulti = (req.Images is { Count: > 0 }) || (req.Documents is { Count: > 0 });
        object input = hasMulti ? BuildMultimodalInput(req) : req.Prompt;

        var body = new
        {
            model,
            instructions = system,
            input,
            max_output_tokens = maxTokens,
            temperature,
            store = false,
        };

        var client = _http.CreateClient("openai");
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/responses")
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

        var p = ParseResponsesApi(raw);
        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, Id, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds);
        if (!string.IsNullOrEmpty(c.Tenant)) _quota.Consume(c.Tenant);
        return new CompleteResult(p.Text, model, p.InputTokens, p.OutputTokens, sw.ElapsedMilliseconds, p.FinishReason);
    }

    // Multimodal input cho Responses API:
    //   {type:"input_text", text}
    //   {type:"input_image", image_url:"data:image/...;base64,..."}
    //   {type:"input_file",  filename, file_data:"data:application/pdf;base64,..."}
    private static object[] BuildMultimodalInput(CompleteRequest req)
    {
        var parts = new List<object> { new { type = "input_text", text = req.Prompt } };
        if (req.Images is { Count: > 0 })
            foreach (var url in req.Images)
            {
                var img = ImagePart.FromDataUrl(url);
                if (img is null) continue;
                parts.Add(new { type = "input_image", image_url = $"data:{img.MediaType};base64,{img.Base64}" });
            }
        if (req.Documents is { Count: > 0 })
            foreach (var url in req.Documents)
            {
                var doc = ImagePart.FromDataUrl(url);
                if (doc is null) continue;
                // filename giúp model biết context (vd "passport.pdf"); name không bắt buộc nhưng nên có.
                parts.Add(new { type = "input_file", filename = "document.pdf", file_data = $"data:{doc.MediaType};base64,{doc.Base64}" });
            }
        return new object[] { new { role = "user", content = parts } };
    }

    // Parse Responses API:
    //   output[*].content[*]: type "output_text" → ghép text. Bỏ qua reasoning items (type=reasoning).
    //   usage.input_tokens / output_tokens
    //   status="incomplete" + incomplete_details.reason="max_output_tokens" → finishReason="length"
    private record Parsed(string Text, int InputTokens, int OutputTokens, string FinishReason);
    private static Parsed ParseResponsesApi(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType)) continue;
                if (itemType.GetString() != "message") continue;   // bỏ qua reasoning, tool_call, …
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in content.EnumerateArray())
                {
                    if (!c.TryGetProperty("type", out var t)) continue;
                    if (t.GetString() != "output_text") continue;
                    if (c.TryGetProperty("text", out var text)) sb.Append(text.GetString());
                }
            }
        }

        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) inTok = itv;
            if (u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) outTok = otv;
        }

        var finish = "stop";
        if (root.TryGetProperty("status", out var status) && status.GetString() == "incomplete")
        {
            finish = "length";
            if (root.TryGetProperty("incomplete_details", out var inc) &&
                inc.TryGetProperty("reason", out var rsn))
            {
                var r = rsn.GetString();
                if (r == "max_output_tokens") finish = "length";
                else if (!string.IsNullOrEmpty(r)) finish = r;
            }
        }

        return new Parsed(sb.ToString(), inTok, outTok, finish);
    }

    // Stream: buffered (gọi xong phát 1 lần). Đủ cho tính năng hiện tại;
    // Responses API có SSE riêng (server-sent events) nhưng chưa cần.
    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var r = await CompleteAsync(req, ct);
        if (!string.IsNullOrEmpty(r.Text)) await onDelta(r.Text);
        return r;
    }
}
