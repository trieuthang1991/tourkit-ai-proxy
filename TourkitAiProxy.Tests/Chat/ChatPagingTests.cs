using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Con trỏ phân trang hộp thư. Hộp thư sắp theo <c>last_activity_at DESC</c> — thứ đổi liên tục khi
/// khách nhắn — nên OFFSET vừa lặp dòng vừa bỏ sót dòng. Con trỏ là hàm thuần, đây là chỗ test thật.
/// </summary>
public class ChatPagingTests
{
    [Fact]
    public void Ma_roi_giai_ra_dung_nguyen_ban()
    {
        var c = new ConvCursor(new DateTime(2026, 8, 25, 10, 30, 15, 123, DateTimeKind.Utc), 4567);
        var lai = ChatCursor.Decode(ChatCursor.Encode(c));
        Assert.NotNull(lai);
        Assert.Equal(c.LastActivityAt, lai!.LastActivityAt);
        Assert.Equal(c.Id, lai.Id);
    }

    [Fact]
    public void Moc_thoi_gian_giu_dung_UTC()
    {
        // Mất Kind=Utc là lệch 7 tiếng — trang sau bắt đầu sai chỗ, người dùng thấy thiếu hội thoại.
        var c = new ConvCursor(new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc), 1);
        var lai = ChatCursor.Decode(ChatCursor.Encode(c))!;
        Assert.Equal(DateTimeKind.Utc, lai.LastActivityAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-phai-ma-hop-le")]
    [InlineData("!!!@@@")]
    public void Ma_hong_thi_tra_null_chu_khong_nem(string? tho)
    {
        // Con trỏ nằm trên URL — người dùng sửa tay, hoặc mã cũ từ bản trước. Ném là cả trang trắng.
        Assert.Null(ChatCursor.Decode(tho));
    }
}
