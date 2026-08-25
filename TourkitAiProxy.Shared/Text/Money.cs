using System.Globalization;

namespace TourkitAiProxy.Shared.Text;

/// <summary>
/// Định dạng tiền theo lối Việt Nam. Một nguồn cho toàn dự án.
///
/// <para><b>Vì sao có lớp này.</b> Cùng một hàm <c>Vnd</c> từng được chép ở <b>8 file</b>
/// (bản tin sáng, bản tin CEO, dự phóng, canh thanh toán, tour sắp khởi hành, nhắc chăm khách,
/// canh bất thường, thẻ chuẩn bị gặp khách). Mỗi bản tự khai lại <c>CultureInfo</c> của mình.</para>
///
/// <para><b>Và chúng KHÔNG giống nhau</b> — đó mới là điểm đáng sợ: 5 bản trả về số trần
/// (<c>"1.234.567"</c>), 3 bản kèm đơn vị (<c>"1.234.567đ"</c>), một bản nhận <c>long</c> thay vì
/// <c>decimal</c>. Gộp mù thành một hàm là <b>âm thầm đổi chữ hiện ra cho người dùng</b> ở năm chỗ.
/// Nên ở đây tách hẳn <see cref="So"/> và <see cref="Vnd"/>: gọi chỗ nào thì nói rõ chỗ ấy muốn gì,
/// không ai phải đoán.</para>
/// </summary>
public static class Money
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Số trần, phân nhóm nghìn: <c>1234567</c> → <c>"1.234.567"</c>. KHÔNG kèm đơn vị.</summary>
    public static string So(decimal v) => v.ToString("N0", Vi);

    /// <summary>Kèm đơn vị: <c>1234567</c> → <c>"1.234.567đ"</c>.</summary>
    public static string Vnd(decimal v) => So(v) + "đ";
}
