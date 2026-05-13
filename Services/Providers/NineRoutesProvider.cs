using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Providers;

/// 9routes (https://github.com/decolua/9router) — local OpenAI-compatible router.
/// Default base url = http://localhost:20128/v1. API key đọc từ Providers:NineRoutes:ApiKey.
/// Key KHÔNG bao giờ leak ra response/client.
public class NineRoutesProvider : IAiProvider
{
    public string Id => "nine-routes";
    public string Label => "9routes (OpenAI-compat)";

    /// 9routes hỗ trợ rất nhiều model upstream (cc/claude-*, sk/gpt-*, ...). Frontend
    /// có thể call /api/v1/providers để fetch và populate dropdown, hoặc gọi
    /// /api/v1/providers/nine-routes/models nếu cần live list từ upstream.
    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("cc/claude-haiku-4-5-20251001", "Claude Haiku 4.5", Recommended: true),
        new ProviderModel("cc/claude-sonnet-4-5-20250929", "Claude Sonnet 4.5"),
    };

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NineRoutesProvider> _log;

    public NineRoutesProvider(IHttpClientFactory http, IConfiguration cfg, ILogger<NineRoutesProvider> log)
    {
        _http = http; _cfg = cfg; _log = log;
    }

    private string BaseUrl =>
        _cfg["Providers:NineRoutes:BaseUrl"] ?? "http://localhost:20128/v1";

    private string? ApiKey =>
        _cfg["Providers:NineRoutes:ApiKey"] ??
        Environment.GetEnvironmentVariable("NINE_ROUTES_API_KEY");

    public async Task<IReadOnlyList<ProviderModel>> ListLiveModelsAsync(CancellationToken ct)
    {
        var client = _http.CreateClient("nine-routes");
        var url = $"{BaseUrl.TrimEnd('/')}/models";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        var key = ApiKey;
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.Add("Authorization", $"Bearer {key}");

        var resp = await client.SendAsync(msg, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException((int)resp.StatusCode, "9routes /models error", raw[..Math.Min(raw.Length, 400)]);

        // OpenAI-compat shape: { data: [{id, owned_by}, ...] }. Fallback: { models: [...] }
        var list = new List<ProviderModel>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            System.Text.Json.JsonElement arr = default;
            if (root.TryGetProperty("data", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array) arr = d;
            else if (root.TryGetProperty("models", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Array) arr = m;
            else return Models;

            foreach (var item in arr.EnumerateArray())
            {
                string? id = null, owner = null;
                if (item.ValueKind == System.Text.Json.JsonValueKind.String) id = item.GetString();
                else if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (item.TryGetProperty("id", out var iid) && iid.ValueKind == System.Text.Json.JsonValueKind.String) id = iid.GetString();
                    if (id == null && item.TryGetProperty("name", out var nm) && nm.ValueKind == System.Text.Json.JsonValueKind.String) id = nm.GetString();
                    if (item.TryGetProperty("owned_by", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String) owner = o.GetString();
                    else if (item.TryGetProperty("provider", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) owner = p.GetString();
                }
                if (string.IsNullOrWhiteSpace(id)) continue;
                var label = string.IsNullOrWhiteSpace(owner) ? id! : $"{id} · {owner}";
                list.Add(new ProviderModel(id!, label));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Parse 9routes /models failed");
            return Models;
        }
        return list.Count > 0 ? list : Models;
    }

    private HttpRequestMessage Build(object body, bool sse)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        var key = ApiKey;
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.Add("Authorization", $"Bearer {key}");
        if (sse) msg.Headers.Add("Accept", "text/event-stream");
        return msg;
    }

    private object BuildBody(string model, string prompt, int maxTokens, double temperature, string systemMsg, bool stream)
    {
        var messages = new[]
        {
            new { role = "system", content = systemMsg },
            new { role = "user",   content = prompt }
        };
        if (!stream) return new { model, max_tokens = maxTokens, temperature, messages };
        return new { model, max_tokens = maxTokens, temperature, stream = true,
                     stream_options = new { include_usage = true }, messages };
    }

    private static string EffectiveSystem(string? sys) =>
        string.IsNullOrWhiteSpace(sys) ? OpenCodeClient.DefaultSystem : sys!;

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(req.Model) ? Models[0].Id : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 8192;
        var temperature = req.Temperature ?? 0.3;
        var systemMsg = EffectiveSystem(req.System);

        var client = _http.CreateClient("nine-routes");
        using var msg = Build(BuildBody(model, req.Prompt, maxTokens, temperature, systemMsg, stream: false), sse: false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.SendAsync(msg, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new UpstreamException((int)resp.StatusCode, "9routes error", raw[..Math.Min(raw.Length, 800)]);

        // 9routes đôi khi trả SSE format ngay cả khi không stream — parse safely.
        var (text, inTok, outTok, finishReason) = ParseResponse(raw);

        if (string.IsNullOrEmpty(text))
        {
            return new CompleteResult("", model, inTok, outTok, sw.ElapsedMilliseconds, finishReason,
                Warning: "9routes trả response rỗng. Check model name + key.",
                RawUpstream: raw[..Math.Min(raw.Length, 2000)]);
        }
        return new CompleteResult(text, model, inTok, outTok, sw.ElapsedMilliseconds, finishReason);
    }

    private static (string text, int inTok, int outTok, string finishReason) ParseResponse(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("data:"))
        {
            // 9routes trả SSE format ngay cả non-stream. Walk chunks, concat text.
            var sb = new StringBuilder();
            int inTok = 0, outTok = 0;
            string finishReason = "";
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("data:")) continue;
                var payload = t.Substring(5).TrimStart();
                if (payload == "[DONE]" || string.IsNullOrEmpty(payload)) continue;
                try
                {
                    using var d = JsonDocument.Parse(payload);
                    var root = d.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var parsed = UpstreamParser.Parse(payload, "openai");
                    if (!string.IsNullOrEmpty(parsed.Text)) sb.Append(parsed.Text);
                    if (parsed.InputTokens > 0) inTok = parsed.InputTokens;
                    if (parsed.OutputTokens > 0) outTok = parsed.OutputTokens;
                    if (!string.IsNullOrEmpty(parsed.FinishReason)) finishReason = parsed.FinishReason;
                }
                catch { continue; }
            }
            return (sb.ToString(), inTok, outTok, finishReason);
        }
        var p = UpstreamParser.Parse(raw, "openai");
        return (p.Text, p.InputTokens, p.OutputTokens, p.FinishReason);
    }

    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(req.Model) ? Models[0].Id : req.Model!;
        var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 8192;
        var temperature = req.Temperature ?? 0.3;
        var systemMsg = EffectiveSystem(req.System);

        var client = _http.CreateClient("nine-routes");
        using var msg = Build(BuildBody(model, req.Prompt, maxTokens, temperature, systemMsg, stream: true), sse: true);

        var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new UpstreamException((int)resp.StatusCode, "9routes stream error", errBody[..Math.Min(errBody.Length, 800)]);
        }

        await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(upstream, Encoding.UTF8);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fullText = new StringBuilder();
        int inTok = 0, outTok = 0;
        string finishReason = "";
        int chunks = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:")) continue;
            var payload = line.Substring(5).TrimStart();
            if (payload == "[DONE]") break;

            string? delta = null;
            try
            {
                using var d = JsonDocument.Parse(payload);
                var root = d.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in ch.EnumerateArray())
                    {
                        if (UpstreamParser.TryObj(c, "delta", out var dd) &&
                            UpstreamParser.TryObj(dd, "content", out var co) &&
                            co.ValueKind == JsonValueKind.String)
                        {
                            var s = co.GetString();
                            if (!string.IsNullOrEmpty(s)) delta = (delta ?? "") + s;
                        }
                        if (UpstreamParser.TryObj(c, "finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        {
                            var v = fr.GetString();
                            if (!string.IsNullOrEmpty(v)) finishReason = v!;
                        }
                    }
                }
                if (root.TryGetProperty("usage", out var usg))
                {
                    if (UpstreamParser.TryObj(usg, "prompt_tokens",     out var i)) inTok  = i.GetInt32();
                    if (UpstreamParser.TryObj(usg, "completion_tokens", out var o)) outTok = o.GetInt32();
                }
            }
            catch (JsonException) { continue; }
            catch (InvalidOperationException) { continue; }

            if (!string.IsNullOrEmpty(delta))
            {
                fullText.Append(delta);
                chunks++;
                await onDelta(delta);
            }
        }
        sw.Stop();
        return new CompleteResult(fullText.ToString(), model, inTok, outTok, sw.ElapsedMilliseconds, finishReason, Attempts: chunks);
    }
}
