// Services/Chat/Channels/IChatChannelAdapter.cs
using TourkitAiProxy.Services.Chat.Inbox;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>Kết quả gửi một tin ra kênh.</summary>
/// <param name="Ok">Gửi được chưa.</param>
/// <param name="ThuLai">Hỏng TẠM THỜI (mạng, nhà cung cấp 5xx) → để hàng đợi thử lại. false nghĩa
/// là thử lại cũng vô ích (hết cửa sổ, chưa khai OA, khách chặn) — đừng quay vòng vô nghĩa.</param>
/// <param name="ExternalMsgId">Id tin phía kênh, để đối soát về sau.</param>
public record SendResult(bool Ok, bool ThuLai, string? ExternalMsgId, string? Error);

/// <summary>
/// Một kênh chat. Lõi (nhận tin → sinh trả lời → xếp hàng đợi → gửi) KHÔNG biết kênh nào; mỗi kênh
/// chỉ cần cài đủ ba việc: xác thực webhook, bóc sự kiện, và gửi tin.
///
/// <para>Thêm kênh mới = thêm 1 lớp cài giao diện này + 1 member trong <see cref="ChatChannel"/>.
/// Nếu phải sửa phần lõi thì phần trừu tượng hoá này đã sai.</para>
/// </summary>
public interface IChatChannelAdapter
{
    ChatChannel Channel { get; }

    /// <summary>
    /// Kiểm chữ ký webhook.
    ///
    /// <para><paramref name="rawBody"/> phải là THÂN THÔ, chưa parse. Ký trên bản đã parse rồi
    /// serialize lại chỉ đúng khi thứ tự khoá và khoảng trắng trùng khít bản gốc — gần như không
    /// bao giờ trùng, và chữ ký sẽ luôn sai.</para>
    /// </summary>
    Task<bool> VerifyAsync(string tenantId, string rawBody, IHeaderDictionary headers, CancellationToken ct);

    /// Bóc thân webhook thành sự kiện chuẩn hoá. Bỏ qua loại không quan tâm bằng cách không trả về.
    IReadOnlyList<InboundChatEvent> Parse(string rawBody);

    /// Gửi một tin chữ ra kênh.
    Task<SendResult> SendTextAsync(string tenantId, string externalUserId, string text, CancellationToken ct);
}
