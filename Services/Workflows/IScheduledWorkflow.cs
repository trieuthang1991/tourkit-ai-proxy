namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Phạm vi chạy: PerUser → mỗi (tenant, username) riêng biệt;
/// PerTenant → chạy 1 lần cho cả tenant (username = '').
/// </summary>
public enum WorkflowScope { PerUser, PerTenant }

/// <summary>Kết quả 1 lần chạy workflow.</summary>
public record WorkflowRunResult(bool Ok, string? Summary, string? Error);

/// <summary>
/// Contract cho mỗi loại workflow tự động. Implement interface này + đăng ký
/// <c>AddSingleton&lt;IScheduledWorkflow, MyWorkflow&gt;()</c> để scheduler tự pickup.
/// </summary>
public interface IScheduledWorkflow
{
    /// Định danh duy nhất (vd "mail-auto-sync"). Phải khớp WorkflowType lưu trong DB.
    string Type { get; }

    /// Nhãn hiển thị (UI).
    string Label { get; }

    /// Mô tả ngắn (UI).
    string Description { get; }

    /// Phạm vi: PerUser (mỗi user) hay PerTenant (toàn tenant).
    WorkflowScope Scope { get; }

    /// Chạy workflow. Gọi bởi scheduler hoặc manual trigger.
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="username">Username (rỗng nếu PerTenant).</param>
    /// <param name="optionsJson">Điều kiện/option ĐỘNG (JSON) do user cấu hình — workflow tự parse. null = mặc định.</param>
    /// <param name="ct">CancellationToken (5 phút wall-clock).</param>
    Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct);
}
