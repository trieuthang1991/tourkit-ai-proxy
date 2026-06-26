using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Workflow tự động đồng bộ Gmail: kéo email mới (tối đa 100) + AI phân loại.
/// Implement <see cref="IScheduledWorkflow"/> để scheduler tự pickup.
/// Scope = PerUser (mỗi user có hộp thư riêng).
/// </summary>
public class MailAutoSyncWorkflow : IScheduledWorkflow
{
    private readonly MailSyncService _sync;
    private readonly ILogger<MailAutoSyncWorkflow> _log;

    public MailAutoSyncWorkflow(MailSyncService sync, ILogger<MailAutoSyncWorkflow> log)
    {
        _sync = sync; _log = log;
    }

    public string Type => "mail-auto-sync";
    public string Label => "Tự động đồng bộ Gmail";
    public string Description => "Kéo email mới từ Gmail, AI phân loại + đặt nhãn 6 nhóm (hỏi/đặt tour, báo giá, khiếu nại...)";
    public WorkflowScope Scope => WorkflowScope.PerUser;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        // Option ĐỘNG do user cấu hình ở /workflows. Hiện hỗ trợ: autoReply (tự động trả lời).
        var opt = MailAutoSyncOptions.Parse(optionsJson);
        try
        {
            // max nhỏ cho mỗi run nền: kết nối nhẹ → ít bị Gmail RST; backlog tự drain dần qua các chu kỳ
            // (FetchRecentAsync cap incremental theo max + checkpoint từng lô).
            var result = await _sync.RunAsync(tenantId, username, max: 50, ct);

            // TODO(auto-reply): khi opt.AutoReply=true → với mỗi mail MỚI hợp lệ, gọi MailReplyService
            // soạn nháp + IMailSender gửi. CHƯA bật gửi tự động (gửi thư cho khách tự động = rủi ro cao,
            // cần xác nhận tiêu chí: chỉ category nào, tone nào, có cần người duyệt không).
            if (opt.AutoReply)
                _log.LogInformation("[MailAutoSync] tenant={T} user={U} autoReply=ON (chưa kích hoạt gửi tự động)", tenantId, username);

            var summary = JsonSerializer.Serialize(new
            {
                fetched = result.Fetched,
                classified = result.Classified,
                skipped = result.Skipped,
                autoReply = opt.AutoReply
            });
            _log.LogInformation("[MailAutoSync] tenant={T} user={U} → fetched={F} classified={C} skipped={S} autoReply={AR}",
                tenantId, username, result.Fetched, result.Classified, result.Skipped, opt.AutoReply);
            return new WorkflowRunResult(Ok: true, Summary: summary, Error: null);
        }
        catch (OperationCanceledException)
        {
            return new WorkflowRunResult(Ok: false, Summary: null, Error: "Vượt quá thời gian 5 phút");
        }
        catch (Exception ex)
        {
            _log.LogWarning("[MailAutoSync] tenant={T} user={U} lỗi: {Err}", tenantId, username, ex.Message);
            return new WorkflowRunResult(Ok: false, Summary: null, Error: ex.Message);
        }
    }
}

/// Shape option ĐỘNG của mail-auto-sync (parse từ OptionsJson). Mở rộng = thêm field + đọc ở đây.
public sealed record MailAutoSyncOptions(bool AutoReply)
{
    public static MailAutoSyncOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new MailAutoSyncOptions(false);
        try
        {
            using var d = JsonDocument.Parse(json);
            var autoReply = d.RootElement.TryGetProperty("autoReply", out var v)
                && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));
            return new MailAutoSyncOptions(autoReply);
        }
        catch { return new MailAutoSyncOptions(false); }
    }
}
