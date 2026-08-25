using System.Text;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat;

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

        new("prepare_meeting",
            "Chuẩn bị TRƯỚC KHI GẶP/GỌI 1 khách: tóm tắt khách là ai + nên nói gì + cần tránh gì. " +
            "Dùng khi 'sắp gặp khách X, chuẩn bị giúp', 'lát nữa gọi cho anh Y nói gì', 'brief khách Z trước khi họp'. " +
            "KHÁC review_customer: review CHẤM HẠNG khách (A–D, để phân loại); tool này dựng GỢI Ý HỘI THOẠI cho " +
            "cuộc gặp sắp tới. User nói 'đánh giá/xếp hạng' → review_customer; nói 'sắp gặp/sắp gọi/chuẩn bị' → tool này. " +
            "Điền customerName = đúng cụm định danh user đưa (TÊN, SỐ ĐIỆN THOẠI, hoặc MÃ KH) — hệ thống tự resolve. " +
            "customerId CHỈ điền khi biết id nội bộ dạng số nguyên nhỏ.",
            new[] { "customerId", "customerName" }, ActionKind.Internal, false, "Chuẩn bị gặp khách"),

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

    /// Tên action nằm sau cờ tính năng. Không có trong bảng này = luôn bật.
    private static readonly Dictionary<string, Func<IConfiguration, bool>> Gated =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["prepare_meeting"] = Bootstrap.FeatureFlags.MeetingBrief,
        };

    /// <summary>
    /// Danh mục action ĐANG MỞ. Đây là thứ được gửi cho AI — tool bị tắt thì AI không hề biết là
    /// có nó, nên không bao giờ gọi. Chặn ở đây đúng hơn là chặn lúc thực thi: chặn lúc thực thi
    /// nghĩa là AI vẫn hứa với người dùng rồi mới báo lỗi.
    /// </summary>
    public static IReadOnlyList<ActionTool> Enabled(IConfiguration cfg)
        => All.Where(a => !Gated.TryGetValue(a.Name, out var on) || on(cfg)).ToList();

    /// Đang mở không? Dùng cho chốt chặn thứ hai ở ActionExecutor (client cũ gửi thẳng tên action).
    public static bool IsEnabled(IConfiguration cfg, string? name)
        => !string.IsNullOrEmpty(name)
           && (!Gated.TryGetValue(name, out var on) || on(cfg));

    /// Catalog gọn nhúng vào prompt planner. Nhận list vào để chỗ gọi tự quyết định lấy
    /// <see cref="All"/> hay <see cref="Enabled"/> — tránh nhét IConfiguration vào lớp này.
    public static string CatalogForPrompt(IReadOnlyList<ActionTool> tools)
    {
        var sb = new StringBuilder();
        foreach (var a in tools)
        {
            var ps = a.Params.Length == 0 ? "(không tham số)" : string.Join(", ", a.Params);
            sb.Append("- ").Append(a.Name).Append(": ").Append(a.Description)
              .Append(" | params: ").Append(ps).Append('\n');
        }
        return sb.ToString();
    }
}
