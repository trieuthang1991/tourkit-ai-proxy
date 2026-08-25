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

    /// <summary>
    /// Workflow này có LUẬT CHUNG cần công ty khai trước không (đọc <c>optionsJson</c>)?
    ///
    /// Dùng cho bản tin: chưa ai khai luật thì không cho đăng ký nhận — chạy bằng mặc định mà không
    /// ai từng xem qua nghĩa là nhắc theo ngưỡng đoán mò, gửi sai ngay buổi sáng đầu tiên.
    ///
    /// Mặc định <c>false</c>: workflow không đọc option nào thì không có gì để khai, chặn người dùng
    /// lại là chặn vào hư không (ca thật: ceo-brief).
    /// </summary>
    bool HasCompanyRules => false;

    /// Chạy workflow. Gọi bởi scheduler hoặc manual trigger.
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="username">Username (rỗng nếu PerTenant).</param>
    /// <param name="optionsJson">Điều kiện/option ĐỘNG (JSON) do user cấu hình — workflow tự parse. null = mặc định.</param>
    /// <param name="ct">CancellationToken (5 phút wall-clock).</param>
    Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct);
}
