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
    int OverdueAppointments = 0);

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
                $"**{d.Title}**{(d.CustomerName != null ? $" — {d.CustomerName}" : "")}"
                + $" · im lặng {d.SilentDays} ngày · khả năng chốt {d.WinRate}%").ToList(),
            input.CoolingDeals.Count, "cơ hội");

        // Quá hạn ghi ngay trên TIÊU ĐỀ mục để thấy trước khi đọc từng dòng.
        Section($"🗓 Lịch hẹn hôm nay ({input.TodayAppointments.Count})"
                + (input.OverdueAppointments > 0 ? $" · {input.OverdueAppointments} quá hạn" : ""),
            input.TodayAppointments.Take(TopN).Select(a =>
                $"{a.Time} — {a.Title}{(a.CustomerName != null ? $" ({a.CustomerName})" : "")}").ToList(),
            input.TodayAppointments.Count, "lịch hẹn");

        var tasks = input.TodayTasks ?? new List<TaskLine>();
        Section($"✅ Việc cần làm hôm nay ({tasks.Count})"
                + (input.OverdueTaskCount > 0 ? $" · {input.OverdueTaskCount} trễ hạn" : ""),
            tasks.Take(TopN).Select(t =>
                (t.IsOverdue ? "⚠️ " : "") + t.Title
                + (t.Priority != null ? $" · ưu tiên {t.Priority.ToLowerInvariant()}" : "")).ToList(),
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
                $"{q.Title}{(q.CustomerName != null ? $" — {q.CustomerName}" : "")}"
                + $" · {q.DaysSinceUpdate} ngày chưa cập nhật").ToList(),
            input.StaleQuotes.Count, "báo giá");

        Section($"🧹 Cơ hội cần dọn ({input.HygieneDeals.Count})",
            input.HygieneDeals.Take(TopHygiene).Select(d =>
                $"{d.Title} — kẹt \"{d.StatusText ?? "?"}\" {d.SilentDays} ngày, chưa có bước tiếp theo").ToList(),
            input.HygieneDeals.Count, "cơ hội");

        // Hộp thư (của cả công ty) — LUÔN có 1 dòng, và CỐ Ý không tính vào `sections`:
        // nếu tính thì lời chúc "hôm nay rảnh" không bao giờ xuất hiện, người dùng mất tín hiệu đó.
        md.AppendLine(input.MailSourceOk
            ? $"📬 Hộp thư công ty: {input.TenantMailPending} thư chờ xử lý ({input.TenantMailQuoteRequests} hỏi giá)."
            : "📬 Hộp thư: n/a (không đọc được).");

        if (sections == 0)
            md.Insert(0, "Hôm nay chưa có việc gấp 🎉 — dành thời gian chăm khách cũ nhé.\n\n");

        var title = $"Bản tin sáng {todayLocal:dd/MM} — {input.FullName ?? input.Username}";
        var bodyMd = md.ToString().TrimEnd();
        return new DigestMessage(title, bodyMd, ToHtml(bodyMd), BriefTypes.Sale);
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
