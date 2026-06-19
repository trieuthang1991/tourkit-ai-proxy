using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Providers;

/// OpenCode Go (Anthropic + OpenAI dual-protocol).
/// API key đọc từ Providers:OpenCode:ApiKey (appsettings) hoặc OPENCODE_API_KEY env var.
/// Key KHÔNG bao giờ leak ra response.
public class OpenCodeProvider : IAiProvider
{
    public string Id => "opencode-go";
    public string Label => "OpenCode Go";

    public IReadOnlyList<ProviderModel> Models { get; } = new[]
    {
        new ProviderModel("deepseek-v4-flash", "DeepSeek V4 Flash", Recommended: true),
        new ProviderModel("deepseek-v4-pro",   "DeepSeek V4 Pro"),
        new ProviderModel("minimax-m2.5",      "MiniMax M2.5"),
        new ProviderModel("minimax-m2.7",      "MiniMax M2.7"),
        new ProviderModel("kimi-k2.6",         "Kimi K2.6"),
        new ProviderModel("glm-5.1",           "GLM 5.1"),
        new ProviderModel("qwen-3.6",          "Qwen 3.6"),
    };

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OpenCodeProvider> _log;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly TenantQuotaStore _quota;

    public OpenCodeProvider(IHttpClientFactory http, IConfiguration cfg, ILogger<OpenCodeProvider> log,
        AiUsageLog usage, AiCallContext ctx, TenantQuotaStore quota)
    {
        _http = http; _cfg = cfg; _log = log; _usage = usage; _ctx = ctx; _quota = quota;
    }

    // Default model: chọn model có Recommended:true trong catalog local, fallback Models[0].
    private string DefaultModel()
        => Models.FirstOrDefault(m => m.Recommended)?.Id ?? Models[0].Id;

    private void LogUsage(string model, int inTok, int outTok, long ms, string status = "ok")
    {
        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, "opencode-go", model, inTok, outTok, ms, status: status);
        // Consume 1 lượt quota cho tenant chỉ khi gọi thành công + có tenant (system call không tenant → skip).
        if (status == "ok" && !string.IsNullOrEmpty(c.Tenant)) _quota.Consume(c.Tenant);
    }

    /// Throw QuotaExhaustedException nếu tenant đã hết quota. System call (no tenant) → skip.
    private void EnsureQuota()
    {
        var t = _ctx.Resolve().Tenant;
        if (string.IsNullOrEmpty(t)) return;
        if (!_quota.IsAvailable(t))
        {
            var s = _quota.Snapshot(t);
            throw new QuotaExhaustedException(t, s.Limit, s.Used);
        }
    }

    private string? ApiKey =>
        _cfg["Providers:OpenCode:ApiKey"] ??
        _cfg["OPENCODE_API_KEY"] ??
        Environment.GetEnvironmentVariable("OPENCODE_API_KEY");

    private static (string path, string fmt) RouteModel(string model) => model switch
    {
        "minimax-m2.5" or "minimax-m2.7" => ("zen/go/v1/messages", "anthropic"),
        _                                 => ("zen/go/v1/chat/completions", "openai")
    };

    private static string EffectiveSystem(string? sys) =>
        string.IsNullOrWhiteSpace(sys) ? OpenCodeClient.DefaultSystem : sys!;

    private static object BuildBody(string model, string fmt, string prompt, int maxTokens, double temperature, string systemMsg, bool stream)
        => fmt == "anthropic"
            ? new
              {
                  model, max_tokens = maxTokens, temperature, stream, system = systemMsg,
                  messages = new[] { new { role = "user", content = prompt } }
              }
            : (stream
                ? (object)new
                  {
                      model, max_tokens = maxTokens, temperature, stream,
                      stream_options = new { include_usage = true },
                      messages = new[]
                      {
                          new { role = "system", content = systemMsg },
                          new { role = "user",   content = prompt }
                      }
                  }
                : new
                  {
                      model, max_tokens = maxTokens, temperature,
                      messages = new[]
                      {
                          new { role = "system", content = systemMsg },
                          new { role = "user",   content = prompt }
                      }
                  });

    private HttpRequestMessage Build(string path, string fmt, object body, bool sse, string key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {key}");
        if (sse) msg.Headers.Add("Accept", "text/event-stream");
        if (fmt == "anthropic")
        {
            msg.Headers.Add("anthropic-version", "2023-06-01");
            msg.Headers.Add("x-api-key", key);   // OpenCode Go /messages reject bearer-only
        }
        return msg;
    }

    public async Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        EnsureQuota();
        var key = ApiKey ?? throw new InvalidOperationException("OPENCODE_API_KEY chưa cấu hình");
        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;
        var temperature = req.Temperature ?? 0.3;
        var systemMsg = EffectiveSystem(req.System);
        var (path, fmt) = RouteModel(model);
        var client = _http.CreateClient("opencode");

        const int MAX_TRANSIENT_RETRIES = 2;
        const int MAX_BUDGET_BUMPS      = 1;
        const int BUDGET_CAP            = 16384;
        int budget = req.MaxTokens is > 0 ? req.MaxTokens.Value : 8192;
        int transientAttempt = 0, budgetBumps = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = "", finishReason = "", lastRaw = "";
        int inTok = 0, outTok = 0;

        while (true)
        {
            using var msg = Build(path, fmt, BuildBody(model, fmt, req.Prompt, budget, temperature, systemMsg, stream: false), sse: false, key);

            HttpResponseMessage resp;
            try { resp = await client.SendAsync(msg, ct); }
            catch (Exception ex)
            {
                if (transientAttempt < MAX_TRANSIENT_RETRIES)
                {
                    var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, transientAttempt));
                    _log.LogWarning(ex, "[opencode] network → retry #{N} sau {Ms}ms", transientAttempt + 1, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                    transientAttempt++;
                    continue;
                }
                throw new UpstreamException(502, "Không kết nối được OpenCode", ex.Message);
            }

            lastRaw = await resp.Content.ReadAsStringAsync(ct);
            var status = (int)resp.StatusCode;

            if ((status == 408 || status == 429 || status >= 500) && transientAttempt < MAX_TRANSIENT_RETRIES)
            {
                var delay = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, transientAttempt));
                _log.LogWarning("[opencode] upstream {Status} → retry #{N}", status, transientAttempt + 1);
                await Task.Delay(delay, ct);
                transientAttempt++;
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new UpstreamException(status, "Upstream error", lastRaw);

            var parsed = UpstreamParser.Parse(lastRaw, fmt);
            text = parsed.Text; inTok = parsed.InputTokens; outTok = parsed.OutputTokens; finishReason = parsed.FinishReason;

            if (string.IsNullOrEmpty(text) && finishReason == "length" && budgetBumps < MAX_BUDGET_BUMPS && budget < BUDGET_CAP)
            {
                var newBudget = Math.Min(budget * 2, BUDGET_CAP);
                _log.LogWarning("[opencode] empty + finish=length → bump {Old}→{New}", budget, newBudget);
                budget = newBudget;
                budgetBumps++;
                continue;
            }
            break;
        }
        sw.Stop();
        var attempts = transientAttempt + budgetBumps + 1;

        if (string.IsNullOrEmpty(text))
        {
            var hint = finishReason == "length"
                ? "Upstream cắt vì max_tokens (đã auto-bump). Đổi model khác."
                : "Parse trả text rỗng.";
            LogUsage(model, inTok, outTok, sw.ElapsedMilliseconds, status: "empty");
            return new CompleteResult("", model, inTok, outTok, sw.ElapsedMilliseconds, finishReason, attempts,
                Warning: hint,
                RawUpstream: lastRaw[..Math.Min(lastRaw.Length, 2000)]);
        }

        LogUsage(model, inTok, outTok, sw.ElapsedMilliseconds);
        return new CompleteResult(text, model, inTok, outTok, sw.ElapsedMilliseconds, finishReason, attempts);
    }

    public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        EnsureQuota();
        var key = ApiKey ?? throw new InvalidOperationException("OPENCODE_API_KEY chưa cấu hình");
        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;
        var temperature = req.Temperature ?? 0.3;
        var budget = req.MaxTokens is > 0 ? req.MaxTokens.Value : 8192;
        var systemMsg = EffectiveSystem(req.System);
        var (path, fmt) = RouteModel(model);
        var client = _http.CreateClient("opencode");

        using var msg = Build(path, fmt, BuildBody(model, fmt, req.Prompt, budget, temperature, systemMsg, stream: true), sse: true, key);

        var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new UpstreamException((int)resp.StatusCode, "Upstream error", errBody[..Math.Min(errBody.Length, 800)]);
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

                if (fmt == "anthropic")
                {
                    if (root.TryGetProperty("type", out var tp))
                    {
                        var typeStr = tp.GetString();
                        if (typeStr == "content_block_delta" && root.TryGetProperty("delta", out var dd))
                        {
                            if (UpstreamParser.TryObj(dd, "text", out var tx)) delta = tx.GetString();
                        }
                        else if (typeStr == "message_delta")
                        {
                            if (root.TryGetProperty("delta", out var md) &&
                                UpstreamParser.TryObj(md, "stop_reason", out var sr) &&
                                sr.ValueKind == JsonValueKind.String)
                            {
                                var v = sr.GetString() ?? "";
                                finishReason = v == "max_tokens" ? "length" : v;
                            }
                            if (root.TryGetProperty("usage", out var usg) &&
                                UpstreamParser.TryObj(usg, "output_tokens", out var ot)) outTok = ot.GetInt32();
                        }
                        else if (typeStr == "message_start" &&
                                 root.TryGetProperty("message", out var mm) &&
                                 UpstreamParser.TryObj(mm, "usage", out var usg2) &&
                                 UpstreamParser.TryObj(usg2, "input_tokens", out var it))
                        {
                            inTok = it.GetInt32();
                        }
                    }
                }
                else
                {
                    if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in ch.EnumerateArray())
                        {
                            if (UpstreamParser.TryObj(c, "delta", out var dd))
                            {
                                if (UpstreamParser.TryObj(dd, "content", out var co) && co.ValueKind == JsonValueKind.String)
                                {
                                    var s = co.GetString();
                                    if (!string.IsNullOrEmpty(s)) delta = (delta ?? "") + s;
                                }
                                if (string.IsNullOrEmpty(delta))
                                {
                                    foreach (var name in new[] { "reasoning_content", "reasoning" })
                                    {
                                        if (UpstreamParser.TryObj(dd, name, out var rc) && rc.ValueKind == JsonValueKind.String)
                                        {
                                            var s = rc.GetString();
                                            if (!string.IsNullOrEmpty(s)) { delta = (delta ?? "") + s; break; }
                                        }
                                    }
                                }
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
        LogUsage(model, inTok, outTok, sw.ElapsedMilliseconds);
        return new CompleteResult(fullText.ToString(), model, inTok, outTok, sw.ElapsedMilliseconds, finishReason, Attempts: chunks);
    }
}

public class UpstreamException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public UpstreamException(int status, string message, string body) : base(message)
    {
        Status = status; Body = body;
    }
}
