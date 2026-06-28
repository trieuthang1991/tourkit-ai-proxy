using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Workflow "Tự động review khách hàng" (PerTenant). Mỗi chu kỳ:
///   Pass 1 — KH CHƯA review (chưa có row dbo.Reviews) trong cửa sổ `createdWithinDays` → AI review (rank A–D).
///   Pass 2 — KH ĐÃ review: đọc ngày review cuối (`GeneratedAt` từ dbo.Reviews); nếu (nay − cuối) ≥ `reReviewDays`
///            → review lại. Chưa tới hạn → bỏ qua.
///
/// Tái dùng `ReviewService.ReviewAsync` (lưu dbo.Reviews → worker ReviewRankSyncWorker sync rank về CRM).
/// Auth = service account per-tenant (KHÔNG cần user online). Quota+Log: AiCallContext.Push("customer-auto-review").
///
/// Lưu ý volume: quét tối đa `pageSize` KH mới nhất/run (cap `reviewMax` lượt AI). KH cũ ngoài cửa sổ list
/// chưa được re-review (chấp nhận v1 — tránh lố; backlog drain dần). `createdWithinDays` chỉ áp Pass 1.
/// </summary>
public class CustomerAutoReviewWorkflow : IScheduledWorkflow
{
    private const int ScanPageSize = 200;

    private readonly CustomerReviewClient _client;
    private readonly ReviewService _reviewService;
    private readonly ReviewRepository _reviewRepo;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly TkSessionStore _sessions;
    private readonly AiCallContext _aiCtx;
    private readonly ILogger<CustomerAutoReviewWorkflow> _log;

    public CustomerAutoReviewWorkflow(
        CustomerReviewClient client, ReviewService reviewService, ReviewRepository reviewRepo,
        TenantServiceAccountStore serviceAccounts, TkSessionStore sessions,
        AiCallContext aiCtx, ILogger<CustomerAutoReviewWorkflow> log)
    {
        _client = client; _reviewService = reviewService; _reviewRepo = reviewRepo;
        _serviceAccounts = serviceAccounts; _sessions = sessions; _aiCtx = aiCtx; _log = log;
    }

    public string Type => "customer-auto-review";
    public string Label => "Tự động review khách hàng";
    public string Description => "Tự AI-chấm hạng khách hàng (A–D) chưa review + review lại định kỳ theo chu kỳ cấu hình";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var opt = CustomerAutoReviewOptions.Parse(optionsJson);

        var svc = _serviceAccounts.Get(tenantId);
        if (svc == null || !svc.Enabled)
            return new WorkflowRunResult(false, null, "Chưa cấu hình tài khoản tự động cho tenant (POST /api/v1/workflows/service-account)");

        string sessionId;
        try { sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct); }
        catch (Exception ex) { return new WorkflowRunResult(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}"); }

        using var _aiScope = _aiCtx.Push("customer-auto-review", tenantId, sessionId);

        int reviewed = 0, rereviewed = 0, skippedFresh = 0, skippedOld = 0;
        var now = DateTime.UtcNow;
        try
        {
            var customers = await _client.ListAsync(sessionId, ScanPageSize, ct);
            foreach (var crm in customers)
            {
                ct.ThrowIfCancellationRequested();
                if (reviewed + rereviewed >= opt.ReviewMax) break;   // cap quota/run
                try
                {
                    var existing = _reviewRepo.Get(tenantId, crm.Customer.Id);
                    if (existing == null)
                    {
                        // Pass 1 — chưa review: chỉ KH tạo trong cửa sổ (tránh lố)
                        if (AgeDays(crm.CreatedAt) > opt.CreatedWithinDays) { skippedOld++; continue; }
                        await _reviewService.ReviewAsync(crm.Customer, tenantId, ct: ct);
                        reviewed++;
                    }
                    else
                    {
                        // Pass 2 — đã review: re-review khi quá hạn theo ngày review cuối
                        var lastUtc = ParseUtc(existing.GeneratedAt);
                        if (lastUtc == null || (now - lastUtc.Value).TotalDays >= opt.ReReviewDays)
                        {
                            await _reviewService.ReviewAsync(crm.Customer, tenantId, ct: ct);
                            rereviewed++;
                        }
                        else skippedFresh++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (QuotaExhaustedException) { throw; }
                catch (Exception ex) { _log.LogWarning("[CustomerAutoReview] KH {Id} lỗi: {Err}", crm.Customer.Id, ex.Message); }
            }
        }
        catch (OperationCanceledException) { return new WorkflowRunResult(false, null, "Vượt quá thời gian 5 phút"); }
        catch (QuotaExhaustedException) { return new WorkflowRunResult(false, null, "Hết quota AI"); }
        catch (Exception ex)
        {
            _log.LogWarning("[CustomerAutoReview] tenant={T} lỗi: {Err}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null, ex.Message);
        }

        var summary = JsonSerializer.Serialize(new { reviewed, rereviewed, skippedFresh, skippedOld });
        _log.LogInformation("[CustomerAutoReview] tenant={T} → reviewed={R} rereviewed={RR} skippedFresh={SF} skippedOld={SO}",
            tenantId, reviewed, rereviewed, skippedFresh, skippedOld);
        return new WorkflowRunResult(true, summary, null);
    }

    private static int AgeDays(string? createdIso)
    {
        if (DateTime.TryParse(createdIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return Math.Max(0, (int)(DateTime.UtcNow.Date - d.Date).TotalDays);
        return 0;   // không parse được → coi như mới (không loại nhầm)
    }

    private static DateTime? ParseUtc(string? iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : (DateTime?)null;
}

/// Option ĐỘNG của customer-auto-review.
public sealed record CustomerAutoReviewOptions(int CreatedWithinDays, int ReReviewDays, int ReviewMax)
{
    public static CustomerAutoReviewOptions Parse(string? json)
    {
        var def = new CustomerAutoReviewOptions(CreatedWithinDays: 30, ReReviewDays: 30, ReviewMax: 20);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            return new CustomerAutoReviewOptions(
                CreatedWithinDays: Clamp(GetInt(r, "createdWithinDays", 30), 1, 365),
                ReReviewDays:      Clamp(GetInt(r, "reReviewDays", 30), 1, 365),
                ReviewMax:         Clamp(GetInt(r, "reviewMax", 20), 1, 100));
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
}
