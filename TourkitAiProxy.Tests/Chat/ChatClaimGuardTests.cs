using System;
using System.Linq;
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

    /// Cắt đúng thân handler <c>POST /assign</c> — CHUYỂN VIỆC cho người khác. Soi cả file
    /// 2.600+ dòng thì khớp nhầm sang mã bên cạnh (chuỗi trùng ngẫu nhiên) là guard xanh giả
    /// mà không canh đúng chỗ.
    ///
    /// <para>⚠️ Ba route nằm liền nhau và cửa sổ phải bám ĐÚNG một cái. Thứ tự trong file:
    /// <c>POST .../assign</c> (chuyển việc) → <c>POST .../assign/me</c> (nhận việc) →
    /// <c>MapDelete .../assign</c> (nhả việc). Bản trước dừng ở MapDelete vì lúc đó giữa hai
    /// route không có gì; từ 08/09/2026 nhận việc chen vào giữa, nên cửa sổ phải dừng sớm hơn —
    /// không thì mọi khẳng định về "chuyển việc" cũng xanh nhờ mã của "nhận việc" nằm cùng cửa sổ.</para>
    private static string AssignHandler()
    {
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/assign\",", StringComparison.Ordinal);
        var j = src.IndexOf("MapPost(\"/conversations/{id:long}/assign/me\"", StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy handler /assign trong ChatInboxEndpoints.cs");
        return src[i..j];
    }

    /// Cắt đúng thân handler <c>POST /assign/me</c> — NHẬN VIỆC cho chính mình.
    private static string ClaimHandler()
    {
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/assign/me\"", StringComparison.Ordinal);
        var j = src.IndexOf("MapDelete(\"/conversations/{id:long}/assign\"", StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "Không thấy handler /assign/me trong ChatInboxEndpoints.cs");
        return src[i..j];
    }

    /// Bỏ mọi dòng chú thích <c>//</c> trước khi soi. Chốt canh nào cấm một DẠNG MÃ đều phải đi
    /// qua đây: chú thích giải thích chính cái dạng bị cấm là chuyện thường (và nên có), để nó
    /// làm chốt đỏ thì người ta gỡ chú thích chứ không gỡ lỗi. Đã xảy ra một lần trên nhánh này
    /// theo chiều ngược lại — chốt Shape ĐẾM cả mã đã chú thích nên xanh giả.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
        var than = ClaimHandler();
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
        var than = ClaimHandler();
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

        // Mã người nhận phải là người THẬT. 0 là giá trị mặc định của int nên thân {} hay
        // {"userId":null} đều rơi về 0 mà không lỗi gì — rồi gán hội thoại cho "người số 0".
        Assert.Contains("if (ma <= 0)", than);
    }

    [Fact]
    public void Doi_truc_chi_rang_buoc_nguoi_KHONG_phai_quan_tri()
    {
        // Chốt 08/09/2026, sau khi đo trên staging: ở CHẾ ĐỘ THỦ CÔNG — chế độ MẶC ĐỊNH — đội
        // trực thường để trống (nó vốn sinh ra cho xoay vòng), nên áp luật đội trực cho cả admin
        // làm admin KHÔNG giao được việc cho ai: bấm giao, nhận 400 "chưa cấu hình đội trực".
        // Mà đặc tả mục 7.1 nói đúng chiều ngược lại — đường giao việc ở chế độ thủ công CHÍNH LÀ
        // "admin giao xuống"; và mục 9 phát biểu luật này riêng cho người KHÔNG phải admin.
        //
        // Chốt bám vào CẤU TRÚC (hai lần kiểm nằm TRONG nhánh không-phải-quản-trị), không bám
        // vào lời văn câu lỗi: đổi câu chữ mà bỏ cửa là lỗ hổng, đổi câu chữ mà giữ cửa thì
        // không phải lỗi — chốt cũ ở nhánh này từng bám lời văn và bị vô hiệu đúng kiểu đó.
        // ⚠️ CỬA NAY CÓ HAI CÁNH (chốt 08/09/2026, lần thứ hai trong ngày): ngoài "không phải
        // quản trị", còn phải "đang ở chế độ XOAY VÒNG". Đội trực là vòng quay chia việc — nó
        // trả lời câu "tới lượt ai". Chế độ thủ công không có lượt nào, nên đem vòng quay đi
        // chặn việc giao tay là mượn luật của việc này áp cho việc khác.
        //
        // Vế chế độ còn là ĐIỀU KIỆN để màn hình cấu hình giấu hẳn khối đội trực khi đang thủ
        // công. Bỏ vế này mà vẫn giấu giao diện thì nhân viên thường bị chặn bởi một danh sách
        // không ai còn thấy, và câu lỗi chỉ họ tới một khối không còn tồn tại.
        var than = AssignHandler();
        var i = than.IndexOf("if (!SessionAuth.LaQuanTriChat(a)", StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy cửa quản trị ở đường chuyển việc — đội trực đang ràng buộc cả admin?");

        var trongNhanh = than[i..];
        // Cả hai cánh cửa phải nằm trên CÙNG một điều kiện — tách ra hai lần if lồng nhau vẫn
        // được, nhưng vế chế độ phải xuất hiện trước khi chạm tới MemberIds.
        var j = trongNhanh.IndexOf("MemberIds", StringComparison.Ordinal);
        Assert.True(j > 0, "Không thấy bản kiểm đội trực trong nhánh");
        Assert.Contains("CheDoPhanCong.XoayVong", trongNhanh[..j]);

        Assert.Contains("MemberIds.Length == 0", trongNhanh);
        Assert.Contains("MemberIds.Contains", trongNhanh);

        // Và KHÔNG được có bản kiểm đội trực nào NGOÀI nhánh đó — dời một trong hai lên trên
        // cửa là luật quay lại ràng buộc admin, trong khi hai khẳng định trên vẫn xanh.
        var truocNhanh = than[..i];
        Assert.DoesNotContain("MemberIds", truocNhanh);
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
    public void Nhan_viec_KHONG_duoc_co_tham_so_than()
    {
        // ⚠️ ĐÂY LÀ LỖI ĐÃ XẢY RA THẬT, và 1219 test đã bỏ lọt nó (08/09/2026, đo bằng trình
        // duyệt trên staging). Nút "Nhận chăm sóc" bấm không có gì xảy ra suốt cả nhánh.
        //
        // Gốc: tham số thân của minimal API — KỂ CẢ khai có dấu hỏi — vẫn gắn
        // AcceptsMetadata("application/json") vào route, và AcceptsMatcherPolicy LOẠI route khỏi
        // danh sách ứng viên khi request không mang Content-Type. Request rơi xuống MapFallback
        // (trang SPA) → 404 kèm HTML. Đo được: thiếu header → 404; có header + thân rỗng → 200.
        //
        // Task 8 sửa đúng phía client (gửi thân RỖNG HẲN) và chốt canh khoá đúng phía client —
        // không ai kiểm phía MÁY CHỦ có nhận nổi request đó không. Đó là giới hạn thật của chốt
        // canh văn bản nguồn, không phải sơ suất của một người.
        //
        // Luật khoá lại được bằng văn bản nguồn là luật CẤU TRÚC: route mà giao diện gọi KHÔNG
        // kèm Content-Type thì KHÔNG được có tham số thân. Không luật nào bắt được "client nhớ
        // đặt header" — nên đừng đặt cược vào đó.
        var src = Endpoint();
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/assign/me\"", StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy route POST /assign/me — nhận việc phải có đường RIÊNG");
        var j = src.IndexOf(") =>", i, StringComparison.Ordinal);
        Assert.True(j > i, "Không cắt được chữ ký handler /assign/me");
        var chuKy = src[i..j];
        Assert.DoesNotMatch(@"[A-Za-z]+Req\??\s+\w+", chuKy);
    }

    [Fact]
    public void Khong_route_chat_nao_khai_than_TUY_CHON()
    {
        // "Thân tuỳ chọn" là ảo tưởng: dấu hỏi chỉ nới lỏng lúc GÁN GIÁ TRỊ, không nới lỏng lúc
        // CHỌN ROUTE. Route vẫn đòi Content-Type, nên client bỏ thân thật thì không tới được
        // handler mà rơi xuống trang SPA — hỏng câm, đúng như nút "Nhận chăm sóc" đã hỏng.
        //
        // Thân bắt buộc thì thiếu/sai header ra 400/415 — nói ra chứ không câm. Muốn "có thể
        // không có gì để gửi" thì tách route riêng không tham số thân (nhận việc, nhả việc),
        // hoặc gửi {} với đủ header (gỡ nối CRM).
        //
        // Soi CẢ FILE sau khi BỎ CHÚ THÍCH: cửa sổ hẹp bị vô hiệu bằng cách dời mã ra ngoài (đã
        // đo hai lần trên nhánh này), còn không bỏ chú thích thì chính đoạn giải thích lỗi này
        // làm chốt đỏ — và người ta sẽ gỡ chú thích chứ không gỡ lỗi.
        Assert.DoesNotMatch(@"[A-Za-z]+Req\?\s+\w+", BoChuThich(Endpoint()));
    }

    [Fact]
    public void Giao_dien_nhan_viec_goi_dung_duong_rieng()
    {
        // Nửa còn lại của cùng một luật: máy chủ có route không thân mà giao diện vẫn gọi
        // /assign trần thì lỗi 404 câm quay lại nguyên vẹn.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("/assign/me'", jsx);
        var m = Regex.Match(jsx, @"async function nhanViec\(\)(.{0,700})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy hàm nhanViec");
        Assert.Contains("/assign/me'", m.Groups[1].Value);
    }

    [Fact]
    public void O_chon_nguoi_phu_trach_do_theo_VAI_chu_khong_luon_theo_doi_truc()
    {
        // ⚠️ NỬA CÒN LẠI của luật "đội trực chỉ ràng buộc người KHÔNG phải quản trị". Sáng
        // 08/09/2026 đã sửa MÁY CHỦ cho quản trị giao việc được khi đội trực rỗng — nhưng giao
        // diện vẫn lọc ô chọn theo đội trực, nên đội trực rỗng là KHÔNG có ô nào để bấm. Máy chủ
        // cho phép mà màn hình không mở đường thì với người dùng là chưa sửa gì.
        //
        // Đội trực sinh ra cho chế độ XOAY VÒNG; ở chế độ THỦ CÔNG — chế độ mặc định — nó thường
        // rỗng. Tức là cấu hình mà phần lớn công ty đang chạy chính là cấu hình bị hỏng.
        //
        // Neo vào BA vế, vì bỏ vế nào cũng làm ô chọn sai mà vế kia vẫn xanh:
        //   (a) danh sách đổ vào ô phân theo VAI (quản trị: toàn bộ nhân viên; còn lại: đội trực);
        //   (b) danh sách đó ĐI ĐƯỢC tới khối "Phụ trách" trong hồ sơ khách;
        //   (c) ô chọn người thật sự ăn danh sách đó, không tự lọc lại theo đội trực.
        //
        // ⚠️ Bản đầu của chốt này neo vào NGUYÊN VĂN `<select className="ci-chon-phutrach">`.
        // Ngày 08/09/2026 ô chọn đổi thành component có tìm kiếm (108 nhân viên, hộp thả xuống
        // của trình duyệt không tìm được) và chốt đỏ oan — trong khi LUẬT nó canh thì không hề
        // đổi. Nay chốt neo vào luồng dữ liệu, không vào tên thẻ: đổi cách vẽ bao nhiêu lần cũng
        // được, miễn danh sách vẫn phân theo vai.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");

        // Điều kiện có HAI vế từ 08/09/2026: quản trị thấy mọi người, VÀ ở chế độ thủ công thì
        // ai cũng thấy mọi người (đội trực chỉ kẹp ở xoay vòng). Khớp đúng luật máy chủ — hai
        // bên lệch nhau thì ô chọn bày ra người mà bấm vào sẽ nhận 400.
        var m = Regex.Match(jsx,
            @"const (\w+) = \(phanCong\.isAdmin \|\| phanCong\.mode !== 2\)\s*\?\s*\(phanCong\.staffs \|\| \[\]\)\s*:\s*doiTruc;");
        Assert.True(m.Success,
            "Không thấy danh sách người phụ trách phân theo vai + chế độ — ô chọn đang lọc theo đội trực cho MỌI người?");
        var bien = m.Groups[1].Value;

        // (b) truyền xuống hồ sơ khách — nơi khối "Phụ trách" sống.
        Assert.Matches(@"chonDuoc=\{" + Regex.Escape(bien) + @"\}", jsx);

        // (c) ô chọn ăn đúng danh sách ấy. Đây là vế hay bị gỡ nhất khi ai đó "dọn" component.
        Assert.Matches(@"<window\.ChonNguoi[^>]*danhSach=\{chonDuoc\}", jsx);

        // Và không được quay lại lọc theo đội trực cho mọi người — lỗi gốc 08/09/2026.
        Assert.DoesNotContain("{doiTruc.length > 0 && (", jsx);
        Assert.DoesNotContain("danhSach={doiTruc}", jsx);
    }

    [Fact]
    public void O_chon_nguoi_trong_phai_noi_DUNG_nguyen_nhan()
    {
        // HAI NGUYÊN NHÂN, MỘT TRIỆU CHỨNG. Ô chọn người phụ trách trống vì:
        //   (a) CRM không trả nhân viên nào — lỗi môi trường, thêm người vào đội trực vô ích;
        //   (b) đội trực chat còn rỗng — lỗi cấu hình, quản trị vào Phân công thêm người là xong.
        // Hai cách sửa khác hẳn nhau, nên nói nhầm là đẩy người dùng đi sai đường.
        //
        // Máy chủ đã tách hai ca này thành hai câu cảnh báo riêng trong log từ 08/09/2026, nhưng
        // giao diện vẫn đổ chung một câu suốt. Đo trên staging sáng 09/09/2026 gặp đúng ca (a):
        // CRM trả 0 nhân viên (statuses=10, sources=4 vẫn về bình thường ⇒ không phải lỗi đọc dữ
        // liệu), mà màn hình lại giục quản trị đi thêm người vào đội trực.
        var jsx = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));

        // Nhánh phân xử phải TỒN TẠI và đứng trước câu nói về đội trực.
        var i = jsx.IndexOf("(phanCong.staffs || []).length === 0", StringComparison.Ordinal);
        Assert.True(i > 0,
            "Không thấy nhánh phân biệt 'CRM không trả nhân viên' với 'đội trực rỗng' — " +
            "ô chọn trống đang đổ chung một nguyên nhân cho hai ca khác nhau.");

        // Và cả hai câu phải có mặt. Thiếu câu nào là ca đó lại bị nói nhầm thành ca kia.
        Assert.Contains("Không lấy được danh sách nhân viên từ CRM", jsx);
        Assert.Contains("Đội trực chat còn trống", jsx);
    }

    [Fact]
    public void Ghi_chu_noi_bo_dung_DUNG_khoa_body_o_ca_hai_chieu()
    {
        // ⚠️ LỖI THẬT, sống âm thầm tới 08/09/2026. Máy chủ nhận `NoteReq(string? Body)` và trả
        // về cũng bằng khoá `body`; giao diện thì gửi `{noiDung: …}` và vẽ `{g.noiDung}`. Sai cả
        // hai chiều nên LƯU GHI CHÚ CHƯA BAO GIỜ CHẠY — mọi lượt gửi nhận 400 "Chưa nhập nội
        // dung ghi chú", và dù có lưu được thì ô nội dung cũng vẽ ra rỗng.
        //
        // Không bộ test nào bắt được: bộ C# đọc mã nguồn hai phía riêng rẽ (mỗi phía tự nó đều
        // "đúng"), còn bộ E2E không bấm tới nút này. Cùng hình dạng với lỗi nút "Nhận chăm sóc"
        // mà chốt ngay bên trên canh — hai nửa của một hợp đồng, mỗi nửa hợp lý một mình.
        // Qua BoChuThich: chính chốt này có một chú thích NHẮC TỚI khoá sai để giải thích lỗi cũ,
        // và không lọc thì nó tự bắt chú thích của mình rồi đỏ (đã xảy ra ngay lúc viết chốt).
        var jsx = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));

        var m = Regex.Match(jsx, @"async function themGhiChu\(e\)([\s\S]{0,1400}?)\n    \}");
        Assert.True(m.Success, "Không thấy hàm themGhiChu");
        var than = m.Groups[1].Value;

        Assert.Contains("/notes", than);
        Assert.Matches(@"JSON\.stringify\(\{\s*body:", than);
        Assert.DoesNotMatch(@"JSON\.stringify\(\{\s*noiDung:", than);

        // Và chiều ĐỌC. Sửa mỗi chiều gửi thì ghi chú lưu được nhưng vẫn hiện ra trống.
        Assert.Contains("{g.body}", jsx);
        Assert.DoesNotContain("g.noiDung", jsx);

        // TẦNG THỨ BA của cùng một lỗi. Cột trong CSDL tên `noi_dung`, thuộc tính model tên
        // `Body` — hai TỪ khác nhau nên luật nối tên-có-gạch-dưới của Dapper không bắc cầu được.
        // Thiếu bí danh thì truy vấn vẫn chạy, vẫn trả đúng số dòng, đúng người, đúng giờ, chỉ
        // riêng nội dung là rỗng. Sửa hai tầng giao diện mà bỏ tầng này thì màn hình vẫn trắng.
        var kho = BoChuThich(ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));
        Assert.Matches(@"noi_dung\s+AS\s+""Body""", kho);
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
        var than = ClaimHandler();
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
