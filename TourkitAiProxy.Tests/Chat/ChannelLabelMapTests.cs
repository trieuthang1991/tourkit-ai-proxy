using TourkitAiProxy.Infrastructure.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Tên Trang chỉ hiện khi nó PHÂN BIỆT được cái gì đó.
///
/// <para>Công ty nối một Trang thì tên Trang là nhiễu trên mọi dòng — ai cũng biết tin nhắn đến từ
/// đâu, dòng nào cũng lặp lại đúng một chữ. Từ hai Trang (hoặc hai OA, hai bot) cùng kênh trở lên
/// thì ngược lại: thiếu nó, người trực trả lời mà không biết mình đang đứng tên Trang nào.</para>
/// </summary>
public class ChannelLabelMapTests
{
    [Fact]
    public void Mot_tai_khoan_tren_kenh_thi_KHONG_dat_ten()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[] { ((short)1, "pageA", (string?)"Trang A") });
        Assert.Empty(ra);
    }

    [Fact]
    public void Hai_tai_khoan_cung_kenh_thi_dat_ten_ca_hai()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)"Trang A"),
            ((short)1, "pageB", (string?)"Trang B"),
            ((short)4, "oa1",   (string?)"OA duy nhất"),   // kênh khác, một mình → không đặt
        });

        Assert.Equal(2, ra.Count);
        Assert.Equal("Trang A", ra[((short)1, "pageA")]);
        Assert.Equal("Trang B", ra[((short)1, "pageB")]);
        Assert.False(ra.ContainsKey(((short)4, "oa1")));
    }

    /// <summary>
    /// Thiếu <c>label</c> thì lùi về mã tài khoản, KHÔNG bỏ trống: trên màn hình có hai Trang mà
    /// một dòng ghi tên còn dòng kia trống thì người trực đọc thành "dòng trống là Trang khác" —
    /// sai, và không có cách nào biết là sai. Mã tài khoản xấu nhưng phân biệt được.
    /// </summary>
    [Fact]
    public void Thieu_label_thi_lui_ve_ma_tai_khoan_chu_khong_bo_trong()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)null),
            ((short)1, "pageB", (string?)"Trang B"),
        });

        Assert.Equal("pageA", ra[((short)1, "pageA")]);
        Assert.Equal("Trang B", ra[((short)1, "pageB")]);
    }

    /// <summary>Chuỗi rỗng và khoảng trắng cũng là "thiếu" — người dùng để trống ô tên là ra thế này.</summary>
    [Fact]
    public void Label_toan_khoang_trang_cung_coi_la_thieu()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)"   "),
            ((short)1, "pageB", (string?)"Trang B"),
        });

        Assert.Equal("pageA", ra[((short)1, "pageA")]);
    }
}
