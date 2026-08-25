using System.Text;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Dựng TIÊU ĐỀ + NỘI DUNG của thẻ "sẵn sàng khởi hành". Tách khỏi workflow để test được: phần chữ
/// này KHÔNG kiểm được bằng chạy thật, vì tenant thử nghiệm không có tour nào khai số chỗ nên
/// nhánh "sắp đầy" không bao giờ chạy tới.
///
/// <para>Cái bẫy ở đây là chữ, không phải logic: thẻ cũ mở đầu bằng "Còn thiếu:", dán "sắp đầy chỗ"
/// vào đó thì đọc thành <i>"còn thiếu: sắp đầy"</i> — vừa vô lý vừa làm người điều hành tưởng tour
/// đang có chuyện, trong khi đó là tin vui.</para>
/// </summary>
public static class TourReadinessCardText
{
    public record CardText(string Title, string Body);

    public static CardText Build(ReadinessCard c)
    {
        var problems = c.Issues.Where(i => !TourReadinessRule.OpportunityCodes.Contains(i.Code)).ToList();
        var chances  = c.Issues.Where(i =>  TourReadinessRule.OpportunityCodes.Contains(i.Code)).ToList();

        var body = new StringBuilder(
            $"**{c.Title}** — khách {c.CustomerName ?? "?"}, khởi hành {c.DepartureDate:dd/MM} "
            + $"(còn {c.DaysLeft} ngày). Phụ trách: {c.SellerName ?? "?"}.");

        if (problems.Count > 0)
            body.Append("\n\nCòn thiếu:\n")
                .Append(string.Join("\n", problems.Select(i => $"- {i.Text}")));
        if (chances.Count > 0)
            body.Append("\n\nCơ hội:\n")
                .Append(string.Join("\n", chances.Select(i => $"- {i.Text}")));

        var title = problems.Count > 0
            ? $"Tour đi trong {c.DaysLeft} ngày — còn {problems.Count} việc chưa xong"
            // Thẻ CHỈ có tin vui: chữ "chưa xong" ở đây là báo động giả.
            : $"Tour đi trong {c.DaysLeft} ngày — sắp đầy chỗ, đẩy bán nốt";

        return new CardText(title, body.ToString());
    }
}
