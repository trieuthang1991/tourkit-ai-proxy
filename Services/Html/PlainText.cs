using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Html;

/// <summary>
/// Đổi chữ HTML thành chữ thuần. MỘT nguồn cho mọi chỗ cần đọc chữ do người nhập qua ô soạn thảo
/// (nhật ký chăm sóc, ghi chú khách, mô tả cơ hội bán hàng, thân email).
///
/// <para><b>Vì sao gom về một chỗ:</b> trước đây mỗi nơi tự viết một bản. Bản của mail giải mã ký tự
/// đặc biệt, bản của khách hàng và cơ hội bán hàng thì KHÔNG — nên tiếng Việt trong CRM về tới AI ở
/// dạng "khong c&amp;oacute; nhu cầu". AI đọc phải chữ méo rồi chép nguyên vào lời khuyên, còn nhân
/// viên nhìn panel thì thấy chữ vô nghĩa.</para>
///
/// <para><b>Thứ tự các bước có ý nghĩa:</b> bỏ hẳn phần style/script TRƯỚC khi gỡ thẻ (không thì CSS
/// rơi thành chữ), và giải mã ký tự SAU khi gỡ thẻ — làm ngược lại thì "&amp;lt;b&amp;gt;" biến
/// thành thẻ thật rồi bị gỡ mất, tức là nuốt luôn chữ người ta viết.</para>
/// </summary>
public static class PlainText
{
    /// <summary>HTML → chữ thuần. Chuỗi rỗng/null → "". Chữ thuần đi vào thì ra gần như nguyên vẹn.</summary>
    public static string FromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;
        s = Regex.Replace(s, "<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, "<(style|script|head)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<\\s*(br|/p|/div|/tr|/li|/h[1-6])\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        //   = khoảng trắng "dính" mà &nbsp; vừa giải mã ra. Không gộp nó thì chuỗi nhìn như có
        // khoảng trắng thường nhưng so sánh/tìm kiếm lại trượt — kiểu lỗi mất cả buổi mới thấy.
        s = Regex.Replace(s, "[ \\t\\r\\f\\u00A0]+", " ");
        s = Regex.Replace(s, " *\\n *", "\n");
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }
}
