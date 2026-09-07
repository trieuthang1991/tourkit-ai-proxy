using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Một khoá duy nhất cho quyền sở hữu hội thoại: <c>assigned_user_id</c>.
/// <c>assigned_username</c> KHÔNG còn được đọc ở đường phân công — xem đặc tả mục 4b
/// ("Luật một khoá: quyết định bằng mã, hiển thị bằng tên").
///
/// <para>Hai cột cùng mang nghĩa "ai là chủ" sinh ra BA trạng thái dòng — <i>(tên có, mã
/// trống)</i> từ dữ liệu cũ, <i>(tên có, mã có)</i> từ tự nhận việc, <i>(tên trống, mã có)</i>
/// từ chuyển việc và xoay vòng. Mỗi câu truy vấn chỉ đọc MỘT cột sẽ đúng với hai trạng thái và
/// sai với trạng thái thứ ba, im lặng. Đã hỏng hai lần từ đúng gốc này:</para>
/// <list type="bullet">
/// <item>khoá chống tranh việc so theo tên → dòng <i>(tên trống, mã có)</i> làm mệnh đề luôn
/// đúng → người thứ hai bấm nhận việc <b>thắng, không có 409</b>, người đang giữ mất việc mà
/// không hay biết;</item>
/// <item>bộ lọc "chỉ của tôi" so theo tên → hội thoại do xoay vòng gán có tên trống nên lọt vào
/// bộ lọc của <b>mọi người</b>.</item>
/// </list>
///
/// <para>Cụm chat chưa vận hành (chủ dự án xác nhận 07/09/2026) nên không có dữ liệu cũ phải
/// lấp và không có client cũ phải chiều — bỏ hẳn cột tên khỏi đường phân công là hết đường sai,
/// không cần luật nào để canh nữa.</para>
///
/// <para>⚠️ Ba bảng khác (theo dõi, dấu đã đọc, nhật ký thao tác) VẪN khoá theo tên — đó là
/// CỐ Ý, không phải sót: chúng lưu dấu vết cá nhân/lịch sử, không phải quyết định quyền sở hữu,
/// nên không có nguy cơ "hai nguồn sự thật" như ở đây.</para>
/// </summary>
public class ChatOwnerKeyGuardTests
{
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    /// <summary>
    /// Thân MỘT hàm trong <c>ChatRepository.cs</c>, cắt tới đúng dấu đóng thân hàm (thụt lề 4
    /// dấu cách + <c>}</c>) — KHÔNG cắt theo một số ký tự cố định. Cùng lối
    /// <c>ThanGanXoayVong</c> ở <c>ChatAssignSchemaGuardTests.cs</c>: cắt theo ranh giới cú
    /// pháp thì hàm dài thêm bao nhiêu cũng không ảnh hưởng, không như cắt theo số ký tự cố
    /// định (thêm một dòng chú thích XML là cửa sổ tràn ra ngoài, guard báo đỏ giả dù mã không
    /// hề sai).
    /// </summary>
    private static string ThanHam(string ten)
    {
        var kho = Repo();
        var batDau = kho.IndexOf(ten, System.StringComparison.Ordinal);
        if (batDau < 0) return "";

        var m = Regex.Match(kho[batDau..], @"\A.*?\r?\n    \}", RegexOptions.Singleline);
        return m.Success ? m.Value : "";
    }

    [Fact]
    public void Duong_phan_cong_KHONG_con_dung_ten_dang_nhap()
    {
        // Hai cột cùng mang nghĩa "ai là chủ" sinh ra BA trạng thái dòng; mỗi truy vấn chỉ đọc
        // MỘT cột sẽ đúng với hai trạng thái và sai với cái thứ ba, im lặng. Đã hỏng hai lần:
        // khoá tranh việc mất 409, và bộ lọc "chỉ của tôi" lọt vào tay mọi người.
        //
        // Cụm chat chưa vận hành nên không phải giữ cột cũ cho tương thích — bỏ hẳn khỏi đường
        // phân công là hết đường sai.
        foreach (var ten in new[] { "ListConversationsAsync", "CountAsync",
                                    "ClaimConversationAsync", "AssignAsync", "AssigneeOfAsync" })
        {
            var than = ThanHam(ten);
            Assert.False(string.IsNullOrWhiteSpace(than), $"Không cắt được thân {ten} — regex đã lạc");
            Assert.DoesNotContain("assigned_username", than);
        }
    }
}
