using System;
using System.Linq;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Lọc theo nhãn phải có mặt ở <b>CẢ HAI</b> câu: liệt kê VÀ đếm.
///
/// <para>Chip trạng thái ("Mới 3 · Đang xử lý 7") đứng ngay trên danh sách và nói về ĐÚNG danh
/// sách đang hiện — đó là luật đã chốt hồi thêm lọc theo kênh (28/08/2026, xem chú thích ở chữ ký
/// <c>CountAsync</c>). Lọc nhãn mà chỉ sửa câu liệt kê thì chip đếm cả công ty trong khi danh sách
/// chỉ hiện khách mang nhãn: hai con số mâu thuẫn nhau, cạnh nhau, trên cùng một màn hình.</para>
/// </summary>
public class ChatTagFilterGuardTests
{
    private static string Kho()
        => BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));

    /// <summary>Bỏ dòng chú thích để chốt soi vào mã thật, không soi vào lời hứa trong chú thích.</summary>
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    /// <summary>
    /// Cắt thân hàm theo mốc cú pháp — dùng chung với chốt luật xem, xem
    /// <see cref="ChatSchemaGuardTests.ThanThanhVien"/>. Không cắt theo N ký tự cố định: thân hàm
    /// dài thêm vài dòng là chốt hoặc mù, hoặc trèo sang hàm kế rồi xanh nhờ mã của hàm khác.
    /// </summary>
    private static string Than(string src, string chuKy)
        => ChatSchemaGuardTests.ThanThanhVien(src, chuKy);

    [Theory]
    [InlineData("public async Task<List<ChatConversation>> ListConversationsAsync(")]
    [InlineData("public async Task<ChatInboxCounts> CountAsync(")]
    public void Ca_liet_ke_lan_dem_deu_loc_theo_nhan(string ham)
    {
        var than = Than(Kho(), ham);

        Assert.Contains("chat_contact_tags", than);
        // Đúng khuôn mảng của kho này: ANY(@nhan) — và ép kiểu tường minh vì tham số có thể null,
        // giống @sauLuc::timestamptz ngay trong cùng câu.
        Assert.Contains("ANY(@nhan::text[])", than);
        // Kẹp công ty bằng THAM SỐ, không bằng cột: ChatTagCatalogGuardTests đòi đúng chữ này trên
        // mọi câu SQL chạm bảng nhãn. Viết t.tenant_id = v.tenant_id thì chốt kia đỏ.
        Assert.Contains("t.tenant_id = @tenant", than);
    }

    /// <summary>
    /// Canh chính bài canh: nếu <see cref="Than"/> cắt trượt (đổi chữ ký, đổi thụt lề) thì hai bài
    /// trên xanh vì soi vào chuỗi rỗng chứ không vì mã đúng.
    /// </summary>
    [Fact]
    public void Cua_so_cat_phai_dung_vao_dung_ham()
    {
        var dem = Than(Kho(), "public async Task<ChatInboxCounts> CountAsync(");

        Assert.Contains("GROUP BY v.status, v.channel", dem);
        // Không được trèo sang hàm kế tiếp.
        Assert.DoesNotContain("class RowCount", dem);
        Assert.DoesNotContain("ORDER BY v.last_activity_at DESC", dem);
    }
}
