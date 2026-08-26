using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nối khách chat với khách CRM.
///
/// <para><c>chat_contacts.crm_customer_id</c> đã có cột từ đợt 1 nhưng <b>chưa dòng code nào ghi
/// giá trị vào đó</b> — panel hồ sơ vẫn luôn hiện "chưa nối". Nối TAY trước, đoán tự động sau:
/// ghép theo tên sai thường xuyên (trùng tên là chuyện bình thường ở khách du lịch), ghép theo số
/// điện thoại thì Zalo/Messenger không cho biết số trừ khi khách tự nhắn.</para>
/// </summary>
public class ChatCrmLinkGuardTests
{
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    [Fact]
    public void Co_ham_ghi_ma_khach_CRM()
    {
        var src = Repo();
        Assert.Contains("NoiCrmAsync", src);

        var m = Regex.Match(src, "NoiCrmAsync(.{0,700})", RegexOptions.Singleline);
        Assert.True(m.Success);
        Assert.Contains("crm_customer_id", m.Groups[1].Value);
        // Kẹp tenant ngay trong câu lệnh: thiếu là nối nhầm khách của công ty khác vào hội thoại
        // của mình, và cái sai đó không có triệu chứng gì cho tới lúc bot đọc lịch sử mua sai người.
        Assert.Contains("tenant_id = @tenant", m.Groups[1].Value);
    }

    [Fact]
    public void Co_hai_duong_tim_khach_va_noi_khach()
    {
        var src = Endpoint();
        Assert.Contains("/conversations/{id:long}/crm-search", src);
        Assert.Contains("/conversations/{id:long}/link-crm", src);
    }

    [Fact]
    public void Tim_khach_dung_PHIEN_CUA_NHAN_VIEN_chu_khong_tai_khoan_dich_vu()
    {
        // Dùng tài khoản dịch vụ là CRM không chặn được theo quyền của người đang tìm — nhân viên
        // chỉ được xem khách của mình vẫn tra ra cả kho khách của công ty.
        var src = Endpoint();
        var m = Regex.Match(src, "crm-search(.{0,1400})", RegexOptions.Singleline);
        Assert.True(m.Success);
        Assert.Matches(@"ListAsync\(\s*a\.SessionId", m.Groups[1].Value);
    }

    [Fact]
    public void Duong_moi_nam_trong_DuongRieng()
    {
        // Không nằm trong danh sách thì lúc tắt cờ Features:Chat, đường này rơi vào MapFallback và
        // trả index.html kèm 200 thay vì 404.
        foreach (var duong in new[] { "/api/v1/chat/conversations/1/crm-search",
                                      "/api/v1/chat/conversations/1/link-crm" })
            Assert.Contains(TourkitAiProxy.Endpoints.ChatInboxEndpoints.DuongRieng,
                p => duong.StartsWith(p + "/", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Noi_va_go_noi_deu_vao_nhat_ky()
    {
        // Nối nhầm khách là bot đọc lịch sử mua của người khác rồi nói với khách này. Phải biết ai
        // nối, lúc nào.
        var src = Endpoint();
        Assert.Contains("noi-crm", src);
        Assert.Contains("go-noi-crm", src);
    }
}
