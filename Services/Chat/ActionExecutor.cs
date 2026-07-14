using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;              // ActionExecuteRequest, ActionResult, ActionChoice, ChatData, MailItem, MailDraft
using TourkitAiProxy.Services.Crm;        // CrmActionQueueRepository, CrmActionInput, CrmActionKind
using TourkitAiProxy.Services.Deals;      // DealOpportunityClient, DealScoringService, DealRepository
using TourkitAiProxy.Services.Mail;       // MailSyncService, IMailSender, MailRepository, MailAccountStore, MailTaxonomy
using TourkitAiProxy.Services.Reviews;    // ReviewService
using TourkitAiProxy.Services.TourKit;    // TourKitCustomerSource

namespace TourkitAiProxy.Services.Chat;

/// Thực thi 1 hành động đã xác nhận. Định tuyến theo ActionKind (tra ActionTools.Find).
/// Task 8a: nhánh CrmQueue (assign_task / create_appointment). Task 8b: nhánh Internal
/// (review_customer / score_deal) — reuse NGUYÊN ReviewService/DealScoringService, KHÔNG
/// reimplement prompt/parse. Task 8c: nhánh Mail (check_mail / send_mail_reply / compose_mail)
/// — reuse NGUYÊN MailSyncService/IMailSender/MailRepository/MailAccountStore, KHÔNG reimplement
/// IMAP/SMTP. Soạn nháp reply/compose (AI) là việc của proposal phase (task khác) — ở đây chỉ GỬI
/// text đã có sẵn trong params.
public class ActionExecutor
{
    private readonly CrmActionQueueRepository _crmQueue;
    private readonly ActionResolver _resolver;
    private readonly TourKitCustomerSource _customerSource;
    private readonly ReviewService _reviewService;
    private readonly DealOpportunityClient _dealClient;
    private readonly DealScoringService _dealScoring;
    private readonly DealRepository _dealRepo;
    private readonly MailSyncService _mailSync;
    private readonly IMailSender _mailSender;
    private readonly MailRepository _mailRepo;
    private readonly MailAccountStore _mailAccount;
    private readonly AiCallContext _aiCtx;
    private readonly ILogger<ActionExecutor> _log;

    /// Message chuẩn khi tenant/user chưa cấu hình hộp thư Gmail — mirror GmailImapClient để UX nhất quán.
    private const string MailNotConfiguredMessage =
        "Chưa cấu hình hộp thư Gmail. Nhập địa chỉ + App Password (16 ký tự) ở phần Cấu hình hộp thư.";

    /// Cap mặc định/tối đa khi sync cho check_mail — mirror MailEndpoints.SyncMaxDefault/SyncMaxAbsolute.
    private const int MailSyncDefaultLimit = 100;
    private const int MailSyncMaxLimit = 500;

    /// Chống thực thi trùng khi user bấm "Xác nhận" 2 lần / SSE retry gửi lại cùng actionId.
    /// Chỉ cache khi ENQUEUE/GỬI thành công — kết quả "không tìm thấy/mơ hồ" KHÔNG cache để user
    /// sửa tên rồi thử lại với cùng actionId vẫn re-resolve được. review_customer/score_deal/check_mail
    /// KHÔNG dùng cache này — chấm/kiểm tra lại luôn cho info tươi, không phải "gửi" cần dedup.
    private static readonly ConcurrentDictionary<string, ActionResult> _done = new();

    public ActionExecutor(
        CrmActionQueueRepository crmQueue, ActionResolver resolver,
        TourKitCustomerSource customerSource, ReviewService reviewService,
        DealOpportunityClient dealClient, DealScoringService dealScoring, DealRepository dealRepo,
        MailSyncService mailSync, IMailSender mailSender, MailRepository mailRepo, MailAccountStore mailAccount,
        AiCallContext aiCtx, ILogger<ActionExecutor> log)
    {
        _crmQueue = crmQueue; _resolver = resolver;
        _customerSource = customerSource; _reviewService = reviewService;
        _dealClient = dealClient; _dealScoring = dealScoring; _dealRepo = dealRepo;
        _mailSync = mailSync; _mailSender = mailSender; _mailRepo = mailRepo; _mailAccount = mailAccount;
        _aiCtx = aiCtx; _log = log;
    }

    // ─── Pure payload builders (test được — xem ActionExecutorTests) ─────────────

    /// Dựng PayloadJson khớp CreateOrUpdateTaskingRequest cho assign-task.
    /// LƯU Ý: workflowId nullable — proxy KHÔNG resolve workflow (không có endpoint list ổn định);
    /// truyền workflowName THÔ để worker app-side tự resolve/đặt default.
    public static string BuildAssignTaskPayload(
        int? workflowId, string? workflowName, string name, string? content, string staffsInChargeCsv,
        int prioritized, DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        int? bookingTicketId)
        => JsonSerializer.Serialize(new
        {
            id = 0, workflowId, workflowName, name, content,
            staffsInCharge = staffsInChargeCsv,
            prioritized, status = 1,
            startDate = startUtc, endDate = endUtc,
            appointmentReminder = reminderMinutes,
            bookingTicketId
        });

    /// Dựng PayloadJson khớp CreateCustomerCareRequest cho create-appointment.
    public static string BuildAppointmentPayload(
        int customerId, string careTitle, string? careDetail,
        DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        string? customerName, string? customerPhone, int? bookingTicketId,
        int? insUid, int typeSchedule)
        => JsonSerializer.Serialize(new
        {
            customerId, careTitle, careDetail,
            careStartTime = startUtc, careEndTime = endUtc,
            status = 1,                       // 1 = Tạo mới (CustomerCareStatus) — lịch hẹn mới
            typeSchedule,                     // 0=Lịch hẹn 1=Lịch tour 2=Nhắc thanh toán 4=Hạn thanh toán
            appointmentReminder = reminderMinutes,
            insUid,                           // người phụ trách (InsUid — single user id), null = worker tự đặt
            bookingTicketId, customerName, customerPhone
        });

    /// Map "cao|tb|thap" → Prioritized (0..3).
    public static int MapPriority(string? p) => (p ?? "").Trim().ToLowerInvariant() switch
    {
        "cao" or "high" => 1,
        "tb" or "trung binh" or "trung bình" or "medium" => 2,
        "thap" or "thấp" or "low" => 3,
        _ => 0
    };

    // ─── Execute ───────────────────────────────────────────────────────────────

    /// Thực thi hành động đã xác nhận. Trả ActionResult. Idempotent theo ActionId.
    public async Task<ActionResult> ExecuteAsync(
        ActionExecuteRequest req, string tenantId, string jwt, string username,
        string? sessionId, CancellationToken ct)
    {
        var tool = ActionTools.Find(req.Action)
            ?? throw new InvalidOperationException($"Unknown action: {req.Action}");

        switch (tool.Kind)
        {
            case ActionKind.CrmQueue:
                return await ExecuteCrmQueueAsync(req, tenantId, username, jwt, ct);
            case ActionKind.Internal:
                return await ExecuteInternalAsync(req, tenantId, jwt, sessionId, ct);
            case ActionKind.Mail:
                return await ExecuteMailAsync(req, tenantId, username, sessionId, ct);
            default:
                throw new InvalidOperationException($"Unhandled kind {tool.Kind}");
        }
    }

    // ─── Internal (review_customer / score_deal) ──────────────────────────────────

    private async Task<ActionResult> ExecuteInternalAsync(
        ActionExecuteRequest req, string tenantId, string jwt, string? sessionId, CancellationToken ct)
        => req.Action.ToLowerInvariant() switch
        {
            "review_customer" => await ExecuteReviewCustomerAsync(req, tenantId, jwt, sessionId, ct),
            "score_deal"      => await ExecuteScoreDealAsync(req, tenantId, jwt, sessionId, ct),
            _ => throw new InvalidOperationException($"Unhandled Internal action: {req.Action}")
        };

    /// review_customer: resolve KH (id trực tiếp hoặc tên → id qua ActionResolver) → fetch context
    /// đầy đủ (Purchases/CareLogs — NGUỒN DUY NHẤT dùng chung page/batch/workflow) → ReviewService
    /// (dual-path native-tool/json-prompt, tự save DB) → gói CustomerReview vào ChatData.Raw cho FE
    /// render lại y hệt <see cref="Models.ChatModels"/> customer-review-card.
    private async Task<ActionResult> ExecuteReviewCustomerAsync(
        ActionExecuteRequest req, string tenantId, string jwt, string? sessionId, CancellationToken ct)
    {
        // TourKitCustomerSource cần sessionId (tự resolve/refresh JWT bên trong) — không có sessionId
        // (vd action gọi từ path không qua session) thì không chạy được, báo user re-login thay vì crash.
        if (string.IsNullOrWhiteSpace(sessionId))
            return new ActionResult(req.Action, "Phiên đăng nhập không hợp lệ — vui lòng đăng nhập lại.");

        var p = req.Params ?? new Dictionary<string, object?>();
        string customerId;
        // Forward-compat: nếu path này từng đi qua action-clarify (hiện chưa xảy ra — xem ghi chú dưới),
        // honor "customerResolvedId" trước customerId/customerName để nhất quán với assign_task/create_appointment.
        var customerResolvedId = Str(p, "customerResolvedId");
        var customerIdParam = Str(p, "customerId");
        if (!string.IsNullOrWhiteSpace(customerResolvedId))
        {
            customerId = customerResolvedId!;
        }
        else if (!string.IsNullOrWhiteSpace(customerIdParam))
        {
            customerId = customerIdParam!;
        }
        else
        {
            var customerName = Str(p, "customerName");
            if (string.IsNullOrWhiteSpace(customerName))
                return new ActionResult(req.Action, "Thiếu thông tin khách hàng để đánh giá.");

            var outcome = await _resolver.ResolveCustomerAsync(jwt, customerName, ct);
            if (outcome.Ambiguous is { Count: > 0 })
                return new ActionResult(req.Action,
                    $"Tên khách hàng \"{customerName}\" khớp nhiều người, vui lòng nói rõ hơn (vd họ tên đầy đủ).");
            if (outcome.Id is null)
                return new ActionResult(req.Action, $"Không tìm thấy khách hàng tên \"{customerName}\".");
            customerId = outcome.Id.Value.ToString(CultureInfo.InvariantCulture);
        }

        var forceFresh = Bool(p, "forceFresh") ?? false;

        var customers = await _customerSource.GetContextsAsync(sessionId, new[] { customerId }, ct);
        var customer = customers.FirstOrDefault();
        if (customer is null)
            return new ActionResult(req.Action, $"Không tìm thấy dữ liệu khách hàng #{customerId}.");

        CustomerReview review;
        using (_aiCtx.Push(AiFeatures.AssistantAction, tenantId, sessionId))
        {
            (review, _) = await _reviewService.ReviewAsync(
                customer, tenantId, forceFresh: forceFresh,
                providerOverride: req.Provider, modelOverride: req.Model, ct: ct);
        }

        _log.LogInformation(
            "[ActionExecutor] review_customer tenant={Tenant} customerId={Id} rank={Rank}",
            tenantId, customerId, review.Rank);

        // SummaryLine của AI đôi khi ĐÃ mở đầu bằng chính hạng (vd "C — …" hoặc "Hạng C — …") → tránh
        // lặp "hạng C — Hạng C —": strip tiền tố ["Hạng "|"hạng "] + rank + dấu (— / - / :) ở đầu SummaryLine.
        var sumLine = StripLeadingRank((review.SummaryLine ?? "").Trim(), review.Rank);
        var summary = $"Khách {customer.Name}: hạng {review.Rank} — {sumLine}";
        var data = new ChatData(
            Kind: "customer-review",
            Title: $"Đánh giá khách hàng — {customer.Name}",
            Raw: JsonSerializer.SerializeToElement(review),
            Stats: new List<ChatStat>(),
            Focus: null);

        return new ActionResult(req.Action, summary, data);
    }

    /// score_deal: resolve deal (id trực tiếp hoặc tên khách/tiêu đề → id qua ActionResolver) →
    /// fetch context (detail + comments + hồ sơ KH — NGUỒN DUY NHẤT dùng chung batch/workflow) →
    /// DealScoringService (dual-path) → SaveScore vào dbo.DealScores (worker DealScoreSyncService
    /// đọc bảng này để sync Rank xuống CRM — mirror POST /deals/{id}/rescore) → gói DealScore vào
    /// ChatData.Raw cho FE.
    private async Task<ActionResult> ExecuteScoreDealAsync(
        ActionExecuteRequest req, string tenantId, string jwt, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new ActionResult(req.Action, "Phiên đăng nhập không hợp lệ — vui lòng đăng nhập lại.");

        var p = req.Params ?? new Dictionary<string, object?>();
        int dealId;
        // Forward-compat: honor "dealResolvedId" trước dealId/dealQuery — xem ghi chú ở ExecuteReviewCustomerAsync.
        var dealResolvedId = Int(p, "dealResolvedId");
        var dealIdParam = Int(p, "dealId");
        if (dealResolvedId is { } rdid)
        {
            dealId = rdid;
        }
        else if (dealIdParam is { } did)
        {
            dealId = did;
        }
        else
        {
            var dealQuery = Str(p, "dealQuery");
            if (string.IsNullOrWhiteSpace(dealQuery))
                return new ActionResult(req.Action, "Thiếu thông tin cơ hội bán hàng để chấm điểm.");

            var outcome = await _resolver.ResolveDealAsync(jwt, dealQuery, ct);
            if (outcome.Ambiguous is { Count: > 0 })
                return new ActionResult(req.Action,
                    $"\"{dealQuery}\" khớp nhiều cơ hội, vui lòng nói rõ hơn (vd tên khách + mã đơn).");
            if (outcome.Id is null)
                return new ActionResult(req.Action, $"Không tìm thấy cơ hội bán hàng khớp \"{dealQuery}\".");
            dealId = outcome.Id.Value;
        }

        var contexts = await _dealClient.GetContextsAsync(sessionId, new[] { dealId }, ct);
        var dwc = contexts.FirstOrDefault();
        if (dwc is null)
            return new ActionResult(req.Action, $"Không tìm thấy cơ hội bán hàng #{dealId}.");

        var deal = dwc.Deal;
        var context = dwc.Context;

        DealScore score;
        using (_aiCtx.Push(AiFeatures.AssistantAction, tenantId, sessionId))
        {
            score = await _dealScoring.ScoreAsync(context.Profile, req.Provider, req.Model, null, ct);
        }
        // Persist như /deals/{id}/rescore — worker DealScoreSyncService đọc dbo.DealScores → sync
        // cột [rank] xuống BookingTickets tenant DB, để list/filter rank trên CRM phản ánh đúng.
        _dealRepo.SaveScore(tenantId, deal.Id, context.Fingerprint, score);

        _log.LogInformation(
            "[ActionExecutor] score_deal tenant={Tenant} dealId={Id} winRate={Rate}",
            tenantId, deal.Id, score.WinRate);

        // Title/Code có thể là chuỗi RỖNG (không null) → dùng IsNullOrWhiteSpace, tránh summary "Deal X — :".
        var label = !string.IsNullOrWhiteSpace(deal.Title) ? deal.Title!
                  : !string.IsNullOrWhiteSpace(deal.Code) ? deal.Code!
                  : $"#{deal.Id}";
        var labelPart = string.IsNullOrWhiteSpace(deal.Title) && string.IsNullOrWhiteSpace(deal.Code)
            ? "" : $" — {label}";
        var summary = $"Deal {deal.CustomerName}{labelPart}: tỉ lệ thắng {score.WinRate}% ({score.Level}) — {score.NextAction}";
        var data = new ChatData(
            Kind: "deal-score",
            Title: $"Chấm điểm deal — {deal.CustomerName}",
            Raw: JsonSerializer.SerializeToElement(score),
            Stats: new List<ChatStat>(),
            Focus: null);

        return new ActionResult(req.Action, summary, data);
    }

    // ─── Mail (check_mail / send_mail_reply / compose_mail) ───────────────────────

    private async Task<ActionResult> ExecuteMailAsync(
        ActionExecuteRequest req, string tenantId, string username, string? sessionId, CancellationToken ct)
        => req.Action.ToLowerInvariant() switch
        {
            "check_mail"      => await ExecuteCheckMailAsync(req, tenantId, username, sessionId, ct),
            "send_mail_reply" => await ExecuteSendMailReplyAsync(req, tenantId, username, ct),
            "compose_mail"    => await ExecuteComposeMailAsync(req, tenantId, username, ct),
            _ => throw new InvalidOperationException($"Unhandled Mail action: {req.Action}")
        };

    /// check_mail: KHÔNG cache theo ActionId (info tươi mỗi lần hỏi) + KHÔNG NeedsConfirm (xem ActionTools).
    /// Sync Gmail (MailSyncService — IMAP fetch + classify mail MỚI, dùng chung với endpoint/workflow)
    /// rồi tóm tắt các mail vừa phân loại theo nhóm. Classifier gọi AI → bọc AiCallContext.Push để
    /// trừ quota tenant + log đúng feature "assistant-action" (path /assistant/action/execute không
    /// tự match FeatureFromPath → sẽ rơi "other" nếu không Push, giống review_customer/score_deal).
    private async Task<ActionResult> ExecuteCheckMailAsync(
        ActionExecuteRequest req, string tenantId, string username, string? sessionId, CancellationToken ct)
    {
        if (!_mailAccount.IsConfigured(tenantId, username))
            return new ActionResult(req.Action, MailNotConfiguredMessage);

        var p = req.Params ?? new Dictionary<string, object?>();
        var fetchCap = Math.Clamp(Int(p, "limit") ?? MailSyncDefaultLimit, 1, MailSyncMaxLimit);

        MailSyncResult sync;
        try
        {
            using (_aiCtx.Push(AiFeatures.AssistantAction, tenantId, sessionId))
            {
                sync = await _mailSync.RunAsync(tenantId, username, fetchCap, ct);
            }
        }
        catch (InvalidOperationException ex)   // chưa cấu hình (race hiếm — vừa check ở trên vẫn có thể đổi)
        {
            return new ActionResult(req.Action, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ActionExecutor] check_mail tenant={Tenant} user={User} lỗi IMAP", tenantId, username);
            return new ActionResult(req.Action, "Không kết nối được hộp thư: " + ex.Message);
        }

        var newMails = sync.NewMails ?? Array.Empty<MailItem>();
        var counts = _mailRepo.Counts(tenantId);

        _log.LogInformation(
            "[ActionExecutor] check_mail tenant={Tenant} user={User} fetched={Fetched} new={New} unread={Unread}",
            tenantId, username, sync.Fetched, newMails.Count, counts.Unread);

        string summary;
        if (newMails.Count == 0)
        {
            summary = counts.Unread > 0
                ? $"Không có mail mới. Hộp thư hiện còn {counts.Unread} mail chưa đọc."
                : "Không có mail mới.";
        }
        else
        {
            var byCat = newMails
                .GroupBy(m => MailTaxonomy.Categories.TryGetValue(m.Category ?? "khac", out var lbl) ? lbl : "Khác")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
            summary = $"Có {newMails.Count} mail mới: {string.Join(", ", byCat)}.";
        }

        var data = new ChatData(
            Kind: "mail-list",
            Title: "Mail mới",
            Raw: JsonSerializer.SerializeToElement(newMails),
            Stats: new List<ChatStat>(),
            Focus: null);

        return new ActionResult(req.Action, summary, data);
    }

    /// send_mail_reply: gửi text ĐÃ SOẠN SẴN (từ proposal phase — task khác) tới người gửi gốc, threading
    /// qua In-Reply-To (IMailSender.SendReplyAsync — mirror POST /mail/{id}/reply/send). Thành công →
    /// SetDraft (lưu nội dung đã gửi) + status "da_phan_hoi" + cache theo ActionId (chống gửi trùng khi
    /// double-confirm). Thất bại SMTP → trả message lỗi, KHÔNG throw, KHÔNG đổi status.
    private async Task<ActionResult> ExecuteSendMailReplyAsync(
        ActionExecuteRequest req, string tenantId, string username, CancellationToken ct)
    {
        if (_done.TryGetValue(req.ActionId, out var cached)) return cached;

        if (!_mailAccount.IsConfigured(tenantId, username))
            return new ActionResult(req.Action, MailNotConfiguredMessage);

        var p = req.Params ?? new Dictionary<string, object?>();
        var mailId = Str(p, "mailId");
        var replyText = Str(p, "replyText");

        if (string.IsNullOrWhiteSpace(mailId))
            return new ActionResult(req.Action, "Thiếu mailId để trả lời.");
        if (string.IsNullOrWhiteSpace(replyText))
            return new ActionResult(req.Action, "Nội dung trả lời rỗng.");

        var mail = _mailRepo.Get(tenantId, mailId);
        if (mail is null)
            return new ActionResult(req.Action, $"Không tìm thấy email #{mailId}.");

        try
        {
            await _mailSender.SendReplyAsync(tenantId, username, mail, replyText, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ActionResult(req.Action, "Gửi email lỗi: " + ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ActionExecutor] send_mail_reply tenant={Tenant} mailId={Id} lỗi SMTP", tenantId, mailId);
            return new ActionResult(req.Action, "Gửi email lỗi: " + ex.Message);
        }

        var draft = new MailDraft(
            mail.Draft?.Tone ?? "lich_su", mail.Draft?.Instruction, replyText, DateTime.UtcNow.ToString("o"));
        _mailRepo.SetDraft(tenantId, mailId, draft, status: "da_phan_hoi");

        _log.LogInformation(
            "[ActionExecutor] send_mail_reply tenant={Tenant} user={User} mailId={Id} OK", tenantId, username, mailId);

        var result = new ActionResult(req.Action, "✅ Đã gửi cho khách.");
        _done[req.ActionId] = result;
        return result;
    }

    /// compose_mail: gửi email MỚI (text đã soạn sẵn từ proposal phase) tới người nhận bất kỳ qua SMTP
    /// (IMailSender.SendAsync — mirror POST /mail/compose/send). Cache theo ActionId chống gửi trùng.
    private async Task<ActionResult> ExecuteComposeMailAsync(
        ActionExecuteRequest req, string tenantId, string username, CancellationToken ct)
    {
        if (_done.TryGetValue(req.ActionId, out var cached)) return cached;

        if (!_mailAccount.IsConfigured(tenantId, username))
            return new ActionResult(req.Action, MailNotConfiguredMessage);

        var p = req.Params ?? new Dictionary<string, object?>();
        var to = Str(p, "to");
        var subject = Str(p, "subject") ?? "";
        var text = Str(p, "text");

        if (string.IsNullOrWhiteSpace(to))
            return new ActionResult(req.Action, "Thiếu người nhận.");
        if (string.IsNullOrWhiteSpace(text))
            return new ActionResult(req.Action, "Nội dung email rỗng.");

        try
        {
            await _mailSender.SendAsync(tenantId, username, to.Trim(), null, subject, text, null, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ActionResult(req.Action, "Gửi email lỗi: " + ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ActionExecutor] compose_mail tenant={Tenant} to={To} lỗi SMTP", tenantId, to);
            return new ActionResult(req.Action, "Gửi email lỗi: " + ex.Message);
        }

        _log.LogInformation(
            "[ActionExecutor] compose_mail tenant={Tenant} user={User} to={To} OK", tenantId, username, to);

        var result = new ActionResult(req.Action, "✅ Đã gửi email.");
        _done[req.ActionId] = result;
        return result;
    }

    private async Task<ActionResult> ExecuteCrmQueueAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        if (_done.TryGetValue(req.ActionId, out var cached)) return cached;

        var (result, success) = req.Action.ToLowerInvariant() switch
        {
            "assign_task" => await ExecuteAssignTaskAsync(req, tenantId, username, jwt, ct),
            "create_appointment" => await ExecuteCreateAppointmentAsync(req, tenantId, username, jwt, ct),
            _ => throw new InvalidOperationException($"Unhandled CrmQueue action: {req.Action}")
        };

        if (success) _done[req.ActionId] = result;
        return result;
    }

    private async Task<(ActionResult Result, bool Success)> ExecuteAssignTaskAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        var p = req.Params ?? new Dictionary<string, object?>();
        var name = Str(p, "name") ?? "Việc mới";
        var content = Str(p, "content");
        var workflowName = Str(p, "workflowName");
        var staffNamesRaw = Str(p, "staffNames");
        var prioritized = MapPriority(Str(p, "prioritized"));
        var startUtc = ParseUtc(Str(p, "startDate"));
        var dueUtc = ParseUtc(Str(p, "dueDate"));
        var reminderMinutes = Int(p, "reminderMinutes") ?? 0;
        var bookingTicketId = Int(p, "bookingTicketId");

        // Nếu proposal phase (ChatAgentService) đã resolve xong (user chọn ở action-clarify), id đã chọn
        // nằm sẵn trong "staffResolvedIds" (CSV) — dùng THẲNG, KHÔNG re-resolve theo "staffNames" gốc.
        // Bắt buộc phải mirror check này ở execute-time vì đây là điểm resolve ĐỘC LẬP thứ 2 (không chỉ
        // dựa vào proposal đã resolve) — thiếu bước này thì dù proposal đã hội tụ, bấm Xác nhận vẫn
        // re-resolve theo tên gốc và lặp lại đúng sự mơ hồ ban đầu khi nhiều người trùng tên.
        var staffResolvedIdsRaw = Str(p, "staffResolvedIds");
        var staffIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(staffResolvedIdsRaw))
        {
            staffIds.AddRange(staffResolvedIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (!string.IsNullOrWhiteSpace(staffNamesRaw))
        {
            foreach (var raw in staffNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var outcome = await _resolver.ResolveStaffAsync(jwt, raw, ct);
                if (outcome.Ambiguous is { Count: > 0 })
                    return (new ActionResult(req.Action,
                        $"Tên nhân viên \"{raw}\" khớp nhiều người, vui lòng nói rõ hơn (vd họ tên đầy đủ)."), false);
                if (outcome.Id is null)
                    return (new ActionResult(req.Action, $"Không tìm thấy nhân viên tên \"{raw}\"."), false);
                staffIds.Add(outcome.Id.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        var staffCsv = string.Join(',', staffIds);

        var payload = BuildAssignTaskPayload(
            null, workflowName, name, content, staffCsv, prioritized, startUtc, dueUtc, reminderMinutes, bookingTicketId);

        await _crmQueue.EnqueueAsync(new CrmActionInput(tenantId, username, CrmActionKind.AssignTask, payload), ct);
        _log.LogInformation(
            "[ActionExecutor] enqueue assign_task tenant={Tenant} user={User} name={Name}",
            tenantId, username, name);

        return (new ActionResult(req.Action, "✅ Đã đưa vào hàng đợi — hệ thống sẽ tạo việc trong CRM ít phút nữa."), true);
    }

    private async Task<(ActionResult Result, bool Success)> ExecuteCreateAppointmentAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        var p = req.Params ?? new Dictionary<string, object?>();
        var careTitle = Str(p, "careTitle") ?? "Lịch hẹn";
        var careDetail = Str(p, "careDetail");
        var startUtc = ParseUtc(Str(p, "startTime"));
        var endUtc = ParseUtc(Str(p, "endTime"));
        // Kết thúc rỗng → mặc định = bắt đầu + 1 tiếng (khớp proposal + thói quen đặt lịch CSKH).
        if (endUtc is null && startUtc is { } s) endUtc = s.AddHours(1);
        var reminderMinutes = Int(p, "reminderMinutes") ?? 0;
        var bookingTicketId = Int(p, "bookingTicketId");

        int customerId;
        var customerName = Str(p, "customerName");
        // Mirror check của "staffResolvedIds" ở trên — điểm resolve ĐỘC LẬP thứ 2 cho khách hàng, phải
        // honor "customerResolvedId" (đã chọn ở action-clarify) trước, KHÔNG re-resolve theo customerName gốc.
        var customerResolvedId = Int(p, "customerResolvedId");
        var customerIdParam = Int(p, "customerId");
        if (customerResolvedId is { } rid)
        {
            customerId = rid;
        }
        else if (customerIdParam is { } cid)
        {
            customerId = cid;
        }
        else if (!string.IsNullOrWhiteSpace(customerName))
        {
            var outcome = await _resolver.ResolveCustomerAsync(jwt, customerName, ct);
            if (outcome.Ambiguous is { Count: > 0 })
                return (new ActionResult(req.Action,
                    $"Tên khách hàng \"{customerName}\" khớp nhiều người, vui lòng nói rõ hơn."), false);
            if (outcome.Id is null)
                return (new ActionResult(req.Action, $"Không tìm thấy khách hàng tên \"{customerName}\"."), false);
            customerId = outcome.Id.Value;
            customerName = outcome.Label ?? customerName;
        }
        else
        {
            return (new ActionResult(req.Action, "Thiếu thông tin khách hàng để tạo lịch hẹn."), false);
        }

        var customerPhone = Str(p, "customerPhone");
        // Người phụ trách (InsUid): id đã chọn ở form/clarify nằm trong staffResolvedIds (lấy id ĐẦU nếu CSV).
        var insUid = Int(p, "staffResolvedIds")
            ?? (Str(p, "staffResolvedIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => int.TryParse(s, out var v) ? (int?)v : null).FirstOrDefault(v => v != null);
        var typeSchedule = Int(p, "typeSchedule") ?? 0;   // mặc định 0 = Lịch hẹn

        var payload = BuildAppointmentPayload(
            customerId, careTitle, careDetail, startUtc, endUtc, reminderMinutes,
            customerName, customerPhone, bookingTicketId, insUid, typeSchedule);

        await _crmQueue.EnqueueAsync(new CrmActionInput(tenantId, username, CrmActionKind.CreateAppointment, payload), ct);
        _log.LogInformation(
            "[ActionExecutor] enqueue create_appointment tenant={Tenant} user={User} customerId={CustomerId}",
            tenantId, username, customerId);

        return (new ActionResult(req.Action, "✅ Đã đưa vào hàng đợi — hệ thống sẽ tạo lịch hẹn trong CRM."), true);
    }

    // ─── Loose param readers (Params dict values đến từ JSON deserialize → JsonElement,
    //     hoặc string/số thô khi construct trực tiếp trong test). internal (không private) —
    //     ChatAgentService (task 10b, proposal phase) tái dùng để đọc cùng shape Params,
    //     tránh trùng lặp logic parse JsonElement/string ─────────────────────────────

    /// Bỏ tiền tố hạng lặp ở đầu SummaryLine: ["Hạng "|"hạng "]?{rank}[— | - | :] — tránh câu ghép
    /// "hạng C — Hạng C — …". Không khớp → trả nguyên chuỗi.
    internal static string StripLeadingRank(string sumLine, string? rank)
    {
        var r = (rank ?? "").Trim();
        if (sumLine.Length == 0 || r.Length == 0) return sumLine;
        var s = sumLine;
        foreach (var pre in new[] { "Hạng ", "hạng ", "HẠNG " })
            if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(pre.Length).TrimStart(); break; }
        if (!s.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return sumLine;   // không có rank → giữ nguyên
        var rest = s.Substring(r.Length).TrimStart();
        if (rest.StartsWith("—") || rest.StartsWith("-") || rest.StartsWith(":"))
            return rest.Substring(1).TrimStart();
        return sumLine;   // rank không kèm dấu phân tách → có thể là từ khác, giữ nguyên cho an toàn
    }

    internal static string? Str(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        if (v is JsonElement je)
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => je.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.GetRawText()
            };
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    internal static int? Int(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var n2)) return n2;
            return null;
        }
        if (v is int i) return i;
        return int.TryParse(v.ToString(), out var n3) ? n3 : null;
    }

    private static bool? Bool(Dictionary<string, object?> p, string key)
    {
        var s = Str(p, key);
        return !string.IsNullOrWhiteSpace(s) && bool.TryParse(s, out var b) ? b : null;
    }

    /// Parse chuỗi ngày/giờ → UTC. Chuỗi có Z/offset → tôn trọng; chuỗi TRẦN (từ planner AI
    /// hoặc <input type="datetime-local"> FE) coi là giờ VN (UTC+7, không DST) → trừ 7h.
    public static DateTime? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return null;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt.AddHours(-7), DateTimeKind.Utc) // bare = giờ VN (UTC+7)
        };
    }
}
