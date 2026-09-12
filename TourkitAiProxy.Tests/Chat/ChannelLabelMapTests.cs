using TourkitAiProxy.Infrastructure.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Tên Trang/OA/bot LUÔN được đặt — kể cả kênh chỉ có một tài khoản (chủ dự án chốt 12/09/2026).
///
/// <para>Bản đầu bỏ qua kênh một tài khoản, lập luận "tên Trang là nhiễu vì ai cũng biết tin đến
/// từ đâu". Sai trong thực tế: hộp thư TRỘN NHIỀU KÊNH, và huy hiệu kênh chỉ nói "Facebook" chứ
/// không nói Trang nào. Trên staging — mỗi kênh đúng một tài khoản — luật cũ làm KHÔNG dòng nào
/// hiện nguồn, nên chủ dự án không biết tin đến từ đâu.</para>
/// </summary>
public class ChannelLabelMapTests
{
    [Fact]
    public void Mot_tai_khoan_tren_kenh_VAN_dat_ten()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[] { ((short)1, "pageA", (string?)"Trang A") });
        Assert.Equal("Trang A", ra[((short)1, "pageA")]);
    }

    [Fact]
    public void Hai_tai_khoan_cung_kenh_thi_dat_ten_ca_hai()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)"Trang A"),
            ((short)1, "pageB", (string?)"Trang B"),
            ((short)4, "oa1",   (string?)"OA duy nhất"),   // kênh khác, một mình → VẪN đặt
        });

        Assert.Equal(3, ra.Count);
        Assert.Equal("Trang A", ra[((short)1, "pageA")]);
        Assert.Equal("Trang B", ra[((short)1, "pageB")]);
        Assert.Equal("OA duy nhất", ra[((short)4, "oa1")]);
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
