using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Tests.Chat;

public class InternalChatOrderFileTests
{
    [Theory]
    [InlineData("3-210983", 3, 210983)]
    [InlineData(" 2-15 ", 2, 15)]
    [InlineData("101-7", 101, 7)]
    public void Doc_dung_loai_don_va_ma_don(string entityId, int tourType, int tourId)
    {
        Assert.True(InternalRoomKey.TryParseOrder(entityId, out var type, out var id));
        Assert.Equal(tourType, type);
        Assert.Equal(tourId, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DH-0142")]
    [InlineData("123")]
    [InlineData("3-")]
    [InlineData("-5")]
    [InlineData("0-5")]
    [InlineData("3-0")]
    [InlineData("3-5-7")]
    public void Ma_khong_phai_don_CRM_thi_khong_dong_bo(string? entityId)
        => Assert.False(InternalRoomKey.TryParseOrder(entityId, out _, out _));
}
