// Services/Chat/NativeToolUseAgent.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;
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
///
/// Action tools (ActionTools — "giao viec"/"tra loi mail"/"danh gia KH"/...) duoc dang ky nhu Anthropic
/// tools[] BO SUNG (ToolSchemaGenerator.BuildAnthropicActionTools), noi tiep sau ChatTools schema trong
/// cung 1 mang tools truyen cho Anthropic. Trong vong lap, TRUOC khi dispatch bat ky tool_use nao nhu
/// tool doc thuong, ta kiem tra co block nao trung ten ActionTools.Find(...) != null khong -- neu co,
/// NGUNG vong lap ngay (khong dispatch/execute tool nao ca luot do), tra AgentResult.Action = ten action
/// + Params = input tool_use (giong het contract cua JsonPlannerAgent). ChatAgentService da co san logic
/// resolve/proposal/execute cho Action != null nen khong can sua gi them o do.
/// </summary>
public class NativeToolUseAgent : IAgentRuntime
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly Cache.ChatCache _cache;
    private readonly UnresolvedQuestionsLog _unresolved;
    private readonly ILogger<NativeToolUseAgent> _log;
    private readonly IHttpClientFactory _http;
    private readonly ProviderKeyStore _keys;
    private readonly AiUsageLog _usage;
    private readonly AiCallContext _ctx;
    private readonly AiModelRegistry _registry;
    private readonly TenantQuotaStore _quota;

    // System prompt: nhấn mạnh BẮT BUỘC gọi tool cho mọi câu liên quan số liệu kinh doanh,
    // KHUYẾN KHÍCH gọi nhiều tool song song khi cần so sánh, và viết phân tích đầy đủ (không cụt).
    private static readonly string SystemPromptBase =
        "Bạn là TRAVAI — trợ lý phân tích số liệu cho doanh nghiệp du lịch. " +
        "Khi được hỏi bạn là ai / tên gì, xưng rõ là TRAVAI (trợ lý số liệu), thân thiện. " +
        "Quy trình: (1) dùng tools để lấy dữ liệu thật; (2) viết PHÂN TÍCH ĐẦY ĐỦ tiếng Việt bám đúng số liệu. " +
        "TUYỆT ĐỐI không bịa số; CHỈ dùng tools có trong catalog. " +
        "BẮT BUỘC gọi tool cho mọi câu hỏi về doanh thu / chi phí / lợi nhuận / khách / tour / deal / marketing / cơ hội / công nợ / lịch hẹn — kể cả câu follow-up (vd 'phân tích thêm', 'tại sao', 'còn X thì sao'). " +
        "KHI USER YÊU CẦU LÀM một việc (không phải hỏi số liệu) — vd 'giao việc cho NV X', 'trả lời khách Y', 'soạn mail mới', 'đánh giá/xếp hạng khách hàng Z', 'chấm điểm deal/cơ hội', 'kiểm tra mail mới', 'đặt lịch hẹn với khách' — BẮT BUỘC gọi ĐÚNG action tool tương ứng (check_mail/send_mail_reply/compose_mail/review_customer/score_deal/assign_task/create_appointment), KHÔNG gọi tool đọc số liệu (vd top_customers) để thay thế. Điền params từ câu nói + ngữ cảnh lượt trước (vd 'khách này' → tên đã nhắc), KHÔNG tự bịa id. " +
        "KHI CẦN SO SÁNH (vd 'so với năm ngoái', 'so với tháng trước', 'cùng kỳ'): gọi NHIỀU tool SONG SONG cùng turn với param khác nhau (khoảng date khác, kỳ khác) để có 2 bộ số đối chiếu. " +
        "PHÂN TÍCH phải có: (a) số chính + xu hướng; (b) so sánh nếu có 2 bộ số; (c) 1-2 đề xuất hành động. " +
        "Dùng thuật ngữ tiếng Việt thuần (doanh thu/chi phí/lợi nhuận/khách hàng); KHÔNG dùng tên trường Anh (revenue/expense...) hoặc Id. " +
        "KHÔNG dùng markdown (không **, ##, *, _, ``` — văn bản thuần). Xuống dòng giữa các đoạn bằng dòng trống. " +
        "Chỉ trả lời thẳng KHÔNG gọi tool nếu là lời chào hoặc câu hỏi về cách dùng trợ lý."
        + ChatGlossary.AnalysisBlock;

    private static readonly JsonSerializerOptions _jsonWeb = new(JsonSerializerDefaults.Web);

    // Gioi han cung de chong AI loop bat tan
    private const int MaxIterations  = 3;
    private const int MaxToolCalls   = 5;
    private const int WallClockSec   = 30;

    public NativeToolUseAgent(
        TourKitApiClient api,
        TkSessionStore sessions,
        Cache.ChatCache cache,
        UnresolvedQuestionsLog unresolved,
        ILogger<NativeToolUseAgent> log,
        IHttpClientFactory http,
        ProviderKeyStore keys,
        AiUsageLog usage,
        AiCallContext ctx,
        AiModelRegistry registry,
        TenantQuotaStore quota)
    {
        _api        = api;
        _sessions   = sessions;
        _cache      = cache;
        _unresolved = unresolved;
        _log        = log;
        _http       = http;
        _keys       = keys;
        _usage      = usage;
        _ctx        = ctx;
        _registry   = registry;
        _quota      = quota;
    }

    /// Chi xu ly khi provider la "anthropic".
    public bool Supports(IAiProvider provider) =>
        string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase);

    // ── Buffered run ─────────────────────────────────────────────────────────────

    // Thoa man interface IAgentRuntime.RunAsync(AgentInput, CancellationToken).
    public Task<AgentResult> RunAsync(AgentInput input, CancellationToken ct)
        => RunCoreAsync(input, ct, emit: null);

    /// Buffered run co kem emit callback tuy chon de StreamAsync gui progress tung turn.
    private async Task<AgentResult> RunCoreAsync(AgentInput input, CancellationToken ct,
        Func<object, Task>? emit = null)
    {
        var apiKey = !string.IsNullOrWhiteSpace(input.ApiKey) ? input.ApiKey : _keys.Get("anthropic");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Chưa nhập API key cho Claude (Anthropic).");

        // Quota check ĐẦU vòng lặp — Consume per-iter (mỗi /messages POST = 1 lượt).
        var callCtx = _ctx.Resolve();
        if (!string.IsNullOrEmpty(callCtx.Tenant) && !_quota.IsAvailable(callCtx.Tenant))
        {
            var snap = _quota.Snapshot(callCtx.Tenant);
            throw new QuotaExhaustedException(callCtx.Tenant, snap.Limit, snap.Used);
        }

        // Resolve model qua AiModelRegistry (ChatAnalytics) — ChatAgentService cũng đã resolve, đây là defensive.
        var resolved = _registry.Resolve(AiFeature.ChatAnalytics, input.Provider.Id, input.Model);
        var model    = resolved.Model;

        // Đọc bộ nhớ chat của phiên, bổ sung context hội thoại trước vào system prompt.
        var memory = _sessions.GetMemory(input.SessionId) ?? SessionChatMemory.Empty();
        var system = BuildSystemPrompt(memory);

        // Build messages tu history (gioi han 6 luot gan nhat)
        var messages = BuildMessages(input.History);

        // Build tool schema: ChatTools (doc) + ActionTools (hanh dong) noi tiep trong 1 mang tools[].
        // cache_control gan o tool CUOI CUNG cua toan mang (action tool cuoi) de Anthropic cache dung
        // toan bo prompt tools -- addCacheControl=false o ChatTools de tranh 2 cache_control (loi API).
        var tools = ToolSchemaGenerator.BuildAnthropicTools(addCacheControl: false)
            .Concat(ToolSchemaGenerator.BuildAnthropicActionTools(addCacheControl: true))
            .ToArray();

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
        // Tich luy MOI tool call thanh cong qua TAT CA iteration — de tong hop compare
        // sau khi loop xong (Input phai Clone vi JsonDocument dispose cuoi moi vong).
        var allToolResults = new List<(string ToolName, JsonElement Input, string ResultJson, ChatTool Tool, ChatData Data, object? Params)>();

        var trace = input.Trace;
        trace?.Step("session_memory", "ok", 0,
            memory.LastTool != null
                ? $"Context hội thoại trước: tool={memory.LastTool}"
                : "Hội thoại mới");

        // ── Vong lap multi-turn (toi da MaxIterations = 3) ───────────────────
        while (iteration < MaxIterations)
        {
            iteration++;
            _log.LogDebug("[NativeTool] iteration={Iter} totalToolCalls={Tc}", iteration, totalToolCalls);

            // Emit progress: bao hieu frontend dang o turn nao.
            if (emit != null) await emit(new { stage = "thinking", iteration });

            // Goi Anthropic, nhan JsonDocument (caller phai dispose sau)
            var iterTimer = trace?.Begin($"anthropic_call_iter{iteration}");
            var (_, doc, lat) = await CallAnthropicAsync(apiKey, model, system, tools, messages, linked.Token);
            totalLat += lat;

            var root    = doc.RootElement;
            var usage   = root.GetProperty("usage");
            var iterInTok  = usage.GetProperty("input_tokens").GetInt32();
            var iterOutTok = usage.GetProperty("output_tokens").GetInt32();
            totalInTok  += iterInTok;
            totalOutTok += iterOutTok;

            // Per-iter Append + Consume (match grain với IAiProvider). Trước đây chỉ Append aggregate
            // cuối loop nên N iter chỉ tính 1 lượt → user dùng 4 lần hiển thị 2.
            _usage.Append(callCtx.Feature, callCtx.SessionId, callCtx.Tenant, "anthropic", model, iterInTok, iterOutTok, lat);
            if (!string.IsNullOrEmpty(callCtx.Tenant)) _quota.Consume(callCtx.Tenant);

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

            iterTimer?.Done("ok",
                $"POST https://api.anthropic.com/v1/messages (model={model}, {tools.Length} tools schema) → " +
                $"stop={stopReason}, {toolUseBlocks.Count} tool_use, text={sb.Length}c, " +
                $"tokens {usage.GetProperty("input_tokens").GetInt32()}/{usage.GetProperty("output_tokens").GetInt32()}",
                new() {
                    ["method"] = "POST",
                    ["url"] = "https://api.anthropic.com/v1/messages",
                    ["model"] = model,
                    ["maxTokensReq"] = 4000,
                    ["toolsSchemaCount"] = tools.Length,
                    ["messagesCount"] = messages.Count,
                    ["systemChars"] = system.Length,
                    ["stopReason"] = stopReason,
                    ["toolUseCount"] = toolUseBlocks.Count,
                    ["tools"] = toolUseBlocks.Select(t => t.Name).ToArray(),
                    ["textChars"] = sb.Length,
                    ["tokIn"] = usage.GetProperty("input_tokens").GetInt32(),
                    ["tokOut"] = usage.GetProperty("output_tokens").GetInt32()
                });

            // ── Action detection: uu tien HANH DONG hon tool doc (mirror JsonPlannerAgent). ──────
            // Neu model goi 1 action tool (ActionTools catalog) trong turn nay -> NGUNG vong lap
            // NGAY, KHONG dispatch nhu tool doc thuong (ChatTools.Find se tra null / BuildPath se fail
            // vi day khong phai tool doc that). Chi surface Action + Params -- KHONG thuc thi (giong
            // Task 6 cua JsonPlannerAgent, executor/resolve/proposal nam o ChatAgentService).
            var actionBlock = toolUseBlocks.FirstOrDefault(t => ActionTools.Find(t.Name) != null);
            if (actionBlock.Name != null)
            {
                var action = ActionTools.Find(actionBlock.Name)!;
                object? actionParamsOut = null;
                try { actionParamsOut = JsonSerializer.Deserialize<object>(actionBlock.Input.GetRawText()); }
                catch { /* input rong/invalid -> giu null, ChatAgentService tu hoi lai thieu gi */ }

                trace?.Step("action_parse", "ok", 0,
                    $"Model gọi ACTION tool: action='{action.Name}', params={actionBlock.Input.GetRawText()}",
                    new() { ["action"] = action.Name, ["params"] = actionBlock.Input.GetRawText() });

                doc.Dispose();
                return new AgentResult(
                    Reply:        $"Đã nhận diện yêu cầu hành động: {action.Name}.",
                    ToolName:     "none",
                    Params:       actionParamsOut,
                    Data:         memory.LastChatData,
                    LatencyMs:    totalLat,
                    InputTokens:  totalInTok,
                    OutputTokens: totalOutTok,
                    Warning:      warning,
                    Iterations:   iteration,
                    Action:       action.Name);
            }

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

            // Emit: bao frontend dang goi tool nao de hien trang thai "Đang lấy số liệu".
            if (emit != null && toolUseBlocks.Count > 0)
                await emit(new { stage = "fetching", iteration, tools = toolUseBlocks.Select(t => t.Name).ToArray() });

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
                        // Trigger: upstream_persistent_error (re-login van fail)
                        var q401 = input.History.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                        _unresolved.Append(
                            tag:            "upstream_persistent_error",
                            sessionId:      input.SessionId,
                            tenantId:       input.TenantId,
                            question:       q401,
                            history:        input.History,
                            plannerRaw:     null,
                            toolChosen:     tub.Name,
                            aiReplyPreview: ex2.Message,
                            provider:       "anthropic",
                            model:          input.Model,
                            iterations:     iteration,
                            latencyMs:      totalLat,
                            tokensIn:       totalInTok,
                            tokensOut:      totalOutTok);
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

            var dispatchTimer = trace?.Begin($"tool_dispatch_iter{iteration}");
            var execResults = await Task.WhenAll(execTasks);
            var baseUrl = _api.BaseUrl;
            var toolDetails = execResults.Select(e => new
            {
                tool = e.tub.Name,
                params_ = e.tub.Input.GetRawText(),
                url = e.Tool != null ? baseUrl + ChatTools.BuildPath(e.Tool, e.tub.Input) : "(tool không trong catalog)",
                responseSize = e.ResultJson.Length,
                ok = !e.ResultJson.StartsWith("{\"error\""),
                responseSnippet = e.ResultJson.Length > 400 ? e.ResultJson[..400] + "…" : e.ResultJson
            }).ToArray();
            dispatchTimer?.Done(
                toolDetails.All(d => d.ok) ? "ok" : "fail",
                $"GET {string.Join(" + ", toolDetails.Select(d => d.url))}" +
                $" → tổng {toolDetails.Sum(d => d.responseSize):N0} bytes",
                new() { ["method"] = "GET", ["auth"] = "Bearer JWT (TourKit session)", ["tools"] = toolDetails });

            // Tich luy ket qua — KHONG emit data giua chung. Compare + emit tong hop
            // 1 LAN sau khi loop xong (user: "xong het moi tong hop so lieu" — truoc day
            // emit moi iteration lam panel ve 2 lan khi AI goi 2 tool so sanh).
            // Input phai Clone() vi JsonDocument bi dispose cuoi moi vong lap.
            foreach (var er in execResults)
            {
                if (er.Data == null || er.Tool == null) continue;
                allToolResults.Add((er.Tool.Name, er.tub.Input.Clone(), er.ResultJson, er.Tool, er.Data, er.Params));
                lastData = er.Data; lastToolName = er.Tool.Name; lastParams = er.Params;
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

        // ── TONG HOP CROSS-ITERATION: build compare tu allToolResults ─────────
        // 2+ call cung 1 tool (vd marketing T5 + marketing T6) → ghep thanh Compare.
        // Truoc day chi group trong 1 iteration → bo sot truong hop AI goi tool nhieu turn.
        if (allToolResults.Count >= 2)
        {
            var grouped = allToolResults.GroupBy(r => r.ToolName)
                .FirstOrDefault(g => g.Count() >= 2);
            if (grouped != null)
            {
                var ordered = grouped.OrderByDescending(r => InferDateOrderKey(r.Input)).ToList();
                var primary = ordered[0];
                var compare = ordered[1];
                var primaryLabel = InferPeriodLabel(primary.Input) ?? "Kỳ chính";
                var compareLabel = InferPeriodLabel(compare.Input) ?? "Kỳ đối chiếu";
                var compareRaw = TryParseRaw(compare.ResultJson);
                lastData = primary.Data with
                {
                    Compare = new ChatDataCompare(
                        PrimaryLabel: primaryLabel,
                        CompareLabel: compareLabel,
                        CompareStats: compare.Data.Stats,
                        CompareRaw: compareRaw)
                };
                lastToolName = primary.ToolName;
                lastParams   = primary.Params;
                _log.LogInformation("[NativeTool] Compare built (cross-iter): {P} vs {C} ({Tool})",
                    primaryLabel, compareLabel, grouped.Key);
                trace?.Step("compare_built", "ok", 0,
                    $"Tổng hợp {grouped.Count()} call '{grouped.Key}' qua {iteration} iter → Compare: {primaryLabel} vs {compareLabel}",
                    new() { ["primary"] = primaryLabel, ["compare"] = compareLabel });
            }
        }

        // ── Kiem tra co bi hard-stop khong ────────────────────────────────────
        if (iteration >= MaxIterations && warning == null)
        {
            _log.LogWarning("[NativeTool] hit max iterations ({Max})", MaxIterations);
            warning = "AI vượt giới hạn vòng lặp (3).";

            // Trigger: iteration_limit_reached -- AI vuot gioi han vong lap (3)
            var question = input.History.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            _unresolved.Append(
                tag:            "iteration_limit_reached",
                sessionId:      input.SessionId,
                tenantId:       input.TenantId,
                question:       question,
                history:        input.History,
                plannerRaw:     null,
                toolChosen:     lastToolName,
                aiReplyPreview: finalText,
                provider:       "anthropic",
                model:          input.Model,
                iterations:     iteration,
                latencyMs:      totalLat,
                tokensIn:       totalInTok,
                tokensOut:      totalOutTok);
        }

        // KHÔNG tái dùng data cũ khi AI không gọi tool trong lượt này: lượt chỉ-text (AI hỏi lại, hoặc câu
        // không route ra tool) PHẢI để panel TRỰC QUAN HÓA TRỐNG — nếu giữ chart cũ (vd "Dòng tiền & Lợi
        // nhuận") thì gây hiểu nhầm là số liệu của câu hiện tại. memory.LastChatData vẫn được GIỮ NGUYÊN
        // (khối cập nhật memory bên dưới chỉ chạy khi lastData!=null) nên các luồng khác không bị ảnh hưởng.
        if (lastData == null && memory.LastChatData != null)
        {
            _log.LogInformation("[NativeTool] AI khong goi tool turn nay → panel de TRONG (khong reuse '{Title}')",
                memory.LastChatData.Title);
            trace?.Step("memory_data_skip", "no-tool", 0,
                "AI không gọi tool turn này → panel để trống, không reuse data cũ");
        }

        // Focus theo cau hoi USER (chi phi / loi nhuan / doanh thu) — gan TRUOC khi emit.
        // Truoc day gan trong loop nhung emit ngay → frontend nhan data nhieu lan,
        // moi lan vo hieu focus cu. Gio tinh 1 lan cuoi cung.
        if (lastData != null)
        {
            var userQ = input.History.LastOrDefault(m => m.Role == "user")?.Content;
            lastData = ChatDataBuilder.WithFocus(lastData, userQ);
        }

        // Emit DUY NHAT 1 lan sau khi loop xong + compare built + focus applied.
        // Frontend chi remount chart 1 lan → khong nhay/ve 2 lan.
        if (emit != null && lastData != null && !string.IsNullOrEmpty(lastData.Title))
            await emit(new { stage = "data", tool = lastToolName, data = lastData });

        // ── Guardrails ────────────────────────────────────────────────────────
        finalText = AgentGuardrails.StripMarkdown(AgentGuardrails.StripEmDash(finalText.Trim()));

        // Reply qua ngan VA chua hit max_iter → retry voi token cao hon (4000 → 6000).
        // Khac voi truoc: thay vi replace bang message generic, ta CO GANG re-call AI 1 lan.
        if (AgentGuardrails.IsTooShort(finalText) && warning == null && messages.Count > 1)
        {
            _log.LogWarning("[NativeTool] reply qua ngan ({Len} chars), retry voi max_tokens=6000",
                finalText?.Length ?? 0);
            try
            {
                var (_, retryDoc, retryLat) = await CallAnthropicAsync(
                    apiKey, model, system, tools, messages, linked.Token, maxTokens: 6000);
                totalLat += retryLat;
                var retryUsage = retryDoc.RootElement.GetProperty("usage");
                var retryInTok  = retryUsage.GetProperty("input_tokens").GetInt32();
                var retryOutTok = retryUsage.GetProperty("output_tokens").GetInt32();
                totalInTok  += retryInTok;
                totalOutTok += retryOutTok;
                // Retry cũng là 1 /messages POST → 1 lượt riêng.
                _usage.Append(callCtx.Feature, callCtx.SessionId, callCtx.Tenant, "anthropic", model, retryInTok, retryOutTok, retryLat);
                if (!string.IsNullOrEmpty(callCtx.Tenant)) _quota.Consume(callCtx.Tenant);
                var retrySb = new StringBuilder();
                foreach (var block in retryDoc.RootElement.GetProperty("content").EnumerateArray())
                {
                    if (block.GetProperty("type").GetString() == "text")
                        retrySb.Append(block.GetProperty("text").GetString());
                }
                retryDoc.Dispose();
                if (!AgentGuardrails.IsTooShort(retrySb.ToString()))
                    finalText = AgentGuardrails.StripMarkdown(AgentGuardrails.StripEmDash(retrySb.ToString().Trim()));
            }
            catch (Exception retryEx)
            {
                _log.LogWarning(retryEx, "[NativeTool] retry-on-short fail, giu finalText cu");
            }
        }

        // Sau retry van qua ngan + co data → message generic (giu cho UX dep)
        if (AgentGuardrails.IsTooShort(finalText) && lastData != null)
            finalText = $"Đã lấy được số liệu \"{lastData.Title}\" (xem bảng bên phải) nhưng chưa tạo được phần phân tích đầy đủ. Anh/Chị thử hỏi cụ thể hơn — vd \"so với tháng trước\", \"top khách hàng nào đóng góp nhiều nhất\".";

        // Validate so AI noi (warning only, khong block)
        var numWarning = lastData != null
            ? AgentGuardrails.ValidateNumbers(finalText, lastData.Stats) : null;
        if (numWarning != null && warning == null)
            warning = numWarning;

        // KHÔNG _usage.Append aggregate ở đây: đã append PER-ITER trong vòng lặp + retry-on-short.
        // Aggregate trước đây đếm 1 chat-stream = 1 lượt bất kể N iter → quota bị under-count.

        // Lưu bộ nhớ chat sau khi có kết quả thực sự (tool thành công + có data).
        if (lastData != null && lastToolName != null)
        {
            var paramsDict = new Dictionary<string, string>();
            if (lastParams != null)
            {
                // Serialize rồi parse lại để trích key/value dạng string an toàn.
                try
                {
                    var paramsEl = JsonSerializer.SerializeToElement(lastParams, _jsonWeb);
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                        foreach (var p in paramsEl.EnumerateObject())
                            paramsDict[p.Name] = p.Value.ValueKind == JsonValueKind.String
                                ? (p.Value.GetString() ?? "") : p.Value.GetRawText();
                }
                catch { /* bỏ qua nếu serialize lỗi */ }
            }

            // Lấy marketName/marketId từ params nếu có.
            paramsDict.TryGetValue("marketName", out var resolvedMarketName);
            int? resolvedMarketId = null;
            if (paramsDict.TryGetValue("marketId", out var midStr) && int.TryParse(midStr, out var mid))
                resolvedMarketId = mid;

            var newMemory = memory with
            {
                LastTool      = lastToolName,
                LastParams    = paramsDict,
                LastMarketName = resolvedMarketName ?? memory.LastMarketName,
                LastMarketId  = resolvedMarketId ?? memory.LastMarketId,
                LastDataTitle = lastData.Title,
                LastChatData  = lastData,  // FULL data để follow-up text-only vẫn hiện panel cũ
                History       = input.History.TakeLast(10).ToList()
            };
            _sessions.UpdateMemory(input.SessionId, newMemory);
        }

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

    /// Chay RunAsync voi emit callback de frontend thay progress tung turn (G2-5).
    /// Emit sequence:
    ///   {stage:"thinking", iteration}   — truoc moi AI call
    ///   {stage:"fetching", iteration, tools:[...]}  — khi co tool_use blocks
    ///   {stage:"data", iteration, tool, data}        — sau khi exec xong, co ChatData
    ///   {delta: text}                                — chuoi ky tu phan tich cuoi
    ///   {done: true, reply, toolName, data}          — ket thuc
    public async Task StreamAsync(AgentInput input, Func<object, Task> emit, CancellationToken ct)
    {
        AgentResult result;
        try
        {
            result = await RunCoreAsync(input, ct, emit: emit);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[NativeTool-stream] RunCoreAsync loi");
            await emit(new { error = ex.Message });
            await emit(new { done = true });
            return;
        }

        // Action nhan dien (khong phai tool doc) -> emit 1 event terminal DUY NHAT, CUNG SHAPE voi
        // JsonPlannerAgent ({done, reply, toolName="none", action, actionParams, data}) de
        // ChatAgentService.AskStreamAsync bat duoc qua reflection property "action"/"actionParams".
        // KHONG emit {delta} truoc do -- action khong co phan phan tich streaming.
        if (!string.IsNullOrWhiteSpace(result.Action))
        {
            await emit(new
            {
                done         = true,
                reply        = result.Reply,
                toolName     = "none",
                action       = result.Action,
                actionParams = result.Params,
                data         = (object?)result.Data
            });
            return;
        }

        // RunCoreAsync da emit {stage="data"} duy nhat sau khi loop xong → KHONG
        // emit lai o day (truoc day ve chart 2 lan voi compare). Chi emit text delta + done.
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
        CancellationToken ct,
        int maxTokens = 4000)
    {
        var body = new
        {
            model,
            max_tokens = maxTokens,
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

    // ── System prompt builder (bao gom memory context) ───────────────────────────

    /// Xây dựng system prompt: base + ngày hôm nay + context hội thoại trước (nếu có).
    private static string BuildSystemPrompt(SessionChatMemory memory)
    {
        var sb = new StringBuilder();
        sb.Append(SystemPromptBase);
        sb.Append($" Hôm nay: {DateTime.Now:yyyy-MM-dd}.");

        // Thêm context hội thoại trước để follow-up kế thừa tool + params.
        if (memory.LastTool != null)
        {
            sb.Append("\n\n<conversation_context>");
            sb.Append($"\nTool gần nhất: {memory.LastTool}");
            if (memory.LastParams != null && memory.LastParams.Count > 0)
            {
                var paramsLine = string.Join(", ", memory.LastParams.Select(p => $"{p.Key}={p.Value}"));
                sb.Append($"\nParams: {paramsLine}");
            }
            if (memory.LastMarketName != null)
                sb.Append($"\nThị trường đã chọn: {memory.LastMarketName} (id={memory.LastMarketId})");
            sb.Append("\nNếu user follow-up 'còn X thì sao' → giữ tool + params, chỉ đổi field user nói khác.");
            sb.Append("\n</conversation_context>");
        }

        return sb.ToString();
    }

    // ── Compare helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Đọc startDate hoặc startTime trong tool input, trả khóa string để ORDER theo thời gian.
    /// Kỳ gần hiện tại nhất → key lớn nhất → được chọn làm primary.
    /// </summary>
    private static string InferDateOrderKey(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        // Ưu tiên startDate, fallback endDate, fallback year
        foreach (var name in new[] { "startDate", "endDate", "startTime", "endTime", "year" })
        {
            if (input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            if (input.TryGetProperty(name, out var vn) && vn.ValueKind == JsonValueKind.Number)
                return vn.GetRawText();
        }
        return "";
    }

    /// <summary>
    /// Đoán nhãn kỳ hiển thị từ tool input: "Tháng 6/2026", "Năm 2025", "01/06 - 30/06/2026"...
    /// Heuristic gọn: nếu có startDate yyyy-MM-dd và endDate cùng tháng → "Tháng M/yyyy".
    /// </summary>
    private static string? InferPeriodLabel(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        string? start = null, end = null;
        if (input.TryGetProperty("startDate", out var s) && s.ValueKind == JsonValueKind.String) start = s.GetString();
        if (input.TryGetProperty("endDate",   out var e) && e.ValueKind == JsonValueKind.String) end   = e.GetString();
        if (start == null && input.TryGetProperty("year", out var y))
            return $"Năm {(y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : y.GetString())}";
        if (start == null || end == null) return null;

        if (DateTime.TryParse(start, out var sd) && DateTime.TryParse(end, out var ed))
        {
            // Cùng tháng cùng năm → "Tháng M/yyyy"
            if (sd.Year == ed.Year && sd.Month == ed.Month)
                return $"Tháng {sd.Month}/{sd.Year}";
            // Cùng năm khác tháng → "T1-T12/yyyy" hoặc "T{M1}-T{M2}/yyyy"
            if (sd.Year == ed.Year)
                return $"T{sd.Month}-T{ed.Month}/{sd.Year}";
            // Khác năm → "yyyy → yyyy"
            return $"{sd:yyyy-MM-dd} → {ed:yyyy-MM-dd}";
        }
        return $"{start} → {end}";
    }

    /// <summary>
    /// Parse safe JsonElement từ chuỗi tool_result. Lấy items[] nếu có envelope, ngược lại lấy root.
    /// </summary>
    private static JsonElement? TryParseRaw(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items))
                return items.Clone();
            return root.Clone();
        }
        catch { return null; }
    }
}
