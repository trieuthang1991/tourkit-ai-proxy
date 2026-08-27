using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Infrastructure.Digest;
using TourkitAiProxy.Infrastructure.Mail;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Báo cho người đăng ký biết vì sao bản tin không tới được, rồi TẮT đăng ký của họ.
///
/// <para><b>Dùng chung cho cả hai bản tin</b> (nhân viên bán hàng và giám đốc). Chép sang hai nơi
/// thì sớm muộn hai nơi lệch nhau — mà lệch ở đây nghĩa là một loại bản tin báo, loại kia im.</para>
///
/// <para><b>Vì sao tắt chứ không nhắc lại theo chu kỳ.</b> Tắt rồi thì lượt sau khỏi kiểm lại và
/// không có lá thư thứ hai. Người dùng đăng nhập, thấy lý do trên thẻ "Bản tin của tôi", tự bật
/// lại — lúc đó <c>UpsertAsync</c> xoá trạng thái hỏng về rỗng.</para>
///
/// <para><b>Chỉ đi qua THƯ và trong app</b> — xem <see cref="BriefReadiness"/>.</para>
/// </summary>
public class BriefReadinessNotifier
{
    private readonly DigestSubscriptionRepository _subs;
    private readonly InsightRepository _insights;
    private readonly MailQueueRepository _queue;
    private readonly ILogger<BriefReadinessNotifier> _log;

    public BriefReadinessNotifier(DigestSubscriptionRepository subs, InsightRepository insights,
        MailQueueRepository queue, ILogger<BriefReadinessNotifier> log)
    { _subs = subs; _insights = insights; _queue = queue; _log = log; }

    /// <summary>
    /// Ghi dòng trong app, gửi thư nhắc (nếu khai email), rồi tắt đăng ký.
    ///
    /// <para><b>Thứ tự quan trọng: báo TRƯỚC, tắt SAU.</b> Tắt trước mà báo hỏng thì người dùng mất
    /// bản tin và không hề được báo — đúng cái lỗi đang sửa, chỉ khác là nay do mình gây ra.</para>
    /// </summary>
    /// <returns><c>false</c> khi không gửi thư nhắc được (chưa khai email) — chỗ gọi đếm để ghi vào
    /// tóm tắt lượt chạy, vì người đó chỉ biết khi nào tự mở app.</returns>
    public async Task<bool> NotifyAndDisableAsync(DigestSubscription sub, BriefReadinessReason lyDo,
        DateTime utcNow, CancellationToken ct)
    {
        var msg = BriefReadiness.BuildReminder(lyDo, sub.BriefType, sub.NotReadySinceUtc, utcNow);
        var guiDuocThu = BriefReadiness.CanRemindByMail(sub);

        try
        {
            // Dòng trong app luôn ghi: đây là kho lưu để họ đọc lại khi quay vào, kể cả khi đã nhận thư.
            var insightId = await _insights.InsertAsync(new AgentInsight(
                0, sub.TenantId, sub.Username, BriefReadiness.ReminderKind, 1,
                msg.Title, msg.BodyMarkdown, null, null, false, utcNow), ct);

            if (guiDuocThu && insightId is { } id)
            {
                // Gửi NGAY, không hẹn theo giờ bản tin: đây là lời nhắc, để tới 7h sáng mai là trễ
                // thêm một ngày mà chẳng được gì.
                var rows = DigestEnqueuePlanner.BuildRows(
                    BriefReadiness.ChannelsForReminder(sub), id, msg, utcNow,
                    utcNow.ToString("dd/MM/yyyy"));
                foreach (var r in rows) await _queue.EnqueueAsync(r, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Báo hỏng thì KHÔNG tắt — để lượt sau thử lại. Thà nhắc muộn còn hơn tắt âm thầm.
            _log.LogWarning(ex, "[{Loai}] tenant={T} user={U} không gửi được lời nhắc — GIỮ NGUYÊN đăng ký",
                sub.BriefType, sub.TenantId, sub.Username);
            return guiDuocThu;
        }

        await _subs.MarkNotReadyAsync(sub.TenantId, sub.Username,
            BriefReadiness.ReasonCode(lyDo), utcNow, ct);

        _log.LogInformation("[{Loai}] tenant={T} user={U} tạm tắt đăng ký — {LyDo}{Thu}",
            sub.BriefType, sub.TenantId, sub.Username,
            BriefReadiness.ReasonLabel(BriefReadiness.ReasonCode(lyDo)),
            guiDuocThu ? ", đã gửi thư nhắc" : ", CHƯA khai email nên không nhắc ra ngoài được");
        return guiDuocThu;
    }
}
