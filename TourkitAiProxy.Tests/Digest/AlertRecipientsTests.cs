using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

/// Ô "gửi tới ai" của cảnh báo doanh thu bất thường — người dùng gõ tay nên phải chịu được mọi
/// kiểu dán. Sai ở đây thì cấu hình nhìn như đã lưu mà sáng hôm sau không ai nhận được gì.
public class AlertRecipientsTests
{
    [Theory]
    [InlineData("a@x.vn,b@y.vn")]
    [InlineData("a@x.vn; b@y.vn")]
    [InlineData("a@x.vn\nb@y.vn")]
    [InlineData("  a@x.vn ,, b@y.vn  ")]
    [InlineData("a@x.vn,	b@y.vn")]
    public void Chap_nhan_moi_kieu_dau_ngan(string raw)
    {
        // Người dùng hay dán từ Excel/Zalo nên dấu ngăn không đoán trước được. Bắt đúng một dấu
        // thì họ dán vào, thấy "đã lưu", rồi không hiểu vì sao không nhận được thư.
        Assert.Equal(new[] { "a@x.vn", "b@y.vn" }, AlertRecipients.Emails(raw));
    }

    [Fact]
    public void Bo_dia_chi_sai_nhung_giu_dia_chi_dung()
    {
        // Một địa chỉ gõ nhầm KHÔNG được làm hỏng lượt gửi cho những người còn lại.
        Assert.Equal(new[] { "ok@x.vn" }, AlertRecipients.Emails("khongcoat, ok@x.vn, thieu@domain"));
    }

    [Fact]
    public void Trung_dia_chi_khac_hoa_thuong_chi_tinh_mot()
    {
        Assert.Single(AlertRecipients.Emails("A@X.vn, a@x.vn"));
    }

    [Fact]
    public void Chat_id_nhom_am_phai_giu_dau_tru()
    {
        // Lọc char.IsDigit trần sẽ cắt mất dấu trừ → -1001234567890 thành 1001234567890, tức là
        // gửi vào một cuộc trò chuyện khác hẳn (hoặc Telegram báo "chat not found").
        Assert.Equal(new[] { "6234567890", "-1001234567890" },
            AlertRecipients.TelegramChatIds("6234567890, -1001234567890"));
    }

    [Fact]
    public void Chat_id_lan_ky_tu_la_van_lay_duoc_so()
    {
        Assert.Equal(new[] { "6234567890" }, AlertRecipients.TelegramChatIds("id: 6234567890"));
    }

    [Fact]
    public void Dau_cach_KHONG_phai_dau_ngan_vi_so_dien_thoai_hay_viet_tach_nhom()
    {
        // Bản đầu cho dấu cách làm dấu ngăn và test này bắt được: "+84 987 654 321" bị xé thành
        // bốn mảnh rồi gửi nhầm trong im lặng. Dấu cách phải nằm TRONG một giá trị.
        Assert.Equal(new[] { "0987654321" }, AlertRecipients.ZaloPhones("+84 987 654 321"));
        Assert.Equal(new[] { "0912345678" }, AlertRecipients.ZaloPhones("0912 345 678"));
    }

    [Fact]
    public void So_zalo_duoc_chuan_hoa_giong_o_Noi_nhan_cua_toi()
    {
        // Cùng bộ luật với DigestPhone: hai chỗ khai không được cho ra hai kết quả khác nhau.
        var got = AlertRecipients.ZaloPhones("0912345678, +84 987 654 321, khongphaiso");
        Assert.Equal(new[] { "0912345678", "0987654321" }, got);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void De_trong_thi_khong_co_noi_nhan_nao(string? raw)
    {
        // Để trống là lựa chọn HỢP LỆ: cảnh báo vẫn vào Bảng tin, chỉ không gửi ra ngoài.
        Assert.Empty(AlertRecipients.Emails(raw));
        Assert.Empty(AlertRecipients.TelegramChatIds(raw));
        Assert.Empty(AlertRecipients.ZaloPhones(raw));
    }
}
