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
/// G2-3: multi-turn loop max 3 iteration + parallel tool execution.
///
/// State machine:
///   turn N (max N=3)
///     → AI call (voi previous messages + tool_results tu turn truoc)
///     → doc stop_reason:
///         "tool_use"   → execute TAT CA tool_use blocks song song (Task.WhenAll)
///                        → append assistant turn + user turn voi tool_result blocks
///                        → N+1 (lap tiep)
///         "end_turn"   → ket thuc, lay text
///         "max_tokens" → ket thuc voi warning
///     → N > 3? hard stop, tra phan da co + warning "AI vuot gioi han vong lap"
///
/// Hard limits:
///   - Max iteration AI  : 3
///   - Max tool calls    : 5 (tong cong toan bo session)
///   - Wall-clock        : 30s
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

    // Gioi han cung de chong AI loop bat tan
    private const int MaxIterations  = 3;
    private const int MaxToolCalls   = 5;
    private const int WallClockSec   = 30;

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

        // Wall-clock timeout 30s chia se qua linked token
        using var wallClock = new CancellationTokenSource(TimeSpan.FromSeconds(WallClockSec));
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, wallClock.Token);

        // ── Bien trang thai cho toan bo vong lap ──────────────────────────────
        int  iteration     = 0;
        int  totalToolCalls= 0;
        int  totalInTok    = 0;
        int  totalOutTok   = 0;
        long totalLat      = 0;

        // Ket qua tich luy qua cac turn
        string    finalText   = "";
        string?   lastToolName= null;
        object?   lastParams  = null;
        ChatData? lastData    = null;
        string?   warning     = null;

        // ── Vong lap multi-turn (toi da MaxIterations = 3) ───────────────────
        while (iteration < MaxIterations)
        {
            iteration++;
            _log.LogDebug("[NativeTool] iteration={Iter} totalToolCalls={Tc}", iteration, totalToolCalls);

            // Goi Anthropic, nhan JsonDocument (caller phai dispose sau)
            var (_, doc, lat) = await CallAnthropicAsync(apiKey, model, system, tools, messages, linked.Token);
            totalLat += lat;

            var root    = doc.RootElement;
            var usage   = root.GetProperty("usage");
            totalInTok  += usage.GetProperty("input_tokens").GetInt32();
            totalOutTok += usage.GetProperty("output_tokens").GetInt32();

            var stopReason = root.GetProperty("stop_reason").GetString();

            // Thu thap tat ca text blocks + tool_use blocks trong turn nay
            var toolUseBlocks = new List<(string Id, string Name, JsonElement Input)>();
            var sb = new StringBuilder();

            foreach (var block in root.GetProperty("content").EnumerateArray())
            {
                var bt = block.GetProperty("type").GetString();
                if (bt == "text")
                    sb.Append(block.GetProperty("text").GetString());
                else if (bt == "tool_use")
                {
                    toolUseBlocks.Add((
                        block.GetProperty("id").GetString()!,
                        block.GetProperty("name").GetString()!,
                        block.GetProperty("input").Clone()
                    ));
                }
            }

            // Text turn nay ghi de finalText (text y nghia nhat la turn cuoi cung)
            if (sb.Length > 0)
                finalText = sb.ToString();

            // ── Kiem tra dieu kien ket thuc vong lap ──────────────────────────
            if (stopReason == "end_turn" || toolUseBlocks.Count == 0)
            {
                // AI tu nguyen ket thuc hoac khong goi them tool nao
                doc.Dispose();
                break;
            }

            if (stopReason == "max_tokens")
            {
                // Het budget token — ket thuc voi warning
                _log.LogWarning("[NativeTool] max_tokens tai iteration {N}", iteration);
                warning = "AI hết token trước khi hoàn thành phân tích.";
                doc.Dispose();
                break;
            }

            // ── Kiem tra gioi han tong so tool calls ──────────────────────────
            if (totalToolCalls + toolUseBlocks.Count > MaxToolCalls)
            {
                _log.LogWarning("[NativeTool] cap tool calls: {Used}+{Pending} > {Max}",
                    totalToolCalls, toolUseBlocks.Count, MaxToolCalls);
                doc.Dispose();
                break;
            }
            totalToolCalls += toolUseBlocks.Count;

            // ── Thuc thi TAT CA tools song song (Task.WhenAll) ─────────────────
            // Lay JWT 1 lan truoc khi bat dau parallel tasks (co the bi renew ben trong)
            var jwt = await _sessions.GetValidJwtAsync(input.SessionId, linked.Token);

            var execTasks = toolUseBlocks.Select(async tub =>
            {
                var tool = ChatTools.Find(tub.Name);
                if (tool == null)
                {
                    _log.LogWarning("[NativeTool] AI goi tool khong ton tai: {Name}", tub.Name);
                    return (tub,
                        ResultJson: $"{{\"error\":\"Tool {tub.Name} không có trong catalog\"}}",
                        Tool: (ChatTool?)null,
                        Data: (ChatData?)null,
                        Params: (object?)null);
                }

                try
                {
                    var path = ChatTools.BuildPath(tool, tub.Input);
                    _log.LogInformation("[NativeTool] tool={Tool} path={Path}", tool.Name, path);
                    var data   = await _api.GetAsync(jwt, path, linked.Token);
                    var cd     = ChatDataBuilder.Build(tool, data);
                    var pars   = JsonSerializer.Deserialize<object>(tub.Input.GetRawText());
                    return (tub, ResultJson: data.GetRawText(), Tool: (ChatTool?)tool, Data: (ChatData?)cd, Params: (object?)pars);
                }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    // JWT het han giua chung → re-login + retry 1 lan
                    _log.LogWarning("[NativeTool] 401 khi goi tool {Name}, re-login...", tub.Name);
                    try
                    {
                        var freshJwt = await _sessions.ForceReloginAsync(input.SessionId, linked.Token);
                        var path     = ChatTools.BuildPath(tool, tub.Input);
                        var data     = await _api.GetAsync(freshJwt, path, linked.Token);
                        var cd       = ChatDataBuilder.Build(tool, data);
                        var pars     = JsonSerializer.Deserialize<object>(tub.Input.GetRawText());
                        return (tub, ResultJson: data.GetRawText(), Tool: (ChatTool?)tool, Data: (ChatData?)cd, Params: (object?)pars);
                    }
                    catch (Exception ex2)
                    {
                        _log.LogError(ex2, "[NativeTool] tool {Name} that bai sau re-login", tub.Name);
                        return (tub,
                            ResultJson: $"{{\"error\":\"{ex2.Message}\"}}",
                            Tool: (ChatTool?)tool,
                            Data: (ChatData?)null,
                            Params: (object?)null);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[NativeTool] tool {Name} that bai", tub.Name);
                    return (tub,
                        ResultJson: $"{{\"error\":\"{ex.Message}\"}}",
                        Tool: (ChatTool?)tool,
                        Data: (ChatData?)null,
                        Params: (object?)null);
                }
            }).ToArray();

            var execResults = await Task.WhenAll(execTasks);

            // Ghi loi ket qua tool cuoi cung co data (cho ChatData panel phai)
            foreach (var er in execResults)
            {
                if (er.Data != null)
                {
                    lastData     = er.Data;
                    lastToolName = er.Tool?.Name;
                    lastParams   = er.Params;
                }
            }

            // ── Xay dung luot tiep: append assistant turn + user turn tool_results ──
            // Clone content truoc khi dispose doc
            var assistantContent = root.GetProperty("content").Clone();
            messages.Add(new { role = "assistant", content = assistantContent });

            // Moi tool_use co 1 tool_result tuong ung (ke ca loi)
            var toolResultBlocks = execResults.Select(er => (object)new
            {
                type        = "tool_result",
                tool_use_id = er.tub.Id,
                content     = er.ResultJson
            }).ToArray();
            messages.Add(new { role = "user", content = toolResultBlocks });

            doc.Dispose();
        } // end while

        // ── Kiem tra co bi hard-stop khong ────────────────────────────────────
        if (iteration >= MaxIterations && warning == null)
        {
            _log.LogWarning("[NativeTool] hit max iterations ({Max})", MaxIterations);
            warning = "AI vượt giới hạn vòng lặp (3).";
        }

        // ── Guardrails ────────────────────────────────────────────────────────
        finalText = AgentGuardrails.StripEmDash(finalText.Trim());

        // Reply qua ngan → fallback message
        if (AgentGuardrails.IsTooShort(finalText) && lastData != null)
            finalText = "Đã lấy được số liệu (xem bảng bên phải) nhưng chưa tạo được phần phân tích.";

        // Validate so AI noi (warning only, khong block)
        var numWarning = lastData != null
            ? AgentGuardrails.ValidateNumbers(finalText, lastData.Stats) : null;
        if (numWarning != null && warning == null)
            warning = numWarning;

        // Ghi usage log
        var c = _ctx.Resolve();
        _usage.Append(c.Feature, c.SessionId, c.Tenant, "anthropic", model, totalInTok, totalOutTok, totalLat);

        return new AgentResult(
            Reply:        finalText,
            ToolName:     lastToolName ?? "none",
            Params:       lastParams,
            Data:         lastData,
            LatencyMs:    totalLat,
            InputTokens:  totalInTok,
            OutputTokens: totalOutTok,
            Warning:      warning,
            Iterations:   iteration);
    }

    // ── Streaming run ────────────────────────────────────────────────────────────

    /// Chay buffered RunAsync roi emit toan bo ket qua (true per-chunk streaming cho G2-5+).
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

    /// Goi POST api.anthropic.com/v1/messages.
    /// Tra (rawBody, JsonDocument, latencyMs). Caller CO TRACH NHIEM dispose JsonDocument.
    private async Task<(string Raw, JsonDocument Doc, long LatencyMs)> CallAnthropicAsync(
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
        req.Headers.Add("x-api-key",         apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta",    "prompt-caching-2024-07-31");

        var sw   = Stopwatch.StartNew();
        var resp = await http.SendAsync(req, ct);
        var raw  = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 800 ? raw[..800] : raw;
            throw new UpstreamException((int)resp.StatusCode, "Anthropic API loi", snippet);
        }

        var doc = JsonDocument.Parse(raw);
        return (raw, doc, sw.ElapsedMilliseconds);
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
