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

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, CancellationToken ct)
    {
        try
        {
            var result = await _sync.RunAsync(tenantId, username, max: 100, ct);
            var summary = JsonSerializer.Serialize(new
            {
                fetched = result.Fetched,
                classified = result.Classified,
                skipped = result.Skipped
            });
            _log.LogInformation("[MailAutoSync] tenant={T} user={U} → fetched={F} classified={C} skipped={S}",
                tenantId, username, result.Fetched, result.Classified, result.Skipped);
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
