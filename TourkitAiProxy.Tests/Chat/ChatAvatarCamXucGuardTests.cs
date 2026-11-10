using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chốt cho ba việc chốt ngày 14/09/2026: ảnh đại diện lấy lại khi hồ sơ cũ, ảnh đại diện nén
/// theo luật riêng, và điểm cảm xúc rút lại khi khách gỡ biểu tượng.
///
/// <para><b>Vì sao canh ở mức mã nguồn.</b> Cả ba đều nằm trên đường CHẠM CSDL hoặc CHẠM NỀN
/// TẢNG — bộ test này chỉ phủ logic thuần, không có gì chạy tới. Mà cả ba đều thuộc loại hỏng
/// IM LẶNG: ảnh cũ vẫn hiện (chỉ là sai người), ảnh to vẫn hiện (chỉ là tốn băng thông), điểm
/// cảm xúc vẫn ra số (chỉ là số sai). Không ai báo lỗi, nên không có chốt thì không ai biết.</para>
/// </summary>
public class ChatAvatarCamXucGuardTests
{
    private static string Kho() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
    private static string Db() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    private static string Soi() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Services/Chat/Inbox/ChatMediaMirror.cs");
    private static string Nhan() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs");

    // ── Ảnh đại diện: lấy lại khi hồ sơ đã cũ ───────────────────────────────

    /// <summary>
    /// Không nền tảng nào đẩy sự kiện "khách đổi ảnh", và gói webhook tin nhắn chỉ mang MÃ người
    /// gửi. Nên cách duy nhất biết là hỏi lại theo chu kỳ — và điều kiện "hồ sơ đã cũ" phải nằm
    /// trong chính câu hỏi "có cần lấy hồ sơ không". Thiếu nó thì khách đã có ảnh KHÔNG BAO GIỜ
    /// được hỏi lại, và ảnh cũ nằm đó vĩnh viễn.
    /// </summary>
    [Fact]
    public void Ho_so_khach_qua_cu_thi_phai_hoi_lai_nen_tang()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(), "public async Task<bool> NeedsContactProfileAsync");
        Assert.Contains("profile_synced_utc", than);
        Assert.Contains("HoSoCuSauNgay", than);
    }

    // ── Ảnh đại diện nhặt từ chính gói tin (10/11/2026) ─────────────────────
    // Ca thật: Pancake KHÔNG có đường tra hồ sơ khách, nên ContactProfileAsync trả null và hồ sơ
    // khách không bao giờ có ảnh — trong khi gói webhook thì CÓ sẵn ảnh, chỉ là trước đây ghi vào
    // từng tin nhắn rồi bỏ đi. Triệu chứng sếp báo: bong bóng chat có ảnh, danh sách thì không.

    /// <summary>
    /// Kênh không có đường hỏi hồ sơ thì phải lấy ảnh từ chính gói tin. Mất vế này là quay về
    /// đúng lỗi cũ, mà lỗi đó KHÔNG báo gì — chỉ là danh sách hiện chữ cái đầu thay cho ảnh.
    /// </summary>
    [Fact]
    public void Khong_hoi_duoc_ho_so_thi_phai_nhat_anh_trong_goi_tin()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Nhan(), "private async Task OneEventAsync");

        Assert.Contains("e.SenderAvatarUrl", than);
        Assert.Contains("MirrorAvatarAsync", than);
    }

    /// <summary>
    /// <b>Tiếng vọng phải bị loại.</b> Tin dội lại là do TRANG gửi, nên ảnh trong đó là ảnh của
    /// Trang. Gán nó vào hồ sơ khách thì cả danh sách hội thoại hiện logo công ty thay cho mặt
    /// khách — hỏng im lặng, trông vẫn "có ảnh" nên không ai báo.
    /// </summary>
    [Fact]
    public void Khong_duoc_lay_anh_cua_TRANG_trong_tin_doi_lai_gan_cho_khach()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Nhan(), "private async Task OneEventAsync");
        var i = than.IndexOf("e.SenderAvatarUrl", StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy chỗ nhặt ảnh từ gói tin");

        // Điều kiện loại tiếng vọng phải nằm NGAY TRONG cùng điều kiện nhặt ảnh, không phải ở
        // một chỗ nào đó khác trong hàm.
        Assert.Contains("!e.IsEcho", than[Math.Max(0, i - 200)..i]);
    }

    /// <summary>
    /// Chỉ đóng mốc hồ sơ khi trong tay THẬT SỰ có ảnh. Gói tin Telegram có tên nhưng không có
    /// ảnh — đóng mốc ở đó là tắt hẳn cơ chế soi lại ảnh, vì hồ sơ luôn "mới" nên không bao giờ
    /// cũ để mà hỏi lại.
    /// </summary>
    [Fact]
    public void Chi_dong_moc_ho_so_khi_that_su_co_anh()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Nhan(), "private async Task OneEventAsync");

        Assert.Contains("dongMocHoSo: hoSo is not null || !string.IsNullOrWhiteSpace(anh)", than);
    }

    /// <summary>
    /// Mốc phải đóng ở MỌI lượt hỏi được nền tảng, kể cả khi tên và ảnh trả về y như cũ. Chỉ đóng
    /// khi có thay đổi thì khách không bao giờ đổi ảnh sẽ bị hỏi lại ở MỖI tin nhắn — đúng thứ
    /// làm cháy hạn mức gọi Graph.
    /// </summary>
    [Fact]
    public void Moc_hoi_ho_so_chi_dong_khi_THAT_SU_hoi_nen_tang()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(), "public async Task UpsertContactAsync");
        // Có ĐIỀU KIỆN, không đóng vô điều kiện.
        Assert.Contains("CASE WHEN @moc THEN now()", than);

        // Và chỗ gọi phải phân biệt được hai lối.
        //
        // Telegram gửi kèm tên người gửi trong MỌI gói tin, nên luồng nhận tin upsert ở mỗi tin
        // để cập nhật tên. Đóng mốc ở lượt gọi đó là nói dối rằng vừa hỏi hồ sơ, và hậu quả đúng
        // bằng việc TẮT HẲN cơ chế soi lại ảnh: hồ sơ luôn "mới" nên không bao giờ cũ để hỏi lại.
        //
        // Canh bằng cách soi HAI lượt gọi, không đếm số lần chuỗi xuất hiện — đếm chuỗi thì chính
        // chú thích giải thích cờ cũng bị tính, và chốt đỏ oan (đã dính 14/09/2026).
        // Mỗi khẳng định nằm gọn MỘT DÒNG. Khẳng định vắt qua dấu xuống dòng là chốt canh giòn:
        // file này lưu bằng CRLF nên `\n` trong chuỗi C# không bao giờ khớp (đã dính 14/09/2026).
        var nhan = Nhan();
        // Lượt LẤY HỒ SƠ: có bật cờ — nay là một ĐIỀU KIỆN chứ không còn là `true` trần.
        //
        // Đổi 10/11/2026 cùng lúc với việc nhặt ảnh từ gói tin cho Pancake. Lằn ranh cũ giữ
        // nguyên, chỉ nói chặt hơn: đóng mốc khi đã hỏi được nền tảng HOẶC khi thật sự cầm được
        // ảnh trong tay. Xem Chi_dong_moc_ho_so_khi_that_su_co_anh.
        Assert.Contains("dongMocHoSo: hoSo is not null || !string.IsNullOrWhiteSpace(anh)", nhan);
        // Lượt theo TỪNG TIN (tên lấy sẵn trong gói tin): KHÔNG được bật cờ.
        Assert.Contains("e.ExternalUserId, e.DisplayName, ct: ct);", nhan);
        Assert.DoesNotContain("e.DisplayName, dongMocHoSo", nhan);
    }

    [Fact]
    public void Co_cot_luu_moc_hoi_ho_so()
        => Assert.Contains("chat_contacts ADD COLUMN IF NOT EXISTS profile_synced_utc", Db());

    // ── Ảnh đại diện: nén theo luật riêng ───────────────────────────────────

    /// <summary>
    /// Giao diện vẽ avatar ở 34–180px. Đi chung luật ảnh thường (chỉ nén khi trên 300KB, nén thì
    /// về 1600px) nghĩa là phần lớn avatar không được nén chút nào.
    /// </summary>
    [Fact]
    public void Anh_dai_dien_co_tran_canh_RIENG_va_nho_hon_anh_thuong()
    {
        var src = Soi();
        Assert.Contains("CanhAvatar = 180", src);
        // Phải THẬT SỰ dùng, không phải khai rồi bỏ đó.
        Assert.Contains("laAnhDaiDien ? CanhAvatar : CanhToiDa", src);
    }

    /// <summary>
    /// Và phải BỎ ngưỡng "dưới 300KB thì thôi" cho riêng avatar: một tấm 900px nặng 120KB lọt
    /// dưới ngưỡng đó mà vẫn gấp năm lần mức cần.
    /// </summary>
    [Fact]
    public void Anh_dai_dien_nen_ca_khi_tep_nho()
        => Assert.Contains("if (!laAnhDaiDien && bytes.LongLength <= BoQuaDuoi)", Soi());

    [Fact]
    public void Duong_soi_anh_dai_dien_bat_dung_co()
        => Assert.Contains("LaAnhDaiDien: true", Nhan());

    // ── Cảm xúc: rút lại khi khách gỡ ───────────────────────────────────────

    /// <summary>
    /// Giữ nguyên điểm khi khách gỡ biểu tượng KHÔNG phải là "không kết luận gì" — nó là tiếp tục
    /// kết luận bằng một tín hiệu khách đã rút lại. Hộp cảm xúc khi đó nói dối bằng con số.
    /// </summary>
    [Fact]
    public void Go_cam_xuc_thi_phai_RUT_diem_da_cong()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Nhan(), "if (e.Reaction is { } camXuc)");
        Assert.Contains("RemoveSentimentSignalAsync", than);
        Assert.Contains("AddSentimentSignalAsync", than);
        // Rút cái CŨ trước rồi mới góp cái mới — đổi biểu tượng là cả hai cùng chạy.
        Assert.True(than.IndexOf("RemoveSentimentSignalAsync", System.StringComparison.Ordinal)
                  < than.IndexOf("AddSentimentSignalAsync", System.StringComparison.Ordinal),
            "Phải RÚT cái cũ TRƯỚC khi GÓP cái mới.");
    }

    /// <summary>
    /// Phải trừ ĐÚNG con số đã cộng, nên điểm phải được lưu ngay trên lượt thả. Chấm lại từ emoji
    /// lúc gỡ là sai với Telegram (trường `name` là custom_emoji_id, không chấm lại được) và sai
    /// bất cứ khi nào thang chấm đổi giữa lúc thả và lúc gỡ.
    /// </summary>
    [Fact]
    public void Diem_duoc_luu_ngay_tren_luot_tha()
    {
        Assert.Contains("chat_reactions ADD COLUMN IF NOT EXISTS sentiment_level", Db());
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(), "public async Task<SentimentLevel?> SetReactionAsync");
        Assert.Contains("SELECT sentiment_level FROM chat_reactions", than);
    }

    /// <summary>
    /// Kẹp sàn 0 cho CẢ HAI cột. Dữ liệu có trước cột <c>sentiment_level</c> đã cộng mà không lưu
    /// điểm, nên gỡ chúng không trừ được gì — một chuỗi gỡ như vậy kéo số đếm xuống ÂM, và số đếm
    /// âm làm phép chia trung bình ra số vô nghĩa.
    /// </summary>
    [Fact]
    public void Rut_diem_phai_kep_san_0()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Kho(), "public async Task RemoveSentimentSignalAsync");
        Assert.Contains("GREATEST(sentiment_sum - @muc, 0)", than);
        Assert.Contains("GREATEST(sentiment_count - 1, 0)", than);
    }
}
