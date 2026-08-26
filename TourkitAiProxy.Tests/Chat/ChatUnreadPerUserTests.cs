using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chưa đọc phải tính theo TỪNG NGƯỜI.
///
/// <para><c>agent_last_read_at</c> là <b>một cột cho cả công ty</b>: A mở hội thoại thì B cũng mất
/// dấu chưa đọc. Hộp thư một người thì không lộ; hai người trở lên là sai ngay — và sai theo kiểu
/// im lặng, vì không có lỗi nào hiện ra, chỉ có tin của khách trôi qua mắt người thứ hai.</para>
/// </summary>
public class ChatUnreadPerUserTests
{
    private static string Db() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    [Fact]
    public void Co_bang_danh_dau_da_doc_theo_tung_nguoi()
    {
        var sql = Db();
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_conversation_reads", sql);

        var m = Regex.Match(sql, "chat_conversation_reads(.{0,700})", RegexOptions.Singleline);
        Assert.True(m.Success);
        // Khoá phải có ĐỦ BA: thiếu username là quay lại đúng cái bug đang sửa; thiếu tenant_id là
        // hai công ty trùng id hội thoại ghi đè nhau.
        Assert.Matches(@"PRIMARY KEY \(tenant_id, conversation_id, username\)", m.Groups[1].Value);
    }

    [Fact]
    public void ON_CONFLICT_khop_dung_khoa_chinh()
    {
        // Postgres đòi cột trong ON CONFLICT khớp một chỉ mục duy nhất — lệch là lỗi lúc CHẠY,
        // nghĩa là chỉ lộ ra khi nhân viên mở hội thoại thật.
        Assert.Contains("ON CONFLICT (tenant_id, conversation_id, username)", Repo());
    }

    [Fact]
    public void Van_GIU_agent_last_read_at_lam_moc_ban_dau()
    {
        // Xoá cột cũ là mọi hội thoại cũ bật lại thành "chưa đọc" cho tất cả mọi người ngay sau
        // khi deploy — cả đội mở app thấy hàng trăm chấm đỏ giả.
        Assert.Contains("agent_last_read_at", Db());
        Assert.Contains("agent_last_read_at", Repo());
    }

    [Fact]
    public void Danh_dau_da_doc_KHONG_con_dung_cot_chung_cua_ca_cong_ty()
    {
        // Còn ghi vào cột chung nghĩa là A đọc xong thì B cũng mất dấu — đúng cái bug đang sửa,
        // chỉ khác là nay có thêm một bảng trông như đã sửa.
        Assert.DoesNotContain("SET agent_last_read_at = now()", Repo());
    }

    [Fact]
    public void Dem_chua_doc_nhan_ten_nguoi_dang_xem()
    {
        // Đếm mà không biết đang đếm cho ai thì chỉ có thể đếm chung cho cả công ty.
        Assert.Matches(@"Task<ChatInboxCounts>\s+CountAsync\([^)]*nguoiDung", Repo());
    }
}
