using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Deals;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Workflow "Tự động review & cảnh báo deal" (PerTenant). Mỗi chu kỳ:
///   Pass 1: AI-chấm deal MỚI chưa chấm (rank=-1) trong cửa sổ statuses + createdWithinDays.
///   Pass 2: duyệt deal ĐÃ chấm (ScoreDeals) → còn đủ điều kiện thì review lại (khi nội dung đổi),
///           hết điều kiện (status đổi khỏi list / quá hạn ngày) → đánh cờ IsFinalized để lần sau bỏ qua.
///   Cảnh báo nguội: deal đang mở + nguội ≥ ngưỡng → enqueue 1 mail (template + params) vào dbo.OutboundMails.
///
/// Auth: SERVICE ACCOUNT per-tenant (dbo.TenantServiceAccounts) → tự login, KHÔNG cần user online.
/// Quota+Log: bọc AI call bằng AiCallContext.Push(AiFeatures.DealAutoReview) → trừ quota tenant + log đúng tên.
/// KHÔNG gửi email — chỉ enqueue; worker riêng (CEO viết) render template + resolve NV phụ trách + gửi.
/// </summary>
public class DealAutoReviewWorkflow : IScheduledWorkflow
{
    private const string AlertKind = "deal-cooling-alert";
    private const int MaxAlertsPerRun = 200;   // chốt chặn: 1 run enqueue tối đa 200 alert nguội (phòng runaway
                                               // khi tenant test có hàng nghìn deal nguội). Còn lại chờ run sau.
    private const int ContextBatchSize = 50;   // upstream /api/ai/booking-tickets/context cap 50 id/call
    private const int AiConcurrency = 10;      // song song 10 AI call/chunk (mirror DealBatchService + Customer workflow).
                                               // Serial 30 deal × 8s = 4min → parallel 10 = 24s.

    private readonly DealOpportunityClient _client;
    private readonly DealScoringService _scoring;
    private readonly DealRepository _dealRepo;
    private readonly MailQueueRepository _mailQueue;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly TkSessionStore _sessions;
    private readonly AiCallContext _aiCtx;
    private readonly RedisStore _redis;
    private readonly ILogger<DealAutoReviewWorkflow> _log;

    public DealAutoReviewWorkflow(
        DealOpportunityClient client, DealScoringService scoring, DealRepository dealRepo,
        MailQueueRepository mailQueue, TenantServiceAccountStore serviceAccounts,
        TkSessionStore sessions, AiCallContext aiCtx, RedisStore redis, ILogger<DealAutoReviewWorkflow> log)
    {
        _client = client; _scoring = scoring; _dealRepo = dealRepo; _mailQueue = mailQueue;
        _serviceAccounts = serviceAccounts; _sessions = sessions; _aiCtx = aiCtx;
        _redis = redis; _log = log;
    }

    public string Type => "deal-auto-review";
    public string Label => "Tự động review & cảnh báo deal";
    public string Description => "Tự động chấm điểm deal và cảnh báo những deal đang nguội.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        // GUARD: tenantId rỗng = sai cấu hình workflow → lộ ra sớm thay vì chạy nhầm cross-tenant.
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _log.LogWarning("[DealAutoReview] DỪNG: tenantId rỗng (kiểm tra dbo.UserWorkflows)");
            return new WorkflowRunResult(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");
        }

        // DISTRIBUTED LOCK: chặn 2 instance (web + worker) chạy SONG SONG cùng (tenant, workflow).
        // Redis SET NX EX (TTL 5min = workflow timeout). Redis down → fail-closed (skip, an toàn).
        var lockKey = $"workflow-lock:deal-auto-review:{tenantId}";
        if (!_redis.SetIfNotExists(lockKey, Environment.MachineName + ":" + Environment.ProcessId, TimeSpan.FromMinutes(5)))
        {
            _log.LogWarning("[DealAutoReview] tenant={T} SKIP: instance khác đang chạy (hoặc Redis lỗi)", tenantId);
            return new WorkflowRunResult(false, null, "Instance khác đang chạy workflow này (hoặc Redis không sẵn sàng), bỏ qua lần này");
        }

        try
        {
        var swTotal = Stopwatch.StartNew();
        var opt = DealAutoReviewOptions.Parse(optionsJson);
        _log.LogInformation("[DealAutoReview] tenant={T} START — statuses=[{Statuses}] createdWithin={CW}d autoReview={AR} reReview={RR} reviewMax={Max} coolingDays={CD} minWinRate={WR} maxNotif={MN}",
            tenantId,
            opt.Statuses.Count == 0 ? "any" : string.Join(",", opt.Statuses),
            opt.CreatedWithinDays, opt.AutoReview, opt.ReReview, opt.ReviewMax,
            opt.CoolingDays, opt.MinWinRateToNotify, opt.MaxNotifications);

        // Service account: bắt buộc cấu hình + bật.
        var svc = _serviceAccounts.Get(tenantId);
        if (svc == null || !svc.Enabled)
        {
            _log.LogWarning("[DealAutoReview] tenant={T} DỪNG: chưa cấu hình tài khoản tự động (svc={Svc} enabled={En})",
                tenantId, svc == null ? "null" : svc.Username, svc?.Enabled);
            return new WorkflowRunResult(false, null, "Chưa cấu hình tài khoản tự động cho tenant (POST /api/v1/workflows/service-account)");
        }

        string sessionId;
        var swLogin = Stopwatch.StartNew();
        try
        {
            sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DealAutoReview] tenant={T} LOGIN FAIL user={U}: {Err}",
                tenantId, svc.Username, ex.Message);
            return new WorkflowRunResult(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }
        swLogin.Stop();
        _log.LogInformation("[DealAutoReview] tenant={T} login OK user={U} sessionId={Sid} ({Ms}ms)",
            tenantId, svc.Username, sessionId, swLogin.ElapsedMilliseconds);

        // QUOTA + LOG: bọc TOÀN BỘ phần gọi AI để trừ quota tenant + log feature="deal-auto-review".
        using var _aiScope = _aiCtx.Push(AiFeatures.DealAutoReview, tenantId, sessionId);

        var startDate = DateTime.UtcNow.Date.AddDays(-opt.CreatedWithinDays).ToString("yyyy-MM-dd");
        bool InStatuses(DealOpportunity d) => opt.Statuses.Count == 0 || opt.Statuses.Contains(d.Status);
        bool Eligible(DealOpportunity d) => InStatuses(d) && d.AgeDays <= opt.CreatedWithinDays;

        // Fetch deals — lọc trạng thái SERVER-SIDE trong 1 REQUEST (upstream `statusesCsv` → IN (...)),
        // tránh gọi mỗi status 1 lần. statuses rỗng → không truyền (mọi trạng thái).
        // Lọc client-side InStatuses chỉ là LỚP AN TOÀN (no-op khi upstream đã lọc; cứu khi upstream cũ chưa deploy).
        var statusesCsv = opt.Statuses.Count > 0 ? string.Join(",", opt.Statuses) : null;
        async Task<List<DealOpportunity>> FetchAsync(int? rank, string? sd, int pageSize)
        {
            var items = (await _client.ListPagedAsync(sessionId, 1, pageSize, ct, rank: rank, startDate: sd, statusesCsv: statusesCsv)).Items;
            return opt.Statuses.Count == 0 ? items : items.Where(InStatuses).ToList();
        }

        int reviewed = 0, rereviewed = 0, autoFinalized = 0, finalizedSkipped = 0, cappedSkipped = 0;
        int coolingCount = 0, queued = 0, skipped = 0, skippedNoAssignee = 0;
        bool quotaHit = false, timedOut = false;

        try
        {
            // ── Pass 1: CHẤM DEAL MỚI (chưa chấm) — batch context ──────────────────
            if (opt.AutoReview)
            {
                var swP1 = Stopwatch.StartNew();
                var newDeals = await FetchAsync(rank: -1, sd: startDate, pageSize: Math.Max(opt.ReviewMax, 50));
                var newRows = _dealRepo.GetBulk(tenantId, newDeals.Select(d => d.Id));
                // Lọc: bỏ deal đã chốt/hủy + deal đã có fingerprint trong DB (race)
                var toReviewIds = newDeals
                    .Where(d => !DealCooling.IsClosedWon(d.StatusName) && d.Status != DealCooling.CancelStatus)
                    .Where(d => !newRows.ContainsKey(d.Id))   // chưa có row → chưa chấm (fingerprint check sau khi có context)
                    .Take(opt.ReviewMax)
                    .Select(d => d.Id)
                    .ToList();
                _log.LogInformation("[DealAutoReview] tenant={T} PASS1 list {N} deal mới, bulk score {M} rows → {K} sẽ chấm",
                    tenantId, newDeals.Count, newRows.Count, toReviewIds.Count);

                foreach (var chunk in Chunk(toReviewIds, ContextBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    if (reviewed + rereviewed >= opt.ReviewMax) break;
                    var swCh = Stopwatch.StartNew();
                    var contexts = await _client.GetContextsAsync(sessionId, chunk, ct);
                    swCh.Stop();
                    _log.LogInformation("[DealAutoReview] tenant={T} PASS1 context batch {N} deal ({Ms}ms) — parallel {P} AI call",
                        tenantId, contexts.Count, swCh.ElapsedMilliseconds, AiConcurrency);

                    // PARALLEL 10 AI call/chunk — song song thay vì serial. Counter Interlocked.
                    // Cap reviewMax: check trước khi vào loop; trong loop nếu vượt → skip nhẹ (không cắt sớm — chấp nhận vài
                    // task lỡ tay run over budget, không đáng kể).
                    await Parallel.ForEachAsync(contexts,
                        new ParallelOptions { MaxDegreeOfParallelism = AiConcurrency, CancellationToken = ct },
                        async (dwc, innerCt) =>
                        {
                            if (Volatile.Read(ref reviewed) + Volatile.Read(ref rereviewed) >= opt.ReviewMax) return;
                            try
                            {
                                var deal = dwc.Deal;
                                var ctx = dwc.Context;
                                var score = await _scoring.ScoreAsync(ctx.Profile, null, null, null, innerCt);
                                _dealRepo.SaveScore(tenantId, deal.Id, ctx.Fingerprint, score);
                                _dealRepo.MarkAutoReviewed(tenantId, deal.Id);
                                Interlocked.Increment(ref reviewed);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (QuotaExhaustedException) { throw; }
                            catch (Exception ex) { _log.LogWarning("[DealAutoReview] tenant={T} PASS1 chấm deal {Id} lỗi: {Err}", tenantId, dwc.Deal.Id, ex.Message); }
                        });
                }
                swP1.Stop();
                _log.LogInformation("[DealAutoReview] tenant={T} PASS1 done ({Ms}ms) → reviewed={R}",
                    tenantId, swP1.ElapsedMilliseconds, reviewed);

                // ── Pass 2: DB-driven re-review (query dbo.DealScores đến hạn) ──────
                if (opt.ReReview)
                {
                    var swP2 = Stopwatch.StartNew();
                    // Cutoff: đại khái reReview khi mấy ngày = createdWithinDays / 2 (mượn tương tự Customer reReviewDays).
                    // Deal option cũ chưa có reReviewDays riêng → dùng createdWithinDays/3 làm mặc định (10 ngày với default 30).
                    var reReviewDays = Math.Max(1, opt.CreatedWithinDays / 3);
                    var cutoffMs = new DateTimeOffset(DateTime.UtcNow.AddDays(-reReviewDays)).ToUnixTimeMilliseconds();
                    var remainingBudget = Math.Max(0, opt.ReviewMax - (reviewed + rereviewed));
                    var dueIds = _dealRepo.GetDueForReReview(tenantId, cutoffMs, remainingBudget > 0 ? remainingBudget : 1);
                    var dueRows = _dealRepo.GetBulk(tenantId, dueIds);
                    _log.LogInformation("[DealAutoReview] tenant={T} PASS2 DB due IDs = {N} (reReview>={D}d, budget={B}) + bulk rows {M}",
                        tenantId, dueIds.Count, reReviewDays, remainingBudget, dueRows.Count);

                    foreach (var chunk in Chunk(dueIds, ContextBatchSize))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (reviewed + rereviewed >= opt.ReviewMax) break;
                        var swCh = Stopwatch.StartNew();
                        var contexts = await _client.GetContextsAsync(sessionId, chunk, ct);
                        swCh.Stop();
                        _log.LogInformation("[DealAutoReview] tenant={T} PASS2 context batch {N} deal ({Ms}ms) — parallel {P} AI call",
                            tenantId, contexts.Count, swCh.ElapsedMilliseconds, AiConcurrency);

                        // PARALLEL 10 AI call/chunk (skip đơn nhanh vẫn dùng path cũ, chỉ AI call mới song song).
                        // SetFinalized + SaveScore + MarkAutoReviewed đều thread-safe từ Dapper (mỗi call mở connection riêng).
                        await Parallel.ForEachAsync(contexts,
                            new ParallelOptions { MaxDegreeOfParallelism = AiConcurrency, CancellationToken = ct },
                            async (dwc, innerCt) =>
                            {
                                if (Volatile.Read(ref reviewed) + Volatile.Read(ref rereviewed) >= opt.ReviewMax) return;
                                try
                                {
                                    var deal = dwc.Deal;
                                    var ctx = dwc.Context;
                                    var row = dueRows.TryGetValue(deal.Id, out var r) ? r : null;
                                    if (row?.IsFinalized == true) { Interlocked.Increment(ref finalizedSkipped); return; }
                                    // AUTO review: đơn đã CHỐT/hủy → không còn cơ hội mở → chốt sổ, NGỪNG tự review lại.
                                    if (DealCooling.IsClosedWon(deal.StatusName) || deal.Status == DealCooling.CancelStatus)
                                    {
                                        _dealRepo.SetFinalized(tenantId, deal.Id, "closed");
                                        Interlocked.Increment(ref autoFinalized); return;
                                    }
                                    if (!Eligible(deal))
                                    {
                                        _dealRepo.SetFinalized(tenantId, deal.Id, InStatuses(deal) ? "aged" : "status-changed");
                                        Interlocked.Increment(ref autoFinalized); return;
                                    }
                                    if (row != null && row.AutoReviewCount >= opt.MaxAutoReviews) { Interlocked.Increment(ref cappedSkipped); return; }
                                    // Lớp dự phòng: fingerprint trùng = nội dung thực sự chưa đổi → skip AI.
                                    if (row != null && row.Fingerprint == ctx.Fingerprint) return;
                                    var score = await _scoring.ScoreAsync(ctx.Profile, null, null, null, innerCt);
                                    _dealRepo.SaveScore(tenantId, deal.Id, ctx.Fingerprint, score);
                                    _dealRepo.MarkAutoReviewed(tenantId, deal.Id);
                                    Interlocked.Increment(ref rereviewed);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (QuotaExhaustedException) { throw; }
                                catch (Exception ex) { _log.LogWarning("[DealAutoReview] tenant={T} PASS2 re-review deal {Id} lỗi: {Err}", tenantId, dwc.Deal.Id, ex.Message); }
                            });
                    }
                    swP2.Stop();
                    _log.LogInformation("[DealAutoReview] tenant={T} PASS2 done ({Ms}ms) → rereviewed={RR} autoFinalized={AF} finalizedSkipped={FS} cappedSkipped={CS}",
                        tenantId, swP2.ElapsedMilliseconds, rereviewed, autoFinalized, finalizedSkipped, cappedSkipped);
                }
            }

            // ── Cảnh báo nguội → enqueue mail (status lọc server-side qua FetchAsync) ──
            var swCool = Stopwatch.StartNew();
            var openDeals = await FetchAsync(rank: null, sd: startDate, pageSize: 200);
            var cooling = openDeals
                .Where(d => DealCooling.IsCooling(d.Status, d.StatusName, d.CoolingDays, opt.CoolingDays, opt.CoolingStatuses))
                .ToList();
            coolingCount = cooling.Count;

            // BULK pre-fetch cho cooling loop: 1 SELECT thay N × (GetReviewControl + PeekCached).
            var coolingRows = _dealRepo.GetBulk(tenantId, cooling.Select(d => d.Id));
            _log.LogInformation("[DealAutoReview] tenant={T} COOLING fetch {N} deal mở + {M} deal nguội (>= {D}d) + bulk {K} rows",
                tenantId, openDeals.Count, cooling.Count, opt.CoolingDays, coolingRows.Count);
            foreach (var deal in cooling)
            {
                ct.ThrowIfCancellationRequested();
                if (queued >= MaxAlertsPerRun)   // chốt chặn runaway: dừng enqueue khi chạm cap/run
                {
                    _log.LogWarning("[DealAutoReview] tenant={T} COOLING chạm cap {Cap} alert/run — dừng enqueue, phần còn lại chờ run sau", tenantId, MaxAlertsPerRun);
                    break;
                }
                if (string.IsNullOrWhiteSpace(deal.Assignees)) { skippedNoAssignee++; continue; }   // chưa giao NV → worker không resolve được người nhận
                var row = coolingRows.TryGetValue(deal.Id, out var r) ? r : null;
                if (row?.IsFinalized == true) { skipped++; continue; }                               // đã chốt sổ → không nhắc

                var score = row?.ToScore();
                if (opt.MinWinRateToNotify > 0 && (score?.WinRate ?? 0) < opt.MinWinRateToNotify) { skipped++; continue; }

                var (recent, last) = await _mailQueue.CountRecentBySourceAsync(tenantId, AlertKind, $"Deal_{deal.Id}", 24 * 30, ct);
                if (recent >= opt.MaxNotifications) { skipped++; continue; }
                if (last.HasValue && (DateTime.UtcNow - last.Value).TotalHours < opt.NotifyMinGapHours) { skipped++; continue; }

                // NV phụ trách có thể nhiều người → email ĐẦU làm người nhận chính (To), còn lại vào Cc.
                var (toEmail, ccEmails) = SplitRecipients(deal.AssigneeEmail);

                await _mailQueue.EnqueueAsync(new OutboundMailInput(
                    TenantId: tenantId,
                    Kind: AlertKind,
                    SourceId: $"Deal_{deal.Id}",
                    Username: null,                     // worker chọn hộp thư tenant
                    TemplateCode: AlertKind,
                    // Producer CHỦ ĐỘNG truyền sẵn email + tên NV phụ trách → worker chỉ gửi, KHÔNG tra DB.
                    // toEmail null nếu upstream /api/ai/booking-tickets chưa trả 'assigneeEmail' → worker đánh Skipped (thiếu email).
                    ToEmail: toEmail, ToName: deal.Assignees, ToUserId: null,
                    Cc: ccEmails,
                    Subject: null,                      // template tự quyết subject
                    Params: BuildAlertParams(deal, score),
                    Data: JsonSerializer.Serialize(new
                    {
                        dealId = deal.Id, code = deal.Code, customerName = deal.CustomerName,
                        assigneeNames = deal.Assignees, coolingDays = deal.CoolingDays,
                        winRate = score?.WinRate, nextAction = score?.NextAction
                    })), ct);
                queued++;
            }
            swCool.Stop();
            _log.LogInformation("[DealAutoReview] tenant={T} COOLING done ({Ms}ms) → queued={Q} skipped={S} skippedNoAssignee={SN}",
                tenantId, swCool.ElapsedMilliseconds, queued, skipped, skippedNoAssignee);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;   // hết 5 phút → DỪNG ÊM, giữ phần đã chấm/cảnh báo (không fail); chu kỳ sau chạy tiếp
            _log.LogWarning("[DealAutoReview] tenant={T} TIMEOUT 5min — dừng êm, giữ phần đã làm", tenantId);
        }
        catch (QuotaExhaustedException)
        {
            quotaHit = true;   // hết quota → DỪNG êm (không fail/auto-pause)
            _log.LogWarning("[DealAutoReview] tenant={T} HẾT QUOTA — dừng êm, không fail", tenantId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[DealAutoReview] tenant={T} LỖI KHÔNG XỬ LÝ ĐƯỢC: {Err}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null, ex.Message);
        }

        swTotal.Stop();
        var summary = JsonSerializer.Serialize(new
        {
            reviewed, rereviewed, autoFinalized, finalizedSkipped, cappedSkipped,
            cooling = coolingCount, queued, skipped, skippedNoAssignee, quotaHit, timedOut,
            durationMs = swTotal.ElapsedMilliseconds
        });
        _log.LogInformation("[DealAutoReview] tenant={T} FINISH ({Ms}ms) → reviewed={R} rereviewed={RR} autoFinalized={AF} cooling={C} queued={Q} skipped={S} quotaHit={QH} timedOut={TO}",
            tenantId, swTotal.ElapsedMilliseconds, reviewed, rereviewed, autoFinalized, coolingCount, queued, skipped, quotaHit, timedOut);
        return new WorkflowRunResult(true, summary, null);
        }
        finally
        {
            // Distributed lock release — luôn chạy (kể cả return sớm do exception).
            _redis.Delete(lockKey);
        }
    }

    /// Build JSON tham số cho template HTML (worker replace). Key cố định (versioned theo TemplateCode).
    private static string BuildAlertParams(DealOpportunity deal, DealScore? score)
    {
        var p = new Dictionary<string, object?>
        {
            ["dealId"] = deal.Id,
            ["dealCode"] = deal.Code,
            ["customerName"] = deal.CustomerName,
            ["phone"] = deal.Phone,
            ["title"] = deal.Title,
            ["totalPriceFormatted"] = FmtVnd(deal.TotalPrice),
            ["statusName"] = deal.StatusName,
            ["sourceName"] = deal.SourceName,
            ["assigneeNames"] = deal.Assignees,
            ["fullName"] = deal.Assignees,        // tên người nhận để worker/template dùng (producer truyền sẵn)
            ["coolingDays"] = deal.CoolingDays,
            ["lastInteractionAt"] = FmtDate(deal.LastInteractionAt),
            ["hasReview"] = score != null,
            ["winRate"] = score?.WinRate,
            ["level"] = score?.Level,
            ["nextAction"] = score?.NextAction,
        };
        return JsonSerializer.Serialize(p);
    }

    /// Format ngày ISO ("2026-06-12T09:59:50.857") → "dd/MM/yyyy HH:mm" cho dễ đọc trong email.
    /// Giữ wall-clock (không đổi TZ) vì chỉ để hiển thị; parse fail → trả nguyên chuỗi.
    private static string? FmtDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return iso;
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : iso;
    }

    /// Tách chuỗi nhiều email NV (ngăn bởi , ; /) → (email chính, danh sách Cc dạng "a,b"). Loại trùng,
    /// giữ thứ tự. Email đầu = To, còn lại = Cc. Rỗng/null → (null, null).
    private static (string? To, string? Cc) SplitRecipients(string? emails)
    {
        if (string.IsNullOrWhiteSpace(emails)) return (null, null);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var e in emails.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(e)) list.Add(e);
        if (list.Count == 0) return (null, null);
        var cc = list.Count > 1 ? string.Join(",", list.Skip(1)) : null;
        return (list[0], cc);
    }

    private static string FmtVnd(long v) => v.ToString("#,##0", CultureInfo.InvariantCulture) + " đ";

    /// Chia list thành batch <see cref="ContextBatchSize"/> phần tử — upstream cap 50 id/context call.
    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

}

/// Option ĐỘNG của deal-auto-review (parse từ OptionsJson, mặc định an toàn).
public sealed record DealAutoReviewOptions(
    List<int> Statuses, int CreatedWithinDays, bool AutoReview, bool ReReview, int ReviewMax, int MaxAutoReviews,
    int CoolingDays, int MinWinRateToNotify, int MaxNotifications, int NotifyMinGapHours,
    List<int> CoolingStatuses)
{
    public static DealAutoReviewOptions Parse(string? json)
    {
        var def = new DealAutoReviewOptions(
            Statuses: new List<int>(), CreatedWithinDays: 30, AutoReview: true, ReReview: true, ReviewMax: 20,
            MaxAutoReviews: 5, CoolingDays: 7, MinWinRateToNotify: 0, MaxNotifications: 3, NotifyMinGapHours: 24,
            CoolingStatuses: new List<int>());
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            var statuses = new List<int>();
            if (r.TryGetProperty("statuses", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) statuses.Add(n);
            var coolingStatuses = new List<int>();
            if (r.TryGetProperty("coolingStatuses", out var carr) && carr.ValueKind == JsonValueKind.Array)
                foreach (var e in carr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var cn) && cn > 0) coolingStatuses.Add(cn);
            return new DealAutoReviewOptions(
                Statuses: statuses,
                CreatedWithinDays: Clamp(GetInt(r, "createdWithinDays", 30), 1, 365),
                AutoReview: GetBool(r, "autoReview", true),
                ReReview: GetBool(r, "reReview", true),
                ReviewMax: Clamp(GetInt(r, "reviewMax", 20), 1, 100),
                MaxAutoReviews: Clamp(GetInt(r, "maxAutoReviews", 5), 1, 50),
                CoolingDays: Clamp(GetInt(r, "coolingDays", 7), 1, 90),
                MinWinRateToNotify: Clamp(GetInt(r, "minWinRateToNotify", 0), 0, 100),
                MaxNotifications: Clamp(GetInt(r, "maxNotifications", 3), 1, 20),
                NotifyMinGapHours: Clamp(GetInt(r, "notifyMinGapHours", 24), 1, 720),
                CoolingStatuses: coolingStatuses);
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
