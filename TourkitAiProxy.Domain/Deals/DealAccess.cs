namespace TourkitAiProxy.Domain.Deals;

/// <summary>
/// Ai được xem một cơ hội (BookingTicket) — chép đúng luật bên web
/// (<c>BookingTicketAction</c>: <c>CH_XEM_ALL</c> xem tất cả; chỉ <c>CH_XEM</c> thì lọc theo chính mình).
///
/// <para>Không hỏi được CRM "người này xem được cơ hội X không": đường chi tiết của CRM trả dữ liệu
/// cho CẢ người không có quyền nào (đo trên staging 19/10/2026). Nên quyết ở proxy bằng mã quyền của
/// phiên + người tạo / người phụ trách đọc từ chi tiết cơ hội.</para>
/// </summary>
public static class DealAccess
{
    /// Người này là người tạo hoặc nằm trong danh sách phụ trách (CSV mã người, DB lưu dạng ",1,2,").
    public static bool IsOwner(int me, int? insUid, string? nguoiPhuTrachsCsv)
        => insUid == me || ParseUserIds(nguoiPhuTrachsCsv).Contains(me);

    /// Mã người từ CSV của CRM (",1,2,3," hoặc "1, 2"). Bỏ rỗng/bẩn/≤0, bỏ trùng, giữ thứ tự.
    public static int[] ParseUserIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(p => int.TryParse(p, out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
    }
}
