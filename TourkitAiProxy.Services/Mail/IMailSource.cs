using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// <summary>Nguồn mail per-(tenant, user) — pull email mới hơn lần sync trước theo creds của user.</summary>
public interface IMailSource
{
    /// <summary>Pull N email mới nhất cho (tenant, user). Incremental: chỉ email có UID > lần trước.</summary>
    /// <param name="ignoreCursor">
    /// true = BỎ QUA mốc UID và kéo lại N thư mới nhất dù đã kéo rồi. Dùng khi cần ĐỌC LẠI nội dung
    /// thư cũ (vd bản bóc thư được sửa, thư chuyển tiếp trước đây lưu ra rỗng). Sync thường luôn để
    /// false — bật lên là kéo lại cả lô, tốn băng thông và thời gian.
    /// </param>
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(
        string tenantId, string username, int max, CancellationToken ct, bool ignoreCursor = false);
}
