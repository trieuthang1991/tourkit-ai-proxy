using System.Text.RegularExpressions;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chat nội bộ (channel = 7) nằm CHUNG bảng với hội thoại khách. Rủi ro chính của thiết kế là
/// phòng nội bộ lọt sang luồng khách — vòng quay chia việc giao nhầm, hộp thư "tất cả kênh" hiện
/// nhầm, dọn theo mốc xoá nhầm — mà không có lỗi nào nổ ra. Không có CI chạy PostgreSQL nên canh
/// ở mức mã nguồn, theo TÊN HÀM đã biết là nguy hiểm (cố ý không canh mọi câu FROM: chặt quá thì
/// sau khó mở rộng — chốt 19/10/2026).
/// </summary>
public class InternalChatGuardTests
{
    [Fact]
    public void Internal_channel_value_is_7()
        => Assert.Equal((short)7, (short)ChatChannel.Internal);

    [Theory]
    [InlineData("ChatRepository.cs", "ListConversationsAsync")]
    [InlineData("ChatRepository.cs", "CountAsync")]
    [InlineData("ChatRepository.cs", "GetConversationAsync")]
    [InlineData("ChatRepository.cs", "GetConversationByMessageAsync")]
    [InlineData("ChatAssignRepository.cs", "HoiThoaiChuaCoNguoiAsync")]
    public void Hoi_thoai_khach_phai_loai_phong_noi_bo(string file, string method)
    {
        var than = ThanHam(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/" + file), method);
        Assert.Contains("channel <> 7", than);
    }

    [Fact]
    public void Don_theo_moc_khong_xoa_phong_noi_bo()
    {
        // Bám thân PruneAsync, không bám chữ "DELETE FROM chat_conversations": câu xoá theo yêu cầu
        // của Meta cũng có chữ đó, và nó đã kẹp đúng kênh của khách nên không cần vế này.
        var than = ThanHam(
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"), "PruneAsync");
        Assert.Contains("DELETE FROM chat_conversations WHERE channel <> 7", than);
    }

    [Fact]
    public void Schema_noi_bo_du_cot_va_chi_muc()
    {
        var sql = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        Assert.Contains("ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_type text", sql);
        Assert.Contains("ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_id", sql);
        Assert.Contains("ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS title", sql);
        Assert.Contains("ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS sender_user_id integer", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_room_members", sql);
        // Vị từ của chỉ mục phải khớp câu WHERE của ListRoomsAsync (left_utc IS NULL) — lệch một
        // chữ là Postgres lặng lẽ quét cả bảng.
        Assert.Matches(@"ix_room_members_user\s+ON chat_room_members \(tenant_id, user_id\) WHERE left_utc IS NULL", sql);
    }

    [Fact]
    public void ListAllAccountsAsync_bo_qua_Internal()
    {
        var than = ThanHam(
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs"),
            "ListAllAccountsAsync");
        Assert.Contains("if (kenh == ChatChannel.Internal) continue;", than);
    }

    /// Thân một hàm: từ dòng khai báo tới dòng khai báo public/private/internal kế tiếp.
    internal static string ThanHam(string src, string method)
    {
        var khaiBao = Regex.Match(src, @"(public|internal|private)\s+(static\s+)?async\s+Task[^\n]*\b" + method + @"\(");
        Assert.True(khaiBao.Success, $"Không thấy khai báo hàm {method}");
        var batDau = khaiBao.Index;
        var ketThuc = Regex.Match(src[(batDau + khaiBao.Length)..], @"\n    (public|internal|private)\s");
        return ketThuc.Success ? src.Substring(batDau, khaiBao.Length + ketThuc.Index) : src[batDau..];
    }
}
