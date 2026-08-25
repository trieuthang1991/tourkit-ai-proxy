using System.Globalization;
using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Domain.Speech;

namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Dự phóng doanh thu cuối tháng cho bản tin điều hành (C4).
///
/// <para><b>Thống kê thẳng, KHÔNG học máy:</b> tốc độ = doanh thu ÷ số ngày đã qua, nhân với số ngày
/// của tháng. Cố ý đơn giản — một mô hình phức tạp hơn cũng không chính xác hơn khi dữ liệu chỉ là
/// doanh thu cộng dồn, mà lại không ai kiểm được nó đúng hay sai.</para>
///
/// <para><b>Cái khó không phải phép tính mà là biết KHI NÀO KHÔNG NÊN NÓI.</b> Con số này nằm trong
/// bản tin gửi giám đốc: nói bừa một lần là mất lòng tin vào cả bản tin. Hai chỗ chặn:
/// chưa khai chỉ tiêu → không hiện gì; chưa qua 5 ngày → chỉ báo số thực đạt, không ước.</para>
/// </summary>
public static class CeoForecast
{
    /// Dưới mốc này thì tốc độ chưa có nghĩa: một hợp đồng lớn về ngày 2 sẽ nhân tốc độ lên hơn
    /// mười lần, ra một con số hoang đường mà lại nằm trong bản tin gửi giám đốc.
    public const int MinDaysToProject = 5;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => TourkitAiProxy.Shared.Text.Money.So(v);

    /// <param name="Projected">Dự phóng cả tháng. <c>null</c> khi còn quá sớm để ước.</param>
    /// <param name="PercentOfTarget">Phần trăm so chỉ tiêu, làm tròn. 0 khi chưa ước được.</param>
    /// <param name="CanProject">Đã đủ ngày để ước chưa.</param>
    public record Result(decimal? Projected, int PercentOfTarget, bool CanProject, string Text);

    /// <summary>
    /// Trả <c>null</c> nghĩa là <b>không hiện mục dự phóng</b> — dùng khi công ty chưa khai chỉ tiêu.
    /// Cố ý không đoán hộ một con số: cùng nguyên tắc với "số khách tối thiểu" của kiểm tra khởi
    /// hành — chỉ tiêu bịa ra sẽ khiến mọi công ty đọc một dự phóng vô nghĩa.
    /// </summary>
    public static Result? Estimate(decimal revenueSoFar, decimal target, DateTime todayVn)
    {
        if (target <= 0) return null;

        var daysElapsed = todayVn.Day;
        var daysInMonth = DateTime.DaysInMonth(todayVn.Year, todayVn.Month);

        if (daysElapsed < MinDaysToProject)
            return new Result(null, 0, false,
                $"Từ đầu tháng đã đạt {Vnd(revenueSoFar)}đ / chỉ tiêu {Vnd(target)}đ. "
                + $"Mới qua {daysElapsed} ngày nên còn sớm để ước cả tháng.");

        var perDay = revenueSoFar / daysElapsed;
        var projected = Math.Round(perDay * daysInMonth, 0, MidpointRounding.AwayFromZero);
        var percent = (int)Math.Round(projected / target * 100, MidpointRounding.AwayFromZero);

        var verdict = percent >= 100 ? "đà này vượt kế hoạch"
                    : percent >= 80  ? "đà này hụt nhẹ, còn kịp bù"
                                     : "đà này khó đạt kế hoạch";

        return new Result(projected, percent, true,
            $"Từ đầu tháng đã đạt {Vnd(revenueSoFar)}đ / chỉ tiêu {Vnd(target)}đ. "
            + $"Theo tốc độ {daysElapsed} ngày qua, cả tháng ước {Vnd(projected)}đ "
            + $"— khoảng {percent}% kế hoạch, {verdict}.");
    }
}
