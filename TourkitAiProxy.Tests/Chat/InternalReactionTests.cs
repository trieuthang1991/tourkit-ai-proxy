using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Thả cảm xúc lên tin nội bộ — dùng lại bảng chat_reactions của hộp thư khách với channel = 7 (không
/// thêm bảng). Bộ biểu tượng cố định như Messenger; mỗi người một biểu tượng trên một tin.
/// </summary>
public class InternalReactionTests
{
    [Theory]
    [InlineData("👍")]
    [InlineData("❤️")]
    [InlineData("😆")]
    [InlineData("😮")]
    [InlineData("😢")]
    [InlineData("😡")]
    public void Bo_bieu_tuong_cho_phep(string e) => Assert.Equal(e, InternalReactions.Normalize(e));

    [Fact]
    public void Tim_thieu_dau_bien_the_van_nhan_va_quy_ve_mot_dang()
        // "❤" (không có U+FE0F) và "❤️" là cùng một biểu tượng — lưu một dạng để đếm không tách đôi.
        => Assert.Equal("❤️", InternalReactions.Normalize("❤"));

    [Theory]
    [InlineData("🍕")]
    [InlineData("hello")]
    [InlineData("👍👍")]
    [InlineData("")]
    [InlineData("<b>")]
    public void Ngoai_bo_thi_khong_nhan(string e) => Assert.Null(InternalReactions.Normalize(e));

    [Fact]
    public void Khoang_trang_hai_dau_bo_qua() => Assert.Equal("👍", InternalReactions.Normalize(" 👍 "));

    [Fact]
    public void Gom_theo_bieu_tuong_dung_thu_tu_tha()
    {
        var rows = new[] { ("😆", 7), ("👍", 8), ("😆", 9) };
        var g = InternalReactions.Group(rows);
        Assert.Equal(2, g.Count);
        Assert.Equal("😆", g[0].Emoji);
        Assert.Equal(new[] { 7, 9 }, g[0].UserIds);
        Assert.Equal(new[] { 8 }, g[1].UserIds);
    }

    [Fact]
    public void Doc_va_ghi_reaction_noi_bo_phai_kep_kenh_7()
    {
        // Cùng bảng với hộp thư khách: thiếu "channel = 7" là đọc/ghi lẫn sang cảm xúc của khách.
        var repo = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs");
        foreach (var ham in new[] { "SetReactionAsync", "ReactionsOfAsync" })
        {
            var than = InternalChatGuardTests.ThanHam(repo, ham);
            Assert.Contains("chat_reactions", than);
            Assert.Contains("channel = 7", than);
            Assert.Contains("tenant_id = @tenant", than);
        }
    }
}
