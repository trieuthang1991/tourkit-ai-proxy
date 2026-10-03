using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Chốt cho BẢN NHÁP AI cất trong Redis — đường <c>/conversations/{id}/suggest</c>.
///
/// <para><b>Vì sao phải canh ở mức mã nguồn.</b> Bộ test này chỉ phủ logic thuần, không chạm Redis
/// lẫn CSDL; còn gọi thật thì <c>SuggestAsync</c> chỉ soạn khi tin CUỐI là của khách, nên trên
/// staging — nơi mọi hội thoại đều đang ở lượt nhân viên trả lời sau cùng — đường ghi đệm không
/// bao giờ chạy tới. Kiểm tay hôm 14/09/2026 chỉ chứng minh được đường ĐỌC và đường XOÁ.</para>
///
/// <para>Đúng cái khoảng mù ấy là chỗ lỗi thầm lặng hay nằm: bỏ quên lượt ghi đệm thì tính năng
/// vẫn "chạy" — soạn vẫn ra chữ, màn hình vẫn hiện thẻ nháp — chỉ là rời hội thoại rồi quay lại
/// là mất trắng, đúng cái phiền mà việc đệm sinh ra để chữa.</para>
/// </summary>
public class ChatNhapAiGuardTests
{
    private static string Endpoint()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    private static string ThanRoute(string route)
        => ChatSchemaGuardTests.ThanThanhVien(Endpoint(), route);

    [Fact]
    public void Soan_xong_thi_PHAI_cat_nhap_vao_dem()
    {
        var than = ThanRoute("g.MapPost(\"/conversations/{id:long}/suggest\"");

        Assert.Contains("KhoaNhapAiAsync", than);
        Assert.Contains("redis.Set", than);
        // Phải có hạn sống. Cất vĩnh viễn là mỗi hội thoại từng nhờ soạn để lại một khoá sống mãi.
        Assert.Contains("NhapAiSong", than);
    }

    /// <summary>
    /// Chỉ cất khi THẬT SỰ có chữ. Cất cả bản rỗng thì lần mở sau đường đọc thấy có khoá, trả về
    /// một bản nháp trắng, và màn hình hiện thẻ "Nháp do AI soạn" không có gì bên trong.
    /// </summary>
    [Fact]
    public void KHONG_cat_khi_khong_soan_ra_chu()
    {
        var than = ThanRoute("g.MapPost(\"/conversations/{id:long}/suggest\"");
        Assert.Contains("ra.Text is { Length: > 0 }", than);
    }

    /// <summary>
    /// Chưa soạn nháp nào là trạng thái BÌNH THƯỜNG của gần như mọi hội thoại, nên đường đọc phải
    /// trả 200 kèm <c>chu = null</c>.
    ///
    /// <para>Trả 404 thì lớp <c>authedFetch</c> chung ở giao diện coi là hỏng — và đây là lượt gọi
    /// chạy TỰ ĐỘNG mỗi lần mở một hội thoại, nên cái giá phải trả là một lượt báo hỏng ở gần như
    /// mọi thao tác mở hội thoại.</para>
    /// </summary>
    [Fact]
    public void Chua_co_nhap_thi_tra_200_chu_khong_phai_404()
    {
        var than = ThanRoute("g.MapGet(\"/conversations/{id:long}/suggest\"");

        Assert.Contains("chu = (string?)null", than);
        // Canh ĐÚNG nhánh "đệm rỗng": nó phải trả Json, không phải NotFound. Đếm tổng số lần
        // Results.NotFound() trong thân là cách canh sai — hàm trích thân ôm cả đường DELETE nằm
        // ngay sau, mà đường đó có NotFound hợp lệ của riêng nó.
        Assert.Contains("if (json is null) return Results.Json(new { chu = (string?)null }, Web);", than);
    }

    /// <summary>
    /// Nháp cũ phải được BÁO là cũ, không phải bị giấu đi và cũng không phải im lặng trả về như
    /// mới. Im lặng thì nhân viên gửi một câu trả lời cho tin nhắn đã bị thay thế.
    /// </summary>
    [Fact]
    public void Nhap_cu_phai_duoc_bao_la_cu()
    {
        var than = ThanRoute("g.MapGet(\"/conversations/{id:long}/suggest\"");
        Assert.Contains("cu = v.LastActivityAt > luu.MocHoiThoai", than);
    }

    /// <summary>
    /// Khoá đệm phải kẹp theo NGƯỜI, không chỉ theo hội thoại: nháp là bản viết dở của riêng người
    /// bấm soạn. Thiếu vế người thì hai nhân viên cùng mở một hội thoại sẽ thấy nháp của nhau —
    /// và dễ gửi đi câu mà người kia đang còn cân nhắc.
    /// </summary>
    [Fact]
    public void Khoa_dem_phai_kep_ca_cong_ty_hoi_thoai_VA_nguoi()
    {
        var than = ChatSchemaGuardTests.ThanThanhVien(Endpoint(), "KhoaNhapAiAsync(SessionAuth.Ctx a");

        Assert.Contains("$\"chat:nhap:{a.TenantId}:{hoiThoaiId}:{ma}\"", than);
        // Không tra ra mã người thì BỎ QUA việc đệm, không được lùi về khoá theo phiên: mã phiên
        // xoay vòng nên mỗi lần đăng nhập lại đẻ một khoá mồ côi sống tới hết hạn mới chết.
        Assert.DoesNotContain("SessionId}", than);
        Assert.Contains("ma is null ? null", than);
    }

    /// <summary>
    /// Hướng soạn phải đi bằng CHUỖI TRUY VẤN, không phải thân.
    ///
    /// <para>Tham số thân của minimal API gắn <c>AcceptsMetadata("application/json")</c> vào route,
    /// và request thiếu <c>Content-Type</c> bị loại ngay ở tầng ĐỊNH TUYẾN rồi rơi xuống trang SPA
    /// — nút bấm nhận 404 kèm HTML, không có gì xảy ra và không lỗi nào hiện ra. Đã trả giá đúng
    /// kiểu đó ở nút "Nhận chăm sóc" hôm 08/09/2026; chốt lại để đừng trả giá lần hai.</para>
    /// </summary>
    [Fact]
    public void Huong_soan_di_bang_chuoi_truy_van_KHONG_phai_than()
    {
        var src = Endpoint();
        Assert.Contains("g.MapPost(\"/conversations/{id:long}/suggest\", async (long id, string? tone,", src);
    }

    /// <summary>
    /// Giao diện chỉ được gửi MỘT MÃ; câu dặn nằm ở máy chủ.
    ///
    /// <para>Chữ ở đây đi thẳng vào lời nhắc gửi cho mô hình. Cho trình duyệt gửi chữ dặn tự do là
    /// mở toang một đường TIÊM LỜI NHẮC: ai mở được Bảng điều khiển trình duyệt cũng gỡ được khung
    /// an toàn cấm bịa giá tour, rồi bảo trợ lý hứa giữ chỗ với khách thật.</para>
    /// </summary>
    [Fact]
    public void Cau_dan_dich_tu_MA_o_may_chu_chu_khong_nhan_chu_tu_do()
    {
        var soanFile = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs");
        var than = ChatSchemaGuardTests.ThanThanhVien(soanFile, "public async Task<SuggestionOutcome> SuggestAsync");

        // Phải đi qua bảng dịch ở Domain.
        Assert.Contains("ChatRules.SuggestionToneHint(tone)", than);
        // Và KHÔNG được nối thẳng biến `tone` vào lời nhắc.
        Assert.DoesNotContain("nhacLai += tone", than);
        Assert.DoesNotContain("+ tone", than);
    }

    /// <summary>
    /// Mã lạ — kể cả mã do người khác gõ tay vào thanh địa chỉ — phải trả về "không có hướng nào",
    /// tức soạn thường, chứ không được ném hay tự đoán sang một hướng gần giống.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("khong-co-that")]
    [InlineData("Formal")]          // phân biệt hoa thường — mã là khoá dữ liệu, không phải chữ đọc
    public void Ma_huong_la_thi_soan_thuong(string? ma)
        => Assert.Null(TourkitAiProxy.Domain.Chat.ChatRules.SuggestionToneHint(ma));

    [Theory]
    [InlineData("formal")]
    [InlineData("callback")]
    [InlineData("ask-info")]
    [InlineData("apologize")]
    public void Bon_ma_that_deu_co_cau_dan(string ma)
        => Assert.False(string.IsNullOrWhiteSpace(
            TourkitAiProxy.Domain.Chat.ChatRules.SuggestionToneHint(ma)));
}
