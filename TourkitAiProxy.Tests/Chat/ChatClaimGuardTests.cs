using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nhận việc phải NGUYÊN TỬ.
///
/// <para>Không có CI chạy PostgreSQL nên đây là lớp canh duy nhất: đọc chính câu SQL và chính mã
/// endpoint. Bản trước là <c>UPDATE … SET assigned_username = @u</c> trần — hai nhân viên bấm cách
/// nhau 100ms thì người sau <b>im lặng cướp việc</b> của người trước, cả hai đều thấy "của tôi" và
/// cùng trả lời một khách. Khách nhận hai câu trả lời khác nhau từ một công ty.</para>
/// </summary>
public class ChatClaimGuardTests
{
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    [Fact]
    public void Nhan_viec_kiem_ai_dang_giu_ngay_trong_WHERE()
    {
        var src = Repo();
        // Lấy một cửa sổ quanh ClaimConversationAsync thay vì soi cả file: "assigned_username IS NULL" có
        // thể nằm ở truy vấn khác (bộ lọc danh sách cũng dùng), khớp nhầm là guard xanh giả.
        var m = Regex.Match(src, "ClaimConversationAsync(.{0,800})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ClaimConversationAsync trong ChatRepository");

        var than = m.Groups[1].Value;
        Assert.Contains("UPDATE chat_conversations", than);
        // Kiểm-rồi-ghi trong C# vẫn lọt khi hai người bấm cùng lúc: giữa lần đọc và lần ghi có một
        // khe. Điều kiện phải nằm TRONG chính câu UPDATE để CSDL quyết định người thắng.
        Assert.Contains("assigned_username IS NULL", than);
    }

    [Fact]
    public void Nhan_viec_tra_so_dong_chu_khong_nuot()
    {
        // Trả void thì chỗ gọi không phân biệt được "nhận được" với "người khác nhận trước" —
        // và endpoint sẽ trả 200 cho cả hai người.
        Assert.Matches(@"Task<int>\s+ClaimConversationAsync", Repo());
    }

    [Fact]
    public void Nguoi_khac_dang_giu_thi_tra_409_chu_khong_phai_200()
    {
        // 200 im lặng là kiểu hỏng tệ nhất: giao diện người thua vẫn hiện "của tôi".
        var src = Endpoint();
        Assert.Contains("StatusCodes.Status409Conflict", src);
    }

    [Fact]
    public void Nhan_viec_lay_ten_tu_PHIEN_chu_khong_tin_than_yeu_cau()
    {
        // Bản trước giao diện gửi `window.tourkitAuth.session.username` — thuộc tính KHÔNG tồn tại,
        // nên thân yêu cầu luôn là chuỗi rỗng và nút "Nhận việc" thật ra đang GỠ giao việc. Nút
        // trông như chạy suốt nhiều tháng. Nay tên người nhận lấy từ phiên ở máy chủ.
        var src = Endpoint();
        Assert.DoesNotContain("body.Username ?? a.Username", src);
        Assert.Matches(@"ClaimConversationAsync\([^)]*a\.Username", src);
    }

    [Fact]
    public void Giao_dien_khong_con_doc_thuoc_tinh_session_khong_ton_tai()
    {
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.DoesNotContain("tourkitAuth?.session", jsx);
        Assert.DoesNotContain("tourkitAuth.session", jsx);
    }

    [Fact]
    public void Nhan_viec_ghi_CA_HAI_cot()
    {
        // Chỉ ghi tên đăng nhập thì luật xem (so theo assigned_user_id) không thấy hội thoại
        // vừa nhận — người nhận việc xong là mất luôn hội thoại khỏi màn hình.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,900})", RegexOptions.Singleline);
        Assert.Contains("assigned_username = @username", m.Groups[1].Value);
        Assert.Contains("assigned_user_id = @userId", m.Groups[1].Value);
    }

    [Fact]
    public void Tham_so_ma_nguoi_phai_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper/C# đổi NULL thành 0, KHÔNG báo
        // lỗi, và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        var repo = Repo();
        Assert.Contains("int? userId", repo);
        Assert.DoesNotContain("int userId,", repo);
    }

    [Fact]
    public void Giao_viec_kiem_nguoi_nhan_co_trong_doi_truc()
    {
        // Endpoint cũ nhận BẤT KỲ chuỗi tên đăng nhập nào: gõ sai một ký tự là hội thoại gán
        // vào hư không, không ai thấy nó nữa và không có lỗi nào hiện ra.
        Assert.Contains("MemberIds.Contains", Endpoint());
    }
}
