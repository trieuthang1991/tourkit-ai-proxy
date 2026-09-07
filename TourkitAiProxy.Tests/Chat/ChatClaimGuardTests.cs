using System;
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

    /// Cắt đúng thân handler <c>/assign</c> — soi cả file 2.600 dòng thì khớp nhầm sang mã bên
    /// cạnh (chuỗi trùng ngẫu nhiên) là guard xanh giả mà không canh đúng chỗ.
    private static string AssignHandler()
    {
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/assign\",", StringComparison.Ordinal);
        var j = src.IndexOf("MapPatch(\"/conversations/{id:long}/status\"", StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy handler /assign trong ChatInboxEndpoints.cs");
        return src[i..j];
    }

    /// Cắt đúng thân handler <c>/send</c> (gửi tin chữ/đính kèm — KHÔNG phải <c>/send-template</c>).
    private static string SendHandler()
    {
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/send\",", StringComparison.Ordinal);
        var j = src.IndexOf("MapPost(\"/conversations/{id:long}/upload\"", StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy handler /send trong ChatInboxEndpoints.cs");
        return src[i..j];
    }

    [Fact]
    public void Nhan_viec_kiem_ai_dang_giu_ngay_trong_WHERE()
    {
        var src = Repo();
        // Lấy một cửa sổ quanh ClaimConversationAsync thay vì soi cả file: "assigned_username IS NULL" có
        // thể nằm ở truy vấn khác (bộ lọc danh sách cũng dùng), khớp nhầm là guard xanh giả.
        var m = Regex.Match(src, "ClaimConversationAsync(.{0,900})", RegexOptions.Singleline);
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
    public void ClaimConversationAsync_tham_so_ma_nguoi_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper/C# đổi NULL thành 0, KHÔNG báo
        // lỗi, và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        //
        // Soi TỪNG hàm riêng (không soi cả file): nếu chỉ soi cả file, hàm này tụt về `int` trần
        // vẫn XANH nhờ hàm kia còn `int?` đâu đó trong file.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,150})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ClaimConversationAsync");
        Assert.Contains("int? userId", m.Groups[1].Value);
        Assert.DoesNotContain("int userId,", m.Groups[1].Value);
    }

    [Fact]
    public void AssignAsync_tham_so_ma_nguoi_la_int_CO_THE_NULL()
    {
        // Cùng lý do với guard trên, nhưng canh RIÊNG cho AssignAsync — đường chuyển/nhả việc.
        // Đây là chỗ bản vá trước đó bỏ sót: guard cũ soi cả file nên AssignAsync có thể tụt về
        // `int` trần mà vẫn xanh nhờ ClaimConversationAsync còn giữ `int?`.
        var m = Regex.Match(Repo(), @"Task AssignAsync\(.{0,150}", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy AssignAsync");
        Assert.Contains("int? userId", m.Value);
        Assert.DoesNotContain("int userId,", m.Value);
    }

    [Fact]
    public void Giao_viec_kiem_nguoi_nhan_co_trong_doi_truc()
    {
        // Endpoint cũ nhận BẤT KỲ chuỗi tên đăng nhập nào: gõ sai một ký tự là hội thoại gán
        // vào hư không, không ai thấy nó nữa và không có lỗi nào hiện ra.
        //
        // Kẹp trong CHÍNH thân handler /assign (không phải "tồn tại đâu đó trong file 2.600
        // dòng") và buộc có đường trả 400 đi kèm — thiếu 400 thì lỗi "không trong đội trực" rơi
        // vào im lặng giống hệt lỗi nó được viết ra để chặn.
        var than = AssignHandler();
        Assert.Contains("MemberIds.Contains", than);
        Assert.Contains("StatusCodes.Status400BadRequest", than);
    }

    [Fact]
    public void Doi_truc_chua_cau_hinh_thi_bao_ro_khong_do_loi_nham_nguoi_gui()
    {
        // Công ty chưa từng cấu hình đội trực (chua_co_dong / MemberIds rỗng) khác hẳn "gõ sai
        // mã người" — nói "người này không có trong đội trực" khi đội trực còn TRỐNG là đổ lỗi
        // nhầm chỗ, người dùng cần biết phải đi CẤU HÌNH trước, không phải đi tìm mã đúng.
        var than = AssignHandler();
        Assert.Contains("chưa được cấu hình", than);
    }

    [Fact]
    public void Gan_viec_re_nhanh_theo_su_hien_dien_cua_khoa_userId()
    {
        // Rẽ theo "có thân hay không" (`body is null`) lộ một lỗ hổng tương thích ngược: client
        // CŨ gửi thân KHÔNG có khoá "userId" (`{}` hoặc `{"username":""}`) — cả hai đều "có
        // thân", nên rẽ theo body-null sẽ ép chúng vào nhánh "có thân" (chuyển/nhả việc) thay vì
        // "nhận việc" như hành vi gốc — một tab đang mở JS cũ sau khi triển khai bấm "Nhận việc"
        // sẽ âm thầm NHẢ việc.
        //
        // Phải rẽ theo SỰ HIỆN DIỆN của khoá "userId" (TryGetProperty), không theo "có thân hay
        // không" và không theo giá trị bên trong khoá.
        var than = AssignHandler();
        Assert.Contains("TryGetProperty(\"userId\"", than);
        Assert.DoesNotContain("if (body is null)", than);
        Assert.DoesNotContain("body?.UserId is null", than);
        Assert.DoesNotContain("body?.Username is null", than);
    }

    [Fact]
    public void Khoa_chong_hai_nguoi_cung_nhan_xet_ca_ma_nguoi()
    {
        // Sau Task 6, chủ sở hữu THẬT là assigned_user_id (luật xem so theo mã). Nhưng đường
        // chuyển việc luôn truyền username=null, và đường xoay vòng cũng vậy — nếu khoá chống
        // nhận-hai-lần chỉ so username thì mọi dòng "có mã, không tên" làm mệnh đề đó LUÔN ĐÚNG:
        // người thứ hai bấm "Nhận việc" thắng, nhận 200 (không 409), người đang giữ mất việc
        // trong im lặng — đúng kịch bản hàm này được viết ra để chặn.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,900})", RegexOptions.Singleline);
        Assert.True(m.Success);
        Assert.Contains("assigned_user_id IS NULL OR assigned_user_id = @userId", m.Groups[1].Value);
    }

    [Fact]
    public void Nhat_ky_chuyen_viec_dung_JsonObject_khong_noi_suy_chuoi()
    {
        // Nhật ký ở khắp file này đều dựng bằng JsonObject (an toàn escape, nhất quán kiểu dữ
        // liệu) — nội suy chuỗi thủ công cho MỘT chỗ là vỡ JSON nếu `ma` đổi kiểu hoặc có ký tự
        // đặc biệt, và không nhất quán với bản ghi cũ.
        var than = AssignHandler();
        Assert.Contains("new JsonObject { [\"cho\"] = ma }", than);
        Assert.DoesNotContain("$\"{{\\\"cho\\\":{ma}}}\"", than);
    }

    [Fact]
    public void Doc_lai_giu_chu_thich_VI_SAO_khong_chi_la_chi_dan_quy_trinh()
    {
        // Task 4 để lại chú thích GIẢI THÍCH (vì sao đọc lại bằng NguoiXem.HeThong, null nghĩa
        // là gì) — không phải chỉ dòng "GIỮ NGUYÊN, đừng thay". Chỉ dẫn quy trình không ngăn
        // được ai sửa sai; câu vì sao mới ngăn. Cả hai nhánh (nhận việc / chuyển-nhả việc) đều
        // đọc lại qua NguoiXem.HeThong nên chỉ cần một chú thích dùng chung không bị mất.
        var than = AssignHandler();
        Assert.Contains("có thể không còn thấy hội thoại này bằng phạm vi cũ", than);
        Assert.Contains("đọc lại HỤT", than);
    }

    [Fact]
    public void Tu_nhan_khi_tra_loi_chay_TRUOC_khi_phat_tin_moi()
    {
        // Task 4 dựng luật "phát giá trị SAU khi đổi, không phải giá trị đọc lúc đầu handler"
        // chính vì lỗi này: phát "tin-moi" trước rồi mới đổi người phụ trách thì sự kiện mang
        // AssignedUserId CŨ — không client nào biết ai vừa nhận cho tới lần tải lại trang.
        var than = SendHandler();
        var iClaim = than.IndexOf("ClaimConversationAsync", StringComparison.Ordinal);
        // Neo vào chính câu lệnh bus.Publish (không chỉ chuỗi "tin-moi") — chuỗi đó có thể xuất
        // hiện cả trong LỜI GIẢI THÍCH phía trên (comment nhắc tên sự kiện), khớp nhầm vào đó thì
        // guard báo đỏ giả dù mã đã đúng thứ tự.
        var iPublish = than.IndexOf("bus.Publish(new(a.TenantId, id, \"tin-moi\"", StringComparison.Ordinal);
        Assert.True(iClaim > 0, "Không thấy tự nhận khi trả lời trong handler /send");
        Assert.True(iPublish > 0, "Không thấy phát sự kiện tin-moi trong handler /send");
        Assert.True(iClaim < iPublish,
            "Tự nhận khi trả lời phải chạy TRƯỚC khi phát \"tin-moi\", không thì sự kiện mang AssignedUserId cũ");
    }
}
