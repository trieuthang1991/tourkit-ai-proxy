using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Digest;

public record CeoNumbers(decimal Revenue, decimal Expense, decimal Profit);

/// <param name="TodayAppointments">Lịch hẹn CSKH hôm nay của cả công ty.</param>
/// <param name="OverdueAppointments">Lịch hẹn đã quá hạn mà chưa xử lý — con số này mới là
/// thứ giám đốc cần thấy: hẹn nhiều mà quá hạn nhiều nghĩa là đội đang không theo kịp.</param>
/// <param name="ShowCompare">Có in phần trăm so kỳ trước không. Tắt khi công ty chọn "không so sánh"
/// — kỳ trước lúc đó không được lấy, in "n/a" khắp nơi thì đọc như hệ thống hỏng.</param>
/// <param name="CompareLabel">Gọi tên kỳ so sánh cho đúng ("so cùng kỳ tháng trước" / "so cùng kỳ
/// năm trước"). Nhãn cứng sẽ NÓI SAI ngay khi công ty đổi kỳ so sánh.</param>
/// <param name="ShowNumbers">Đính bảng số dưới bài AI viết. Tắt thì bản tin chỉ còn lời văn —
/// gọn hơn nhưng mất chỗ đối chiếu, nên mặc định bật.</param>
public record CeoBriefData(CeoNumbers ThisMtd, CeoNumbers PrevMtd, List<string> TopSellers,
    int NewDealsYesterday, int OpenPaymentAlerts,
    int TodayAppointments = 0, int OverdueAppointments = 0,
    bool ShowSellers = true, bool ShowNewDeals = true, bool ShowAppointments = true,
    bool ShowAlerts = true, bool ShowNumbers = true,
    bool ShowCompare = true, string CompareLabel = "so cùng kỳ tháng trước");

/// <summary>
/// Dựng bản tin điều hành cho giám đốc.
///
/// <para><b>Số do máy chủ tính, AI CHỈ viết lời.</b> Đây là ranh giới cố ý: AI mà tự tính thì
/// sai số là chuyện sớm muộn, mà sai số trong báo cáo cho giám đốc thì tai hại hơn hẳn diễn đạt
/// kém. Prompt đưa số ĐÃ tính sẵn kèm lệnh cấm bịa thêm số nào ngoài danh sách.</para>
///
/// <para><b>AI lỗi hoặc hết lượt thì bản tin KHÔNG mất</b> — rơi về <see cref="RenderFallback"/>
/// in thẳng bảng số. Thà đọc bảng số khô còn hơn sáng ra không có gì.</para>
///
/// <para>Bảng số luôn được đính kèm DƯỚI phần AI viết, để người đọc đối chiếu được ngay —
/// không phải tin AI suông.</para>
/// </summary>
public static class CeoBriefBuilder
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi) + "đ";

    /// So sánh kỳ này với kỳ trước. Kỳ trước = 0 → "n/a" chứ không phải "+∞%" hay chia cho 0.
    public static string PctChange(decimal cur, decimal prev)
    {
        if (prev == 0) return "n/a";
        var pct = Math.Round((cur - prev) / prev * 100);
        return (pct >= 0 ? "+" : "") + pct + "%";
    }

    /// <summary>
    /// Không có hẹn nào thì nói thẳng "không có cuộc nào" — để trống dễ bị hiểu là lỗi lấy dữ liệu.
    ///
    /// <para><b>Quá hạn gọi đúng tên là "tồn đọng", không phải "cần xử lý ngay".</b> Nguồn
    /// (<c>/api/ai/appointments?dateFilter=3</c>) trả MỌI cuộc quá hạn chưa đóng từ trước tới nay,
    /// nên ở CRM dùng lâu con số này lên hàng nghìn. Lần chạy thật đầu tiên ra 2.338 cuộc và AI viết
    /// thành "cần xử lý ngay lập tức" — đọc như công ty đang cháy, mà thực chất là nợ dữ liệu tích
    /// luỹ nhiều năm. Số vẫn hiện đủ (che đi là giấu vấn đề), chỉ gọi tên cho đúng.</para>
    /// </summary>
    private static string AppointmentLine(CeoBriefData d)
    {
        if (d.TodayAppointments == 0 && d.OverdueAppointments == 0) return "không có cuộc nào";
        var s = $"{d.TodayAppointments} cuộc";
        if (d.OverdueAppointments > 0) s += $" · tồn đọng {d.OverdueAppointments} cuộc quá hạn (tích luỹ từ trước)";
        return s;
    }

    /// Bảng số — chỉ in những mục công ty chọn đưa vào bản tin. Mục bị tắt thì KHÔNG lấy số nên
    /// cũng không được in: in "0" cho một mục không lấy là nói dối, người đọc tưởng thật sự bằng 0.
    private static string NumbersBlock(CeoBriefData d)
    {
        var sb = new StringBuilder();
        string Cmp(decimal cur, decimal prev, bool withLabel = false)
            => d.ShowCompare ? $" ({PctChange(cur, prev)}{(withLabel ? " " + d.CompareLabel : "")})" : "";

        sb.AppendLine($"- Doanh thu: {Vnd(d.ThisMtd.Revenue)}{Cmp(d.ThisMtd.Revenue, d.PrevMtd.Revenue, true)}");
        sb.AppendLine($"- Chi phí: {Vnd(d.ThisMtd.Expense)}{Cmp(d.ThisMtd.Expense, d.PrevMtd.Expense)}");
        sb.AppendLine($"- Lợi nhuận: {Vnd(d.ThisMtd.Profit)}{Cmp(d.ThisMtd.Profit, d.PrevMtd.Profit)}");

        // Hai số này trước nằm chung một dòng; tách ra để tắt riêng từng cái mà dòng không bị cụt
        // kiểu "Cơ hội mới hôm qua: 3 · " .
        if (d.ShowNewDeals) sb.AppendLine($"- Cơ hội mới hôm qua: {d.NewDealsYesterday}");
        if (d.ShowAlerts) sb.AppendLine($"- Cảnh báo thanh toán đang mở: {d.OpenPaymentAlerts}");
        if (d.ShowAppointments) sb.AppendLine($"- Lịch hẹn hôm nay: {AppointmentLine(d)}");
        if (d.ShowSellers)
            sb.AppendLine($"- Top nhân viên bán hàng từ đầu tháng: {(d.TopSellers.Count > 0 ? string.Join("; ", d.TopSellers) : "n/a")}");

        return sb.ToString().TrimEnd();
    }

    public static string BuildPrompt(CeoBriefData d, DateTime todayLocal) =>
        $"Bạn là trợ lý điều hành cho giám đốc công ty du lịch. Hôm nay {todayLocal:dd/MM/yyyy}.\n" +
        "Viết 5-8 câu tiếng Việt tổng kết tình hình từ CHÍNH XÁC các số dưới đây. " +
        "TUYỆT ĐỐI không bịa thêm số nào ngoài input, không dùng tiêu đề markdown, " +
        "giọng tự nhiên, đi thẳng vào ý chính, nêu rõ điều đáng lưu ý nhất.\n" +
        // Không có dòng này thì AI lấy con số tồn đọng lớn nhất làm tiêu điểm và viết "cần xử lý
        // ngay lập tức" — báo động sai về việc đã nằm đó nhiều năm (xem AppointmentLine).
        "Lưu ý: số 'tồn đọng ... quá hạn' là tích luỹ từ trước, KHÔNG phải việc phát sinh hôm nay — " +
        "nếu có nói thì nói như việc cần dọn dần, đừng gọi là khẩn cấp trong ngày.\n" +
        // Chi phí 0đ là chuyện thật ở công ty chưa ghi nhận chi phí vào CRM (đã gặp lần chạy thật) —
        // AI mà tự suy "không phát sinh chi phí" thì thành kết luận sai về hoạt động kinh doanh.
        "Nếu chi phí bằng 0, hiểu là CHƯA GHI NHẬN trong hệ thống, không được kết luận là công ty " +
        "không phát sinh chi phí hay lãi trọn doanh thu.\n" +
        // Công ty tắt so sánh kỳ trước → prompt không có phần trăm nào. Không dặn thì AI vẫn viết
        // "tăng so với tháng trước" theo thói quen, tức là bịa ra một so sánh không có số.
        (d.ShowCompare ? "" : "Bộ số này KHÔNG có kỳ so sánh — tuyệt đối không viết tăng/giảm so với kỳ trước.\n") +
        "\n" + NumbersBlock(d);

    public static DigestMessage RenderFallback(CeoBriefData d, DateTime todayLocal)
        => Wrap(NumbersBlock(d), todayLocal);

    public static DigestMessage WrapAiReply(string aiProse, CeoBriefData d, DateTime todayLocal)
        => Wrap(d.ShowNumbers
                    ? aiProse.Trim() + "\n\n**Số liệu:**\n" + NumbersBlock(d)
                    : aiProse.Trim(),
                todayLocal);

    private static DigestMessage Wrap(string bodyMd, DateTime todayLocal)
        => new($"Bản tin điều hành {todayLocal:dd/MM}", bodyMd,
               SaleBriefBuilder.ToHtml(bodyMd),   // dùng chung 1 bộ đổi HTML → không lệch cách escape
               BriefTypes.Ceo);
}
