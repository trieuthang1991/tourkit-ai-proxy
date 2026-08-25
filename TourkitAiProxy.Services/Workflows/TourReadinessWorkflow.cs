using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Infrastructure.Digest;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Kiểm tra sẵn sàng khởi hành (O1). Tour chạm mốc D-7 / D-3 / D-1 mà còn thiếu điều kiện →
/// dựng một thẻ liệt kê ĐÚNG những gì còn thiếu, ghi vào Bảng tin (dedup theo mốc).
///
/// <para><b>Khác "Canh thanh toán" (O2) thế nào:</b> O2 chỉ hỏi một câu — thu đủ tiền chưa — và
/// hỏi mỗi ngày. O1 gom NHIỀU điều kiện vào một thẻ và chỉ nói đúng 3 lần: một lần còn kịp xoay
/// (D-7), một lần cảnh báo (D-3), một lần chốt cuối (D-1). Hai cái bổ sung nhau: ai chỉ cần canh
/// tiền thì bật O2, ai lo cả chuyến đi thì bật O1. Bật cả hai sẽ có phần trùng về tiền — thẻ O1
/// ghi rõ tổng thể nên đọc nó là đủ.</para>
///
/// <para><b>KHÔNG gọi AI</b> — luật thuần, không tốn lượt. Số do CRM trả, không suy diễn.</para>
///
/// <para>Dùng <b>tài khoản tự động</b> của công ty vì đây là cảnh báo tenant-wide
/// (<c>Username=''</c>) và cần quyền đọc toàn bộ tour.</para>
/// </summary>
public class TourReadinessWorkflow : IScheduledWorkflow
{
    /// Giữ Bảng tin bao nhiêu ngày (dọn cuối mỗi lượt để bảng không phình mãi).
    private const int KeepInsightDays = 90;

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly ILogger<TourReadinessWorkflow> _log;

    public TourReadinessWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, ILogger<TourReadinessWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _log = log; }

    public string Type => "tour-readiness";
    public string Label => "Kiểm tra sẵn sàng khởi hành";
    public string Description => "Tour sắp đi mà còn thiếu tiền, thiếu khách hoặc cần hồ sơ visa → nhắc điều hành ở mốc D-7 / D-3 / D-1. Phần chỗ ngồi soát sớm hơn (D-21 / D-14 / D-7) và báo cả tour sắp đầy để đẩy bán nốt. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    // Có luật chung (mốc kiểm, ngưỡng khách tối thiểu, loại tour cần visa) → công ty phải khai trước.
    public bool HasCompanyRules => true;

    // ── Tuỳ chọn per-tenant ──────────────────────────────────────────────────────
    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['tour-readiness'] bên workflow-options.jsx.
    private record Options(
        List<int> ScanTourTypes,
        List<int> Milestones,
        bool CheckPayment, bool CheckSeats, bool CheckVisa,
        int MinSeats, List<int> VisaTourTypes,
        bool CheckNearlyFull, int NearlyFullPercent, List<int> CapacityMilestones);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(
            // Không khai loại thì upstream chỉ trả FIT — xem TourTypes. Vì thế đây là tuỳ chọn
            // quyết định tác vụ NHÌN THẤY GÌ, không phải một bộ lọc phụ.
            ScanTourTypes: TourTypes.DefaultScan.ToList(),
            Milestones: new() { 7, 3, 1 },
            CheckPayment: true, CheckSeats: true, CheckVisa: true,
            // 0 = CHƯA khai → không kiểm chỗ ngồi. Đoán hộ một ngưỡng ở đây là báo nhầm hàng loạt:
            // công ty chạy tour lẻ 2 khách sẽ thấy mọi tour đều "thiếu khách".
            MinSeats: 0,
            VisaTourTypes: new() { 102 },
            CheckNearlyFull: true,
            NearlyFullPercent: 80,
            // Xa hơn mốc tiền/visa: bán nốt chỗ cuối mà tới D-7 mới nói thì đã hết đường xoay.
            CapacityMilestones: new() { 21, 14, 7 });
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;

            bool Bit(string k, bool dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : dv;
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
                Milestones = Ints("milestones", def.Milestones),
                CheckPayment = Bit("checkPayment", def.CheckPayment),
                CheckSeats = Bit("checkSeats", def.CheckSeats),
                CheckVisa = Bit("checkVisa", def.CheckVisa),
                MinSeats = Num("minSeats", def.MinSeats, 0, 200),
                VisaTourTypes = Ints("visaTourTypes", def.VisaTourTypes),
                CheckNearlyFull = Bit("checkNearlyFull", def.CheckNearlyFull),
                // Sàn 50%: dưới mức đó thì "sắp đầy" mất nghĩa, tour nào cũng bị nhắc.
                NearlyFullPercent = Num("nearlyFullPercent", def.NearlyFullPercent, 50, 100),
                CapacityMilestones = Ints("capacityMilestones", def.CapacityMilestones),
            };
        }
        catch (JsonException) { return def; }
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var opt = ParseOptions(optionsJson);
        if (!opt.CheckPayment && !opt.CheckSeats && !opt.CheckVisa)
            return new(true, "Đã tắt cả 3 nhóm kiểm — không có gì để quét.", null);

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
            _log.LogWarning(ex, "[tour-readiness] tenant={T} đăng nhập tài khoản tự động thất bại", tenantId);
            return new(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }

        // Ngày VIỆT NAM: "7 ngày tới" phải tính theo lịch người dùng nhìn, không phải lịch UTC.
        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        // Cửa sổ LẤY dữ liệu phải phủ mốc XA NHẤT của CẢ HAI nhóm. Lấy mỗi mốc tiền/visa (7 ngày)
        // thì tour ở D-14/D-21 không bao giờ được kéo về, và phần canh chỗ ngồi im lặng không báo
        // gì — hỏng kiểu tệ nhất: nhìn như đang chạy bình thường.
        var window = Math.Max(
            opt.Milestones.Count > 0 ? opt.Milestones.Max() : 7,
            opt.CapacityMilestones.Count > 0 ? opt.CapacityMilestones.Max() : 0);
        var to = todayVn.AddDays(window);

        // MỘT LƯỢT GỌI CHO MỖI LOẠI: upstream chỉ lọc được 1 loại/lần và mặc định là FIT.
        var failedTypes = new List<string>();
        var items = await TourTypes.FetchByTypesAsync(_api, jwt, opt.ScanTourTypes,
            todayVn, to, 200, failedTypes, ct);
        if (items.Count == 0 && failedTypes.Count == opt.ScanTourTypes.Count && failedTypes.Count > 0)
        {
            _log.LogWarning("[tour-readiness] tenant={T} đọc danh sách tour lỗi ở mọi loại", tenantId);
            return new(false, null, "Không đọc được danh sách tour (" + string.Join(", ", failedTypes) + ").");
        }

        var rows = new List<TourReadinessRow>();
        int skipped = 0, missingPaid = 0;
        // Xem PaymentWatchdogRule.ResolveOwner: phải phân biệt "API chưa nâng cấp" (thiếu HẲN
        // thuộc tính sellerSource → giữ hành vi cũ) với "tour chưa gán ai" (có thuộc tính, rỗng
        // → bỏ qua). Gộp hai cái là hôm deploy proxy trước API thì tác vụ im lặng hoàn toàn.
        bool apiHasSeller = false;
        foreach (var it in items)
        {
            if (!TryGetInt(it, "id", out var id)) { skipped++; continue; }
            if (!TryGetDate(it, "departureDate", out var dep)) { skipped++; continue; }

            // Thiếu actualRevenue mà coi là 0 thì mọi tour thành "nợ trọn doanh thu" (lỗi thật
            // 12/08 của O2). Ở đây nhẹ hơn: vẫn giữ dòng để còn kiểm khách/visa, chỉ TẮT phần
            // tiền cho riêng dòng đó bằng cách cho ActualRevenue = Revenue.
            var hasPaid = TryGetDec(it, "actualRevenue", out var actual);
            var revenue = GetDec(it, "revenue");
            if (!hasPaid) { missingPaid++; actual = revenue; }

            if (it.TryGetProperty("sellerSource", out _)) apiHasSeller = true;

            rows.Add(new TourReadinessRow(id,
                GetStr(it, "title") ?? GetStr(it, "tourCode") ?? $"Tour #{id}",
                GetStr(it, "customerName"), GetStr(it, "sellerName"),
                dep, revenue, actual,
                GetInt(it, "slots"), GetInt(it, "booked"),
                GetInt(it, "tourType"), GetStr(it, "tourTypeLabel"),
                GetInt(it, "onHold"),
                SellerUserName: GetStr(it, "sellerUserName")));
        }

        var cards = TourReadinessRule.Evaluate(rows, todayVn, opt.Milestones,
            opt.CheckPayment, opt.CheckSeats, opt.CheckVisa, opt.MinSeats, opt.VisaTourTypes,
            opt.CheckNearlyFull, opt.NearlyFullPercent, opt.CapacityMilestones);

        int created = 0, deduped = 0, noOwner = 0;
        foreach (var c in cards)
        {
            ct.ThrowIfCancellationRequested();

            // Không biết gửi cho ai thì BỎ QUA, không rơi về mức công ty (chốt 18/08).
            // ⚠️ Ở tác vụ này cái giá cao hơn hẳn canh thanh toán: phần kiểm số chỗ (đủ khách tối
            // thiểu, sắp đầy chỗ) CHỈ chạy trên tour ghép, mà tour ghép lại đúng là loại thiếu
            // người phụ trách ~90% (đo staging 08/2026: GIT 746/830, LandTour 331/350). Nên đừng
            // ngạc nhiên khi số thẻ tụt mạnh — đó là chủ ý, không phải hỏng. Tour ghép nhiều người
            // cùng bán nên gán cho một người thì gán ai cũng sai.
            var (owner, skipNoOwner) = PaymentWatchdogRule.ResolveOwner(c.SellerUserName, apiHasSeller);
            if (skipNoOwner) { noOwner++; continue; }
            // Chữ trên thẻ tách sang TourReadinessCardText — phần đó KHÔNG kiểm được bằng chạy
            // thật (tenant thử nghiệm không có tour nào khai số chỗ), nên phải test riêng.
            var text = TourReadinessCardText.Build(c);
            var problems = c.Issues.Where(i => !TourReadinessRule.OpportunityCodes.Contains(i.Code)).ToList();

            var id = await _insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: tenantId,
                // Đích danh NV phụ trách; rỗng chỉ khi API chưa nâng cấp (giữ hành vi cũ).
                Username: owner,
                Kind: "tour-readiness", Severity: c.Severity,
                Title: text.Title,
                Body: text.Body,
                DataJson: JsonSerializer.Serialize(new
                {
                    c.TourId, c.DaysLeft, c.Milestone,
                    issues = c.Issues.Select(i => i.Code).ToArray(),
                }),
                AlertKey: c.AlertKey,               // kèm mốc: D-7 và D-3 là hai lời nhắc khác nhau
                IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

            if (id == null) deduped++; else created++;
        }

        await _insights.PruneAsync(KeepInsightDays, ct);

        // "còn thiếu điều kiện" nay không còn đúng: thẻ có thể chỉ mang tin vui (tour sắp đầy).
        var scanned = string.Join(" + ", opt.ScanTourTypes.Select(TourTypes.Name));
        var summary = $"Quét {rows.Count} tour ({scanned}) trong {window} ngày tới → {cards.Count} tour cần chú ý "
                    + $"({created} thẻ mới, {deduped} đã nhắc ở mốc này"
                    + (noOwner > 0 ? $", BỎ QUA {noOwner} tour chưa gán người phụ trách" : "") + ")"
                    + (skipped > 0 ? $", bỏ qua {skipped} dòng thiếu dữ liệu" : "")
                    + (failedTypes.Count > 0 ? $". Không đọc được loại: {string.Join(", ", failedTypes)}" : "")
                    + (missingPaid > 0 ? $". Lưu ý: {missingPaid} tour không có số thực thu nên bỏ phần kiểm tiền cho những tour đó" : "")
                    + (opt.CheckSeats && opt.MinSeats == 0 ? ". Chưa khai số khách tối thiểu nên chưa kiểm phần khách" : "")
                    // Cấu hình tự mâu thuẫn thì phải NÓI RA: bật kiểm visa mà không quét loại nào
                    // được coi là hồ sơ visa thì phần đó im lặng không chạy — đúng lỗi đã tồn tại
                    // suốt vì tác vụ chỉ kéo FIT.
                    + (opt.CheckVisa && !opt.VisaTourTypes.Any(v => opt.ScanTourTypes.Contains(v))
                        ? $". CẢNH BÁO: đang bật kiểm visa nhưng không quét loại nào tính là hồ sơ visa ({string.Join(", ", opt.VisaTourTypes.Select(TourTypes.Name))}) — phần visa không chạy"
                        : "")
                    + ".";
        _log.LogInformation("[tour-readiness] tenant={T} {Sum}", tenantId, summary);
        return new(true, summary, null);
    }

    // ── Đọc JSON: envelope /api/ai/* serialize camelCase ──────────────────────
    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal GetDec(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// Khác GetDec: PHÂN BIỆT "không có field" với "có và bằng 0".
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
