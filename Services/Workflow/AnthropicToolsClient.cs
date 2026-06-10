// Services/Workflow/AnthropicToolsClient.cs
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Reusable Anthropic native tools API client cho các feature single-shot hoặc multi-step:
/// Customer Review / Visa / Deal / TourBuilder.
///
/// Khác Chat (NativeToolUseAgent) ở chỗ:
/// - Chat trả TEXT cuối (phân tích), output đa dạng
/// - Đây trả STRUCTURED OBJECT từ "terminal tool" (vd submit_review) — schema-enforced
///
/// Loop:
/// 1. Call /messages với tools[] + messages
/// 2. AI có thể trả tool_use blocks → caller chạy handler tương ứng → append tool_result
/// 3. Loop tới khi AI gọi terminal tool (vd submit_review) → trả input của tool đó
/// 4. Max iterations / wall clock guard
/// </summary>
public class AnthropicToolsClient
{
    private readonly HttpClient _http;
    private readonly ProviderKeyStore _keys;
    private readonly ILogger<AnthropicToolsClient> _log;

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const int MaxIterations = 5;
    private const int WallClockSec = 60;
    private const int MaxTransientRetries = 2;

    public AnthropicToolsClient(IHttpClientFactory httpFactory, ProviderKeyStore keys, ILogger<AnthropicToolsClient> log)
    {
        _http = httpFactory.CreateClient();
        _keys = keys;
        _log = log;
    }

    /// <summary>
    /// Chạy agentic loop với native tools, dừng khi AI gọi terminal tool.
    /// </summary>
    /// <param name="systemPrompt">System message</param>
    /// <param name="userPrompt">User message (turn đầu)</param>
    /// <param name="tools">Tool schemas (JSON array)</param>
    /// <param name="terminalToolName">Tên tool chấm dứt loop (vd "submit_review")</param>
    /// <param name="toolHandler">Async handler: tool name + input JSON → tool_result text. Trả null nếu tool name là terminal.</param>
    /// <param name="apiKey">Anthropic API key (caller resolve từ session/config/header)</param>
    /// <param name="model">Anthropic model (default claude-sonnet-4-5)</param>
    /// <param name="maxTokens">max_tokens per turn</param>
    /// <param name="trace">Optional trace collector cho debug</param>
    /// <param name="onIteration">Optional callback bắn trước mỗi /messages call (iter#). Dùng cho UI emit stage "calling-iter-N".</param>
    /// <returns>JSON input của terminal tool call (đã validate qua schema). Null nếu AI không gọi terminal.</returns>
    public async Task<ToolsResult> RunAsync(
        string systemPrompt, string userPrompt,
        JsonElement[] tools, string terminalToolName,
        Func<string, JsonElement, CancellationToken, Task<string>> toolHandler,
        string apiKey, string model = "claude-sonnet-4-5",
        int maxTokens = 4000,
        TraceCollector? trace = null,
        Func<int, Task>? onIteration = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Anthropic API key trống — cần config Providers:Anthropic:ApiKey hoặc client gửi qua apiKey.");

        using var wallClock = new CancellationTokenSource(TimeSpan.FromSeconds(WallClockSec));
        using var linked    = CancellationTokenSource.CreateLinkedTokenSource(ct, wallClock.Token);

        // messages history qua các turn — append cả assistant turn + user turn (tool_result)
        var messages = new List<object>
        {
            new { role = "user", content = userPrompt }
        };

        int  iteration   = 0;
        int  totalInTok  = 0;
        int  totalOutTok = 0;
        long totalLat    = 0;
        JsonElement? terminalInput = null;
        string? warning = null;

        while (iteration < MaxIterations)
        {
            iteration++;
            if (onIteration != null) await onIteration(iteration);
            var iterTimer = trace?.Begin($"anthropic_iter{iteration}");

            var (doc, lat) = await PostAsync(apiKey, model, systemPrompt, tools, messages, maxTokens, linked.Token);
            totalLat += lat;

            using (doc)
            {
                var root  = doc.RootElement;
                var usage = root.GetProperty("usage");
                totalInTok  += usage.GetProperty("input_tokens").GetInt32();
                totalOutTok += usage.GetProperty("output_tokens").GetInt32();

                var stopReason = root.GetProperty("stop_reason").GetString();
                var toolUseBlocks = new List<(string Id, string Name, JsonElement Input)>();
                var assistantContent = new List<object>();

                foreach (var block in root.GetProperty("content").EnumerateArray())
                {
                    var bt = block.GetProperty("type").GetString();
                    if (bt == "text")
                    {
                        assistantContent.Add(new { type = "text", text = block.GetProperty("text").GetString() });
                    }
                    else if (bt == "tool_use")
                    {
                        var id    = block.GetProperty("id").GetString()!;
                        var name  = block.GetProperty("name").GetString()!;
                        var input = block.GetProperty("input").Clone();
                        toolUseBlocks.Add((id, name, input));
                        assistantContent.Add(new {
                            type  = "tool_use",
                            id, name,
                            input = JsonSerializer.Deserialize<JsonElement>(input.GetRawText())
                        });
                    }
                }

                iterTimer?.Done(toolUseBlocks.Count > 0 ? "ok" : "end",
                    $"stop={stopReason}, {toolUseBlocks.Count} tool_use, " +
                    $"tokens {usage.GetProperty("input_tokens").GetInt32()}/{usage.GetProperty("output_tokens").GetInt32()}, {lat}ms",
                    new() {
                        ["model"]        = model,
                        ["stopReason"]   = stopReason!,
                        ["toolUseCount"] = toolUseBlocks.Count,
                        ["tools"]        = toolUseBlocks.Select(t => t.Name).ToArray(),
                        ["tokIn"]        = usage.GetProperty("input_tokens").GetInt32(),
                        ["tokOut"]       = usage.GetProperty("output_tokens").GetInt32()
                    });

                // Check terminal tool
                var terminal = toolUseBlocks.FirstOrDefault(t => t.Name == terminalToolName);
                if (terminal.Name != null)
                {
                    terminalInput = terminal.Input;
                    trace?.Step("terminal_tool", "ok", 0,
                        $"AI gọi terminal tool '{terminalToolName}' → trích input làm kết quả");
                    break;
                }

                // Không có terminal → check stop conditions
                if (stopReason == "end_turn" || toolUseBlocks.Count == 0)
                {
                    warning = $"AI dừng (stop={stopReason}) nhưng KHÔNG gọi terminal tool '{terminalToolName}'.";
                    _log.LogWarning("[AnthropicTools] {Warning}", warning);
                    break;
                }

                if (stopReason == "max_tokens")
                {
                    warning = "AI đạt max_tokens, kết quả có thể chưa hoàn chỉnh.";
                    _log.LogWarning("[AnthropicTools] max_tokens iter={Iter}", iteration);
                    break;
                }

                // Append assistant turn + execute tool handlers + append user turn với tool_results
                messages.Add(new { role = "assistant", content = assistantContent });

                var dispatchTimer = trace?.Begin($"tool_dispatch_iter{iteration}");
                var toolResults = new List<object>();
                foreach (var (id, name, input) in toolUseBlocks)
                {
                    try
                    {
                        var result = await toolHandler(name, input, linked.Token);
                        toolResults.Add(new {
                            type        = "tool_result",
                            tool_use_id = id,
                            content     = result
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[AnthropicTools] handler tool {Tool} fail", name);
                        toolResults.Add(new {
                            type        = "tool_result",
                            tool_use_id = id,
                            content     = $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}",
                            is_error    = true
                        });
                    }
                }
                dispatchTimer?.Done("ok",
                    $"Chạy {toolUseBlocks.Count} tool handler — feed lại AI cho iter sau",
                    new() { ["tools"] = toolUseBlocks.Select(t => t.Name).ToArray() });

                messages.Add(new { role = "user", content = toolResults });
            }
        }

        return new ToolsResult(
            TerminalInput: terminalInput,
            Iterations:    iteration,
            TokensIn:      totalInTok,
            TokensOut:     totalOutTok,
            LatencyMs:     totalLat,
            Warning:       warning,
            Model:         model);
    }

    // ─── Anthropic /messages POST (có retry transient: network + 408/429/5xx) ──
    private async Task<(JsonDocument doc, long latencyMs)> PostAsync(
        string apiKey, string model, string system, JsonElement[] tools,
        List<object> messages, int maxTokens, CancellationToken ct)
    {
        var body = new
        {
            model,
            max_tokens = maxTokens,
            system,
            messages = messages.ToArray(),
            tools    = tools
        };

        int attempt = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);
            req.Content = JsonContent.Create(body);

            HttpResponseMessage? resp = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { resp = await _http.SendAsync(req, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxTransientRetries)
                {
                    var d = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                    _log.LogWarning(ex, "[AnthropicTools] network → retry #{N} sau {Ms}ms",
                        attempt + 1, d.TotalMilliseconds);
                    await Task.Delay(d, ct);
                    attempt++;
                    continue;
                }
                sw.Stop();

                var raw = await resp.Content.ReadAsStringAsync(ct);
                var status = (int)resp.StatusCode;

                if ((status == 408 || status == 429 || status >= 500) && attempt < MaxTransientRetries)
                {
                    var d = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt));
                    _log.LogWarning("[AnthropicTools] upstream {Status} → retry #{N} sau {Ms}ms",
                        status, attempt + 1, d.TotalMilliseconds);
                    await Task.Delay(d, ct);
                    attempt++;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("[AnthropicTools] {Status}: {Body}", resp.StatusCode, raw.Length > 500 ? raw[..500] : raw);
                    throw new InvalidOperationException($"Anthropic {status}: {(raw.Length > 200 ? raw[..200] : raw)}");
                }

                return (JsonDocument.Parse(raw), sw.ElapsedMilliseconds);
            }
            finally { resp?.Dispose(); }
        }
    }
}

public record ToolsResult(
    JsonElement? TerminalInput,
    int Iterations,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    string? Warning,
    string Model
);
