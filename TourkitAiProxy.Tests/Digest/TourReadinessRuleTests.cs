using TourkitAiProxy.Services.Digest;
using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// Chấm "tour sắp đi đã sẵn sàng chưa" — luật thuần, không AI, không DB.
///
/// Yêu cầu quan trọng nhất: KHÔNG báo nhầm. Điều hành nhờn cảnh báo thì cái thật cũng bị bỏ qua,
/// mà cái thật ở đây là tour bay mà chưa thu đủ tiền hoặc thiếu hồ sơ visa.
public class TourReadinessRuleTests
{
    private static readonly DateTime Today = new(2026, 8, 14);

    private static TourReadinessRow Tour(int daysLeft, decimal revenue = 10_000_000m,
        decimal actual = 10_000_000m, int slots = 0, int booked = 0, int tourType = 101,
        int onHold = 0)
        => new(TourId: 1, Title: "Tour Đà Nẵng", CustomerName: "Anh Nam", SellerName: "Sale1",
               DepartureDate: Today.AddDays(daysLeft), Revenue: revenue, ActualRevenue: actual,
               Slots: slots, Booked: booked, TourType: tourType, TourTypeLabel: "Tour visa",
               OnHold: onHold);

    // ── Phạm vi thời gian ─────────────────────────────────────────────────────────

    [Fact]
    public void Tour_da_khoi_hanh_thi_khong_nhac()
        => Assert.Empty(TourReadinessRule.Evaluate(new[] { Tour(-1, actual: 0) }, Today));

    [Fact]
    public void Tour_con_xa_hon_moc_lon_nhat_thi_chua_toi_luot()
        => Assert.Empty(TourReadinessRule.Evaluate(new[] { Tour(8, actual: 0) }, Today));

    /// Tour còn 5 ngày thuộc mốc 7 (đã qua 7, chưa tới 3) — nhắc theo mốc gần nhất ĐÃ đi qua.
    [Theory]
    [InlineData(7, 7)]
    [InlineData(5, 7)]
    [InlineData(3, 3)]
    [InlineData(2, 3)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Chon_dung_moc_gan_nhat_da_cham(int daysLeft, int expectedMilestone)
    {
        var cards = TourReadinessRule.Evaluate(new[] { Tour(daysLeft, actual: 0) }, Today);
        Assert.Single(cards);
        Assert.Equal(expectedMilestone, cards[0].Milestone);
    }

    // ── Nhóm kiểm: tiền ───────────────────────────────────────────────────────────

    [Fact]
    public void Thu_du_tien_va_khong_thieu_gi_thi_KHONG_dung_the()
        => Assert.Empty(TourReadinessRule.Evaluate(new[] { Tour(3) }, Today));

    [Fact]
    public void Con_thieu_tien_thi_ghi_ro_con_bao_nhieu()
    {
        var cards = TourReadinessRule.Evaluate(new[] { Tour(3, revenue: 10_000_000m, actual: 4_000_000m) }, Today);
        Assert.Contains(cards[0].Issues, i => i.Code == "payment" && i.Text.Contains("6.000.000"));
    }

    /// Chạy thật ra "còn thiếu 26.870.862đ / 26.870.862đ" — hai số bằng nhau đọc như lỗi hiển thị,
    /// trong khi ý nghĩa thật (chưa thu đồng nào) mới là điều đáng báo động nhất.
    [Fact]
    public void Chua_thu_dong_nao_thi_noi_thang_thay_vi_hai_so_bang_nhau()
    {
        var cards = TourReadinessRule.Evaluate(new[] { Tour(3, revenue: 26_870_862m, actual: 0) }, Today);
        var issue = cards[0].Issues.Single(i => i.Code == "payment");
        Assert.Equal("chưa thu đồng nào — cả tour 26.870.862đ", issue.Text);
    }

    /// Tour chưa chốt giá thì "còn thiếu" là khái niệm vô nghĩa — nhắc chỉ gây nhiễu.
    [Fact]
    public void Chua_chot_gia_thi_khong_tinh_la_thieu_tien()
        => Assert.Empty(TourReadinessRule.Evaluate(new[] { Tour(3, revenue: 0, actual: 0) }, Today));

    // ── Nhóm kiểm: chỗ ngồi ───────────────────────────────────────────────────────

    /// Công ty CHƯA khai ngưỡng (minSeats=0) thì tuyệt đối không đoán hộ — bỏ qua hẳn.
    [Fact]
    public void Chua_khai_nguong_toi_thieu_thi_khong_kiem_cho_ngoi()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(3, slots: 20, booked: 2) }, Today, minSeats: 0));

    [Fact]
    public void Duoi_nguong_toi_thieu_thi_bao()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(3, slots: 20, booked: 5) }, Today, minSeats: 10);
        Assert.Contains(cards[0].Issues, i => i.Code == "seats" && i.Text.Contains("5/20"));
    }

    /// Tour lẻ (không khai số chỗ) không có khái niệm "đủ khách tối thiểu".
    [Fact]
    public void Tour_khong_khai_so_cho_thi_bo_qua_kiem_cho_ngoi()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(3, slots: 0, booked: 0) }, Today, minSeats: 10));

    // ── Nhóm kiểm: visa ───────────────────────────────────────────────────────────

    [Fact]
    public void Tour_loai_visa_thi_nhac_kiem_ho_so()
    {
        var cards = TourReadinessRule.Evaluate(new[] { Tour(3, tourType: 102) }, Today);
        Assert.Contains(cards[0].Issues, i => i.Code == "visa");
    }

    [Fact]
    public void Tour_thuong_thi_khong_nhac_visa()
        => Assert.Empty(TourReadinessRule.Evaluate(new[] { Tour(3, tourType: 101) }, Today));

    [Fact]
    public void Tat_kiem_visa_thi_khong_nhac_du_dung_loai()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(3, tourType: 102) }, Today, checkVisa: false));

    // ── Mức độ gấp + thứ tự ───────────────────────────────────────────────────────

    /// Càng gần ngày đi càng gấp; thiếu tiền nâng thêm một bậc vì đòi sau khi bay là không đòi được.
    [Theory]
    [InlineData(7, 2)]   // mốc 7 = 1, có thiếu tiền → 2
    [InlineData(3, 3)]   // mốc 3 = 2, có thiếu tiền → 3
    [InlineData(1, 3)]   // mốc 1 = 3, đã kịch trần
    public void Cang_gan_ngay_di_cang_gap(int daysLeft, int expectedSeverity)
    {
        var cards = TourReadinessRule.Evaluate(new[] { Tour(daysLeft, actual: 0) }, Today);
        Assert.Equal(expectedSeverity, cards[0].Severity);
    }

    [Fact]
    public void Gap_truoc_roi_toi_ngay_di_gan_nhat()
    {
        var rows = new[]
        {
            Tour(7, actual: 0) with { TourId = 1 },   // severity 2
            Tour(1, actual: 0) with { TourId = 2 },   // severity 3
            Tour(3, actual: 0) with { TourId = 3 },   // severity 3, đi sau tour 2
        };
        var cards = TourReadinessRule.Evaluate(rows, Today);
        Assert.Equal(new[] { 2, 3, 1 }, cards.Select(c => c.TourId).ToArray());
    }

    // ── Chống nhắc lại ────────────────────────────────────────────────────────────

    /// Cùng tour ở D-7 và D-3 là HAI lời nhắc khác nhau, nên khoá chống trùng phải kèm mốc.
    [Fact]
    public void Khoa_chong_trung_kem_moc()
    {
        var d7 = TourReadinessRule.Evaluate(new[] { Tour(7, actual: 0) }, Today)[0].AlertKey;
        var d3 = TourReadinessRule.Evaluate(new[] { Tour(3, actual: 0) }, Today)[0].AlertKey;
        Assert.Equal("readiness:1:7", d7);
        Assert.Equal("readiness:1:3", d3);
        Assert.NotEqual(d7, d3);
    }

    [Fact]
    public void Tat_het_cac_nhom_kiem_thi_khong_dung_the_nao()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(1, actual: 0, slots: 20, booked: 1, tourType: 102) }, Today,
            checkPayment: false, checkSeats: false, checkVisa: false, minSeats: 10));

    // ── Chỗ ngồi: GIỮ CHỖ CŨNG CHIẾM CHỖ (lỗi thật của bản đầu) ──────────────────

    /// Đo trên dữ liệu thật: slots=20, booked=6, onHold=1 → upstream nói còn 13 chỗ, tức đã kín 7.
    /// Bản đầu chỉ đếm booked nên tính là 6 → công ty khai ngưỡng 7 nhận cảnh báo "chưa đủ khách"
    /// cho một tour ĐÃ đủ. Báo động giả làm người ta bỏ qua luôn cảnh báo thật.
    [Fact]
    public void Giu_cho_duoc_tinh_la_da_chiem_cho()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(3, slots: 20, booked: 6, onHold: 1) }, Today, minSeats: 7);
        Assert.Empty(cards);
    }

    /// Chiều ngược lại phải vẫn báo: 6 đã đặt + 0 giữ chỗ, ngưỡng 7 → thiếu thật.
    [Fact]
    public void Thieu_khach_that_thi_van_bao()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(3, slots: 20, booked: 6) }, Today, minSeats: 7);
        var seats = cards.Single().Issues.Single(i => i.Code == "seats");
        // Tách rõ phần giữ chỗ: điều hành cần biết phần đó có thể rơi.
        Assert.Contains("6", seats.Text);
        Assert.Contains("20", seats.Text);
    }

    // ── Tour sắp đầy → đẩy bán nốt ───────────────────────────────────────────────

    [Fact]
    public void Sap_day_thi_nhac_ban_not()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(7, slots: 20, booked: 16, onHold: 1) }, Today, nearlyFullPercent: 80);
        var issue = cards.Single().Issues.Single(i => i.Code == "nearly_full");
        Assert.Contains("3", issue.Text);      // còn 3 chỗ
    }

    /// Đầy hẳn thì KHÔNG nhắc — không còn gì để bán, nhắc là nhiễu.
    [Fact]
    public void Day_han_thi_im()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(7, slots: 20, booked: 20) }, Today, nearlyFullPercent: 80));

    /// Tour lẻ (không khai số chỗ) không có khái niệm "sắp đầy".
    [Fact]
    public void Tour_khong_khai_cho_thi_im()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(7, slots: 0, booked: 5) }, Today, nearlyFullPercent: 80));

    /// Dùng TỈ LỆ chứ không phải số chỗ tuyệt đối: còn 3/100 là gần đầy từ lâu, còn 3/20 mới đáng
    /// nhắc. Một ngưỡng "còn ≤3 chỗ" sẽ đúng với tour nhỏ và vô nghĩa với tour lớn.
    [Fact]
    public void Nguong_tinh_theo_ti_le_khong_theo_so_cho()
    {
        var tourLon = TourReadinessRule.Evaluate(
            new[] { Tour(7, slots: 100, booked: 50) }, Today, nearlyFullPercent: 80);
        Assert.Empty(tourLon);   // kín 50% — còn 50 chỗ, chưa phải lúc hối bán nốt
    }

    [Fact]
    public void Tat_canh_bao_sap_day_thi_im()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(7, slots: 20, booked: 17) }, Today, checkNearlyFull: false));

    // ── Mốc RIÊNG cho phần chỗ ngồi ──────────────────────────────────────────────

    /// Tour còn 10 ngày: ngoài tầm mốc tiền/visa {7,3,1} nhưng trong tầm mốc chỗ {21,14,7}.
    /// Chỉ được soi phần chỗ — báo "chưa thu đủ tiền" ở D-10 là bình thường, nói ra là nhiễu.
    [Fact]
    public void Ngoai_moc_tien_nhung_trong_moc_cho_thi_chi_bao_cho()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(10, actual: 0, slots: 20, booked: 17) }, Today,
            nearlyFullPercent: 80);
        var card = cards.Single();
        Assert.All(card.Issues, i => Assert.Equal("nearly_full", i.Code));
        Assert.DoesNotContain(card.Issues, i => i.Code == "payment");
    }

    /// Mốc trùng nhau (D-7 có ở cả hai tập) → vẫn chỉ MỘT thẻ, gộp cả hai loại việc.
    /// Hai thẻ về cùng một tour trong cùng buổi sáng là bắt người đọc tự ghép lại trong đầu.
    [Fact]
    public void Moc_trung_van_chi_ra_mot_the()
    {
        var cards = TourReadinessRule.Evaluate(
            new[] { Tour(7, actual: 0, slots: 20, booked: 17) }, Today, nearlyFullPercent: 80);
        var card = Assert.Single(cards);
        Assert.Contains(card.Issues, i => i.Code == "payment");
        Assert.Contains(card.Issues, i => i.Code == "nearly_full");
    }

    /// Thẻ CHỈ có tin vui thì không được tô mức gấp — severity tính theo nhóm vấn đề.
    [Fact]
    public void The_chi_co_tin_vui_thi_khong_gap()
    {
        var card = TourReadinessRule.Evaluate(
            new[] { Tour(1, slots: 20, booked: 17) }, Today, nearlyFullPercent: 80).Single();
        Assert.Equal(0, card.Severity);
    }

    /// Tour xa hơn mốc chỗ lớn nhất thì chưa tới lượt bất kỳ nhóm nào.
    [Fact]
    public void Xa_hon_moc_cho_lon_nhat_thi_bo_qua()
        => Assert.Empty(TourReadinessRule.Evaluate(
            new[] { Tour(30, actual: 0, slots: 20, booked: 17) }, Today));
}
