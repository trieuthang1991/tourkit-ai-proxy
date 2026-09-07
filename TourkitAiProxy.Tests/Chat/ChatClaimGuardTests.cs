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

    /// Cắt đúng thân handler <c>POST /assign</c> — soi cả file 2.600+ dòng thì khớp nhầm sang mã
    /// bên cạnh (chuỗi trùng ngẫu nhiên) là guard xanh giả mà không canh đúng chỗ.
    ///
    /// <para>⚠️ Dừng TRƯỚC <c>MapDelete .../assign</c> (nhả việc), không dừng ở
    /// <c>MapPatch .../status</c> như bản cũ — route DELETE đứng CHEN GIỮA hai route đó từ khi
    /// nhả việc tách khỏi thân POST, nên cửa sổ cũ ôm luôn cả handler DELETE dù chú thích chỉ
    /// tuyên bố cắt "thân handler /assign" (số ít, ý nói riêng nhánh POST).</para>
    private static string AssignHandler()
    {
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/assign\",", StringComparison.Ordinal);
        var j = src.IndexOf("MapDelete(\"/conversations/{id:long}/assign\"", StringComparison.Ordinal);
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
        //
        // Đường phân công bỏ hẳn assigned_username (đặc tả mục 4b) — khoá duy nhất còn lại là
        // assigned_user_id, nên đó là cột phải xuất hiện trong chính mệnh đề khoá này.
        Assert.Contains("assigned_user_id IS NULL", than);
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
    public void Nhan_viec_lay_ma_tu_PHIEN_chu_khong_tin_than_yeu_cau()
    {
        // Bản rất cũ giao diện gửi `window.tourkitAuth.session.username` — thuộc tính KHÔNG tồn
        // tại, nên thân yêu cầu luôn là chuỗi rỗng và nút "Nhận việc" thật ra đang GỠ giao việc.
        // Nay đường phân công bỏ hẳn username: nhánh nhận việc phải lấy MÃ từ phiên qua
        // EnsureCrmUserIdAsync, không tin bất cứ gì đọc được từ thân yêu cầu — để client không
        // tự khai mã người khác rồi "nhận việc" hộ họ.
        var than = AssignHandler();
        Assert.Contains("EnsureCrmUserIdAsync(a.SessionId, ct)", than);
        Assert.Matches(@"ClaimConversationAsync\(a\.TenantId, id, maToi\.Value, ct\)", than);
    }

    [Fact]
    public void Giao_dien_khong_con_doc_thuoc_tinh_session_khong_ton_tai()
    {
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.DoesNotContain("tourkitAuth?.session", jsx);
        Assert.DoesNotContain("tourkitAuth.session", jsx);
    }

    [Fact]
    public void Nhan_viec_ghi_DUNG_MOT_cot_khoa_duy_nhat()
    {
        // Đường phân công bỏ hẳn assigned_username (đặc tả mục 4b) — ClaimConversationAsync chỉ
        // còn ghi đúng MỘT cột. Ghi cả hai (bản trước) hoặc quên mất assigned_user_id đều sai:
        // ghi cả hai là còn hai nguồn sự thật — đúng gốc hai lỗi đã xảy ra.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,900})", RegexOptions.Singleline);
        Assert.Contains("assigned_user_id = @userId", m.Groups[1].Value);
        Assert.DoesNotContain("assigned_username", m.Groups[1].Value);
    }

    [Fact]
    public void ClaimConversationAsync_doi_nguoi_that_KHONG_con_nullable()
    {
        // Sau khi bỏ username, ClaimConversationAsync chỉ còn ĐÚNG MỘT tham số định danh — nó
        // phải là người THẬT, không phải "có thể trống": "nhận việc mà không biết ai nhận" vô
        // nghĩa, khác hẳn AssignAsync (vẫn phải nhận null, vì đó là cách NHẢ việc).
        //
        // Soi TỪNG hàm riêng (không soi cả file): nếu chỉ soi cả file, hàm này đổi kiểu sai vẫn
        // XANH nhờ AssignAsync còn giữ `int?` đâu đó trong file.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,150})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ClaimConversationAsync");
        Assert.Contains("int userId,", m.Groups[1].Value);
        Assert.DoesNotContain("int? userId", m.Groups[1].Value);
    }

    [Fact]
    public void Nhan_viec_kiem_ma_null_TRUOC_khi_ep_kieu_Value()
    {
        // EnsureCrmUserIdAsync trả int? — ép thẳng .Value mà không kiểm null trước là
        // InvalidOperationException ném thẳng ra ngoài (500) đúng lúc phiên không tra được mã
        // (JWT thiếu claim, upstream lỗi). Phải chặn và báo lỗi tử tế trước khi ép kiểu.
        var than = AssignHandler();
        Assert.Contains("if (maToi is null)", than);
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
    public void AssignReq_chi_mot_truong_KHONG_nullable()
    {
        // Cụm chat chưa vận hành nên không có "khoá vắng mặt" cần phân biệt với "khoá mang
        // null" — nhả việc đi hẳn đường DELETE riêng (xem guard bên dưới). AssignReq quay lại
        // kiểu có kiểu, một trường, không nullable: không cần đọc thân thô để phân biệt hai
        // trạng thái mà route đã tách bằng phương thức HTTP.
        var src = Endpoint();
        Assert.Contains("public record AssignReq(int UserId)", src);
        Assert.DoesNotContain("record AssignReq(int? UserId)", src);
    }

    [Fact]
    public void Assign_KHONG_doc_than_tho_qua_ReadFromJsonAsync()
    {
        // ReadFromJsonAsync kiểm Content-Type TRƯỚC khi đọc: header sai/thiếu (client gửi thân
        // nhưng không kèm đúng header) làm nó ném InvalidOperationException — KHÔNG phải
        // JsonException — nên bắt mỗi JsonException để lọt nguyên vẹn thành 500. Bản trước dùng
        // hàm này để đọc thân thô rồi tự tay bắt lỗi; bản này quay về model-binding kiểu có kiểu
        // của chính minimal API, nên endpoint không còn tự tay đọc/parse gì cả.
        //
        // Soi CẢ FILE, không kẹp trong thân handler. Lý lẽ "kẹp hẹp cho khỏi đỏ oan" nghe hợp lý
        // nhưng ĐO RA LÀ SAI theo hai hướng: (a) file này hiện có ĐÚNG 0 lượt dùng ReadFromJsonAsync
        // nên không có gì để đỏ oan — sáu lượt dùng hợp lệ nằm ở file khác mà chốt không đọc tới;
        // (b) cửa sổ hẹp luôn bị vô hiệu bằng cách DỜI MÃ RA NGOÀI: đặt một hàm gói ở cuối file rồi
        // gọi từ trong handler thì lỗ hổng quay lại nguyên vẹn mà 18/18 test vẫn xanh (đã đo).
        //
        // Luật chung: DoesNotContain + cửa sổ hẹp = chốt canh giả. Cấm thì cấm cả file.
        Assert.DoesNotContain("ReadFromJsonAsync", Endpoint());
    }

    [Fact]
    public void Nha_viec_di_duong_DELETE_rieng()
    {
        // Ba thao tác, ba đường: KHÔNG thân → nhận việc; {"userId":N} → chuyển việc; DELETE →
        // nhả việc. Tách nhả việc khỏi POST xoá luôn nhu cầu phân biệt "khoá vắng mặt" (nhận
        // việc) với "khoá mang giá trị null" (nhả việc) — sự nhập nhằng biến mất ở tầng thiết
        // kế, không cần đọc thân thô để phân xử. Cùng lối /follow (POST theo dõi, DELETE bỏ).
        Assert.Contains("MapDelete(\"/conversations/{id:long}/assign\"", Endpoint());
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
