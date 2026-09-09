using System.Linq;
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
    public void Cau_hinh_phan_cong_phai_nho_tam_VA_don_ngay_khi_luu()
    {
        // Từ khi có luật xem, MỌI request hộp thư chat đọc thêm một dòng cấu hình (trước 0, nay 27
        // endpoint) — dòng gần như không bao giờ đổi. Nhớ tạm 60 giây, cùng con số với cấu hình
        // trợ lý chat để hai thứ cùng cụm không có hai cảm giác "bao lâu thấy hiệu lực".
        //
        // BA vế, thiếu vế nào cũng hỏng theo kiểu IM LẶNG:
        //   (a) có nhớ tạm — thiếu thì chỉ là chậm, không ai thấy;
        //   (b) NHỚ CẢ null — "chưa cấu hình" là trạng thái thường gặp NHẤT, không nhớ null thì
        //       đúng nhóm đông nhất vẫn đánh truy vấn mỗi request, tức làm cache cho phần thiểu số;
        //   (c) lượt Lưu DỌN NGAY — chờ hết hạn mới thấy hiệu lực thì người vừa bấm Lưu tưởng nút
        //       hỏng rồi bấm thêm mấy lần. Lỗi này đã xảy ra một lần ở cụm này rồi.
        // ⚠️ HAI CÁI BẪY, cả hai đã bắt được bản ĐẦU của chính chốt này (08/09/2026):
        //   • BỎ CHÚ THÍCH TRƯỚC KHI SOI. Bản đầu chỉ Contains("_cache.Remove(tenant)"), nên chú
        //     thích lại đúng dòng đó là lượt Lưu thôi dọn mà chốt VẪN XANH. Đã đo.
        //   • BÁM HÌNH DẠNG CÂU LỆNH, không bám chuỗi con. Bản đầu chỉ Contains("_cache.TryGetValue"),
        //     nên thêm "false && " ngay trước là bộ nhớ tạm không bao giờ được đọc mà chốt VẪN
        //     XANH. Đã đo. Nay đòi nguyên hình dạng của câu lệnh, chèn gì vào trước điều kiện
        //     cũng làm lệch hình dạng.
        //
        // Vế HÀNH VI thật của luật này nằm ở bài E2E D2 (lưu rồi đọc lại phải ra thứ vừa lưu):
        // không dọn bộ nhớ tạm thì lượt đọc trả giá trị CŨ và D2 đỏ. Chốt văn bản nguồn ở đây chỉ
        // giữ cho cấu trúc khỏi mục — nó KHÔNG thay được bài chạy thật.
        var kho = BoChuThich(Kho());
        Assert.Contains("private readonly Dictionary<string, (ChatAssignSettings? Val, DateTime HetHan)> _cache", kho);

        var doc = Regex.Match(kho, @"public async Task<ChatAssignSettings\?> LayCauHinhAsync(.{0,700})",
            RegexOptions.Singleline);
        Assert.True(doc.Success, "Không thấy LayCauHinhAsync");
        Assert.Matches(
            @"if \(_cache\.TryGetValue\(tenant, out var (\w+)\) && \1\.HetHan > DateTime\.UtcNow\)\s*return \1\.Val;",
            doc.Groups[1].Value);
        Assert.Matches(@"_cache\[tenant\]\s*=\s*\(\w+, DateTime\.UtcNow \+ NhoTam\)", doc.Groups[1].Value);

        // (c) neo vào CHÍNH thân LuuCauHinhAsync, không phải "đâu đó trong file": Remove nằm ở hàm
        // khác thì lượt Lưu vẫn để lại giá trị cũ mà hai vế trên vẫn xanh.
        var luu = Regex.Match(kho, @"public async Task LuuCauHinhAsync(.{0,1800})", RegexOptions.Singleline);
        Assert.True(luu.Success, "Không thấy LuuCauHinhAsync");
        Assert.Matches(@"lock \(_khoa\) _cache\.Remove\(tenant\);", luu.Groups[1].Value);
    }

    /// Bỏ mọi dòng chú thích trước khi soi. Chốt canh nào đòi hoặc cấm một DẠNG MÃ đều phải đi qua
    /// đây: chú thích nhắc tới chính dạng đó là chuyện thường, mà để nó tính là mã thì chú thích
    /// lại một dòng là vô hiệu được cả chốt. Đã trả giá đúng kiểu này hai lần trong một ngày.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void Con_tro_vong_quay_KHONG_duoc_doc_qua_doi_tuong_cau_hinh()
    {
        // Câu lệnh xoay vòng cũng GHI vào chat_assign_settings (đẩy con trỏ) mà KHÔNG dọn bộ nhớ
        // tạm — cố ý, vì nó chỉ đổi rotation_last_user_id và vòng quay đọc con trỏ ngay TRONG câu
        // lệnh đó. Chốt này khoá chính giả định ấy lại: ngày nào có người đọc con trỏ từ đối tượng
        // cấu hình (tức từ bộ nhớ tạm), họ sẽ nhận giá trị cũ tới 60 giây và vòng quay gán trùng
        // người — im lặng, không lỗi nào hiện ra. Lúc đó bài này đỏ và nói rõ phải làm gì.
        //
        // Cho phép ĐÚNG ba chỗ: khai báo trong bản ghi miền, thuộc tính của dòng thô, và câu dựng
        // bản ghi. Mọi lượt dùng khác đều phải đỏ.
        var choPhep = new[]
        {
            "TourkitAiProxy.Domain/Chat/ChatAssign.cs",
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs",
        };
        var pham = new List<string>();
        foreach (var f in Directory.EnumerateFiles(GocRepo(), "*.cs", SearchOption.AllDirectories))
        {
            var duong = Path.GetRelativePath(GocRepo(), f).Replace(Path.DirectorySeparatorChar, '/');
            if (duong.Contains("/bin/") || duong.Contains("/obj/")) continue;
            if (duong.StartsWith("TourkitAiProxy.Tests/")) continue;
            if (choPhep.Contains(duong)) continue;
            if (File.ReadAllText(f).Contains("RotationLastUserId")) pham.Add(duong);
        }
        Assert.True(pham.Count == 0,
            "RotationLastUserId đang bị đọc ngoài kho phân công:\n  " + string.Join("\n  ", pham)
            + "\n\nCon trỏ vòng quay đi qua bộ nhớ tạm nên có thể cũ tới 60 giây. Muốn dùng nó thì "
            + "phải đọc thẳng CSDL, hoặc cho lượt ghi con trỏ dọn bộ nhớ tạm.");
    }

    private static string GocRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    [Fact]
    public void Cau_hinh_phan_cong_KHONG_duoc_doc_thang_vao_record_vi_tri()
    {
        // ⚠️ LỖI ĐÃ XẢY RA THẬT (08/09/2026, đo trên staging) và nó tắt CẢ hộp thư chat của
        // công ty: Dapper dựng record vị trí bằng cách so KIỂU tham số hàm dựng với kiểu cột do
        // trình đọc khai. Npgsql khai cột integer[] là System.Array (cho phép mảng nhiều chiều),
        // tham số của ta là int[] — không hàm dựng nào khớp, Dapper ném InvalidOperationException.
        //
        // Vì sao không ai thấy suốt cả nhánh: chưa có dòng cấu hình thì truy vấn trả null và mọi
        // thứ chạy êm. Công ty bấm Lưu ở màn hình Cấu hình phân công LẦN ĐẦU là hỏng — và vì
        // SessionAuth.ReadNguoiXemAsync gọi LayCauHinhAsync ở MỌI request chat, cả hộp thư 500.
        // Bấm Lưu một lần, mất hộp thư.
        //
        // Luật: đọc vào lớp có thuộc tính GHI ĐƯỢC (đường thuộc tính ép kiểu giá trị THẬT trong
        // hộp, mà giá trị thật đúng là int[]), rồi mới dựng record miền.
        // DuongDocCauHinh() trỏ vào ChatInboxEndpoints (đường ĐỌC của endpoint) — lỗi này
        // nằm ở KHO DỮ LIỆU, file khác hẳn. Đọc đúng file, đừng mượn helper gần đúng.
        var kho = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs");
        Assert.DoesNotContain("Async<ChatAssignSettings>", kho);
        Assert.Contains("class DongCauHinh", kho);
        Assert.Contains("int[]? MemberIds", kho);
        // Cột NULL (dữ liệu cũ) phải thành mảng RỖNG: chỗ gọi đọc .Length và .Contains ngay,
        // để null lọt ra là NullReferenceException giữa đường phân công.
        Assert.Contains("Array.Empty<int>()", kho);
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

    [Fact]
    public void Hai_ca_danh_sach_nhan_vien_rong_phai_ghi_HAI_dong_log_KHAC_nhau()
    {
        // "Ô chọn người phụ trách trống" có HAI nguyên nhân, và chúng đòi hai người khác nhau
        // đi sửa hai chỗ khác nhau: ERP không còn người bán nào, hay ERP CÓ trả người bán mà
        // dòng nào cũng thiếu mã/tên. Một dòng log dùng chung cho cả hai là chỉ sai đường.
        //
        // Đã trả giá 09/09/2026: /api/ai/reference trả về ĐỦ 108 người bán nhưng `name` null
        // sạch, log chỉ ghi "ERP không trả người bán nào", và cả buổi đó đi tìm lỗi kết nối
        // với lỗi phiên — trong khi kết nối vẫn tốt và dữ liệu vẫn về đủ.
        var than = BoChuThich(ThanHam("assign-settings"));
        Assert.False(string.IsNullOrWhiteSpace(than), "Không cắt được thân đường đọc cấu hình");

        // (a) Phải ĐẾM ngay ở đầu vòng lặp đọc người bán. Đếm chỗ khác — sau vòng lặp, hay
        //     mượn luôn nhanVien.Count — thì con số lại là số GIỮ ĐƯỢC: đúng con số đã làm ta
        //     lạc đường, không phải con số cần.
        var dem = Regex.Match(than,
            @"foreach \(var it in sellers\.EnumerateArray\(\)\)\s*\{\s*(\w+)\+\+;");
        Assert.True(dem.Success,
            "Phải đếm số dòng ERP trả về ngay tại đầu vòng lặp đọc sellers");
        var bien = dem.Groups[1].Value;

        // (b) Ca "ERP không trả dòng nào" phải là một nhánh RIÊNG, phân xử bằng chính biến đếm.
        Assert.Matches(@"else if \(!\w+ && " + bien + @" == 0\)\s*log\.LogWarning\(", than);

        // (c) Ca còn lại phải NÓI RA con số. Thiếu {N} thì hai dòng chỉ khác nhau ở câu chữ,
        //     người đọc log vẫn không biết ERP đã gửi về bao nhiêu — tức vẫn không phân biệt
        //     được "không có ai" với "có mà hỏng", đúng thứ chốt này sinh ra để chặn.
        var canhBao = Regex.Matches(than, @"log\.LogWarning\((.{0,600}?)\);", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(canhBao, c => c.Contains("{N}") && c.Contains(bien));
    }

    /// <summary>
    /// Đường "chia lại hội thoại chưa có người" phải DÙNG LẠI vòng quay, không chép luật chia.
    ///
    /// <para><b>Vì sao đây là luật quan trọng nhất của đường này.</b> Vòng quay là MỘT câu lệnh
    /// vừa đẩy con trỏ vừa gán, để hai tin tới cùng lúc không gán hai người cho một hội thoại.
    /// Chép luật đó ra bản thứ hai thì hai bản dùng chung một con trỏ mà không chung một cách
    /// đẩy — vòng quay lệch, và lệch âm thầm: mọi hội thoại vẫn có người, chỉ là chia không đều
    /// và không ai biết cho tới lúc có người kêu.</para>
    /// </summary>
    [Fact]
    public void Chia_lai_phai_dung_lai_vong_quay_chu_khong_chep_luat_chia()
    {
        var than = ThanHamPost("assign-settings/chia-lai");
        Assert.False(string.IsNullOrWhiteSpace(than), "Không cắt được thân đường chia lại");

        // (a) Dùng lại đúng câu lệnh nguyên tử của vòng quay.
        Assert.Contains("assign.GanXoayVongAsync(a.TenantId, id, ct)", than);
        // Và KHÔNG có câu SQL chia nào viết tay ở đây.
        Assert.DoesNotContain("UPDATE chat_conversations", than);
        Assert.DoesNotContain("rotation_last_user_id", than);

        // (b) Cùng cửa quyền với chỗ LƯU cấu hình — đây là thao tác trên cấu hình của cả công ty.
        Assert.Contains("CanConfigSystemAsync", than);

        // (c) HAI câu từ chối RIÊNG cho hai nguyên nhân riêng: sai chế độ, và đội trực rỗng. Gộp
        //     làm một là chỉ đường sai đúng một nửa số lần — người đọc đi đổi chế độ trong khi
        //     thứ thiếu là người, hoặc ngược lại.
        var iChe = than.IndexOf("Mode != CheDoPhanCong.XoayVong", StringComparison.Ordinal);
        var iDoi = than.IndexOf("MemberIds.Length == 0", StringComparison.Ordinal);
        Assert.True(iChe > 0, "Thiếu cửa kiểm chế độ xoay vòng");
        Assert.True(iDoi > iChe, "Thiếu cửa kiểm đội trực rỗng, hoặc nó nằm TRƯỚC cửa chế độ");

        // (d) Có TRẦN mỗi lượt. Lượt chạy gọi CSDL một lần cho mỗi hội thoại; một công ty vừa nối
        //     kênh có hàng nghìn hội thoại cũ, không có trần thì một cú bấm hết giờ chờ giữa
        //     chừng — mà phần đã chia thì không hoàn tác được.
        Assert.Contains("TranChiaLai", than);

        // (e) Nhật ký ghi dưới danh nghĩa NGƯỜI BẤM, không phải null. null nghĩa là hệ thống tự
        //     làm; đây là việc có người ra lệnh, và "ai bấm" là câu hỏi đầu tiên khi tra lại.
        Assert.Contains("\"chia-lai\"", than);
        Assert.DoesNotMatch(@"AppendAuditAsync\([^;]{0,80}?null,\s*""chia-lai""", than);
    }

    [Fact]
    public void Man_hinh_phan_cong_phai_co_nut_chia_lai()
    {
        // Nửa còn lại của cùng một luật: máy chủ có đường mà giao diện không có nút thì với người
        // dùng là chưa có gì — và phần tồn đọng vẫn nằm đó, vô hình với nhân viên thường.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-assign-settings.jsx");
        Assert.Contains("'/api/v1/chat/assign-settings/chia-lai'", jsx);
        var m = Regex.Match(jsx, @"async function chiaLai\(\)(.{0,900})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy hàm chiaLai");
        Assert.Contains("method: 'POST'", m.Groups[1].Value);
        // Nút chỉ hiện ở chế độ xoay vòng — ở thủ công thì không có vòng quay nào để chia theo.
        Assert.Contains("{mode === 2 && (", jsx);
    }

    [Fact]
    public void Tam_nghi_nhan_viec_phai_dung_luat_o_ca_ba_cho()
    {
        var kho = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs");

        // (a) VÒNG QUAY phải bỏ qua người đang tạm nghỉ. Thiếu vế này thì công tắc chỉ là một
        //     cái nút đẹp: người bấm tưởng mình đã dừng, việc vẫn rơi vào họ.
        var vq = Regex.Match(kho, @"GanXoayVongAsync(.{0,3000})", RegexOptions.Singleline);
        Assert.True(vq.Success, "Không thấy GanXoayVongAsync");
        Assert.Contains("paused_ids", vq.Groups[1].Value);

        // (b) Cửa "không cho người CUỐI CÙNG nghỉ" phải nằm TRONG câu UPDATE.
        //     Đọc-rồi-ghi thì hai người cùng bấm một lúc đều thấy "vẫn còn người khác" và cả hai
        //     cùng nghỉ được → đội trực không còn ai nhận việc → hội thoại nằm lại không người
        //     phụ trách → vô hình với nhân viên thường. Đúng lỗ hổng vừa bịt cùng ngày.
        var tn = Regex.Match(kho, @"DatTamNghiAsync(.{0,2500})", RegexOptions.Singleline);
        Assert.True(tn.Success, "Không thấy DatTamNghiAsync");
        var than = tn.Groups[1].Value;
        var iThem = than.IndexOf("array_append", StringComparison.Ordinal);
        Assert.True(iThem > 0, "Không thấy nhánh BẬT tạm nghỉ");
        var iHetCau = than.IndexOf("\"\"\"", iThem, StringComparison.Ordinal);
        Assert.True(iHetCau > iThem, "Không cắt được câu lệnh bật tạm nghỉ");
        var cauBat = than[iThem..iHetCau];
        Assert.Contains("EXISTS (SELECT 1 FROM unnest(s.member_ids) u", cauBat);
        Assert.Contains("u <> @userId", cauBat);

        // (c) Lượt LƯU của quản trị KHÔNG được đụng paused_ids — bấm Lưu một cái mà gọi cả đội
        //     đi làm lại thì đúng vào lúc người ta đang nghỉ thật, và không ai thấy gì.
        // Cắt bằng RANH GIỚI CÚ PHÁP (tới thành viên kế tiếp của lớp), không đếm ký tự: bản đầu
        // của chốt này kẹp 2000 ký tự và đỏ oan ngay khi thân hàm dài thêm mấy dòng chú thích.
        var iLuu = kho.IndexOf("public async Task LuuCauHinhAsync", StringComparison.Ordinal);
        Assert.True(iLuu > 0, "Không thấy LuuCauHinhAsync");
        var sau = kho[(iLuu + 20)..];
        var mHet = Regex.Match(sau, @"^    (public|/// <summary>)", RegexOptions.Multiline);
        // BỎ CHÚ THÍCH trước khi soi: chính chú thích ở đó nói "KHÔNG đụng paused_ids", và bản
        // đầu của chốt này bắt luôn câu ấy. Cấm một DẠNG MÃ thì phải đi qua đây — đã ghi sổ.
        Assert.DoesNotContain("paused_ids", BoChuThich(mHet.Success ? sau[..mHet.Index] : sau));
    }

    [Fact]
    public void Cong_tac_tam_nghi_la_cua_NGUOI_TRUC_khong_phai_cua_quan_tri()
    {
        // Người đi họp, đi ăn, hết ca thì tự tắt. Quản trị đặt hộ thì luôn trễ so với thực tế,
        // mà trễ ở đây nghĩa là khách rơi vào người không có mặt rồi nằm đó.
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("'/api/v1/chat/tam-nghi?nghi=' + nghi", jsx);

        // Công tắc nằm trên HỘP THƯ (nơi người trực ngồi), không nằm trong màn cấu hình.
        var cas = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-assign-settings.jsx");
        Assert.DoesNotContain("tam-nghi", cas);

        // Chỉ hiện khi mình NẰM TRONG đội trực — ngoài đội thì vốn không có lượt nào, bày công
        // tắc ra chỉ làm người ta tưởng đang có.
        Assert.Contains("{phanCong.trongDoiTruc && (", jsx);

        // Và máy chủ phải phát ra đủ ba ô giao diện cần, nếu không công tắc vẽ mù.
        var ep = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        foreach (var o in new[] { "pausedIds", "tamNghi", "trongDoiTruc" })
            Assert.Contains(o + " ", ep);
    }

    /// <summary>Thân một handler <c>MapPost</c>, cắt tới dấu đóng của chính nó.</summary>
    private static string ThanHamPost(string ten)
    {
        var kho = DuongDocCauHinh();
        var neo = $"MapPost(\"/{ten}\"";
        var batDau = kho.IndexOf(neo, StringComparison.Ordinal);
        if (batDau < 0) return "";
        var m = Regex.Match(kho[batDau..], @"\A.*?\r?\n        \}\);", RegexOptions.Singleline);
        return m.Success ? m.Value : "";
    }
}
