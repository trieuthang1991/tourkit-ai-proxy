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
    public string Description => "Tự AI-chấm deal mới chưa chấm + cảnh báo deal nguội (đẩy mail vào hàng đợi)";
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

        int reviewed = 0, rereviewed = 0, autoFinalized = 0, finalizedSkipped = 0, cappedSkipped = 0;
        int coolingCount = 0, queued = 0, skipped = 0, skippedNoAssignee = 0;

        try
        {
            // ── Pass 1: REVIEW DEAL MỚI (chưa chấm) ──────────────────────────────
            if (opt.AutoReview)
            {
                var page = await _client.ListPagedAsync(sessionId, 1, opt.ReviewMax, ct, rank: -1, startDate: startDate);
                foreach (var deal in page.Items.Where(InStatuses))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
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

                // ── Pass 2: REVIEW LẠI (đọc deal đã chấm) ────────────────────────
                var scored = await _client.ListPagedAsync(sessionId, 1, 200, ct, rank: 1 /*sentinel: đã chấm bất kỳ*/);
                foreach (var deal in scored.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var meta = _dealRepo.GetReviewControl(tenantId, deal.Id);
                        if (meta?.IsFinalized == true) { finalizedSkipped++; continue; }
                        if (!Eligible(deal))
                        {
                            _dealRepo.SetFinalized(tenantId, deal.Id, InStatuses(deal) ? "aged" : "status-changed");
                            autoFinalized++; continue;
                        }
                        if (meta != null && meta.AutoReviewCount >= opt.MaxAutoReviews) { cappedSkipped++; continue; }
                        var ctx = await _client.GetContextAsync(sessionId, deal, ct);
                        if (_dealRepo.GetScore(tenantId, deal.Id, ctx.Fingerprint) != null) continue;   // nội dung chưa đổi
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

            // ── Cảnh báo nguội → enqueue mail ────────────────────────────────────
            var page2 = await _client.ListPagedAsync(sessionId, 1, 200, ct, startDate: startDate);
            var cooling = page2.Items
                .Where(d => d.Status != CancelStatus && !IsClosedWon(d.StatusName) && InStatuses(d))
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

                await _mailQueue.EnqueueAsync(new OutboundMailInput(
                    TenantId: tenantId,
                    Kind: AlertKind,
                    SourceId: $"Deal_{deal.Id}",
                    Username: null,                     // worker chọn hộp thư tenant
                    TemplateCode: AlertKind,
                    ToEmail: null, ToName: null, ToUserId: null,   // worker resolve NV phụ trách từ DealId
                    Cc: null,
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
            return new WorkflowRunResult(false, null, "Vượt quá thời gian 5 phút");
        }
        catch (QuotaExhaustedException)
        {
            return new WorkflowRunResult(false, null, "Hết quota AI");
        }
        catch (Exception ex)
        {
            _log.LogWarning("[DealAutoReview] tenant={T} lỗi: {Err}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null, ex.Message);
        }

        var summary = JsonSerializer.Serialize(new
        {
            reviewed, rereviewed, autoFinalized, finalizedSkipped, cappedSkipped,
            cooling = coolingCount, queued, skipped, skippedNoAssignee
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
            ["coolingDays"] = deal.CoolingDays,
            ["lastInteractionAt"] = deal.LastInteractionAt,
            ["hasReview"] = score != null,
            ["winRate"] = score?.WinRate,
            ["level"] = score?.Level,
            ["nextAction"] = score?.NextAction,
        };
        return JsonSerializer.Serialize(p);
    }

    private static string FmtVnd(long v) => v.ToString("#,##0", CultureInfo.InvariantCulture) + " đ";

    /// Deal đã CHỐT/thành công (không còn là cơ hội mở) — mirror DealOpportunityClient.ListOpenAsync.
    private static bool IsClosedWon(string? statusName)
    {
        var sn = DealHeuristic.Normalize(statusName);
        return sn.Length > 0 && (sn.Contains("chot don") || sn.Contains("thanh cong")
            || sn.Contains("hoan thanh") || sn.Contains("hoan tat") || sn.Contains("da ban"));
    }
}

/// Option ĐỘNG của deal-auto-review (parse từ OptionsJson, mặc định an toàn).
public sealed record DealAutoReviewOptions(
    List<int> Statuses, int CreatedWithinDays, bool AutoReview, int ReviewMax, int MaxAutoReviews,
    int CoolingDays, int MinWinRateToNotify, int MaxNotifications, int NotifyMinGapHours)
{
    public static DealAutoReviewOptions Parse(string? json)
    {
        var def = new DealAutoReviewOptions(
            Statuses: new List<int>(), CreatedWithinDays: 30, AutoReview: true, ReviewMax: 20,
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
