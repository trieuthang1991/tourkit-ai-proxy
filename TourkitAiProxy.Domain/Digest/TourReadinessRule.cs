using System.Globalization;

namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// 1 dòng tour lấy từ /api/ai/tours, đủ field để chấm sẵn sàng khởi hành.
///
/// <para><b>GIỮ CHỖ CŨNG CHIẾM CHỖ.</b> Đo trên dữ liệu thật (demo2, 15/08): tour
/// <c>slots=20 booked=6 onHold=1</c> thì upstream trả <c>available=13</c> — tức
/// <c>available = slots − booked − onHold</c>. Bản đầu của luật này đếm khách bằng mỗi
/// <c>Booked</c> nên tour đã kín 7/20 bị tính là 6: công ty khai ngưỡng tối thiểu 7 sẽ nhận cảnh
/// báo "chưa đủ khách" cho một tour đã đủ. Dùng <see cref="Taken"/>, đừng dùng thẳng Booked.</para>
///
/// <para>Cố ý KHÔNG nhận trường <c>available</c> của upstream mà tự tính: giữ một nguồn duy nhất
/// (3 số gốc), khỏi cảnh hai con số vênh nhau mà không biết tin cái nào.</para>
/// </summary>
public record TourReadinessRow(
    int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, decimal Revenue, decimal ActualRevenue,
    int Slots, int Booked, int TourType, string? TourTypeLabel, int OnHold = 0,
    string? SellerUserName = null)
{
    /// Số chỗ đã bị chiếm = đã đặt + đang giữ.
    public int Taken => Booked + OnHold;

    /// Số chỗ còn trống. Kẹp ≥ 0: dữ liệu bẩn (đặt quá số chỗ) không được biến thành số âm rồi
    /// làm tỉ lệ kín vọt trên 100%.
    public int Available => Math.Max(0, Slots - Taken);

    /// Tỉ lệ kín (0..1). Tour không khai số chỗ → 0, để mọi phép so ngưỡng tự trượt.
    public double FillRate => Slots > 0 ? (double)Math.Min(Taken, Slots) / Slots : 0;
}

/// 1 điểm chưa sẵn sàng của một tour.
public record ReadinessIssue(string Code, string Text);

/// 1 thẻ "tour sắp khởi hành — còn thiếu gì".
public record ReadinessCard(
    int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, int DaysLeft, int Milestone,
    List<ReadinessIssue> Issues, int Severity, string AlertKey,
    string? SellerUserName = null);

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

    /// <summary>
    /// Mốc RIÊNG cho phần chỗ ngồi — xa hơn mốc tiền/visa vì hai việc có ĐỒNG HỒ khác nhau.
    ///
    /// <para>"Chưa thu đủ tiền" chỉ gấp khi sát ngày đi. Còn "bán nốt 3 chỗ cuối" hay "vắng quá,
    /// cân nhắc dồn chuyến" mà tới D-7 mới nói thì đã hết đường xoay — đúng cái O5 sinh ra để sửa.</para>
    /// </summary>
    public static readonly int[] DefaultCapacityMilestones = { 21, 14, 7 };

    /// Mã của các "việc" là TIN VUI chứ không phải vấn đề. Severity không tính theo nhóm này, và
    /// thẻ chỉ chứa nhóm này phải đổi cách viết — dán "sắp đầy" dưới chữ "Còn thiếu:" đọc rất vô lý.
    public static readonly IReadOnlySet<string> OpportunityCodes =
        new HashSet<string>(StringComparer.Ordinal) { "nearly_full" };

    // Ghim vi-VN, KHÔNG dùng culture của máy: máy chạy en-US sẽ in "6,000,000đ" trong khi cả bản
    // tin dùng "6.000.000đ" — hai kiểu số trong một thẻ đọc như ghép từ hai nguồn.
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => TourkitAiProxy.Shared.Text.Money.So(v);

    /// <param name="minSeats">Số khách tối thiểu để tour chạy. 0 = công ty CHƯA khai → bỏ qua
    /// hẳn phần kiểm chỗ ngồi. Đoán hộ một con số ở đây là báo nhầm hàng loạt.</param>
    /// <param name="visaTourTypes">Mã loại tour cần hồ sơ visa. Rỗng = không kiểm.</param>
    /// <param name="capacityMilestones">Mốc riêng cho phần chỗ ngồi — xem
    /// <see cref="DefaultCapacityMilestones"/>.</param>
    /// <param name="nearlyFullPercent">Kín từ bao nhiêu % thì nhắc đẩy bán nốt.</param>
    public static List<ReadinessCard> Evaluate(
        IEnumerable<TourReadinessRow> rows, DateTime todayLocal,
        IReadOnlyList<int>? milestones = null,
        bool checkPayment = true, bool checkSeats = true, bool checkVisa = true,
        int minSeats = 0, IReadOnlyCollection<int>? visaTourTypes = null,
        bool checkNearlyFull = true, int nearlyFullPercent = 80,
        IReadOnlyList<int>? capacityMilestones = null)
    {
        var readinessMarks = (milestones is { Count: > 0 } ? milestones : DefaultMilestones)
            .Where(d => d > 0).Distinct().ToHashSet();
        var capacityMarks = (capacityMilestones is { Count: > 0 } ? capacityMilestones : DefaultCapacityMilestones)
            .Where(d => d > 0).Distinct().ToHashSet();

        // HỢP của hai tập mốc. Ở mỗi mốc chỉ chạy nhóm kiểm nào có mốc đó trong tập CỦA NÓ — nhờ
        // vậy tour ở D-10 chỉ bị soi phần chỗ, còn ở D-5 soi cả hai nhưng vẫn ra MỘT thẻ.
        var marks = readinessMarks.Union(capacityMarks).OrderByDescending(d => d).ToList();
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

            // Mốc quyết định KHI NÀO lên tiếng; còn ĐƯỢC NÓI GÌ thì theo CỬA SỔ của từng nhóm.
            // Bản đầu chặn theo "mốc hiện tại có nằm trong tập của nhóm không" → tour ở D-3 mất
            // luôn phần kiểm chỗ (vì tập chỗ là {21,14,7} không chứa 3), tức là tệ hơn trước khi sửa.
            var atReadiness = daysLeft <= readinessMarks.Max();
            var atCapacity  = daysLeft <= capacityMarks.Max();

            var issues = new List<ReadinessIssue>();

            if (atReadiness && checkPayment && r.Revenue > 0)
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
            // không có khái niệm "đủ khách tối thiểu". Đếm theo Taken (đặt + GIỮ CHỖ), xem ghi chú
            // ở TourReadinessRow: đếm mỗi Booked sẽ báo thiếu khách cho tour đã đủ.
            if (atCapacity && checkSeats && minSeats > 0 && r.Slots > 0 && r.Taken < minSeats)
                issues.Add(new("seats", r.OnHold > 0
                    // Tách phần giữ chỗ ra: điều hành cần biết phần đó có thể rơi.
                    ? $"mới {r.Taken}/{r.Slots} chỗ ({r.Booked} đã đặt + {r.OnHold} giữ chỗ), dưới mức tối thiểu {minSeats}"
                    : $"mới {r.Taken}/{r.Slots} khách, dưới mức tối thiểu {minSeats}"));

            // Sắp đầy → đẩy bán nốt. Available > 0 là BẮT BUỘC: tour đầy hẳn thì không còn gì để
            // bán, nhắc chỉ là nhiễu. Ngưỡng theo TỈ LỆ chứ không theo số chỗ tuyệt đối — "còn ≤3
            // chỗ" đúng với tour 20 chỗ nhưng vô nghĩa với tour 100 chỗ.
            if (atCapacity && checkNearlyFull && r.Slots > 0 && r.Available > 0
                && r.FillRate * 100 >= nearlyFullPercent)
                issues.Add(new("nearly_full",
                    $"đã kín {r.Taken}/{r.Slots} chỗ — còn {r.Available} chỗ, đẩy bán nốt"));

            if (atReadiness && checkVisa && visaTypes.Contains(r.TourType))
                issues.Add(new("visa", $"tour {r.TourTypeLabel ?? "visa"} — kiểm hồ sơ visa của khách"));

            if (issues.Count == 0) continue;

            // Severity CHỈ tính theo nhóm vấn đề. Tour sắp đầy là tin vui — tô đỏ là sai, và làm
            // hỏng luôn thứ tự đọc (thẻ tin vui chen lên trên tour sắp bay mà chưa thu tiền).
            var problems = issues.Where(i => !OpportunityCodes.Contains(i.Code)).ToList();
            var severity = problems.Count == 0
                ? 0
                : milestone <= 1 ? 3 : milestone <= 3 ? 2 : 1;
            // Có thiếu tiền thì nâng thêm một bậc vì đòi tiền sau khi khách đã bay gần như không đòi được.
            if (problems.Any(i => i.Code == "payment") && severity is > 0 and < 3) severity++;

            result.Add(new ReadinessCard(r.TourId, r.Title, r.CustomerName, r.SellerName,
                r.DepartureDate.Date, daysLeft, milestone, issues, severity,
                $"readiness:{r.TourId}:{milestone}", r.SellerUserName));
        }

        // Gấp trước, rồi tới ngày đi gần nhất — điều hành đọc từ trên xuống là đúng thứ tự xử lý.
        return result
            .OrderByDescending(c => c.Severity)
            .ThenBy(c => c.DepartureDate)
            .ToList();
    }
}
