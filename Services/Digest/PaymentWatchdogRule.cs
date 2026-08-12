namespace TourkitAiProxy.Services.Digest;

/// 1 dòng tour kèm số tiền, lấy từ /api/ai/tours.
public record TourPaymentRow(int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, decimal Revenue, decimal ActualRevenue);

/// 1 cảnh báo "sắp đi mà chưa thu đủ".
public record PaymentAlert(int TourId, string Title, string? CustomerName, string? SellerName,
    decimal Outstanding, DateTime DepartureDate, int DaysLeft, int Severity, string AlertKey);

/// <summary>
/// Luật thuần (không gọi AI, không đụng DB): tour khởi hành trong <c>[hôm nay, +windowDays]</c>
/// mà <c>ActualRevenue &lt; Revenue</c> → còn nợ → cảnh báo.
///
/// <para><b>Mức độ:</b> còn ≤3 ngày → 2 (gấp), còn lại → 1 (nhắc). Chọn 3 vì dưới mức đó thì
/// đòi tiền trước giờ bay gần như không kịp.</para>
///
/// <para><b>Vì sao viết thành luật thuần thay vì nhét vào workflow:</b> sai ở đây tốn tiền thật —
/// bỏ sót thì tour khởi hành mà chưa thu đủ; báo thừa thì nhân viên nhờn cảnh báo rồi bỏ qua cả
/// cái thật. Tách ra mới test được hết các ca biên.</para>
///
/// <para><c>AlertKey = "payment:{tourId}"</c> để <see cref="InsightRepository.InsertAsync"/> chống
/// nhắc lại trong 24h — workflow chạy mỗi giờ nhưng mỗi tour chỉ nhắc 1 lần/ngày.</para>
/// </summary>
public static class PaymentWatchdogRule
{
    /// Còn ≤ số ngày này thì coi là gấp.
    private const int CriticalDays = 3;

    public static List<PaymentAlert> Evaluate(IEnumerable<TourPaymentRow> rows, DateTime todayLocal, int windowDays = 7)
    {
        var result = new List<PaymentAlert>();
        // .Date cả 2 vế: DepartureDate hay todayLocal lỡ kèm giờ thì số ngày còn lại vẫn đúng,
        // không bị lệch 1 ngày ở mốc gần nửa đêm.
        var today = todayLocal.Date;

        foreach (var r in rows)
        {
            // Chưa chốt giá (Revenue<=0) thì khái niệm "còn nợ" vô nghĩa — nhắc chỉ gây nhiễu.
            if (r.Revenue <= 0) continue;

            var outstanding = r.Revenue - r.ActualRevenue;
            if (outstanding <= 0) continue;   // đủ hoặc trả dư

            var daysLeft = (r.DepartureDate.Date - today).Days;
            if (daysLeft < 0 || daysLeft > windowDays) continue;   // đã đi rồi, hoặc còn xa

            result.Add(new PaymentAlert(
                TourId: r.TourId,
                Title: r.Title,
                CustomerName: r.CustomerName,
                SellerName: r.SellerName,
                Outstanding: outstanding,
                DepartureDate: r.DepartureDate,
                DaysLeft: daysLeft,
                Severity: daysLeft <= CriticalDays ? 2 : 1,
                AlertKey: $"payment:{r.TourId}"));
        }

        return result;
    }
}
