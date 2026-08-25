using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh ở mức mã nguồn (không có CI chạy PostgreSQL). Ba điều phải giữ:
/// bảng sự kiện vào tồn tại, có chống trùng ở tầng CSDL, và webhook KHÔNG còn fire-and-forget.
/// </summary>
public class ChatInboundEventTests
{
    [Fact]
    public void Co_bang_su_kien_vao()
    {
        var sql = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_inbound_events", sql);
    }

    [Fact]
    public void Chong_trung_o_TANG_CSDL_chu_khong_chi_trong_code()
    {
        // Webhook gửi lại đồng thời hai lần thì kiểm-rồi-ghi trong code vẫn lọt. Phải là chỉ mục
        // duy nhất để chính CSDL từ chối bản thứ hai.
        var sql = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        var m = Regex.Match(sql,
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_inbound_events \(([^)]*)\)");
        Assert.True(m.Success, "chat_inbound_events thiếu chỉ mục duy nhất chống trùng");
        Assert.Contains("provider_event_id", m.Groups[1].Value);
    }

    [Fact]
    public void Webhook_khong_con_fire_and_forget()
    {
        // Đã trả 200 nghĩa là kênh sẽ KHÔNG gửi lại. Xử lý còn nằm trong bộ nhớ lúc đó thì
        // IIS recycle / deploy / crash làm mất hẳn tin của khách, không dấu vết.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        Assert.DoesNotContain("Task.Run", src);
    }
}
