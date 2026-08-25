using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Domain.Digest;
using TourkitAiProxy.Services.Workflows;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Khoá cách đọc 3 số chính từ envelope <c>/api/ai/financial-summary</c>.
///
/// <para>Đây là chỗ dễ sai mà KHÔNG BAO GIỜ nổ: đoán sai tên field thì không khớp gì cả, bản tin
/// gửi bình thường nhưng báo 0đ khắp nơi và mọi so sánh thành "n/a" — nhìn như hệ thống chạy tốt.
/// Bản kế hoạch ban đầu đoán <c>revenue/expense/profit</c>, tên thật là
/// <c>kpiRevenue/kpiTotalExpense/kpiGrossProfit</c> (đối chiếu DashboardService bên TourKit.Api).</para>
/// </summary>
public class CeoFinancialReadTests
{
    /// Envelope thật: items phẳng, mỗi metric có group/groupTitle/key/label/value/formatted.
    private static JsonElement Envelope(params (string Key, decimal Value)[] metrics)
    {
        var items = string.Join(",", metrics.Select(m =>
            $@"{{""group"":""revenue"",""key"":""{m.Key}"",""label"":""x"",""value"":{m.Value},""formatted"":""x""}}"));
        return JsonDocument.Parse($@"{{""section"":""financial-summary"",""items"":[{items}]}}").RootElement;
    }

    [Fact]
    public void Doc_dung_3_key_that_cua_upstream()
    {
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiRevenue", 500_000_000m),
            ("kpiActualReceived", 300_000_000m),
            ("kpiTotalExpense", 200_000_000m),
            ("kpiGrossProfit", 300_000_000m)));

        Assert.Equal(500_000_000m, n.Revenue);
        Assert.Equal(200_000_000m, n.Expense);
        Assert.Equal(300_000_000m, n.Profit);
    }

    [Fact]
    public void KHONG_nham_sang_so_thuc_te_ben_canh()
    {
        // kpiActualReceived (thực thu) và kpiActualExpense nằm cùng danh sách. Lấy nhầm thì con số
        // vẫn "hợp lý" nên không ai phát hiện — phải khoá đúng cặp tổng.
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiActualReceived", 111m), ("kpiRevenue", 999m),
            ("kpiActualExpense", 222m), ("kpiTotalExpense", 888m),
            ("kpiActualProfit", 333m), ("kpiGrossProfit", 777m)));

        Assert.Equal(999m, n.Revenue);
        Assert.Equal(888m, n.Expense);
        Assert.Equal(777m, n.Profit);
    }

    [Fact]
    public void Thieu_loi_nhuan_thi_tu_tinh_doanh_thu_tru_chi_phi()
    {
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiRevenue", 100m), ("kpiTotalExpense", 40m)));
        Assert.Equal(60m, n.Profit);
    }

    [Fact]
    public void Lo_thi_ra_so_AM_chu_khong_ve_0()
    {
        // Lỗ là thông tin quan trọng nhất của bản tin điều hành; kẹp về 0 là che mất.
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiRevenue", 100m), ("kpiTotalExpense", 250m)));
        Assert.Equal(-150m, n.Profit);
    }

    [Fact]
    public void Loi_nhuan_upstream_bang_0_thi_TON_TRONG_khong_tu_tinh_lai()
    {
        // 0 do upstream trả là con số THẬT (doanh thu vừa đủ bù chi). Tự tính lại sẽ ghi đè
        // bằng rev−exp và bịa ra số khác với báo cáo chính thức.
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiRevenue", 100m), ("kpiTotalExpense", 40m), ("kpiGrossProfit", 0m)));
        Assert.Equal(0m, n.Profit);
    }

    [Fact]
    public void Envelope_rong_hoac_thieu_items_thi_ve_0_chu_khong_no()
    {
        Assert.Equal(new CeoNumbers(0, 0, 0), CeoBriefWorkflow.ReadFinancial(Envelope()));
        Assert.Equal(new CeoNumbers(0, 0, 0),
            CeoBriefWorkflow.ReadFinancial(JsonDocument.Parse(@"{""section"":""x""}").RootElement));
    }

    [Fact]
    public void Key_la_thi_bo_qua_chu_khong_lam_lech_so()
    {
        var n = CeoBriefWorkflow.ReadFinancial(Envelope(
            ("kpiSomethingNew", 12345m), ("kpiRevenue", 10m), ("kpiTotalExpense", 3m)));
        Assert.Equal(10m, n.Revenue);
        Assert.Equal(7m, n.Profit);
    }

    // ── Khoảng cùng kỳ tháng trước ─────────────────────────────────────────────

    [Fact]
    public void Cung_ky_thang_truoc_phai_CUNG_SO_NGAY()
    {
        // 12/08 → 01/07..12/07. Lấy trọn tháng 7 thì tháng nào cũng ra "giảm mạnh".
        var (s, e) = CeoBriefWorkflow.PrevPeriod(new DateTime(2026, 8, 12));
        Assert.Equal(new DateTime(2026, 7, 1), s);
        Assert.Equal(new DateTime(2026, 7, 12), e);
    }

    [Fact]
    public void Ngay_31_bi_kep_vao_cuoi_thang_ngan_KHONG_tran_sang_thang_nay()
    {
        // 31/03 → kỳ trước 01/02..28/02. Cộng thẳng 30 ngày sẽ ra 03/03, tràn sang tháng 3 và
        // ăn trùng chính kỳ đang so — so sánh mất nghĩa mà vẫn ra số trông hợp lý.
        var (s, e) = CeoBriefWorkflow.PrevPeriod(new DateTime(2026, 3, 31));
        Assert.Equal(new DateTime(2026, 2, 1), s);
        Assert.Equal(new DateTime(2026, 2, 28), e);
        Assert.Equal(2, e.Month);
    }

    [Fact]
    public void Nam_nhuan_thi_lay_duoc_29_02()
    {
        var (s, e) = CeoBriefWorkflow.PrevPeriod(new DateTime(2024, 3, 30));
        Assert.Equal(new DateTime(2024, 2, 1), s);
        Assert.Equal(new DateTime(2024, 2, 29), e);
    }

    [Fact]
    public void Thang_1_thi_lui_ve_thang_12_nam_truoc()
    {
        var (s, e) = CeoBriefWorkflow.PrevPeriod(new DateTime(2026, 1, 5));
        Assert.Equal(new DateTime(2025, 12, 1), s);
        Assert.Equal(new DateTime(2025, 12, 5), e);
    }

    [Fact]
    public void Mung_1_thi_ky_truoc_dung_1_ngay_chu_khong_am()
    {
        var (s, e) = CeoBriefWorkflow.PrevPeriod(new DateTime(2026, 8, 1));
        Assert.Equal(new DateTime(2026, 7, 1), s);
        Assert.Equal(new DateTime(2026, 7, 1), e);
        Assert.True(e >= s);
    }
}
