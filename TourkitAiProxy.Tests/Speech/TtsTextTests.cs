using TourkitAiProxy.Services.Speech;
using Xunit;

namespace TourkitAiProxy.Tests.Speech;

/// <summary>
/// Google TTS có giới hạn độ dài, nên chữ dài phải cắt bớt. Bản cũ cắt THẲNG giữa từ và KHÔNG báo
/// gì — người bấm "Nghe" một bản tin dài sẽ nghe hết nửa câu rồi im, và tưởng bản tin chỉ có thế.
///
/// Ở đây không nâng giới hạn (giới hạn là của nhà cung cấp), chỉ làm cho việc cắt (1) rơi vào chỗ
/// nghỉ tự nhiên và (2) nói ra là đã cắt.
/// </summary>
public class TtsTextTests
{
    [Fact]
    public void Chu_ngan_hon_gioi_han_thi_giu_nguyen()
    {
        var r = TtsText.Cap("Chào buổi sáng. Hôm nay có 3 việc cần làm.", 2000);
        Assert.False(r.Truncated);
        Assert.Equal("Chào buổi sáng. Hôm nay có 3 việc cần làm.", r.Text);
    }

    [Fact]
    public void Dung_bang_gioi_han_thi_khong_coi_la_cat()
    {
        var s = new string('a', 100);
        var r = TtsText.Cap(s, 100);
        Assert.False(r.Truncated);
        Assert.Equal(100, r.Text.Length);
    }

    [Fact]
    public void Cat_o_cuoi_CAU_gan_nhat_chu_khong_cat_giua_cau()
    {
        // Chữ không dấu CỐ Ý: chỗ cắt tính theo chỉ số ký tự, mà tiếng Việt có dấu tổ hợp nên
        // đếm tay dễ sai — test sẽ hỏng vì phép đếm của người viết chứ không phải vì code sai.
        var r = TtsText.Cap("Cau mot xong roi. Cau hai dang do dang thi bi cat.", 25);
        Assert.True(r.Truncated);
        Assert.Equal("Cau mot xong roi.", r.Text);
    }

    [Fact]
    public void Het_cau_nam_lui_qua_xa_thi_cat_o_cuoi_tu_thay_vi_nuot_bot()
    {
        // Cùng chuỗi trên nhưng giới hạn 30: dấu chấm lùi tới 43% — quá mức lùi cho phép, vì lùi
        // về đó là vứt gần nửa phần đọc được. Ưu tiên hết-câu KHÔNG được lấn át việc giữ nội dung.
        var r = TtsText.Cap("Cau mot xong roi. Cau hai dang do dang thi bi cat.", 30);
        Assert.True(r.Truncated);
        Assert.Equal("Cau mot xong roi. Cau hai", r.Text);
    }

    [Fact]
    public void Cat_dung_ca_khi_chu_co_dau_tieng_Viet()
    {
        // Không khẳng định cắt ở ĐÚNG ký tự nào (xem lý do ở test trên), chỉ khẳng định điều thật
        // sự quan trọng: không đứt giữa từ và không dài quá giới hạn.
        var r = TtsText.Cap("Cơ hội cần gọi lại. Nguyễn Hạnh im lặng bốn tám ngày rồi nhé.", 30);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= 30);
        Assert.StartsWith("Cơ hội cần gọi lại", r.Text);
        Assert.False(r.Text.EndsWith(" "), "không được để lại khoảng trắng cuối");
    }

    [Fact]
    public void Khong_co_dau_cham_thi_cat_o_cuoi_TU_chu_khong_giua_tu()
    {
        var r = TtsText.Cap("một hai ba bốn năm sáu bảy tám", 14);
        Assert.True(r.Truncated);
        Assert.Equal("một hai ba", r.Text);   // không được ra "một hai ba bố"
        Assert.DoesNotContain("bố", r.Text);
    }

    [Fact]
    public void Mot_tu_dai_khong_cho_cat_thi_danh_cat_cung()
    {
        // Không có chỗ nghỉ nào → thà cắt cứng còn hơn trả rỗng.
        var r = TtsText.Cap(new string('x', 50), 10);
        Assert.True(r.Truncated);
        Assert.Equal(10, r.Text.Length);
    }

    [Fact]
    public void Cham_hoi_va_cham_than_cung_la_het_cau()
    {
        Assert.Equal("Ban khoe khong?", TtsText.Cap("Ban khoe khong? Toi thi on lam nhe.", 22).Text);
        Assert.Equal("Xong roi roi!", TtsText.Cap("Xong roi roi! Con viec nua dang cho ban.", 20).Text);
    }

    [Fact]
    public void Khong_lui_qua_xa_de_khoi_nuot_gan_het_bai()
    {
        // Câu đầu rất ngắn rồi một câu rất dài: lùi về câu đầu thì mất gần hết nội dung.
        // Trường hợp này cắt ở cuối TỪ trong phạm vi cho phép, giữ được nhiều chữ hơn.
        var s = "Ừ. " + string.Join(" ", Enumerable.Repeat("chữ", 40));
        var r = TtsText.Cap(s, 60);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length > 30, $"lùi quá xa, chỉ còn {r.Text.Length} ký tự: '{r.Text}'");
    }

    [Fact]
    public void Rong_hoac_null_tra_rong_va_khong_bao_cat()
    {
        Assert.False(TtsText.Cap(null, 100).Truncated);
        Assert.Equal("", TtsText.Cap(null, 100).Text);
        Assert.Equal("", TtsText.Cap("   ", 100).Text);
    }
}
