using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

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
    public string Description => "Tour sắp đi chạm mốc D-7 / D-3 / D-1 mà còn thiếu tiền, thiếu khách hoặc cần hồ sơ visa → nhắc điều hành. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    // Có luật chung (mốc kiểm, ngưỡng khách tối thiểu, loại tour cần visa) → công ty phải khai trước.
    public bool HasCompanyRules => true;

    // ── Tuỳ chọn per-tenant ──────────────────────────────────────────────────────
    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['tour-readiness'] bên workflow-options.jsx.
    private record Options(
        List<int> Milestones,
        bool CheckPayment, bool CheckSeats, bool CheckVisa,
        int MinSeats, List<int> VisaTourTypes);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(
            Milestones: new() { 7, 3, 1 },
            CheckPayment: true, CheckSeats: true, CheckVisa: true,
            // 0 = CHƯA khai → không kiểm chỗ ngồi. Đoán hộ một ngưỡng ở đây là báo nhầm hàng loạt:
            // công ty chạy tour lẻ 2 khách sẽ thấy mọi tour đều "thiếu khách".
            MinSeats: 0,
            VisaTourTypes: new() { 102 });
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
                Milestones = Ints("milestones", def.Milestones),
                CheckPayment = Bit("checkPayment", def.CheckPayment),
                CheckSeats = Bit("checkSeats", def.CheckSeats),
                CheckVisa = Bit("checkVisa", def.CheckVisa),
                MinSeats = Num("minSeats", def.MinSeats, 0, 200),
                VisaTourTypes = Ints("visaTourTypes", def.VisaTourTypes),
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
        var window = opt.Milestones.Count > 0 ? opt.Milestones.Max() : 7;
        var to = todayVn.AddDays(window);

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt,
                $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[tour-readiness] tenant={T} đọc danh sách tour lỗi", tenantId);
            return new(false, null, $"Không đọc được danh sách tour: {ex.Message}");
        }

        var rows = new List<TourReadinessRow>();
        int skipped = 0, missingPaid = 0;
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetInt(it, "id", out var id)) { skipped++; continue; }
                if (!TryGetDate(it, "departureDate", out var dep)) { skipped++; continue; }

                // Thiếu actualRevenue mà coi là 0 thì mọi tour thành "nợ trọn doanh thu" (lỗi thật
                // 12/08 của O2). Ở đây nhẹ hơn: vẫn giữ dòng để còn kiểm khách/visa, chỉ TẮT phần
                // tiền cho riêng dòng đó bằng cách cho ActualRevenue = Revenue.
                var hasPaid = TryGetDec(it, "actualRevenue", out var actual);
                var revenue = GetDec(it, "revenue");
                if (!hasPaid) { missingPaid++; actual = revenue; }

                rows.Add(new TourReadinessRow(id,
                    GetStr(it, "title") ?? GetStr(it, "tourCode") ?? $"Tour #{id}",
                    GetStr(it, "customerName"), GetStr(it, "sellerName"),
                    dep, revenue, actual,
                    GetInt(it, "slots"), GetInt(it, "booked"),
                    GetInt(it, "tourType"), GetStr(it, "tourTypeLabel")));
            }
        }

        var cards = TourReadinessRule.Evaluate(rows, todayVn, opt.Milestones,
            opt.CheckPayment, opt.CheckSeats, opt.CheckVisa, opt.MinSeats, opt.VisaTourTypes);

        int created = 0, deduped = 0;
        foreach (var c in cards)
        {
            ct.ThrowIfCancellationRequested();
            var lines = string.Join("\n", c.Issues.Select(i => $"- {i.Text}"));
            var body = $"**{c.Title}** — khách {c.CustomerName ?? "?"}, khởi hành {c.DepartureDate:dd/MM} "
                     + $"(còn {c.DaysLeft} ngày). Phụ trách: {c.SellerName ?? "?"}.\n\nCòn thiếu:\n{lines}";

            var id = await _insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: tenantId,
                Username: "",                       // tenant-wide: cả công ty cùng thấy
                Kind: "tour-readiness", Severity: c.Severity,
                Title: $"Tour đi trong {c.DaysLeft} ngày — còn {c.Issues.Count} việc chưa xong",
                Body: body,
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

        var summary = $"Quét {rows.Count} tour trong {window} ngày tới → {cards.Count} tour còn thiếu điều kiện "
                    + $"({created} thẻ mới, {deduped} đã nhắc ở mốc này)"
                    + (skipped > 0 ? $", bỏ qua {skipped} dòng thiếu dữ liệu" : "")
                    + (missingPaid > 0 ? $". Lưu ý: {missingPaid} tour không có số thực thu nên bỏ phần kiểm tiền cho những tour đó" : "")
                    + (opt.CheckSeats && opt.MinSeats == 0 ? ". Chưa khai số khách tối thiểu nên chưa kiểm phần khách" : "")
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
