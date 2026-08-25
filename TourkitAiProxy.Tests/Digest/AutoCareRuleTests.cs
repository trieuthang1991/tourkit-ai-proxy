using TourkitAiProxy.Services.Digest;
using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Tìm khách đáng chăm mà lâu không ai đụng tới. Yêu cầu quan trọng nhất: danh sách phải NGẮN và
/// ĐÚNG người — một danh sách 200 dòng thì không ai gọi dòng nào.
/// </summary>
public class AutoCareRuleTests
{
    private static readonly DateTime Today = new(2026, 8, 15);

    private static CareCustomer KH(int id, string name, int? quietDays = 100,
        decimal revenue = 50_000_000m, string? rank = "A", string? phone = "0901234567")
        => new(id, name, phone, null, rank, revenue, 1,
               quietDays == null ? null : Today.AddDays(-quietDays.Value));

    private static List<CareLead> Find(IEnumerable<CareCustomer> cs, int quietDays = 90,
        IReadOnlyCollection<string>? ranks = null, bool requireBought = true, int max = 20)
        => AutoCareRule.Find(cs, Today, ranks, quietDays, requireBought, max);

    [Fact]
    public void Khach_im_lau_qua_nguong_thi_vao_danh_sach()
    {
        var r = Find(new[] { KH(1, "Anh Nam", quietDays: 100) });

        var lead = Assert.Single(r);
        Assert.Equal(100, lead.QuietDays);
        Assert.Contains("Anh Nam", lead.Text);
        Assert.Contains("0901234567", lead.Text);   // có số để gọi ngay, khỏi tra lại
    }

    [Fact]
    public void Vua_cham_xong_thi_khong_nhac()
        => Assert.Empty(Find(new[] { KH(1, "Anh Nam", quietDays: 10) }));

    /// Chưa từng được chăm lần nào → BỎ QUA. Trên dữ liệu thật chỉ 15/100 khách có ngày chăm gần
    /// nhất, nên coi "chưa có ngày" là "đã im lâu" sẽ quét về 85% tệp khách — danh sách đó vô dụng.
    [Fact]
    public void Chua_tung_duoc_cham_thi_bo_qua()
        => Assert.Empty(Find(new[] { KH(1, "Anh Nam", quietDays: null) }));

    /// "Khách chưa mua bao giờ và lâu không chăm" chính là dữ liệu rác trong CRM — để lọt vào là
    /// chôn vùi mấy khách thật sự đáng gọi.
    [Fact]
    public void Chua_tung_mua_thi_bo_qua_khi_bat_dieu_kien()
        => Assert.Empty(Find(new[] { KH(1, "Khách rác", revenue: 0m) }));

    [Fact]
    public void Tat_dieu_kien_da_mua_thi_van_lay()
        => Assert.Single(Find(new[] { KH(1, "Khách mới", revenue: 0m) }, requireBought: false));

    [Fact]
    public void Loc_theo_hang_khi_cong_ty_co_chon()
    {
        var cs = new[] { KH(1, "Khách A", rank: "A"), KH(2, "Khách C", rank: "C") };

        var r = Find(cs, ranks: new[] { "A" });

        Assert.Equal("Khách A", Assert.Single(r).Name);
    }

    [Fact]
    public void Khong_chon_hang_thi_lay_het()
        => Assert.Equal(2, Find(new[] { KH(1, "A", rank: "A"), KH(2, "C", rank: "C") }).Count);

    /// Khách chi nhiều lên trước. Sắp theo "im lâu nhất" đơn thuần sẽ đẩy mấy khách mua một lần từ
    /// đời nào lên đầu và che mất khách sộp — người gọi chỉ đọc mấy dòng đầu.
    [Fact]
    public void Khach_chi_nhieu_len_truoc()
    {
        var cs = new[] {
            KH(1, "Khách nhỏ, im rất lâu", quietDays: 900, revenue: 1_000_000m),
            KH(2, "Khách sộp", quietDays: 95, revenue: 500_000_000m),
        };

        Assert.Equal("Khách sộp", Find(cs)[0].Name);
    }

    /// Cùng mức tiền thì ai im lâu hơn lên trước.
    [Fact]
    public void Cung_muc_tien_thi_im_lau_hon_len_truoc()
    {
        var cs = new[] { KH(1, "Im 100 ngày", quietDays: 100), KH(2, "Im 300 ngày", quietDays: 300) };

        Assert.Equal("Im 300 ngày", Find(cs)[0].Name);
    }

    /// Danh sách phải NGẮN: 200 dòng thì không ai gọi dòng nào.
    [Fact]
    public void Cat_danh_sach_theo_gioi_han()
    {
        var cs = Enumerable.Range(1, 50).Select(i => KH(i, $"KH {i}")).ToList();

        Assert.Equal(5, Find(cs, max: 5).Count);
    }

    [Fact]
    public void Khach_khong_co_so_dien_thoai_van_vao_nhung_khong_loi_chu_rong()
    {
        var lead = Assert.Single(Find(new[] { KH(1, "Không số", phone: null) }));

        Assert.DoesNotContain("·", lead.Text);
        Assert.DoesNotContain("null", lead.Text);
    }
}
