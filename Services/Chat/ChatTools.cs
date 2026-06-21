using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Chat;

/// 1 "tool" = 1 endpoint GET read-only của TourKit.Api mà AI có thể chọn để lấy số liệu.
public record ChatTool(
    string Name,
    string Description,
    string Path,                       // path TourKit.Api, vd "/api/dashboard/kpi"
    string[] Params,                   // query keys AI được phép điền
    string Kind,                       // gợi ý loại dữ liệu (cho stats + panel)
    string Title,                      // tiêu đề panel phải
    Dictionary<string, string>? Defaults = null  // query mặc định (vd pageSize=20)
);

/// <summary>
/// Catalog tool — NGUỒN DUY NHẤT cho cả prompt planner lẫn dispatch. Thêm tool = thêm 1 entry.
/// Chỉ READ/GET (đọc-phân tích), không có thao tác ghi (CreateTicket… nằm ngoài phạm vi).
/// </summary>
public static class ChatTools
{
    // Surface AI chuẩn của TourKit: /api/ai/* — envelope đồng nhất {section,title,total,summary,items}.
    public static readonly IReadOnlyList<ChatTool> All = new List<ChatTool>
    {
        new("financial_summary",
            "Bảng CHI TIẾT ĐẦY ĐỦ 12 chỉ số tài chính 1 kỳ (thực thu, CÔNG NỢ, thực chi, lợi nhuận gộp/ròng, hoa hồng...). " +
            "CHỈ dùng khi người dùng hỏi CHI TIẾT/ĐẦY ĐỦ chỉ số, hoặc hỏi đích danh thực thu/công nợ/thực chi/lợi nhuận ròng. " +
            "KHÔNG dùng cho câu doanh thu/lợi nhuận/chi phí ĐƠN GIẢN ('doanh thu tháng này') → dùng cashflow.",
            "/api/ai/financial-summary",
            new[] { "startDate", "endDate", "month", "year", "dateType", "branch" },
            "kpi", "Chi tiết tài chính"),

        new("cashflow",
            "MẶC ĐỊNH cho câu hỏi 'doanh thu / lợi nhuận / chi phí (tháng/kỳ này)' — trả về doanh thu + chi phí + lợi nhuận GỌN GÀNG (kèm biểu đồ). " +
            "Cũng dùng để so sánh nhiều tháng / xu hướng (groupBy=month) hoặc theo ngày (groupBy=day). " +
            "BẮT BUỘC startDate + endDate. 'tháng này' → startDate=đầu tháng, endDate=cuối tháng.",
            "/api/ai/cashflow",
            new[] { "startDate", "endDate", "groupBy" },
            "cashflow", "Doanh thu & Lợi nhuận",
            new() { ["groupBy"] = "month" }),

        new("marketing",
            "Hiệu quả marketing: khách đến từ kênh/nguồn nào (Facebook, Zalo, giới thiệu...). Dùng cho cơ cấu nguồn KH.",
            "/api/ai/marketing",
            new[] { "year", "startDate", "endDate" },
            "marketing", "Hiệu quả Marketing"),

        new("departures",
            "Các tour SẮP KHỞI HÀNH (điều hành khởi hành).",
            "/api/ai/departures",
            Array.Empty<string>(),
            "tours", "Tour sắp khởi hành"),

        new("top_customers",
            "Top khách hàng mua nhiều nhất (theo doanh số). Bỏ trống ngày = tháng này.",
            "/api/ai/top-customers",
            new[] { "startDate", "endDate" },
            "topcustomers", "Top khách hàng"),

        new("top_sellers",
            "Top nhân viên/seller doanh số cao nhất. Bỏ trống ngày = tháng này.",
            "/api/ai/top-sellers",
            new[] { "startDate", "endDate", "dateType" },
            "topsellers", "Top Seller"),

        new("employee_performance",
            "Báo cáo HIỆU SUẤT / KPI CHI TIẾT từng nhân viên sale: số Data KH, chăm sóc KH, cơ hội (tổng/mới/chốt), " +
            "tổng đơn hàng, KH mua 1 lần / mua lại, doanh thu, tỉ lệ chốt đơn, tỉ lệ chuyển đổi cơ hội, giá trị TB đơn — " +
            "mỗi chỉ số kèm % tăng/giảm so KỲ TRƯỚC. " +
            "DÙNG cho 'hiệu suất nhân viên', 'KPI nhân viên/sale', 'tỉ lệ chốt đơn của sale', 'nhân viên nào làm hiệu quả', 'so với kỳ trước'. " +
            "Lọc 1 nhân viên theo TÊN (employeeName — proxy tự đổi sang id) hoặc 1 chi nhánh (branch). " +
            "KHÁC top_sellers (chỉ xếp hạng doanh số top 10) — đây là BỘ CHỈ SỐ ĐẦY ĐỦ của từng người. Bỏ trống ngày = tháng này.",
            "/api/ai/employee-performance",
            new[] { "startDate", "endDate", "employeeId", "employeeName", "branch" },
            "employees", "Hiệu suất nhân viên"),

        new("tours",
            "Danh sách tour. " +
            "tourType: 1=LandTour, 2=FIT, 3=GIT, 100=Booking, 101=DV lẻ, 102=Visa, 104=Vé bay. " +
            "status (trạng thái tour, mặc định -1=tất cả). Status DYNAMIC per-tenant — danh sách " +
            "đầy đủ ở /api/ai/reference Lookups.TourStatuses (array {LoaiDonHang, Value, Name}). " +
            "Default chuẩn: 101=Sắp chạy, 102=Đang chạy, 103=Hoàn thành, 104=Đã hủy, " +
            "106=Báo giá, 107=Hủy không đi, 108=Chưa quyết toán, 109=Đã quyết toán, 110=Kích hoạt giá. " +
            "DÙNG cho 'tour FIT đang mở/sắp chạy' (status=101), 'tour đã hủy' (status=104). " +
            "Cũng lọc: tên (tourName), ngày khởi hành, thị trường (marketName), chi nhánh.",
            "/api/ai/tours",
            new[] { "tourType", "tourName", "status", "startDate", "endDate", "marketId", "marketName", "branch", "pageIndex", "pageSize" },
            "tours", "Danh sách tour",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("booking_tickets",
            "Cơ hội bán hàng / lead khách hàng. " +
            "trangThai (trạng thái): 1=Tạo mới, 2=Chờ xử lý, 3=Đang xử lý, 4=Đã xử lý, 5=Hủy, 6=Chốt đơn. " +
            "nguon (nguồn): 1=Website, 2=Nội bộ, 3=Đại lý, 4=Pancake. " +
            "DÙNG cho 'cơ hội/lead chờ xử lý' (trangThai=2), 'lead Pancake' (nguon=4), " +
            "'khách/lead THUỘC THỊ TRƯỜNG X' (marketName). Cũng lọc theo nhân viên phụ trách + khoảng ngày.",
            "/api/ai/booking-tickets",
            new[] { "keyword", "trangThai", "nguon", "nhanVienPhuTrach", "marketId", "marketName", "startDate", "endDate", "pageIndex", "pageSize" },
            "tickets", "Cơ hội bán hàng",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("tasks",
            "Công việc (task) cần làm. " +
            "tabFilter: 0=Tất cả, 1=Sắp tới, 2=Trễ hạn, 3=Hôm nay, 4=Tuần này, 5=Tháng này. " +
            "trangThai: 1=Chưa bắt đầu, 2=Đang thực hiện, 3=Đang kiểm tra, 4=Hoàn thành, 5=Hủy. " +
            "mucDoUuTien: 1=Cao, 2=Trung bình, 3=Thấp. " +
            "DÙNG cho 'việc hôm nay' (tabFilter=3), 'việc trễ hạn' (tabFilter=2), 'việc ưu tiên cao' (mucDoUuTien=1).",
            "/api/ai/tasks",
            new[] { "tabFilter", "trangThai", "mucDoUuTien", "workFlowId", "startDate", "endDate", "pageIndex", "pageSize" },
            "tasks", "Công việc",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("customers",
            "Danh sách khách hàng. " +
            "customerGroupId (nhóm KH): 1=Cá nhân, 2=Doanh nghiệp, 3=CTV, 4=Đại lý. " +
            "careFilter (chưa chăm sóc bao lâu): 0=Không lọc, 1=7 ngày, 2=15 ngày, 3=30 ngày, 4=90 ngày chưa chăm sóc. " +
            "birthdayThisMonth=true → sinh nhật tháng này. " +
            "DÙNG cho 'KH chưa chăm sóc 30 ngày' (careFilter=3), 'KH sinh nhật tháng này' (birthdayThisMonth=true), " +
            "'KH doanh nghiệp' (customerGroupId=2). Lọc thêm: từ khóa (filter), loại KH, giới tính, ngày tạo.",
            "/api/ai/customers",
            new[] { "filter", "customerGroupId", "customerTypeId", "gender", "careFilter", "birthdayThisMonth", "startDate", "endDate", "pageIndex", "pageSize" },
            "customers", "Khách hàng",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("appointments",
            "Lịch hẹn chăm sóc khách hàng (CSKH). " +
            "dateFilter: 0=Tất cả, 1=Hôm nay, 2=Tuần này, 3=Quá hạn, 4=Tạo mới, 5=Thành công. " +
            "DÙNG cho 'lịch hẹn tuần này' (dateFilter=2), 'lịch hẹn quá hạn' (dateFilter=3), 'lịch hẹn hôm nay' (dateFilter=1).",
            "/api/ai/appointments",
            new[] { "dateFilter", "status", "keyword", "startDate", "endDate", "pageIndex", "pageSize" },
            "appointments", "Quản lý lịch hẹn",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("vouchers",
            "Phiếu thu/chi (dòng tiền chi tiết). " +
            "voucherType: 4=Phiếu Thu, 5=Phiếu Chi. " +
            "approvalStatus: -1=Tất cả, 1=Đã duyệt, 0=Từ chối. " +
            "onlyWaiting=true → CHỈ phiếu CHỜ duyệt (chưa duyệt + chưa từ chối). " +
            "DÙNG cho 'phiếu chi chờ duyệt' (voucherType=5, onlyWaiting=true), 'phiếu thu đã duyệt' (voucherType=4, approvalStatus=1).",
            "/api/ai/vouchers",
            new[] { "filter", "voucherType", "approvalStatus", "onlyWaiting", "startDate", "endDate", "pageIndex", "pageSize" },
            "vouchers", "Phiếu thu/chi",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20", ["voucherType"] = "4" }),

        new("notifications",
            "Thông báo / việc cần duyệt (đơn, phiếu chờ duyệt, lịch hẹn, bình luận).",
            "/api/ai/notifications",
            Array.Empty<string>(),
            "stats", "Thông báo cần xử lý"),

        new("branch_performance",
            "Hiệu suất theo CHI NHÁNH (doanh thu, thực thu, công nợ, chi, hoa hồng theo chi nhánh). " +
            "DÙNG cho 'chi nhánh nào doanh số cao nhất', 'so sánh chi nhánh', 'xếp hạng chi nhánh'. " +
            "Bỏ trống ngày = ALL time. Có thể lọc theo `branch` (1 chi nhánh) hoặc `groupId` (nhóm).",
            "/api/ai/branch-performance",
            new[] { "startDate", "endDate", "branch", "groupId" },
            "branch", "Hiệu suất chi nhánh"),

        new("product_line_revenue",
            "Hiệu suất theo DÒNG SẢN PHẨM / loại tour (FIT/GIT/LandTour/Visa/Booking…). " +
            "DÙNG cho 'dòng sản phẩm nào lãi nhất', 'tour FIT vs GIT', 'lợi nhuận theo loại tour'. " +
            "Bỏ trống ngày = ALL time. Có thể lọc `branch` hoặc `tourType`.",
            "/api/ai/product-line-revenue",
            new[] { "startDate", "endDate", "branch", "tourType" },
            "productline", "Dòng sản phẩm"),

        new("market_analysis",
            "Hiệu suất theo THỊ TRƯỜNG (Hàn Quốc, Nhật, Nội địa Miền Nam, …). " +
            "DÙNG cho 'thị trường nào doanh thu cao nhất', 'so sánh thị trường', 'lợi nhuận theo thị trường'. " +
            "Bỏ trống ngày = ALL time. Có thể lọc `branch` hoặc `marketId`.",
            "/api/ai/market-analysis",
            new[] { "startDate", "endDate", "branch", "marketId" },
            "market", "Phân tích thị trường"),

        new("list_markets",
            "Danh sách thị trường tour (Hàn Quốc, Nội địa - Miền Nam…). Dùng khi hỏi 'có những thị trường nào'.",
            "/api/tours/markets",
            Array.Empty<string>(),
            "markets", "Thị trường"),
    };

    public static ChatTool? Find(string? name)
        => string.IsNullOrEmpty(name) ? null
           : All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// Catalog JSON gọn để nhúng vào prompt planner.
    public static string CatalogForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var t in All)
        {
            var ps = t.Params.Length == 0 ? "(không tham số)" : string.Join(", ", t.Params);
            sb.Append("- ").Append(t.Name).Append(": ").Append(t.Description)
              .Append(" | params: ").Append(ps).Append('\n');
        }
        return sb.ToString();
    }

    /// Ghép path + query từ params AI cung cấp (chỉ giữ key hợp lệ) trộn với Defaults.
    public static string BuildPath(ChatTool tool, JsonElement? prms)
    {
        var query = new Dictionary<string, string>(tool.Defaults ?? new());

        if (prms is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var p in obj.EnumerateObject())
            {
                // chỉ nhận key nằm trong danh sách Params được phép của tool
                if (!tool.Params.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var val = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True   => "true",
                    JsonValueKind.False  => "false",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(val)) query[p.Name] = val!;
            }
        }

        if (query.Count == 0) return tool.Path;
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{tool.Path}?{qs}";
    }
}
