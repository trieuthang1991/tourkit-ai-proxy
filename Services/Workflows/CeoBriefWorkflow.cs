using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;

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
    private readonly TkSessionStore _sessions;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly MailQueueRepository _queue;
    private readonly IConfiguration _cfg;
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly AiCallContext _ctx;
    private readonly ILogger<CeoBriefWorkflow> _log;

    public CeoBriefWorkflow(DigestSubscriptionRepository subs, TkSessionStore sessions,
        TkSessionRepository sessionRepo, TourKitApiClient api, InsightRepository insights,
        MailQueueRepository queue, IConfiguration cfg, ProviderRegistry providers, AiModelRegistry models,
        AiCallContext ctx, ILogger<CeoBriefWorkflow> log)
    {
        _subs = subs; _sessions = sessions; _sessionRepo = sessionRepo; _api = api;
        _insights = insights; _queue = queue; _cfg = cfg; _providers = providers; _models = models; _ctx = ctx; _log = log;
    }

    public string Type => "ceo-brief";
    public string Label => "Bản tin điều hành (giám đốc)";
    public string Description => "Mỗi sáng gửi doanh thu – chi phí – lợi nhuận so cùng kỳ tháng trước, kèm biến động chính. Tốn khoảng 1 lượt AI mỗi lần gửi.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var utcNow = DateTime.UtcNow;
        var todayVn = DigestDue.NowVn(utcNow).Date;

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
        int prepared = 0, noSession = 0, failed = 0, skipped = 0, aiCalls = 0, aiFailed = 0;
        var parts = new List<string>();

        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var session = await _sessionRepo.GetByUserAsync(tenantId, sub.Username, ct);
                if (session == null)
                {
                    noSession++;
                    _log.LogInformation("[ceo-brief] tenant={T} user={U} chưa đăng nhập lần nào — bỏ qua",
                        tenantId, sub.Username);
                    continue;
                }

                var jwt = await _sessions.GetValidJwtAsync(session.Id, ct);
                var data = await FetchDataAsync(tenantId, sub.Username, jwt, todayVn, ct);

                var key = Fingerprint(data);
                if (!byNumbers.TryGetValue(key, out var msg))
                {
                    msg = await ComposeAsync(tenantId, session.Id, data, todayVn, ct,
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
                    todayVn.ToString("dd/MM/yyyy"));
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
                    + (noSession > 0 ? $", bỏ qua {noSession} (chưa đăng nhập)" : "")
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
        CeoBriefData data, DateTime todayVn, CancellationToken ct, Action onAiCall, Action onAiFail)
    {
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
                MaxTokens: 1200,
                Temperature: 0.4,   // đủ tự nhiên nhưng vẫn sát số, không "văn hoa" thêm ý
                System: null,
                ApiKey: resolved.ApiKey), ct);

            if (string.IsNullOrWhiteSpace(r.Text))
            {
                onAiFail();
                _log.LogWarning("[ceo-brief] tenant={T} AI trả rỗng → dùng bảng số", tenantId);
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
        DateTime todayVn, CancellationToken ct)
    {
        var mtdStart = new DateTime(todayVn.Year, todayVn.Month, 1);
        var (prevStart, prevEnd) = PrevPeriod(todayVn);

        var thisMtd = await Fin(mtdStart, todayVn, "kỳ này");
        var prevMtd = await Fin(prevStart, prevEnd, "kỳ trước");

        var sellers = new List<string>();
        await Safe("top-sellers", async () =>
        {
            var d = await _api.GetAsync(jwt,
                $"/api/ai/top-sellers?StartDate={mtdStart:yyyy-MM-dd}&EndDate={todayVn:yyyy-MM-dd}", ct);
            foreach (var it in Items(d).Take(3))
            {
                var name = Str(it, "fullName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                sellers.Add($"{name} — {Str(it, "totalRevenueFormatted") ?? "?"}");
            }
        });

        int newDeals = 0;
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
        await Safe("appointments", async () =>
        {
            // dateFilter: 1=hôm nay, 3=quá hạn (đối chiếu CustomerCareService).
            var d = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=1&pageIndex=1&pageSize=1", ct);
            todayAppt = Int(d, "total");
            var od = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=3&pageIndex=1&pageSize=1", ct);
            overdueAppt = Int(od, "total");
        });

        int openAlerts = 0;
        await Safe("payment-alerts", async () =>
        {
            openAlerts = await _insights.UnreadCountAsync(tenantId, user, ct, kind: "payment-alert");
        });

        return new CeoBriefData(thisMtd, prevMtd, sellers, newDeals, openAlerts,
            TodayAppointments: todayAppt, OverdueAppointments: overdueAppt);

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
    {
        var prevStart = new DateTime(todayLocal.Year, todayLocal.Month, 1).AddMonths(-1);
        var day = Math.Min(todayLocal.Day, DateTime.DaysInMonth(prevStart.Year, prevStart.Month));
        return (prevStart, prevStart.AddDays(day - 1));
    }

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
