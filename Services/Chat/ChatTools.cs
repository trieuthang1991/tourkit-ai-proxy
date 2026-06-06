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

        new("tours",
            "Danh sách tour (FIT/GIT/LandTour/DV lẻ/Booking/Visa). Lọc theo loại (tourType), tên, trạng thái, ngày khởi hành, thị trường (điền marketName, vd 'Nội địa miền Nam'), chi nhánh.",
            "/api/ai/tours",
            new[] { "tourType", "tourName", "status", "startDate", "endDate", "marketId", "marketName", "branch", "pageIndex", "pageSize" },
            "tours", "Danh sách tour",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("booking_tickets",
            "Cơ hội bán hàng / lead khách hàng. DÙNG cho 'khách/lead THUỘC THỊ TRƯỜNG X' (lọc marketName). Lọc: trạng thái (trangThai), nguồn (nguon), nhân viên phụ trách, khoảng ngày.",
            "/api/ai/booking-tickets",
            new[] { "keyword", "trangThai", "nguon", "nhanVienPhuTrach", "marketId", "marketName", "startDate", "endDate", "pageIndex", "pageSize" },
            "tickets", "Cơ hội bán hàng",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("tasks",
            "Công việc (task). Lọc theo tab (tabFilter: 0 tất cả,1 sắp tới,2 trễ hạn,3 hôm nay,4 tuần này,5 tháng này), trạng thái, ưu tiên, khoảng ngày.",
            "/api/ai/tasks",
            new[] { "tabFilter", "trangThai", "mucDoUuTien", "workFlowId", "startDate", "endDate", "pageIndex", "pageSize" },
            "tasks", "Công việc",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("customers",
            "Danh sách khách hàng. Lọc: từ khóa (filter), nhóm KH (customerGroupId), loại (customerTypeId), giới tính, sinh nhật tháng này (birthdayThisMonth=true), chưa chăm sóc (careFilter), khoảng ngày.",
            "/api/ai/customers",
            new[] { "filter", "customerGroupId", "customerTypeId", "gender", "careFilter", "birthdayThisMonth", "startDate", "endDate", "pageIndex", "pageSize" },
            "customers", "Khách hàng",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("appointments",
            "Lịch hẹn chăm sóc khách hàng (CSKH). Lọc: dateFilter (0 tất cả,1 hôm nay,2 tuần này,3 quá hạn,4 tạo mới,5 thành công), trạng thái, từ khóa, khoảng ngày.",
            "/api/ai/appointments",
            new[] { "dateFilter", "status", "keyword", "startDate", "endDate", "pageIndex", "pageSize" },
            "appointments", "Quản lý lịch hẹn",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20" }),

        new("vouchers",
            "Phiếu thu/chi (dòng tiền chi tiết). voucherType = 4 Phiếu Thu | 5 Phiếu Chi. Lọc: trạng thái duyệt (approvalStatus), chỉ chờ duyệt (onlyWaiting=true), từ khóa, khoảng ngày.",
            "/api/ai/vouchers",
            new[] { "filter", "voucherType", "approvalStatus", "onlyWaiting", "startDate", "endDate", "pageIndex", "pageSize" },
            "vouchers", "Phiếu thu/chi",
            new() { ["pageIndex"] = "1", ["pageSize"] = "20", ["voucherType"] = "4" }),

        new("notifications",
            "Thông báo / việc cần duyệt (đơn, phiếu chờ duyệt, lịch hẹn, bình luận).",
            "/api/ai/notifications",
            Array.Empty<string>(),
            "stats", "Thông báo cần xử lý"),

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
