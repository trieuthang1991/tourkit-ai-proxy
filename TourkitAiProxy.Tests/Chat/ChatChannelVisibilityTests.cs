using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Tests.Chat;

/// Ẩn kênh theo cấu hình — ca dùng thật 10/11/2026: Facebook chưa duyệt ứng dụng Meta nên tin
/// Facebook đi vòng qua Pancake, tab Messenger phải biến mất khỏi hộp thư.
public class ChatChannelVisibilityTests
{
    [Fact]
    public void Khong_khai_gi_thi_KHONG_an_kenh_nao()
    {
        // Ngược hướng an toàn của FeatureFlags, và cố ý: quên khai lúc deploy mà mất sạch kênh
        // đang chạy thật thì không có lấy một dòng log để lần ra.
        Assert.Empty(ChatChannelVisibility.Parse(null));
        Assert.Empty(ChatChannelVisibility.Parse(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Messenger")]
    [InlineData("messenger")]   // người khai gõ thường
    [InlineData("MESSENGER")]
    [InlineData("  Messenger  ")]   // thừa dấu cách khi chép tay
    [InlineData("1")]           // bản ghi cũ / máy sinh cấu hình dùng số
    public void Doc_duoc_ca_ten_lan_so_va_khong_phan_biet_hoa_thuong(string khai)
        => Assert.Equal(new[] { ChatChannel.Messenger }, ChatChannelVisibility.Parse(new[] { khai }));

    [Fact]
    public void An_duoc_nhieu_kenh_cung_luc()
    {
        var ra = ChatChannelVisibility.Parse(new[] { "Messenger", "Instagram" });

        // Instagram đi kèm Messenger vì nó nối THÔNG QUA một Trang Facebook — chưa duyệt
        // Facebook thì bày tab Instagram ra cũng chỉ là một nút bấm vào không nối được.
        Assert.Equal(2, ra.Count);
        Assert.Contains(ChatChannel.Messenger, ra);
        Assert.Contains(ChatChannel.Instagram, ra);
    }

    [Theory]
    [InlineData("KhongCoKenhNay")]
    [InlineData("9999")]        // số không ứng với kênh nào
    [InlineData("")]
    [InlineData("   ")]
    public void Khai_sai_thi_BO_QUA_chu_khong_nem_loi(string rac)
    {
        // Gõ sai một chữ trong file cấu hình mà làm cả máy chủ không khởi động được là đổi một
        // phiền nhỏ lấy một sự cố lớn.
        var ra = ChatChannelVisibility.Parse(new[] { rac, "Messenger" });

        Assert.Equal(new[] { ChatChannel.Messenger }, ra);
    }

    [Fact]
    public void Khai_null_lan_trong_mang_cung_khong_lam_do()
        => Assert.Equal(new[] { ChatChannel.Zalo },
            ChatChannelVisibility.Parse(new string?[] { null, "Zalo", null }));

    [Fact]
    public void Khai_trung_thi_chi_tinh_mot_lan()
        => Assert.Single(ChatChannelVisibility.Parse(new[] { "TikTok", "tiktok", "6" }));

    // ── Đọc qua IConfiguration thật ──────────────────────────────────────────
    // Hàm thuần đúng mà đường đọc cấu hình sai khoá thì vẫn hỏng câm: khai vào file xong không
    // kênh nào bị ẩn, và không có gì báo. Nên phải dựng đúng hình dạng mảng của .NET để kiểm.

    private static IConfiguration Dung(params (string Khoa, string GiaTri)[] o)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(o.Select(x => new KeyValuePair<string, string?>(x.Khoa, x.GiaTri)))
            .Build();

    [Fact]
    public void Doc_dung_khoa_Chat_HiddenChannels_tu_file_cau_hinh()
    {
        var cfg = Dung(("Chat:HiddenChannels:0", "Messenger"), ("Chat:HiddenChannels:1", "Instagram"));

        Assert.True(ChatChannelVisibility.IsHidden(cfg, ChatChannel.Messenger));
        Assert.True(ChatChannelVisibility.IsHidden(cfg, ChatChannel.Instagram));
    }

    [Fact]
    public void Kenh_khong_khai_thi_van_mo__Pancake_la_duong_thay_the_nen_tuyet_doi_khong_duoc_an_lay()
    {
        var cfg = Dung(("Chat:HiddenChannels:0", "Messenger"), ("Chat:HiddenChannels:1", "Instagram"));

        Assert.False(ChatChannelVisibility.IsHidden(cfg, ChatChannel.Pancake));
        Assert.False(ChatChannelVisibility.IsHidden(cfg, ChatChannel.Zalo));
        Assert.False(ChatChannelVisibility.IsHidden(cfg, ChatChannel.Telegram));
    }

    [Fact]
    public void Thieu_han_muc_Chat_trong_file_cau_hinh_cung_khong_no()
        => Assert.Empty(ChatChannelVisibility.Hidden(Dung(("Mot:Khoa:Khac", "x"))));
}
