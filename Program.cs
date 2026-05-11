using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ─── CORS: allow Tourkit frontend (file:// + localhost + your deployed host) ───
builder.Services.AddCors(o => o.AddPolicy("tourkit", p => p
    .WithOrigins(
        "http://localhost:3000",
        "http://localhost:5173",
        "http://localhost:8080",
        "https://tourkit.vn",
        "http://localhost:5080"
    )
    .AllowAnyHeader()
    .AllowAnyMethod()
    .SetIsOriginAllowed(_ => true)   // dev only — siết lại khi deploy prod
));

builder.Services.AddHttpClient("opencode", c =>
{
    c.BaseAddress = new Uri("https://opencode.ai/");
    c.Timeout = TimeSpan.FromSeconds(120);
});

// ─── Model registry: route to correct endpoint + format ──────────────────────
static (string path, string fmt) RouteModel(string model) => model switch
{
    "minimax-m2.5" or "minimax-m2.7" => ("zen/go/v1/messages", "anthropic"),
    _                                 => ("zen/go/v1/chat/completions", "openai"),
};

// ─── Parser: Anthropic + OpenAI shapes, normalized finishReason ──────────────
static (string text, int inTok, int outTok, string finishReason) ParseUpstream(string raw, string fmt)
{
    string text = "", finishReason = "";
    int inTok = 0, outTok = 0;
    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;

    if (fmt == "anthropic")
    {
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("text", out var t)) text += t.GetString();
        if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
        {
            var v = sr.GetString() ?? "";
            finishReason = v == "max_tokens" ? "length" : v;   // normalize → OpenAI naming
        }
        if (root.TryGetProperty("usage", out var usg))
        {
            if (usg.TryGetProperty("input_tokens", out var i)) inTok = i.GetInt32();
            if (usg.TryGetProperty("output_tokens", out var o)) outTok = o.GetInt32();
        }
    }
    else
    {
        if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in ch.EnumerateArray())
            {
                if (c.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString() ?? "";

                if (c.TryGetProperty("message", out var m))
                {
                    if (m.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrEmpty(s)) text += s;
                    }
                    if (string.IsNullOrEmpty(text))
                    {
                        foreach (var name in new[] { "reasoning_content", "reasoning" })
                        {
                            if (m.TryGetProperty(name, out var rc) && rc.ValueKind == JsonValueKind.String)
                            {
                                var s = rc.GetString();
                                if (!string.IsNullOrEmpty(s)) { text += s; break; }
                            }
                        }
                    }
                }
                if (string.IsNullOrEmpty(text) && c.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var dc))
                    text += dc.GetString();
                if (string.IsNullOrEmpty(text) && c.TryGetProperty("text", out var pt))
                    text += pt.GetString();
            }
        }
        if (root.TryGetProperty("usage", out var usg))
        {
            if (usg.TryGetProperty("prompt_tokens", out var i)) inTok = i.GetInt32();
            if (usg.TryGetProperty("completion_tokens", out var o)) outTok = o.GetInt32();
        }
    }
    return (text, inTok, outTok, finishReason);
}

builder.Services.AddSingleton<UsageTracker>();

var app = builder.Build();
app.UseCors("tourkit");

// ─── Serve frontend (wwwroot) — frontend ĐÃ NẰM TRONG project API ────────────
// Truy cập http://localhost:5080 sẽ tự load index.html từ wwwroot/
app.UseDefaultFiles();   // tự tìm index.html
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true,   // .jsx / .babel không có MIME chuẩn
    DefaultContentType = "text/plain",
    OnPrepareResponse = ctx =>
    {
        // Dev: tắt cache cho .jsx / .js / .css để edit là refresh thấy ngay
        var p = ctx.File.Name;
        if (p.EndsWith(".jsx") || p.EndsWith(".js") || p.EndsWith(".css") || p.EndsWith(".html"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});
app.Logger.LogInformation("Frontend wwwroot: {Path}",
    Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

// ─── Endpoints ────────────────────────────────────────────────────────────────

app.MapGet("/healthz", () => Results.Json(new
{
    ok = true,
    service = "Tourkit AI Proxy",
    endpoints = new[] { "POST /api/ai/complete", "GET /api/ai/usage", "GET /api/ai/models" }
}));

app.MapGet("/api/ai/models", () => Results.Json(new[]
{
    new { id = "deepseek-v4-flash", label = "DeepSeek V4 Flash", recommended = true },
    new { id = "deepseek-v4-pro",   label = "DeepSeek V4 Pro",   recommended = false },
    new { id = "minimax-m2.5",      label = "MiniMax M2.5",      recommended = false },
    new { id = "minimax-m2.7",      label = "MiniMax M2.7",      recommended = false },
    new { id = "kimi-k2.6",         label = "Kimi K2.6",         recommended = false },
    new { id = "glm-5.1",           label = "GLM 5.1",           recommended = false },
    new { id = "qwen-3.6",          label = "Qwen 3.6",          recommended = false }
}));

app.MapGet("/api/ai/usage", (UsageTracker u) => Results.Json(u.Snapshot()));

app.MapPost("/api/ai/complete", async (
    CompleteRequest req,
    IHttpClientFactory http,
    UsageTracker usage,
    IConfiguration cfg,
    ILogger<Program> log) =>
{
    var key = cfg["OPENCODE_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
        return Results.Json(new { error = "Server chưa cấu hình OPENCODE_API_KEY" }, statusCode: 500);

    var model = string.IsNullOrWhiteSpace(req.Model) ? "deepseek-v4-flash" : req.Model!;
    var temperature = req.Temperature ?? 0.3;   // thấp hơn cho JSON/structured output ổn định
    // System message: cản model "ngồi nghĩ", ép trả thẳng output. Client có thể override.
    var systemMsg = string.IsNullOrWhiteSpace(req.System)
        ? "Output ONLY the requested format. No thinking, no explanation, no markdown fences. Respond directly with the final answer. / Trả lời trực tiếp, không suy luận, không markdown."
        : req.System!;

    var (path, fmt) = RouteModel(model);
    var client = http.CreateClient("opencode");

    // Retry policy:
    //   transient (network exception / 408 / 429 / 5xx) → up to 2 retries với exponential backoff
    //   text rỗng + finish=length (model đốt budget cho reasoning) → 1 retry với maxTokens × 2
    const int MAX_TRANSIENT_RETRIES = 2;
    const int MAX_BUDGET_BUMPS = 1;
    const int BUDGET_CAP = 16384;
    int budget = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
    int transientAttempt = 0;
    int budgetBumps = 0;

    var totalSw = System.Diagnostics.Stopwatch.StartNew();
    string text = "", finishReason = "", lastRaw = "";
    int inTok = 0, outTok = 0;

    while (true)
    {
        object body = fmt == "anthropic"
            ? (object)new {
                model, max_tokens = budget, temperature, system = systemMsg,
                messages = new[] { new { role = "user", content = req.Prompt } }
            }
            : new {
                model, max_tokens = budget, temperature,
                messages = new[] {
                    new { role = "system", content = systemMsg },
                    new { role = "user",   content = req.Prompt }
                }
            };

        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {key}");
        if (fmt == "anthropic")
        {
            msg.Headers.Add("anthropic-version", "2023-06-01");
            msg.Headers.Add("x-api-key", key);   // OpenCode Go /messages cần header này
        }

        HttpResponseMessage resp;
        try { resp = await client.SendAsync(msg); }
        catch (Exception ex)
        {
            if (transientAttempt < MAX_TRANSIENT_RETRIES)
            {
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, transientAttempt));
                log.LogWarning(ex, "Network error → retry #{N} sau {Ms}ms", transientAttempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay);
                transientAttempt++;
                continue;
            }
            log.LogError(ex, "Upstream call failed sau {N} lần thử", MAX_TRANSIENT_RETRIES + 1);
            return Results.Json(new { error = "Không kết nối được OpenCode", detail = ex.Message }, statusCode: 502);
        }

        lastRaw = await resp.Content.ReadAsStringAsync();
        var status = (int)resp.StatusCode;

        if ((status == 408 || status == 429 || status >= 500) && transientAttempt < MAX_TRANSIENT_RETRIES)
        {
            var delay = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, transientAttempt));
            log.LogWarning("Upstream {Status} → retry #{N} sau {Ms}ms", status, transientAttempt + 1, delay.TotalMilliseconds);
            await Task.Delay(delay);
            transientAttempt++;
            continue;
        }

        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Upstream {Status} non-retryable: {Body}", status, lastRaw);
            return Results.Json(new { error = "Upstream error", status, body = lastRaw }, statusCode: status);
        }

        try { (text, inTok, outTok, finishReason) = ParseUpstream(lastRaw, fmt); }
        catch (Exception ex)
        {
            log.LogError(ex, "Parse upstream failed");
            return Results.Json(new { error = "Parse error", raw = lastRaw[..Math.Min(lastRaw.Length, 500)] }, statusCode: 500);
        }

        // Reasoning model đốt sạch budget cho thinking → bump rồi thử lại
        if (string.IsNullOrEmpty(text) && finishReason == "length" && budgetBumps < MAX_BUDGET_BUMPS && budget < BUDGET_CAP)
        {
            var newBudget = Math.Min(budget * 2, BUDGET_CAP);
            log.LogWarning("Empty content + finish=length → bump maxTokens {Old} → {New}", budget, newBudget);
            budget = newBudget;
            budgetBumps++;
            continue;
        }

        break;
    }
    totalSw.Stop();
    var attempts = transientAttempt + budgetBumps + 1;

    if (string.IsNullOrEmpty(text))
    {
        log.LogWarning("Empty text sau {Attempts} attempts (finish={Finish})", attempts, finishReason);
        var hint = finishReason == "length"
            ? "Upstream cắt vì max_tokens (đã auto-bump nhưng vẫn rỗng). Model đốt budget cho reasoning — đổi sang model khác."
            : "Parse trả text rỗng. Raw upstream để debug:";
        return Results.Json(new {
            text = "",
            model, latencyMs = totalSw.ElapsedMilliseconds,
            inputTokens = inTok, outputTokens = outTok,
            finishReason, attempts,
            warning = hint,
            rawUpstream = lastRaw[..Math.Min(lastRaw.Length, 2000)]
        });
    }

    usage.Track(model, inTok, outTok, totalSw.ElapsedMilliseconds);

    return Results.Json(new
    {
        text, model,
        latencyMs = totalSw.ElapsedMilliseconds,
        inputTokens = inTok,
        outputTokens = outTok,
        finishReason,
        attempts
    });
});

// ─── Streaming endpoint: relay upstream SSE → client SSE ─────────────────────
// Cùng request shape với /api/ai/complete. Trả Server-Sent Events:
//   data: {"delta":"chữ chunk..."}
//   data: {"delta":"chữ tiếp..."}
//   ...
//   data: {"done":true,"text":"<full text>","inputTokens":..,"outputTokens":..,"latencyMs":..,"model":"..","finishReason":".."}
// Hoặc khi lỗi:
//   data: {"error":"..","status":..}
//   data: {"done":true}
app.MapPost("/api/ai/stream", async (
    CompleteRequest req,
    HttpContext ctx,
    IHttpClientFactory http,
    UsageTracker usage,
    IConfiguration cfg,
    ILogger<Program> log) =>
{
    var key = cfg["OPENCODE_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Server chưa cấu hình OPENCODE_API_KEY" });
        return;
    }

    var model = string.IsNullOrWhiteSpace(req.Model) ? "deepseek-v4-flash" : req.Model!;
    var temperature = req.Temperature ?? 0.3;
    var budget = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
    var systemMsg = string.IsNullOrWhiteSpace(req.System)
        ? "Output ONLY the requested format. No thinking, no explanation, no markdown fences. Respond directly with the final answer. / Trả lời trực tiếp, không suy luận, không markdown."
        : req.System!;

    var (path, fmt) = RouteModel(model);
    var client = http.CreateClient("opencode");

    // Body với stream:true
    // OpenAI format cần stream_options.include_usage để gửi usage trong chunk cuối
    object body = fmt == "anthropic"
        ? (object)new {
            model, max_tokens = budget, temperature, stream = true, system = systemMsg,
            messages = new[] { new { role = "user", content = req.Prompt } }
        }
        : new {
            model, max_tokens = budget, temperature, stream = true,
            stream_options = new { include_usage = true },
            messages = new[] {
                new { role = "system", content = systemMsg },
                new { role = "user",   content = req.Prompt }
            }
        };

    using var msg = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    msg.Headers.Add("Authorization", $"Bearer {key}");
    msg.Headers.Add("Accept", "text/event-stream");
    if (fmt == "anthropic")
    {
        msg.Headers.Add("anthropic-version", "2023-06-01");
        msg.Headers.Add("x-api-key", key);
    }

    // Setup client response as SSE
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";   // disable nginx buffering nếu có
    // Disable Kestrel response buffering — ép từng FlushAsync flush ngay xuống wire
    var bodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
    bodyFeature?.DisableBuffering();
    await ctx.Response.StartAsync(ctx.RequestAborted);
    log.LogInformation("[stream] start model={Model} fmt={Fmt}", model, fmt);

    var totalSw = System.Diagnostics.Stopwatch.StartNew();
    var fullText = new StringBuilder();
    int inTok = 0, outTok = 0;
    string finishReason = "";

    async Task WriteEventAsync(object payload)
    {
        var line = "data: " + JsonSerializer.Serialize(payload) + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    HttpResponseMessage resp;
    try
    {
        resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Stream upstream connect failed");
        await WriteEventAsync(new { error = "Không kết nối được OpenCode", detail = ex.Message });
        await WriteEventAsync(new { done = true });
        return;
    }

    if (!resp.IsSuccessStatusCode)
    {
        var errBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        log.LogWarning("Stream upstream {Status}: {Body}", (int)resp.StatusCode, errBody);
        await WriteEventAsync(new { error = "Upstream error", status = (int)resp.StatusCode, body = errBody[..Math.Min(errBody.Length, 800)] });
        await WriteEventAsync(new { done = true });
        return;
    }

    Stream upstream;
    StreamReader reader;
    try
    {
        upstream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
        reader = new StreamReader(upstream, Encoding.UTF8);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[stream] open upstream body failed");
        await WriteEventAsync(new { error = "open upstream body failed", detail = ex.Message });
        await WriteEventAsync(new { done = true });
        return;
    }

    string? line2;
    int chunkCount = 0;
    try
    {
    while ((line2 = await reader.ReadLineAsync(ctx.RequestAborted)) != null)
    {
        if (string.IsNullOrWhiteSpace(line2)) continue;
        if (!line2.StartsWith("data:")) continue;
        var payload = line2.Substring(5).TrimStart();
        if (payload == "[DONE]") break;

        string? delta = null;
        try
        {
            using var d = JsonDocument.Parse(payload);
            var root = d.RootElement;

            if (fmt == "anthropic")
            {
                // events: content_block_delta {delta:{type:"text_delta",text:"..."}}
                //         message_delta {delta:{stop_reason:"..."}, usage:{output_tokens:..}}
                //         message_start  {message:{usage:{input_tokens:..}}}
                if (root.TryGetProperty("type", out var tp))
                {
                    var typeStr = tp.GetString();
                    if (typeStr == "content_block_delta" && root.TryGetProperty("delta", out var dd))
                    {
                        if (dd.TryGetProperty("text", out var tx)) delta = tx.GetString();
                    }
                    else if (typeStr == "message_delta")
                    {
                        if (root.TryGetProperty("delta", out var md) &&
                            md.TryGetProperty("stop_reason", out var sr) &&
                            sr.ValueKind == JsonValueKind.String)
                        {
                            var v = sr.GetString() ?? "";
                            finishReason = v == "max_tokens" ? "length" : v;
                        }
                        if (root.TryGetProperty("usage", out var usg) &&
                            usg.TryGetProperty("output_tokens", out var ot)) outTok = ot.GetInt32();
                    }
                    else if (typeStr == "message_start" &&
                             root.TryGetProperty("message", out var mm) &&
                             mm.TryGetProperty("usage", out var usg2) &&
                             usg2.TryGetProperty("input_tokens", out var it))
                    {
                        inTok = it.GetInt32();
                    }
                }
            }
            else
            {
                // OpenAI: {choices:[{delta:{content:"..." | reasoning_content:"..."}, finish_reason}]}
                // Final chunk có usage (nếu upstream emit) hoặc finish_reason ở choice
                if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in ch.EnumerateArray())
                    {
                        if (c.TryGetProperty("delta", out var dd))
                        {
                            if (dd.TryGetProperty("content", out var co) && co.ValueKind == JsonValueKind.String)
                            {
                                var s = co.GetString();
                                if (!string.IsNullOrEmpty(s)) delta = (delta ?? "") + s;
                            }
                            // Fallback cho reasoning models (DeepSeek/Kimi) khi content rỗng
                            if (string.IsNullOrEmpty(delta))
                            {
                                foreach (var name in new[] { "reasoning_content", "reasoning" })
                                {
                                    if (dd.TryGetProperty(name, out var rc) && rc.ValueKind == JsonValueKind.String)
                                    {
                                        var s = rc.GetString();
                                        if (!string.IsNullOrEmpty(s)) { delta = (delta ?? "") + s; break; }
                                    }
                                }
                            }
                        }
                        if (c.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        {
                            var v = fr.GetString();
                            if (!string.IsNullOrEmpty(v)) finishReason = v!;
                        }
                    }
                }
                if (root.TryGetProperty("usage", out var usg))
                {
                    if (usg.TryGetProperty("prompt_tokens", out var i)) inTok = i.GetInt32();
                    if (usg.TryGetProperty("completion_tokens", out var o)) outTok = o.GetInt32();
                }
            }
        }
        catch (JsonException) { continue; }

        if (!string.IsNullOrEmpty(delta))
        {
            fullText.Append(delta);
            chunkCount++;
            await WriteEventAsync(new { delta });
        }
    }
    }
    catch (OperationCanceledException)
    {
        log.LogInformation("[stream] client aborted (sent {N} chunks, {Len} chars)", chunkCount, fullText.Length);
        reader.Dispose(); upstream.Dispose();
        return;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[stream] read loop crashed (sent {N} chunks, {Len} chars)", chunkCount, fullText.Length);
        try {
            await WriteEventAsync(new { error = "stream read failed", detail = ex.Message });
            await WriteEventAsync(new { done = true });
        } catch { }
        reader.Dispose(); upstream.Dispose();
        return;
    }
    reader.Dispose(); upstream.Dispose();

    totalSw.Stop();
    log.LogInformation("[stream] done model={Model} chunks={N} len={Len} latencyMs={Ms} in={In} out={Out}",
        model, chunkCount, fullText.Length, totalSw.ElapsedMilliseconds, inTok, outTok);
    if (outTok > 0) usage.Track(model, inTok, outTok, totalSw.ElapsedMilliseconds);
    await WriteEventAsync(new
    {
        done = true,
        text = fullText.ToString(),
        model,
        latencyMs = totalSw.ElapsedMilliseconds,
        inputTokens = inTok,
        outputTokens = outTok,
        finishReason
    });
});

app.Run();

// ─── DTOs ────────────────────────────────────────────────────────────────────
public record CompleteRequest(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("maxTokens")] int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("system")] string? System
);

public class UsageTracker
{
    private readonly object _lock = new();
    private long _calls, _inTok, _outTok, _totalMs;
    private readonly Dictionary<string, long> _byModel = new();

    public void Track(string model, int inTok, int outTok, long ms)
    {
        lock (_lock)
        {
            _calls++;
            _inTok += inTok;
            _outTok += outTok;
            _totalMs += ms;
            _byModel[model] = _byModel.GetValueOrDefault(model) + 1;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            // DeepSeek V4 Pro pricing (OpenCode Go flat $10/mo, but here we estimate retail)
            var costUsd = (_inTok * 0.27 + _outTok * 1.10) / 1_000_000.0;
            return new
            {
                calls = _calls,
                inputTokens = _inTok,
                outputTokens = _outTok,
                avgLatencyMs = _calls == 0 ? 0 : _totalMs / _calls,
                estimatedCostUsd = Math.Round(costUsd, 4),
                byModel = _byModel
            };
        }
    }
}
