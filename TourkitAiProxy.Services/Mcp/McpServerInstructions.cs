using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// <c>instructions</c> gửi cho client MCP lúc bắt tay — client thường nhét nó vào system prompt của AI.
/// Rút từ luật của planner trợ lý (<c>JsonPlannerAgent</c>, khối QUY TẮC) để AI phía người dùng chọn tool
/// giống hệt trợ lý.
///
/// <para><b>Ngày hôm nay tính lúc có request, theo giờ VIỆT NAM.</b> Máy chủ chạy chế độ không trạng thái
/// nên SDK dựng lại tuỳ chọn cho MỖI request (<c>ConfigureSessionOptions</c>) — ngày luôn mới, không đông
/// cứng từ lúc khởi động. Lấy ngày UTC thì 0h–7h sáng VN ra ngày hôm trước; AI không biết hôm nay là ngày
/// nào thì "tháng này" thành tháng mà mô hình được huấn luyện.</para>
/// </summary>
public static class McpServerInstructions
{
    private static readonly string[] Thu = { "Chủ nhật", "Thứ hai", "Thứ ba", "Thứ tư", "Thứ năm", "Thứ sáu", "Thứ bảy" };

    public static string Build(DateTime utcNow)
    {
        var vn = DigestDue.NowVn(utcNow);
        var today = DateOnly.FromDateTime(vn);
        var dau = new DateOnly(today.Year, today.Month, 1);
        var cuoi = dau.AddMonths(1).AddDays(-1);
        var dauTruoc = dau.AddMonths(-1);
        var cuoiTruoc = dau.AddDays(-1);

        return $"""
Máy chủ này trả số liệu CRM TourKit của CHÍNH người dùng, theo đúng quyền CRM của họ (không xem được công ty khác).

Ngày giờ:
- Hôm nay là {Thu[(int)today.DayOfWeek]}, {today:yyyy-MM-dd} (giờ Việt Nam, UTC+7). Mọi mốc "hôm nay", "tuần này", "tháng này", "năm nay" tính theo ngày này.
- "Tháng này" = startDate {dau:yyyy-MM-dd}, endDate {cuoi:yyyy-MM-dd}. "Tháng trước" = {dauTruoc:yyyy-MM-dd} → {cuoiTruoc:yyyy-MM-dd}. "Năm ngoái" = năm {today.Year - 1}.
- Tham số ngày luôn gửi dạng yyyy-MM-dd.

Chọn công cụ:
- Hỏi về một tour cụ thể: gọi tour_catalog trước để tìm đúng tour, rồi mới gọi tour_detail.
- Hỏi về một NHÂN VIÊN (doanh số, hiệu suất, KPI của một người): dùng employee_performance, không dùng top_sellers.
- Lọc theo thị trường hay nhân viên: cứ điền đúng tên người dùng nói vào marketName / employeeName — máy chủ tự đổi sang mã, đừng tự đoán mã.
- Chỉ trả lời bằng số liệu công cụ trả về; không có số thì nói là không có, không bịa.
""";
    }
}
