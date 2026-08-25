using System.Diagnostics;
using System.Text.Json;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Workflow "Tự động review khách hàng" (PerTenant) — 2-FLOW PATTERN (2026-07 refactor):
///   Pass 1 (KH MỚI chưa review): upstream lọc `rank=-1&startDate=UtcNow-createdWithinDays`
///                                → chỉ KH tạo trong cửa sổ + chưa review; fetch context batch → review.
///   Pass 2 (KH ĐẾN HẠN review lại): DB proxy query `dbo.Reviews WHERE GeneratedAt < cutoff`
///                                   → biết chính xác KH nào due; fetch context batch → check fingerprint → review.
///
/// Cả 2 flow đều dùng <see cref="TourKitCustomerSource.GetContextsAsync"/> — SAME endpoint
/// `/api/ai/customers/context` mà page endpoint + batch service dùng → fingerprint đồng nhất,
/// không re-review nhầm do shape lệch.
///
/// Auth = service account per-tenant. Quota+Log: <c>AiCallContext.Push(AiFeatures.CustomerAutoReview)</c>.
/// </summary>
public class CustomerAutoReviewWorkflow : IScheduledWorkflow
{
    private const int MaxPerRun = 200;      // cap KH xử lý/lượt (mỗi pass) → run không quá lâu
    private const int ListPageSize = 50;    // upstream list page (rank=-1 filter)
    private const int ContextBatchSize = 50;// upstream context endpoint cap 50 id/call
    private const int AiConcurrency = 10;   // song song 10 AI call/chunk — mirror DealBatchService pattern.
                                            // 200 KH × 8s serial ~27min → parallel 10 ~2.7min → xong 1 lượt.

    private readonly TourKitCustomerSource _source;
    private readonly ReviewService _reviewService;
    private readonly ReviewRepository _reviewRepo;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly TkSessionStore _sessions;
    private readonly AiCallContext _aiCtx;
    private readonly RedisStore _redis;
    private readonly ILogger<CustomerAutoReviewWorkflow> _log;

    public CustomerAutoReviewWorkflow(
        TourKitCustomerSource source, ReviewService reviewService, ReviewRepository reviewRepo,
        TenantServiceAccountStore serviceAccounts, TkSessionStore sessions,
        AiCallContext aiCtx, RedisStore redis, ILogger<CustomerAutoReviewWorkflow> log)
    {
        _source = source; _reviewService = reviewService; _reviewRepo = reviewRepo;
        _serviceAccounts = serviceAccounts; _sessions = sessions; _aiCtx = aiCtx;
        _redis = redis; _log = log;
    }

    public string Type => "customer-auto-review";
    public string Label => "Tự động review khách hàng";
    public string Description => "Tự động chấm hạng khách hàng (A–D) và định kỳ chấm lại.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        // GUARD: tenantId rỗng = sai cấu hình workflow → lộ ra sớm thay vì chạy nhầm cross-tenant.
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _log.LogWarning("[CustomerAutoReview] DỪNG: tenantId rỗng (kiểm tra dbo.UserWorkflows)");
            return new WorkflowRunResult(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");
        }

        // DISTRIBUTED LOCK: chặn 2 instance (web + worker) chạy SONG SONG cùng (tenant, workflow) → double AI quota.
        // Redis SET NX EX (TTL 5min = workflow timeout). Redis down → fail-closed (skip lần này, an toàn).
        // Cross-instance work; in-process cũng OK (nhiều tick chồng chéo).
        var lockKey = $"workflow-lock:customer-auto-review:{tenantId}";
        if (!_redis.SetIfNotExists(lockKey, Environment.MachineName + ":" + Environment.ProcessId, TimeSpan.FromMinutes(5)))
        {
            _log.LogWarning("[CustomerAutoReview] tenant={T} SKIP: instance khác đang chạy (hoặc Redis lỗi)", tenantId);
            return new WorkflowRunResult(false, null, "Instance khác đang chạy workflow này (hoặc Redis không sẵn sàng), bỏ qua lần này");
        }

        try
        {
        var swTotal = Stopwatch.StartNew();
        var opt = CustomerAutoReviewOptions.Parse(optionsJson);
        _log.LogInformation("[CustomerAutoReview] tenant={T} START — createdWithin={CW}d, reReview={RR}, reReviewDays={RRD}d, reviewMax={Max}",
            tenantId, opt.CreatedWithinDays, opt.ReReview, opt.ReReviewDays, opt.ReviewMax);

        var svc = _serviceAccounts.Get(tenantId);
        if (svc == null || !svc.Enabled)
        {
            _log.LogWarning("[CustomerAutoReview] tenant={T} DỪNG: chưa cấu hình tài khoản tự động (svc={Svc} enabled={En})",
                tenantId, svc == null ? "null" : svc.Username, svc?.Enabled);
            return new WorkflowRunResult(false, null, "Chưa cấu hình tài khoản tự động cho tenant (POST /api/v1/workflows/service-account)");
        }

        string sessionId;
        var swLogin = Stopwatch.StartNew();
        try { sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CustomerAutoReview] tenant={T} LOGIN FAIL user={U}: {Err}", tenantId, svc.Username, ex.Message);
            return new WorkflowRunResult(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }
        swLogin.Stop();
        _log.LogInformation("[CustomerAutoReview] tenant={T} login OK user={U} sessionId={Sid} ({Ms}ms)",
            tenantId, svc.Username, sessionId, swLogin.ElapsedMilliseconds);

        using var _aiScope = _aiCtx.Push(AiFeatures.CustomerAutoReview, tenantId, sessionId);

        int reviewed = 0, rereviewed = 0, skippedUnchanged = 0, skippedAlreadyReviewed = 0;
        bool quotaHit = false, timedOut = false;
        var now = DateTime.UtcNow;
        try
        {
            // ══════════ PASS 1: KH MỚI chưa review ══════════
            var swP1 = Stopwatch.StartNew();
            var startDate = now.Date.AddDays(-opt.CreatedWithinDays).ToString("yyyy-MM-dd");
            var newIds = new List<string>();
            for (int page = 1; page <= (MaxPerRun / ListPageSize); page++)
            {
                var listPage = await _source.ListAsync(sessionId,
                    new TourKitCustomerSource.CustomerFilter(Rank: -1, StartDate: startDate),
                    page, ListPageSize, ct);
                if (listPage.Items.Count == 0) break;
                newIds.AddRange(listPage.Items.Select(c => c.Id));
                if (listPage.Items.Count < ListPageSize) break;   // trang cuối
                if (newIds.Count >= MaxPerRun) break;
            }
            _log.LogInformation("[CustomerAutoReview] tenant={T} PASS1 fetch {N} KH chưa review (rank=-1 startDate={SD})",
                tenantId, newIds.Count, startDate);

            if (newIds.Count > 0)
            {
                // Dedup edge race: KH có thể đã được review giữa fetch list và chấm — bulk check.
                var existingP1 = _reviewRepo.GetBulkSlim(tenantId, newIds);

                foreach (var chunk in Chunk(newIds, ContextBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    if (reviewed + rereviewed >= opt.ReviewMax) break;   // cap tổng AI/run — chờ chu kỳ sau
                    var swChunk = Stopwatch.StartNew();
                    var contexts = await _source.GetContextsAsync(sessionId, chunk, ct);
                    swChunk.Stop();
                    _log.LogInformation("[CustomerAutoReview] tenant={T} PASS1 context batch {N} KH ({Ms}ms) — parallel {P} AI call",
                        tenantId, contexts.Count, swChunk.ElapsedMilliseconds, AiConcurrency);

                    // PARALLEL: 10 AI call/chunk cùng lúc — mirror DealBatchService. 200 KH × 8s = 27min serial → 2.7min parallel.
                    // Counter dùng Interlocked (nhiều thread cùng ++). QuotaExhaustedException throw ra ngoài để dừng workflow gọn.
                    // Cap reviewMax: Volatile.Read đọc counter tươi; chấp nhận 1-9 task lỡ tay vượt cap (không đáng kể).
                    await Parallel.ForEachAsync(contexts,
                        new ParallelOptions { MaxDegreeOfParallelism = AiConcurrency, CancellationToken = ct },
                        async (customer, innerCt) =>
                        {
                            if (Volatile.Read(ref reviewed) + Volatile.Read(ref rereviewed) >= opt.ReviewMax) return;
                            try
                            {
                                if (existingP1.ContainsKey(customer.Id)) { Interlocked.Increment(ref skippedAlreadyReviewed); return; }
                                await _reviewService.ReviewAsync(customer, tenantId, ct: innerCt);
                                Interlocked.Increment(ref reviewed);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (QuotaExhaustedException) { throw; }
                            catch (Exception ex) { _log.LogWarning("[CustomerAutoReview] tenant={T} PASS1 KH {Id} lỗi: {Err}", tenantId, customer.Id, ex.Message); }
                        });
                }
            }
            swP1.Stop();
            _log.LogInformation("[CustomerAutoReview] tenant={T} PASS1 done ({Ms}ms) → reviewed={R} skippedAlreadyReviewed={S}",
                tenantId, swP1.ElapsedMilliseconds, reviewed, skippedAlreadyReviewed);

            // ══════════ PASS 2: KH ĐẾN HẠN review lại (DB-driven) ══════════
            if (opt.ReReview)
            {
                var swP2 = Stopwatch.StartNew();
                var cutoffMs = new DateTimeOffset(now.AddDays(-opt.ReReviewDays)).ToUnixTimeMilliseconds();
                var dueIds = _reviewRepo.GetDueForReReview(tenantId, cutoffMs, MaxPerRun);
                _log.LogInformation("[CustomerAutoReview] tenant={T} PASS2 DB due IDs = {N} (cutoff={CO})",
                    tenantId, dueIds.Count, opt.ReReviewDays);

                if (dueIds.Count > 0)
                {
                    // Cần fingerprint hiện tại để so với context mới → dedup unchanged customer.
                    var existingP2 = _reviewRepo.GetBulkSlim(tenantId, dueIds);

                    foreach (var chunk in Chunk(dueIds, ContextBatchSize))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (reviewed + rereviewed >= opt.ReviewMax) break;   // cap tổng AI/run — chờ chu kỳ sau
                        var swChunk = Stopwatch.StartNew();
                        var contexts = await _source.GetContextsAsync(sessionId, chunk, ct);
                        swChunk.Stop();
                        _log.LogInformation("[CustomerAutoReview] tenant={T} PASS2 context batch {N} KH ({Ms}ms) — parallel {P} AI call",
                            tenantId, contexts.Count, swChunk.ElapsedMilliseconds, AiConcurrency);

                        // PARALLEL 10 AI call/chunk — cùng pattern PASS 1.
                        await Parallel.ForEachAsync(contexts,
                            new ParallelOptions { MaxDegreeOfParallelism = AiConcurrency, CancellationToken = ct },
                            async (customer, innerCt) =>
                            {
                                if (Volatile.Read(ref reviewed) + Volatile.Read(ref rereviewed) >= opt.ReviewMax) return;
                                try
                                {
                                    var existing = existingP2.TryGetValue(customer.Id, out var e) ? e : null;
                                    if (existing == null) return;   // race: bị xoá giữa 2 query → bỏ qua
                                    if (ReviewRepository.FingerprintFor(customer) == existing.Fingerprint)
                                    { Interlocked.Increment(ref skippedUnchanged); return; }
                                    await _reviewService.ReviewAsync(customer, tenantId, ct: innerCt);
                                    Interlocked.Increment(ref rereviewed);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (QuotaExhaustedException) { throw; }
                                catch (Exception ex) { _log.LogWarning("[CustomerAutoReview] tenant={T} PASS2 KH {Id} lỗi: {Err}", tenantId, customer.Id, ex.Message); }
                            });
                    }
                }
                swP2.Stop();
                _log.LogInformation("[CustomerAutoReview] tenant={T} PASS2 done ({Ms}ms) → rereviewed={RR} skippedUnchanged={SU}",
                    tenantId, swP2.ElapsedMilliseconds, rereviewed, skippedUnchanged);
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = true;   // hết 5 phút → DỪNG ÊM, giữ phần đã review (không fail); chu kỳ sau chạy tiếp
            _log.LogWarning("[CustomerAutoReview] tenant={T} TIMEOUT 5min — dừng êm, giữ phần đã làm", tenantId);
        }
        catch (QuotaExhaustedException)
        {
            quotaHit = true;   // hết quota → DỪNG êm (không fail/auto-pause)
            _log.LogWarning("[CustomerAutoReview] tenant={T} HẾT QUOTA — dừng êm, không fail", tenantId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CustomerAutoReview] tenant={T} LỖI KHÔNG XỬ LÝ ĐƯỢC: {Err}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null, ex.Message);
        }

        swTotal.Stop();
        var summary = JsonSerializer.Serialize(new
        {
            reviewed, rereviewed, skippedUnchanged, skippedAlreadyReviewed,
            quotaHit, timedOut, durationMs = swTotal.ElapsedMilliseconds
        });
        _log.LogInformation("[CustomerAutoReview] tenant={T} FINISH ({Ms}ms) → reviewed={R} rereviewed={RR} skipUnchanged={SU} skipAlreadyReviewed={SAR} quotaHit={Q} timedOut={TO}",
            tenantId, swTotal.ElapsedMilliseconds, reviewed, rereviewed, skippedUnchanged, skippedAlreadyReviewed, quotaHit, timedOut);
        return new WorkflowRunResult(true, summary, null);
        }
        finally
        {
            // Distributed lock release — luôn chạy (kể cả return sớm do exception).
            // Redis lỗi khi Delete → TTL 5min tự expire (an toàn, chỉ chậm chu kỳ kế nếu run < 5min).
            _redis.Delete(lockKey);
        }
    }

    /// Chia list thành các batch <see cref="ContextBatchSize"/> phần tử — upstream cap 50 id/context call.
    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}

/// Option ĐỘNG của customer-auto-review.
/// <c>ReviewMax</c>: cap tổng AI call/lượt (Pass 1 + Pass 2). Default 200 = MaxPerRun (fetch upstream) →
/// workflow xử hết KH trong 1 lượt. User có thể chỉnh xuống qua UI nếu muốn tiết kiệm quota AI.
public sealed record CustomerAutoReviewOptions(int CreatedWithinDays, bool ReReview, int ReReviewDays, int ReviewMax)
{
    public static CustomerAutoReviewOptions Parse(string? json)
    {
        var def = new CustomerAutoReviewOptions(CreatedWithinDays: 30, ReReview: true, ReReviewDays: 30, ReviewMax: 200);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            return new CustomerAutoReviewOptions(
                CreatedWithinDays: Clamp(GetInt(r, "createdWithinDays", 30), 1, 365),
                ReReview:          GetBool(r, "reReview", true),
                ReReviewDays:      Clamp(GetInt(r, "reReviewDays", 30), 1, 365),
                ReviewMax:         Clamp(GetInt(r, "reviewMax", 200), 1, 500));
        }
        catch { return def; }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    private static int GetInt(JsonElement r, string k, int def)
    {
        if (!r.TryGetProperty(k, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return def;
    }
    private static bool GetBool(JsonElement r, string k, bool def)
    {
        if (!r.TryGetProperty(k, out var v)) return def;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        return def;
    }
}
