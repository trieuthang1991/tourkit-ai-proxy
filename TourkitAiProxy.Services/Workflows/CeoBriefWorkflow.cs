using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Infrastructure.Digest;
using TourkitAiProxy.Infrastructure.Mail;
using TourkitAiProxy.Domain.Mail;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Bản tin điều hành cho giám đốc: doanh thu – chi phí – lợi nhuận so cùng kỳ, cộng vài biến động
/// đáng để ý. Chạy mỗi 60' rồi tự chọn ai "đến giờ" (giống bản tin sales).
///
/// <para><b>Số do máy chủ lấy và tính, AI chỉ viết lời.</b> AI mà tự tính thì sai số là chuyện sớm
/// muộn — sai số trong báo cáo cho giám đốc tai hại hơn hẳn diễn đạt kém. AI lỗi hoặc hết lượt thì
/// rơi về bảng số khô (<see cref="CeoBriefBuilder.RenderFallback"/>), bản tin không bao giờ mất.</para>
///
/// <para><b>Mỗi người lấy số bằng PHIÊN CỦA CHÍNH MÌNH</b>, không dùng tài khoản tự động — cùng
/// nguyên tắc với bản tin sales: CRM tự áp quyền, lọc sai thì chỉ thiếu chứ không lộ. Hệ quả tốt
/// ngoài dự tính: người quyền hẹp đọc được đúng phần của mình thay vì số toàn công ty.</para>
///
/// <para><b>Tiết kiệm lượt AI bằng cách gộp theo BỘ SỐ, không theo tenant.</b> Cách hiển nhiên là
/// "1 lượt AI/công ty/ngày", nhưng nó chỉ đúng khi mọi người thấy cùng một bộ số — mà điều đó lại
/// không còn đúng khi mỗi người fetch bằng token riêng. Nên khoá cache theo chính bộ số: ai thấy
/// giống nhau thì dùng chung một bản (trường hợp thường gặp: mọi giám đốc → 1 lượt), ai thấy khác
/// thì được viết riêng cho đúng số của mình. Đắt hơn đúng bằng phần thực sự khác nhau.</para>
/// </summary>
public class CeoBriefWorkflow : IScheduledWorkflow
{
    private readonly DigestSubscriptionRepository _subs;
    private readonly BriefReadinessNotifier _readiness;
    private readonly TkSessionStore _sessions;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly MailQueueRepository _queue;
    private readonly TenantChannelSettingsStore _channels;
    private readonly IConfiguration _cfg;
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly AiCallContext _ctx;
    private readonly ILogger<CeoBriefWorkflow> _log;

    public CeoBriefWorkflow(BriefReadinessNotifier readiness,
        DigestSubscriptionRepository subs, TkSessionStore sessions,
        TkSessionRepository sessionRepo, TourKitApiClient api, InsightRepository insights,
        MailQueueRepository queue, TenantChannelSettingsStore channels, IConfiguration cfg, ProviderRegistry providers, AiModelRegistry models,
        AiCallContext ctx, ILogger<CeoBriefWorkflow> log)
    {
        _subs = subs; _sessions = sessions; _sessionRepo = sessionRepo; _api = api;
        _readiness = readiness;
        _insights = insights; _queue = queue; _channels = channels; _cfg = cfg; _providers = providers; _models = models; _ctx = ctx; _log = log;
    }

    public string Type => "ceo-brief";
    public string Label => "Bản tin điều hành (giám đốc)";
    public string Description => "Mỗi ngày gửi doanh thu – chi phí – lợi nhuận so cùng kỳ tháng trước, kèm biến động chính. Tốn khoảng 1 lượt AI mỗi lần gửi.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    // Có luật chung (mục nào vào bản tin, kỳ so sánh) → công ty phải khai trước khi ai đăng ký nhận.
    public bool HasCompanyRules => true;

    // ── Tuỳ chọn per-tenant ──────────────────────────────────────────────────────
    //
    // Mấy giá trị này TRƯỚC ĐÂY là hằng số trong code: luôn so cùng kỳ tháng trước, luôn in đủ 6
    // dòng, luôn gọi AI. Hằng số nghĩa là mình đoán hộ mọi công ty — mà một công ty theo mùa vụ thì
    // "so tháng trước" gần như vô nghĩa, còn công ty chưa ghi nhận chi phí vào CRM thì dòng lợi
    // nhuận chỉ tổ gây hiểu lầm.
    //
    // ⚠️ default ở đây PHẢI khớp WORKFLOW_OPTIONS['ceo-brief'] bên wwwroot/components/workflow-options.jsx,
    // lệch nhau thì giao diện hiện một đằng, hệ thống chạy một nẻo.
    private record CeoBriefOptions(
        string ComparePeriod,      // prev-month | prev-year | none
        bool SecSellers, int SellerCount,
        bool SecNewDeals, bool SecAppointments, bool SecAlerts,
        bool SecTasks, List<int> TaskStatuses,
        bool UseAi, bool ShowNumbers,
        bool SecForecast, decimal RevenueTarget);

    private static CeoBriefOptions ParseOptions(string? json)
    {
        // Mặc định = hành vi cũ y nguyên: tenant chưa khai gì thì bản tin không đổi.
        var def = new CeoBriefOptions("prev-month", SecSellers: true, SellerCount: 3,
            SecNewDeals: true, SecAppointments: true, SecAlerts: true,
            // Rỗng = rơi về enum hệ thống (1/2/3 đang mở). Công ty khai rồi thì danh sách của họ
            // quyết định — có nơi coi "Đang kiểm tra" là đã xong, chờ duyệt.
            SecTasks: true, TaskStatuses: new List<int>(),
            UseAi: true, ShowNumbers: true,
            // 0 = CHƯA khai chỉ tiêu → mục dự phóng tự tắt. Không đoán hộ: một con số bịa ra sẽ
            // khiến mọi công ty đọc một dự phóng vô nghĩa rồi mất tin vào cả bản tin.
            SecForecast: true, RevenueTarget: 0m);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;

            // Chỉ tiêu là số TIỀN nên có thể vượt int (3 tỷ vẫn lọt, nhưng 3.000 tỷ thì không).
            // Kẹp âm về 0 = coi như chưa khai, thay vì đẻ ra phần trăm âm.
            decimal Money(string k, decimal dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                   && v.TryGetDecimal(out var m) && m > 0 ? m : dv;
            bool Bit(string k, bool dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : dv;
            int Num(string k, int dv, int lo, int hi)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? Math.Clamp(n, lo, hi) : dv;

            var cmp = r.TryGetProperty("comparePeriod", out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString() : null;
            // Giá trị lạ → về mặc định, KHÔNG ném: một chuỗi rác trong cấu hình không đáng làm mất
            // bản tin của cả công ty.
            if (cmp is not ("prev-month" or "prev-year" or "none")) cmp = def.ComparePeriod;

            var taskSt = new List<int>();
            if (r.TryGetProperty("taskStatuses", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var e in ts.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) taskSt.Add(n);

            return def with
            {
                ComparePeriod = cmp!,
                TaskStatuses = taskSt,
                SecSellers = Bit("secSellers", def.SecSellers),
                SellerCount = Num("sellerCount", def.SellerCount, 1, 10),
                SecNewDeals = Bit("secNewDeals", def.SecNewDeals),
                SecAppointments = Bit("secAppointments", def.SecAppointments),
                SecAlerts = Bit("secAlerts", def.SecAlerts),
                SecTasks = Bit("secTasks", def.SecTasks),
                UseAi = Bit("useAi", def.UseAi),
                ShowNumbers = Bit("showNumbers", def.ShowNumbers),
                SecForecast = Bit("secForecast", def.SecForecast),
                RevenueTarget = Money("revenueTarget", def.RevenueTarget),
            };
        }
        catch (JsonException)
        {
            return def;
        }
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var utcNow = DateTime.UtcNow;
        var todayVn = DigestDue.NowVn(utcNow).Date;
        var opt = ParseOptions(optionsJson);

        var lead = _cfg.GetValue("Digest:LeadMinutes", 10);
        var subs = await _subs.ListEnabledAsync(tenantId, BriefTypes.Ceo, ct);
        var due = new List<DigestSubscription>();
        foreach (var s in subs)
            if (DigestDue.ShouldPrepare(s, utcNow, lead)
                && !await _insights.ExistsTodayAsync(tenantId, s.Username, BriefTypes.Ceo, todayVn, ct))
                due.Add(s);
        if (due.Count == 0)
            return new(true, "Chưa tới giờ chuẩn bị của ai (0 đăng ký đến hạn).", null);

        // Khoá theo BỘ SỐ (không theo tenant): xem ghi chú class. Chỉ sống trong 1 lượt chạy.
        var byNumbers = new Dictionary<string, DigestMessage>();
        int prepared = 0, noSession = 0, reloginFailed = 0, khongNhacDuoc = 0,
            failed = 0, skipped = 0, aiCalls = 0, aiFailed = 0;
        var parts = new List<string>();

        // Mẫu ZNS của công ty: tra MỘT LẦN cho cả lượt, không tra lại theo từng người.
        // Chưa khai thì để null — worker sẽ đánh dấu "thiếu cấu hình" chứ KHÔNG mượn mẫu khác.
        string? zaloTemplateId = null;
        if (due.Any(x => x.ChannelZalo))
        {
            var zcfg = await _channels.GetZaloAsync(tenantId, ct);
            zaloTemplateId = zcfg?.TemplateFor(BriefTypes.Ceo);
        }

        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Tự xin chìa khoá nếu chưa có phiên — người dùng KHÔNG phải làm gì. Đăng nhập
                // một chạm của TourKit chỉ cần tên công ty + tên đăng nhập, hai thứ nằm sẵn trên
                // dòng đăng ký; không hề đụng tới mật khẩu.
                var sanSang = await _readiness.TimHoacTuCapPhienAsync(sub, ct);
                if (sanSang.SessionId is null)
                {
                    // Tới đây nghĩa là CRM từ chối cấp chìa — tài khoản khoá/xoá, hoặc chưa khai
                    // khoá SSO. Cả hai đều cần người xử lý, không tự khỏi được.
                    if (sanSang.LyDo == BriefReadinessReason.ReloginFailed) reloginFailed++;
                    else noSession++;
                    if (!await _readiness.NotifyAndDisableAsync(sub, sanSang.LyDo!.Value, utcNow, ct))
                        khongNhacDuoc++;
                    continue;
                }

                string jwt;
                try { jwt = await _sessions.GetValidJwtAsync(sanSang.SessionId, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    reloginFailed++;
                    _log.LogWarning(ex, "[ceo-brief] tenant={T} user={U} chìa khoá hỏng",
                        tenantId, sub.Username);
                    if (!await _readiness.NotifyAndDisableAsync(sub, BriefReadinessReason.ReloginFailed, utcNow, ct))
                        khongNhacDuoc++;
                    continue;
                }
                var data = await FetchDataAsync(tenantId, sub.Username, jwt, todayVn, opt, ct);

                var key = Fingerprint(data);
                if (!byNumbers.TryGetValue(key, out var msg))
                {
                    msg = await ComposeAsync(tenantId, sanSang.SessionId, data, todayVn, opt, ct,
                                             onAiCall: () => aiCalls++, onAiFail: () => aiFailed++);
                    byNumbers[key] = msg;
                }

                // NỘI DUNG + KHO LƯU: bản tin ghi vào Bảng tin (in-app luôn-bật — kho lưu để
                // xem/nghe lại), Id của dòng này là nguồn nội dung cho các kênh ngoài.
                var insightId = await _insights.InsertAsync(new AgentInsight(
                    0, tenantId, sub.Username, BriefTypes.Ceo, 0,
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
                        _log.LogWarning(ex, "[ceo-brief] enqueue kênh {Ch} lỗi tenant={T} user={U}",
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
                _log.LogWarning(ex, "[ceo-brief] tenant={T} user={U} gửi lỗi", tenantId, sub.Username);
                parts.Add($"{sub.Username}[LỖI: {ex.Message}]");
            }
        }

        var summary = $"{due.Count} đăng ký đến hạn → chuẩn bị {prepared}"
                    + (noSession > 0 ? $", tạm tắt {noSession} (hết hạn phiên)" : "")
                    + (reloginFailed > 0 ? $", tạm tắt {reloginFailed} (đăng nhập lại hỏng)" : "")
                    + (khongNhacDuoc > 0 ? $", {khongNhacDuoc} không gửi nhắc được (chưa khai email)" : "")
                    + (skipped > 0 ? $", trùng {skipped}" : "")
                    + (failed > 0 ? $", lỗi {failed}" : "")
                    + $". Lượt AI: {aiCalls}"
                    + (aiFailed > 0 ? $" ({aiFailed} lỗi → dùng bảng số)" : "")
                    + (parts.Count > 0 ? ". " + string.Join(" · ", parts) : "");
        _log.LogInformation("[ceo-brief] tenant={T} {Sum}", tenantId, summary);

        try { await _insights.PruneAsync(_cfg.GetValue("Digest:InsightKeepDays", 30), ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* prune lỗi không làm hỏng lượt chạy */ }

        return new(true, summary, null);
    }

    /// <summary>
    /// Gọi AI viết lời. Lỗi/hết lượt/trả rỗng → bảng số khô. Thà đọc bảng số còn hơn sáng ra
    /// không có bản tin nào — nên hàm này CỐ Ý không bao giờ ném.
    /// </summary>
    private async Task<DigestMessage> ComposeAsync(string tenantId, string sessionId,
        CeoBriefData data, DateTime todayVn, CeoBriefOptions opt, CancellationToken ct,
        Action onAiCall, Action onAiFail)
    {
        // Công ty tắt "AI viết lời" → in thẳng bảng số, không gọi AI (không tốn lượt nào).
        if (!opt.UseAi) return CeoBriefBuilder.RenderFallback(data, todayVn);
        try
        {
            // STRICT: workflow nền không có HttpContext → thiếu Push là bypass quota tenant
            // và log feature=unknown/tenant=null (xem AiCallContext).
            using var _ = _ctx.Push(AiFeatures.Digest, tenantId, sessionId);
            onAiCall();

            // Chọn model QUA AiModelRegistry, không gọi thẳng provider mặc định: nếu để provider tự
            // chọn thì nó lấy model "Recommended" của chính nó (Sonnet) và bỏ qua Models:Primary
            // trong cấu hình — người vận hành chỉnh sang Haiku mà hoá đơn vẫn tính giá Sonnet.
            var resolved = _models.Resolve(AiFeature.Digest);
            var provider = _providers.Resolve(resolved.Provider);
            var r = await provider.CompleteAsync(new CompleteRequest(
                Prompt: CeoBriefBuilder.BuildPrompt(data, todayVn),
                Provider: resolved.Provider, Model: resolved.Model,
                // 10000: model reasoning (vd DeepSeek qua nine-routes) tiêu phần lớn hạn mức vào
                // phần NGHĨ trước khi viết. Để 1200–1400 thì nó nghĩ hết sạch, `content` trả về rỗng,
                // và bản tin gửi đi là chuỗi suy nghĩ cụt giữa chừng (đã xảy ra thật 19/08).
                MaxTokens: 10000,
                Temperature: 0.4,   // đủ tự nhiên nhưng vẫn sát số, không "văn hoa" thêm ý
                System: null,
                ApiKey: resolved.ApiKey), ct);

            // Xem chú thích cùng chốt này ở SaleBriefWorkflow: chuỗi suy nghĩ KHÔNG rỗng nên
            // IsNullOrWhiteSpace không bắt được, phải kiểm TextFromReasoning mới rơi về bảng số.
            if (string.IsNullOrWhiteSpace(r.Text) || r.TextFromReasoning)
            {
                onAiFail();
                _log.LogWarning("[ceo-brief] tenant={T} AI {Ly} → dùng bảng số", tenantId,
                    r.TextFromReasoning ? "chỉ trả chuỗi suy nghĩ (nghĩ hết hạn mức)" : "trả rỗng");
                return CeoBriefBuilder.RenderFallback(data, todayVn);
            }
            return CeoBriefBuilder.WrapAiReply(r.Text, data, todayVn);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            onAiFail();
            _log.LogWarning(ex, "[ceo-brief] tenant={T} AI lỗi → dùng bảng số", tenantId);
            return CeoBriefBuilder.RenderFallback(data, todayVn);
        }
    }

    /// <summary>
    /// Lấy số: tài chính 2 kỳ (từ đầu tháng tới hôm nay vs cùng khoảng tháng trước), top sale,
    /// cơ hội mới hôm qua, lịch hẹn hôm nay/quá hạn, cảnh báo thanh toán đang mở.
    ///
    /// <para>Mỗi nguồn bọc try riêng: nguồn lỗi thì phần đó là 0/rỗng, các số còn lại vẫn có.
    /// Bảng số luôn đính kèm dưới bài viết nên người đọc thấy ngay chỗ nào bằng 0 mà đối chiếu.</para>
    /// </summary>
    private async Task<CeoBriefData> FetchDataAsync(string tenantId, string user, string jwt,
        DateTime todayVn, CeoBriefOptions opt, CancellationToken ct)
    {
        var mtdStart = new DateTime(todayVn.Year, todayVn.Month, 1);
        bool compare = opt.ComparePeriod != "none";
        var (prevStart, prevEnd) = ComparePeriodRange(todayVn, opt.ComparePeriod);

        var thisMtd = await Fin(mtdStart, todayVn, "kỳ này");
        // Không so sánh thì KHÔNG gọi — mỗi mục tắt là bớt một lượt gọi CRM, đây là lý do chính
        // khiến các công tắc này đáng có chứ không chỉ để bản tin ngắn lại.
        var prevMtd = compare ? await Fin(prevStart, prevEnd, "kỳ trước") : new CeoNumbers(0, 0, 0);

        var sellers = new List<string>();
        if (opt.SecSellers)
        await Safe("top-sellers", async () =>
        {
            var d = await _api.GetAsync(jwt,
                $"/api/ai/top-sellers?StartDate={mtdStart:yyyy-MM-dd}&EndDate={todayVn:yyyy-MM-dd}", ct);
            foreach (var it in Items(d).Take(opt.SellerCount))
            {
                var name = Str(it, "fullName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Tự định dạng từ số THÔ thay vì lấy chuỗi *Formatted của CRM: CRM trả
                // "4,346,775,776 đ" (dấu phẩy, có khoảng trắng) còn cả bản tin dùng "4.346.775.776đ".
                // Hai kiểu số cạnh nhau trong một bảng đọc như hai nguồn khác nhau.
                var rev = Dec(it, "totalRevenue");
                sellers.Add($"{name} — {(rev > 0 ? CeoBriefBuilder.Vnd(rev) : Str(it, "totalRevenueFormatted") ?? "?")}");
            }
        });

        int newDeals = 0;
        if (opt.SecNewDeals)
        await Safe("booking-tickets", async () =>
        {
            // StartDate/EndDate của booking-tickets lọc theo NGÀY TẠO (InsDttm) — đã đối chiếu
            // BookingTicketService.SearchAsync. PageSize=1 vì chỉ cần 'total', không cần danh sách.
            var y = todayVn.AddDays(-1);
            var d = await _api.GetAsync(jwt,
                $"/api/ai/booking-tickets?StartDate={y:yyyy-MM-dd}&EndDate={y:yyyy-MM-dd}&PageIndex=1&PageSize=1", ct);
            newDeals = Int(d, "total");
        });

        int todayAppt = 0, overdueAppt = 0;
        if (opt.SecAppointments)
        await Safe("appointments", async () =>
        {
            // dateFilter: 1=hôm nay, 3=quá hạn (đối chiếu CustomerCareService).
            var d = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=1&pageIndex=1&pageSize=1", ct);
            todayAppt = Int(d, "total");
            var od = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=3&pageIndex=1&pageSize=1", ct);
            overdueAppt = Int(od, "total");
        });

        // ── Việc còn treo của cả công ty ─────────────────────────────────────────
        // CRM không có endpoint "đếm việc chưa xong", và tabFilter=0 thì gộp cả việc đã hoàn thành
        // lẫn đã hủy. Nên đếm từng trạng thái ĐANG MỞ rồi cộng lại. PageSize=1 vì chỉ cần 'total'.
        //
        // Trạng thái nào là "chưa xong" do CÔNG TY khai (mỗi nơi đặt tên một kiểu, có nơi coi
        // "Đang kiểm tra" là đã làm xong chờ duyệt). Chưa khai thì rơi về enum hệ thống 1/2/3
        // (4=Hoàn thành, 5=Hủy là đã đóng) — cùng cách CRM tự hiểu.
        //
        // Riêng "trễ hạn" đã có sẵn tabFilter=2 — SP tự loại 4/5 nên khỏi lọc tay.
        // ⚠️ Trễ hạn phải đếm trên CÙNG tập trạng thái với "chưa xong", nếu không hai số không lồng
        // nhau. Bản đầu lấy trễ hạn bằng tabFilter=2 trần — CRM ở đó chỉ loại mã 4/5 nên gom cả
        // những trạng thái công ty tự thêm ngoài 1/2/3. Kết quả trên erp.tourkit.vn: "335 việc chưa
        // hoàn thành, TRONG ĐÓ 591 việc đã quá hạn" — số con lớn hơn số tổng, nhìn là biết sai.
        //
        // CHI PHÍ: 2 lời gọi cho MỖI trạng thái được chọn (mặc định 3 → 6 lời gọi/người/ngày).
        // Mỗi lời gọi chạy SP uspSearchTasking, và SP đó tính COUNT bằng CTE cross-join nên vẫn
        // quét trọn tập đã lọc dù mình xin pageSize=1 — pageSize chỉ tiết kiệm phần truyền dữ liệu.
        // CRM KHÔNG có endpoint đếm việc theo nhóm trạng thái trong một lần gọi (đã dò: /api/tasks
        // chỉ có tab-counts theo MỐC THỜI GIAN, và bản thân nó cũng chạy 6 lần SearchAsync), nên
        // chưa gộp được. Nếu sau này upstream thêm group-by-status thì thay cả vòng lặp này bằng
        // một lời gọi.
        int openTasks = 0, lateTasks = 0;
        var openSt = opt.TaskStatuses.Count > 0 ? opt.TaskStatuses : new List<int> { 1, 2, 3 };
        if (opt.SecTasks)
        await Safe("tasks", async () =>
        {
            foreach (var st in openSt)
            {
                var d = await _api.GetAsync(jwt,
                    $"/api/ai/tasks?tabFilter=0&trangThai={st}&pageIndex=1&pageSize=1", ct);
                openTasks += Int(d, "total");

                var late = await _api.GetAsync(jwt,
                    $"/api/ai/tasks?tabFilter=2&trangThai={st}&pageIndex=1&pageSize=1", ct);
                lateTasks += Int(late, "total");
            }
        });

        int openAlerts = 0;
        if (opt.SecAlerts)
        await Safe("payment-alerts", async () =>
        {
            // companyWide để mặc định false: đếm thẻ CỦA CHÍNH người nhận, không gộp thẻ cấp
            // công ty. Thẻ cấp công ty chỉ sinh ra khi TourKit.Api chưa có trường người phụ trách;
            // lúc đó đếm gộp sẽ đưa con số của người khác vào bản tin của người này. Thà thiếu.
            openAlerts = await _insights.UnreadCountAsync(tenantId, user, ct, kind: "payment-alert");
        });

        return new CeoBriefData(thisMtd, prevMtd, sellers, newDeals, openAlerts,
            TodayAppointments: todayAppt, OverdueAppointments: overdueAppt,
            OpenTasks: openTasks, LateTasks: lateTasks,
            ShowSellers: opt.SecSellers, ShowNewDeals: opt.SecNewDeals,
            ShowAppointments: opt.SecAppointments, ShowAlerts: opt.SecAlerts,
            ShowTasks: opt.SecTasks, ShowNumbers: opt.ShowNumbers,
            ShowCompare: compare, CompareLabel: CompareLabelOf(opt.ComparePeriod),
            // Dự phóng dựa trên doanh thu TỪ ĐẦU THÁNG TỚI HÔM NAY (thisMtd) — đúng thứ phép tính
            // cần. Tắt mục hoặc chưa khai chỉ tiêu → Estimate trả null → dòng này không hiện.
            Forecast: opt.SecForecast
                ? CeoForecast.Estimate(thisMtd.Revenue, opt.RevenueTarget, todayVn)
                : null);

        async Task<CeoNumbers> Fin(DateTime s, DateTime e, string label)
        {
            var res = new CeoNumbers(0, 0, 0);
            await Safe($"financial-summary ({label})", async () =>
            {
                var d = await _api.GetAsync(jwt,
                    $"/api/ai/financial-summary?StartDate={s:yyyy-MM-dd}&EndDate={e:yyyy-MM-dd}", ct);
                res = ReadFinancial(d);
            });
            return res;
        }

        async Task Safe(string name, Func<Task> f)
        {
            try { await f(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("[ceo-brief] tenant={T} user={U} nguồn '{N}' lỗi: {Err}",
                    tenantId, user, name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Khoảng CÙNG KỲ tháng trước: từ mùng 1 tháng trước tới ngày tương ứng.
    ///
    /// <para>Phải cùng số ngày, không phải cả tháng trước — so 12 ngày đầu tháng này với 31 ngày
    /// trọn tháng trước thì tháng nào cũng ra "giảm mạnh", đọc xong hiểu sai tình hình.</para>
    ///
    /// <para>Ngày bị KẸP vào cuối tháng trước: hôm nay 31/03 thì kỳ trước là 01/02–28/02, không
    /// phải 01/02 + 30 ngày = 03/03 (tràn sang tháng 3, ăn trùng cả kỳ này).</para>
    /// </summary>
    internal static (DateTime Start, DateTime End) PrevPeriod(DateTime todayLocal)
        => ShiftPeriod(todayLocal, months: -1);

    /// <summary>
    /// Khoảng CÙNG KỲ theo lựa chọn của công ty. "none" trả về khoảng tháng trước cho có giá trị,
    /// nhưng nơi gọi sẽ không lấy số của nó.
    ///
    /// <para>Vì sao cho chọn năm trước: công ty du lịch theo mùa. So tháng 6 với tháng 5 thì mùa hè
    /// nào cũng "tăng mạnh", còn tháng 9 với tháng 8 thì năm nào cũng "giảm sâu" — đọc lên không
    /// biết được gì. So cùng kỳ năm trước mới thấy thật sự hơn hay kém.</para>
    /// </summary>
    internal static (DateTime Start, DateTime End) ComparePeriodRange(DateTime todayLocal, string mode)
        => mode == "prev-year" ? ShiftPeriod(todayLocal, months: -12) : ShiftPeriod(todayLocal, months: -1);

    /// Dời kỳ đi N tháng, giữ nguyên "từ mùng 1 tới ngày tương ứng".
    /// Ngày bị KẸP vào cuối tháng đích: hôm nay 31/03 thì kỳ trước là 01/02–28/02, không phải
    /// 01/02 + 30 ngày = 03/03 (tràn sang tháng 3, ăn trùng cả kỳ này).
    private static (DateTime Start, DateTime End) ShiftPeriod(DateTime todayLocal, int months)
    {
        var start = new DateTime(todayLocal.Year, todayLocal.Month, 1).AddMonths(months);
        var day = Math.Min(todayLocal.Day, DateTime.DaysInMonth(start.Year, start.Month));
        return (start, start.AddDays(day - 1));
    }

    internal static string CompareLabelOf(string mode) => mode switch
    {
        "prev-year" => "so cùng kỳ năm trước",
        _ => "so cùng kỳ tháng trước",
    };

    /// <summary>
    /// Đọc 3 số chính từ envelope financial-summary.
    ///
    /// <para><b>Khoá tên field theo bản thật</b> (đối chiếu <c>DashboardService.GetAiFinancialSummaryAsync</c>):
    /// <c>kpiRevenue</c> / <c>kpiTotalExpense</c> / <c>kpiGrossProfit</c>. Đoán tên (revenue/expense/profit)
    /// thì không khớp gì cả và bản tin sẽ báo 0đ khắp nơi — sai mà vẫn trông như chạy được.</para>
    ///
    /// <para>Lợi nhuận: nếu upstream không trả thì tự tính doanh thu − chi phí, để bản tin không
    /// hiện "0đ" trong khi 2 số kia có thật.</para>
    /// </summary>
    internal static CeoNumbers ReadFinancial(JsonElement d)
    {
        decimal rev = 0, exp = 0, prof = 0;
        bool hasProf = false;

        foreach (var it in Items(d))
        {
            var key = Str(it, "key");
            if (key == null) continue;
            var val = Dec(it, "value");
            switch (key)
            {
                case "kpiRevenue": rev = val; break;
                case "kpiTotalExpense": exp = val; break;
                case "kpiGrossProfit": prof = val; hasProf = true; break;
            }
        }

        if (!hasProf) prof = rev - exp;
        return new CeoNumbers(rev, exp, prof);
    }

    /// Dấu vân tay của bộ số, để biết 2 người có thấy GIỐNG NHAU không (xem ghi chú class).
    /// Chỉ gồm phần AI dùng để viết — thêm gì vào prompt thì thêm vào đây, không thì 2 bộ số khác
    /// nhau lại dùng chung một bài viết.
    private static string Fingerprint(CeoBriefData d) => string.Join("|",
        d.ThisMtd.Revenue, d.ThisMtd.Expense, d.ThisMtd.Profit,
        d.PrevMtd.Revenue, d.PrevMtd.Expense, d.PrevMtd.Profit,
        d.NewDealsYesterday, d.OpenPaymentAlerts, d.TodayAppointments, d.OverdueAppointments,
        d.OpenTasks, d.LateTasks,
        string.Join(";", d.TopSellers));

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

    private static decimal Dec(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
