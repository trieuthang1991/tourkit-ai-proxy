using System.Text.Json;
using TourkitAiProxy.Services.Mail;

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

    public static List<OutboundMailInput> BuildRows(DigestSubscription sub, long insightId,
        DigestMessage m, DateTime scheduledUtc, string dateVn)
    {
        var rows = new List<OutboundMailInput>(3);
        foreach (var ch in OutboundChannels.EnabledOf(sub))
        {
            rows.Add(ch switch
            {
                OutboundChannel.Email => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    TemplateCode: "daily-brief", ToEmail: sub.Email!.Trim(), Subject: m.Title,
                    Params: JsonSerializer.Serialize(new { title = m.Title, bodyHtml = m.BodyHtml, briefType = m.Kind, date = dateVn }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Email),
                OutboundChannel.Telegram => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title,
                    Data: JsonSerializer.Serialize(new
                    {
                        chatId = sub.TelegramChatId!.Trim(),
                        title = m.Title,
                        body = m.BodyMarkdown,
                    }),
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
                    }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Zalo),
            });
        }
        return rows;
    }
}
