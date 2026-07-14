// Services/Chat/ChatAgentService.cs
using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Orchestrate 1 luot chat-analytics.
/// ChatAgentService chi lam:
///   1. Setup: resolve provider, truncate input, lay session info.
///   2. L1 cache lookup (pre-planner) -- hit thi tra ngay, 0 token AI.
///   3. Resolve IAgentRuntime phu hop (JsonPlannerAgent la fallback cho moi provider).
///   4. Goi runtime.RunAsync / runtime.StreamAsync.
///   5. Luu L1 cache sau khi co ket qua.
/// L2 cache nam trong agent runtime (can biet tool name moi lookup duoc).
/// </summary>
public class ChatAgentService
{
    private readonly IEnumerable<IAgentRuntime> _runtimes;
    private readonly ProviderRegistry _registry;
    private readonly AiModelRegistry _modelRegistry;
    private readonly TkSessionStore _sessions;
    private readonly Cache.ChatCache _cache;
    private readonly UnresolvedQuestionsLog _unresolved;
    private readonly IWorkflowTraceAccessor _traceAccessor;
    private readonly ActionExecutor _exec;
    private readonly ActionResolver _resolver;
    private readonly MailReplyService _mailReply;
    private readonly MailRepository _mailRepo;
    private readonly AiCallContext _aiCtx;
    private readonly ILogger<ChatAgentService> _log;

    public ChatAgentService(
        IEnumerable<IAgentRuntime> runtimes,
        ProviderRegistry registry,
        AiModelRegistry modelRegistry,
        TkSessionStore sessions,
        Cache.ChatCache cache,
        UnresolvedQuestionsLog unresolved,
        IWorkflowTraceAccessor traceAccessor,
        ActionExecutor exec,
        ActionResolver resolver,
        MailReplyService mailReply,
        MailRepository mailRepo,
        AiCallContext aiCtx,
        ILogger<ChatAgentService> log)
    {
        _runtimes       = runtimes;
        _registry       = registry;
        _modelRegistry  = modelRegistry;
        _sessions       = sessions;
        _cache          = cache;
        _unresolved     = unresolved;
        _traceAccessor  = traceAccessor;
        _exec           = exec;
        _resolver       = resolver;
        _mailReply      = mailReply;
        _mailRepo       = mailRepo;
        _aiCtx          = aiCtx;
        _log            = log;
    }

    public async Task<ChatResult> AskAsync(ChatRequest req, string sessionId, CancellationToken ct)
    {
        // Resolve qua AiModelRegistry → req.Provider/Model có thể null, đọc từ Models:ChatAnalytics.
        var resolved = _modelRegistry.Resolve(AiFeature.ChatAnalytics, req.Provider, req.Model);
        req = req with {
            Provider = resolved.Provider,
            Model    = resolved.Model,
            ApiKey   = req.ApiKey ?? resolved.ApiKey
        };
        var provider = _registry.Resolve(req.Provider);
        var history = req.Messages ?? new();
        var question = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // Trace collector: Step() là no-op khi req.Debug=false → zero overhead trong production.
        // Tương thích cả ?debug=1 / X-Debug header thông qua trace accessor (set bởi middleware ngoài).
        var trace = _traceAccessor.Current ?? new TraceCollector(req.Debug);
        trace.SetWorkflow("ChatAgent");
        trace.SetMeta("provider", provider.Id);
        trace.SetMeta("model", req.Model);

        // Truncate input truoc khi cache-key + truyen vao agent.
        var (truncQuestion, wasTruncated) = AgentGuardrails.TruncateInput(question, 1500);
        if (wasTruncated)
            trace.Step("input_truncate", "ok", 0,
                $"Câu hỏi {question.Length} ký tự > giới hạn 1500 → cắt còn 1500");
        if (wasTruncated)
        {
            _log.LogWarning("[chat] user input truncated tu {Orig} -> 1500 chars", question.Length);
            // Trigger: input_truncated -- cau hoi qua dai, bi cat truoc khi gui vao agent
            _unresolved.Append(
                tag:            "input_truncated",
                sessionId:      sessionId,
                tenantId:       "",  // chua resolve session luc nay
                question:       question,  // log nguyen ban truoc khi cat
                history:        history,
                plannerRaw:     null,
                toolChosen:     null,
                aiReplyPreview: null,
                provider:       req.Provider,
                model:          req.Model,
                iterations:     null,
                latencyMs:      0,
                tokensIn:       0,
                tokensOut:      0);
        }

        // Thay the question trong history bang version da truncate (de agent nhan dung).
        if (wasTruncated && history.Count > 0)
        {
            var last = history.LastOrDefault(m => m.Role == "user");
            if (last != null)
            {
                var idx = history.LastIndexOf(last);
                history = new List<ChatTurn>(history) { [idx] = new ChatTurn(last.Role, truncQuestion) };
            }
        }

        var session = _sessions.Get(sessionId);
        var tenantId = session?.TenantId ?? "";
        var username = session?.Username ?? "";
        // Cache PHAI scope theo user (phan quyen data co the khac giua cac user cung tenant).
        bool useCache = !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(username);

        // L1 cache (pre-planner): cau hoi y het sau khi normalize -> tra ngay, skip toan bo AI.
        var l1Key = AgentCacheKeys.L1Key(tenantId, username, truncQuestion);
        var l1Timer = trace.Begin("l1_cache_lookup");
        if (useCache && !string.IsNullOrWhiteSpace(truncQuestion)
            && _cache.TryGet<ChatResult>("r1|" + l1Key, out var l1Hit) && l1Hit != null)
        {
            _log.LogInformation("[chat] L1 cache hit");
            l1Timer.Done("ok", "L1 HIT — câu hỏi giống y hệt trong cache → trả ngay, skip toàn bộ AI",
                new() { ["cacheKey"] = l1Key });
            // Tra ket qua cache + dinh trace (chi co 1 step l1_cache_lookup) khi debug.
            return trace.Enabled
                ? l1Hit with { Trace = trace.Build() }
                : l1Hit;
        }
        l1Timer.Done("skip", useCache ? "L1 MISS — chưa có cache, chạy tiếp planner" : "L1 SKIP — chưa có session, không cache",
            new() { ["cacheKey"] = l1Key });

        // Resolve runtime: runtime dau tien Supports(provider), fallback JsonPlannerAgent.
        var runtime = _runtimes.FirstOrDefault(r => r.Supports(provider))
            ?? _runtimes.OfType<JsonPlannerAgent>().Single();
        var agentName = runtime.GetType().Name;
        trace.SetMeta("agent", agentName);
        trace.Step("runtime_select", "ok", 0,
            $"Provider {provider.Id} → dùng {agentName}",
            new() { ["provider"] = provider.Id, ["model"] = req.Model });

        var input = new AgentInput(
            Provider:  provider,
            Model:     req.Model,
            ApiKey:    req.ApiKey,
            History:   history,
            SessionId: sessionId,
            TenantId:  tenantId,
            Username:  username,
            Trace:     trace);

        var agentRunTimer = trace.Begin("agent_run");
        var agentResult = await runtime.RunAsync(input, ct);
        agentRunTimer.Done("ok",
            $"Agent xong sau {agentResult.Iterations} iteration, tool={agentResult.ToolName}, " +
            $"tokens={agentResult.InputTokens}/{agentResult.OutputTokens}",
            new() { ["tool"] = agentResult.ToolName, ["iterations"] = agentResult.Iterations });

        // Planner nhận diện HÀNH ĐỘNG (không phải câu hỏi số liệu) -- thực thi run-through
        // (check_mail/review_customer/score_deal) hoặc trả placeholder cho action cần xác nhận,
        // rồi trả ngay -- KHÔNG chạy tiếp path phân tích số liệu bình thường bên dưới.
        if (!string.IsNullOrWhiteSpace(agentResult.Action))
        {
            var actionTimer = trace.Begin("action_execute");
            var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
            var payload = await TryHandleActionAsync(
                agentResult.Action, AsJsonElement(agentResult.Params),
                sessionId, tenantId, jwt, username, req.Provider, req.Model, ct);
            var (actionReply, actionData, actionWarning) = FoldActionPayload(payload);
            actionTimer.Done("ok",
                $"Action '{agentResult.Action}' → {(string.IsNullOrWhiteSpace(actionReply) ? "(không có kết quả)" : actionReply)}",
                new() { ["action"] = agentResult.Action });

            var actionChatResult = new ChatResult(
                actionReply,
                "none",
                agentResult.Params,
                actionData,
                agentResult.LatencyMs,
                agentResult.InputTokens,
                agentResult.OutputTokens,
                actionWarning ?? agentResult.Warning);

            return trace.Enabled ? actionChatResult with { Trace = trace.Build() } : actionChatResult;
        }

        var result = new ChatResult(
            agentResult.Reply,
            agentResult.ToolName,
            agentResult.Params,
            agentResult.Data,
            agentResult.LatencyMs,
            agentResult.InputTokens,
            agentResult.OutputTokens,
            agentResult.Warning);

        // Luu L1 cache (chi khi co noi dung thuc su va co tenantId hop le).
        if (useCache && HasContent(agentResult.Data) && !string.IsNullOrWhiteSpace(truncQuestion))
        {
            var ttl = ChooseTtlFromResult(result);
            _cache.Set("r1|" + l1Key, result, ttl);
            trace.Step("l1_cache_save", "ok", 0,
                $"Lưu L1 cache TTL {ttl.TotalMinutes:0}phút (câu hỏi này hỏi lại sẽ trả ngay)");
        }

        return trace.Enabled ? result with { Trace = trace.Build() } : result;
    }

    /// <summary>
    /// Ban STREAMING: phat su kien qua emit -- {stage}, {delta}, {done}.
    /// </summary>
    public async Task AskStreamAsync(ChatRequest req, string sessionId, Func<object, Task> emit, CancellationToken ct)
    {
        // Resolve qua AiModelRegistry → req.Provider/Model có thể null, đọc từ Models:ChatAnalytics.
        var resolved = _modelRegistry.Resolve(AiFeature.ChatAnalytics, req.Provider, req.Model);
        req = req with {
            Provider = resolved.Provider,
            Model    = resolved.Model,
            ApiKey   = req.ApiKey ?? resolved.ApiKey
        };
        var provider = _registry.Resolve(req.Provider);
        var history = req.Messages ?? new();
        var question = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        var trace = _traceAccessor.Current ?? new TraceCollector(req.Debug);
        trace.SetWorkflow("ChatAgent");
        trace.SetMeta("provider", provider.Id);
        trace.SetMeta("model", req.Model);

        var (truncQuestion, wasTruncated) = AgentGuardrails.TruncateInput(question, 1500);
        if (wasTruncated) trace.Step("input_truncate", "ok", 0,
            $"Câu hỏi {question.Length} ký tự > giới hạn 1500 → cắt còn 1500");
        if (wasTruncated)
        {
            _log.LogWarning("[chat-stream] user input truncated tu {Orig} -> 1500 chars", question.Length);
            // Trigger: input_truncated (stream path)
            _unresolved.Append(
                tag:            "input_truncated",
                sessionId:      sessionId,
                tenantId:       "",
                question:       question,
                history:        history,
                plannerRaw:     null,
                toolChosen:     null,
                aiReplyPreview: null,
                provider:       req.Provider,
                model:          req.Model,
                iterations:     null,
                latencyMs:      0,
                tokensIn:       0,
                tokensOut:      0);
        }

        if (wasTruncated && history.Count > 0)
        {
            var last = history.LastOrDefault(m => m.Role == "user");
            if (last != null)
            {
                var idx = history.LastIndexOf(last);
                history = new List<ChatTurn>(history) { [idx] = new ChatTurn(last.Role, truncQuestion) };
            }
        }

        var session = _sessions.Get(sessionId);
        var tenantId = session?.TenantId ?? "";
        var username = session?.Username ?? "";
        bool useCache = !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(username);

        // L1 cache (pre-planner)
        var l1Key = AgentCacheKeys.L1Key(tenantId, username, truncQuestion);
        var l1Timer = trace.Begin("l1_cache_lookup");
        if (useCache && !string.IsNullOrWhiteSpace(truncQuestion)
            && _cache.TryGet<ChatResult>("r1|" + l1Key, out var l1Hit) && l1Hit != null)
        {
            _log.LogInformation("[chat-stream] L1 cache hit");
            l1Timer.Done("ok", "L1 HIT — trả ngay từ cache",
                new() { ["cacheKey"] = l1Key });
            await emit(new
            {
                done = true, reply = l1Hit.Reply, toolName = l1Hit.ToolName,
                data = l1Hit.Data, cached = true,
                trace = trace.Enabled ? trace.Build() : null
            });
            return;
        }
        l1Timer.Done("skip", "L1 MISS — chạy planner",
            new() { ["cacheKey"] = l1Key });

        var runtime = _runtimes.FirstOrDefault(r => r.Supports(provider))
            ?? _runtimes.OfType<JsonPlannerAgent>().Single();
        var agentName = runtime.GetType().Name;
        trace.SetMeta("agent", agentName);
        trace.Step("runtime_select", "ok", 0,
            $"Provider {provider.Id} → dùng {agentName}",
            new() { ["provider"] = provider.Id, ["model"] = req.Model });

        var input = new AgentInput(
            Provider:  provider,
            Model:     req.Model,
            ApiKey:    req.ApiKey,
            History:   history,
            SessionId: sessionId,
            TenantId:  tenantId,
            Username:  username,
            Trace:     trace);

        // Wrapping emit: bat su kien {done} de luu L1 cache
        // (chat data va reply co trong event done cua agent).
        ChatResult? streamResult = null;

        await runtime.StreamAsync(input, async evt =>
        {
            var type = evt.GetType();

            // Planner nhận diện HÀNH ĐỘNG -- JsonPlannerAgent phát 1 event terminal
            // {done, reply, toolName="none", action, actionParams, data}. Chặn (không forward
            // event thô này), thực thi run-through hoặc trả placeholder, rồi tự phát
            // action-result + done thay thế.
            var actionProp = type.GetProperty("action");
            var actionVal = actionProp?.GetValue(evt) as string;
            if (!string.IsNullOrWhiteSpace(actionVal))
            {
                var actionParamsProp = type.GetProperty("actionParams");
                var actionParamsRaw = actionParamsProp?.GetValue(evt);
                var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
                var payload = await TryHandleActionAsync(
                    actionVal, AsJsonElement(actionParamsRaw),
                    sessionId, tenantId, jwt, username, req.Provider, req.Model, ct);

                if (payload != null)
                {
                    await emit(payload);
                    await emit(new { done = true });
                    return;
                }
                // payload null (action khong co trong catalog) -> fallback: forward nguyen event.
            }

            await emit(evt);

            // Khi agent phat done -> ghi nho de luu L1 sau.
            var doneProp = type.GetProperty("done");
            if (doneProp?.GetValue(evt) is true)
            {
                var replyProp  = type.GetProperty("reply");
                var toolProp   = type.GetProperty("toolName");
                var dataProp   = type.GetProperty("data");
                var replyVal   = replyProp?.GetValue(evt) as string ?? "";
                var toolVal    = toolProp?.GetValue(evt) as string ?? "none";
                var dataVal    = dataProp?.GetValue(evt) as ChatData;
                streamResult = new ChatResult(replyVal, toolVal, null, dataVal, 0, 0, 0, null);
            }
        }, ct);

        // Luu L1 cache neu co noi dung.
        if (useCache && streamResult != null && HasContent(streamResult.Data)
            && !string.IsNullOrWhiteSpace(truncQuestion))
        {
            var ttl = ChooseTtlByData(streamResult.Data);
            _cache.Set("r1|" + l1Key, streamResult, ttl);
            trace.Step("l1_cache_save", "ok", 0,
                $"Lưu L1 cache TTL {ttl.TotalMinutes:0}phút");
        }

        // Emit trace event cuoi cung khi debug=true. Frontend nhan {trace:...} co the
        // render collapsible "Cach van hanh" duoi reply.
        if (trace.Enabled)
            await emit(new { trace = trace.Build() });
    }

    // ─── Actions (Task 10a: run-through actions; Task 10b: proposal/clarify cho confirm-actions) ──

    /// SSE/return payload cho 1 hành động đã xử lý (dù chạy thẳng hay chờ xác nhận). Property names
    /// PascalCase -> serialize camelCase (kind/result/proposal/clarify) qua JsonSerializerDefaults.Web
    /// ở endpoint, khớp shape "action-result"/"action-proposal"/"action-clarify" phía frontend.
    private sealed record ActionResultEnvelope(string Kind, ActionResult Result);
    private sealed record ActionProposalEnvelope(string Kind, ActionProposal Proposal);
    private sealed record ActionClarifyEnvelope(string Kind, ActionClarify Clarify);

    /// Chuyen 1 gia tri object (thuong la JsonElement boxed sau khi Deserialize&lt;object&gt;, hoac
    /// da la JsonElement qua reflection GetValue) ve JsonElement? -- dung chung cho AskAsync/AskStreamAsync.
    private static JsonElement? AsJsonElement(object? value)
        => value is JsonElement je ? je : null;

    /// Gap 1 payload action (ActionResultEnvelope/ActionProposalEnvelope/ActionClarifyEnvelope/null) ve
    /// (reply, data, warning) de nhet vao ChatResult (ban buffered AskAsync khong the mang thang object
    /// proposal/clarify -- ChatResult.Data la ChatData strongly-typed). Dung lai kenh Data.Kind/Raw giong
    /// cach ActionExecutor da lam cho customer-review/deal-score/mail-list -- frontend doc data.kind de
    /// biet render gi, chi them 2 kind moi "action-proposal"/"action-clarify".
    private static (string Reply, ChatData? Data, string? Warning) FoldActionPayload(object? payload)
        => payload switch
        {
            ActionResultEnvelope { Result: var r } =>
                (r.Message ?? "", r.Data, r.Warning),
            ActionProposalEnvelope { Proposal: var p } =>
                (p.Summary, new ChatData(
                    Kind: "action-proposal", Title: p.Title,
                    Raw: JsonSerializer.SerializeToElement(p, JsonOpts),
                    Stats: new List<ChatStat>(), Focus: null), null),
            ActionClarifyEnvelope { Clarify: var c } =>
                (c.Question, new ChatData(
                    Kind: "action-clarify", Title: null,
                    Raw: JsonSerializer.SerializeToElement(c, JsonOpts),
                    Stats: new List<ChatStat>(), Focus: null), null),
            _ => ("", null, null)
        };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// Nhan 1 "action" da duoc planner nhan dien (JsonPlannerAgent) + thuc thi:
    ///   - NeedsConfirm == false (check_mail/review_customer/score_deal) -> chay thang qua ActionExecutor.
    ///   - NeedsConfirm == true  (send_mail_reply/compose_mail/assign_task/create_appointment) -> resolve
    ///     ten->id (+ soan draft AI cho mail) roi tra action-proposal (san sang xac nhan) hoac
    ///     action-clarify (ten mo ho, can chon) hoac action-result (khong tim thay/thieu thong tin).
    /// Tra null khi 'action' khong khop entry nao trong ActionTools catalog (khong phai action hop le)
    /// -- caller se fallback forward event/flow nhu binh thuong.
    private async Task<object?> TryHandleActionAsync(
        string action, JsonElement? actionParams,
        string sessionId, string tenantId, string jwt, string username,
        string? provider, string? model, CancellationToken ct)
    {
        var paramsDict = new Dictionary<string, object?>();
        if (actionParams is { ValueKind: JsonValueKind.Object } obj)
            foreach (var prop in obj.EnumerateObject())
                paramsDict[prop.Name] = prop.Value;

        return await BuildActionEnvelopeAsync(
            action, paramsDict, sessionId, tenantId, jwt, username, provider, model, ct);
    }

    /// Cung logic voi TryHandleActionAsync nhung nhan thang Dictionary (khong qua JsonElement) -- dung
    /// chung boi chat (planner tu nhan dien action) VA endpoint POST /assistant/action/resolve (Task
    /// "clarify mang id"): sau khi user chon 1 lua chon trong action-clarify, endpoint inject id da chon
    /// vao params (xem AssistantActionEndpoints) roi goi lai method nay de rebuild proposal/result --
    /// KHONG re-resolve theo ten (tranh lap vo han khi nhieu ban ghi trung ten). Public de endpoint goi.
    public async Task<object?> BuildActionEnvelopeAsync(
        string action, Dictionary<string, object?> paramsDict,
        string sessionId, string tenantId, string jwt, string username,
        string? provider, string? model, CancellationToken ct)
    {
        var tool = ActionTools.Find(action);
        if (tool == null) return null;

        if (tool.NeedsConfirm)
        {
            var actionId = Guid.NewGuid().ToString("N");
            return action.ToLowerInvariant() switch
            {
                "assign_task" => await BuildAssignTaskProposalAsync(actionId, tool, paramsDict, jwt, ct),
                "create_appointment" => await BuildCreateAppointmentProposalAsync(actionId, tool, paramsDict, jwt, ct),
                "send_mail_reply" => await BuildSendMailReplyProposalAsync(
                    actionId, tool, paramsDict, tenantId, username, sessionId, provider, model, ct),
                "compose_mail" => await BuildComposeMailProposalAsync(
                    actionId, tool, paramsDict, tenantId, username, sessionId, provider, model, ct),
                _ => new ActionResultEnvelope("action-result",
                    new ActionResult(action, "Hành động cần xác nhận nhưng chưa được hỗ trợ.")),
            };
        }

        var execReq = new ActionExecuteRequest(
            ActionId: Guid.NewGuid().ToString("N"),
            Action:   action,
            Params:   paramsDict,
            Provider: provider,
            Model:    model);

        var result = await _exec.ExecuteAsync(execReq, tenantId, jwt, username, sessionId, ct);
        return new ActionResultEnvelope("action-result", result);
    }

    // ─── Proposal builders (Task 10b) — resolve tên→id qua ActionResolver, (mail) soạn draft AI buffered,
    //     rồi trả action-proposal (sẵn sàng xác nhận) hoặc action-clarify (tên mơ hồ, cần chọn) hoặc
    //     action-result (không tìm thấy/thiếu thông tin). KHÔNG thực thi (gửi mail/enqueue CRM) ở đây —
    //     việc đó chỉ xảy ra sau khi user xác nhận, qua POST /assistant/action/execute (ActionExecutor). ──

    private static object ClarifyEnvelope(
        string actionId, string action, string question, List<ActionChoice> choices,
        Dictionary<string, object?> paramsDict, string field)
        => new ActionClarifyEnvelope("action-clarify",
            new ActionClarify(actionId, action, question, choices, paramsDict, field));

    private static object NotFoundEnvelope(string action, string message)
        => new ActionResultEnvelope("action-result", new ActionResult(action, message));

    /// assign_task: resolve từng tên trong staffNames (CSV) qua ActionResolver -- tên mơ hồ (nhiều người
    /// trùng) dừng lại hỏi luôn (không đợi resolve hết CSV rồi mới hỏi tên đầu tiên mơ hồ); tên không thấy
    /// -> báo lỗi luôn. workflowName giữ nguyên THÔ, KHÔNG resolve (worker app-side tự resolve/đặt default,
    /// xem ActionResolver.ResolveWorkflowAsync). Params giữ NGUYÊN paramsDict gốc (staffNames vẫn là tên
    /// người, KHÔNG phải id) vì ActionExecutor.ExecuteAssignTaskAsync đã tự re-resolve tên->id khi execute
    /// thật -- ở đây chỉ resolve "thăm dò" để phát hiện sớm tên mơ hồ/không thấy cho UX xác nhận.
    private static readonly List<ActionOption> PriorityOptions = new()
    {
        new("cao", "Cao"),
        new("tb", "Trung bình"),
        new("thap", "Thấp"),
    };

    /// Chuẩn hóa chuỗi ưu tiên thô (từ planner, có thể "cao"/"high"/"trung bình"/rỗng…) về đúng 1 trong
    /// 3 key của PriorityOptions để bind vào <select> — mirror ActionExecutor.MapPriority nhưng trả
    /// string key (không phải int) vì field UI cần value khớp option. Không rõ/rỗng → mặc định "tb".
    private static string NormalizePriorityKey(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "cao" or "high" => "cao",
        "thap" or "thấp" or "low" => "thap",
        "tb" or "trung binh" or "trung bình" or "medium" => "tb",
        _ => "tb",
    };

    private async Task<object> BuildAssignTaskProposalAsync(
        string actionId, ActionTool tool, Dictionary<string, object?> p, string jwt, CancellationToken ct)
    {
        var name = ActionExecutor.Str(p, "name") ?? "Việc mới";
        var content = ActionExecutor.Str(p, "content");
        var staffNamesRaw = ActionExecutor.Str(p, "staffNames");
        var dueDate = ActionExecutor.Str(p, "dueDate");
        var prioritizedRaw = ActionExecutor.Str(p, "prioritized");

        // Sau khi user đã chọn 1 lựa chọn ở action-clarify (POST /assistant/action/resolve), id đã chọn
        // được inject vào "staffResolvedIds" (CSV) — dùng THẲNG, KHÔNG re-resolve theo tên (nếu re-resolve
        // lại "staffNames" gốc mà nhiều người trùng tên thì sẽ ra lại chính danh sách mơ hồ đó → lặp vô hạn).
        var staffResolvedIdsRaw = ActionExecutor.Str(p, "staffResolvedIds");
        var staffLabels = new List<string>();
        var resolvedIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(staffResolvedIdsRaw))
        {
            foreach (var id in staffResolvedIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                resolvedIds.Add(id);
                staffLabels.Add($"NV #{id}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(staffNamesRaw))
        {
            foreach (var raw in staffNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var outcome = await _resolver.ResolveStaffAsync(jwt, raw, ct);
                if (outcome.Ambiguous is { Count: > 0 })
                    return ClarifyEnvelope(actionId, "assign_task",
                        $"Tên nhân viên \"{raw}\" khớp nhiều người, bạn chọn ai?", outcome.Ambiguous, p, "staff");
                if (outcome.Id is null)
                    return NotFoundEnvelope("assign_task", $"Không tìm thấy nhân viên tên \"{raw}\".");
                staffLabels.Add(outcome.Label ?? raw);
                resolvedIds.Add(outcome.Id.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        // Dropdown "Người phụ trách": lấy toàn bộ NV làm options. key CỐ Ý là "staffResolvedIds"
        // (không phải "staffNames") — value của select LÀ id nhân viên, nên khi user đổi lựa chọn rồi
        // Xác nhận, editedVals gộp thẳng vào params.staffResolvedIds (xem confirmAction ở assistant.jsx)
        // → ActionExecutor dùng ID này TRỰC TIẾP, KHÔNG re-resolve theo tên → hội tụ, tránh mơ hồ trùng
        // tên (quy ước "staffResolvedIds" từ commit 33744ca).
        var staffOptions = (await _resolver.ListStaffAsync(jwt, ct))
            .Select(s => new ActionOption(
                s.Id.ToString(CultureInfo.InvariantCulture),
                s.Hint != null ? $"{s.Name} ({s.Hint})" : s.Name))
            .ToList();
        var staffValue = resolvedIds.Count > 0 ? resolvedIds[0]
            : (staffOptions.Count > 0 ? staffOptions[0].Value : "");

        var priority = NormalizePriorityKey(prioritizedRaw);

        var fields = new List<ActionField>
        {
            new("name", "Tên việc", name, "text"),
            new("content", "Nội dung", content, "textarea"),
            new("staffResolvedIds", "Người phụ trách", staffValue, "select", staffOptions),
            new("dueDate", "Hạn hoàn thành", dueDate, "datetime"),
            new("prioritized", "Ưu tiên", priority, "select", PriorityOptions),
        };

        var staffLabel = staffLabels.Count > 0 ? string.Join(", ", staffLabels) : "(chưa rõ người phụ trách)";
        var summary = $"Giao việc \"{name}\" cho {staffLabel}"
            + (string.IsNullOrWhiteSpace(dueDate) ? "" : $", hạn {dueDate}");

        var proposal = new ActionProposal(actionId, "assign_task", tool.Title, summary, p, fields, true);
        return new ActionProposalEnvelope("action-proposal", proposal);
    }

    /// create_appointment: resolve customerName qua ActionResolver (bỏ qua nếu đã có customerId thẳng —
    /// mirror ActionExecutor.ExecuteCreateAppointmentAsync). Params giữ nguyên paramsDict gốc.
    private async Task<object> BuildCreateAppointmentProposalAsync(
        string actionId, ActionTool tool, Dictionary<string, object?> p, string jwt, CancellationToken ct)
    {
        var careTitle = ActionExecutor.Str(p, "careTitle") ?? "Lịch hẹn";
        var careDetail = ActionExecutor.Str(p, "careDetail");
        var startTime = ActionExecutor.Str(p, "startTime");
        var endTime = ActionExecutor.Str(p, "endTime");

        string customerLabel;
        // Đã chọn ở action-clarify trước đó (POST /assistant/action/resolve) → id đã chọn nằm trong
        // "customerResolvedId", dùng THẲNG, KHÔNG re-resolve theo "customerName" gốc (tránh lặp vô hạn
        // khi nhiều KH trùng tên).
        var customerResolvedId = ActionExecutor.Str(p, "customerResolvedId");
        var customerIdParam = ActionExecutor.Str(p, "customerId");
        var customerName = ActionExecutor.Str(p, "customerName");
        if (!string.IsNullOrWhiteSpace(customerResolvedId))
        {
            customerLabel = customerName ?? $"KH #{customerResolvedId}";
        }
        else if (!string.IsNullOrWhiteSpace(customerIdParam))
        {
            customerLabel = customerName ?? $"KH #{customerIdParam}";
        }
        else if (!string.IsNullOrWhiteSpace(customerName))
        {
            var outcome = await _resolver.ResolveCustomerAsync(jwt, customerName, ct);
            if (outcome.Ambiguous is { Count: > 0 })
                return ClarifyEnvelope(actionId, "create_appointment",
                    $"Tên khách hàng \"{customerName}\" khớp nhiều người, bạn chọn ai?", outcome.Ambiguous, p, "customer");
            if (outcome.Id is null)
                return NotFoundEnvelope("create_appointment", $"Không tìm thấy khách hàng tên \"{customerName}\".");
            customerLabel = outcome.Label ?? customerName;
        }
        else
        {
            return NotFoundEnvelope("create_appointment", "Thiếu thông tin khách hàng để tạo lịch hẹn.");
        }

        var fields = new List<ActionField>
        {
            new("careTitle", "Tiêu đề", careTitle, "text"),
            new("careDetail", "Chi tiết", careDetail, "textarea"),
            new("startTime", "Bắt đầu", startTime, "datetime"),
            new("endTime", "Kết thúc", endTime, "datetime"),
        };
        var summary = $"Đặt lịch hẹn với {customerLabel}: {careTitle}"
            + (string.IsNullOrWhiteSpace(startTime) ? "" : $", lúc {startTime}");

        var proposal = new ActionProposal(actionId, "create_appointment", tool.Title, summary, p, fields, true);
        return new ActionProposalEnvelope("action-proposal", proposal);
    }

    /// send_mail_reply: cần mailId (planner tự điền khi user đang xem/vừa hỏi về 1 email cụ thể — resolve
    /// mailQuery→mailId qua tìm kiếm hộp thư chưa làm ở đây, quá phức tạp cho scope 10b) → load mail →
    /// soạn draft AI BUFFERED (DraftStreamAsync đã buffered nội bộ — chỉ gọi onDelta 1 lần với full text,
    /// xem MailReplyService.RunAsync — nên onDelta truyền vào đây là no-op) → gói vào field "replyText"
    /// editable. Bọc AiCallContext.Push để trừ quota tenant + log đúng feature (mirror ActionExecutor).
    private async Task<object> BuildSendMailReplyProposalAsync(
        string actionId, ActionTool tool, Dictionary<string, object?> p,
        string tenantId, string username, string sessionId, string? provider, string? model, CancellationToken ct)
    {
        var mailId = ActionExecutor.Str(p, "mailId");
        if (string.IsNullOrWhiteSpace(mailId))
            return NotFoundEnvelope("send_mail_reply",
                "Vui lòng cho biết trả lời email nào (mở email đó rồi hỏi lại, hoặc nêu rõ tiêu đề/người gửi).");

        var mail = _mailRepo.Get(tenantId, mailId);
        if (mail is null)
            return NotFoundEnvelope("send_mail_reply", $"Không tìm thấy email #{mailId}.");

        var tone = ActionExecutor.Str(p, "tone") ?? "lich_su";
        var instruction = ActionExecutor.Str(p, "instruction");

        string draft;
        using (_aiCtx.Push(AiFeatures.AssistantAction, tenantId, sessionId))
        {
            var draftReq = new DraftReplyRequest(tone, instruction, provider, model, null);
            draft = await _mailReply.DraftStreamAsync(tenantId, username, mail, draftReq, _ => Task.CompletedTask, ct);
        }

        var fields = new List<ActionField> { new("replyText", "Nội dung trả lời", draft, "textarea") };
        var paramsOut = new Dictionary<string, object?>(p) { ["mailId"] = mailId, ["replyText"] = draft };
        var summary = $"Trả lời {mail.From.Name} — {mail.Subject}";

        var proposal = new ActionProposal(actionId, "send_mail_reply", tool.Title, summary, paramsOut, fields, true);
        return new ActionProposalEnvelope("action-proposal", proposal);
    }

    /// compose_mail: soạn draft AI BUFFERED (ComposeNewStreamAsync — cùng cơ chế buffered như
    /// DraftStreamAsync, xem ghi chú trên) từ brief/tone → gói vào field "text" editable.
    private async Task<object> BuildComposeMailProposalAsync(
        string actionId, ActionTool tool, Dictionary<string, object?> p,
        string tenantId, string username, string sessionId, string? provider, string? model, CancellationToken ct)
    {
        var to = ActionExecutor.Str(p, "to");
        var subject = ActionExecutor.Str(p, "subject");
        var brief = ActionExecutor.Str(p, "brief");
        if (string.IsNullOrWhiteSpace(to))
            return NotFoundEnvelope("compose_mail", "Thiếu người nhận để soạn email.");
        if (string.IsNullOrWhiteSpace(brief))
            return NotFoundEnvelope("compose_mail", "Thiếu nội dung chính để soạn email.");

        var tone = ActionExecutor.Str(p, "tone") ?? "lich_su";

        string draft;
        using (_aiCtx.Push(AiFeatures.AssistantAction, tenantId, sessionId))
        {
            var composeReq = new ComposeDraftRequest(to, subject, brief, tone, provider, model, null);
            draft = await _mailReply.ComposeNewStreamAsync(tenantId, username, composeReq, _ => Task.CompletedTask, ct);
        }

        var fields = new List<ActionField>
        {
            new("to", "Người nhận", to, "text"),
            new("subject", "Tiêu đề", subject, "text"),
            new("text", "Nội dung", draft, "textarea"),
        };
        var paramsOut = new Dictionary<string, object?>(p) { ["to"] = to, ["subject"] = subject ?? "", ["text"] = draft };
        var summary = $"Soạn email mới gửi {to}" + (string.IsNullOrWhiteSpace(subject) ? "" : $": {subject}");

        var proposal = new ActionProposal(actionId, "compose_mail", tool.Title, summary, paramsOut, fields, true);
        return new ActionProposalEnvelope("action-proposal", proposal);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static bool HasContent(ChatData? d)
        => d != null && (d.Stats.Count > 0
            || (d.Raw is { ValueKind: System.Text.Json.JsonValueKind.Array } arr && arr.GetArrayLength() > 0));

    // TTL ngan (3 phut) neu result co data cua thang hien tai (dang cap nhat lien tuc).
    // TTL dai (15 phut) cho query thang cu/quy co dinh.
    private static TimeSpan ChooseTtlFromResult(ChatResult r)
        => ChooseTtlByData(r.Data);

    private static TimeSpan ChooseTtlByData(ChatData? data)
    {
        // Khong co data -> TTL ngan mac dinh
        if (data == null) return TimeSpan.FromMinutes(5);
        // Dung Title (titile co the chua thang/nam) lam hint nhe; neu khong xac dinh -> TTL ngan an toan
        return TimeSpan.FromMinutes(5);
    }
}
