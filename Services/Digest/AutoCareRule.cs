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
        IReadOnlyCollection<string>? ranks, int quietDays, bool requireBought, int max = 0)
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
        //
        // ⚠️ CẮT SỐ LƯỢNG PHẢI LÀM SAU KHI LỌC "ĐÃ NHẮC RỒI", KHÔNG PHẢI Ở ĐÂY.
        // Cắt ở đây thì khi 20 khách sộp nhất đều đã được nhắc, tác vụ báo "không có ai mới" và
        // KHÔNG BAO GIỜ với tới khách thứ 21 — tính năng đứng im sau vài ngày mà không lỗi nào hiện
        // ra. Vì thế `max` để mặc định là không cắt; nơi gọi tự cắt sau khi lọc (xem
        // CustomerAutoCareWorkflow). Tham số vẫn giữ để test cũ và nơi gọi khác dùng được.
        var ordered = result
            .OrderByDescending(x => x.TotalRevenue)
            .ThenByDescending(x => x.QuietDays);
        return max <= 0 ? ordered.ToList() : ordered.Take(max).ToList();
    }

    /// <summary>
    /// Đổi "im bao nhiêu ngày" sang các nhóm lọc <c>CareFilter</c> mà CRM hiểu, để LỌC NGAY TRONG
    /// SQL thay vì kéo cả tệp khách về rồi lọc.
    ///
    /// <para><b>Vì sao đáng làm.</b> Đo trên staging 18/08: không lọc thì tệp khách là <b>22.079</b>,
    /// mà tác vụ chỉ kéo được 200 khách MỚI NHẤT — tức phủ 0,6%, và phủ nhầm đầu (khách cũ mới là
    /// nhóm dễ ngủ quên). Lọc <c>CareFilter=4</c> thì cả tệp "im ≥90 ngày" chỉ còn <b>131</b> khách,
    /// một lời gọi lấy hết, <b>577ms</b> — ngang lời gọi không lọc (640ms). Phủ 100% mà không tăng
    /// tải, khỏi phải phân trang 110 lần.</para>
    ///
    /// <para><b>Vì sao an toàn với hệ đang chạy.</b> Đây đúng là truy vấn mà màn Khách hàng của CRM
    /// vẫn chạy khi người dùng chọn bộ lọc CSKH — không phải kiểu tải mới. Ngược lại, cách "nâng
    /// PageSize cho nhiều lên" mới là cách làm sập: bên trong <c>SearchAsync</c> có vài mệnh đề
    /// <c>IN</c> theo id của trang (tính ngày chăm gần nhất, gộp doanh thu, tra người phụ trách),
    /// nâng số bản ghi là nhân số tham số lên theo — chạm trần 2100 tham số của SQL Server.</para>
    ///
    /// <para><b>Chiều của bộ lọc là "CHƯA được chăm bao lâu", không phải "đã chăm trong bao lâu".</b>
    /// Dễ đọc ngược nên đã đo tận nơi (staging 18/08, đối chiếu ngày chăm cuối của TỪNG khách trả về):
    /// nhóm 1 → 10 ngày · nhóm 3 → 32–83 ngày · <b>nhóm 4 → 95–846 ngày</b>. Còn khi KHÔNG lọc thì
    /// 300 khách đầu chỉ mới im 10–74 ngày — tức cách cũ (không lọc, lấy 300 mới nhất) nhắm đúng vào
    /// nhóm vừa được chăm, ngược hẳn mục đích.</para>
    ///
    /// <para>Nhóm của CRM là khoảng cố định: 1=[7,15) · 2=[15,30) · 3=[30,90) · 4=≥90. Nên trả về
    /// TẤT CẢ nhóm có thể chứa khách im ≥ <paramref name="quietDays"/> — tức một tập CHA. Lọc chính
    /// xác vẫn do <see cref="Find"/> làm, ở đây chỉ thu hẹp cho đỡ tải.</para>
    ///
    /// <para>Nhóm CareFilter loại luôn khách CHƯA có lần chăm nào (CRM nối trong, mirror INNER JOIN)
    /// — trùng đúng luật của <see cref="Find"/>, nên không mất ai.</para>
    /// </summary>
    public static int[] CareFilterBuckets(int quietDays) => quietDays switch
    {
        >= 90 => new[] { 4 },
        >= 30 => new[] { 3, 4 },
        >= 15 => new[] { 2, 3, 4 },
        _     => new[] { 1, 2, 3, 4 },   // quietDays bị kẹp tối thiểu 7 nên nhóm 1 luôn phủ được
    };
}
