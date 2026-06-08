// Services/TourKit/ITenantContext.cs
namespace TourkitAiProxy.Services.TourKit;

/// <summary>
/// Cung cấp TenantId của request hiện tại — đọc từ X-Session-Id header + TkSessionStore.
/// Phase 1 RESTful sẽ extract qua TenantFilter; ở plan này chỉ tạo skeleton, services
/// vẫn nhận tenantId qua parameter (Option B mẫu Reviews).
/// </summary>
public interface ITenantContext
{
    /// TenantId của session hiện tại. Throw nếu anonymous — caller phải đảm bảo auth đã pass.
    string TenantId { get; }

    /// Try variant — trả null nếu anonymous (vd background job, healthcheck).
    string? TryGetTenantId();
}
