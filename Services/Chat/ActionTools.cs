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
            "Đánh giá/xếp hạng 1 khách hàng (A–D + gợi ý). Dùng khi 'đánh giá khách X', 'review khách này', " +
            "'đánh giá khách có SĐT …', 'đánh giá khách mã KH_…'. LUÔN gọi tool này khi user muốn đánh giá 1 khách — " +
            "KHÔNG hỏi lại tên nếu user đã cho ĐỊNH DANH bất kỳ. Điền customerName = đúng cụm định danh user đưa: " +
            "TÊN, hoặc SỐ ĐIỆN THOẠI (vd '0982385108'), hoặc MÃ KH (vd 'KH_00041133') — hệ thống tự resolve ra khách. " +
            "customerId CHỈ điền khi biết id nội bộ dạng số nguyên nhỏ (vd 15878); nếu là SĐT/mã thì để vào customerName.",
            new[] { "customerId", "customerName", "forceFresh" }, ActionKind.Internal, false, "Đánh giá khách hàng"),

        new("score_deal",
            "Chấm điểm 1 cơ hội bán hàng/deal. Dùng khi 'chấm deal X', 'đánh giá cơ hội của khách B'. " +
            "params: dealId (nếu biết id) HOẶC dealQuery. LUÔN điền dealQuery = đúng cụm user nói để nhận diện " +
            "cơ hội (tên khách hàng, tiêu đề cơ hội, hoặc mã đơn) — vd 'chấm deal của khách em thủy' → dealQuery='em thủy'. " +
            "TUYỆT ĐỐI không bỏ trống dealQuery khi user đã nêu khách/cơ hội.",
            new[] { "dealId", "dealQuery" }, ActionKind.Internal, false, "Chấm điểm deal"),

        new("assign_task",
            "GIAO VIỆC cho nhân viên. Dùng khi 'giao việc … cho …', 'tạo task cho nhân viên Y'. " +
            "GỌI NGAY với thông tin user ĐÃ cho — TUYỆT ĐỐI KHÔNG hỏi lại thêm chi tiết (ưu tiên/loại/workflow/khách...). " +
            "CHỈ cần nhân viên + nội dung (hoặc suy từ câu) là đủ để gọi; thẻ xác nhận sẽ cho user tự chỉnh phần còn lại. " +
            "Các field còn lại có MẶC ĐỊNH: name/content = suy từ câu user nói, ưu tiên = trung bình (tb), " +
            "không gắn khách/workflow nếu user không nêu. " +
            "params: workflowName?, name (tiêu đề ngắn, suy từ câu), content (nội dung chi tiết), staffNames (CSV tên), " +
            "prioritized(cao|tb|thap, mặc định tb), startDate?, dueDate?, reminderMinutes?. " +
            "startDate = ngày/giờ BẮT ĐẦU (user KHÔNG nêu → BỎ TRỐNG, backend tự mặc định = thời điểm giao việc); dueDate = HẠN hoàn thành. " +
            "QUAN TRỌNG: startDate/dueDate PHẢI theo định dạng ISO CÓ GIỜ 'yyyy-MM-ddTHH:mm' theo GIỜ VIỆT NAM, " +
            "và phải GIỮ ĐÚNG GIỜ user nói — vd 'trước 20h hôm nay' → dueDate = <hôm nay>T20:00; " +
            "'9h sáng mai' → <ngày mai>T09:00; 'chiều mai' → T14:00. TUYỆT ĐỐI không bỏ giờ về T00:00 khi user đã nêu giờ.",
            new[] { "workflowName", "name", "content", "staffNames", "prioritized", "startDate", "dueDate", "reminderMinutes", "customerName", "bookingTicketId" },
            ActionKind.CrmQueue, true, "Giao việc"),

        new("create_appointment",
            "TẠO LỊCH HẸN CSKH cho khách. Dùng khi 'đặt lịch hẹn với khách X', 'hẹn tư vấn'. " +
            "GỌI NGAY với thông tin user ĐÃ cho — TUYỆT ĐỐI KHÔNG hỏi lại thêm chi tiết (kết thúc/nhắc/loại/người phụ trách). " +
            "CHỈ cần khách + thời điểm bắt đầu là đủ để gọi; thẻ xác nhận sẽ cho user tự chỉnh phần còn lại. " +
            "Các field còn lại có MẶC ĐỊNH: kết thúc = bắt đầu + 1 tiếng, loại = lịch hẹn, nhắc = không, người phụ trách = người tạo. " +
            "params: customerName (tên/SĐT/mã khách), careTitle (suy ra từ câu, vd 'Tư vấn tour'), careDetail?, staffName? (chỉ khi user nêu rõ), " +
            "typeSchedule? (0 mặc định), startTime, endTime?, reminderMinutes?. " +
            "startTime/endTime theo ISO CÓ GIỜ 'yyyy-MM-ddTHH:mm' giờ VN, GIỮ ĐÚNG giờ user nói (vd '14h30 mai' → <mai>T14:30), không bỏ về T00:00.",
            new[] { "customerName", "customerId", "careTitle", "careDetail", "staffName", "typeSchedule", "startTime", "endTime", "reminderMinutes", "bookingTicketId" },
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
