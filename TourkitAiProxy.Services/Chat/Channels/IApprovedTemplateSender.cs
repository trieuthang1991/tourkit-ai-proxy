// Services/Chat/Channels/IApprovedTemplateSender.cs
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>Một chỗ trống phải điền trong mẫu tin.</summary>
/// <param name="Key">Khoá nền tảng nhận. Meta đánh số theo vị trí (<c>1</c>, <c>2</c>…); Zalo dùng
/// tên tự đặt (<c>ma_don</c>, <c>ngay_khoi_hanh</c>).</param>
/// <param name="Label">Chữ hiện cạnh ô nhập. Zalo trả tên thật; Meta không trả gì nên sinh tạm.</param>
/// <param name="Sample">Ví dụ nền tảng kèm theo, nếu có — nhân viên đoán ra phải điền gì.</param>
public record ChatTemplateSlot(string Key, string Label, string? Sample);

/// <summary>Một mẫu tin ĐÃ ĐƯỢC NỀN TẢNG DUYỆT.</summary>
/// <param name="Id">Mã mẫu. Zalo gửi theo mã này; Meta gửi theo <paramref name="Name"/>.</param>
/// <param name="Status">Chỉ mẫu <c>APPROVED</c> mới gửi được — vẫn trả về mẫu chờ duyệt để giao
/// diện nói rõ "đang chờ duyệt", thay vì giấu đi rồi người dùng tưởng mẫu bị mất.</param>
/// <param name="Preview">Nội dung mẫu để xem trước. Không có thì giao diện chỉ hiện tên.</param>
public record ChatTemplate(string Id, string Name, string Language, string? Category,
    string Status, IReadOnlyList<ChatTemplateSlot> Slots, string? Preview)
{
    public bool SendReady => string.Equals(Status, "APPROVED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Lý do một kênh KHÔNG gửi mẫu cho hội thoại này được — nói bằng câu người đọc hiểu.</summary>
public record TemplateBlocked(string Reason);

/// <summary>
/// Kênh có <b>mẫu tin đã được nền tảng duyệt</b> — đường DUY NHẤT nhắn cho khách khi cửa sổ trả lời
/// tự do đã đóng.
///
/// <para><b>Vì sao đây là phần thiếu quan trọng nhất.</b> Hết 24 giờ (Meta) hoặc 48 giờ (Zalo) là
/// hộp thư câm hẳn: không gửi được xác nhận đặt tour, không nhắc ngày khởi hành, không báo đổi giờ
/// bay — đúng những việc một công ty du lịch cần nhất, và đều rơi vào lúc khách đã im lâu.</para>
///
/// <para><b>Ba kênh, ba cơ chế khác hẳn nhau</b> — đừng gộp:</para>
/// <list type="bullet">
///   <item><b>WhatsApp</b> gửi theo <c>name</c> + <c>language</c>, tham số đánh số theo vị trí,
///     tách theo khối (header/body/button).</item>
///   <item><b>Messenger</b> hình dạng gần giống WhatsApp nhưng phải kèm
///     <c>messaging_type: "UTILITY"</c>, và mẫu khai trên TRANG chứ không trên tài khoản doanh nghiệp.</item>
///   <item><b>Zalo ZNS</b> gửi theo <b>SỐ ĐIỆN THOẠI</b>, không theo id người dùng — nên hội thoại
///     nào chưa biết số của khách thì <b>không gửi được</b>, dù kênh vẫn nối tốt. Tham số là tên
///     tự đặt, không phải số thứ tự. Và ZNS <b>tính tiền theo từng tin</b>.</item>
/// </list>
///
/// <para>Instagram không có mẫu tin nào cả. Telegram và TikTok không có cửa sổ nên cũng không cần.</para>
/// </summary>
public interface IApprovedTemplateSender
{
    /// <summary>
    /// Mẫu công ty đã đăng ký trên nền tảng. Trả rỗng khi chưa có mẫu nào — <b>không ném</b>: chưa
    /// đăng ký mẫu là trạng thái bình thường của một công ty mới, không phải lỗi.
    /// </summary>
    Task<IReadOnlyList<ChatTemplate>> ListTemplatesAsync(string tenantId, string accountId,
        CancellationToken ct);

    /// <summary>
    /// Kênh này gửi mẫu cho hội thoại đó được không. <c>null</c> = được.
    ///
    /// <para>Kiểm TRƯỚC khi bày danh sách mẫu ra: để nhân viên chọn mẫu, điền năm ô rồi mới báo
    /// "kênh này thiếu số điện thoại" là bắt họ làm công cốc.</para>
    /// </summary>
    TemplateBlocked? WhyBlocked(ChatContact? khach) => null;

    /// <summary>Gửi một mẫu. <paramref name="giaTri"/> khoá theo <see cref="ChatTemplateSlot.Key"/>.</summary>
    Task<SendResult> SendTemplateAsync(string tenantId, string accountId, string externalUserId,
        ChatContact? khach, ChatTemplate mau, IReadOnlyDictionary<string, string> giaTri,
        CancellationToken ct);
}
