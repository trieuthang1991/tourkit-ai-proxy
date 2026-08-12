using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Kênh email — KHÔNG tự gửi, chỉ xếp vào hàng đợi <c>dbo.OutboundMails</c>; worker phía
/// <c>toutkit-app</c> render mẫu rồi gửi (đúng lối đã dùng cho cảnh báo deal nguội).
///
/// <para>Tách gửi ra khỏi proxy có lý do: gửi mail chậm và hay hỏng vặt (SMTP timeout, hộp thư đầy).
/// Nếu gửi ngay trong workflow thì một địa chỉ hỏng đủ làm chậm cả lượt bản tin của mọi người.
/// Xếp hàng đợi thì workflow xong ngay, việc gửi và thử lại là chuyện của worker.</para>
///
/// <para>Người dùng KHÔNG phải khai gì thêm: hộp thư gửi đi lấy từ cấu hình SmartMail sẵn có của
/// công ty (worker tự chọn khi <c>Username = null</c>). Email rẻ nên hệ thống lo, không bắt cấu hình
/// riêng như Zalo.</para>
/// </summary>
public class EmailChannel : IDigestChannel
{
    /// Mã dùng chung cho cả Kind lẫn TemplateCode — worker tra mẫu theo mã này.
    public const string TemplateCode = "daily-brief";

    private readonly MailQueueRepository _queue;
    private readonly ILogger<EmailChannel> _log;

    public EmailChannel(MailQueueRepository queue, ILogger<EmailChannel> log) { _queue = queue; _log = log; }

    public string Id => "email";

    public bool IsConfigured(DigestSubscription sub)
        => sub.ChannelEmail && !string.IsNullOrWhiteSpace(sub.Email);

    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        try
        {
            var prms = JsonSerializer.Serialize(new
            {
                title = m.Title,
                bodyHtml = m.BodyHtml,
                briefType = m.Kind,
                // Ngày VIỆT NAM để tiêu đề mail khớp với ngày người đọc thấy trên lịch của họ.
                date = DigestDue.NowVn(DateTime.UtcNow).ToString("dd/MM/yyyy"),
            });

            await _queue.EnqueueAsync(new OutboundMailInput(
                TenantId: sub.TenantId,
                Kind: TemplateCode,
                // SourceId gồm ngày VN → mỗi người mỗi ngày một dòng, dễ đối soát khi khách báo
                // "hôm nay không nhận được mail".
                SourceId: $"{m.Kind}:{sub.Username}:{DigestDue.NowVn(DateTime.UtcNow):yyyy-MM-dd}",
                Username: null,               // worker chọn hộp thư của công ty
                TemplateCode: TemplateCode,
                ToEmail: sub.Email,
                ToName: sub.Username,
                Subject: null,                // mẫu tự quyết tiêu đề
                Params: prms), ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[digest/email] xếp hàng đợi lỗi tenant={T} user={U}", sub.TenantId, sub.Username);
            return false;
        }
    }
}
