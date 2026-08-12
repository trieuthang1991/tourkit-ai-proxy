using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Digest;

public record CeoNumbers(decimal Revenue, decimal Expense, decimal Profit);

/// <param name="TodayAppointments">Lịch hẹn CSKH hôm nay của cả công ty.</param>
/// <param name="OverdueAppointments">Lịch hẹn đã quá hạn mà chưa xử lý — con số này mới là
/// thứ giám đốc cần thấy: hẹn nhiều mà quá hạn nhiều nghĩa là đội đang không theo kịp.</param>
public record CeoBriefData(CeoNumbers ThisMtd, CeoNumbers PrevMtd, List<string> TopSellers,
    int NewDealsYesterday, int OpenPaymentAlerts,
    int TodayAppointments = 0, int OverdueAppointments = 0);

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

    /// Không có hẹn nào thì nói thẳng "không có cuộc nào" — để trống dễ bị hiểu là lỗi lấy dữ liệu.
    /// Có quá hạn thì mới nêu, và nêu KÈM chữ "cần xử lý" để nó thành việc chứ không phải con số suông.
    private static string AppointmentLine(CeoBriefData d)
    {
        if (d.TodayAppointments == 0 && d.OverdueAppointments == 0) return "không có cuộc nào";
        var s = $"{d.TodayAppointments} cuộc";
        if (d.OverdueAppointments > 0) s += $" · {d.OverdueAppointments} cuộc QUÁ HẠN cần xử lý";
        return s;
    }

    private static string NumbersBlock(CeoBriefData d) => new StringBuilder()
        .AppendLine($"- Doanh thu: {Vnd(d.ThisMtd.Revenue)} ({PctChange(d.ThisMtd.Revenue, d.PrevMtd.Revenue)} so cùng kỳ tháng trước)")
        .AppendLine($"- Chi phí: {Vnd(d.ThisMtd.Expense)} ({PctChange(d.ThisMtd.Expense, d.PrevMtd.Expense)})")
        .AppendLine($"- Lợi nhuận: {Vnd(d.ThisMtd.Profit)} ({PctChange(d.ThisMtd.Profit, d.PrevMtd.Profit)})")
        .AppendLine($"- Cơ hội mới hôm qua: {d.NewDealsYesterday} · Cảnh báo thanh toán đang mở: {d.OpenPaymentAlerts}")
        .AppendLine($"- Lịch hẹn hôm nay: {AppointmentLine(d)}")
        .AppendLine($"- Top nhân viên bán hàng từ đầu tháng: {(d.TopSellers.Count > 0 ? string.Join("; ", d.TopSellers.Take(3)) : "n/a")}")
        .ToString().TrimEnd();

    public static string BuildPrompt(CeoBriefData d, DateTime todayLocal) =>
        $"Bạn là trợ lý điều hành cho giám đốc công ty du lịch. Hôm nay {todayLocal:dd/MM/yyyy}.\n" +
        "Viết 5-8 câu tiếng Việt tổng kết tình hình từ CHÍNH XÁC các số dưới đây. " +
        "TUYỆT ĐỐI không bịa thêm số nào ngoài input, không dùng tiêu đề markdown, " +
        "giọng tự nhiên, đi thẳng vào ý chính, nêu rõ điều đáng lưu ý nhất:\n\n" +
        NumbersBlock(d);

    public static DigestMessage RenderFallback(CeoBriefData d, DateTime todayLocal)
        => Wrap(NumbersBlock(d), todayLocal);

    public static DigestMessage WrapAiReply(string aiProse, CeoBriefData d, DateTime todayLocal)
        => Wrap(aiProse.Trim() + "\n\n**Số liệu:**\n" + NumbersBlock(d), todayLocal);

    private static DigestMessage Wrap(string bodyMd, DateTime todayLocal)
        => new($"Bản tin điều hành {todayLocal:dd/MM}", bodyMd,
               SaleBriefBuilder.ToHtml(bodyMd),   // dùng chung 1 bộ đổi HTML → không lệch cách escape
               BriefTypes.Ceo);
}
