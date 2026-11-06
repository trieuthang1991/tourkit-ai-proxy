using TourkitAiProxy.Services.NccImport;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Hai thứ canh ở đây đều sinh ra từ một sự cố thật trên travelai.vn 06/11/2026: tải lên
/// "Hanoi Emotion - CONTRACT RATES 2027.pdf" thì màn hình hiện đúng một dòng
/// <c>"Lỗi server (UpstreamException): 9routes error"</c> — không nói được chuyện gì, và người
/// dùng không biết phải làm gì tiếp.
/// </summary>
public class NccImportUpstreamTests
{
    // ── Cắt cho vừa cửa sổ ngữ cảnh ──────────────────────────────────────────

    [Fact]
    public void File_ngan_thi_giu_nguyen_khong_bao_cat()
    {
        var (ra, daCat) = NccImportService.CatBotChoVua("bảng giá ngắn");
        Assert.Equal("bảng giá ngắn", ra);
        Assert.False(daCat);
    }

    [Fact]
    public void File_dung_bang_tran_van_khong_cat()
        => Assert.False(NccImportService.CatBotChoVua(new string('x', NccImportService.TranKyTu)).DaCat);

    [Fact]
    public void File_dai_thi_cat_va_bao_da_cat()
    {
        var (ra, daCat) = NccImportService.CatBotChoVua(new string('x', NccImportService.TranKyTu + 5_000));
        Assert.True(daCat);
        Assert.True(ra.Length <= NccImportService.TranKyTu);
    }

    /// <summary>
    /// Cắt giữa một dòng bảng giá thì AI đọc được nửa dòng rồi <b>bịa nốt phần còn lại</b> — sai
    /// số im lặng, tệ hơn hẳn việc mất hẳn dòng đó. Nên lùi về cuối dòng gần nhất.
    /// </summary>
    [Fact]
    public void Cat_o_ranh_gioi_dong_chu_khong_cat_giua_dong()
    {
        // Dòng cuối bắt đầu ngay sát trần → phải bị bỏ trọn, không để lại nửa dòng.
        var vanBan = new string('a', NccImportService.TranKyTu - 10) + "\n" + new string('b', 5_000);
        var (ra, _) = NccImportService.CatBotChoVua(vanBan);
        Assert.DoesNotContain("b", ra);
        Assert.EndsWith("a", ra);
    }

    /// Một dòng khổng lồ không có xuống dòng nào thì cứ cắt thẳng, đừng trả về rỗng.
    [Fact]
    public void Mot_dong_khong_lo_thi_van_cat_thang()
    {
        var (ra, daCat) = NccImportService.CatBotChoVua(new string('x', NccImportService.TranKyTu * 2));
        Assert.True(daCat);
        Assert.Equal(NccImportService.TranKyTu, ra.Length);
    }

    // ── Dịch lỗi nhà cung cấp ────────────────────────────────────────────────

    /// Tên nhà cung cấp là chuyện nội bộ — người dùng không chọn, không sửa được.
    [Theory]
    [InlineData(400, "9routes error")]
    [InlineData(500, "upstream exploded")]
    [InlineData(429, "rate limited")]
    public void Khong_bao_gio_phun_ten_nha_cung_cap_ra_man_hinh(int status, string body)
    {
        var s = NccImportService.MoTaLoiAi(status, body);
        Assert.DoesNotContain("9routes", s, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upstream", s, System.StringComparison.OrdinalIgnoreCase);
    }

    /// Ca thường gặp nhất với báo giá PDF nhiều trang — mỗi hãng gọi một kiểu, phải khớp được hết.
    [Theory]
    [InlineData(400, "This model's maximum context length is 128000 tokens")]
    [InlineData(400, "input is too long for requested model")]
    [InlineData(400, "number of tokens exceeded the limit")]
    [InlineData(413, "")]
    public void Qua_dai_thi_bao_tach_bot_trang(int status, string body)
        => Assert.Contains("Tách bớt trang", NccImportService.MoTaLoiAi(status, body));

    [Theory]
    [InlineData(401, "báo quản trị")]
    [InlineData(403, "báo quản trị")]
    [InlineData(429, "Chờ một lát")]
    [InlineData(503, "lỗi phía họ")]
    public void Moi_ma_loi_deu_noi_duoc_viec_phai_lam(int status, string mong)
        => Assert.Contains(mong, NccImportService.MoTaLoiAi(status, ""), System.StringComparison.OrdinalIgnoreCase);

    /// Mã lạ thì vẫn phải nêu mã ra để còn tra, kèm một đường lui dùng được.
    [Fact]
    public void Ma_la_thi_neu_ma_va_duong_lui()
    {
        var s = NccImportService.MoTaLoiAi(418, "lạ hoắc");
        Assert.Contains("418", s);
        Assert.Contains("dán", s);
    }

    /// Thân lỗi rỗng/null không được làm hàm ném.
    [Fact]
    public void Than_loi_rong_khong_nem()
    {
        Assert.False(string.IsNullOrWhiteSpace(NccImportService.MoTaLoiAi(400, null)));
        Assert.False(string.IsNullOrWhiteSpace(NccImportService.MoTaLoiAi(0, "")));
    }
}
