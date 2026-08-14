using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Digest;

public record DealLine(int DealId, string Title, string? CustomerName, int WinRate, int SilentDays, string? StatusText);
public record ApptLine(string Time, string Title, string? CustomerName);

/// 1 việc cần làm. <paramref name="Priority"/> = "Cao"/"Trung bình"/"Thấp" (nhãn từ CRM).
/// <paramref name="IsOverdue"/> = đã trễ hạn → hiện dấu trước tiêu đề để nhặt mắt ngay.
public record TaskLine(string Title, string? Priority, bool IsOverdue);
public record CustomerLine(string Name, string Rank, int DaysSinceLastBooking);
public record QuoteLine(string Title, string? CustomerName, int DaysSinceUpdate);

public record SaleBriefInput(
    string Username, string? FullName,
    List<DealLine> CoolingDeals, List<ApptLine> TodayAppointments,
    List<CustomerLine> SleepingVips, List<QuoteLine> StaleQuotes,
    int TenantMailPending, int TenantMailQuoteRequests,
    List<DealLine> HygieneDeals, List<PaymentAlert> MyPaymentAlerts,
    bool MailSourceOk,
    // Việc cần làm hôm nay + số lịch hẹn đã quá hạn. Mặc định rỗng/0 để chỗ gọi cũ không vỡ.
    List<TaskLine>? TodayTasks = null,
    int OverdueTaskCount = 0,
    int OverdueAppointments = 0,
    // Hộp thư cần cờ RIÊNG vì nó luôn in một dòng, không như các mục khác (danh sách rỗng thì tự
    // ẩn). Tắt mục này mà truyền số 0 thì bản tin ghi "0 thư chờ xử lý" — nói sai, vì thực tế là
    // KHÔNG KIỂM, không phải không có thư.
    bool ShowMailbox = true);

/// <summary>
/// Dựng nội dung bản tin cho nhân viên bán hàng — RULE THUẦN, KHÔNG gọi AI (không tốn lượt).
///
/// <para>Trả lời đúng một câu: <i>"sáng nay tôi gọi ai trước?"</i> Nên mỗi mục cắt top 5 —
/// bản tin 30 dòng thì không ai đọc, và đọc không hết thì thà đừng gửi.</para>
///
/// <para><b>Không bao giờ rỗng:</b> không có việc gấp thì vẫn gửi một dòng chúc. Bản tin
/// im lặng vài ngày là người dùng thôi mở, sau đó ngày có việc thật cũng bị bỏ qua.</para>
///
/// <para>Sinh cả markdown lẫn HTML từ CÙNG một nguồn để hai bản không bao giờ lệch nội dung.</para>
/// </summary>
public static class SaleBriefBuilder
{
    private const int TopN = 5;
    private const int TopHygiene = 3;   // mục "dọn dẹp" ít cấp thiết hơn → cắt sâu hơn

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi) + "đ";

    /// Nhãn hiển thị: tiêu đề rỗng thì rơi về tên khách, cùng lắm mới ghi chung chung.
    /// Dữ liệu thật có cơ hội không đặt tiêu đề → trước đây in ra dòng '**** — Tên khách' vô nghĩa.
    private static string Label(string? title, string? customerName)
        => HasText(title) ? title!.Trim()
         : HasText(customerName) ? customerName!.Trim()
         : "(chưa đặt tên)";

    /// Chuỗi có nội dung thật? Loại cả trường hợp CRM trả dấu gạch thay cho giá trị rỗng —
    /// in ra 'ưu tiên —' thì vô nghĩa mà còn làm dòng dài thêm.
    private static bool HasText(string? s)
        => !string.IsNullOrWhiteSpace(s) && s!.Trim() is not ("-" or "—" or "–" or "n/a");

    /// Mọi việc đều trễ thì nói thẳng — con số '50/50 trễ hạn' không cho thêm thông tin gì.
    private static string OverdueTag(int overdue, int total)
        => overdue <= 0 ? ""
         : overdue >= total ? " · TẤT CẢ đều trễ hạn"
         : $" · {overdue} trễ hạn";

    public static DigestMessage Build(SaleBriefInput input, DateTime todayLocal)
    {
        var md = new StringBuilder();
        int sections = 0;

        void Section(string heading, IReadOnlyList<string> lines, int total, string unit)
        {
            if (lines.Count == 0) return;
            sections++;
            md.AppendLine($"**{heading}**");
            foreach (var l in lines) md.AppendLine($"- {l}");
            var more = total - lines.Count;
            if (more > 0) md.AppendLine($"- … và {more} {unit} khác");
            md.AppendLine();
        }

        Section($"📞 Cơ hội cần gọi lại ({input.CoolingDeals.Count})",
            input.CoolingDeals.Take(TopN).Select(d =>
                $"**{Label(d.Title, d.CustomerName)}**"
                + (d.CustomerName != null && !string.IsNullOrWhiteSpace(d.Title) ? $" — {d.CustomerName}" : "")
                + $" · im lặng {d.SilentDays} ngày"
                // WinRate = 0 nghĩa là CHƯA AI chấm, KHÔNG phải "khả năng chốt bằng 0" — hai điều
                // khác nhau hoàn toàn. In "0%" làm người đọc tưởng cơ hội vô vọng nên bỏ luôn.
                + (d.WinRate > 0 ? $" · khả năng chốt {d.WinRate}%" : "")).ToList(),
            input.CoolingDeals.Count, "cơ hội");

        // Quá hạn ghi ngay trên TIÊU ĐỀ mục để thấy trước khi đọc từng dòng.
        Section($"🗓 Lịch hẹn hôm nay ({input.TodayAppointments.Count})"
                + (input.OverdueAppointments > 0 ? $" · {input.OverdueAppointments} quá hạn" : ""),
            input.TodayAppointments.Take(TopN).Select(a =>
                $"{a.Time} — {a.Title}{(a.CustomerName != null ? $" ({a.CustomerName})" : "")}").ToList(),
            input.TodayAppointments.Count, "lịch hẹn");

        var tasks = input.TodayTasks ?? new List<TaskLine>();
        Section($"✅ Việc cần làm hôm nay ({tasks.Count})" + OverdueTag(input.OverdueTaskCount, tasks.Count),
            tasks.Take(TopN).Select(t =>
                (t.IsOverdue ? "⚠️ " : "") + t.Title
                + (HasText(t.Priority) ? $" · ưu tiên {t.Priority!.ToLowerInvariant()}" : "")).ToList(),
            tasks.Count, "việc");

        Section($"💰 Khách sắp đi còn thiếu tiền ({input.MyPaymentAlerts.Count})",
            input.MyPaymentAlerts.Take(TopN).Select(p =>
                $"**{p.Title}** — {p.CustomerName ?? "?"} thiếu {Vnd(p.Outstanding)}, còn {p.DaysLeft} ngày").ToList(),
            input.MyPaymentAlerts.Count, "tour");

        Section($"💤 Khách quen lâu không chăm ({input.SleepingVips.Count})",
            input.SleepingVips.Take(TopN).Select(c =>
                $"{c.Name} (hạng {c.Rank}) — {c.DaysSinceLastBooking} ngày chưa mua lại").ToList(),
            input.SleepingVips.Count, "khách");

        Section($"📄 Báo giá chưa ai động ({input.StaleQuotes.Count})",
            input.StaleQuotes.Take(TopN).Select(q =>
                $"{Label(q.Title, q.CustomerName)}"
                + (HasText(q.CustomerName) && HasText(q.Title) ? $" — {q.CustomerName}" : "")
                + $" · {q.DaysSinceUpdate} ngày chưa cập nhật").ToList(),
            input.StaleQuotes.Count, "báo giá");

        Section($"🧹 Cơ hội cần dọn ({input.HygieneDeals.Count})",
            input.HygieneDeals.Take(TopHygiene).Select(d =>
                $"{Label(d.Title, d.CustomerName)} — kẹt \"{(HasText(d.StatusText) ? d.StatusText : "không rõ trạng thái")}\" {d.SilentDays} ngày, chưa có bước tiếp theo").ToList(),
            input.HygieneDeals.Count, "cơ hội");

        // Hộp thư (của cả công ty) — LUÔN có 1 dòng, và CỐ Ý không tính vào `sections`:
        // nếu tính thì lời chúc "hôm nay rảnh" không bao giờ xuất hiện, người dùng mất tín hiệu đó.
        if (input.ShowMailbox)
            md.AppendLine(input.MailSourceOk
                ? $"📬 Hộp thư công ty: {input.TenantMailPending} thư chờ xử lý ({input.TenantMailQuoteRequests} hỏi giá)."
                : "📬 Hộp thư: n/a (không đọc được).");

        if (sections == 0)
            md.Insert(0, "Hôm nay chưa có việc gấp 🎉 — dành thời gian chăm khách cũ nhé.\n\n");

        var title = $"Bản tin sáng {todayLocal:dd/MM} — {input.FullName ?? input.Username}";
        var bodyMd = md.ToString().TrimEnd();
        return new DigestMessage(title, bodyMd, ToHtml(bodyMd), BriefTypes.Sale);
    }

    // ── AI tinh chỉnh (số do máy chủ lấy, AI chỉ sắp xếp lại cho đọc được) ───────────
    //
    // Vì sao cần: bản rule ở trên in ĐỦ mọi mục vượt ngưỡng, mỗi mục cắt 5 dòng. Gặp CRM dùng lâu
    // thì thành bảng tồn kho — chạy thật trên erp.tourkit.vn ra "61 việc · 50 trễ hạn" và
    // "và 56 việc khác", người đọc không biết bắt đầu từ đâu. AI đọc CÙNG bộ dữ kiện đó rồi chọn
    // ra việc đáng làm sáng nay.
    //
    // Ranh giới giống bản tin điều hành: SỐ DO MÁY CHỦ LẤY, AI CHỈ VIẾT LỜI. AI không được bịa
    // thêm số, và AI lỗi/hết lượt thì rơi về bản rule — bản tin không bao giờ mất.

    /// <summary>
    /// Dữ kiện thô đưa cho AI. Cố ý KHÔNG đưa bản markdown đã render: đưa bản đã render thì AI có
    /// xu hướng chép lại y nguyên (kể cả mấy dòng "và 56 việc khác"), thành ra không tinh chỉnh gì.
    /// Đưa dữ kiện dạng danh sách phẳng thì nó mới thật sự chọn và xếp lại.
    /// </summary>
    public static string BuildPrompt(SaleBriefInput input, DateTime todayLocal, int maxItems)
    {
        var f = new StringBuilder();
        void List<T>(string label, IReadOnlyList<T> items, Func<T, string> fmt, int take = 12)
        {
            if (items.Count == 0) return;
            f.AppendLine($"{label} (tổng {items.Count}):");
            foreach (var x in items.Take(take)) f.AppendLine("  - " + fmt(x));
            if (items.Count > take) f.AppendLine($"  - (còn {items.Count - take} dòng nữa, không liệt kê)");
        }

        List("CƠ HỘI IM LẶNG cần gọi lại", input.CoolingDeals,
            d => $"{Label(d.Title, d.CustomerName)} · im lặng {d.SilentDays} ngày"
               + (d.WinRate > 0 ? $" · khả năng chốt {d.WinRate}%" : "")
               + (HasText(d.StatusText) ? $" · trạng thái {d.StatusText}" : ""));
        List("LỊCH HẸN HÔM NAY", input.TodayAppointments, a => $"{a.Time} {a.Title} ({a.CustomerName})");
        List("VIỆC CẦN LÀM HÔM NAY", input.TodayTasks ?? new(),
            t => $"{t.Title}{(HasText(t.Priority) ? $" · ưu tiên {t.Priority}" : "")}{(t.IsOverdue ? " · ĐÃ TRỄ HẠN" : "")}");
        List("TOUR SẮP ĐI MÀ KHÁCH CHƯA TRẢ ĐỦ", input.MyPaymentAlerts,
            p => $"{Label(p.Title, p.CustomerName)} · còn thiếu {Vnd(p.Outstanding)} · khởi hành {p.DepartureDate:dd/MM} (còn {p.DaysLeft} ngày)");
        List("KHÁCH HẠNG A/B LÂU CHƯA MUA LẠI", input.SleepingVips,
            c => $"{c.Name} (hạng {c.Rank}) · {c.DaysSinceLastBooking} ngày");
        List("BÁO GIÁ LÂU CHƯA CẬP NHẬT", input.StaleQuotes,
            q => $"{Label(q.Title, q.CustomerName)} · {q.DaysSinceUpdate} ngày");
        List("CƠ HỘI KẸT TRẠNG THÁI cần dọn hồ sơ", input.HygieneDeals,
            d => $"{Label(d.Title, d.CustomerName)} · kẹt \"{d.StatusText}\" {d.SilentDays} ngày");

        if (input.OverdueTaskCount > 0) f.AppendLine($"Số việc đã trễ hạn (tích luỹ): {input.OverdueTaskCount}");
        if (input.OverdueAppointments > 0) f.AppendLine($"Số lịch hẹn quá hạn (tích luỹ): {input.OverdueAppointments}");
        if (input.ShowMailbox)
            f.AppendLine(input.MailSourceOk
                ? $"Hộp thư công ty: {input.TenantMailPending} thư chờ xử lý, {input.TenantMailQuoteRequests} thư hỏi giá"
                : "Hộp thư công ty: không đọc được");

        return $"Bạn là trợ lý của nhân viên bán hàng tour, tên {input.FullName ?? input.Username}. "
             + $"Hôm nay {todayLocal:dd/MM/yyyy}.\n"
             + "Từ CHÍNH XÁC các dữ kiện dưới đây, viết bản tin sáng ngắn gọn bằng tiếng Việt trả lời "
             + "đúng một câu hỏi: SÁNG NAY LÀM GÌ TRƯỚC.\n\n"
             + "Quy tắc:\n"
             + $"- Chọn TỐI ĐA {maxItems} việc đáng làm nhất. Bỏ phần còn lại, đừng liệt kê cho đủ.\n"
             + "- Giữ NGUYÊN VĂN tên khách và tiêu đề cơ hội để người đọc tra được trong CRM.\n"
             + "- TUYỆT ĐỐI không bịa thêm số nào ngoài dữ kiện. Không suy ra số mới.\n"
             // Trạng thái là chữ TỰ DO mỗi công ty tự đặt. Dữ liệu thật gặp đủ kiểu:
             // "Call/Trao đổi lần 1", "CS sau 15 ngày ( Ko Feedback ==> Kéo Hủy )", "Chờ xử lý đây",
             // "Gọi điện lần 1  10/06/2026". AI mà suy diễn từ mấy chuỗi này sẽ kết luận sai.
             + "- Tên trạng thái là chữ do TỪNG CÔNG TY tự đặt, có thể lộn xộn hoặc dính ngày tháng. "
             + "Chỉ dùng để nhắc lại nguyên văn, TUYỆT ĐỐI không suy diễn ý nghĩa hay đoán bước tiếp theo "
             + "từ tên trạng thái.\n"
             // Học từ bản tin điều hành: lần chạy thật đầu tiên AI lấy con số tồn đọng lớn nhất làm
             // tiêu điểm rồi viết "cần xử lý ngay" — báo động sai về việc nằm đó nhiều năm.
             + "- Số 'tích luỹ'/'đã trễ hạn' là nợ cũ, KHÔNG phải việc phát sinh hôm nay. Nếu có nhắc "
             + "thì nói như việc dọn dần, đừng gọi là khẩn cấp.\n"
             + "- Việc CÓ GIỜ trong ngày (lịch hẹn) và tour sắp khởi hành mà khách chưa trả đủ thì ưu "
             + "tiên cao nhất — đó là loại trễ một ngày là mất.\n"
             + "- Cơ hội im lặng ÍT ngày đáng gọi hơn cơ hội im lặng rất lâu: im 3 ngày còn cứu được, "
             + "im 60 ngày gần như đã nguội.\n"
             + "- Dùng gạch đầu dòng, có thể in đậm bằng **…**. Không dùng tiêu đề markdown (#).\n"
             + "- Không mở đầu bằng lời chào hay giải thích, vào thẳng việc.\n\n"
             + "DỮ KIỆN:\n" + f.ToString().TrimEnd();
    }

    /// <summary>
    /// Bọc lời AI thành bản tin. Đính kèm DƯỚI một dòng tổng số để người đọc biết mình đang xem
    /// phần đã chọn lọc, không phải toàn bộ — thiếu dòng này thì họ tưởng chỉ có mấy việc đó.
    /// </summary>
    public static DigestMessage WrapAiReply(string aiProse, SaleBriefInput input, DateTime todayLocal)
    {
        var totals = new List<string>(4);
        if (input.CoolingDeals.Count > 0) totals.Add($"{input.CoolingDeals.Count} cơ hội cần gọi");
        if ((input.TodayTasks?.Count ?? 0) > 0 || input.OverdueTaskCount > 0)
            totals.Add($"{input.TodayTasks?.Count ?? 0} việc ({input.OverdueTaskCount} trễ)");
        if (input.HygieneDeals.Count > 0) totals.Add($"{input.HygieneDeals.Count} cơ hội cần dọn");
        if (input.StaleQuotes.Count > 0) totals.Add($"{input.StaleQuotes.Count} báo giá bỏ dở");

        var body = aiProse.Trim();
        if (totals.Count > 0)
            body += $"\n\n_Đang có tổng cộng: {string.Join(" · ", totals)}. Mở app để xem đầy đủ._";

        var title = $"Bản tin sáng {todayLocal:dd/MM} — {input.FullName ?? input.Username}";
        return new DigestMessage(title, body, ToHtml(body), BriefTypes.Sale);
    }

    /// <summary>
    /// Markdown tối giản → HTML cho email.
    /// <b>Escape TRƯỚC, đổi <c>**x**</c> thành thẻ SAU</b> — làm ngược thì thẻ mình tạo cũng bị
    /// escape (email in ra chữ "&lt;b&gt;"). Nhờ thứ tự này mà tên khách / tiêu đề cơ hội do
    /// người dùng nhập bị vô hiệu hoá, còn phần in đậm mình chủ động tạo thì giữ nguyên.
    /// </summary>
    internal static string ToHtml(string bodyMd)
        => "<div style=\"font-family:sans-serif;line-height:1.6\">"
         + Regex.Replace(System.Net.WebUtility.HtmlEncode(bodyMd), @"\*\*(.+?)\*\*", "<b>$1</b>")
                .Replace("\n", "<br>")
         + "</div>";
}
