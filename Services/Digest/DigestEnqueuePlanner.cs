using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Dựng danh sách dòng hàng đợi cho 1 bản tin đã chuẩn bị xong: mỗi kênh NGOÀI đang bật = 1 dòng,
/// ScheduledUtc = giờ người chọn, SourceId = Id bản tin trong AgentInsights. THUẦN → test được.
/// Email mang Params (hợp đồng worker toutkit-app giữ NGUYÊN); telegram/zalo chỉ mang nơi nhận
/// trong Data — nội dung drainer đọc lại từ AgentInsights qua SourceId (1 nguồn).
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
                    Subject: m.Title, Data: JsonSerializer.Serialize(new { chatId = sub.TelegramChatId!.Trim() }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Telegram),
                _ => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title, Data: JsonSerializer.Serialize(new { zaloUserId = sub.ZaloUserId!.Trim() }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Zalo),
            });
        }
        return rows;
    }
}
