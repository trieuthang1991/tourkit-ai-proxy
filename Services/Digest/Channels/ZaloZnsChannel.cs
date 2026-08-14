using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Kênh Zalo cho nút "Gửi thử" — XẾP VÀO HÀNG ĐỢI, không tự gửi.
///
/// <para><b>Vì sao không gửi thẳng như Telegram:</b> Zalo gửi bằng ZNS qua OA của bên cung cấp dịch
/// vụ, cần app id + secret key + refresh token xoay vòng. Nhét bộ khoá đó sang cả proxy nghĩa là hai
/// nơi cùng giữ bí mật và hai nơi cùng phải làm mới token — mà token Zalo đổi mỗi lần làm mới, hai
/// bên làm mới song song là đạp lên nhau, bên nào chậm chân giữ token đã hết hiệu lực. Nên khoá chỉ
/// nằm ở worker; proxy xếp dòng, worker gửi.</para>
///
/// <para>Đây cũng đúng lối kênh email đang làm từ trước, nên không phải ngoại lệ mới: "Gửi thử" qua
/// Zalo sẽ tới trong khoảng một nhịp rút hàng đợi thay vì tức thì. Chỉ Telegram gửi ngay được, vì
/// nó chỉ cần mỗi bot token.</para>
/// </summary>
public class ZaloZnsChannel : IDigestChannel
{
    private readonly MailQueueRepository _queue;
    private readonly ILogger<ZaloZnsChannel> _log;

    public ZaloZnsChannel(MailQueueRepository queue, ILogger<ZaloZnsChannel> log)
    { _queue = queue; _log = log; }

    public string Id => "zalo";

    public bool IsConfigured(DigestSubscription sub)
        => sub.ChannelZalo && !string.IsNullOrWhiteSpace(sub.ZaloPhone);

    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        try
        {
            await _queue.EnqueueAsync(new OutboundMailInput(
                TenantId: sub.TenantId,
                Kind: DigestEnqueuePlanner.Kind,
                SourceId: null,                 // bản thử không gắn với bản tin nào trong Bảng tin
                Username: sub.Username,
                Subject: m.Title,
                Data: JsonSerializer.Serialize(new
                {
                    phone = sub.ZaloPhone!.Trim(),
                    title = m.Title,
                    body = m.BodyMarkdown,
                }),
                Channel: OutboundChannel.Zalo), ct);   // ScheduledUtc null = rút ngay nhịp tới
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[digest/zalo] xếp hàng đợi lỗi tenant={T} user={U}", sub.TenantId, sub.Username);
            return false;
        }
    }
}
