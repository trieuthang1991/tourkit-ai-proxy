using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Bóc gói webhook Pancake. Phủ đúng những chỗ sai thì <b>im lặng</b>: tin của nhân viên bị coi là
/// tin của khách (trợ lý nói đè lên người thật), sự kiện không phải tin nhắn bị dựng thành dòng
/// trắng, và giờ bị lệch múi.
/// </summary>
public class PancakeAdapterTests
{
    private static PancakeChatAdapter Bo() => new(
        new KhongGoiMang(), null!, NullLogger<PancakeChatAdapter>.Instance);

    /// Không lượt test nào dưới đây chạm mạng — Parse là hàm thuần.
    private sealed class KhongGoiMang : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException(
            "Parse không được gọi mạng");
    }

    private const string Page = "tt_6711731671916708866";
    private const string Khach = "tt_6570511458700967938";

    private static string Goi(string? eventType = "messaging", string? fromId = Khach,
        string? text = "alo abc", string loai = "INBOX", string? attachments = null,
        string? insertedAt = "2024-12-25T11:06:07.000000")
    {
        var att = attachments ?? "[]";
        var chu = text is null ? "null" : $"\"{text}\"";
        var ev = eventType is null ? "null" : $"\"{eventType}\"";
        return $$"""
        {
          "page_id": "{{Page}}",
          "event_type": {{ev}},
          "data": {
            "conversation": { "id": "conv-1", "type": "{{loai}}",
                              "from": { "id": "{{Khach}}", "name": "Jineo" } },
            "message": { "id": "msg-1", "conversation_id": "conv-1", "page_id": "{{Page}}",
                         "message": {{chu}}, "type": "{{loai}}",
                         "inserted_at": "{{insertedAt}}",
                         "from": { "id": "{{fromId}}", "name": "Jineo" },
                         "attachments": {{att}} },
            "post": { "id": "post-9" }
          }
        }
        """;
    }

    [Fact]
    public void Tin_cua_khach_vao_hop_thu()
    {
        var sk = Assert.Single(Bo().Parse(Goi()));
        Assert.Equal(ChatChannel.Pancake, sk.Channel);
        Assert.Equal(Khach, sk.ExternalUserId);
        Assert.Equal("msg-1", sk.ExternalMsgId);
        Assert.Equal("alo abc", sk.Text);
        Assert.False(sk.IsEcho);
        Assert.Equal(ChatSurface.DirectMessage, sk.Surface);
    }

    /// <summary>
    /// Gói tin KHÔNG có cờ "của khách hay của trang" — phải so id người gửi với id trang. Bỏ bước
    /// này thì tin nhân viên trả lời từ chính app Pancake quay về thành tin của khách, và trợ lý
    /// trả lời đè lên người thật.
    /// </summary>
    [Fact]
    public void Tin_do_trang_gui_la_tieng_vong_va_khach_lay_tu_hoi_thoai()
    {
        var sk = Assert.Single(Bo().Parse(Goi(fromId: Page)));
        Assert.True(sk.IsEcho);
        Assert.Equal(Khach, sk.ExternalUserId);   // KHÔNG phải id trang
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("subscription")]
    [InlineData("post")]
    public void Su_kien_khong_phai_tin_nhan_thi_bo_qua(string loai)
        => Assert.Empty(Bo().Parse(Goi(eventType: loai)));

    /// Không chữ, không đính kèm → bỏ qua. Ghi một dòng trắng thì hội thoại vẫn nhảy lên đầu và
    /// vẫn tính chưa đọc, nhân viên mở ra không thấy gì.
    [Fact]
    public void Goi_rong_thi_khong_sinh_tin()
        => Assert.Empty(Bo().Parse(Goi(text: null)));

    [Fact]
    public void Binh_luan_duoc_danh_dau_la_cong_khai_kem_ma_bai()
    {
        var sk = Assert.Single(Bo().Parse(Goi(loai: "COMMENT")));
        Assert.Equal(ChatSurface.Comment, sk.Surface);
        Assert.Equal("post-9", sk.ThreadId);
    }

    [Fact]
    public void Binh_luan_livestream_cung_la_cong_khai()
        => Assert.Equal(ChatSurface.Comment,
            Assert.Single(Bo().Parse(Goi(loai: "COMMENT_LIVESTREAM"))).Surface);

    /// <summary>
    /// Pancake gửi giờ KHÔNG có hậu tố múi giờ ("2024-12-25T11:06:07.000000"). Không ép UTC thì
    /// .NET coi là giờ máy chủ và hộp thư hiện lệch đúng bằng chênh lệch múi giờ — ở Việt Nam là
    /// 7 tiếng, đủ để mọi tin trông như gửi từ hôm qua.
    /// </summary>
    [Fact]
    public void Gio_khong_co_mui_duoc_hieu_la_UTC()
    {
        var sk = Assert.Single(Bo().Parse(Goi()));
        Assert.Equal(new DateTime(2024, 12, 25, 11, 6, 7, DateTimeKind.Utc), sk.SentUtc);
    }

    [Theory]
    [InlineData("photo", ChatKind.Image)]
    [InlineData("image", ChatKind.Image)]
    [InlineData("audio", ChatKind.Audio)]
    [InlineData("sticker", ChatKind.Sticker)]
    [InlineData("video", ChatKind.File)]
    [InlineData("link", ChatKind.File)]
    public void Loai_dinh_kem_doan_theo_tien_to(string type, ChatKind mong)
    {
        var sk = Assert.Single(Bo().Parse(Goi(text: null,
            attachments: $"[{{\"type\":\"{type}\",\"link\":\"https://x/y\"}}]")));
        Assert.Equal(mong, sk.Kind);
    }

    /// Đính kèm mà không có chữ vẫn phải thành tin — không thì ảnh khách gửi biến mất.
    [Fact]
    public void Chi_co_dinh_kem_van_sinh_tin()
    {
        var sk = Assert.Single(Bo().Parse(Goi(text: null,
            attachments: "[{\"type\":\"photo\",\"image\":{\"url\":\"https://x/a.png\"}}]")));
        Assert.Equal(ChatKind.Image, sk.Kind);
        Assert.NotNull(sk.AttachmentJson);
    }

    [Fact]
    public void Goi_hong_khong_nem_loi()
    {
        Assert.Empty(Bo().Parse("khong phai json"));
        Assert.Empty(Bo().Parse("{}"));
        Assert.Empty(Bo().Parse(""));
    }

    [Fact]
    public void Boc_duoc_page_id_de_dinh_tuyen()
    {
        Assert.Equal(Page, PancakeChatAdapter.PageIdOfEvent(Goi()));
        Assert.Null(PancakeChatAdapter.PageIdOfEvent("{}"));
        Assert.Null(PancakeChatAdapter.PageIdOfEvent("hong"));
    }

    /// <summary>
    /// Chuỗi bí mật phải dán được thẳng vào ô URL bên Pancake: chỉ chữ–số–gạch. Có ký tự khác là
    /// trình duyệt/Pancake mã hoá lại và đường dẫn không còn khớp lúc tin gửi tới.
    /// </summary>
    [Fact]
    public void Chuoi_bi_mat_an_toan_tren_url_va_khong_trung()
    {
        var a = PancakeChatAdapter.SinhChuoiBiMat();
        Assert.True(a.Length >= 24, $"chuỗi quá ngắn: {a.Length}");
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, PancakeChatAdapter.SinhChuoiBiMat());
    }

    /// <summary>
    /// Đọc danh sách trang phải chịu được CẢ HAI hình dạng Pancake trả về: mảng phẳng
    /// <c>pages[]</c> và dạng nhóm theo nền tảng <c>categorized{}</c>. Đoán một kiểu thì bản kia ra
    /// danh sách rỗng mà không có lỗi nào — người khai tưởng tài khoản không có trang.
    /// </summary>
    [Theory]
    [InlineData("""{"pages":[{"id":"p1","name":"Trang 1","platform":"facebook"}]}""")]
    [InlineData("""{"categorized":{"facebook":[{"id":"p1","name":"Trang 1","platform":"facebook"}]}}""")]
    public void Doc_duoc_ca_hai_hinh_dang_danh_sach_trang(string json)
    {
        var t1 = Assert.Single(PancakeChatAdapter.DocDanhSachTrang(json));
        Assert.Equal("p1", t1.Id);
        Assert.Equal("Trang 1", t1.Name);
        Assert.Equal("facebook", t1.Platform);
    }

    /// Hai nhánh có thể cùng trả một trang — gộp vào phải KHÔNG nhân đôi, không thì người khai thấy
    /// trang lặp lại và tick nhầm hai lần.
    [Fact]
    public void Trang_trung_o_hai_nhanh_chi_tinh_mot_lan()
        => Assert.Single(PancakeChatAdapter.DocDanhSachTrang(
            """{"categorized":{"fb":[{"id":"p1","name":"A"}]},"pages":[{"id":"p1","name":"A"}]}"""));

    [Fact]
    public void Danh_sach_trang_hong_thi_tra_rong()
    {
        Assert.Empty(PancakeChatAdapter.DocDanhSachTrang(null));
        Assert.Empty(PancakeChatAdapter.DocDanhSachTrang("khong phai json"));
        Assert.Empty(PancakeChatAdapter.DocDanhSachTrang("{}"));
        // Trang thiếu id thì bỏ: không có id là không định tuyến được webhook về đâu cả.
        Assert.Empty(PancakeChatAdapter.DocDanhSachTrang("""{"pages":[{"name":"thieu id"}]}"""));
    }

    /// <summary>
    /// Trả về thật của Pancake gói năm nhóm, và nhóm <c>activated_page_ids</c> là mảng CHUỖI chứ
    /// không phải mảng object. Đánh chỉ mục <c>["id"]</c> lên một chuỗi thì .NET ném
    /// <c>InvalidOperationException</c> — cả lượt dò trang hỏng dù token hoàn toàn đúng, và người
    /// khai chỉ thấy một thông báo lỗi không liên quan gì tới nguyên nhân.
    /// </summary>
    [Fact]
    public void Nhom_mang_chuoi_khong_lam_hong_ca_luot_do()
    {
        var trang = PancakeChatAdapter.DocDanhSachTrang("""
        {"categorized":{"activated":[{"id":"p1","name":"Trang 1","platform":"facebook"}],
                        "inactivated":[],
                        "activated_page_ids":["p1","p9"]}}
        """);
        Assert.Equal("p1", Assert.Single(trang).Id);
    }

    /// Chủ token là quản trị thì Pancake trả kèm khoá trang sẵn có. Lấy được thì KHÔNG sinh mới —
    /// sinh mới là huỷ khoá cũ, làm chết tích hợp khác đang dùng chính trang đó.
    [Fact]
    public void Lay_duoc_khoa_trang_san_co_khi_Pancake_tra_kem()
    {
        var t = Assert.Single(PancakeChatAdapter.DocDanhSachTrang(
            """{"pages":[{"id":"p1","name":"A","settings":{"page_access_token":"khoa-cu"}}]}"""));
        Assert.Equal("khoa-cu", t.PageAccessToken);
    }

    [Fact]
    public void Khong_co_settings_thi_khoa_trang_la_null()
        => Assert.Null(Assert.Single(PancakeChatAdapter.DocDanhSachTrang(
            """{"pages":[{"id":"p1","name":"A"}]}""")).PageAccessToken);

    /// <summary>
    /// Khoá TRANG mang sẵn mã trang trong phần tải JWT. Đọc được mã này là phân biệt được hai loại
    /// token mà người khai không phân biệt nổi bằng mắt — dán nhầm loại thì Pancake chỉ trả đúng
    /// ba chữ "Invalid access_token", không gợi ý nổi phải làm gì.
    ///
    /// <para>Chuỗi dưới dựng theo đúng token thật: phần tải <c>{"id":"975563595648296","typ":"JWT"}</c>.</para>
    /// </summary>
    [Fact]
    public void Doc_duoc_ma_trang_trong_khoa_trang()
        => Assert.Equal("975563595648296", PancakeChatAdapter.DocPageIdTuToken(
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
            + ".eyJpZCI6Ijk3NTU2MzU5NTY0ODI5NiIsInR5cCI6IkpXVCJ9.chu-ky-gia"));

    /// Phần tải base64url đã cắt dấu '=' thì phải bù lại, không thì Convert.FromBase64String ném và
    /// mọi khoá trang có độ dài "lẻ" bị coi nhầm là khoá tài khoản.
    [Fact]
    public void Bu_duoc_dem_base64url_bi_cat()
    {
        var tai = System.Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("""{"id":"abc"}"""))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal("abc", PancakeChatAdapter.DocPageIdTuToken($"x.{tai}.y"));
    }

    /// Khoá TÀI KHOẢN không phải JWT ba đoạn → trả null để chỗ gọi rẽ sang đường liệt kê trang.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khoa-tai-khoan-thuong")]
    [InlineData("chi.hai-doan")]
    [InlineData("a.khong-phai-base64!!!.c")]
    [InlineData("a.eyJ0eXAiOiJKV1QifQ.c")]   // JWT hợp lệ nhưng phần tải không có "id"
    public void Khong_phai_khoa_trang_thi_tra_null(string? token)
        => Assert.Null(PancakeChatAdapter.DocPageIdTuToken(token));

    /// <summary>
    /// Pancake là CỔNG GOM nên hạn trả lời thật là hạn của nền tảng gốc sau lưng từng trang, mà
    /// luật chỉ biết "kênh". Để mở là cố ý — khoá theo một con số tự đoán là khoá nhầm tay nhân
    /// viên ở nửa số trang. Test này canh quyết định đó, đổi thì phải đổi có chủ ý.
    /// </summary>
    [Fact]
    public void Khong_khoa_o_soan_theo_han_tra_loi()
        => Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Pancake, null, DateTime.UtcNow).Open);

    [Fact]
    public void Ten_va_slug_kenh_da_khai()
    {
        Assert.Equal("Pancake", ChatRules.ChannelName(ChatChannel.Pancake));
        Assert.Equal("pancake", ChatRules.ChannelSlug(ChatChannel.Pancake));
    }

    /// <summary>
    /// Đường dẫn đính kèm nằm ở HAI chỗ tuỳ loại: ảnh ở <c>image.url</c>, liên kết/tệp ở
    /// <c>link</c>. Chỉ đọc một chỗ là mất hẳn loại kia — nhân viên thấy một đính kèm không bấm được.
    /// </summary>
    [Theory]
    [InlineData("""[{"type":"photo","image":{"url":"https://x/a.png"}}]""", "https://x/a.png")]
    [InlineData("""[{"type":"link","link":"https://netfull.online/"}]""", "https://netfull.online/")]
    public void Doc_duong_dan_dinh_kem_o_ca_hai_cho(string json, string mong)
    {
        var tep = ChatAttachment.Read(ChatChannel.Pancake, ChatKind.Image, json, chieu: 0);
        Assert.Equal(mong, Assert.Single(tep).Url);
    }
}
