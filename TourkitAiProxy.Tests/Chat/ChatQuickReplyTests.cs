using System.Text.RegularExpressions;
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class ChatQuickReplyTests
{
    [Fact]
    public void ON_CONFLICT_phai_khop_bieu_thuc_cua_chi_muc()
    {
        // Chỉ mục là biểu thức `lower(trigger)`, không phải cột trần. Postgres đòi ON CONFLICT
        // ghi ĐÚNG biểu thức đó; viết `(tenant_id, trigger)` là lỗi lúc CHẠY chứ không phải lúc
        // biên dịch — tức chỉ lộ ra khi có người bấm lưu mẫu thật.
        var schema = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatDb.cs");
        var m = Regex.Match(schema,
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_quick_replies \(([^;]*?)\);");
        Assert.True(m.Success, "chat_quick_replies thiếu chỉ mục duy nhất");
        var bieuThuc = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim().TrimEnd(')');

        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatQuickReplyRepository.cs");
        Assert.Contains($"ON CONFLICT ({bieuThuc})", repo);
    }

    [Theory]
    [InlineData("giá", "gia")]          // bỏ dấu: gõ "/gia" phải ra mẫu "/giá"
    [InlineData("/gia", "gia")]         // người dùng gõ luôn dấu gạch
    [InlineData("  Báo Giá  ", "bao-gia")]
    [InlineData("hẹn lịch", "hen-lich")]
    [InlineData("giá!!!", "gia")]
    public void Chuan_hoa_trigger(string tho, string mong)
        => Assert.Equal(mong, ChatQuickReplyRepository.ChuanHoaTrigger(tho));

    [Fact]
    public void Trigger_rong_thi_nem()
    {
        // Mẫu không có lệnh gọi thì gõ "/" mãi cũng không ra — thà chặn lúc lưu.
        var ex = Assert.Throws<ArgumentException>(() => ChatQuickReplyRepository.ChuanHoaTrigger("///"));

        // Message này được endpoint trả THẲNG cho người dùng. Truyền nameof() vào ArgumentException
        // sẽ nối thêm "(Parameter 'tho')" — đã lọt ra thật một lần khi thử trên staging.
        Assert.DoesNotContain("Parameter", ex.Message);
    }
}
