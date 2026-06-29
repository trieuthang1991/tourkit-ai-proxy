using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
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
/// Quota+Log: bọc AI call bằng AiCallContext.Push("deal-auto-review") → trừ quota tenant + log đúng tên.
/// KHÔNG gửi email — chỉ enqueue; worker riêng (CEO viết) render template + resolve NV phụ trách + gửi.
/// </summary>
public class DealAutoReviewWorkflow : IScheduledWorkflow
{
    private const int CancelStatus = 5;   // TourKit BookingTicketStatus: 5 = Hủy
    private const string AlertKind = "deal-cooling-alert";

    private readonly DealOpportunityClient _client;
    private readonly DealScoringService _scoring;
    private readonly DealRepository _dealRepo;
    private readonly MailQueueRepository _mailQueue;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly TkSessionStore _sessions;
    private readonly AiCallContext _aiCtx;
    private readonly ILogger<DealAutoReviewWorkflow> _log;

    public DealAutoReviewWorkflow(
        DealOpportunityClient client, DealScoringService scoring, DealRepository dealRepo,
        MailQueueRepository mailQueue, TenantServiceAccountStore serviceAccounts,
        TkSessionStore sessions, AiCallContext aiCtx, ILogger<DealAutoReviewWorkflow> log)
    {
        _client = client; _scoring = scoring; _dealRepo = dealRepo; _mailQueue = mailQueue;
        _serviceAccounts = serviceAccounts; _sessions = sessions; _aiCtx = aiCtx; _log = log;
    }

    public string Type => "deal-auto-review";
    public string Label => "Tự động review & cảnh báo deal";
    public string Description => "Tự động chấm điểm deal và cảnh báo những deal đang nguội.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var opt = DealAutoReviewOptions.Parse(optionsJson);

        // Service account: bắt buộc cấu hình + bật.
        var svc = _serviceAccounts.Get(tenantId);
        if (svc == null || !svc.Enabled)
            return new WorkflowRunResult(false, null, "Chưa cấu hình tài khoản tự động cho tenant (POST /api/v1/workflows/service-account)");

        string sessionId;
        try
        {
            sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct);
        }
        catch (Exception ex)
        {
            return new WorkflowRunResult(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }

        // QUOTA + LOG: bọc TOÀN BỘ phần gọi AI để trừ quota tenant + log feature="deal-auto-review".
        using var _aiScope = _aiCtx.Push("deal-auto-review", tenantId, sessionId);

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
            // ── Pass 1: REVIEW DEAL MỚI (chưa chấm) ──────────────────────────────
            if (opt.AutoReview)
            {
                var newDeals = await FetchAsync(rank: -1, sd: startDate, pageSize: Math.Max(opt.ReviewMax, 50));
                foreach (var deal in newDeals)
                {
                    ct.ThrowIfCancellationRequested();
                    if (reviewed + rereviewed >= opt.ReviewMax) break;   // cap tổng lượt AI/run (quota)
                    try
                    {
                        if (IsClosedWon(deal.StatusName) || deal.Status == CancelStatus) continue;   // auto: đơn đã chốt/hủy → không chấm
                        var ctx = await _client.GetContextAsync(sessionId, deal, ct);
                        if (_dealRepo.GetScore(tenantId, deal.Id, ctx.Fingerprint) != null) continue;   // đã chấm fingerprint này
                        var score = await _scoring.ScoreAsync(ctx.Profile, null, null, null, ct);
                        _dealRepo.SaveScore(tenantId, deal.Id, ctx.Fingerprint, score);
                        _dealRepo.MarkAutoReviewed(tenantId, deal.Id);
                        reviewed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (QuotaExhaustedException) { throw; }
                    catch (Exception ex) { _log.LogWarning("[DealAutoReview] chấm deal {Id} lỗi: {Err}", deal.Id, ex.Message); }
                }

                // ── Pass 2: REVIEW LẠI (đọc deal đã chấm) — chỉ khi BẬT review lại ──
                var scoredDeals = opt.ReReview
                    ? await FetchAsync(rank: 1 /*sentinel: đã chấm bất kỳ*/, sd: null, pageSize: 200)
                    : new List<DealOpportunity>();
                foreach (var deal in scoredDeals)
                {
                    ct.ThrowIfCancellationRequested();
                    if (reviewed + rereviewed >= opt.ReviewMax) break;   // cap tổng lượt AI/run (quota)
                    try
                    {
                        var meta = _dealRepo.GetReviewControl(tenantId, deal.Id);
                        if (meta?.IsFinalized == true) { finalizedSkipped++; continue; }
                        // AUTO review: đơn đã CHỐT/thành công/đã hủy → không còn cơ hội mở → chốt sổ, NGỪNG tự review lại.
                        // (Chỉ chặn auto; user chủ động review tay đi path khác, KHÔNG bị ảnh hưởng.)
                        if (IsClosedWon(deal.StatusName) || deal.Status == CancelStatus)
                        {
                            _dealRepo.SetFinalized(tenantId, deal.Id, "closed");
                            autoFinalized++; continue;
                        }
                        if (!Eligible(deal))
                        {
                            _dealRepo.SetFinalized(tenantId, deal.Id, InStatuses(deal) ? "aged" : "status-changed");
                            autoFinalized++; continue;
                        }
                        if (meta != null && meta.AutoReviewCount >= opt.MaxAutoReviews) { cappedSkipped++; continue; }
                        // SO TIMESTAMP (code, không AI, không fetch): deal có hoạt động mới kể từ lần chấm cuối chưa?
                        // LastInteractionAt = MAX(ngày tạo, sửa, comment mới nhất). ≤ ngày chấm cuối → không đổi → bỏ qua.
                        var cached = _dealRepo.PeekCached(tenantId, deal.Id);
                        var lastReviewUtc = ParseUtc(cached?.SavedAt);
                        var lastInteract = ParseUtc(deal.LastInteractionAt);
                        if (lastReviewUtc != null && lastInteract != null && lastInteract.Value <= lastReviewUtc.Value)
                            continue;   // không hoạt động mới → bỏ qua, KHÔNG fetch detail/comment
                        var ctx = await _client.GetContextAsync(sessionId, deal, ct);
                        if (_dealRepo.GetScore(tenantId, deal.Id, ctx.Fingerprint) != null) continue;   // lớp dự phòng: fingerprint trùng = nội dung thực sự chưa đổi
                        var score = await _scoring.ScoreAsync(ctx.Profile, null, null, null, ct);
                        _dealRepo.SaveScore(tenantId, deal.Id, ctx.Fingerprint, score);
                        _dealRepo.MarkAutoReviewed(tenantId, deal.Id);
                        rereviewed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (QuotaExhaustedException) { throw; }
                    catch (Exception ex) { _log.LogWarning("[DealAutoReview] re-review deal {Id} lỗi: {Err}", deal.Id, ex.Message); }
                }
            }

            // ── Cảnh báo nguội → enqueue mail (status lọc server-side qua FetchAsync) ──
            var openDeals = await FetchAsync(rank: null, sd: startDate, pageSize: 200);
            var cooling = openDeals
                .Where(d => d.Status != CancelStatus && !IsClosedWon(d.StatusName))
                .Where(d => d.IsCooling && d.CoolingDays >= opt.CoolingDays)
                .ToList();
            coolingCount = cooling.Count;

            foreach (var deal in cooling)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(deal.Assignees)) { skippedNoAssignee++; continue; }   // chưa giao NV → worker không resolve được người nhận
                var meta = _dealRepo.GetReviewControl(tenantId, deal.Id);
                if (meta?.IsFinalized == true) { skipped++; continue; }                               // đã chốt sổ → không nhắc

                var score = _dealRepo.PeekCached(tenantId, deal.Id)?.Score;
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
        }
        catch (OperationCanceledException)
        {
            timedOut = true;   // hết 5 phút → DỪNG ÊM, giữ phần đã chấm/cảnh báo (không fail); chu kỳ sau chạy tiếp
        }
        catch (QuotaExhaustedException)
        {
            quotaHit = true;   // hết quota → DỪNG êm (không fail/auto-pause)
        }
        catch (Exception ex)
        {
            _log.LogWarning("[DealAutoReview] tenant={T} lỗi: {Err}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null, ex.Message);
        }

        var summary = JsonSerializer.Serialize(new
        {
            reviewed, rereviewed, autoFinalized, finalizedSkipped, cappedSkipped,
            cooling = coolingCount, queued, skipped, skippedNoAssignee, quotaHit, timedOut
        });
        _log.LogInformation("[DealAutoReview] tenant={T} → reviewed={R} rereviewed={RR} autoFinalized={AF} cooling={C} queued={Q} skipped={S}",
            tenantId, reviewed, rereviewed, autoFinalized, coolingCount, queued, skipped);
        return new WorkflowRunResult(true, summary, null);
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

    private static DateTime? ParseUtc(string? iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : (DateTime?)null;

    /// Deal đã CHỐT/thành công (không còn là cơ hội mở) — mirror DealOpportunityClient.ListOpenAsync.
    private static bool IsClosedWon(string? statusName)
    {
        var sn = DealHeuristic.Normalize(statusName);
        // "da chot" bắt cả "Đã chốt" / "Đã chốt đơn"; KHÔNG match nhầm "chưa chốt"/"sắp chốt" (chua/sap chot ≠ da chot).
        return sn.Length > 0 && (sn.Contains("chot don") || sn.Contains("da chot") || sn.Contains("thanh cong")
            || sn.Contains("hoan thanh") || sn.Contains("hoan tat") || sn.Contains("da ban"));
    }
}

/// Option ĐỘNG của deal-auto-review (parse từ OptionsJson, mặc định an toàn).
public sealed record DealAutoReviewOptions(
    List<int> Statuses, int CreatedWithinDays, bool AutoReview, bool ReReview, int ReviewMax, int MaxAutoReviews,
    int CoolingDays, int MinWinRateToNotify, int MaxNotifications, int NotifyMinGapHours)
{
    public static DealAutoReviewOptions Parse(string? json)
    {
        var def = new DealAutoReviewOptions(
            Statuses: new List<int>(), CreatedWithinDays: 30, AutoReview: true, ReReview: true, ReviewMax: 20,
            MaxAutoReviews: 5, CoolingDays: 7, MinWinRateToNotify: 0, MaxNotifications: 3, NotifyMinGapHours: 24);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            var statuses = new List<int>();
            if (r.TryGetProperty("statuses", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) statuses.Add(n);
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
                NotifyMinGapHours: Clamp(GetInt(r, "notifyMinGapHours", 24), 1, 720));
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
