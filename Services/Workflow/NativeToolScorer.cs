// Services/Workflow/NativeToolScorer.cs
using System.Text.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Thin wrapper trên <see cref="AnthropicToolsClient"/> cho các score-like service
/// (Visa / Deal / Tour Builder) — chỉ khác Review ở chỗ KHÔNG dùng strategy pattern
/// (mỗi service tự route giữa JSON path cũ và native-tool path mới).
///
/// Mỗi service inject + gọi <c>NativeToolScorer.RunAsync</c> để được:
///   - Resolve apiKey từ override → ProviderKeyStore fallback
///   - Gọi AnthropicToolsClient với 1 terminal tool (single-shot, không tool trung gian)
///   - Throw nếu AI không gọi terminal
///   - Trả về (T parsed, model, tokIn, tokOut, latency, warning)
///
/// Service chỉ phải định nghĩa: systemPrompt + userPrompt + toolSchema + parser → 1 method ngắn.
/// </summary>
public class NativeToolScorer
{
    private readonly AnthropicToolsClient _tools;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly ILogger<NativeToolScorer> _log;

    public NativeToolScorer(
        AnthropicToolsClient tools, ProviderKeyStore keys,
        AiUsageLog usage, AiCallContext ctx,
        ILogger<NativeToolScorer> log)
    {
        _tools = tools; _keys = keys; _usage = usage; _ctx = ctx; _log = log;
    }

    /// <summary>
    /// Chạy 1 lượt scoring qua Anthropic native function-calling.
    /// </summary>
    /// <typeparam name="T">Kiểu kết quả service (vd VisaResult / DealScore).</typeparam>
    /// <param name="systemPrompt">System message (ngữ cảnh nghề + chỉ thị gọi tool)</param>
    /// <param name="userPrompt">User message (data cần scoring)</param>
    /// <param name="toolSchema">Schema của terminal tool (gọi <c>BuildAnthropicTool</c> helper)</param>
    /// <param name="terminalToolName">Tên tool (vd "submit_visa_score")</param>
    /// <param name="parser">Parse JsonElement input của terminal tool → T</param>
    /// <param name="apiKeyOverride">apiKey client gửi (BYO key); null → ProviderKeyStore fallback</param>
    /// <param name="model">Anthropic model — caller phải resolve qua AiModelRegistry trước khi gọi</param>
    /// <param name="maxTokens">max_tokens (mặc định 3000)</param>
    /// <param name="trace">Trace collector (no-op nếu null)</param>
    public async Task<ScorerResult<T>> RunAsync<T>(
        string systemPrompt, string userPrompt,
        JsonElement toolSchema, string terminalToolName,
        Func<JsonElement, T> parser,
        string? apiKeyOverride,
        string model,
        int maxTokens = 3000,
        TraceCollector? trace = null,
        CancellationToken ct = default)
    {
        var apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride) ? apiKeyOverride! : _keys.Get("anthropic");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key trống — cần config Providers:Anthropic:ApiKey hoặc client gửi apiKey.");

        var result = await _tools.RunAsync(
            systemPrompt: systemPrompt,
            userPrompt:   userPrompt,
            tools:        new[] { toolSchema },
            terminalToolName: terminalToolName,
            toolHandler:  NoIntermediate,
            apiKey:       apiKey,
            model:        model,
            maxTokens:    maxTokens,
            trace:        trace,
            ct:           ct);

        if (result.TerminalInput == null)
        {
            var warn = result.Warning ?? $"AI không gọi tool '{terminalToolName}'";
            trace?.Step("terminal_missing", "fail", 0, warn);
            throw new InvalidOperationException($"NativeTool scoring thất bại: {warn}");
        }

        T parsed;
        try { parsed = parser(result.TerminalInput.Value); }
        catch (Exception ex)
        {
            trace?.Step("parse_tool_input", "fail", 0, $"Parser throw: {ex.Message}");
            throw new InvalidOperationException(
                $"Parse tool input '{terminalToolName}' lỗi: {ex.Message}", ex);
        }

        // Ghi usage log (AnthropicToolsClient.RunAsync không tự track như IAiProvider.CompleteAsync)
        var callCtx = _ctx.Resolve();
        _usage.Append(callCtx.Feature, callCtx.SessionId, callCtx.Tenant,
            "anthropic", result.Model, result.TokensIn, result.TokensOut, result.LatencyMs);

        return new ScorerResult<T>(
            Value:     parsed,
            Model:     result.Model,
            TokensIn:  result.TokensIn,
            TokensOut: result.TokensOut,
            LatencyMs: result.LatencyMs,
            Warning:   result.Warning);
    }

    /// <summary>
    /// Helper build schema cho Anthropic tools API: tự wrap {name, description, input_schema}.
    /// Service chỉ phải truyền properties object — đỡ phải nhớ shape lồng nhau.
    /// </summary>
    public static JsonElement BuildAnthropicTool(string name, string description,
        object properties, string[] required)
    {
        var schema = new
        {
            name,
            description,
            input_schema = new
            {
                type = "object",
                properties,
                required
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// Service single-shot không có tool trung gian — AI gọi tool nào khác = lỗi config.
    private static Task<string> NoIntermediate(string name, JsonElement input, CancellationToken ct)
        => Task.FromResult($"{{\"error\":\"Tool '{name}' không nhận diện được — agent chỉ chấp nhận terminal tool\"}}");
}

public record ScorerResult<T>(
    T Value,
    string Model,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    string? Warning
);
