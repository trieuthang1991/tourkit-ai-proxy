// Services/Chat/NativeToolUseAgent.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Agent dung Anthropic native function-calling (tools API chinh thuc).
/// Chi ap dung khi provider == "anthropic". Cac provider khac fallback ve JsonPlannerAgent.
///
/// G2-2: single-turn — toi da 1 tool_use block, 2 AI call.
///   Call 1: system + tools + user messages → co the tra stop_reason = "tool_use" hoac "end_turn".
///   Call 2 (neu co tool_use): gui tool_result → lay phan tich final.
///
/// BuildChatData + helper stats duoc tach ra ChatDataBuilder de share voi JsonPlannerAgent.
/// </summary>
public class NativeToolUseAgent : IAgentRuntime
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly Cache.ChatCache _cache;
    private readonly ILogger<NativeToolUseAgent> _log;
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;

    // System prompt ngan, inject ngay hom nay de AI co date context.
    private static readonly string SystemPromptBase =
        "Bạn là trợ lý số liệu Tourkit. Dùng tools để lấy dữ liệu thật, " +
        "sau đó trả lời ngắn gọn tiếng Việt bám đúng số liệu. " +
        "CHỈ dùng tools có trong catalog; TUYỆT ĐỐI không bịa số. " +
        "Nếu câu hỏi không cần số liệu, trả lời thẳng không gọi tool.";

    private static readonly JsonSerializerOptions _jsonWeb = new(JsonSerializerDefaults.Web);

    public NativeToolUseAgent(
        TourKitApiClient api,
        TkSessionStore sessions,
        Cache.ChatCache cache,
        ILogger<NativeToolUseAgent> log,
        IHttpClientFactory http,
        ProviderKeyStore keys,
        AiUsageLog usage,
        AiCallContext ctx)
    {
        _api      = api;
        _sessions = sessions;
        _cache    = cache;
        _log      = log;
        _http     = http;
        _keys     = keys;
        _usage    = usage;
        _ctx      = ctx;
    }

    /// Chi xu ly khi provider la "anthropic".
    public bool Supports(IAiProvider provider) =>
        string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase);

    // ── Buffered run ─────────────────────────────────────────────────────────────

    public async Task<AgentResult> RunAsync(AgentInput input, CancellationToken ct)
    {
        var apiKey = !string.IsNullOrWhiteSpace(input.ApiKey) ? input.ApiKey : _keys.Get("anthropic");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Chưa nhập API key cho Claude (Anthropic).");

        var model  = input.Model ?? "claude-sonnet-4-5";
        var system = SystemPromptBase + $" Hôm nay: {DateTime.Now:yyyy-MM-dd}.";

        // Build messages tu history (gioi han 6 luot gan nhat)
        var messages = BuildMessages(input.History);

        // Build tool schema, cache_control o tool cuoi de Anthropic cache phan prompt nay.
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: true);

        var sw = Stopwatch.StartNew();
        int inTok = 0, outTok = 0;
        int iterations = 1;

        // ── Call 1: planner + tool selection ──────────────────────────────────────
        using var doc1 = await CallAnthropicAsync(apiKey, model, system, tools, messages, ct);
        var root1    = doc1.RootElement;
        var stop1    = root1.GetProperty("stop_reason").GetString();
        var usage1   = root1.GetProperty("usage");
        inTok  += usage1.GetProperty("input_tokens").GetInt32();
        outTok += usage1.GetProperty("output_tokens").GetInt32();

        // Phan tich content blocks tu response 1
        string? toolName   = null;
        JsonElement? toolInput = null;
        string? toolUseId  = null;
        string  finalText  = "";

        foreach (var block in root1.GetProperty("content").EnumerateArray())
        {
            var blockType = block.GetProperty("type").GetString();
            if (blockType == "text")
                finalText += block.GetProperty("text").GetString();
            else if (blockType == "tool_use")
            {
                toolName   = block.GetProperty("name").GetString();
                toolInput  = block.GetProperty("input").Clone();
                toolUseId  = block.GetProperty("id").GetString();
            }
        }

        ChatData? chatData = null;

        if (stop1 == "tool_use" && toolName != null)
        {
            var tool = ChatTools.Find(toolName);
            if (tool == null)
            {
                // AI goi tool khong co trong catalog → bao loi, khong crash.
                _log.LogWarning("[NativeTool] AI goi tool khong ton tai: {Name}", toolName);
                finalText = $"Tool '{toolName}' không có trong catalog. Vui lòng thử lại.";
            }
            else
            {
                // ── Dispatch tool: goi TourKit.Api ────────────────────────────────
                var jwt  = await _sessions.GetValidJwtAsync(input.SessionId, ct);
                var path = ChatTools.BuildPath(tool, toolInput);
                _log.LogInformation("[NativeTool] tool={Tool} path={Path}", tool.Name, path);

                JsonElement toolData;
                try
                {
                    toolData = await _api.GetAsync(jwt, path, ct);
                }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    // JWT het han giua chung → re-login 1 lan.
                    jwt      = await _sessions.ForceReloginAsync(input.SessionId, ct);
                    toolData = await _api.GetAsync(jwt, path, ct);
                }

                // Build ChatData tu tool + data (dung chung ChatDataBuilder).
                chatData = ChatDataBuilder.Build(tool, toolData);

                // ── Call 2: gui tool_result, nhan phan tich ──────────────────────
                // Append assistant turn (phai clone content truoc vi doc1 se bi dispose).
                var content1Clone = root1.GetProperty("content").Clone();
                messages.Add(new { role = "assistant", content = content1Clone });
                messages.Add(new { role = "user", content = (object)new[]
                {
                    new
                    {
                        type        = "tool_result",
                        tool_use_id = toolUseId,
                        content     = toolData.GetRawText()
                    }
                }});

                using var doc2 = await CallAnthropicAsync(apiKey, model, system, tools, messages, ct);
                var root2  = doc2.RootElement;
                var usage2 = root2.GetProperty("usage");
                inTok  += usage2.GetProperty("input_tokens").GetInt32();
                outTok += usage2.GetProperty("output_tokens").GetInt32();
                iterations = 2;

                // Lay text tu response 2.
                finalText = "";
                foreach (var block in root2.GetProperty("content").EnumerateArray())
                {
                    if (block.GetProperty("type").GetString() == "text")
                        finalText += block.GetProperty("text").GetString();
                }
            }
        }

        long latencyMs = sw.ElapsedMilliseconds;

        // Guardrail: xoa em-dash.
        finalText = AgentGuardrails.StripEmDash(finalText.Trim());

        // Neu reply qua ngan → thong bao fallback (khong retry buffered o day, retry o streaming se phuc tap).
        if (AgentGuardrails.IsTooShort(finalText) && chatData != null)
            finalText = "Đã lấy được số liệu (xem bảng bên phải) nhưng chưa tạo được phần phân tích.";

        // Validate so AI noi (warning only).
        var numWarning = chatData != null
            ? AgentGuardrails.ValidateNumbers(finalText, chatData.Stats) : null;

        // Ghi usage log.
        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, "anthropic", model, inTok, outTok, latencyMs);

        object? paramsOut = toolInput.HasValue
            ? JsonSerializer.Deserialize<object>(toolInput.Value.GetRawText()) : null;

        return new AgentResult(
            Reply:        finalText,
            ToolName:     toolName ?? "none",
            Params:       paramsOut,
            Data:         chatData,
            LatencyMs:    latencyMs,
            InputTokens:  inTok,
            OutputTokens: outTok,
            Warning:      numWarning,
            Iterations:   iterations);
    }

    // ── Streaming run ────────────────────────────────────────────────────────────

    /// Phase 2: dung buffered RunAsync roi emit toan bo ket qua (true streaming phai co G2-3).
    public async Task StreamAsync(AgentInput input, Func<object, Task> emit, CancellationToken ct)
    {
        await emit(new { stage = "thinking", iteration = 1 });

        AgentResult result;
        try
        {
            result = await RunAsync(input, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[NativeTool-stream] RunAsync loi");
            await emit(new { error = ex.Message });
            await emit(new { done = true });
            return;
        }

        // Neu co data → gui truoc de panel phai hien so lieu ngay.
        if (result.Data != null && !string.IsNullOrEmpty(result.Data.Title))
            await emit(new { stage = "analyzing", tool = result.ToolName, data = result.Data });

        await emit(new { delta = result.Reply });
        await emit(new
        {
            done     = true,
            reply    = result.Reply,
            toolName = result.ToolName,
            data     = result.Data
        });
    }

    // ── Anthropic HTTP helper ────────────────────────────────────────────────────

    /// Goi POST api.anthropic.com/v1/messages, tra JsonDocument (caller phai dispose).
    private async Task<JsonDocument> CallAnthropicAsync(
        string apiKey, string model, string system,
        object[] tools, List<object> messages,
        CancellationToken ct)
    {
        var body = new
        {
            model,
            max_tokens = 2000,
            system,
            tools,
            messages
        };

        var http = _http.CreateClient("anthropic");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, _jsonWeb),
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.Add("x-api-key",          apiKey);
        req.Headers.Add("anthropic-version",  "2023-06-01");
        req.Headers.Add("anthropic-beta",      "prompt-caching-2024-07-31");

        var resp = await http.SendAsync(req, ct);
        var raw  = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 800 ? raw[..800] : raw;
            throw new UpstreamException((int)resp.StatusCode, "Anthropic API loi", snippet);
        }

        return JsonDocument.Parse(raw);
    }

    // ── Message builder ──────────────────────────────────────────────────────────

    /// Lay 6 luot lich su gan nhat, chuyen sang format Anthropic messages array.
    private static List<object> BuildMessages(List<ChatTurn> history)
    {
        var msgs = new List<object>();
        foreach (var turn in history.TakeLast(6))
            msgs.Add(new { role = turn.Role, content = turn.Content });
        return msgs;
    }
}
