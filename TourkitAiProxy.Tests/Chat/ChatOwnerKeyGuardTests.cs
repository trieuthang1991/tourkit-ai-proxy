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
/// <para>⚠️ Từ 07/09/2026 <b>TOÀN cụm chat khoá theo MÃ NGƯỜI, không còn ngoại lệ</b> — ba bảng
/// theo dõi / dấu đã đọc / nhật ký thao tác cũng đã chuyển (chủ dự án chốt cùng ngày). Trước đó
/// chúng cố ý ở lại với tên đăng nhập vì "chỉ là dấu vết cá nhân"; lý lẽ đó chỉ đúng khi có dữ
/// liệu thật, mà cụm chat chưa vận hành nên không có dấu nào để mất. Một hệ, một loại khoá:
/// trộn hai loại chính là gốc của mọi nhập nhằng đã gặp trong đợt này (đặc tả mục 4b).</para>
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
    ///
    /// <para>⚠️ Neo vào CHỮ KÝ hàm (<c>Task</c>/<c>Task&lt;...&gt;</c> ngay trước tên), KHÔNG
    /// neo vào tên trần bằng <c>IndexOf(ten)</c>: tên trần khớp luôn cả một dòng
    /// <c>&lt;see cref="{ten}"/&gt;</c> nếu dòng đó đứng SỚM HƠN trong file so với định nghĩa
    /// thật — file này đã có kiểu tham chiếu chéo đó (xref tới <c>ClaimConversationAsync</c>
    /// trong doc-comment của <c>AssignAsync</c>), nên rủi ro không phải giả định suông. Cắt
    /// nhầm sang thân hàm khác thì <c>than</c> vẫn KHÔNG rỗng — chốt chống-xanh-giả không tự
    /// biết mình đã cắt sai.</para>
    /// </summary>
    private static string ThanHam(string ten)
    {
        var kho = Repo();
        // Hai nhánh riêng (có generic / không có generic) thay vì gộp bằng "(?:<.*?>)?" — nhóm
        // KHÔNG BẮT BUỘC bọc quanh lượng từ lười (?:<.*?>)? khớp SAI (trả false) trên .NET 10 dù
        // cùng nội dung, cùng mẫu vẫn khớp đúng trên .NET 8 — đã tự kiểm bằng một ứng dụng .NET
        // độc lập trước khi đổi, không đoán suông.
        var esc = Regex.Escape(ten);
        var m0 = Regex.Match(kho, @"Task<.*?>\s+" + esc + @"\(|Task\s+" + esc + @"\(");
        if (!m0.Success) return "";
        var batDau = m0.Index;

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

    [Fact]
    public void Toan_cum_chat_khoa_theo_MA_NGUOI_khong_con_ten_dang_nhap()
    {
        // Một hệ, một loại khoá. Trộn hai loại là gốc của mọi nhập nhằng đã gặp (đặc tả 4b).
        var sql = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        foreach (var bang in new[] { "chat_conversation_reads", "chat_conversation_follows",
                                     "chat_audit" })
        {
            var than = ThanBang(sql, bang);
            Assert.False(string.IsNullOrWhiteSpace(than), $"Không cắt được thân bảng {bang}");
            Assert.DoesNotContain("username", than);
            Assert.Contains("user_id", than);
        }

        // Chỉ mục nằm NGOÀI thân bảng nên vòng lặp trên không chạm tới. Bỏ sót nó thì chỉ mục
        // vẫn trỏ vào cột đã biến mất — schema dựng lỗi ngay lần khởi động đầu.
        Assert.Contains("ON chat_conversation_follows (tenant_id, user_id)", sql);

        // Và phần TRUY VẤN, không chỉ phần khai báo: đổi schema sang mã mà để truy vấn đọc tên là
        // hỏng lúc chạy chứ không hỏng lúc dựng. Chốt cũ chỉ soi ChatDb.cs nên vế này lọt.
        var repo = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        foreach (var cam in new[] { "f.username", "f2.username", "r.username",
                                    "conversation_id, username" })
            Assert.DoesNotContain(cam, repo);
    }

    /// <summary>
    /// Cắt từ <c>CREATE TABLE IF NOT EXISTS &lt;tên&gt; (</c> tới dấu <c>);</c> đầu tiên sau đó.
    ///
    /// <para>⚠️ Cắt theo RANH GIỚI CÚ PHÁP, không theo một số ký tự cố định. Bản đầu của chốt
    /// canh này cắt 600 ký tự: thân <c>chat_conversation_reads</c> chỉ khoảng 300, nên cửa sổ
    /// tràn sang khối chú thích của bảng kế tiếp — mà chú thích đó có nguyên chữ
    /// <c>username</c>. Chốt sẽ ĐỎ VĨNH VIỄN kể cả khi mã đã chuyển đúng hoàn toàn, và người sau
    /// sẽ đi sửa thứ đang đúng.</para>
    ///
    /// <para>⚠️ Dùng <c>IndexOf</c> chứ KHÔNG dùng regex: nhóm không bắt buộc bọc quanh lượng từ
    /// lười khớp SAI (lặng lẽ trả <c>false</c>) trên .NET 10 dù đúng trên .NET 8 — xem chú thích
    /// ở <see cref="ThanHam"/>. Cắt hụt cho ra chuỗi rỗng, nên chỗ gọi PHẢI khẳng định
    /// <c>than</c> khác rỗng trước khi khẳng định bất cứ điều gì về nội dung nó.</para>
    /// </summary>
    private static string ThanBang(string sql, string bang)
    {
        var i = sql.IndexOf($"CREATE TABLE IF NOT EXISTS {bang} (", StringComparison.Ordinal);
        if (i < 0) return "";
        var j = sql.IndexOf(");", i, StringComparison.Ordinal);
        return j < 0 ? "" : sql.Substring(i, j - i);
    }
}
