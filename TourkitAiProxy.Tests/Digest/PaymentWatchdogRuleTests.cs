using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Luật cảnh báo "tour sắp đi mà khách chưa trả đủ".
/// Sai ở đây tốn tiền thật: bỏ sót thì tour khởi hành mà chưa thu đủ; báo thừa thì nhân viên
/// nhờn cảnh báo rồi bỏ qua cả cái thật.
public class PaymentWatchdogRuleTests
{
    private static readonly DateTime Today = new(2026, 8, 11);

    private static TourPaymentRow Row(int id, int daysToDeparture, decimal revenue, decimal actual)
        => new(id, $"Tour {id}", "Khách A", "Sale B", Today.AddDays(daysToDeparture), revenue, actual);

    [Fact]
    public void No_du_tien_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 5, 100m, 100m) }, Today));

    [Fact]
    public void Con_no_trong_cua_so_7_ngay_thi_canh_bao()
    {
        var a = Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 5, 100m, 40m) }, Today));
        Assert.Equal(60m, a.Outstanding);
        Assert.Equal("payment:1", a.AlertKey);
        Assert.Equal(1, a.Severity);           // D-5 → mức nhắc
        Assert.Equal(5, a.DaysLeft);
    }

    [Fact]
    public void D3_tro_xuong_la_critical()
        => Assert.Equal(2, PaymentWatchdogRule.Evaluate(new[] { Row(1, 3, 100m, 0m) }, Today).Single().Severity);

    [Fact]
    public void Ngoai_cua_so_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 8, 100m, 0m) }, Today));

    [Fact]
    public void Da_khoi_hanh_hom_qua_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, -1, 100m, 0m) }, Today));

    [Fact]
    public void Khoi_hanh_hom_nay_van_canh_bao_critical()
    {
        var a = Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 0, 100m, 50m) }, Today));
        Assert.Equal(2, a.Severity);
    }

    // ── Ca thêm ngoài plan: biên và dữ liệu bẩn ──────────────────────────────

    [Fact]
    public void Dung_bien_cua_so_ngay_thu_7_van_canh_bao()
        => Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 7, 100m, 0m) }, Today));

    [Fact]
    public void Dung_bien_critical_ngay_thu_4_chi_la_muc_nhac()
        => Assert.Equal(1, PaymentWatchdogRule.Evaluate(new[] { Row(1, 4, 100m, 0m) }, Today).Single().Severity);

    [Fact]
    public void Thu_du_roi_van_bao_thi_la_sai_ke_ca_khi_thua_tien()
    {
        // Khách trả DƯ (đặt cọc thêm/quy đổi) — không phải đang nợ, tuyệt đối không nhắc.
        Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 2, 100m, 150m) }, Today));
    }

    [Fact]
    public void Doanh_thu_bang_0_khong_coi_la_no()
    {
        // Tour chưa chốt giá (Revenue=0) thì "nợ" không có nghĩa — nhắc là gây nhiễu.
        Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 2, 0m, 0m) }, Today));
    }

    [Fact]
    public void Gio_trong_ngay_khoi_hanh_khong_lam_lech_so_ngay()
    {
        // DepartureDate kèm giờ (23:30) không được làm DaysLeft lệch 1 ngày.
        var row = new TourPaymentRow(1, "T", "K", "S", Today.AddDays(3).AddHours(23.5), 100m, 0m);
        var a = Assert.Single(PaymentWatchdogRule.Evaluate(new[] { row }, Today.AddHours(9)));
        Assert.Equal(3, a.DaysLeft);
        Assert.Equal(2, a.Severity);
    }

    [Fact]
    public void Nhieu_tour_thi_bao_du_va_moi_tour_mot_khoa_rieng()
    {
        var alerts = PaymentWatchdogRule.Evaluate(new[]
        {
            Row(1, 1, 100m, 0m),
            Row(2, 6, 200m, 199m),
            Row(3, 9, 100m, 0m),      // ngoài cửa sổ
            Row(4, 2, 100m, 100m),    // đã đủ
        }, Today);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(new[] { "payment:1", "payment:2" }, alerts.Select(a => a.AlertKey).OrderBy(x => x).ToArray());
        Assert.Equal(1m, alerts.Single(a => a.TourId == 2).Outstanding);
    }

    [Fact]
    public void Danh_sach_rong_khong_no()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(Array.Empty<TourPaymentRow>(), Today));

    // ── 2 ca dưới đây thêm sau khi thử phá code: bản test cũ KHÔNG bắt được 2 lỗi thật ────

    [Fact]
    public void Khoi_hanh_hom_qua_luc_23h_van_khong_canh_bao()
    {
        // Bỏ .Date ở DepartureDate thì: (hôm qua 23:00 − hôm nay 00:00) = −1 giờ, mà TimeSpan.Days
        // cắt về 0 → thành "khởi hành HÔM NAY, mức gấp". Tour đã đi rồi mà vẫn giục đòi tiền.
        // Ca cũ dùng đúng nửa đêm nên ra −1 ngày, không lộ lỗi này.
        var row = new TourPaymentRow(1, "T", "K", "S", Today.AddDays(-1).AddHours(23), 100m, 0m);
        Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { row }, Today));
    }

    [Fact]
    public void Da_hoan_tien_khong_bi_coi_la_dang_no()
    {
        // Tour huỷ đã hoàn tiền: Revenue=0, ActualRevenue âm → nếu bỏ chốt chặn Revenue<=0 thì
        // outstanding = 0 − (−100) = 100 → báo đòi 100đ của một tour KHÔNG còn doanh thu.
        var row = new TourPaymentRow(1, "T", "K", "S", Today.AddDays(2), 0m, -100m);
        Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { row }, Today));
    }

    // ── Cửa sổ ngày do công ty khai ─────────────────────────────────────────────
    [Fact]
    public void Cua_so_noi_rong_thi_tour_xa_hon_cung_duoc_bao()
        => Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 20, 100m, 0m) }, Today, windowDays: 30));

    // ── Ngưỡng nợ tối thiểu ─────────────────────────────────────────────────────
    [Fact]
    public void Mac_dinh_khong_nguong_thi_no_le_van_bao()
        => Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 5, 100_000_000m, 99_999_000m) }, Today));

    [Fact]
    public void No_nho_hon_nguong_thi_bo_qua()
    {
        // Chênh 1.000đ do làm tròn vẫn là "còn nợ" theo phép trừ, nhưng chiếm nguyên một thẻ
        // trong Bảng tin. Công ty khai ngưỡng 1 triệu thì dòng này phải im.
        var rows = new[] { Row(1, 5, 100_000_000m, 99_999_000m) };
        Assert.Empty(PaymentWatchdogRule.Evaluate(rows, Today, minOutstanding: 1_000_000m));
    }

    [Fact]
    public void No_bang_dung_nguong_thi_van_bao()
    {
        // Ranh giới: "từ 1 triệu trở lên thì nhắc" — bằng đúng ngưỡng là CÓ nhắc, không phải bỏ.
        var rows = new[] { Row(1, 5, 100_000_000m, 99_000_000m) };
        Assert.Single(PaymentWatchdogRule.Evaluate(rows, Today, minOutstanding: 1_000_000m));
    }

    // ─── Cảnh báo này là việc CỦA AI ───────────────────────────────────────────────
    // Ba trạng thái, và cái đắt nhất là trạng thái ĐẦU: nếu gộp "API chưa nâng cấp" chung với
    // "tour chưa gán ai" thì hôm nào deploy proxy trước TourKit.Api là tác vụ im lặng hoàn toàn —
    // không cảnh báo nào, không lỗi nào, không ai biết.

    [Fact]
    public void Api_chua_nang_cap_thi_van_bao_ca_cong_ty_chu_khong_im_lang()
    {
        var (owner, skip) = PaymentWatchdogRule.ResolveOwner(null, apiHasSellerField: false);
        Assert.False(skip);
        Assert.Equal("", owner);
    }

    [Fact]
    public void Tour_chua_gan_nguoi_phu_trach_thi_BO_QUA_chu_khong_roi_ve_cong_ty()
    {
        // Không rơi về cả công ty: cảnh báo ai cũng thấy = không ai chịu trách nhiệm. Chỗ thiếu
        // dồn vào tour ghép (GIT 90%, LandTour 95%) nên rơi về công ty là nhấn chìm cảnh báo thật.
        var (_, skip) = PaymentWatchdogRule.ResolveOwner(null, apiHasSellerField: true);
        Assert.True(skip);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ten_dang_nhap_rong_hay_toan_khoang_trang_cung_la_chua_gan(string raw)
    {
        var (_, skip) = PaymentWatchdogRule.ResolveOwner(raw, apiHasSellerField: true);
        Assert.True(skip);
    }

    [Fact]
    public void Co_nguoi_phu_trach_thi_ghi_dich_danh_va_cat_khoang_trang()
    {
        var (owner, skip) = PaymentWatchdogRule.ResolveOwner("  trang01 ", apiHasSellerField: true);
        Assert.False(skip);
        Assert.Equal("trang01", owner);
    }

    [Fact]
    public void Nguoi_phu_trach_di_theo_tour_qua_Evaluate()
    {
        var rows = new[] { Row(1, 5, 100_000_000m, 0m) with { SellerUserName = "trang01" } };
        Assert.Equal("trang01", PaymentWatchdogRule.Evaluate(rows, Today).Single().SellerUserName);
    }
}
