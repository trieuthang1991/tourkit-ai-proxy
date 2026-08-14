namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Kênh gửi ra ngoài — enum kênh DUY NHẤT toàn hệ (đã thay hẳn DigestChannel/ChannelMask cũ).
/// SỐ tường minh, lưu thẳng cột dbo.OutboundMails.Channel (TINYINT): số để tránh lỗi gõ chuỗi,
/// default 0 = email nên dòng cũ trong DB tự đúng nghĩa. In-app KHÔNG nằm đây — nó là kho lưu
/// luôn-bật (AgentInsights), không phải kênh gửi.
/// Worker toutkit-app MIRROR đúng bảng số này (xem docs/mail-templates/README.md) — nó là nơi RÚT
/// hàng đợi và gửi cả 3 kênh; proxy chỉ XẾP vào. Thêm kênh mới = thêm 1 member ở CẢ 2 repo + 1 lớp
/// IOutboundChannelSender bên worker.
/// </summary>
public enum OutboundChannel : byte
{
    Email    = 0,
    Telegram = 1,
    Zalo     = 2,
}

/// Helper thuần quanh OutboundChannel (test được).
public static class OutboundChannels
{
    /// Các kênh NGOÀI người này đang bật VÀ đã khai đủ nơi nhận. Bật mà thiếu nơi nhận → bỏ,
    /// không thì enqueue ra dòng không bao giờ gửi được.
    public static List<OutboundChannel> EnabledOf(DigestSubscription s)
    {
        var list = new List<OutboundChannel>(3);
        if (s.ChannelEmail    && !string.IsNullOrWhiteSpace(s.Email))          list.Add(OutboundChannel.Email);
        if (s.ChannelTelegram && !string.IsNullOrWhiteSpace(s.TelegramChatId)) list.Add(OutboundChannel.Telegram);
        if (s.ChannelZalo     && !string.IsNullOrWhiteSpace(s.ZaloPhone))      list.Add(OutboundChannel.Zalo);
        return list;
    }

    /// Tên tiếng Việt cho log/summary.
    public static string Describe(OutboundChannel ch) => ch switch
    {
        OutboundChannel.Email => "email", OutboundChannel.Telegram => "telegram",
        OutboundChannel.Zalo => "zalo", _ => ((byte)ch).ToString(),
    };
}
