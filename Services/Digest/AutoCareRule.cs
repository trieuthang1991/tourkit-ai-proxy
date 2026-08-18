using System.Globalization;

namespace TourkitAiProxy.Services.Digest;

/// 1 khách hàng, đủ field để chấm "có cần chăm lại không".
public record CareCustomer(
    int Id, string Name, string? Phone, string? Email,
    string? RankName, decimal TotalRevenue, int TotalTours, DateTime? LastCareDate,
    /// Tên đăng nhập NV phụ trách khách (customers.INS_UID phía CRM). null = chưa gán.
    string? StaffUserName = null);

/// 1 dòng trong danh sách nhắc chăm lại.
public record CareLead(int Id, string Name, string? Phone, string? RankName,
    decimal TotalRevenue, int QuietDays, string Text, string? StaffUserName = null);

/// <summary>
/// Tìm khách ĐÁNG chăm mà lâu không ai đụng tới (S6).
///
/// <para><b>Vì sao NHẮC GỌI chứ không gửi thư tự động.</b> Lộ trình gốc hình dung tính năng này tự
/// soạn và gửi thư chăm sóc. Đo dữ liệu thật (15/08): <b>số điện thoại có ở 100/100 khách, email
/// chỉ 14/100</b>. Một tính năng gửi thư tự động sẽ với tới được một phần bảy tệp khách — mà lại là
/// thứ rủi ro nhất trong cả lộ trình, vì nó gửi ra ngoài công ty. Việc đúng với dữ liệu đang có là
/// nhắc nhân viên GỌI, và gọi thì phải người gọi.</para>
///
/// <para><b>Đòi khách ĐÃ TỪNG MUA</b> (tuỳ chọn, mặc định bật): danh sách "khách chưa mua bao giờ và
/// lâu không chăm" chính là toàn bộ dữ liệu rác trong CRM — nhắc là chôn vùi mấy khách thật sự đáng
/// gọi.</para>
///
/// <para><b>Bỏ qua khách chưa từng được chăm lần nào.</b> Trên dữ liệu thật chỉ 15/100 khách có ngày
/// chăm gần nhất; coi "chưa có ngày" là "đã im lâu" sẽ quét về 85% tệp khách — một danh sách không
/// ai đọc nổi và cũng không nói lên điều gì.</para>
/// </summary>
public static class AutoCareRule
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi);

    /// <param name="ranks">Hạng được coi là đáng chăm. Rỗng = mọi hạng.</param>
    /// <param name="quietDays">Bao lâu không chăm thì coi là "ngủ quên".</param>
    /// <param name="requireBought">Chỉ lấy khách đã từng phát sinh doanh thu.</param>
    /// <param name="max">Cắt danh sách còn bao nhiêu dòng.</param>
    public static List<CareLead> Find(
        IEnumerable<CareCustomer> customers, DateTime todayLocal,
        IReadOnlyCollection<string>? ranks, int quietDays, bool requireBought, int max)
    {
        var today = todayLocal.Date;
        var wanted = ranks is { Count: > 0 }
            ? new HashSet<string>(ranks, StringComparer.OrdinalIgnoreCase)
            : null;

        var result = new List<CareLead>();
        foreach (var c in customers)
        {
            // Chưa từng được chăm → bỏ qua, xem ghi chú ở đầu lớp.
            if (c.LastCareDate == null) continue;

            var quiet = (today - c.LastCareDate.Value.Date).Days;
            if (quiet < quietDays) continue;

            if (requireBought && c.TotalRevenue <= 0) continue;
            if (wanted != null && (c.RankName == null || !wanted.Contains(c.RankName))) continue;

            var money = c.TotalRevenue > 0 ? $", đã mua {Vnd(c.TotalRevenue)}đ" : "";
            var rank = string.IsNullOrWhiteSpace(c.RankName) ? "" : $" (hạng {c.RankName})";
            result.Add(new CareLead(c.Id, c.Name, c.Phone, c.RankName, c.TotalRevenue, quiet,
                $"{c.Name}{rank} — {quiet} ngày chưa chăm{money}"
                + (string.IsNullOrWhiteSpace(c.Phone) ? "" : $" · {c.Phone}"),
                c.StaffUserName));
        }

        // Khách đã chi nhiều lên trước; cùng mức tiền thì ai im lâu hơn lên trước. Sắp theo "im lâu
        // nhất" đơn thuần sẽ đẩy mấy khách mua một lần từ đời nào lên đầu, che mất khách sộp.
        return result
            .OrderByDescending(x => x.TotalRevenue)
            .ThenByDescending(x => x.QuietDays)
            .Take(Math.Max(1, max))
            .ToList();
    }
}
