using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Domain.Mail;

/// <remarks>
/// Ở Domain chứ không ở cạnh MailQueueRepository: đây là HÌNH DẠNG của một dòng hàng đợi, do
/// luật nghiệp vụ (DigestEnqueuePlanner) dựng ra; repository chỉ ghi nó xuống. Để cạnh repository
/// thì planner phải phụ thuộc ngược vào tầng hạ tầng.
///
/// Namespace GIỮ NGUYÊN <c>TourkitAiProxy.Services.Mail</c> để không phải sửa using ở nơi dùng —
/// đổi tên namespace là việc dọn riêng, đừng trộn vào đợt di chuyển.
/// </remarks>
/// Input enqueue 1 mail (Id/Status/CreatedUtc do DB sinh). `Params` = JSON tham số replace vào template.
public record OutboundMailInput(
    string TenantId,
    string Kind,
    string? SourceId = null,
    string? Username = null,
    string? TemplateCode = null,
    string? ToEmail = null,
    string? ToName = null,
    int? ToUserId = null,
    string? Cc = null,
    string? Subject = null,
    string? Params = null,
    string? Data = null,
    DateTime? ScheduledUtc = null,
    OutboundChannel Channel = OutboundChannel.Email);
