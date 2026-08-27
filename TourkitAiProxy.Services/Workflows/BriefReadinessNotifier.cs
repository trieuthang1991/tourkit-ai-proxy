using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Infrastructure.Digest;
using TourkitAiProxy.Infrastructure.Mail;
using TourkitAiProxy.Infrastructure.TourKit;

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
    /// <summary>
    /// Kẹp thời gian chờ khi tự xin chìa khoá.
    ///
    /// <para>⚠️ Đo thật 27/08/2026: hỏi CRM cho một tên đăng nhập KHÔNG tồn tại thì họ
    /// <b>không từ chối, mà treo</b> tới hết 30 giây. Trong vòng lặp bản tin, mỗi người hỏng là
    /// chặn cả lượt chạy chừng đó — mười người là năm phút. Kẹp ngắn: đằng nào chờ lâu cũng
    /// không cứu được ai.</para>
    /// </summary>
    private static readonly TimeSpan HanTuCapPhien = TimeSpan.FromSeconds(10);

    private readonly DigestSubscriptionRepository _subs;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TkSessionStore _sessions;
    private readonly IConfiguration _cfg;
    private readonly InsightRepository _insights;
    private readonly MailQueueRepository _queue;
    private readonly ILogger<BriefReadinessNotifier> _log;

    public BriefReadinessNotifier(DigestSubscriptionRepository subs, InsightRepository insights,
        MailQueueRepository queue, TkSessionRepository sessionRepo, TkSessionStore sessions,
        IConfiguration cfg, ILogger<BriefReadinessNotifier> log)
    {
        _subs = subs; _insights = insights; _queue = queue;
        _sessionRepo = sessionRepo; _sessions = sessions; _cfg = cfg; _log = log;
    }

    /// <summary>Kết quả tìm phiên cho một người nhận.</summary>
    /// <param name="SessionId">Có giá trị = sẵn sàng gửi. Null = xem <paramref name="LyDo"/>.</param>
    public record KetQua(string? SessionId, BriefReadinessReason? LyDo);

    /// <summary>
    /// Tìm phiên của người nhận — <b>tự xin chìa khoá nếu chưa có</b>.
    ///
    /// <para><b>Đây là lối thoát cho gần như mọi ca hỏng.</b> Đăng nhập một chạm của TourKit ký
    /// bằng <c>Sso:Secret</c> và chỉ cần <c>tenantId</c> + tên đăng nhập — hai thứ nằm sẵn ngay
    /// trên dòng đăng ký. Nên khi phiên hết hạn, hệ thống <b>tự xin chìa khoá mới</b> thay vì
    /// bắt người dùng đi đăng nhập rồi quay lại bật đăng ký. Đo thật: CRM cấp chìa cho một tài
    /// khoản thật mà không cần mật khẩu, không cần thao tác nào của họ.</para>
    ///
    /// <para>Chỉ khi CRM TỪ CHỐI (tài khoản khoá/xoá) mới thật sự bó tay — lúc đó mới báo và tắt
    /// đăng ký. Ca đó hiếm, và nó cần người xử lý bên CRM chứ không phải đăng nhập lại.</para>
    /// </summary>
    public async Task<KetQua> TimHoacTuCapPhienAsync(DigestSubscription sub, CancellationToken ct)
    {
        var phien = await _sessionRepo.GetByUserAsync(sub.TenantId, sub.Username, ct);
        if (phien != null) return new(phien.Id, null);

        // Chưa khai khoá SSO (bản cài tại chỗ, máy dev) → không tự cấp được, đành nhờ người dùng.
        var khoa = _cfg["Sso:Secret"];
        if (string.IsNullOrWhiteSpace(khoa))
        {
            _log.LogInformation("[{Loai}] tenant={T} user={U} chưa khai Sso:Secret — không tự cấp phiên được",
                sub.BriefType, sub.TenantId, sub.Username);
            return new(null, BriefReadinessReason.NoSession);
        }

        try
        {
            using var hetGio = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hetGio.CancelAfter(HanTuCapPhien);
            var moi = await _sessions.CreateFromSsoAsync(sub.TenantId, sub.Username, hetGio.Token);
            _log.LogInformation("[{Loai}] tenant={T} user={U} tự cấp phiên mới — người dùng không phải làm gì",
                sub.BriefType, sub.TenantId, sub.Username);
            return new(moi.Id, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Gộp "CRM từ chối" với "CRM không trả lời": cả hai đều cần người xử lý bên CRM,
            // và bảo họ đăng nhập lại là bảo làm một việc chắc chắn hỏng.
            _log.LogWarning(ex, "[{Loai}] tenant={T} user={U} CRM không cấp phiên",
                sub.BriefType, sub.TenantId, sub.Username);
            return new(null, BriefReadinessReason.ReloginFailed);
        }
    }

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
