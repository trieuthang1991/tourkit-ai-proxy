using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Orchestrate 1 lượt chat-analytics (luồng 1-lượt, KHÔNG native function-calling):
///   1. Planner: AI chọn ĐÚNG 1 tool (hoặc 'none') + params → JSON.
///   2. Dispatch: gọi TourKit.Api (đọc) lấy dữ liệu thật.
///   3. Số liệu (stats) TÍNH SERVER-SIDE từ dữ liệu thật — KHÔNG để AI bịa số.
///   4. Analysis: AI pass 2 viết phân tích tiếng Việt bám số liệu.
/// Template: Services/Reviews/ReviewService.cs (prompt → CompleteAsync → parse JSON).
/// </summary>
public class ChatAgentService
{
    private readonly ProviderRegistry _registry;
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly Cache.ChatCache _cache;   // Redis (nếu cấu hình) / in-memory
    private readonly ILogger<ChatAgentService> _log;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    // Danh mục thị trường theo tenant (cho resolver tên→id) — giữ in-memory, đổi chậm. TTL dài.
    private readonly ConcurrentDictionary<string, (List<(int Id, string Name)> List, DateTime Exp)> _markets = new();
    private static readonly TimeSpan MarketTtl = TimeSpan.FromHours(6);

    public ChatAgentService(ProviderRegistry registry, TourKitApiClient api, TkSessionStore sessions, Cache.ChatCache cache, ILogger<ChatAgentService> log)
    {
        _registry = registry; _api = api; _sessions = sessions; _cache = cache; _log = log;
    }

    public async Task<ChatResult> AskAsync(ChatRequest req, string sessionId, CancellationToken ct)
    {
        var provider = _registry.Resolve(req.Provider);
        var history = req.Messages ?? new();
        var question = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        var tenantId = _sessions.Get(sessionId)?.TenantId ?? "";
        // Bỏ qua cache khi không có tenantId thật (session expired/invalid) — tránh pollute cross-tenant.
        bool useCache = !string.IsNullOrWhiteSpace(tenantId);

        // L1 cache (pre-planner): câu hỏi y hệt sau khi normalize → trả ngay, skip toàn bộ AI.
        // TTL ngắn (3 phút) để user F5/reload không bị stale lâu.
        var l1Key = AgentCacheKeys.L1Key(tenantId, question);
        if (useCache && !string.IsNullOrWhiteSpace(question)
            && _cache.TryGet<ChatResult>("r1|" + l1Key, out var l1Hit) && l1Hit != null)
        {
            _log.LogInformation("[chat] L1 cache hit");
            return l1Hit;
        }

        int tokIn = 0, tokOut = 0;
        long latency = 0;

        // ─── 1. Planner ───────────────────────────────────────────────────────────
        var plannerReq = new CompleteRequest(
            Prompt:      BuildPlannerPrompt(history),
            Provider:    req.Provider, Model: req.Model,
            MaxTokens:   3000, Temperature: 0.1,   // reasoning model có thể "nghĩ" dài trước JSON
            System:      PLANNER_SYSTEM, ApiKey: req.ApiKey);

        var plan = await CompleteWithFallbackAsync(provider, plannerReq, ct);
        tokIn += plan.InputTokens; tokOut += plan.OutputTokens; latency += plan.LatencyMs;

        string toolName = "none"; JsonElement? toolParams = null; string? directReply = null;
        try
        {
            using var doc = LooseJson.ParseFirstObject(plan.Text);
            var root = doc.RootElement;
            toolName = GetStr(root, "tool") ?? "none";
            if (root.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object)
                toolParams = pr.Clone();
            directReply = GetStr(root, "reply");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Planner JSON parse fail — fallback none. Raw: {Raw}",
                plan.Text[..Math.Min(plan.Text.Length, 200)]);
        }

        var tool = ChatTools.Find(toolName);

        // Lưới an toàn: planner fail/none nhưng câu hỏi rõ ràng cần số liệu → định tuyến theo từ khóa.
        if (tool == null)
        {
            var (hName, hParams) = HeuristicRoute(question);
            if (hName != null) { toolName = hName; toolParams = hParams; tool = ChatTools.Find(hName); }
        }

        // ─── Không cần dữ liệu → trả reply thẳng ───────────────────────────────────
        if (tool == null)
        {
            var reply = !string.IsNullOrWhiteSpace(directReply)
                ? directReply!
                : "Mình là trợ lý số liệu Tourkit. Anh/Chị có thể hỏi: doanh thu tháng này, top khách hàng, danh sách tour sắp đi, nguồn marketing...";
            return new ChatResult(reply, "none", null, null, latency, tokIn, tokOut, plan.Warning);
        }

        // ─── 2. Dispatch sang TourKit.Api (đọc) ────────────────────────────────────
        // Resolver: đổi marketName ("Nội địa miền Nam") → marketId trước khi gọi (multi-step có kiểm soát).
        toolParams = await ResolveMarketAsync(sessionId, toolParams, ct);

        // L2 cache (post-planner): tool + canonical params giống → trả ngay,
        // skip dispatch + analysis. TTL 5 phút.
        var l2Key = AgentCacheKeys.L2Key(tenantId, tool.Name, toolParams);
        if (useCache && _cache.TryGet<ChatResult>("r2|" + l2Key, out var l2Hit) && l2Hit != null)
        {
            _log.LogInformation("[chat] L2 cache hit ({Tool})", tool.Name);
            return l2Hit;
        }

        var path = ChatTools.BuildPath(tool, toolParams);
        _log.LogInformation("[chat] tool={Tool} path={Path}", tool.Name, path);

        var cacheKey = $"{tenantId}|{path}";

        JsonElement data;
        if (_cache.TryGet<JsonElement>("d|" + cacheKey, out var cached))
        {
            data = cached;   // cache hit — KHÔNG gọi TourKit
            _log.LogInformation("[chat] cache hit ({Key})", cacheKey);
        }
        else
        {
            var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
            try
            {
                data = await _api.GetAsync(jwt, path, ct);
            }
            catch (TourKitApiException ex) when (ex.Status == 401)
            {
                // JWT hết hạn giữa chừng → re-login 1 lần rồi thử lại.
                jwt = await _sessions.ForceReloginAsync(sessionId, ct);
                data = await _api.GetAsync(jwt, path, ct);
            }
            if (IsUsableData(data)) _cache.Set("d|" + cacheKey, data, CacheTtl);
        }

        // ─── 3. Đọc envelope /api/ai/* (items + summary + total + title) ───────────
        var chatData = BuildChatData(tool, data);

        // ─── 4. Analysis (AI pass 2) — pass history để bắt nhịp câu trước-sau ──────
        var analysisReq = new CompleteRequest(
            Prompt:      BuildAnalysisPrompt(history, tool, chatData.Raw ?? data, chatData.Stats),
            Provider:    req.Provider, Model: req.Model,
            MaxTokens:   2000, Temperature: 0.4,
            System:      ANALYSIS_SYSTEM, ApiKey: req.ApiKey);

        var analysis = await CompleteWithFallbackAsync(provider, analysisReq, ct);
        tokIn += analysis.InputTokens; tokOut += analysis.OutputTokens; latency += analysis.LatencyMs;

        // Apply guardrails: strip em-dash, retry neu qua ngan, validate so.
        var rawReply = analysis.Text;
        if (AgentGuardrails.IsTooShort(rawReply))
        {
            _log.LogWarning("[chat] analysis qua ngan ({Len} chars), retry voi max_tokens cao hon", rawReply?.Length ?? 0);
            var retryReq = analysisReq with { MaxTokens = (analysisReq.MaxTokens ?? 2000) * 3 / 2 };
            var retry = await CompleteWithFallbackAsync(provider, retryReq, ct);
            if (!AgentGuardrails.IsTooShort(retry.Text))
            {
                rawReply = retry.Text;
                tokIn += retry.InputTokens; tokOut += retry.OutputTokens; latency += retry.LatencyMs;
            }
        }

        var finalReply = string.IsNullOrWhiteSpace(rawReply)
            ? "Đã lấy được số liệu (xem bảng bên phải) nhưng chưa tạo được phần phân tích."
            : AgentGuardrails.StripEmDash(rawReply.Trim());

        // Validate so AI noi (warning only, khong block)
        var numberWarning = AgentGuardrails.ValidateNumbers(finalReply, chatData.Stats);
        var combinedWarning = string.Join(" | ", new[] { analysis.Warning, numberWarning }.Where(x => !string.IsNullOrWhiteSpace(x)));

        object? prmsOut = toolParams.HasValue ? JsonSerializer.Deserialize<object>(toolParams.Value.GetRawText()) : null;
        var result = new ChatResult(finalReply, tool.Name, prmsOut, chatData, latency, tokIn, tokOut, combinedWarning);

        // Lưu L1 + L2 cache (chỉ khi có nội dung thực sự và có tenantId hợp lệ).
        if (useCache && HasContent(chatData))
        {
            var ttl = ChooseTtl(toolParams);
            if (!string.IsNullOrWhiteSpace(question)) _cache.Set("r1|" + l1Key, result, ttl);
            _cache.Set("r2|" + l2Key, result, ttl);
        }
        return result;
    }

    /// <summary>
    /// Bản STREAMING: phát sự kiện qua `emit` — {stage} (planning/fetching/analyzing, kèm data sớm),
    /// {delta} cho từng đoạn chữ phân tích, {done} cuối. Để UI hiển thị tiến trình + chữ chạy dần.
    /// </summary>
    public async Task AskStreamAsync(ChatRequest req, string sessionId, Func<object, Task> emit, CancellationToken ct)
    {
        var provider = _registry.Resolve(req.Provider);
        var history = req.Messages ?? new();
        var question = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var tenantId = _sessions.Get(sessionId)?.TenantId ?? "";
        // Bỏ qua cache khi không có tenantId thật (session expired/invalid) — tránh pollute cross-tenant.
        bool useCache = !string.IsNullOrWhiteSpace(tenantId);

        // L1 cache (pre-planner): câu hỏi y hệt sau khi normalize → trả ngay, skip toàn bộ AI.
        var l1Key = AgentCacheKeys.L1Key(tenantId, question);
        if (useCache && !string.IsNullOrWhiteSpace(question)
            && _cache.TryGet<ChatResult>("r1|" + l1Key, out var l1Hit) && l1Hit != null)
        {
            _log.LogInformation("[chat-stream] L1 cache hit");
            await emit(new { done = true, reply = l1Hit.Reply, toolName = l1Hit.ToolName, data = l1Hit.Data, cached = true });
            return;
        }

        await emit(new { stage = "planning" });
        var plannerReq = new CompleteRequest(BuildPlannerPrompt(history), req.Provider, req.Model, 3000, 0.1, PLANNER_SYSTEM, req.ApiKey);
        var plan = await CompleteWithFallbackAsync(provider, plannerReq, ct);

        string toolName = "none"; JsonElement? toolParams = null; string? directReply = null;
        try
        {
            using var doc = LooseJson.ParseFirstObject(plan.Text);
            var root = doc.RootElement;
            toolName = GetStr(root, "tool") ?? "none";
            if (root.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object) toolParams = pr.Clone();
            directReply = GetStr(root, "reply");
        }
        catch (Exception ex) { _log.LogWarning(ex, "Planner JSON parse fail (stream) — fallback none"); }

        var tool = ChatTools.Find(toolName);
        if (tool == null)
        {
            var (hName, hParams) = HeuristicRoute(question);
            if (hName != null) { toolName = hName; toolParams = hParams; tool = ChatTools.Find(hName); }
        }

        if (tool == null)
        {
            var reply = !string.IsNullOrWhiteSpace(directReply) ? directReply!
                : "Mình là trợ lý số liệu Tourkit. Anh/Chị có thể hỏi: doanh thu tháng này, top khách hàng, tour sắp khởi hành, nguồn marketing...";
            await emit(new { done = true, reply, toolName = "none", data = (object?)null });
            return;
        }

        await emit(new { stage = "fetching", tool = tool.Name, title = tool.Title });
        toolParams = await ResolveMarketAsync(sessionId, toolParams, ct);

        // L2 cache (post-planner): tool + canonical params giống → trả ngay,
        // skip dispatch + analysis. TTL 5 phút.
        var l2Key = AgentCacheKeys.L2Key(tenantId, tool.Name, toolParams);
        if (useCache && _cache.TryGet<ChatResult>("r2|" + l2Key, out var l2Hit) && l2Hit != null)
        {
            _log.LogInformation("[chat-stream] L2 cache hit ({Tool})", tool.Name);
            await emit(new { done = true, reply = l2Hit.Reply, toolName = l2Hit.ToolName, data = l2Hit.Data, cached = true });
            return;
        }

        var path = ChatTools.BuildPath(tool, toolParams);
        _log.LogInformation("[chat-stream] tool={Tool} path={Path}", tool.Name, path);

        var cacheKey = $"{tenantId}|{path}";
        JsonElement data;
        if (_cache.TryGet<JsonElement>("d|" + cacheKey, out var cached)) data = cached;
        else
        {
            var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
            try { data = await _api.GetAsync(jwt, path, ct); }
            catch (TourKitApiException ex) when (ex.Status == 401)
            {
                jwt = await _sessions.ForceReloginAsync(sessionId, ct);
                data = await _api.GetAsync(jwt, path, ct);
            }
            if (IsUsableData(data)) _cache.Set("d|" + cacheKey, data, CacheTtl);
        }

        var chatData = BuildChatData(tool, data);

        // Gửi DATA SỚM → panel phải hiện số liệu/biểu đồ ngay, trong khi chữ phân tích chạy dần.
        await emit(new { stage = "analyzing", tool = tool.Name, data = chatData });

        var analysisReq = new CompleteRequest(BuildAnalysisPrompt(history, tool, chatData.Raw ?? data, chatData.Stats), req.Provider, req.Model, 2000, 0.4, ANALYSIS_SYSTEM, req.ApiKey);
        var sb = new StringBuilder();
        var analysis = await StreamWithFallbackAsync(provider, analysisReq,
            async delta => { sb.Append(delta); await emit(new { delta }); }, ct);

        var rawStreamReply = sb.Length > 0 ? sb.ToString()
            : (string.IsNullOrWhiteSpace(analysis.Text) ? "" : analysis.Text);

        // Apply guardrails: strip em-dash, validate so.
        var finalReply = string.IsNullOrWhiteSpace(rawStreamReply)
            ? "Đã lấy được số liệu (xem bảng bên phải)."
            : AgentGuardrails.StripEmDash(rawStreamReply.Trim());

        // Validate so AI noi (warning only, khong block)
        var numberWarning = AgentGuardrails.ValidateNumbers(finalReply, chatData.Stats);
        var combinedWarning = string.Join(" | ", new[] { analysis.Warning, numberWarning }.Where(x => !string.IsNullOrWhiteSpace(x)));

        object? prmsOut = toolParams.HasValue ? JsonSerializer.Deserialize<object>(toolParams.Value.GetRawText()) : null;

        // Luu L1 + L2 cache (chi khi co noi dung thuc su va co tenantId hop le).
        if (useCache && HasContent(chatData))
        {
            var ttl = ChooseTtl(toolParams);
            var streamResult = new ChatResult(finalReply, tool.Name, prmsOut, chatData, analysis.LatencyMs, analysis.InputTokens, analysis.OutputTokens, combinedWarning);
            if (!string.IsNullOrWhiteSpace(question)) _cache.Set("r1|" + l1Key, streamResult, ttl);
            _cache.Set("r2|" + l2Key, streamResult, ttl);
        }
        await emit(new { done = true, reply = finalReply, toolName = tool.Name, data = chatData });
    }

    /// Stream có fallback: chỉ fallback sang provider mặc định nếu CHƯA phát delta nào (tránh trùng chữ).
    private async Task<CompleteResult> StreamWithFallbackAsync(IAiProvider primary, CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var any = false;
        try { return await primary.StreamAsync(req, async d => { any = true; await onDelta(d); }, ct); }
        catch (Exception ex) when (!any && (ex is UpstreamException || ex is InvalidOperationException))
        {
            var def = _registry.Resolve(null);
            if (def.Id == primary.Id) throw;
            _log.LogWarning("AI stream {P} lỗi → fallback {Def}", primary.Id, def.Id);
            return await def.StreamAsync(req with { Provider = def.Id, Model = null, ApiKey = null }, onDelta, ct);
        }
    }

    // ─── Prompts ────────────────────────────────────────────────────────────────
    /// Gọi AI; nếu provider đang chọn lỗi upstream (vd 9routes 401 token hết hạn) → tự fallback
    /// sang provider mặc định để trợ lý không chết.
    private async Task<CompleteResult> CompleteWithFallbackAsync(IAiProvider primary, CompleteRequest req, CancellationToken ct)
    {
        try { return await primary.CompleteAsync(req, ct); }
        catch (Exception ex) when (ex is UpstreamException || ex is InvalidOperationException)
        {
            // UpstreamException = AI lỗi (vd 401); InvalidOperationException = thiếu API key.
            var def = _registry.Resolve(null);
            if (def.Id == primary.Id) throw;   // không có gì để fallback
            var status = (ex as UpstreamException)?.Status;
            _log.LogWarning("AI provider {P} lỗi ({Reason}) → fallback sang {Def}",
                primary.Id, status?.ToString() ?? ex.Message, def.Id);
            return await def.CompleteAsync(req with { Provider = def.Id, Model = null, ApiKey = null }, ct);
        }
    }

    private const string PLANNER_SYSTEM =
        "Bạn là bộ định tuyến (router) cho trợ lý số liệu công ty du lịch Tourkit. " +
        "Đọc câu hỏi và CHỌN ĐÚNG 1 tool để lấy số liệu, hoặc 'none' nếu chào hỏi/không cần dữ liệu. " +
        "TUYỆT ĐỐI KHÔNG suy luận, KHÔNG giải thích, KHÔNG viết câu nào ngoài JSON. " +
        "Ký tự ĐẦU TIÊN của output BẮT BUỘC là '{'. Chỉ trả 1 object JSON duy nhất rồi dừng.";

    // Lưới an toàn khi planner trả sai/không-JSON: định tuyến nhanh theo từ khóa tiếng Việt.
    private static (string? tool, JsonElement? prms) HeuristicRoute(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        bool Has(params string[] ws) => ws.Any(w => q.Contains(w));
        JsonElement P(object o) => JsonSerializer.SerializeToElement(o);
        var now = DateTime.Now;

        // CHI TIẾT/ĐẦY ĐỦ chỉ số, hoặc hỏi đích danh thực thu/công nợ/thực chi → financial_summary (12 KPI).
        if (Has("chi tiết", "đầy đủ", "tất cả chỉ số", "kpi", "công nợ", "thực thu", "thực chi", "lợi nhuận ròng", "tổng quan tài chính"))
        {
            var fStart = new DateTime(now.Year, now.Month, 1);
            return ("financial_summary", P(new { startDate = fStart.ToString("yyyy-MM-dd"), endDate = now.ToString("yyyy-MM-dd") }));
        }

        // Chuỗi thời gian (cashflow): "dòng tiền", "biểu đồ", hoặc chỉ số tài chính kèm "tháng/12 tháng/xu hướng/so sánh".
        if (Has("dòng tiền", "cashflow", "xu hướng", "biểu đồ", "đồ thị")
            || (Has("tháng", "12 tháng", "theo tháng", "hàng tháng", "so sánh", "gần đây")
                && Has("doanh thu", "doanh số", "chi phí", "lợi nhuận", "lãi")))
        {
            var start = new DateTime(now.Year, now.Month, 1).AddMonths(-11);
            return ("cashflow", P(new { startDate = start.ToString("yyyy-MM-dd"), endDate = now.ToString("yyyy-MM-dd"), groupBy = "month" }));
        }
        if (Has("top khách", "khách hàng chi tiêu", "khách vip", "khách hàng tốt", "mua nhiều")) return ("top_customers", null);
        if (Has("top nhân viên", "top seller", "nhân viên", "sale giỏi")) return ("top_sellers", null);
        if (Has("marketing", "nguồn khách", "nguồn kh", "kênh")) return ("marketing", null);
        if (Has("lịch hẹn", "cuộc hẹn", "cskh")) return ("appointments", null);
        if (Has("phiếu thu", "phiếu chi", "voucher")) return ("vouchers", null);
        if (Has("cơ hội", "booking ticket", "phiếu tư vấn", "lead")) return ("booking_tickets", null);
        if (Has("thông báo", "cần duyệt", "chờ duyệt")) return ("notifications", null);
        if (Has("công việc", "task", "đầu việc")) return ("tasks", null);
        if (Has("tour sắp", "sắp khởi hành", "sắp đi")) return ("departures", null);
        if (Has("tour")) return ("tours", null);
        if (Has("khách hàng", "danh sách kh")) return ("customers", null);
        // Doanh thu / lợi nhuận / chi phí (tháng này) ĐƠN GIẢN → cashflow (gọn: thu/chi/lợi nhuận), KHÔNG dump 12 KPI.
        if (Has("doanh thu", "doanh số", "chi phí", "lợi nhuận", "lãi", "tài chính"))
        {
            var start = new DateTime(now.Year, now.Month, 1);
            return ("cashflow", P(new { startDate = start.ToString("yyyy-MM-dd"), endDate = now.ToString("yyyy-MM-dd"), groupBy = "month" }));
        }
        return (null, null);
    }

    // ─── Resolver thị trường: tên → marketId ──────────────────────────────────────
    private async Task<List<(int Id, string Name)>> GetMarketsAsync(string sessionId, CancellationToken ct)
    {
        var tenant = _sessions.Get(sessionId)?.TenantId ?? "";
        if (_markets.TryGetValue(tenant, out var c) && c.Exp > DateTime.UtcNow) return c.List;

        JsonElement data;
        var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
        try { data = await _api.GetAsync(jwt, "/api/tours/markets", ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            jwt = await _sessions.ForceReloginAsync(sessionId, ct);
            data = await _api.GetAsync(jwt, "/api/tours/markets", ct);
        }

        var list = new List<(int, string)>();
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("id", out var idp) && idp.TryGetInt32(out var id)
                    && m.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String)
                    list.Add((id, np.GetString()!));

        _markets[tenant] = (list, DateTime.UtcNow.Add(MarketTtl));
        _log.LogInformation("Loaded {N} markets cho tenant {T}", list.Count, tenant);
        return list;
    }

    // Chuẩn hóa: thường hóa, bỏ dấu tiếng Việt, đ→d, bỏ dấu câu, gộp khoảng trắng.
    // "Nội địa - Miền Nam" và "Nội địa miền Nam" → cùng "noi dia mien nam".
    private static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int? MatchMarket(List<(int Id, string Name)> markets, string query)
    {
        var q = Norm(query);
        if (q.Length == 0) return null;
        foreach (var m in markets) if (Norm(m.Name) == q) return m.Id;                            // khớp tuyệt đối (đã chuẩn hóa)
        foreach (var m in markets) { var n = Norm(m.Name); if (n.Contains(q) || q.Contains(n)) return m.Id; }  // chứa nhau
        var qt = q.Split(' ');                                                                    // tất cả token query có trong tên
        foreach (var m in markets)
        {
            var mt = Norm(m.Name).Split(' ').ToHashSet();
            if (qt.Length > 1 && qt.All(t => mt.Contains(t))) return m.Id;
        }
        return null;
    }

    /// Nếu params có marketName → đổi sang marketId (bỏ marketName). Giữ nguyên các param khác.
    private async Task<JsonElement?> ResolveMarketAsync(string sessionId, JsonElement? prms, CancellationToken ct)
    {
        if (prms is not { ValueKind: JsonValueKind.Object } obj) return prms;
        if (!obj.TryGetProperty("marketName", out var mn) || mn.ValueKind != JsonValueKind.String) return prms;

        var name = mn.GetString()?.Trim() ?? "";
        var dict = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals("marketName", StringComparison.OrdinalIgnoreCase)) continue;  // bỏ field ảo
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        if (name.Length > 0 && !dict.ContainsKey("marketId"))
        {
            var markets = await GetMarketsAsync(sessionId, ct);
            var id = MatchMarket(markets, name);
            if (id.HasValue) { dict["marketId"] = id.Value; _log.LogInformation("[chat] thị trường '{Name}' → marketId={Id}", name, id); }
            else _log.LogInformation("[chat] không khớp thị trường '{Name}'", name);
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private string BuildPlannerPrompt(List<ChatTurn> history)
    {
        var today = DateTime.Now;
        var convo = new StringBuilder();
        foreach (var m in history.TakeLast(6))
            convo.Append(m.Role == "user" ? "Người dùng: " : "Trợ lý: ").Append(m.Content).Append('\n');

        return $@"HÔM NAY: {today:yyyy-MM-dd} (tháng {today.Month}, năm {today.Year}).

CÁC TOOL CÓ SẴN:
{ChatTools.CatalogForPrompt()}

HỘI THOẠI:
{convo}

QUY TẮC:
- Chọn 1 tool khớp nhất với câu hỏi cuối của người dùng.
- Điền params hợp lý. Ngày dạng yyyy-MM-dd. ""tháng này"" → startDate=đầu tháng, endDate=cuối tháng dựa HÔM NAY.
- NĂM:
   * ""năm nay"" / không nói gì về năm → dùng năm HÔM NAY.
   * ""năm 2025"" / ""2024"" / số 4 chữ rõ ràng → dùng đúng số đó.
   * ""năm ngoái"" / ""năm trước"" → năm HÔM NAY trừ 1.
   * ""cùng kỳ năm ngoái"" + có khoảng ngày → giữ tháng/ngày nhưng đổi năm = HÔM NAY trừ 1.
- ""doanh thu / lợi nhuận / chi phí (tháng/kỳ này)"" ĐƠN GIẢN → dùng cashflow (gọn: thu/chi/lợi nhuận + biểu đồ). CHỈ dùng financial_summary khi hỏi CHI TIẾT/ĐẦY ĐỦ chỉ số, hoặc hỏi đích danh thực thu / công nợ / thực chi / lợi nhuận ròng.
- Chỉ dùng key params có trong tool đã chọn.
- Lọc theo THỊ TRƯỜNG (vd ""Nội địa miền Nam"", ""Hàn Quốc"") → điền marketName = ĐÚNG tên người dùng nói (KHÔNG tự đoán id).
- Câu về ""khách hàng / lead / cơ hội THUỘC thị trường X"" → dùng list_booking_tickets (khách gắn thị trường qua cơ hội), KHÔNG dùng list_customers (không lọc được thị trường).
- Nếu chào hỏi / không cần số liệu → tool=""none"" kèm ""reply"" trả lời ngắn.

OUTPUT JSON:
{{ ""tool"": ""<tên tool hoặc none>"", ""params"": {{ }}, ""reply"": ""(chỉ khi tool=none)"" }}

Trả JSON ngay:";
    }

    private const string ANALYSIS_SYSTEM =
        "Bạn là chuyên viên phân tích kinh doanh cho công ty du lịch Tourkit. " +
        "Phân tích súc tích, thực dụng bằng tiếng Việt, văn phong chuyên nghiệp. " +
        "CHỈ dựa trên số liệu được cung cấp — TUYỆT ĐỐI không bịa số. " +
        "Dùng thuật ngữ tiếng Việt thuần (doanh thu, chi phí, lợi nhuận, khách hàng…); " +
        "KHÔNG dùng tên trường tiếng Anh (revenue, expense, kpiRevenue...) và KHÔNG nhắc tới Id. " +
        "Nêu nhận định chính + 1-2 đề xuất hành động nếu phù hợp. Không lặp lại nguyên bảng.";

    private string BuildAnalysisPrompt(List<ChatTurn> history, ChatTool tool, JsonElement data, List<ChatStat> stats)
    {
        var dataJson = data.GetRawText();
        if (dataJson.Length > 6000) dataJson = dataJson[..6000] + " …(cắt bớt)";

        var statsLine = stats.Count == 0 ? "(không có)" :
            string.Join("; ", stats.Select(s => $"{s.Label}={FmtNum(s.Value)}{s.Unit}"));

        // Truyền 6 lượt hội thoại gần nhất để AI bắt nhịp ngữ cảnh trước-sau
        // (vd user hỏi "so sánh với năm trước" tham chiếu câu hỏi trước đó).
        var convo = new StringBuilder();
        foreach (var m in history.TakeLast(6))
            convo.Append(m.Role == "user" ? "Người dùng: " : "Trợ lý: ").Append(m.Content).Append('\n');
        var question = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        return $@"HỘI THOẠI GẦN NHẤT:
{convo}
CÂU HỎI HIỆN TẠI: {question}

NGUỒN: {tool.Title} ({tool.Name})

SỐ LIỆU ĐÃ TÍNH: {statsLine}

DỮ LIỆU THÔ (JSON):
{dataJson}

Viết phân tích ngắn gọn (3-6 câu) trả lời câu hỏi HIỆN TẠI, bám đúng số liệu trên.
Nếu câu hỏi có ý ĐỐI CHIẾU với câu trước (vd 'so với năm ngoái', 'cao hơn không', 'theo chiều ngược lại') → so sánh tường minh với số liệu đã được nhắc trước đó.";
    }

    // ─── Stats tính server-side (deterministic) ───────────────────────────────────
    /// Tìm mảng "rows" trong data (data là array, hoặc object có 1 property mảng); tính:
    /// số bản ghi + tổng các cột tiền. Nếu data là object scalar → surface các số top-level.
    // Nhận diện chỉ số người dùng muốn từ câu hỏi (chi phí→expense, doanh thu→revenue, lợi nhuận→profit),
    // chỉ giữ chỉ số thực sự có trong dữ liệu trả về.
    // token tiếng Anh có trong tên field (kpiRevenue, expense, profit…) ↔ từ khóa tiếng Việt.
    private static readonly (string Token, string[] Kw)[] FocusTokens =
    {
        ("revenue", new[] { "doanh thu", "doanh số", "doanh so", "doanhthu", "revenue", "sales" }),
        ("expense", new[] { "chi phí", "chi phi", "chiphi", "expense", "cost" }),
        ("profit",  new[] { "lợi nhuận", "loi nhuan", "loinhuan", "profit", "lãi" }),
    };

    // Nhãn tiếng Việt cho các field hay gặp (đặt theo nghĩa trong app TourKit).
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kpiRevenue"] = "Doanh thu", ["kpiActualReceived"] = "Thực thu", ["kpiReceivable"] = "Công nợ phải thu",
        ["kpiOpportunities"] = "Giá trị cơ hội", ["kpiTotalExpense"] = "Tổng chi phí", ["kpiActualExpense"] = "Thực chi",
        ["kpiProviderDebt"] = "Công nợ NCC", ["kpiManagementCost"] = "Chi phí quản lý", ["kpiGrossProfit"] = "Lợi nhuận gộp",
        ["kpiActualProfit"] = "Lợi nhuận thực", ["kpiNetProfit"] = "Lợi nhuận ròng", ["kpiCommission"] = "Hoa hồng",
        ["totalTours"] = "Số tour", ["totalCustomers"] = "Số khách",
        ["revenue"] = "Doanh thu", ["expense"] = "Chi phí", ["profit"] = "Lợi nhuận",
        ["totalCount"] = "Tổng", ["newCount"] = "Mới", ["successCount"] = "Thành công", ["failCount"] = "Thất bại",
        ["totalPayment"] = "Tổng chi tiêu", ["totalTour"] = "Số tour", ["count"] = "Số lượng",
        ["totalRevenue"] = "Tổng chi tiêu", ["totalCustomers"] = "Số khách",
        ["actualRevenue"] = "Thực thu", ["totalExpense"] = "Tổng chi phí", ["actualExpense"] = "Thực chi",
        ["refund"] = "Hoàn tiền", ["pricePerSlot"] = "Giá/khách", ["available"] = "Còn chỗ", ["booked"] = "Đã đặt",
    };

    // Metadata phân trang — KHÔNG hiển thị thành thẻ số liệu.
    private static readonly HashSet<string> PageKeys = new(StringComparer.OrdinalIgnoreCase)
    { "pageIndex", "pageSize", "page", "pageNumber", "totalCount", "totalRow", "totalRows", "totalPage", "totalPages", "totalRecord", "totalRecords" };

    private static string Friendly(string key) => Labels.TryGetValue(key, out var v) ? v : key;

    private static List<string>? DetectFocus(string question, JsonElement data)
    {
        var q = (question ?? "").ToLowerInvariant();
        // "dòng tiền"/"tổng quan"/"tất cả" → muốn xem đầy đủ, không thu hẹp về 1 chỉ số.
        if (q.Contains("dòng tiền") || q.Contains("tổng quan") || q.Contains("tất cả") || q.Contains("toàn bộ"))
            return null;
        var matched = FocusTokens.Where(t => t.Kw.Any(k => q.Contains(k))).Select(t => t.Token).ToList();
        if (matched.Count == 0) return null;

        var fields = CollectFieldNames(data);
        var focus = fields.Where(f => matched.Any(tok => f.ToLowerInvariant().Contains(tok))).ToList();
        return focus.Count > 0 ? focus : null;
    }

    private static HashSet<string> CollectFieldNames(JsonElement data)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonElement rows = default; bool has = false;
        if (data.ValueKind == JsonValueKind.Array) { rows = data; has = true; }
        else if (data.ValueKind == JsonValueKind.Object)
            foreach (var p in data.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0
                    && p.Value[0].ValueKind == JsonValueKind.Object) { rows = p.Value; has = true; break; }
        if (has && rows.GetArrayLength() > 0 && rows[0].ValueKind == JsonValueKind.Object)
            foreach (var p in rows[0].EnumerateObject()) set.Add(p.Name);
        return set;
    }

    // Dữ liệu CRM đáng cache: không cache rỗng/undefined (tránh poison cache theo path khi staging trả rỗng/transient).
    private static bool IsUsableData(JsonElement d)
    {
        if (d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return false;
        if (d.ValueKind == JsonValueKind.Array) return true;
        if (d.ValueKind == JsonValueKind.Object)
            return d.TryGetProperty("items", out _) || d.EnumerateObject().Any();
        return false;
    }

    // Chỉ cache khi có nội dung thật (tránh cache kết quả rỗng do lỗi/transient → poison 30 phút).
    private static bool HasContent(ChatData? d)
        => d != null && (d.Stats.Count > 0
            || (d.Raw is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0));

    // Đọc envelope AI (/api/ai/*): items → bảng/chart (Raw), summary+total → thẻ số, title → tiêu đề.
    private ChatData BuildChatData(ChatTool tool, JsonElement data)
    {
        JsonElement items = data;
        string title = tool.Title;
        int total = 0;
        JsonElement? summary = null;

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("items", out var it)) items = it;
            if (data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString() ?? title;
            if (data.TryGetProperty("total", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tv)) total = tv;
            if (data.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Object) summary = sm;
        }

        var stats = BuildEnvelopeStats(tool, items, total, summary);
        var raw = items.ValueKind == JsonValueKind.Undefined ? data : items;
        return new ChatData(tool.Kind, title, raw.Clone(), stats, null, SuggestFor(tool.Name));
    }

    // Nhóm 1 chỉ số tài chính → doanh thu / chi phí / lợi nhuận (để panel gom nhóm rõ ràng).
    private static string FinGroup(string key, string label)
    {
        var s = (key + " " + label).ToLowerInvariant();
        if (s.Contains("lợi nhuận") || s.Contains("loi nhuan") || s.Contains("profit") || s.Contains("lãi")) return "profit";
        if (s.Contains("chi phí") || s.Contains("chi phi") || s.Contains("expense") || s.Contains("cost")
            || s.Contains("hoa hồng") || s.Contains("commission") || s.Contains("thực chi")
            || s.Contains("quản lý") || s.Contains("management") || s.Contains("nợ ncc") || s.Contains("providerdebt")) return "expense";
        if (s.Contains("doanh thu") || s.Contains("revenue") || s.Contains("thực thu") || s.Contains("received")
            || s.Contains("phải thu") || s.Contains("receivable") || s.Contains("cơ hội") || s.Contains("opportun")) return "revenue";
        return "other";
    }

    // Tag gợi ý "xem gì tiếp theo" theo tool — chip bấm là hỏi luôn.
    private static List<string>? SuggestFor(string toolName) => toolName switch
    {
        "financial_summary" => new() { "Dòng tiền 12 tháng gần đây", "Top khách hàng tháng này", "Hiệu quả marketing", "Công nợ phải thu" },
        "cashflow"          => new() { "Chi tiết tài chính tháng này", "Top khách hàng", "Top seller doanh số" },
        "top_customers"     => new() { "Doanh thu tháng này", "Khách chưa chăm sóc", "Lịch hẹn CSKH" },
        "top_sellers"       => new() { "Doanh thu tháng này", "Top khách hàng" },
        "marketing"         => new() { "Top khách hàng", "Doanh thu tháng này" },
        "departures"        => new() { "Tour sắp khởi hành còn chỗ", "Top khách hàng" },
        "tours"             => new() { "Tour sắp khởi hành", "Doanh thu tháng này" },
        "customers"         => new() { "Top khách hàng", "Khách sinh nhật tháng này" },
        "booking_tickets"   => new() { "Top khách hàng", "Lịch hẹn CSKH" },
        _                   => null
    };

    private static List<ChatStat> BuildEnvelopeStats(ChatTool tool, JsonElement items, int total, JsonElement? summary)
    {
        var stats = new List<ChatStat>();

        // financial-summary: items CHÍNH LÀ 12 chỉ số tài chính → thẻ số, NHÓM theo doanh thu/chi phí/lợi nhuận.
        if (tool.Name == "financial_summary" && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in items.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var key = GetStr(m, "key") ?? "";
                var label = GetStr(m, "label") ?? key;
                if (m.TryGetProperty("value", out var v) && TryNum(v, out var n))
                {
                    var f = GetStr(m, "formatted") ?? "";
                    stats.Add(new ChatStat(label, n, f.Contains('đ') ? "đ" : null, FinGroup(key, label)));
                }
            }
            return stats;
        }

        if (total > 0) stats.Add(new ChatStat("Tổng số", total, null));

        if (summary is { ValueKind: JsonValueKind.Object } sm)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in sm.EnumerateObject()) names.Add(p.Name);
            foreach (var p in sm.EnumerateObject())
            {
                var n = p.Name;
                // Bỏ field hiển thị/twin (*Formatted/*Name/*Label) và field code có twin (vd tourType↔tourTypeLabel).
                if (n.EndsWith("Formatted", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith("Name", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith("Label", StringComparison.OrdinalIgnoreCase)) continue;
                if (names.Contains(n + "Label") || names.Contains(n + "Name")) continue;
                if (!TryNum(p.Value, out var val)) continue;
                // Tiền nếu có twin *Formatted (server format kèm đ); ngược lại là số đếm.
                var unit = names.Contains(n + "Formatted") ? "đ" : null;
                stats.Add(new ChatStat(Friendly(n), val, unit));
            }
        }

        return stats;
    }

    private static List<ChatStat> ComputeStats(JsonElement data, List<string>? focus = null)
    {
        var stats = new List<ChatStat>();
        if (data.ValueKind == JsonValueKind.Undefined || data.ValueKind == JsonValueKind.Null)
            return stats;

        // 1) Mảng thuần → dataset ĐẦY ĐỦ (vd cashflow, marketing) → đếm + tổng tiền hợp lệ.
        if (data.ValueKind == JsonValueKind.Array)
        {
            AddRowStats(stats, data, complete: true, total: data.GetArrayLength(), focus);
            return stats;
        }

        if (data.ValueKind != JsonValueKind.Object) return stats;

        // 2) Object: tìm mảng bản ghi (list có phân trang, vd items[]).
        JsonElement rows = default; bool hasRows = false; int? total = null;
        foreach (var p in data.EnumerateObject())
        {
            if (!hasRows && p.Value.ValueKind == JsonValueKind.Array
                && p.Value.GetArrayLength() > 0 && p.Value[0].ValueKind == JsonValueKind.Object)
            { rows = p.Value; hasRows = true; }
            if ((p.Name.Equals("totalRow", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Equals("totalCount", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Equals("totalRecord", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Equals("totalRecords", StringComparison.OrdinalIgnoreCase))
                && p.Value.TryGetInt32(out var t)) total = t;
        }

        if (hasRows)
        {
            // List có phân trang → KHÔNG cộng tổng tiền (chỉ là 1 trang → dễ sai). Chỉ hiện tổng số.
            AddRowStats(stats, rows, complete: false, total: total ?? rows.GetArrayLength(), focus);
            return stats;
        }

        // 3) Object KPI thuần (không có mảng items) → surface số top-level, BỎ metadata phân trang.
        foreach (var p in data.EnumerateObject())
        {
            if (!TryNum(p.Value, out var n) || PageKeys.Contains(p.Name)) continue;
            if (focus != null && !focus.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            stats.Add(new ChatStat(Friendly(p.Name), n, IsMoney(p.Name) ? "đ" : null));
        }
        if (focus == null && stats.Count > 12) stats = stats.Take(12).ToList();
        return stats;
    }

    private static void AddRowStats(List<ChatStat> stats, JsonElement rows, bool complete, int total, List<string>? focus)
    {
        stats.Add(new ChatStat(complete ? "Số bản ghi" : "Tổng số", total, null));
        if (!complete) return;   // list phân trang → bỏ qua tổng tiền (tránh tổng thiếu/sai lệch)
        if (rows.GetArrayLength() == 0 || rows[0].ValueKind != JsonValueKind.Object) return;

        var moneyKeys = rows[0].EnumerateObject()
            .Where(p => IsMoney(p.Name) && TryNum(p.Value, out _))
            .Select(p => p.Name)
            .Where(k => focus == null || focus.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Distinct().Take(3).ToList();

        foreach (var key in moneyKeys)
        {
            double sum = 0;
            foreach (var r in rows.EnumerateArray())
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) && TryNum(v, out var n))
                    sum += n;
            stats.Add(new ChatStat($"Tổng {Friendly(key)}", sum, "đ"));
        }
    }

    private static readonly string[] MoneyHints =
    {
        "doanhthu", "revenue", "tongtien", "thanhtien", "thanhtoan", "amount", "money",
        "gia", "price", "tien", "commission", "hoahong", "loinhuan", "profit",
        "congno", "debt", "paid", "total", "tong", "payment", "value",
        "expense", "cost", "chiphi"
    };

    private static readonly string[] NotMoney = { "count", "qty", "row", "soluong", "index", "page", "year", "month", "stt" };

    private static bool IsMoney(string key)
    {
        var k = key.ToLowerInvariant();
        if (NotMoney.Any(n => k.Contains(n))) return false;
        return MoneyHints.Any(h => k.Contains(h));
    }

    private static bool TryNum(JsonElement el, out double n)
    {
        n = 0;
        if (el.ValueKind == JsonValueKind.Number) { n = el.GetDouble(); return true; }
        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n)) return true;
        return false;
    }

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
           ? p.GetString() : null;

    private static string FmtNum(double n) => n.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// TTL ngắn (3 phút) cho query realtime (tháng hiện tại — data đang cập nhật liên tục).
    /// TTL dài (15 phút) cho query tháng cũ/quý cố định (data không đổi).
    /// KHÔNG dùng prefix năm ("2026") vì match cả startDate=2026-01-01 (tháng cố định).
    private static TimeSpan ChooseTtl(JsonElement? prms)
    {
        if (prms == null || prms.Value.ValueKind != JsonValueKind.Object) return TimeSpan.FromMinutes(5);
        var today = DateTime.Now;
        foreach (var p in prms.Value.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var v = p.Value.GetString() ?? "";
            // Chỉ check tháng hiện tại (yyyy-MM) — tháng cũ trong cùng năm là cố định, không cần TTL ngắn.
            if (v.StartsWith($"{today:yyyy-MM}"))
                return TimeSpan.FromMinutes(3);
        }
        return TimeSpan.FromMinutes(15);
    }
}
