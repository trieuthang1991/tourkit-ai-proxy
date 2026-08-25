using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Domain.Speech;

namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Dựng nội dung thư "tour sắp đi mà khách còn nợ". Hàm THUẦN — không chạm DB, không gọi mạng —
/// để test được, vì đường gửi thật đi qua worker ở repo khác nên không chạy thử ở đây được.
///
/// <para>Theo đúng hợp đồng của <c>daily-brief</c>: proxy render sẵn <c>bodyHtml</c>, worker chèn
/// NGUYÊN vào thân thư. Nghĩa là <b>escape phải làm ở đây</b> — tên tour và tên khách do người
/// ngoài nhập, để lọt thẻ HTML vào là thư hiện lung tung.</para>
/// </summary>
public static class PaymentAlertMail
{
    public record Result(string Subject, string ParamsJson);

    public static Result Build(IReadOnlyList<PaymentAlert> alerts, DateTime todayVn)
    {
        var subject = alerts.Count == 1
            ? $"Còn 1 tour chưa thu đủ tiền trước khởi hành ({todayVn:dd/MM})"
            : $"Còn {alerts.Count} tour chưa thu đủ tiền trước khởi hành ({todayVn:dd/MM})";

        var total = alerts.Sum(a => a.Outstanding);
        var sb = new StringBuilder();
        sb.Append("<p>Tính đến ").Append(todayVn.ToString("dd/MM/yyyy"))
          .Append(", có <b>").Append(alerts.Count)
          .Append("</b> tour sắp khởi hành mà khách chưa trả đủ, tổng còn thiếu <b>")
          .Append(Money(total)).Append("đ</b>.</p>");

        sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;width:100%\">");
        sb.Append("<tr style=\"background:#f3f4f6;text-align:left\">")
          .Append("<th>Tour</th><th>Khách</th><th>Còn thiếu</th><th>Khởi hành</th><th>Phụ trách</th></tr>");

        // Gấp nhất lên đầu: còn ít ngày nhất, rồi tới nợ nhiều nhất. Xếp theo tên tour thì người
        // đọc phải tự dò xem cái nào sắp đi.
        foreach (var a in alerts.OrderBy(a => a.DaysLeft).ThenByDescending(a => a.Outstanding))
        {
            var urgent = a.DaysLeft <= 3;
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb")
              .Append(urgent ? ";background:#fef2f2" : "").Append("\">")
              .Append("<td>").Append(Esc(a.Title)).Append("</td>")
              .Append("<td>").Append(Esc(a.CustomerName ?? "—")).Append("</td>")
              .Append("<td><b>").Append(Money(a.Outstanding)).Append("đ</b></td>")
              .Append("<td>").Append(a.DepartureDate.ToString("dd/MM"))
              .Append(" (còn ").Append(a.DaysLeft).Append(" ngày)</td>")
              .Append("<td>").Append(Esc(a.SellerName ?? "—")).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</table>");
        sb.Append("<p style=\"color:#6b7280;font-size:13px\">Thư tự động từ tác vụ ")
          .Append("&quot;Canh thanh toán trước khởi hành&quot;. Mỗi tour chỉ được nhắc trong giới ")
          .Append("hạn số lần bạn đặt ở trang Tự động hoá.</p>");

        var json = JsonSerializer.Serialize(new
        {
            title = subject,
            bodyHtml = sb.ToString(),
            date = todayVn.ToString("dd/MM/yyyy"),
            count = alerts.Count,
            totalOutstanding = total,
        });
        return new Result(subject, json);
    }

    // Escape TỐI THIỂU — HtmlEncode biến chữ có dấu thành &#225;. Xem MailHtml.
    private static string Esc(string s) => MailHtml.Esc(s);

    /// <summary>
    /// Ghim vi-VN như mọi chỗ khác của Bảng tin (xem <see cref="TourReadinessRule"/>). <c>:N0</c>
    /// trần lấy theo ngôn ngữ của MÁY CHỦ nên cùng một số ra "8,000,000" ở máy này và "8.000.000"
    /// ở máy kia — thư gửi đi trông khác nhau tuỳ máy nào chạy, mà không chỗ nào lộ ra.
    /// </summary>
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Money(decimal v) => v.ToString("N0", Vi);
}
