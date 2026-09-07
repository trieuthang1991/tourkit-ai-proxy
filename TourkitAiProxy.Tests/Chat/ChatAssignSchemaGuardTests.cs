using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh schema phân công. Không có CI chạy PostgreSQL nên đây là lớp duy nhất: đọc thẳng
/// câu SQL trong ChatDb.cs.
/// </summary>
public class ChatAssignSchemaGuardTests
{
    private static string Sql() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");

    [Fact]
    public void Them_cot_nguoi_phu_trach_bang_ma_so()
    {
        // Cột MỚI assigned_user_id là khoá so quyền — chỉ việc của test này là xác nhận nó
        // được thêm. KHÔNG ghim chuỗi "assigned_username" vào đây nữa: cột đó đang bị khối
        // DO $$ ở dưới XOÁ DẦN khi hết dữ liệu (xem chú thích tại đó), tức là một cột SẮP CHẾT
        // — bắt nó phải xuất hiện mãi mãi trong schema là bắt test này đỏ oan đúng lúc dọn dẹp
        // xong và đoạn DROP COLUMN được gỡ khỏi migration.
        Assert.Contains("ADD COLUMN IF NOT EXISTS assigned_user_id integer", Sql());
    }

    [Fact]
    public void Doi_truc_la_MOT_COT_chu_khong_phai_bang_rieng()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_assign_settings", Sql());
        // Sau khi khoá định danh đổi sang mã người thì mỗi thành viên đội trực chỉ còn đúng
        // MỘT con số — tách bảng cho một cột số là thêm một lượt đọc mà không được gì.
        Assert.Contains("member_ids integer[]", Sql());
        Assert.DoesNotContain("chat_assign_members", Sql());
    }

    [Fact]
    public void Ma_nguoi_phu_trach_phai_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper đổi NULL thành 0, KHÔNG báo lỗi,
        // và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        var model = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs");
        Assert.Contains("int? AssignedUserId", model);
    }

    [Fact]
    public void Con_tro_xoay_vong_luu_MA_NGUOI_chu_khong_phai_vi_tri()
    {
        // Lưu số thứ tự thì thêm/bớt một người là cả vòng lệch — im lặng.
        Assert.Contains("rotation_last_user_id", Sql());
        Assert.DoesNotContain("rotation_index", Sql());
    }

    [Fact]
    public void Moi_lenh_schema_deu_idempotent()
    {
        // SchemaSql chạy MỖI LẦN khởi động. Một lệnh không IF NOT EXISTS là app chết ở lần
        // khởi động thứ hai.
        var sql = Sql();
        foreach (var manh in new[] { "chat_assign_settings" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {manh}", sql);
    }

    private static string Kho() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs");

    /// <summary>
    /// Thân hàm <c>GanXoayVongAsync</c>, cắt tới đúng dấu đóng thân hàm (thụt lề 4 dấu cách +
    /// <c>}</c>) — KHÔNG cắt theo một số ký tự cố định.
    ///
    /// <para>Cắt theo số ký tự là quả bom hẹn giờ: thêm vài dòng chú thích XML vào hàm là cửa sổ
    /// tràn ra ngoài câu SQL thật, và các test bên dưới bắt đầu báo ĐỎ GIẢ dù mã không hề sai.
    /// Cắt theo ranh giới cú pháp thì hàm dài thêm bao nhiêu cũng không ảnh hưởng.</para>
    /// </summary>
    private static string ThanGanXoayVong()
    {
        var kho = Kho();
        var batDau = kho.IndexOf("GanXoayVongAsync", StringComparison.Ordinal);
        Assert.True(batDau >= 0, "Không thấy GanXoayVongAsync");

        var m = Regex.Match(kho[batDau..], @"\A.*?\r?\n    \}", RegexOptions.Singleline);
        Assert.True(m.Success, "Không tìm được dấu đóng thân hàm GanXoayVongAsync");
        return m.Value;
    }

    [Fact]
    public void Xoay_vong_dung_DUY_NHAT_mot_luot_goi_CSDL()
    {
        // Tách hàm thành "đọc con trỏ" (ExecuteScalarAsync) rồi "ghi" (ExecuteAsync) ở một lượt
        // OpenAsync riêng là ĐÚNG lỗi Critical guard này sinh ra để chặn: hai tin tới cùng lúc
        // đọc cùng một con trỏ CŨ rồi cùng ghi đè lên nhau, khách nhận hai lời chào. Một lượt
        // OpenAsync = một round-trip CSDL = một câu lệnh nguyên tử.
        Assert.Single(Regex.Matches(ThanGanXoayVong(), "OpenAsync"));
    }

    [Fact]
    public void Xoay_vong_quay_con_tro_va_gan_trong_MOT_cau_lenh()
    {
        // Đọc rồi ghi thì hai tin tới cùng lúc sẽ gán hai người khác nhau, cái sau đè cái
        // trước, và khách nhận hai lời chào.
        var than = ThanGanXoayVong();

        // Bốn chuỗi dưới đây phải cùng nằm trong MỘT chuỗi SQL literal — không phải "xuất hiện
        // đâu đó trong thân hàm" (kể cả rải ra ở hai câu SQL riêng của hai lượt gọi CSDL khác
        // nhau, thứ mà bản test cũ không phân biệt được và vẫn XANH dù đã mất tính nguyên tử).
        var moDau = than.IndexOf("\"\"\"", StringComparison.Ordinal);
        Assert.True(moDau >= 0, "Không thấy câu SQL nào trong GanXoayVongAsync");
        var dongCua = than.IndexOf("\"\"\"", moDau + 3, StringComparison.Ordinal);
        Assert.True(dongCua > moDau, "Câu SQL không đóng đúng cách (thiếu dấu \"\"\" đóng)");
        var cauSql = than[(moDau + 3)..dongCua];

        Assert.Contains("WITH", cauSql);                          // CTE ghi dữ liệu
        Assert.Contains("UPDATE chat_assign_settings", cauSql);   // quay con trỏ
        Assert.Contains("UPDATE chat_conversations", cauSql);     // gán người
        // Điều kiện phải nằm TRONG câu UPDATE để CSDL quyết định người thắng.
        Assert.Contains("assigned_user_id IS NULL", cauSql);
    }

    [Fact]
    public void Xoay_vong_chi_chia_cho_doi_truc()
    {
        // Không đọc member_ids thì máy chia hội thoại cho CẢ công ty — khách hỏi tour rơi vào
        // kế toán, ngồi đó không ai trả lời.
        Assert.Contains("member_ids", ThanGanXoayVong());
    }

    [Fact]
    public void Xoay_vong_chay_TRUOC_moi_lenh_phat_su_kien()
    {
        // Gán xong mới bắn "tin mới" thì người vừa được giao mới thấy thông báo ngay. Đảo thứ
        // tự này là lỗi CÂM: build vẫn qua, mọi test khác vẫn xanh, và hộp thư của người vừa
        // được gán vẫn im lặng cho tới khi họ tự tải lại trang.
        var kho = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs");
        var m = Regex.Match(kho,
            @"private async Task OneEventAsync.*?(?=\r?\n    (?:private|public))",
            RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy thân hàm OneEventAsync");
        var than = m.Value;

        var iGan = than.IndexOf("GanXoayVongAsync", StringComparison.Ordinal);
        var iBan = than.IndexOf("_bus.Publish", StringComparison.Ordinal);
        // Bắt buộc kiểm >= 0 riêng: thiếu bước này thì lượt gọi xoay vòng bị xoá sạch làm
        // iGan = -1, và "-1 < iBan" vẫn đúng — test XANH GIẢ đúng lúc tính năng đã biến mất.
        Assert.True(iGan >= 0, "Không thấy lượt gọi xoay vòng trong OneEventAsync");
        Assert.True(iBan >= 0, "Không thấy lệnh phát sự kiện trong OneEventAsync");
        Assert.True(iGan < iBan,
            "Xoay vòng phải chạy TRƯỚC khi phát sự kiện — nếu không, người vừa được gán không " +
            "nhận được thông báo tin mới nào.");
    }

    private static string DuongDocCauHinh() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    /// <summary>
    /// Thân route GET ứng với tên đường dẫn (không dấu "/") trong <c>ChatInboxEndpoints.cs</c>,
    /// cắt tới đúng dấu đóng khối lambda (khớp thụt lề mở <c>g.MapGet(</c>) — KHÔNG cắt theo một
    /// số ký tự cố định. Cùng lối <c>ThanGanXoayVong</c> ở trên: hàm dài thêm bao nhiêu cũng
    /// không ảnh hưởng, không như cắt theo số ký tự cố định (thêm một dòng chú thích là cửa sổ
    /// tràn ra ngoài, guard báo đỏ giả dù mã không hề sai).
    /// </summary>
    private static string ThanHam(string ten)
    {
        var kho = DuongDocCauHinh();
        var neo = $"MapGet(\"/{ten}\"";
        var batDau = kho.IndexOf(neo, StringComparison.Ordinal);
        if (batDau < 0) return "";

        var m = Regex.Match(kho[batDau..], @"\A.*?\r?\n        \}\);", RegexOptions.Singleline);
        return m.Success ? m.Value : "";
    }

    [Fact]
    public void Danh_sach_nhan_vien_phai_di_qua_dem()
    {
        // Danh sách gần như không đổi mà bị gọi ở mỗi lần mở hộp thư — không đệm là bắt ERP
        // gánh một lượt gọi cho mỗi cú bấm.
        //
        // Đòi LƯỢT GỌI THẬT (có dấu chấm: redis.Get(/redis.Set(), không phải chữ "redis" trần —
        // chữ trần khớp luôn theo TÊN THAM SỐ "RedisStore redis" trong chữ ký hàm, nên ai xoá
        // sạch redis.Get/redis.Set mà quên xoá tham số thì bản test cũ vẫn xanh.
        var than = ThanHam("assign-settings");
        Assert.False(string.IsNullOrWhiteSpace(than), "Không cắt được thân đường đọc cấu hình");
        Assert.Contains("redis.Get(", than);
        Assert.Contains("redis.Set(", than);
        Assert.Contains("FromHours(2)", than);

        // Lệnh ghi đệm PHẢI nằm SAU điều kiện "danh sách khác rỗng" — đó là luật quan trọng
        // nhất của cụm này: đệm một danh sách RỖNG do ERP lỗi tạm thời là khoá cả công ty khỏi
        // màn hình cấu hình suốt 2 tiếng mà không có lỗi nào hiện ra. Bỏ điều kiện đó thì hai
        // chuỗi ở trên vẫn còn nguyên và bản test cũ (chỉ đòi Contains rời rạc) vẫn xanh — đây
        // là chỗ nó phải đỏ.
        var chiSoDieuKien = than.IndexOf("nhanVien.Count > 0)", StringComparison.Ordinal);
        var chiSoGhiDem = than.IndexOf("redis.Set(", StringComparison.Ordinal);
        Assert.True(chiSoDieuKien >= 0, "Không thấy điều kiện kiểm danh sách nhân viên khác rỗng");
        Assert.True(chiSoGhiDem > chiSoDieuKien,
            "redis.Set phải nằm SAU điều kiện \"nhanVien.Count > 0\" — đệm danh sách rỗng là bị cấm");
    }
}
