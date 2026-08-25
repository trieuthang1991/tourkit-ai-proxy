using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Infrastructure.Mail;
using TourkitAiProxy.Domain.Mail;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Domain.Deals;
using TourkitAiProxy.Infrastructure.Digest;   // SaleBriefRepository

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Bản tin sáng cho nhân viên bán hàng. Chạy mỗi 60' rồi tự chọn ai "đến giờ".
///
/// <para><b>Mỗi người lấy dữ liệu bằng PHIÊN CỦA CHÍNH MÌNH</b> — không dùng tài khoản tự động.
/// Đây là điểm khác plan gốc và là quyết định có chủ đích: tài khoản tự động có quyền xem toàn
/// công ty, nếu lọc sai một dòng thì nhân viên A đọc được cơ hội của nhân viên B. Dùng token của
/// chính họ thì <b>CRM tự chặn</b> — lọc sai cũng chỉ thiếu, không lộ.</para>
///
/// <para>Điều kiện thay thế: người đó phải từng đăng nhập ít nhất 1 lần. Nhẹ hơn tưởng —
/// <see cref="TkSessionStore"/> giữ mật khẩu (mã hoá) và tự đăng nhập lại khi token hết hạn,
/// lưu tới 30 ngày. Không có phiên → bỏ qua người đó và ghi rõ lý do, KHÔNG fail cả lượt.</para>
///
/// <para><b>Số do máy chủ lấy, AI chỉ sắp xếp lại.</b> Bản rule in đủ mọi mục vượt ngưỡng nên gặp
/// CRM dùng lâu thì thành bảng tồn kho (chạy thật erp.tourkit.vn: "61 việc · 50 trễ hạn"). AI đọc
/// cùng bộ dữ kiện rồi chọn ra việc đáng làm sáng nay. AI lỗi/hết lượt → rơi về bản rule, bản tin
/// KHÔNG bao giờ mất. Tắt AI bằng tuỳ chọn <c>useAi=false</c>.</para>
///
/// <para>Nguồn nào lỗi thì mục đó rỗng, bản tin vẫn gửi: thà thiếu một mục còn hơn sáng ra không
/// có bản tin nào.</para>
/// </summary>
public class SaleBriefWorkflow : IScheduledWorkflow
{
    private const int SilentDaysMinDefault = 3;      // im lặng bao nhiêu ngày thì đưa vào "cần gọi lại"
    private const int VipSleepDays = 60;      // khách hạng A/B bao lâu không mua lại thì nhắc
    private const int StaleQuoteDaysDefault = 5;     // báo giá bao lâu không ai sửa thì nhắc
    private const int HygieneStuckDaysDefault = 14;  // cơ hội kẹt 1 trạng thái bao lâu thì coi là cần dọn

    private readonly DigestSubscriptionRepository _subs;
    private readonly TkSessionStore _sessions;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TourKitApiClient _api;
    private readonly SaleBriefRepository _repo;
    private readonly MailRepository _mails;
    private readonly InsightRepository _insights;
    private readonly MailQueueRepository _queue;
    private readonly TenantChannelSettingsStore _channels;
    private readonly IConfiguration _cfg;
    private readonly Providers.ProviderRegistry _providers;
    private readonly Providers.AiModelRegistry _models;
    private readonly AiCallContext _ctx;
    private readonly ILogger<SaleBriefWorkflow> _log;

    public SaleBriefWorkflow(DigestSubscriptionRepository subs, TkSessionStore sessions,
        TkSessionRepository sessionRepo, TourKitApiClient api, SaleBriefRepository repo,
        MailRepository mails, InsightRepository insights, MailQueueRepository queue, TenantChannelSettingsStore channels,
        IConfiguration cfg, Providers.ProviderRegistry providers, Providers.AiModelRegistry models,
        AiCallContext ctx, ILogger<SaleBriefWorkflow> log)
    {
        _subs = subs; _sessions = sessions; _sessionRepo = sessionRepo; _api = api;
        _repo = repo; _mails = mails; _insights = insights; _queue = queue; _channels = channels; _cfg = cfg;
        _providers = providers; _models = models; _ctx = ctx; _log = log;
    }

    /// <summary>
    /// Cấu hình per-tenant. Mọi thứ ở đây TRƯỚC ĐÂY LÀ HẰNG SỐ TRONG CODE — mà hằng số nghĩa là
    /// mình đoán hộ công ty. Ngưỡng "im lặng bao lâu thì cần gọi" ở công ty bán tour đoàn khác hẳn
    /// công ty bán vé lẻ; tệ hơn nữa là <see cref="ClosedStatuses"/>: mỗi CRM tự đặt tên trạng thái
    /// nên đoán bằng từ khoá tiếng Việt kiểu gì cũng có tenant sai.
    /// </summary>
    /// <param name="UseAi">AI tinh chỉnh lại bản tin (tốn 1 lượt/người/ngày). Tắt = dùng bản rule.</param>
    /// <param name="MaxItems">Trần số việc AI được chọn — bản tin dài thì không ai đọc.</param>
    /// <param name="ClosedStatuses">Mã trạng thái coi là ĐÃ ĐÓNG (bỏ khỏi bản tin). RỖNG = tự nhận
    /// diện bằng <c>DealCooling</c> (Hủy=5 + từ khoá "đã chốt"/"hoàn thành"…) — đúng cho phần lớn
    /// tenant nhưng KHÔNG chắc; công ty nào đặt tên trạng thái riêng thì khai mã vào đây.</param>
    /// <param name="CallStatuses">Trạng thái cơ hội mà nhân viên CÒN phải chăm. Hỏi theo hướng
    /// KHẲNG ĐỊNH ("chỉ nhắc khi ở trạng thái…") thay vì phủ định ("coi là đã đóng"): bắt người dùng
    /// suy ngược "cái nào là đóng để loại ra" là bắt họ làm việc của mình. RỖNG = mọi trạng thái
    /// đang mở, tự loại Hủy/đã-chốt bằng <c>DealCooling</c> — tiện nhưng là ĐOÁN theo tên, công ty
    /// đặt tên riêng thì nên chọn tay.</param>
    private record SaleBriefOptions(
        bool UseAi, int MaxItems,
        int SilentDaysMin, int HygieneStuckDays, int StaleQuoteDays,
        List<int> CallStatuses, List<int> TaskStatuses,
        bool SecCooling, bool SecHygiene, bool SecQuotes,
        bool SecAppointments, bool SecTasks, bool SecPayments, bool SecVips, bool SecMailbox);

    private SaleBriefOptions ParseOptions(string? json)
    {
        // Mặc định BẬT HẾT: tenant chưa cấu hình gì thì nhận đủ như trước, không ai mất mục nào
        // vì một đợt nâng cấp.
        var def = new SaleBriefOptions(UseAi: true, MaxItems: 7,
            SilentDaysMin: SilentDaysMinDefault, HygieneStuckDays: HygieneStuckDaysDefault,
            StaleQuoteDays: StaleQuoteDaysDefault,
            CallStatuses: new List<int>(), TaskStatuses: new List<int>(),
            SecCooling: true, SecHygiene: true, SecQuotes: true,
            SecAppointments: true, SecTasks: true, SecPayments: true, SecVips: true, SecMailbox: true);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            List<int> Ints(string key)
            {
                var outp = new List<int>();
                if (r.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) outp.Add(n);
                return outp;
            }
            var call = Ints("callStatuses");
            var taskSt = Ints("taskStatuses");

            bool Bit(string k, bool dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : dv;

            int Get(string k, int dv, int lo, int hi)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? Math.Clamp(n, lo, hi) : dv;

            return def with
            {
                UseAi = r.TryGetProperty("useAi", out var ua) && ua.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? ua.GetBoolean() : def.UseAi,
                MaxItems = Get("maxItems", def.MaxItems, 3, 20),
                SilentDaysMin = Get("silentDaysMin", def.SilentDaysMin, 1, 90),
                HygieneStuckDays = Get("hygieneStuckDays", def.HygieneStuckDays, 3, 365),
                StaleQuoteDays = Get("staleQuoteDays", def.StaleQuoteDays, 1, 365),
                CallStatuses = call,
                TaskStatuses = taskSt,
                SecCooling = Bit("secCooling", def.SecCooling),
                SecHygiene = Bit("secHygiene", def.SecHygiene),
                SecQuotes = Bit("secQuotes", def.SecQuotes),
                SecAppointments = Bit("secAppointments", def.SecAppointments),
                SecTasks = Bit("secTasks", def.SecTasks),
                SecPayments = Bit("secPayments", def.SecPayments),
                SecVips = Bit("secVips", def.SecVips),
                SecMailbox = Bit("secMailbox", def.SecMailbox),
            };
        }
        catch (JsonException)
        {
            // OptionsJson hỏng → chạy bằng mặc định. Ném ở đây là mất bản tin của cả công ty vì
            // một dấu phẩy sai trong cấu hình.
            _log.LogWarning("[sale-brief] OptionsJson không đọc được — dùng mặc định");
            return def;
        }
    }


    public string Type => "sale-brief";
    public string Label => "Bản tin cho nhân viên bán hàng";
    public string Description => "Mỗi ngày gom việc cần làm (cơ hội cần gọi, lịch hẹn, việc, báo giá) gửi từng người đã đăng ký. AI sắp xếp lại cho gọn — tốn 1 lượt/người/ngày, tắt được ở tuỳ chọn.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    // Có luật chung (mục nào vào bản tin, ngưỡng, trạng thái) → công ty phải khai trước khi ai đăng ký.
    public bool HasCompanyRules => true;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var opt = ParseOptions(optionsJson);
        var utcNow = DateTime.UtcNow;
        var todayVn = DigestDue.NowVn(utcNow).Date;

        // Chỉ CHUẨN BỊ: đến cửa sổ (giờ chọn − lead) và hôm nay chưa có bản tin loại này cho người đó.
        var lead = _cfg.GetValue("Digest:LeadMinutes", 10);
        var subs = await _subs.ListEnabledAsync(tenantId, BriefTypes.Sale, ct);
        var due = new List<DigestSubscription>();
        foreach (var s in subs)
            if (DigestDue.ShouldPrepare(s, utcNow, lead)
                && !await _insights.ExistsTodayAsync(tenantId, s.Username, BriefTypes.Sale, todayVn, ct))
                due.Add(s);
        if (due.Count == 0)
            return new(true, "Chưa tới giờ chuẩn bị của ai (0 đăng ký đến hạn).", null);

        // Hộp thư là số của CẢ CÔNG TY nên đọc 1 lần, dùng cho mọi người nhận.
        int mailPending = 0, mailQuote = 0;
        bool mailOk = true;
        try
        {
            var c = _mails.Counts(tenantId);
            mailPending = c.ByStatus.TryGetValue("moi", out var m) ? m : 0;
            mailQuote = c.ByCategory.TryGetValue("xin_bao_gia", out var q) ? q : 0;
        }
        catch (Exception ex)
        {
            mailOk = false;
            _log.LogWarning("[sale-brief] tenant={T} đọc hộp thư lỗi: {Err}", tenantId, ex.Message);
        }

        int prepared = 0, noSession = 0, failed = 0, skipped = 0, aiCalls = 0, aiFails = 0;
        var parts = new List<string>();

        // Mẫu ZNS của công ty: tra MỘT LẦN cho cả lượt, không tra lại theo từng người.
        // Chưa khai thì để null — worker sẽ đánh dấu "thiếu cấu hình" chứ KHÔNG mượn mẫu khác.
        string? zaloTemplateId = null;
        if (due.Any(x => x.ChannelZalo))
        {
            var zcfg = await _channels.GetZaloAsync(tenantId, ct);
            zaloTemplateId = zcfg?.TemplateFor(BriefTypes.Sale);
        }

        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var session = await _sessionRepo.GetByUserAsync(tenantId, sub.Username, ct);
                if (session == null)
                {
                    noSession++;
                    _log.LogInformation("[sale-brief] tenant={T} user={U} chưa đăng nhập lần nào — bỏ qua",
                        tenantId, sub.Username);
                    continue;
                }

                // Token của CHÍNH người này; tự đăng nhập lại nếu hết hạn (user không thấy gì).
                var jwt = await _sessions.GetValidJwtAsync(session.Id, ct);
                var crmUserId = await _sessions.EnsureCrmUserIdAsync(session.Id, ct);

                var input = await BuildInputAsync(tenantId, sub.Username, session.FullName,
                    crmUserId, jwt, todayVn, mailPending, mailQuote, mailOk, opt, ct);

                var msg = opt.UseAi
                    ? await ComposeAsync(tenantId, session.Id, input, todayVn, opt, ct,
                        () => aiCalls++, () => aiFails++)
                    : SaleBriefBuilder.Build(input, todayVn);

                // NỘI DUNG + KHO LƯU: bản tin ghi vào Bảng tin (in-app luôn-bật — kho lưu để
                // xem/nghe lại), Id của dòng này là nguồn nội dung cho các kênh ngoài.
                var insightId = await _insights.InsertAsync(new AgentInsight(
                    0, tenantId, sub.Username, BriefTypes.Sale, 0,
                    msg.Title, msg.BodyMarkdown, null, null, false, DateTime.UtcNow), ct);
                // insightId null chỉ khi AlertKey trùng trong 24h — bản tin dùng AlertKey=null nên không xảy ra;
                // giữ guard phòng sau này ai thêm AlertKey.
                if (insightId == null) { skipped++; continue; }

                var schedUtc = DigestDue.SendMomentUtc(sub, utcNow);
                var rows = DigestEnqueuePlanner.BuildRows(sub, insightId.Value, msg, schedUtc,
                    todayVn.ToString("dd/MM/yyyy"), zaloTemplateId);
                int qOk = 0, qFail = 0;
                foreach (var r in rows)
                {
                    // Enqueue từng kênh CÔ LẬP: 1 dòng lỗi (blip DB) KHÔNG được cướp mất các kênh còn lại
                    // của người này — insight đã ghi nên lượt sau ExistsToday=true sẽ bỏ qua, không có cơ hội bù.
                    try { await _queue.EnqueueAsync(r, ct); qOk++; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        qFail++;
                        _log.LogWarning(ex, "[sale-brief] enqueue kênh {Ch} lỗi tenant={T} user={U}",
                            r.Channel, tenantId, sub.Username);
                    }
                }
                prepared++;
                parts.Add($"{sub.Username}[inapp+{qOk} kênh queue{(qFail > 0 ? $", {qFail} kênh lỗi" : "")}]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "[sale-brief] tenant={T} user={U} gửi lỗi", tenantId, sub.Username);
                parts.Add($"{sub.Username}[LỖI: {ex.Message}]");
            }
        }

        var summary = $"{due.Count} đăng ký đến hạn → chuẩn bị {prepared}"
                    + (noSession > 0 ? $", bỏ qua {noSession} (chưa đăng nhập)" : "")
                    + (skipped > 0 ? $", trùng {skipped}" : "")
                    + (failed > 0 ? $", lỗi {failed}" : "")
            // Ghi rõ số lượt AI vào tóm tắt: nhìn lịch sử chạy là biết ngay tốn bao nhiêu lượt và
            // có bao nhiêu lần phải rơi về bản rule — khỏi phải mò log.
                    + (aiCalls > 0 ? $" · AI {aiCalls} lượt" + (aiFails > 0 ? $" ({aiFails} lỗi → dùng bản rule)" : "") : "")
                    + (parts.Count > 0 ? ". " + string.Join(" · ", parts) : "");
        _log.LogInformation("[sale-brief] tenant={T} {Sum}", tenantId, summary);

        try { await _insights.PruneAsync(_cfg.GetValue("Digest:InsightKeepDays", 30), ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* prune lỗi không làm hỏng lượt chạy */ }

        return new(true, summary, null);
    }

    /// <summary>
    /// Gom dữ liệu cho 1 người. MỖI nguồn bọc try riêng: nguồn nào lỗi thì mục đó rỗng,
    /// các mục còn lại vẫn có. Bản tin thiếu một mục vẫn hơn không có bản tin.
    /// </summary>
    /// <summary>
    /// AI sắp xếp lại bản tin từ dữ kiện máy chủ đã lấy. Lỗi / hết lượt / trả rỗng → rơi về bản
    /// rule. Hàm này CỐ Ý không bao giờ ném: thà đọc bản rule dài dòng còn hơn sáng ra không có
    /// bản tin nào.
    /// </summary>
    private async Task<DigestMessage> ComposeAsync(string tenantId, string sessionId,
        SaleBriefInput input, DateTime todayVn, SaleBriefOptions opt, CancellationToken ct,
        Action onAiCall, Action onAiFail)
    {
        try
        {
            // STRICT: workflow nền không có HttpContext → thiếu Push là bypass quota tenant và log
            // feature=unknown/tenant=null (xem AiCallContext).
            using var _ = _ctx.Push(AiFeatures.Digest, tenantId, sessionId);
            onAiCall();

            // Qua AiModelRegistry, không gọi thẳng provider mặc định — để provider tự chọn thì nó
            // lấy model "Recommended" của chính nó và bỏ qua cấu hình Models:* của người vận hành.
            var resolved = _models.Resolve(Providers.AiFeature.Digest);
            var provider = _providers.Resolve(resolved.Provider);
            var r = await provider.CompleteAsync(new Domain.Models.CompleteRequest(
                Prompt: SaleBriefBuilder.BuildPrompt(input, todayVn, opt.MaxItems),
                Provider: resolved.Provider, Model: resolved.Model,
                // 10000: model reasoning (vd DeepSeek qua nine-routes) tiêu phần lớn hạn mức vào
                // phần NGHĨ trước khi viết. Để 1200–1400 thì nó nghĩ hết sạch, `content` trả về rỗng,
                // và bản tin gửi đi là chuỗi suy nghĩ cụt giữa chừng (đã xảy ra thật 19/08).
                MaxTokens: 10000,
                Temperature: 0.3,   // sát dữ kiện; cao hơn là bắt đầu "văn hoa" thêm ý không có
                System: null,
                ApiKey: resolved.ApiKey), ct);

            // Rỗng HOẶC chỉ có chuỗi suy nghĩ → đều là "AI không trả lời được", dùng bản rule.
            // TextFromReasoning là chốt QUAN TRỌNG: khi model nghĩ hết hạn mức, UpstreamParser lấy
            // tạm reasoning_content làm Text (để debug). Text đó KHÔNG rỗng nên chốt IsNullOrWhiteSpace
            // ở trên không bắt được, và nguyên phần "Chúng ta cần trả lời câu hỏi…" từng lọt vào thư
            // gửi đi thật. Bản rule vốn đã chạy tốt — nó chỉ chưa từng được gọi tới.
            if (string.IsNullOrWhiteSpace(r.Text) || r.TextFromReasoning)
            {
                onAiFail();
                _log.LogWarning("[sale-brief] tenant={T} user={U} AI {Ly} → dùng bản rule",
                    tenantId, input.Username,
                    r.TextFromReasoning ? "chỉ trả chuỗi suy nghĩ (nghĩ hết hạn mức)" : "trả rỗng");
                return SaleBriefBuilder.Build(input, todayVn);
            }
            return SaleBriefBuilder.WrapAiReply(r.Text, input, todayVn);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            onAiFail();
            _log.LogWarning(ex, "[sale-brief] tenant={T} user={U} AI lỗi → dùng bản rule",
                tenantId, input.Username);
            return SaleBriefBuilder.Build(input, todayVn);
        }
    }

    private async Task<SaleBriefInput> BuildInputAsync(string tenantId, string user, string? fullName,
        int? crmUserId, string jwt, DateTime todayVn,
        int mailPending, int mailQuote, bool mailOk, SaleBriefOptions opt, CancellationToken ct)
    {
        var cooling = new List<DealLine>();
        var hygiene = new List<DealLine>();
        var appts = new List<ApptLine>();
        var tasks = new List<TaskLine>();
        var vips = new List<CustomerLine>();
        var quotes = new List<QuoteLine>();
        var payments = new List<PaymentAlert>();
        int overdueAppt = 0, overdueTask = 0;

        // Điểm AI đã chấm (nếu có) — chỉ để hiện % khả năng chốt, thiếu thì để 0.
        var winRates = await LoadWinRatesAsync(tenantId, ct);

        // ── Cơ hội bán hàng: cần gọi lại + cần dọn ────────────────────────────
        if (opt.SecCooling || opt.SecHygiene)
        await Safe("booking-tickets", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/booking-tickets?pageIndex=1&pageSize=100", ct);
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                var code = Str(it, "code") ?? "";
                var title = Str(it, "title") ?? Str(it, "customerName") ?? code;
                var silent = Int(it, "coolingDays");
                var statusId = Int(it, "status");
                var statusName = Str(it, "statusName");

                // ĐƠN ĐÃ ĐÓNG THÌ BỎ HẲN — không còn là cơ hội, không gọi lại cũng không dọn.
                // Thiếu điều kiện này, bản tin bảo nhân viên "gọi lại" cả đơn đã HỦY: trên dữ liệu
                // thật erp.tourkit.vn ngày 14/08 có 63 cơ hội cần gọi mà phần lớn trạng thái "Hủy".
                // Dùng DealCooling — NGUỒN DUY NHẤT của khái niệm này, đã dùng ở deals.jsx và
                // deal-auto-review; bản tin sáng là chỗ duy nhất còn tự tính nên mới lệch.
                // Công ty khai danh sách trạng thái "còn phải chăm" thì TIN HỌ TUYỆT ĐỐI — họ biết
                // CRM của mình. Không khai mới rơi về đoán theo tên (DealCooling), vốn chỉ đúng với
                // công ty đặt tên trạng thái theo lối phổ biến.
                var eligible = opt.CallStatuses.Count > 0
                    ? opt.CallStatuses.Contains(statusId)
                    : statusId != DealCooling.CancelStatus && !DealCooling.IsClosedWon(statusName);
                if (!eligible) continue;

                var wr = winRates.TryGetValue(code, out var w) ? w : 0;
                var line = new DealLine(0, title, Str(it, "customerName"), wr, silent, statusName);

                // MỖI CƠ HỘI CHỈ VÀO ĐÚNG MỘT MỤC. Trước đây hai điều kiện độc lập nên cơ hội im
                // lâu thoả cả hai, bị đếm và in hai lần ở "cần gọi lại" lẫn "cần dọn" — người đọc
                // thấy trùng và không biết phải làm gì.
                //
                // Kẹt lâu hơn thì ưu tiên DỌN, vì hai mục là hai HÀNH ĐỘNG khác nhau: "gọi lại" là
                // còn cơ hội bán, còn "dọn" là hồ sơ kẹt cần cập nhật cho đúng. Cơ hội kẹt quá
                // HygieneStuckDays ngày mà chưa có bước tiếp theo thì gọi khách chưa phải việc đầu
                // tiên — phải biết nó đang ở đâu đã.
                if (silent >= opt.HygieneStuckDays)
                {
                    if (opt.SecHygiene) hygiene.Add(line);
                }
                else if (opt.SecCooling
                         && (Bool(it, "isCooling") || silent >= opt.SilentDaysMin)) cooling.Add(line);
            }
            // Nguội lâu nhất lên đầu — đó là cái dễ mất nhất.
            cooling.Sort((a, b) => b.SilentDays.CompareTo(a.SilentDays));
            hygiene.Sort((a, b) => b.SilentDays.CompareTo(a.SilentDays));
        });

        // ── Lịch hẹn: CHỈ HÔM NAY ─────────────────────────────────────────────
        // Cố ý KHÔNG kéo lịch quá hạn (bỏ hẳn lệnh gọi dateFilter=3). Một cuộc hẹn đã trôi qua thì
        // không "làm bù" được — nhắc lại chỉ làm bản tin dài thêm mà không đổi việc gì phải làm hôm
        // nay. Việc quá hạn thì khác: vẫn làm được nên vẫn nhắc (xem mục dưới).
        //
        // ⚠️ CRM KHÔNG tự bỏ lịch đã xong: dateFilter=1 lọc THUẦN theo ngày (CustomerCareService chỉ
        // loại Status=4 "đã xoá"). Không lọc ở đây thì bản tin bảo đi gặp khách mà cuộc hẹn đã diễn
        // ra xong. Trạng thái lịch hẹn là enum HỆ THỐNG (1=Tạo mới, 2=Thành công, 3=Không thành
        // công, 4=Đã xoá), công ty KHÔNG tự thêm được → lọc bằng mã là chắc chắn đúng, không phải
        // đoán theo tên như trạng thái cơ hội.
        if (opt.SecAppointments)
        await Safe("appointments", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=1&pageIndex=1&pageSize=50", ct);
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                if (!ApptStillOpen(it)) continue;
                appts.Add(new ApptLine(
                    Str(it, "scheduleTimeFormatted") ?? "",
                    Str(it, "title") ?? "Lịch hẹn",
                    Str(it, "customerName")));
            }
        });

        // ── Việc cần làm hôm nay + trễ hạn ────────────────────────────────────
        // ⚠️ tabFilter=3 (hôm nay) KHÔNG loại việc đã Hoàn thành/Hủy — chỉ tabFilter=2 (trễ hạn) mới
        // loại (điều kiện nằm trong SP uspSearchTasking). Nên chỉ danh sách "hôm nay" cần lọc tay.
        // Mã trạng thái task cũng là enum hệ thống 1..5; công ty chỉ ĐỔI TÊN được (SectionWork),
        // không thêm mã mới → lọc theo mã vẫn đúng dù tên hiển thị là gì.
        if (opt.SecTasks)
        await Safe("tasks", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/tasks?tabFilter=3&pageIndex=1&pageSize=50", ct);
            foreach (var it in Items(d))
            {
                if (!TaskStillOpen(it, opt.TaskStatuses)) continue;
                tasks.Add(new TaskLine(Str(it, "name") ?? Str(it, "code") ?? "Công việc",
                                       Str(it, "priorityName"), IsOverdue: false));
            }
            var od = await _api.GetAsync(jwt, "/api/ai/tasks?tabFilter=2&pageIndex=1&pageSize=50", ct);
            var late = Items(od).Where(it => TaskStillOpen(it, opt.TaskStatuses)).ToList();
            overdueTask = late.Count;
            // Việc trễ chèn LÊN ĐẦU và đánh dấu — đó là thứ cần làm trước.
            for (int i = late.Count - 1; i >= 0; i--)
                tasks.Insert(0, new TaskLine(Str(late[i], "name") ?? "Công việc",
                                             Str(late[i], "priorityName"), IsOverdue: true));
        });

        // ── Tour sắp đi còn thiếu tiền (của mình) ─────────────────────────────
        if (opt.SecPayments)
        await Safe("tours", async () =>
        {
            var to = todayVn.AddDays(7);
            var d = await _api.GetAsync(jwt,
                $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);
            var rows = new List<TourPaymentRow>();
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                if (!TryDec(it, "actualRevenue", out var actual)) continue;   // xem PaymentWatchdogWorkflow
                if (!TryDate(it, "departureDate", out var dep)) continue;
                rows.Add(new TourPaymentRow(Int(it, "id"), Str(it, "title") ?? "Tour",
                    Str(it, "customerName"), Str(it, "sellerName"), dep, Dec(it, "revenue"), actual));
            }
            payments = PaymentWatchdogRule.Evaluate(rows, todayVn);
        });

        // ── Khách hạng A/B lâu không mua lại (bảng Reviews của proxy) ─────────
        if (opt.SecVips)
        await Safe("reviews", async () =>
        {
            var rows = await _repo.HangKhachAsync(tenantId, ct);
            foreach (var r in rows)
            {
                var days = (int)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(r.GeneratedAt)).TotalDays;
                if (days >= VipSleepDays) vips.Add(new CustomerLine(r.CustomerId, r.Rank, days));
            }
            vips.Sort((a, b) => b.DaysSinceLastBooking.CompareTo(a.DaysSinceLastBooking));
        });

        // ── Báo giá của mình lâu chưa cập nhật ────────────────────────────────
        if (opt.SecQuotes)
        await Safe("quotes", async () =>
        {
            var rows = await _repo.BaoGiaCuAsync(tenantId, user, opt.StaleQuoteDays, ct);
            foreach (var r in rows)
                quotes.Add(new QuoteLine(r.Title ?? "Báo giá", r.CustomerName,
                    (int)(DateTime.UtcNow - r.UpdatedAt).TotalDays));
        });

        return new SaleBriefInput(user, fullName, cooling, appts, vips, quotes,
            mailPending, mailQuote, hygiene, payments, mailOk,
            TodayTasks: tasks, OverdueTaskCount: overdueTask, OverdueAppointments: overdueAppt,
            ShowMailbox: opt.SecMailbox);

        // Bọc từng nguồn: lỗi 1 nguồn KHÔNG được làm mất cả bản tin.
        async Task Safe(string name, Func<Task> f)
        {
            try { await f(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("[sale-brief] tenant={T} user={U} nguồn '{N}' lỗi: {Err}",
                    tenantId, user, name, ex.Message);
            }
        }
    }

    /// Map mã cơ hội → % khả năng chốt từ điểm AI đã lưu. Chưa chấm thì không có mặt.
    private async Task<Dictionary<string, int>> LoadWinRatesAsync(string tenantId, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = await _repo.DiemDealAsync(tenantId, ct);
            foreach (var r in rows) map[r.DealId] = r.WinRate ?? 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[sale-brief] tenant={T} đọc điểm deal lỗi: {Err}", tenantId, ex.Message);
        }
        return map;
    }

    /// <summary>
    /// Dòng này có thuộc người nhận không? Người có quyền xem toàn công ty dùng token của chính
    /// mình VẪN thấy hết, nên phải lọc thêm. So theo id CRM trước (chắc nhất), rồi tới họ tên,
    /// cuối cùng là tên đăng nhập. KHÔNG có thông tin phụ trách → coi là KHÔNG phải của mình,
    /// thà thiếu còn hơn nhắc việc người khác.
    /// </summary>
    private static bool Mine(JsonElement it, string username, string? fullName, int? crmUserId)
    {
        if (crmUserId != null)
        {
            foreach (var f in new[] { "assigneeId", "nhanVienPhuTrachId", "sellerId", "insUid" })
                if (it.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number
                    && v.GetInt32() == crmUserId.Value) return true;
        }
        foreach (var f in new[] { "assignee", "assignees", "sellerName", "createdBy", "nhanVienPhuTrach" })
        {
            var s = Str(it, f);
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!string.IsNullOrWhiteSpace(fullName) && s!.Contains(fullName!, StringComparison.OrdinalIgnoreCase)) return true;
            if (s!.Contains(username, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── Đọc JSON (envelope /api/ai/* camelCase) ───────────────────────────────
    private static IEnumerable<JsonElement> Items(JsonElement d)
        => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("items", out var it)
           && it.ValueKind == JsonValueKind.Array
            ? it.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bool(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    // ── Việc còn phải làm hay đã xong ─────────────────────────────────────────
    // Mã trạng thái ở đây là enum HỆ THỐNG của CRM (công ty chỉ đổi tên hiển thị, không thêm mã),
    // nên lọc theo mã là chắc chắn — khác hẳn trạng thái cơ hội vốn do từng công ty tự tạo.
    // Thiếu mã (0) thì coi như CÒN mở: bỏ sót một việc đã xong đỡ tệ hơn nuốt mất việc chưa làm.

    /// Lịch hẹn: 1=Tạo mới · 2=Thành công · 3=Không thành công · 4=Đã xoá.
    private static bool ApptStillOpen(JsonElement e)
    {
        var s = Int(e, "status");
        return s is not (2 or 3 or 4);
    }

    /// Công việc: 1=Chưa bắt đầu · 2=Đang thực hiện · 3=Đang kiểm tra · 4=Hoàn thành · 5=Hủy.
    /// Công ty chọn danh sách riêng thì danh sách ĐÓ quyết định (tôn trọng cách họ dùng trạng thái —
    /// có nơi coi "Đang kiểm tra" là xong rồi); chưa chọn thì rơi về enum hệ thống.
    private static bool TaskStillOpen(JsonElement e, IReadOnlyCollection<int> openStatuses)
    {
        var s = Int(e, "status");
        if (openStatuses is { Count: > 0 }) return openStatuses.Contains(s);
        return s is not (4 or 5);
    }

    private static decimal Dec(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static bool TryDec(JsonElement e, string n, out decimal val)
    {
        val = 0m;
        if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetDecimal(); return true; }
        return false;
    }

    private static bool TryDate(JsonElement e, string n, out DateTime val)
    {
        val = default;
        return DateTime.TryParse(Str(e, n), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out val);
    }
}
