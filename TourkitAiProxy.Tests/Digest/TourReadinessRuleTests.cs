using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Chấm "tour sắp đi đã sẵn sàng chưa" — luật thuần, không AI, không DB.
///
/// Yêu cầu quan trọng nhất: KHÔNG báo nhầm. Điều hành nhờn cảnh báo thì cái thật cũng bị bỏ qua,
/// mà cái thật ở đây là tour bay mà chưa thu đủ tiền hoặc thiếu hồ sơ visa.
public class TourReadinessRuleTests
{
    private static readonly DateTime Today = new(2026, 8, 14);

    private static TourReadinessRow Tour(int daysLeft, decimal revenue = 10_000_000m,
        decimal actual = 10_000_000m, int slots = 0, int booked = 0, int tourType = 101)
        => new(TourId: 1, Title: "Tour Đà Nẵng", CustomerName: "Anh Nam", SellerName: "Sale1",
               DepartureDate: Today.AddDays(daysLeft), Revenue: revenue, ActualRevenue: actual,
               Slots: slots, Booked: booked, TourType: tourType, TourTypeLabel: "Tour visa");

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
}
