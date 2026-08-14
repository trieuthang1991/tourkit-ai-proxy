using TourkitAiProxy.Services.Digest.Channels;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Phát 1 bản tin NGAY LẬP TỨC qua mọi kênh mà người nhận đã bật.
///
/// <para><b>Chỉ còn phục vụ "Gửi thử".</b> Bản tin hằng ngày KHÔNG đi đường này nữa: workflow chỉ
/// chuẩn bị nội dung rồi bỏ vào hàng đợi, gửi là việc của hàng đợi (đúng giờ, gửi bù được nếu máy
/// chủ bận). Gửi thử thì ngược lại — người dùng bấm nút và muốn thấy kết quả ngay, chờ hàng đợi
/// thì mất luôn ý nghĩa của việc thử.</para>
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
    /// Kết quả 1 lượt phát: chuỗi cho người đọc + danh sách id kênh GỬI ĐƯỢC.
    /// Không còn cờ bit "đã gửi" nào phải lưu — lượt thử là chuyện một lần, xong là xong.
    public readonly record struct SendResult(string Summary, List<string> SentChannels);

    private readonly IReadOnlyList<IDigestChannel> _channels;
    private readonly ILogger<DigestDispatcher> _log;

    public DigestDispatcher(IEnumerable<IDigestChannel> channels, ILogger<DigestDispatcher> log)
    {
        _channels = channels.ToList();
        _log = log;
    }

    public async Task<SendResult> SendAsync(DigestSubscription sub, DigestMessage m,
        CancellationToken ct)
    {
        var parts = new List<string>(_channels.Count);
        var sent = new List<string>(_channels.Count);

        foreach (var ch in _channels)
        {
            if (!ch.IsConfigured(sub)) { parts.Add($"{ch.Id}:skip"); continue; }

            try
            {
                var ok = await ch.SendAsync(sub, m, ct);
                parts.Add($"{ch.Id}:{(ok ? "ok" : "FAIL")}");
                if (ok) sent.Add(ch.Id);
                else
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

        return new SendResult(
            parts.Count > 0 ? string.Join(" ", parts) : "(không có kênh nào)",
            sent);
    }
}
