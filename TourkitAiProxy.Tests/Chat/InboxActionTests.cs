using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh các quyết định thiết kế của nhóm hành động hộp thư — những chỗ mà làm sai thì
/// <b>không có lỗi nào hiện lên</b>, chỉ có nút bấm không ra kết quả.
///
/// <para><b>Vì sao canh bằng mã nguồn chứ không bằng hàm thuần.</b> Bản đầu của bộ test này gọi
/// một hàm <c>ChatRules.MocChuaDoc</c> viết riêng cho việc lùi mốc đọc — nhưng câu lệnh thật lại
/// tự trừ khoảng thời gian ngay trong SQL, nên hàm đó <b>không chỗ nào gọi</b>. Ba test xanh mà
/// canh một hàm chết: đổi <c>1 millisecond</c> thành <c>1 second</c> trong SQL thì vẫn xanh. Guard
/// không bao giờ đỏ được thì tệ hơn không có guard, vì nó tạo cảm giác đã được canh.</para>
/// </summary>
public class InboxActionTests
{
    private static string Repo()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    /// <summary>Bóc thân một phương thức của repository ra để soi câu SQL bên trong.</summary>
    private static string Than(string ten, int daiToiDa = 1400)
    {
        var i = Repo().IndexOf(ten, System.StringComparison.Ordinal);
        Assert.True(i > 0, $"Không thấy {ten} trong ChatRepository");
        var s = Repo();
        return s.Substring(i, System.Math.Min(daiToiDa, s.Length - i));
    }

    [Fact]
    public void Danh_dau_chua_doc_phai_DAT_moc_chu_khong_XOA_dong()
    {
        // Xoá dòng đọc thì phép tính chưa đọc lùi về cột chung agent_last_read_at — vốn có thể vẫn
        // mới vì người khác vừa mở — và hội thoại vẫn hiện là ĐÃ đọc. Người dùng bấm nút, không
        // thấy gì đổi, và không có lỗi nào để lần ra.
        var than = Than("MarkUnreadAsync");

        Assert.Contains("INSERT INTO chat_conversation_reads", than);
        Assert.Contains("ON CONFLICT", than);
        Assert.DoesNotContain("DELETE FROM chat_conversation_reads", than);
    }

    [Fact]
    public void Moc_chua_doc_phai_lui_ve_TRUOC_tin_cuoi_cua_khach()
    {
        // Danh sách tính chưa đọc bằng phép so LỚN HƠN THỰC SỰ:
        //     contact_replied_at > last_read_at
        // nên đặt mốc BẰNG ĐÚNG thời điểm tin cuối là không đủ — bấm nút mà không có gì xảy ra.
        // Phải trừ đi một khoảng, và khoảng đó phải NHỎ: trừ cả phút thì những tin khách gửi
        // trước đó vài giây cũng thành chưa đọc oan.
        var than = Than("MarkUnreadAsync");

        var m = Regex.Match(than, @"created_utc\s*-\s*interval\s*'(\d+)\s*(millisecond|second)s?'");
        Assert.True(m.Success,
            "Câu lệnh phải trừ một khoảng khỏi created_utc của tin cuối, nếu không hội thoại "
            + "vẫn hiện là đã đọc — xem phép so trong ListConversationsAsync.");

        var so = int.Parse(m.Groups[1].Value);
        var giay = m.Groups[2].Value == "second" ? so : so / 1000.0;
        Assert.True(giay > 0, "Khoảng trừ phải lớn hơn 0");
        Assert.True(giay <= 1, "Trừ quá 1 giây là kéo theo tin cũ thành chưa đọc oan");
    }

    [Fact]
    public void Danh_dau_chua_doc_phai_khoa_dung_cong_ty()
    {
        // Không khoá tenant thì một id đoán được của công ty khác cũng đánh dấu được — và vì
        // bảng đọc không có khoá ngoại sang hội thoại, câu lệnh vẫn chạy trơn tru.
        var than = Than("MarkUnreadAsync");
        Assert.Contains("c.tenant_id = @tenant", than);
        Assert.Contains("m.tenant_id = @tenant", than);
    }

    [Fact]
    public void Chi_lay_tin_CUA_KHACH_lam_moc()
    {
        // Lấy tin cuối bất kỳ thì câu trả lời của chính nhân viên cũng thành mốc, và hội thoại
        // vừa được trả lời xong lại nhảy lên "chưa đọc".
        Assert.Contains("m.direction = 0", Than("MarkUnreadAsync"));
    }
}
