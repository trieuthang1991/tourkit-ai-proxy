namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Escape HTML cho nội dung thư — <b>TỐI THIỂU</b>, chỉ bốn ký tự thật sự phá cấu trúc HTML.
///
/// <para><b>TUYỆT ĐỐI KHÔNG dùng <c>WebUtility.HtmlEncode</c> cho chữ tiếng Việt.</b> Nó mã hoá MỌI
/// ký tự ngoài ASCII thành thực thể số, nên "Những khách này đã từng mua" biến thành
/// <c>Những kh&amp;#225;ch n&amp;#224;y đ&amp;#227; từng mua</c>. Trình duyệt render lại đúng, nhưng:</para>
/// <list type="bullet">
/// <item>thân thư phình lên nhiều lần — mail dài dễ bị cắt bớt;</item>
/// <item>ai mở hàng đợi ra đối soát thì đọc không nổi, tưởng dữ liệu hỏng;</item>
/// <item>và nếu thư rơi vào đường render KHÔNG phải HTML thì người nhận thấy nguyên dãy số.</item>
/// </list>
///
/// <para>Đã dính đúng lỗi này hai lần: lần đầu ở phần chữ dựng sẵn cho máy tìm kiếm (17/08, chữ
/// "Hộp thư AI" thành một dãy số làm thẻ mô tả vượt giới hạn của Google), lần hai ở thư nhắc chăm
/// khách (18/08). Hai lần đều do với tay lấy hàm quen thay vì hàm đúng — nên tách hẳn ra đây, đặt
/// cạnh chỗ dựng thư, để lần sau khỏi phải nhớ.</para>
/// </summary>
public static class MailHtml
{
    /// Escape 4 ký tự phá cấu trúc HTML. Giữ NGUYÊN chữ có dấu.
    public static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>
    /// <summary>
    /// Tuỳ chọn serialize cho <c>Params</c>/<c>Data</c> của hàng đợi gửi — giữ chữ tiếng Việt
    /// NGUYÊN DẠNG trong DB thay vì <c>á</c>.
    ///
    /// <para>Mặc định của System.Text.Json escape mọi ký tự ngoài ASCII, nên một dòng hàng đợi tiếng
    /// Việt trở thành dãy <c>\uXXXX</c> đọc không nổi. Worker vẫn giải mã đúng nên thư không hỏng —
    /// nhưng người mở bảng ra đối soát thì tưởng dữ liệu lỗi, và đã mất công đi tìm thật.</para>
    ///
    /// <para>An toàn: giá trị chỉ đi vào JSON rồi được parse lại, không nhúng thẳng vào HTML. Phần
    /// escape HTML đã làm riêng ở <see cref="Esc"/> trước khi đưa vào đây.</para>
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Escape rồi đổi xuống dòng thành <c>&lt;br&gt;</c> — dùng cho thân thư dựng từ chữ thuần.
    /// Thứ tự BẮT BUỘC là escape trước: làm ngược thì chính thẻ <c>&lt;br&gt;</c> vừa chèn bị escape
    /// thành chữ, người nhận đọc được đúng chuỗi "&amp;lt;br&amp;gt;" giữa các dòng.
    /// </summary>
    public static string EscToHtml(string? s) => Esc(s).Replace("\n", "<br>");
}
