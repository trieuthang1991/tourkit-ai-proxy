using TourkitAiProxy.Domain.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class BriefNarrationTests
{
    [Fact] public void Bo_dau_sao_in_dam()
        => Assert.Equal("Tour Nhật Bản.", BriefNarration.ToSpeakable("**Tour Nhật Bản**"));

    [Fact] public void Bo_emoji_va_gach_dau_dong_ghep_cau()
        => Assert.Equal("Cơ hội cần gọi lại (5). Tour A.",
            BriefNarration.ToSpeakable("**📞 Cơ hội cần gọi lại (5)**\n- Tour A"));

    [Fact] public void Doi_tien_va_phan_tram()
        => Assert.Equal("Doanh thu: 1.234.567 đồng (+12 phần trăm).",
            BriefNarration.ToSpeakable("- Doanh thu: 1.234.567đ (+12%)"));

    [Fact] public void Nhieu_dong_ghep_thanh_cau()
        => Assert.Equal("Xin chào. Hôm nay có 2 việc.",
            BriefNarration.ToSpeakable("Xin chào.\n\nHôm nay có 2 việc"));

    [Fact] public void Giu_nguyen_dau_cau_san_co()
        => Assert.Equal("Hộp thư công ty: 4 thư chờ xử lý.",
            BriefNarration.ToSpeakable("📬 Hộp thư công ty: 4 thư chờ xử lý."));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   \n  ")]
    public void Rong_tra_chuoi_rong(string? input)
        => Assert.Equal("", BriefNarration.ToSpeakable(input));

    [Fact] public void Dong_co_hoi_gach_va_in_dam_long_nhau()
        => Assert.Equal("Tour Nhật Bản — Nguyễn Văn A, im lặng 5 ngày, khả năng chốt 70 phần trăm.",
            BriefNarration.ToSpeakable("- **Tour Nhật Bản** — Nguyễn Văn A · im lặng 5 ngày · khả năng chốt 70%"));

    [Fact] public void Dong_thanh_toan_giu_dinh_dang_tien()
        => Assert.Equal("Tour Đà Nẵng — Trần B thiếu 5.000.000 đồng, còn 3 ngày.",
            BriefNarration.ToSpeakable("- **Tour Đà Nẵng** — Trần B thiếu 5.000.000đ, còn 3 ngày"));
}
