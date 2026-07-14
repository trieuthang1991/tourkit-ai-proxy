using System.Text;

namespace TourkitAiProxy.Services.Chat;

public enum ActionKind { Mail, Internal, CrmQueue }

/// 1 "action" = 1 hành động GHI/nghiệp vụ trợ lý có thể đề xuất. Song song ChatTools (read).
public record ActionTool(
    string Name, string Description, string[] Params,
    ActionKind Kind, bool NeedsConfirm, string Title);

/// Catalog action — NGUỒN DUY NHẤT cho prompt planner + dispatch executor.
public static class ActionTools
{
    public static readonly IReadOnlyList<ActionTool> All = new List<ActionTool>
    {
        new("check_mail",
            "Kiểm tra & tóm tắt mail MỚI (sync IMAP + liệt kê chưa đọc). Dùng khi user nói 'kiểm tra mail mới', 'có mail nào mới không'.",
            new[] { "limit" }, ActionKind.Mail, false, "Kiểm tra hộp thư"),

        new("send_mail_reply",
            "Soạn & GỬI trả lời cho 1 email của khách. Dùng khi 'trả lời khách X', 'phản hồi mail khiếu nại'. " +
            "params: mailId (lấy từ danh sách mail vừa liệt kê/check_mail), tone (lich_su|than_thien|dam_phan|xin_loi), instruction.",
            new[] { "mailId", "tone", "instruction" }, ActionKind.Mail, true, "Trả lời email"),

        new("compose_mail",
            "Soạn & GỬI 1 email MỚI tới người nhận bất kỳ. params: to, subject, brief, tone.",
            new[] { "to", "subject", "brief", "tone" }, ActionKind.Mail, true, "Soạn email mới"),

        new("review_customer",
            "Đánh giá/xếp hạng 1 khách hàng (A–D + gợi ý). Dùng khi 'đánh giá khách X', 'review khách này'. " +
            "params: customerId (hoặc customerName để resolve), forceFresh.",
            new[] { "customerId", "customerName", "forceFresh" }, ActionKind.Internal, false, "Đánh giá khách hàng"),

        new("score_deal",
            "Chấm điểm 1 cơ hội bán hàng/deal. Dùng khi 'chấm deal X', 'đánh giá cơ hội của khách B'. " +
            "params: dealId (hoặc dealQuery để resolve).",
            new[] { "dealId", "dealQuery" }, ActionKind.Internal, false, "Chấm điểm deal"),

        new("assign_task",
            "GIAO VIỆC cho nhân viên. Dùng khi 'giao việc … cho …', 'tạo task cho nhân viên Y'. " +
            "params: workflowName, name, content, staffNames (CSV tên), prioritized(cao|tb|thap), startDate, dueDate, reminderMinutes. " +
            "startDate = ngày/giờ BẮT ĐẦU (nếu user nêu), dueDate = HẠN hoàn thành. " +
            "QUAN TRỌNG: startDate/dueDate PHẢI theo định dạng ISO CÓ GIỜ 'yyyy-MM-ddTHH:mm' theo GIỜ VIỆT NAM, " +
            "và phải GIỮ ĐÚNG GIỜ user nói — vd 'trước 20h hôm nay' → dueDate = <hôm nay>T20:00; " +
            "'9h sáng mai' → <ngày mai>T09:00; 'chiều mai' → T14:00. TUYỆT ĐỐI không bỏ giờ về T00:00 khi user đã nêu giờ.",
            new[] { "workflowName", "name", "content", "staffNames", "prioritized", "startDate", "dueDate", "reminderMinutes", "customerName", "bookingTicketId" },
            ActionKind.CrmQueue, true, "Giao việc"),

        new("create_appointment",
            "TẠO LỊCH HẸN CSKH cho khách. Dùng khi 'đặt lịch hẹn với khách X', 'hẹn tư vấn'. " +
            "params: customerName, careTitle, careDetail, startTime, endTime, reminderMinutes. " +
            "startTime/endTime PHẢI theo ISO CÓ GIỜ 'yyyy-MM-ddTHH:mm' giờ VN, GIỮ ĐÚNG giờ user nói (vd '14h30 mai' → <mai>T14:30), không bỏ về T00:00.",
            new[] { "customerName", "customerId", "careTitle", "careDetail", "startTime", "endTime", "reminderMinutes", "bookingTicketId" },
            ActionKind.CrmQueue, true, "Tạo lịch hẹn"),
    };

    public static ActionTool? Find(string? name)
        => string.IsNullOrEmpty(name) ? null
           : All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// Catalog gọn nhúng vào prompt planner.
    public static string CatalogForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var a in All)
        {
            var ps = a.Params.Length == 0 ? "(không tham số)" : string.Join(", ", a.Params);
            sb.Append("- ").Append(a.Name).Append(": ").Append(a.Description)
              .Append(" | params: ").Append(ps).Append('\n');
        }
        return sb.ToString();
    }
}
