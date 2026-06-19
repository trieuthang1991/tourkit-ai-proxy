// Services/Chat/ChatAgentService.cs
using TourkitAiProxy.Models;
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
    private readonly ILogger<ChatAgentService> _log;

    public ChatAgentService(
        IEnumerable<IAgentRuntime> runtimes,
        ProviderRegistry registry,
        AiModelRegistry modelRegistry,
        TkSessionStore sessions,
        Cache.ChatCache cache,
        UnresolvedQuestionsLog unresolved,
        IWorkflowTraceAccessor traceAccessor,
        ILogger<ChatAgentService> log)
    {
        _runtimes       = runtimes;
        _registry       = registry;
        _modelRegistry  = modelRegistry;
        _sessions       = sessions;
        _cache          = cache;
        _unresolved     = unresolved;
        _traceAccessor  = traceAccessor;
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
            await emit(evt);

            // Khi agent phat done -> ghi nho de luu L1 sau.
            if (evt is { } o)
            {
                var type = o.GetType();
                var doneProp = type.GetProperty("done");
                if (doneProp?.GetValue(o) is true)
                {
                    var replyProp  = type.GetProperty("reply");
                    var toolProp   = type.GetProperty("toolName");
                    var dataProp   = type.GetProperty("data");
                    var replyVal   = replyProp?.GetValue(o) as string ?? "";
                    var toolVal    = toolProp?.GetValue(o) as string ?? "none";
                    var dataVal    = dataProp?.GetValue(o) as ChatData;
                    streamResult = new ChatResult(replyVal, toolVal, null, dataVal, 0, 0, 0, null);
                }
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
