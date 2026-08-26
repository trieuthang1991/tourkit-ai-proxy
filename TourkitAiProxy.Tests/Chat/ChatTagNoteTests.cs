using System.Text.RegularExpressions;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nhãn và ghi chú của khách.
///
/// <para>Chuẩn hoá nhãn là <b>đúng cùng một vấn đề</b> với lệnh gọi mẫu trả lời nhanh: người Việt
/// gõ nhanh sẽ không bật bộ gõ để ra dấu. Viết lại lần hai là hai chỗ lệch nhau — "khách-vip" ở
/// đây và "khách vip" ở kia, rồi lọc theo nhãn ra rỗng mà không ai hiểu tại sao.</para>
/// </summary>
public class ChatTagNoteTests
{
    private static string Db() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    // ── Hàm chuẩn hoá: test THẬT, không phải guard mã nguồn ─────────────────

    [Theory]
    [InlineData("Khách VIP", "khach-vip")]
    [InlineData("  đã  chốt  ", "da-chot")]
    [InlineData("Cần gọi lại!", "can-goi-lai")]
    [InlineData("tour-nhật-bản", "tour-nhat-ban")]
    [InlineData("ĐÃ-CHỐT", "da-chot")]
    public void Chuan_hoa_nhan_bo_dau_ha_chu_thuong_noi_bang_gach(string tho, string mong)
        => Assert.Equal(mong, ChatRules.ChuanHoaSlug(tho));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void Nhan_rong_thi_tra_chuoi_rong_chu_khong_nem(string tho)
    {
        // Hàm THUẦN thì trả giá trị, không ném: chỗ gọi tự quyết định rỗng là lỗi hay là bỏ qua.
        // Ném từ trong hàm thuần là ép mọi chỗ gọi phải bọc try, kể cả chỗ chỉ muốn lọc bỏ.
        Assert.Equal("", ChatRules.ChuanHoaSlug(tho));
    }

    [Fact]
    public void Lenh_goi_mau_tra_loi_nhanh_dung_CHUNG_ham_do()
    {
        // Hai chỗ chuẩn hoá khác nhau là "khach-vip" bên này và "khach vip" bên kia — lọc theo nhãn
        // ra rỗng mà không ai hiểu tại sao.
        Assert.Equal(ChatRules.ChuanHoaSlug("Khách VIP"),
                     ChatQuickReplyRepository.ChuanHoaTrigger("/Khách VIP"));
    }

    // ── Schema ──────────────────────────────────────────────────────────────

    [Fact]
    public void Co_bang_nhan_khoa_du_bon_cot()
    {
        var sql = Db();
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_contact_tags", sql);
        var m = Regex.Match(sql, "chat_contact_tags(.{0,600})", RegexOptions.Singleline);
        Assert.True(m.Success);
        // Thiếu tenant_id là hai công ty cùng gắn nhãn cho một mã người dùng Zalo sẽ đè lên nhau.
        Assert.Matches(@"PRIMARY KEY \(tenant_id, channel, external_id, tag\)", m.Groups[1].Value);
    }

    [Fact]
    public void Co_bang_ghi_chu_va_chi_muc_tra_theo_khach()
    {
        var sql = Db();
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_contact_notes", sql);
        Assert.Contains("ON chat_contact_notes (tenant_id, channel, external_id, created_utc DESC)", sql);
    }

    [Fact]
    public void ON_CONFLICT_cua_nhan_khop_dung_khoa_chinh()
    {
        // Postgres đòi cột trong ON CONFLICT khớp một chỉ mục duy nhất — lệch là lỗi lúc CHẠY.
        Assert.Contains("ON CONFLICT (tenant_id, channel, external_id, tag)",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));
    }

    [Fact]
    public void Co_du_duong_nhan_va_ghi_chu()
    {
        var src = Endpoint();
        Assert.Contains("/conversations/{id:long}/tags", src);
        Assert.Contains("/conversations/{id:long}/notes", src);
    }

    [Fact]
    public void Duong_moi_nam_trong_DuongRieng()
    {
        foreach (var duong in new[] { "/api/v1/chat/conversations/1/tags",
                                      "/api/v1/chat/conversations/1/notes" })
            Assert.Contains(TourkitAiProxy.Endpoints.ChatInboxEndpoints.DuongRieng,
                p => duong.StartsWith(p + "/", System.StringComparison.Ordinal));
    }
}
