using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nhật ký thao tác. Spec §1.3: "Mọi thao tác nhạy cảm đều được phân quyền và audit".
///
/// <para>Nhận/nhả việc, đổi trạng thái, tạm dừng bot, gỡ kết nối kênh — trước đây <b>không lưu
/// dấu vết nào</b>. Khi khách khiếu nại "ai nói câu này với tôi" thì không tra được, và khi một
/// hội thoại bị đóng nhầm thì không biết ai đóng.</para>
/// </summary>
public class ChatAuditGuardTests
{
    private static string Db() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    [Fact]
    public void Co_bang_nhat_ky()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_audit", Db());
        // Tra theo hội thoại là cách dùng duy nhất hiện có — thiếu chỉ mục là quét cả bảng mỗi lần
        // mở panel hồ sơ, và bảng này chỉ có lớn thêm.
        Assert.Contains("ON chat_audit (tenant_id, conversation_id, created_utc DESC)", Db());
    }

    [Theory]
    [InlineData("nhan-viec")]
    [InlineData("nha-viec")]
    [InlineData("chuyen-viec")]
    [InlineData("doi-trang-thai")]
    [InlineData("tam-dung-bot")]
    [InlineData("go-ket-noi")]
    public void Moi_thao_tac_nhay_cam_deu_duoc_ghi(string hanhDong)
    {
        // Canh ở mức mã nguồn vì không có CI chạy PostgreSQL. Thiếu một hành động ở đây thì lỗ
        // hổng chỉ lộ ra lúc cần tra — tức là lúc đã muộn.
        Assert.Contains(hanhDong, Endpoint());
    }

    [Fact]
    public void Nhan_viec_that_su_goi_ghi_nhat_ky()
    {
        var src = Endpoint();
        var m = Regex.Match(src, "ClaimConversationAsync(.{0,1200})", RegexOptions.Singleline);
        Assert.True(m.Success);
        // Đi qua GhiNhatKyAsync — hàm gói chung lượt ghi nhật ký, chính nó gọi AppendAuditAsync.
        Assert.Contains("GhiNhatKyAsync", m.Groups[1].Value);
    }

    [Fact]
    public void KHONG_chep_noi_dung_tin_vao_nhat_ky()
    {
        // Tin đã nằm ở chat_messages. Chép lại là nhân đôi dữ liệu khách VÀ nhân đôi chỗ phải xoá
        // khi khách yêu cầu xoá dữ liệu — sót một chỗ là vẫn còn lưu trái ý khách.
        // Soi TỪNG lời gọi tới hết dấu chấm phẩy, không soi một cửa sổ ký tự cố định: cửa sổ dễ
        // trùm sang mã bên cạnh rồi báo đỏ vì một chữ chẳng liên quan.
        // Phải soi CẢ hàm gói GhiNhatKyAsync. Từ 07/09/2026 mười tám chỗ ghi nhật ký đi qua nó
        // chứ không gọi thẳng AppendAuditAsync nữa; chốt canh chỉ bắt tên trực tiếp thì đếm được
        // ĐÚNG MỘT lời gọi (chính thân hàm gói) và luật này thành trang trí — đã đo: chép thẳng
        // v.LastPreview vào một lượt ghi nhật ký mà toàn bộ 1219 test vẫn xanh.
        var goi = Regex.Matches(Endpoint(), @"(?:AppendAuditAsync|GhiNhatKyAsync)\([^;]*;", RegexOptions.Singleline)
            .Select(x => x.Value).ToList();
        Assert.NotEmpty(goi);

        foreach (var cam in new[] { "body.Text", "tin.Body", "LastPreview", "Summarize" })
            Assert.DoesNotContain(goi, g => g.Contains(cam, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Duong_dan_nhat_ky_nam_trong_DuongRieng()
    {
        // Không nằm trong danh sách thì lúc tắt cờ Features:Chat, đường này rơi vào MapFallback và
        // trả index.html kèm 200 thay vì 404 — client gọi API nhận về HTML.
        const string duong = "/api/v1/chat/conversations/1/audit";
        Assert.Contains(TourkitAiProxy.Endpoints.ChatInboxEndpoints.OwnedPaths,
            p => duong.StartsWith(p + "/", System.StringComparison.Ordinal));
    }

    /// Bỏ dòng chú thích trước khi soi — chú thích ở cả hai đầu đều nhắc tới chính các mã hành
    /// động mà chốt đang đối chiếu.
    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));

    /// <summary>
    /// MỌI mã hành động máy chủ ghi vào nhật ký đều phải có nhãn tiếng Việt ở giao diện.
    ///
    /// <para><b>Vì sao cần chốt.</b> Giao diện cố ý lùi về in mã trần khi thiếu nhãn — đúng lựa
    /// chọn, vì giấu mất dòng nhật ký còn tệ hơn. Nhưng "xấu mà vẫn chạy" nghĩa là KHÔNG có
    /// triệu chứng nào: bộ test xanh, màn hình vẫn hiện, chỉ là người đọc thấy
    /// <c>"Hệ thống xoay-vong"</c> thay vì <c>"Hệ thống tự động chia việc"</c>.</para>
    ///
    /// <para>Đã trả giá 09/09/2026: máy chủ ghi 16 mã, giao diện có 13 nhãn. Ba mã thiếu gồm
    /// <c>xoay-vong</c> và <c>tu-nhan-khi-tra-loi</c> — tức là ĐÚNG hai đường tự động, hai dòng
    /// mà người đọc cần nhất khi hỏi "việc này máy giao hay người giao?".</para>
    /// </summary>
    [Fact]
    public void Moi_ma_hanh_dong_may_chu_ghi_deu_phai_co_nhan_o_giao_dien()
    {
        // Quét CẢ HAI dạng gọi trên CẢ HAI tệp.
        //
        // ⚠️ Bản đầu của chốt này chỉ quét AppendAuditAsync ở ChatInboundService, vì lúc viết thì
        // đường nền là chỗ DUY NHẤT ghi thẳng qua kho. Vài giờ sau, đường "chia lại hội thoại
        // chưa có người" cũng ghi thẳng như thế nhưng nằm ở tệp endpoint — và chốt XANH dù nhãn
        // còn thiếu. Chốt bám VỊ TRÍ thì mã dời chỗ một cái là vô hiệu; nay bám DẠNG GỌI.
        var ma = new SortedSet<string>(System.StringComparer.Ordinal);
        var nguon = new[]
        {
            BoChuThich(Endpoint()),
            BoChuThich(ChatSchemaGuardTests.DocFile(
                "TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs")),
        };
        foreach (var src in nguon)
        {
            foreach (Match m in Regex.Matches(src,
                         @"GhiNhatKyAsync\(ctx, repo, sessions, a, [^,]+, ""([a-z][a-z-]+)"""))
                ma.Add(m.Groups[1].Value);
            // Ghi thẳng qua kho — dùng khi người thao tác KHÔNG phải chủ thể thông thường của
            // GhiNhatKyAsync (đường nền ghi dưới danh nghĩa hệ thống, đường chia lại ghi dưới
            // danh nghĩa quản trị đang bấm nút).
            foreach (Match m in Regex.Matches(src,
                         @"AppendAuditAsync\([^;]{0,400}?""([a-z][a-z-]+)""", RegexOptions.Singleline))
                ma.Add(m.Groups[1].Value);
        }

        // CHỐT CANH CHO CHÍNH CHỐT NÀY: biểu thức bóc mã mà hỏng thì tập rỗng, và "tập rỗng nằm
        // trong mọi tập" nên phần đối chiếu bên dưới xanh trong im lặng — canh vào hư không.
        Assert.True(ma.Count >= 14,
            $"Chỉ bóc được {ma.Count} mã hành động ({string.Join(", ", ma)}) — biểu thức bóc hỏng rồi.");
        Assert.Contains("xoay-vong", ma);

        var jsx = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));
        var bang = Regex.Match(jsx, @"const TEN_HANH_DONG = \{(.*?)\n  \};", RegexOptions.Singleline);
        Assert.True(bang.Success, "Không thấy bảng TEN_HANH_DONG trong chat-inbox.jsx");
        var nhan = new HashSet<string>(Regex.Matches(bang.Groups[1].Value, @"'([a-z][a-z-]+)':\s*'")
            .Select(m => m.Groups[1].Value), System.StringComparer.Ordinal);

        var thieu = ma.Where(x => !nhan.Contains(x)).ToList();
        Assert.True(thieu.Count == 0,
            "Máy chủ ghi mã này mà giao diện không có nhãn, nhật ký sẽ in mã trần: "
            + string.Join(", ", thieu));
    }

    [Fact]
    public void Nhat_ky_hien_TEN_nguoi_duoc_giao_chu_khong_hien_ma()
    {
        // Nhật ký lưu MÃ người (quyết định bằng mã, hiển thị bằng tên). Bản trước in thẳng mã ra
        // cho "chuyển việc" — đọc thành "Anh A chuyển việc cho 2" — và đường tự động chia thì
        // không có nhánh nào cả. Cùng lớp lỗi với mục khách quen của bản tin sáng cùng ngày.
        var jsx = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));

        // Có hàm tra tên dùng chung, và nó lùi về #mã khi tra không ra (ô trống thì người dùng
        // tưởng hỏng).
        Assert.Matches(@"const tenNhanVien = \(staffs, ma\) =>", jsx);
        Assert.Contains("'#' + ma", jsx);

        // CẢ HAI đường giao việc đều đi qua hàm đó — giao tay và tự động chia.
        var m = Regex.Match(jsx, @"const them =(.{0,700}?);", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy nhánh dựng phần đuôi của dòng nhật ký");
        var them = m.Groups[1].Value;
        Assert.Contains("'chuyen-viec'", them);
        Assert.Contains("'xoay-vong'", them);
        Assert.Contains("tenNhanVien(staffs, ct.cho)", them);
        // In thẳng mã là dạng mã bị cấm ở đây.
        Assert.DoesNotContain("' cho ' + ct.cho", them);
    }

    [Fact]
    public void Dong_nhat_ky_phai_co_moc_thoi_gian_tuyet_doi()
    {
        // "3 giờ trước" đủ để lướt, nhưng nhật ký sinh ra để TRA LẠI: đối chiếu với hộp thư, với
        // lịch sử CRM, với lời khách kể. Không có mốc thật thì tra bằng gì.
        var jsx = BoChuThich(ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));
        Assert.Matches(@"<span title=\{fmtDate\(d\.createdUtc, \{ time: true \}\)\}>\{fmtAgo\(d\.createdUtc\)\}</span>", jsx);
    }
}
