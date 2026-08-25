using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Domain.Mail;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Canh thanh toán trước khởi hành. Quét tour khởi hành trong 7 ngày tới mà khách chưa trả đủ
/// → ghi cảnh báo vào Bảng tin (dedup 24h theo <c>AlertKey</c>).
///
/// <para><b>Luồng THEO TỔ CHỨC</b> nên dùng tài khoản tự động của công ty — khác bản tin cá nhân
/// (chạy bằng tài khoản của chính người nhận). Đúng vì cảnh báo này là <c>Username=''</c>,
/// tenant-wide: cả công ty cùng thấy, và cần quyền đọc toàn bộ tour.</para>
///
/// <para><b>KHÔNG gọi AI</b> → không tốn lượt, không cần <c>AiCallContext</c>.</para>
/// </summary>
public class PaymentWatchdogWorkflow : IScheduledWorkflow
{
    /// Giữ Bảng tin bao nhiêu ngày (dọn cuối mỗi lượt để bảng không phình mãi).
    private const int KeepInsightDays = 90;

    /// Tiền trên thẻ: ghim vi-VN như mọi chỗ khác của Bảng tin, không theo ngôn ngữ máy chủ.
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => TourkitAiProxy.Shared.Text.Money.So(v);

    // ── Tuỳ chọn per-tenant ──────────────────────────────────────────────────────
    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['payment-watchdog'] bên workflow-options.jsx.
    private record Options(List<int> ScanTourTypes, int WindowDays, decimal MinOutstanding, int PaymentStatus,
        int MaxReminders, bool EmailEnabled, List<string> AlertEmails);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(
            // Không khai loại thì upstream chỉ trả FIT — xem TourTypes. Trước đây tác vụ này không
            // truyền loại, nên nợ của tour GIT/LandTour/Visa chưa bao giờ được canh.
            ScanTourTypes: TourTypes.DefaultScan.ToList(),
            WindowDays: 7,
            // 0 = báo mọi khoản còn thiếu, kể cả lẻ vài nghìn do làm tròn. Để 0 làm mặc định là cố
            // ý: đây là tiền của công ty, thà thừa một dòng còn hơn tự bỏ qua hộ. Công ty nào thấy
            // nhiễu thì tự nâng lên.
            MinOutstanding: 0m,
            // 1 = dùng ĐÚNG bộ lọc "Chưa thu hết" của màn hình tìm kiếm tour (PaymentStatusSearch).
            // Quan trọng vì định nghĩa của phần mềm CHẶT hơn phép trừ doanh thu − đã thu: nó chỉ
            // tính đơn đã ghi nhận dòng tiền, phía KHÁCH (không phải phía nhà cung cấp), và bỏ
            // khách đã huỷ. Lấy theo phần mềm thì cảnh báo khớp với cái nhân viên thấy khi bấm lọc;
            // tự trừ thì có ngày lệch mà không ai biết bên nào đúng.
            PaymentStatus: 1,
            // Nhịp là 1 lần/ngày/tour (khoá chống trùng 24h), TRẦN này chặn tổng số lần. Không có
            // trần thì cửa sổ 30 ngày = 30 lần nhắc cùng một tour; tới lần thứ tư người ta không
            // đọc nữa, và cảnh báo thật lẫn vào đó.
            MaxReminders: 3,
            EmailEnabled: false,
            AlertEmails: new());
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;

            int Num(string k, int dv, int lo, int hi)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? Math.Clamp(n, lo, hi) : dv;
            List<int> Ints(string k, List<int> dv)
            {
                if (!r.TryGetProperty(k, out var arr) || arr.ValueKind != JsonValueKind.Array) return dv;
                var outp = new List<int>();
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) outp.Add(n);
                return outp.Count > 0 ? outp : dv;
            }

            return def with
            {
                ScanTourTypes = Ints("scanTourTypes", def.ScanTourTypes),
                WindowDays = Num("windowDays", def.WindowDays, 1, 60),
                MinOutstanding = Num("minOutstanding", 0, 0, 1_000_000_000),
                // Chỉ nhận 0 (tự tính) hoặc 1 (theo bộ lọc phần mềm). 2 = "đã thu hết" và 3 =
                // "chưa CHI hết" (tiền trả nhà cung cấp) đều không phải việc của tác vụ này —
                // nhận bừa thì thẻ vẫn ghi "khách còn thiếu" trong khi số là tiền mình nợ NCC.
                PaymentStatus = Num("paymentStatus", def.PaymentStatus, 0, 1),
                MaxReminders = Num("maxReminders", def.MaxReminders, 0, 30),
                EmailEnabled = r.TryGetProperty("emailEnabled", out var ev)
                    && ev.ValueKind is JsonValueKind.True or JsonValueKind.False && ev.GetBoolean(),
                AlertEmails = r.TryGetProperty("alertEmails", out var em) && em.ValueKind == JsonValueKind.String
                    ? ParseEmails(em.GetString())
                    : def.AlertEmails,
            };
        }
        catch (JsonException) { return def; }
    }

    /// Tách danh sách email người dùng gõ. Chấp cả dấu phẩy, chấm phẩy và xuống dòng — người ta
    /// hay dán từ chỗ khác sang. Bỏ chuỗi không có '@' thay vì cố sửa: xếp một địa chỉ sai vào
    /// hàng đợi chỉ tạo ra một dòng lỗi mà không ai biết là do gõ nhầm.
    internal static List<string> ParseEmails(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new()
            : raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(x => x.Trim())
                 .Where(x => x.Length > 0 && x.Contains('@'))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly Mail.MailQueueRepository _mailQueue;
    private readonly DigestSubscriptionRepository _subs;
    private readonly ILogger<PaymentWatchdogWorkflow> _log;

    public PaymentWatchdogWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, Mail.MailQueueRepository mailQueue,
        DigestSubscriptionRepository subs, ILogger<PaymentWatchdogWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _mailQueue = mailQueue; _subs = subs; _log = log; }

    public string Type => "payment-watchdog";
    public string Label => "Canh thanh toán trước khởi hành";
    public string Description => "Tour sắp khởi hành mà khách còn nợ → cảnh báo vào Bảng tin. Chọn được loại tour cần quét, số ngày trước khi đi và mức nợ đáng nhắc. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    // Loại tour cần quét là thứ mỗi công ty bán mỗi khác → phải khai, không đoán hộ.
    public bool HasCompanyRules => true;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var opt = ParseOptions(optionsJson);

        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động (trang Tự động hóa) — cần nó để đọc dữ liệu tour.");

        string jwt;
        try
        {
            var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
            jwt = await _sessions.GetValidJwtAsync(sid, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[payment-watchdog] tenant={T} đăng nhập tài khoản tự động thất bại", tenantId);
            return new(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }

        // Ngày VIỆT NAM: "7 ngày tới" phải tính theo lịch người dùng nhìn, không phải lịch UTC.
        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        var to = todayVn.AddDays(opt.WindowDays);

        // MỘT LƯỢT GỌI CHO MỖI LOẠI: upstream chỉ lọc được 1 loại/lần và mặc định là FIT.
        var failedTypes = new List<string>();
        // Lọc "chưa thu hết" NGAY Ở NGUỒN (nếu chọn) — vừa đúng định nghĩa phần mềm, vừa kéo ít
        // dòng hơn hẳn: đo trên staging 17/08, 55 tour FIT trong một năm rút còn 4.
        var items = await TourTypes.FetchByTypesAsync(_api, jwt, opt.ScanTourTypes,
            todayVn, to, 200, failedTypes, ct,
            opt.PaymentStatus == 1 ? "PaymentStatusSearch=1" : null);
        if (items.Count == 0 && failedTypes.Count == opt.ScanTourTypes.Count && failedTypes.Count > 0)
        {
            _log.LogWarning("[payment-watchdog] tenant={T} đọc danh sách tour lỗi ở mọi loại", tenantId);
            return new(false, null, "Không đọc được danh sách tour (" + string.Join(", ", failedTypes) + ").");
        }

        var rows = new List<TourPaymentRow>();
        int skipped = 0, missingPaid = 0;
        // apiHasSeller: bản /api/ai/tours ĐANG CHẠY có trả trường người phụ trách không.
        // Phải phân biệt cho bằng được với "tour này chưa gán ai":
        //   • thiếu HẲN thuộc tính  → API cũ chưa nâng cấp → giữ hành vi cũ (ghi cả công ty),
        //     nếu không thì ngày deploy proxy trước API là CẢ HAI tác vụ im lặng hoàn toàn.
        //   • có thuộc tính, giá trị rỗng → tour thật sự chưa gán → bỏ qua + đếm.
        bool apiHasSeller = false;
        foreach (var it in items)
        {
            // Thiếu id hoặc ngày khởi hành thì không xét được — bỏ dòng đó, KHÔNG ném,
            // vì một dòng dữ liệu bẩn không đáng làm hỏng cả lượt quét.
            if (!TryGetInt(it, "id", out var id)) { skipped++; continue; }
            if (!TryGetDate(it, "departureDate", out var dep)) { skipped++; continue; }

            // BẮT BUỘC có actualRevenue. Bản /api/ai/tours cũ KHÔNG trả field này → nếu cứ
            // coi thiếu = 0 thì "còn nợ" = trọn doanh thu, tức mọi tour đều bị báo nợ toàn bộ
            // (đã xảy ra thật 12/08). Thà bỏ qua và nói rõ trong summary còn hơn báo số sai.
            if (!TryGetDec(it, "actualRevenue", out var actual)) { missingPaid++; continue; }

            // sellerSource là trường "chứng nhận API đã nâng cấp": nó LUÔN có mặt ở bản mới (giá
            // trị "tour" hoặc null), và KHÔNG có ở bản cũ. Dùng nó thay vì sellerUserName vì
            // sellerUserName rỗng là trạng thái hợp lệ (tour ghép chưa gán ai).
            if (it.TryGetProperty("sellerSource", out _)) apiHasSeller = true;

            rows.Add(new TourPaymentRow(id,
                GetStr(it, "title") ?? GetStr(it, "tourCode") ?? $"Tour #{id}",
                GetStr(it, "customerName"), GetStr(it, "sellerName"),
                dep, GetDec(it, "revenue"), actual,
                SellerUserName: GetStr(it, "sellerUserName")));
        }

        var alerts = PaymentWatchdogRule.Evaluate(rows, todayVn, opt.WindowDays, opt.MinOutstanding);
        int created = 0, deduped = 0, capped = 0, noOwner = 0;

        // Đã nhắc mấy lần rồi? Hỏi MỘT LƯỢT cho cả danh sách, không hỏi từng tour.
        var counts = opt.MaxReminders > 0
            ? await _insights.CountByAlertKeysAsync(tenantId, alerts.Select(a => a.AlertKey).ToList(), ct)
            : new Dictionary<string, int>();

        var sentNow = new List<PaymentAlert>();
        foreach (var a in alerts)
        {
            ct.ThrowIfCancellationRequested();

            if (opt.MaxReminders > 0 && counts.TryGetValue(a.AlertKey, out var already)
                && already >= opt.MaxReminders) { capped++; continue; }

            // ── Cảnh báo này là việc CỦA AI ────────────────────────────────────────────
            // Không có người phụ trách thì BỎ QUA, tuyệt đối KHÔNG rơi về mức cả công ty.
            // Rơi về công ty là tái tạo đúng cái đang sửa: cảnh báo ai cũng thấy = không ai chịu
            // trách nhiệm. Và chỗ thiếu người phụ trách KHÔNG rải đều — nó dồn vào TOUR GHÉP
            // (đo staging 08/2026: GIT thiếu 90%, LandTour 95%; còn Dịch vụ lẻ/Booking/Visa/Vé
            // máy bay thiếu 0%). Một chuyến GIT là sản phẩm chung, nhiều người cùng bán — gán cho
            // một người thì gán ai cũng sai, mà đổ hết vào Bảng tin chung thì 746 tour GIT nhấn
            // chìm những cảnh báo có chủ thật.
            //
            // Bỏ qua nhưng KHÔNG im lặng: đếm rồi ghi vào tóm tắt lần chạy, để người bật tác vụ
            // biết vùng mù thay vì tưởng đã canh hết.
            //
            // apiHasSeller=false nghĩa là API chưa nâng cấp, chưa có căn cứ để chia → giữ hành vi
            // cũ (cả công ty) chứ không bỏ qua sạch. Ba trạng thái ở PaymentWatchdogRule.ResolveOwner.
            var (owner, skipNoOwner) = PaymentWatchdogRule.ResolveOwner(a.SellerUserName, apiHasSeller);
            if (skipNoOwner) { noOwner++; continue; }
            // Vnd() chứ không phải {:N0} trần: máy chạy en-US in "7,350,000đ" trong khi thẻ ngay
            // bên cạnh (tour-readiness) in "7.350.000đ" — hai kiểu số cạnh nhau trong cùng Bảng tin.
            var body = $"**{a.Title}** — khách {a.CustomerName ?? "?"} còn thiếu **{Vnd(a.Outstanding)}đ**, "
                     + $"khởi hành {a.DepartureDate:dd/MM} (còn {a.DaysLeft} ngày). Phụ trách: {a.SellerName ?? "?"}.";

            var id = await _insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: tenantId,
                // Đích danh NV phụ trách. Rỗng chỉ xảy ra khi API chưa nâng cấp (giữ hành vi cũ:
                // cả công ty cùng thấy) — tour đã gán ai thì chỉ người đó thấy.
                Username: owner,
                Kind: "payment-alert", Severity: a.Severity,
                Title: $"Thu nốt tiền tour trước khởi hành (còn {a.DaysLeft} ngày)",
                Body: body,
                DataJson: JsonSerializer.Serialize(new { a.TourId, a.Outstanding, a.DaysLeft }),
                AlertKey: a.AlertKey,               // dedup 24h — chạy mỗi giờ nhưng chỉ nhắc 1 lần/ngày
                IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

            if (id == null) deduped++; else { created++; sentNow.Add(a); }
        }

        await _insights.PruneAsync(KeepInsightDays, ct);

        // ── Email: MỖI NGƯỜI PHỤ TRÁCH MỘT THƯ GỘP ────────────────────────────────────
        // Vẫn gộp theo lượt quét (không phải mỗi tour một thư): hộp thư sáng ra 20 thư cùng tiêu
        // đề thì chỉ cái đầu được đọc. Nhưng người nhận nay theo ĐÚNG luật của Bảng tin — xem
        // PaymentWatchdogRule.PlanMails để biết vì sao đổi (20/08/2026).
        // Chỉ gửi tour VỪA sinh cảnh báo mới (sentNow) — tour bị chặn vì trùng ngày hoặc đủ số lần
        // nhắc thì thư cũng không được nhắc lại.
        int queued = 0;
        string? mailNote = null;
        var noMailOwners = new List<string>();
        if (opt.EmailEnabled && sentNow.Count > 0)
        {
            // Email lấy từ hồ sơ dùng chung ("Nơi nhận của tôi") để nhân viên chỉ khai MỘT LẦN cho
            // mọi thông báo. KHÔNG lọc theo `Enabled` của bản tin: đó là đăng ký bản tin sáng, một
            // người có thể không nhận bản tin nhưng vẫn phải nhận cảnh báo tiền của tour mình bán.
            var emailOfUser = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var profiles = await _subs.ListWithChannelsAsync(tenantId, ct);
                foreach (var p in profiles)
                    if (p.ChannelEmail && !string.IsNullOrWhiteSpace(p.Email)
                        && !string.IsNullOrWhiteSpace(p.Username))
                        emailOfUser[p.Username.Trim()] = p.Email.Trim();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[payment-watchdog] tenant={T} đọc nơi nhận dùng chung lỗi", tenantId);
            }

            var plan = PaymentWatchdogRule.PlanMails(sentNow, apiHasSeller, emailOfUser, opt.AlertEmails);
            noMailOwners = plan.OwnersWithoutEmail;

            if (plan.Mails.Count == 0)
                mailNote = "Đã bật gửi email nhưng không gửi được cho ai: người phụ trách chưa khai "
                         + "email ở khối \"Nơi nhận của tôi\", và cũng chưa khai địa chỉ nào ở đây";
            else
            {
                foreach (var m in plan.Mails)
                {
                    var mail = PaymentAlertMail.Build(m.Alerts, todayVn);
                    try
                    {
                        await _mailQueue.EnqueueAsync(new OutboundMailInput(
                            TenantId: tenantId,
                            Kind: "payment-alert",
                            // Một dòng cho mỗi địa chỉ mỗi ngày → dễ đối soát khi ai đó bảo
                            // "hôm nay tôi không nhận được thư".
                            SourceId: $"payment-alert:{todayVn:yyyy-MM-dd}:{m.ToEmail}",
                            TemplateCode: "payment-alert",
                            ToEmail: m.ToEmail,
                            Subject: mail.Subject,
                            Params: mail.ParamsJson), ct);
                        queued++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[payment-watchdog] tenant={T} xếp thư cho {Addr} lỗi", tenantId, m.ToEmail);
                    }
                }
            }
        }

        var scanned = string.Join(" + ", opt.ScanTourTypes.Select(TourTypes.Name));
        var nguon = opt.PaymentStatus == 1 ? "theo bộ lọc phần mềm" : "tự tính";
        var summary = $"Quét {rows.Count} tour ({scanned}) khởi hành trong {opt.WindowDays} ngày tới → {alerts.Count} còn nợ ({nguon}) "
                    + $"({created} cảnh báo mới, {deduped} đã báo hôm nay"
                    + (capped > 0 ? $", {capped} đã đủ {opt.MaxReminders} lần nhắc nên dừng" : "")
                    // Nói ra vùng mù. Bỏ qua im lặng thì người bật tác vụ tưởng đã canh hết, mà
                    // thường đây là tour ghép — loại đúng ra phải giao cho điều hành, không phải sale.
                    + (noOwner > 0 ? $", BỎ QUA {noOwner} tour chưa gán người phụ trách" : "") + ")"
                    + (queued > 0 ? $", xếp {queued} thư chờ gửi (mỗi người phụ trách 1 thư)" : "")
                    // Không im lặng: người phụ trách chưa khai email thì cảnh báo của họ CHỈ nằm
                    // trên Bảng tin. Không nói ra thì người bật tác vụ tưởng ai cũng đã nhận thư.
                    + (noMailOwners.Count > 0
                        ? $", {noMailOwners.Count} người phụ trách chưa khai email nên chỉ hiện ở Bảng tin"
                        : "")
                    + (mailNote != null ? $". {mailNote}" : "")
                    + (skipped > 0 ? $", bỏ qua {skipped} dòng thiếu dữ liệu" : "")
                    + (failedTypes.Count > 0 ? $". Không đọc được loại: {string.Join(", ", failedTypes)}" : "")
                    + (missingPaid > 0 ? $". CẢNH BÁO: {missingPaid} tour không có số thực thu — TourKit.Api cần bản có field actualRevenue, tạm bỏ qua để không báo sai" : "") + ".";
        _log.LogInformation("[payment-watchdog] tenant={T} {Sum}", tenantId, summary);
        return new(true, summary, null);
    }

    // ── Đọc JSON: envelope /api/ai/* serialize camelCase ──────────────────────
    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal GetDec(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    /// Khác GetDec: PHÂN BIỆT "không có field" với "có và bằng 0". Với số tiền đã trả, hai thứ đó
    /// khác nhau hoàn toàn — thiếu field mà coi là 0 thì mọi tour thành nợ trọn doanh thu.
    private static bool TryGetDec(JsonElement e, string name, out decimal val)
    {
        val = 0m;
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetDecimal(); return true; }
        return false;
    }

    private static bool TryGetInt(JsonElement e, string name, out int val)
    {
        val = 0;
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetInt32(); return true; }
        return false;
    }

    /// AssumeUniversal|AdjustToUniversal: TryParse trần trả Kind=Local → lệch múi giờ khi so ngày.
    private static bool TryGetDate(JsonElement e, string name, out DateTime val)
    {
        val = default;
        var s = GetStr(e, name);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out val);
    }
}
