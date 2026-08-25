using System.Text.Json;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Dựng danh sách dòng hàng đợi cho 1 bản tin đã chuẩn bị xong: mỗi kênh NGOÀI đang bật = 1 dòng,
/// ScheduledUtc = giờ người chọn, SourceId = Id bản tin trong AgentInsights. THUẦN → test được.
///
/// <para><b>Dòng mang theo ĐỦ thứ cần để gửi</b> — email mang <c>Params</c> (hợp đồng worker
/// toutkit-app giữ NGUYÊN); telegram/zalo mang nơi nhận + tiêu đề + nội dung trong <c>Data</c>.
/// Cố ý KHÔNG bắt worker đọc lại <c>dbo.AgentInsights</c> qua <c>SourceId</c>: worker bên
/// toutkit-app đã phải với sang bảng của proxy để lấy token OA Zalo rồi, thêm một bảng nữa là
/// thêm một chỗ hai repo phải khớp lược đồ với nhau. <c>SourceId</c> vẫn ghi để đối soát.</para>
/// </summary>
public static class DigestEnqueuePlanner
{
    public const string Kind = "daily-brief";

    /// <param name="zaloTemplateId">Mã mẫu ZNS của công ty cho ĐÚNG loại bản tin này (null = chưa
    /// khai). Đính kèm ngay trên dòng hàng đợi thay vì bắt worker tự tra: worker bên toutkit-app
    /// đọc bảng của proxy càng ít càng tốt, và lúc gửi mới đi tra thì mẫu có thể đã bị đổi so với
    /// lúc dựng nội dung.</param>
    public static List<OutboundMailInput> BuildRows(DigestSubscription sub, long insightId,
        DigestMessage m, DateTime scheduledUtc, string dateVn, string? zaloTemplateId = null)
    {
        var rows = new List<OutboundMailInput>(3);
        foreach (var ch in OutboundChannels.EnabledOf(sub))
        {
            rows.Add(ch switch
            {
                OutboundChannel.Email => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    TemplateCode: "daily-brief", ToEmail: sub.Email!.Trim(), Subject: m.Title,
                    Params: JsonSerializer.Serialize(new { title = m.Title, bodyHtml = m.BodyHtml, briefType = m.Kind, date = dateVn }, MailHtml.Json),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Email),
                OutboundChannel.Telegram => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title,
                    Data: JsonSerializer.Serialize(new
                    {
                        chatId = sub.TelegramChatId!.Trim(),
                        title = m.Title,
                        body = m.BodyMarkdown,
                    }, MailHtml.Json),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Telegram),
                // Zalo đi bằng ZNS → nơi nhận là SỐ ĐIỆN THOẠI. Vẫn mang theo title/body dù mẫu ZNS
                // chỉ hiện được vài tham số: worker quyết lấy gì nhét vào mẫu, và nếu sau này đổi
                // mẫu (thêm tham số) thì không phải sửa lại chỗ xếp hàng đợi.
                _ => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title,
                    Data: JsonSerializer.Serialize(new
                    {
                        phone = sub.ZaloPhone!.Trim(),
                        title = m.Title,
                        body = m.BodyMarkdown,
                        // Mẫu ZNS của CHÍNH công ty đó. null = chưa khai → worker không đoán mẫu
                        // khác thay thế, mà đánh dấu "thiếu cấu hình" để trang theo dõi nói ra.
                        templateId = zaloTemplateId,
                        // Loại bản tin: worker cần để tra đúng mẫu khi gặp dòng CŨ (do bản proxy
                        // trước xếp vào, chưa có templateId). Thiếu nó thì bản dự phòng phải đoán,
                        // và đoán trượt là gửi bản tin điều hành bằng mẫu của bản tin bán hàng.
                        briefType = m.Kind,
                    }, MailHtml.Json),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Zalo),
            });
        }
        return rows;
    }
}
