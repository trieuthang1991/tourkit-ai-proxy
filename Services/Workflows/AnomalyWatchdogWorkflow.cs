using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Canh doanh thu bất thường (C2). So doanh thu tuần vừa rồi với mức thường của mấy tuần trước →
/// lệch quá ngưỡng thì ghi cảnh báo vào Bảng tin.
///
/// <para><b>KHÔNG cần bảng lịch sử số liệu.</b> Lộ trình ban đầu xếp một bảng chụp số mỗi ngày làm
/// nền cho tác vụ này. Nhưng CRM trả được doanh thu của KHOẢNG NGÀY BẤT KỲ, nên lịch sử vốn đã có
/// sẵn — hỏi mỗi tuần một lần là dựng lại được nền, khỏi nuôi thêm một bảng và một tác vụ chép số.</para>
///
/// <para><b>KHÔNG gọi AI</b> — luật thuần, không tốn lượt. Lộ trình có nhắc "AI giải thích nguyên
/// nhân", nhưng để sau: biết mình sụt 40% đã đủ hành động, còn một đoạn văn đoán nguyên nhân thì
/// tốn lượt mỗi tuần cho mọi công ty mà chưa chắc đúng.</para>
///
/// <para>Dùng <b>tài khoản tự động</b> vì đây là cảnh báo của cả công ty (<c>Username=''</c>).</para>
/// </summary>
public class AnomalyWatchdogWorkflow : IScheduledWorkflow
{
    private const int KeepInsightDays = 90;

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly ILogger<AnomalyWatchdogWorkflow> _log;

    public AnomalyWatchdogWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, ILogger<AnomalyWatchdogWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _log = log; }

    public string Type => "anomaly-watchdog";
    public string Label => "Canh doanh thu bất thường";
    public string Description => "So doanh thu tuần vừa rồi với mức thường của mấy tuần trước — lệch quá ngưỡng thì cảnh báo vào Bảng tin. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    /// Có ngưỡng lệch + số tuần nền → công ty nên xem lại cho hợp ngành mình.
    public bool HasCompanyRules => true;

    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['anomaly-watchdog'] bên workflow-options.jsx.
    private record Options(int BaselineWeeks, int ThresholdPercent, bool AlertOnIncrease);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(BaselineWeeks: 4, ThresholdPercent: 30, AlertOnIncrease: true);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            int Num(string k, int dv, int lo, int hi)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? Math.Clamp(n, lo, hi) : dv;
            bool Bit(string k, bool dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : dv;
            return def with
            {
                BaselineWeeks = Num("baselineWeeks", def.BaselineWeeks, AnomalyRule.MinBaselineWeeks, 12),
                ThresholdPercent = Num("thresholdPercent", def.ThresholdPercent, 10, 200),
                AlertOnIncrease = Bit("alertOnIncrease", def.AlertOnIncrease),
            };
        }
        catch (JsonException) { return def; }
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var opt = ParseOptions(optionsJson);

        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động (trang Tự động hóa) — cần nó để đọc số liệu.");

        string jwt;
        try
        {
            var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
            jwt = await _sessions.GetValidJwtAsync(sid, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[anomaly-watchdog] tenant={T} đăng nhập tài khoản tự động thất bại", tenantId);
            return new(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }

        // Tuần tính theo lịch VIỆT NAM và LÙI hẳn về tuần đã trọn vẹn: lấy tuần đang chạy dở sẽ
        // luôn thấy "sụt giảm" — thứ Hai nào cũng báo động vì tuần mới mới có một ngày.
        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        var currentEnd = todayVn.AddDays(-1);
        var currentStart = currentEnd.AddDays(-6);

        decimal current = 0;
        var baseline = new List<decimal>();
        int failed = 0;

        async Task<decimal?> RevenueOf(DateTime s, DateTime e)
        {
            try
            {
                var d = await _api.GetAsync(jwt,
                    $"/api/ai/financial-summary?StartDate={s:yyyy-MM-dd}&EndDate={e:yyyy-MM-dd}", ct);
                return CeoBriefWorkflow.ReadFinancial(d).Revenue;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[anomaly-watchdog] tenant={T} đọc doanh thu {S:yyyy-MM-dd}..{E:yyyy-MM-dd} lỗi",
                    tenantId, s, e);
                failed++;
                return null;
            }
        }

        var cur = await RevenueOf(currentStart, currentEnd);
        if (cur == null)
            return new(false, null, "Không đọc được doanh thu tuần vừa rồi — chưa đủ căn cứ để so.");
        current = cur.Value;

        for (int w = 1; w <= opt.BaselineWeeks; w++)
        {
            ct.ThrowIfCancellationRequested();
            var e = currentEnd.AddDays(-7 * w);
            var v = await RevenueOf(e.AddDays(-6), e);
            if (v != null) baseline.Add(v.Value);
        }

        // Một tuần nền hỏng thì bỏ qua tuần đó; hỏng quá nửa thì KHÔNG kết luận — nền thủng lỗ chỗ
        // sinh ra cảnh báo sai mà nhìn vẫn như thật.
        if (baseline.Count < AnomalyRule.MinBaselineWeeks)
            return new(true, $"Chỉ đọc được {baseline.Count}/{opt.BaselineWeeks} tuần nền — chưa đủ để kết luận.", null);

        var a = AnomalyRule.Detect(current, baseline, opt.ThresholdPercent);
        if (a == null)
            return new(true, $"Doanh thu tuần vừa rồi trong mức bình thường (nền {baseline.Count} tuần)"
                             + (failed > 0 ? $", {failed} tuần không đọc được" : "") + ".", null);

        if (a.DeviationPercent > 0 && !opt.AlertOnIncrease)
            return new(true, $"Tăng {a.DeviationPercent}% nhưng đã tắt phần báo tăng — bỏ qua.", null);

        var body = a.Text + "\n\nMở *Trợ lý số liệu* để xem chi tiết theo nguồn khách, nhân viên hoặc tuyến.";
        var week = currentStart.ToString("yyyy-MM-dd");

        var id = await _insights.InsertAsync(new AgentInsight(
            Id: 0, TenantId: tenantId,
            Username: "",                       // tenant-wide: cả công ty cùng thấy
            Kind: "anomaly-alert", Severity: a.Severity,
            Title: a.DeviationPercent > 0
                ? $"Doanh thu tuần vừa rồi tăng {a.DeviationPercent}%"
                : $"Doanh thu tuần vừa rồi giảm {Math.Abs(a.DeviationPercent)}%",
            Body: body,
            DataJson: JsonSerializer.Serialize(new
            {
                week, a.Current, a.Baseline, a.DeviationPercent, baselineWeeks = baseline.Count,
            }),
            // Khoá kèm TUẦN: mỗi tuần là một kết luận riêng, nhưng chạy lại trong cùng tuần thì
            // không nhắc lại — tác vụ này thường đặt chạy hằng ngày.
            AlertKey: $"anomaly:{week}",
            IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

        await _insights.PruneAsync(KeepInsightDays, ct);

        var summary = $"Tuần {week}: {a.Text} "
                    + (id == null ? "(đã báo tuần này rồi)" : "(cảnh báo mới)")
                    + (failed > 0 ? $". {failed} tuần nền không đọc được" : "") + ".";
        _log.LogInformation("[anomaly-watchdog] tenant={T} {Sum}", tenantId, summary);
        return new(true, summary, null);
    }
}
