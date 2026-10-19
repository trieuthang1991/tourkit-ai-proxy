using System.Text.RegularExpressions;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Khoá phòng chat nội bộ (hàm thuần) + canh SQL của InternalChatRepository ở mức mã nguồn.
/// Canh đúng những chỗ đã chọn vì hiệu năng/cô lập tenant, không canh tổng quát.
/// </summary>
public class InternalChatRepositoryGuardTests
{
    [Fact]
    public void Khoa_1_1_khong_phu_thuoc_thu_tu()
    {
        Assert.Equal("direct:3:5", InternalRoomKey.Direct(5, 3));
        Assert.Equal(InternalRoomKey.Direct(3, 5), InternalRoomKey.Direct(5, 3));
    }

    [Fact]
    public void Khoa_1_1_tu_chat_voi_minh_thi_nem()
        => Assert.Throws<ArgumentException>(() => InternalRoomKey.Direct(4, 4));

    [Fact]
    public void Khoa_entity_dung_dinh_dang()
    {
        Assert.Equal("order:123", InternalRoomKey.ForEntity("order", "123"));
        Assert.Equal("company:9", InternalRoomKey.ForEntity("company", " 9 "));
        Assert.Throws<ArgumentException>(() => InternalRoomKey.ForEntity("lead", "1"));
        Assert.Throws<ArgumentException>(() => InternalRoomKey.ForEntity("order", " "));
    }

    [Fact]
    public void Khoa_nhom_moi_lan_khac_nhau()
    {
        var a = InternalRoomKey.Group();
        Assert.StartsWith("group:", a);
        Assert.NotEqual(a, InternalRoomKey.Group());
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("order", true)]
    [InlineData("company", true)]
    [InlineData("group", true)]
    [InlineData("lead", false)]
    [InlineData(null, false)]
    public void Loai_phong_hop_le(string? t, bool ok) => Assert.Equal(ok, InternalRoomKey.IsValidType(t));

    // ── SQL ──────────────────────────────────────────────────────────────

    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs");

    [Fact]
    public void Moi_cau_SQL_deu_kep_tenant()
    {
        var khoi = Regex.Matches(Repo(), "\"\"\"([\\s\\S]*?)\"\"\"").Select(m => m.Groups[1].Value)
                        .Where(s => s.Contains("chat_")).ToList();
        Assert.NotEmpty(khoi);
        // Câu CHÈN mang tenant ở VALUES chứ không ở WHERE — vẫn là kẹp tenant.
        foreach (var s in khoi)
            Assert.True(s.Contains("tenant_id = @tenant") || s.Contains("VALUES (@tenant"),
                        "Câu SQL thiếu kẹp tenant:\n" + s);
    }

    [Fact]
    public void Tao_phong_ON_CONFLICT_khop_chi_muc()
    {
        var chiMuc = Regex.Match(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs"),
            @"CREATE UNIQUE INDEX IF NOT EXISTS ux_conv_scope_surface\s+ON chat_conversations \(([^)]*)\)");
        var onConflict = Regex.Match(Repo(), @"INSERT INTO chat_conversations[\s\S]*?ON CONFLICT \(([^)]*)\)");
        Assert.True(chiMuc.Success && onConflict.Success);
        static string Chuan(string s) => string.Join(",", s.Split(',').Select(x => x.Trim()));
        Assert.Equal(Chuan(chiMuc.Groups[1].Value), Chuan(onConflict.Groups[1].Value));
    }

    [Fact]
    public void Them_thanh_vien_mot_cau_unnest()
    {
        var than = InternalChatGuardTests.ThanHam(Repo(), "CreateOrGetRoomAsync");
        Assert.Contains("unnest(@", than);
        Assert.Contains("ON CONFLICT (conversation_id, user_id) DO UPDATE SET left_utc = NULL", than);
    }

    [Fact]
    public void Lich_su_di_dung_chi_muc_ix_msg_conv()
        => Assert.Contains("ORDER BY m.created_utc DESC, m.id DESC",
                           InternalChatGuardTests.ThanHam(Repo(), "ListMessagesAsync"));

    [Theory]
    [InlineData("AppendMessageAsync")]
    [InlineData("ListMessagesAsync")]
    [InlineData("ListMessagesAfterAsync")]
    [InlineData("MarkReadAsync")]
    public void Kiem_thanh_vien_gop_trong_cau(string method)
        => Assert.Contains("FROM chat_room_members", InternalChatGuardTests.ThanHam(Repo(), method));

    [Fact]
    public void Khong_co_ham_kiem_thanh_vien_rieng()
        => Assert.DoesNotContain("IsMemberAsync", Repo());

    [Fact]
    public void Dem_chua_doc_co_tran()
        => Assert.Contains("LIMIT 100", InternalChatGuardTests.ThanHam(Repo(), "ListRoomsAsync"));
}
