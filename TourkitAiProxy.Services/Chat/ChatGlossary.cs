namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Từ điển domain nhúng vào system prompt phân tích (Chat-Analytics) — NGUỒN DUY NHẤT.
/// Mục tiêu: AI đọc số liệu ĐÚNG — luôn gọi TÊN thay vì Id/mã, diễn giải viết tắt + mã trạng thái,
/// và biết cách đọc tỉ lệ/tiền. Dùng chung cho cả JsonPlannerAgent lẫn NativeToolUseAgent.
/// Đa số dữ liệu upstream đã kèm *Name/*Label/*Formatted → khối này là LƯỚI AN TOÀN cho mã còn sót
/// + chuẩn hóa cách đọc. Const để ghép vào const prompt khác lúc biên dịch.
/// </summary>
public static class ChatGlossary
{
    public const string AnalysisBlock =
        "\n\nQUY TẮC ĐỌC SỐ LIỆU (bắt buộc): " +
        "Luôn gọi người/khách/nhân viên/tour bằng TÊN (các trường *Name, FullName, CreatorName, Assignee, Seller...), " +
        "TUYỆT ĐỐI không đọc Id hay chuỗi mã số (vd 4521, '4521,3310'). " +
        "Nếu một mục CHỈ có Id/mã số mà KHÔNG có tên đi kèm → KHÔNG đọc con số đó, chỉ nói 'chưa có tên'. " +
        "Số tiền: LUÔN RÚT GỌN nhất quán để khớp thẻ số liệu bên phải — từ 1 tỷ trở lên ghi 'X,XX tỷ' " +
        "(tối đa 2 chữ số thập phân, dùng dấu PHẨY làm thập phân, vd 85,96 tỷ); từ 1 triệu đến dưới 1 tỷ ghi 'X,XX triệu'; " +
        "dưới 1 triệu ghi số đầy đủ kèm 'đ'. TUYỆT ĐỐI KHÔNG ghi số tiền đầy đủ nhiều chữ số (vd 1.031.000.000 đồng) " +
        "và KHÔNG dùng dấu chấm làm phần thập phân trong văn phân tích. " +
        "Tỉ lệ ở dạng số thập phân (0.35) nghĩa là 35 phần trăm. " +
        "\n\nVIẾT TẮT (diễn giải thành cụm đầy đủ khi nói): " +
        "NCC = nhà cung cấp; KH = khách hàng; NV = nhân viên; CSKH = chăm sóc khách hàng; CV = công việc; " +
        "HĐ = hợp đồng; SĐT = số điện thoại; DT = doanh thu; LN = lợi nhuận; CP = chi phí; TT = thanh toán; " +
        "PT = phiếu thu; PC = phiếu chi; TB = trung bình; CN = chi nhánh; PB = phòng ban. " +
        "\n\nMÃ TRẠNG THÁI (nếu gặp số thô chưa có nhãn, diễn giải theo bảng): " +
        "Loại tour: 1 LandTour, 2 FIT, 3 GIT, 100 Booking, 101 dịch vụ lẻ, 102 Visa, 104 vé máy bay. " +
        "Cơ hội bán hàng: 1 tạo mới, 2 chờ xử lý, 3 đang xử lý, 4 đã xử lý, 5 hủy, 6 chốt đơn. " +
        "Nguồn phiếu: 1 website, 2 nội bộ, 3 đại lý, 4 Pancake. " +
        "Công việc: 1 chưa bắt đầu, 2 đang thực hiện, 3 đang kiểm tra, 4 hoàn thành, 5 hủy. " +
        "Ưu tiên công việc: 1 cao, 2 trung bình, 3 thấp. " +
        "Lịch hẹn CSKH: 1 tạo mới, 2 thành công, 3 không thành công, 4 đã xóa. " +
        "Phiếu: 4 phiếu thu, 5 phiếu chi. " +
        "Trạng thái tour: 101 sắp chạy, 102 đang chạy, 103 hoàn thành, 104 đã hủy, 106 báo giá, " +
        "107 hủy không đi, 108 chưa quyết toán, 109 đã quyết toán.";
}
