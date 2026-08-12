using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

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
    /// Cửa sổ quét: tour khởi hành trong bao nhiêu ngày tới.
    private const int WindowDays = 7;
    /// Giữ Bảng tin bao nhiêu ngày (dọn cuối mỗi lượt để bảng không phình mãi).
    private const int KeepInsightDays = 90;

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly ILogger<PaymentWatchdogWorkflow> _log;

    public PaymentWatchdogWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, ILogger<PaymentWatchdogWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _log = log; }

    public string Type => "payment-watchdog";
    public string Label => "Canh thanh toán trước khởi hành";
    public string Description => "Tour sắp khởi hành (7 ngày) mà khách còn nợ → cảnh báo vào Bảng tin. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

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
        var to = todayVn.AddDays(WindowDays);

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt,
                $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[payment-watchdog] tenant={T} đọc danh sách tour lỗi", tenantId);
            return new(false, null, $"Không đọc được danh sách tour: {ex.Message}");
        }

        var rows = new List<TourPaymentRow>();
        int skipped = 0, missingPaid = 0;
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                // Thiếu id hoặc ngày khởi hành thì không xét được — bỏ dòng đó, KHÔNG ném,
                // vì một dòng dữ liệu bẩn không đáng làm hỏng cả lượt quét.
                if (!TryGetInt(it, "id", out var id)) { skipped++; continue; }
                if (!TryGetDate(it, "departureDate", out var dep)) { skipped++; continue; }

                // BẮT BUỘC có actualRevenue. Bản /api/ai/tours cũ KHÔNG trả field này → nếu cứ
                // coi thiếu = 0 thì "còn nợ" = trọn doanh thu, tức mọi tour đều bị báo nợ toàn bộ
                // (đã xảy ra thật 12/08). Thà bỏ qua và nói rõ trong summary còn hơn báo số sai.
                if (!TryGetDec(it, "actualRevenue", out var actual)) { missingPaid++; continue; }

                rows.Add(new TourPaymentRow(id,
                    GetStr(it, "title") ?? GetStr(it, "tourCode") ?? $"Tour #{id}",
                    GetStr(it, "customerName"), GetStr(it, "sellerName"),
                    dep, GetDec(it, "revenue"), actual));
            }
        }

        var alerts = PaymentWatchdogRule.Evaluate(rows, todayVn, WindowDays);
        int created = 0, deduped = 0;

        foreach (var a in alerts)
        {
            ct.ThrowIfCancellationRequested();
            var body = $"**{a.Title}** — khách {a.CustomerName ?? "?"} còn thiếu **{a.Outstanding:N0}đ**, "
                     + $"khởi hành {a.DepartureDate:dd/MM} (còn {a.DaysLeft} ngày). Phụ trách: {a.SellerName ?? "?"}.";

            var id = await _insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: tenantId,
                Username: "",                       // tenant-wide: cả công ty cùng thấy
                Kind: "payment-alert", Severity: a.Severity,
                Title: $"Thu nốt tiền tour trước khởi hành (còn {a.DaysLeft} ngày)",
                Body: body,
                DataJson: JsonSerializer.Serialize(new { a.TourId, a.Outstanding, a.DaysLeft }),
                AlertKey: a.AlertKey,               // dedup 24h — chạy mỗi giờ nhưng chỉ nhắc 1 lần/ngày
                IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

            if (id == null) deduped++; else created++;
        }

        await _insights.PruneAsync(KeepInsightDays, ct);

        var summary = $"Quét {rows.Count} tour sắp khởi hành → {alerts.Count} còn nợ "
                    + $"({created} cảnh báo mới, {deduped} đã báo trước đó)"
                    + (skipped > 0 ? $", bỏ qua {skipped} dòng thiếu dữ liệu" : "")
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
