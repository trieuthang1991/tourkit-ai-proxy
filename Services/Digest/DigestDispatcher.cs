using TourkitAiProxy.Services.Digest.Channels;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Phát 1 bản tin qua mọi kênh mà người nhận đã bật.
///
/// <para><b>Bất biến quan trọng nhất: một kênh hỏng KHÔNG được làm chết các kênh còn lại.</b>
/// Telegram sập thì bản tin vẫn phải vào được app và email. Vì vậy mỗi kênh chạy trong try riêng,
/// lỗi chỉ ghi vào summary chứ không nổi lên trên.</para>
///
/// <para>Chạy TUẦN TỰ chứ không song song: mỗi người chỉ vài kênh, song song không nhanh hơn đáng
/// kể mà lại làm log rối và khó lần khi có sự cố.</para>
///
/// <para>Summary dạng <c>"inapp:ok email:FAIL telegram:skip"</c> ghi vào lịch sử chạy workflow —
/// nhìn là biết ngay kênh nào hỏng, không phải mò log.</para>
/// </summary>
public class DigestDispatcher
{
    private readonly IReadOnlyList<IDigestChannel> _channels;
    private readonly ILogger<DigestDispatcher> _log;

    public DigestDispatcher(IEnumerable<IDigestChannel> channels, ILogger<DigestDispatcher> log)
    {
        _channels = channels.ToList();
        _log = log;
    }

    public async Task<string> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        var parts = new List<string>(_channels.Count);

        foreach (var ch in _channels)
        {
            if (!ch.IsConfigured(sub)) { parts.Add($"{ch.Id}:skip"); continue; }

            try
            {
                var ok = await ch.SendAsync(sub, m, ct);
                parts.Add($"{ch.Id}:{(ok ? "ok" : "FAIL")}");
                if (!ok)
                    _log.LogWarning("[digest] kênh {Ch} gửi hỏng cho tenant={T} user={U} loại={Kind}",
                        ch.Id, sub.TenantId, sub.Username, m.Kind);
            }
            catch (OperationCanceledException) { throw; }   // huỷ là huỷ, đừng nuốt
            catch (Exception ex)
            {
                // Kênh lẽ ra phải tự bắt lỗi của mình. Vào được đây nghĩa là kênh đó viết thiếu —
                // vẫn giữ cho các kênh sau chạy tiếp.
                parts.Add($"{ch.Id}:FAIL");
                _log.LogWarning(ex, "[digest] kênh {Ch} ném lỗi (đáng lẽ phải tự bắt) tenant={T} user={U}",
                    ch.Id, sub.TenantId, sub.Username);
            }
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "(không có kênh nào)";
    }
}
