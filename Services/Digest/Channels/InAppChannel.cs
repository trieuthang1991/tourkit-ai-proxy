namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Kênh trong app — ghi 1 dòng vào Bảng tin (dbo.AgentInsights).
///
/// <para>Kênh NỀN, không cần khai báo gì và người dùng không tắt được ở mức thực thi: dù họ tắt hết
/// email/Telegram/Zalo thì bản tin vẫn còn chỗ để xem lại. Chỉ khi tắt hẳn đăng ký mới thôi.</para>
/// </summary>
public class InAppChannel : IDigestChannel
{
    private readonly InsightRepository _repo;
    private readonly ILogger<InAppChannel> _log;

    public InAppChannel(InsightRepository repo, ILogger<InAppChannel> log) { _repo = repo; _log = log; }

    public string Id => "inapp";

    public bool IsConfigured(DigestSubscription sub) => sub.ChannelInApp;

    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        try
        {
            // KHÔNG đặt AlertKey: bản tin hằng ngày phải được ghi mỗi ngày một dòng.
            // AlertKey là để chống nhắc trùng một VIỆC, không phải chống trùng bản tin.
            var id = await _repo.InsertAsync(new AgentInsight(
                Id: 0, TenantId: sub.TenantId, Username: sub.Username,
                Kind: m.Kind, Severity: m.Severity,
                Title: m.Title, Body: m.BodyMarkdown,
                DataJson: null, AlertKey: null, IsRead: false, CreatedUtc: DateTime.UtcNow), ct);
            return id != null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[digest/inapp] ghi Bảng tin lỗi tenant={T} user={U}", sub.TenantId, sub.Username);
            return false;
        }
    }
}
