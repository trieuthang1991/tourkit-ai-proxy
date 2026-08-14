using System.Globalization;

namespace TourkitAiProxy.Services.Digest;

/// 1 dòng tour lấy từ /api/ai/tours, đủ field để chấm sẵn sàng khởi hành.
public record TourReadinessRow(
    int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, decimal Revenue, decimal ActualRevenue,
    int Slots, int Booked, int TourType, string? TourTypeLabel);

/// 1 điểm chưa sẵn sàng của một tour.
public record ReadinessIssue(string Code, string Text);

/// 1 thẻ "tour sắp khởi hành — còn thiếu gì".
public record ReadinessCard(
    int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, int DaysLeft, int Milestone,
    List<ReadinessIssue> Issues, int Severity, string AlertKey);

/// <summary>
/// Luật thuần (không AI, không DB): tour chạm mốc D-7 / D-3 / D-1 mà còn thiếu điều kiện khởi
/// hành → dựng thẻ nhắc điều hành.
///
/// <para><b>Vì sao chấm theo MỐC chứ không quét mỗi ngày:</b> điều hành cần nghe đúng 3 lần —
/// một lần còn kịp xoay (D-7), một lần cảnh báo (D-3), một lần chốt cuối (D-1). Nhắc mỗi ngày
/// suốt một tuần thì tới ngày thứ ba là không ai đọc nữa.</para>
///
/// <para><b>Ba nhóm kiểm, mỗi nhóm bật/tắt riêng vì độ tin của dữ liệu khác nhau:</b>
/// tiền (chắc chắn — có số thật), chỗ ngồi (chỉ đúng khi công ty khai ngưỡng tối thiểu),
/// visa (CRM KHÔNG lưu trạng thái hồ sơ nên chỉ nhắc được là "tour có visa, tự kiểm").</para>
///
/// <para><b>AlertKey kèm mốc</b> (<c>readiness:{tourId}:{milestone}</c>): cùng một tour ở D-7 và
/// D-3 là hai lời nhắc khác nhau, nhưng chạy lại trong cùng một mốc thì không nhắc lại.</para>
/// </summary>
public static class TourReadinessRule
{
    /// Ba mốc mặc định, giảm dần. Đổi thứ tự sẽ làm hỏng phép chọn "mốc gần nhất đã chạm".
    public static readonly int[] DefaultMilestones = { 7, 3, 1 };

    // Ghim vi-VN, KHÔNG dùng culture của máy: máy chạy en-US sẽ in "6,000,000đ" trong khi cả bản
    // tin dùng "6.000.000đ" — hai kiểu số trong một thẻ đọc như ghép từ hai nguồn.
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi);

    /// <param name="minSeats">Số khách tối thiểu để tour chạy. 0 = công ty CHƯA khai → bỏ qua
    /// hẳn phần kiểm chỗ ngồi. Đoán hộ một con số ở đây là báo nhầm hàng loạt.</param>
    /// <param name="visaTourTypes">Mã loại tour cần hồ sơ visa. Rỗng = không kiểm.</param>
    public static List<ReadinessCard> Evaluate(
        IEnumerable<TourReadinessRow> rows, DateTime todayLocal,
        IReadOnlyList<int>? milestones = null,
        bool checkPayment = true, bool checkSeats = true, bool checkVisa = true,
        int minSeats = 0, IReadOnlyCollection<int>? visaTourTypes = null)
    {
        var marks = (milestones is { Count: > 0 } ? milestones : DefaultMilestones)
            .Where(d => d > 0).Distinct().OrderByDescending(d => d).ToList();
        if (marks.Count == 0) return new();

        var visaTypes = visaTourTypes ?? new[] { 102 };
        var today = todayLocal.Date;
        var result = new List<ReadinessCard>();

        foreach (var r in rows)
        {
            var daysLeft = (r.DepartureDate.Date - today).Days;
            // Đã khởi hành thì không còn gì để chuẩn bị; xa hơn mốc lớn nhất thì chưa tới lượt.
            if (daysLeft < 0 || daysLeft > marks[0]) continue;

            // Mốc ĐANG áp dụng = mốc nhỏ nhất mà tour đã chạm. Tour còn 5 ngày thì thuộc mốc 7
            // (đã qua 7, chưa tới 3) — nhắc theo mốc gần nhất đã đi qua, không phải mốc kế tiếp.
            var milestone = marks.LastOrDefault(m => daysLeft <= m);
            if (milestone == 0) continue;

            var issues = new List<ReadinessIssue>();

            if (checkPayment && r.Revenue > 0)
            {
                var outstanding = r.Revenue - r.ActualRevenue;
                if (outstanding > 0)
                    // Chưa thu đồng nào thì nói thẳng. Chạy thật ra "còn thiếu 26.870.862đ /
                    // 26.870.862đ" — hai số bằng nhau đọc như lỗi hiển thị, trong khi ý nghĩa
                    // thật (chưa thu gì cả) mới là điều đáng báo động nhất.
                    issues.Add(new("payment", r.ActualRevenue <= 0
                        ? $"chưa thu đồng nào — cả tour {Vnd(r.Revenue)}đ"
                        : $"còn thiếu {Vnd(outstanding)}đ / {Vnd(r.Revenue)}đ"));
            }

            // Chỉ kiểm khi công ty đã khai ngưỡng VÀ tour có khai số chỗ — tour lẻ (Slots=0)
            // không có khái niệm "đủ khách tối thiểu".
            if (checkSeats && minSeats > 0 && r.Slots > 0 && r.Booked < minSeats)
                issues.Add(new("seats", $"mới {r.Booked}/{r.Slots} khách, dưới mức tối thiểu {minSeats}"));

            if (checkVisa && visaTypes.Contains(r.TourType))
                issues.Add(new("visa", $"tour {r.TourTypeLabel ?? "visa"} — kiểm hồ sơ visa của khách"));

            if (issues.Count == 0) continue;

            // Càng gần ngày đi càng gấp; có thiếu tiền thì nâng thêm một bậc vì đòi tiền sau khi
            // khách đã bay gần như không đòi được.
            var severity = milestone <= 1 ? 3 : milestone <= 3 ? 2 : 1;
            if (issues.Any(i => i.Code == "payment") && severity < 3) severity++;

            result.Add(new ReadinessCard(r.TourId, r.Title, r.CustomerName, r.SellerName,
                r.DepartureDate.Date, daysLeft, milestone, issues, severity,
                $"readiness:{r.TourId}:{milestone}"));
        }

        // Gấp trước, rồi tới ngày đi gần nhất — điều hành đọc từ trên xuống là đúng thứ tự xử lý.
        return result
            .OrderByDescending(c => c.Severity)
            .ThenBy(c => c.DepartureDate)
            .ToList();
    }
}
