namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Cờ bật/tắt tính năng CHƯA RA MẮT, đọc từ <c>appsettings.json</c> mục <c>Features</c>.
///
/// <para>Đây KHÔNG phải phân quyền. Quyền (<c>CH_HT_XEM</c>…) trả lời "người này được xem gì";
/// cờ ở đây trả lời "tính năng này đã ra mắt chưa" — tắt là tắt cho tất cả, kể cả admin.</para>
///
/// <para><b>Thiếu key = TẮT.</b> Cố ý sai theo hướng an toàn: quên khai báo lúc deploy thì hậu quả
/// là tính năng bị ẩn (phiền, nhưng sửa 1 dòng); nếu mặc định bật thì quên khai báo nghĩa là bản
/// tin GỬI THẬT cho khách trên bản public.</para>
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// Cụm bản tin: bản tin sáng (<c>sale-brief</c>), bản tin điều hành (<c>ceo-brief</c>),
    /// canh thanh toán (<c>payment-watchdog</c>) và Bảng tin trong app.
    ///
    /// <para>MỘT cờ cho cả cụm vì với người dùng chúng là một tính năng: 3 tác vụ đều ghi vào Bảng
    /// tin, và Bảng tin là chỗ đọc lại. Tách cờ riêng sẽ đẻ ra trạng thái vô nghĩa — bật bản tin
    /// mà không có chỗ nào xem nó.</para>
    /// </summary>
    public static bool Digest(IConfiguration cfg) => cfg.GetValue("Features:Digest", false);
}
