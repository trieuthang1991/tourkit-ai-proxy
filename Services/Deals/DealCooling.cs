namespace TourkitAiProxy.Services.Deals;

/// <summary>
/// Định nghĩa "deal NGUỘI" — NGUỒN DUY NHẤT cho UI badge/KPI (deals.jsx) + alert workflow
/// (DealAutoReviewWorkflow). Trước đây mỗi nơi tự tính khác nhau → deal "Hoàn thành" vẫn hiện nguội.
///
/// Nguội ⟺ trạng thái ĐỦ ĐIỀU KIỆN theo dõi VÀ <c>CoolingDays ≥ ngưỡng</c> (lâu không tương tác,
/// từ LastInteractionAt upstream). KHÔNG dùng tuổi deal (age) — đó là khái niệm khác, hiển thị riêng.
/// </summary>
public static class DealCooling
{
    public const int CancelStatus = 5;   // TourKit BookingTicketStatus: 5 = Hủy

    /// Deal đã CHỐT/thành công (không còn cơ hội mở) — nhận diện qua TÊN trạng thái (diacritics-insensitive).
    /// Chỉ dùng cho case coolingStatuses RỖNG (auto loại trừ đóng/hủy).
    /// "da chot" bắt cả "Đã chốt"/"Đã chốt đơn"; KHÔNG match nhầm "chưa chốt"/"sắp chốt".
    public static bool IsClosedWon(string? statusName)
    {
        var sn = DealHeuristic.Normalize(statusName);
        return sn.Length > 0 && (sn.Contains("chot don") || sn.Contains("da chot") || sn.Contains("thanh cong")
            || sn.Contains("hoan thanh") || sn.Contains("hoan tat") || sn.Contains("da ban"));
    }

    /// Verdict "nguội" theo policy per-tenant.
    /// - <paramref name="coolingStatuses"/> RỖNG → eligible = KHÔNG chốt-thắng (keyword) VÀ không Hủy.
    /// - <paramref name="coolingStatuses"/> CÓ giá trị → eligible = status ∈ list (list thắng hoàn toàn,
    ///   không áp thêm keyword — tôn trọng lựa chọn tenant).
    public static bool IsCooling(int status, string? statusName, int coolingDays, int threshold,
                                 IReadOnlyCollection<int> coolingStatuses)
    {
        if (threshold <= 0 || coolingDays < threshold) return false;
        bool eligible = coolingStatuses is { Count: > 0 }
            ? coolingStatuses.Contains(status)
            : (status != CancelStatus && !IsClosedWon(statusName));
        return eligible;
    }
}
