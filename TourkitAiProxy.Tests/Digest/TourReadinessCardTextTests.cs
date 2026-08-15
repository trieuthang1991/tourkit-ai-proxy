using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Chữ trên thẻ "sẵn sàng khởi hành". Test ở đây vì chạy thật KHÔNG phủ được nhánh này: tenant thử
/// nghiệm không có tour nào khai số chỗ nên "sắp đầy" không bao giờ chạy tới — mà đó lại đúng là
/// nhánh có chữ dễ sai nhất.
/// </summary>
public class TourReadinessCardTextTests
{
    private static ReadinessCard Card(params ReadinessIssue[] issues)
        => new(TourId: 7, Title: "Tour Nhật Bản", CustomerName: "Chị Lan", SellerName: "Sale2",
               DepartureDate: new DateTime(2026, 8, 22), DaysLeft: 7, Milestone: 7,
               Issues: issues.ToList(), Severity: 1, AlertKey: "readiness:7:7");

    private static readonly ReadinessIssue Payment = new("payment", "còn thiếu 5.000.000đ / 20.000.000đ");
    private static readonly ReadinessIssue NearlyFull = new("nearly_full", "đã kín 17/20 chỗ — còn 3 chỗ, đẩy bán nốt");

    /// Thẻ CHỈ có tin vui: tuyệt đối không được dùng chữ "chưa xong" (báo động giả), và không được
    /// dán tin vui xuống dưới mục "Còn thiếu:" — đọc thành "còn thiếu: sắp đầy".
    [Fact]
    public void Chi_co_tin_vui_thi_khong_dung_chu_chua_xong()
    {
        var t = TourReadinessCardText.Build(Card(NearlyFull));

        Assert.DoesNotContain("chưa xong", t.Title);
        Assert.Contains("sắp đầy chỗ", t.Title);
        Assert.DoesNotContain("Còn thiếu:", t.Body);
        Assert.Contains("Cơ hội:", t.Body);
        Assert.Contains("còn 3 chỗ", t.Body);
    }

    /// Thẻ chỉ có vấn đề: giữ nguyên cách viết cũ, không tự dưng mọc mục "Cơ hội".
    [Fact]
    public void Chi_co_van_de_thi_giu_nguyen_cach_viet_cu()
    {
        var t = TourReadinessCardText.Build(Card(Payment));

        Assert.Contains("còn 1 việc chưa xong", t.Title);
        Assert.Contains("Còn thiếu:", t.Body);
        Assert.DoesNotContain("Cơ hội:", t.Body);
    }

    /// Vừa có vấn đề vừa có tin vui: hai mục riêng, và số "việc chưa xong" CHỈ đếm vấn đề —
    /// đếm cả tin vui vào đó là nói dối người đọc ngay ở tiêu đề.
    [Fact]
    public void Vua_co_van_de_vua_co_tin_vui_thi_tach_hai_muc()
    {
        var t = TourReadinessCardText.Build(Card(Payment, NearlyFull));

        Assert.Contains("còn 1 việc chưa xong", t.Title);
        Assert.Contains("Còn thiếu:", t.Body);
        Assert.Contains("Cơ hội:", t.Body);
        // Thứ tự: vấn đề trước, cơ hội sau.
        Assert.True(t.Body.IndexOf("Còn thiếu:", StringComparison.Ordinal)
                  < t.Body.IndexOf("Cơ hội:", StringComparison.Ordinal));
    }

    [Fact]
    public void Than_the_luon_co_khach_ngay_di_va_nguoi_phu_trach()
    {
        var t = TourReadinessCardText.Build(Card(Payment));

        Assert.Contains("Tour Nhật Bản", t.Body);
        Assert.Contains("Chị Lan", t.Body);
        Assert.Contains("22/08", t.Body);
        Assert.Contains("Sale2", t.Body);
    }

    /// Thiếu tên khách/người phụ trách là chuyện thường trong dữ liệu thật — không được để lòi ra
    /// chữ rỗng hay "null" giữa câu.
    [Fact]
    public void Thieu_ten_thi_thay_bang_dau_hoi_khong_de_trong()
    {
        var c = Card(Payment) with { CustomerName = null, SellerName = null };
        var t = TourReadinessCardText.Build(c);

        Assert.DoesNotContain("null", t.Body);
        Assert.Contains("khách ?", t.Body);
    }
}
