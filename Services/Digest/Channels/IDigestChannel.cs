namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Một kênh phát bản tin (trong app / email / Telegram / Zalo).
///
/// <para><b>Hợp đồng:</b> <c>SendAsync</c> trả <c>false</c> khi gửi hỏng và tự log Warning bên trong —
/// KHÔNG ném ra ngoài. Bộ phát vẫn bọc try phòng hờ, nhưng kênh nào cũng nên tự xử lý lỗi của mình
/// để log nêu đúng nguyên nhân thay vì một dòng "boom" chung chung.</para>
/// </summary>
public interface IDigestChannel
{
    /// "inapp" | "email" | "telegram" | "zalo" — hiện trong summary lịch sử chạy.
    string Id { get; }

    /// Đăng ký này có bật kênh NÀY và đủ thông tin để gửi chưa? Chưa đủ → bộ phát bỏ qua, ghi "skip".
    bool IsConfigured(DigestSubscription sub);

    Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct);
}
