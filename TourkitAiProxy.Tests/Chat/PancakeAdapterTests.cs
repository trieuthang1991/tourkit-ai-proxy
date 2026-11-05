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

    /// <summary>
    /// Thay chỗ giữ chỗ trong JSON mẫu. Dùng hàm này thay vì chuỗi nội suy <c>$$"""…"""</c> vì
    /// JSON lồng nhau kết thúc bằng <c>}}}</c>, mà trình biên dịch đọc đó là dấu đóng nội suy —
    /// lỗi CS9007, và cách chữa (thêm <c>$</c>) lại đẻ ra đúng xung đột đó ở mức sâu hơn.
    /// </summary>
    private static string Thay(string mau)
        => mau.Replace("PAGE", Page).Replace("KHACH", Khach);

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
        Assert.Equal("conv-1", sk.ExternalUserId);
        Assert.Equal("msg-1", sk.ExternalMsgId);
        Assert.Equal("alo abc", sk.Text);
        Assert.False(sk.IsEcho);
        Assert.Equal(ChatSurface.DirectMessage, sk.Surface);
    }

    /// <summary>
    /// <b>Mã khách ở kênh này là MÃ HỘI THOẠI</b>, không phải mã người gửi — API gửi của Pancake
    /// nhận <c>conversation_id</c> (xem <c>SendTextAsync</c>). Lấy mã người gửi thì câu trả lời đi
    /// tới một hội thoại không tồn tại, <b>không bao giờ tới khách</b>, mà hộp thư vẫn báo đã gửi.
    ///
    /// <para>Hai mã này thật sự khác nhau: đo trên dữ liệu thật 05/11/2026, cả 3/3 hội thoại đều
    /// lệch — <c>pzl_u_490605425456992929_3510358233866837464</c> có người gửi
    /// <c>pzl_u_3510358233866837464</c>.</para>
    /// </summary>
    [Fact]
    public void Ma_khach_la_ma_HOI_THOAI_chu_khong_phai_ma_nguoi_gui()
    {
        var sk = Assert.Single(Bo().Parse(Goi()));
        Assert.Equal("conv-1", sk.ExternalUserId);
        Assert.NotEqual(Khach, sk.ExternalUserId);
    }

    /// Thiếu khối hội thoại thì lấy conversation_id trên chính tin — gói webhook rút gọn vẫn có nó.
    [Fact]
    public void Thieu_khoi_hoi_thoai_thi_lay_conversation_id_tren_tin()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{"message":{
           "id":"m9","conversation_id":"conv-9","message":"x","type":"INBOX",
           "from":{"id":"KHACH"},"attachments":[]}}}
        """)));
        Assert.Equal("conv-9", sk.ExternalUserId);
    }

    /// <summary>
    /// Chat NHÓM (Zalo group qua Pancake): nhiều người gửi khác nhau trong CÙNG một hội thoại.
    /// Khoá theo người gửi thì một nhóm vỡ thành nhiều dòng rời trong hộp thư, và tên dòng đổi
    /// theo người vừa nói sau cùng.
    /// </summary>
    [Fact]
    public void Nhom_nhieu_nguoi_gui_van_la_MOT_hoi_thoai()
    {
        var sk = Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"pzl_g_1_2","type":"INBOX",
                          "from":{"id":"pzl_g_2","name":"SUPPORT TOURKIT - HAPPY TRIP"}},
          "messages":[
            {"id":"a","message":"nguoi mot","type":"INBOX","from":{"id":"pzl_u_111","name":"Duy"},"attachments":[]},
            {"id":"b","message":"nguoi hai","type":"INBOX","from":{"id":"pzl_u_222","name":"Lan"},"attachments":[]}]}}
        """));
        Assert.Equal(2, sk.Count);
        Assert.All(sk, x => Assert.Equal("pzl_g_1_2", x.ExternalUserId));
        // Nhãn dòng là tên NHÓM, không phải tên người vừa nhắn.
        Assert.All(sk, x => Assert.Equal("SUPPORT TOURKIT - HAPPY TRIP", x.DisplayName));
    }

    /// <summary>
    /// Pancake trả <c>message</c> đã DỰNG HTML. Đo trên dữ liệu thật 05/11/2026: 30/30 tin có thẻ,
    /// câu "đúng rồi" về thành <c>&lt;div&gt;đúng rồi&lt;/div&gt;</c>. Đọc <c>message</c> trước
    /// <c>original_message</c> là hộp thư hiện nguyên thẻ cho nhân viên, và trợ lý học theo đó.
    /// </summary>
    [Fact]
    public void Uu_tien_chu_tho_hon_ban_dung_HTML()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
          "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},"attachments":[],
                     "message":"<div>đúng rồi</div>","original_message":"đúng rồi"}}}
        """)));
        Assert.Equal("đúng rồi", sk.Text);
    }

    /// Không có chữ thô thì phải bóc thẻ, chứ không ghi nguyên HTML vào hộp thư.
    [Fact]
    public void Khong_co_chu_tho_thi_boc_the()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
          "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},"attachments":[],
                     "message":"<div>đúng rồi</div>"}}}
        """)));
        Assert.Equal("đúng rồi", sk.Text);
    }

    [Theory]
    [InlineData("<div>a</div><div>b</div>", "a\nb")]           // mỗi div một dòng
    [InlineData("dòng 1<br>dòng 2", "dòng 1\ndòng 2")]
    [InlineData("<p>x</p>", "x")]
    [InlineData("gi&aacute; 5 &amp; 7", "giá 5 & 7")]          // giải mã thực thể
    [InlineData("chữ trần không thẻ", "chữ trần không thẻ")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Boc_the_dung_cach(string? vao, string? mong)
        => Assert.Equal(mong, PancakeChatAdapter.BoThe(vao));

    /// <summary>
    /// Gói tin KHÔNG có cờ "của khách hay của trang" — phải so id người gửi với id trang. Bỏ bước
    /// này thì tin nhân viên trả lời từ chính app Pancake quay về thành tin của khách, và trợ lý
    /// trả lời đè lên người thật.
    /// </summary>
    [Fact]
    public void Tin_do_trang_gui_la_tieng_vong_va_van_thuoc_dung_hoi_thoai()
    {
        var sk = Assert.Single(Bo().Parse(Goi(fromId: Page)));
        Assert.True(sk.IsEcho);
        // Tiếng vọng phải nằm ĐÚNG hội thoại đó, không mang id trang đi đâu cả.
        Assert.Equal("conv-1", sk.ExternalUserId);
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
    /// Gói LỊCH SỬ mang cả trang tin (<c>data.messages</c>) thay vì một tin (<c>data.message</c>),
    /// và phải đi qua ĐÚNG đường đọc của webhook. Tách hai bộ đọc thì tin cũ và tin mới ra kết quả
    /// khác nhau — cùng một hội thoại hiện hai kiểu tuỳ nó tới bằng đường nào.
    /// </summary>
    [Fact]
    public void Goi_lich_su_nhieu_tin_ra_dung_tung_tin()
    {
        var sk = Bo().Parse($$"""
        {
          "page_id": "{{Page}}", "event_type": "messaging",
          "data": {
            "conversation": { "id": "conv-1", "type": "INBOX",
                              "from": { "id": "{{Khach}}", "name": "Jineo" } },
            "messages": [
              { "id": "m1", "message": "tin cua khach", "type": "INBOX",
                "inserted_at": "2024-12-25T11:06:07.000000",
                "from": { "id": "{{Khach}}", "name": "Jineo" }, "attachments": [] },
              { "id": "m2", "message": "nhan vien tra loi", "type": "INBOX",
                "inserted_at": "2024-12-25T11:07:00.000000",
                "from": { "id": "{{Page}}", "name": "nF store VN" }, "attachments": [] }
            ]
          }
        }
        """);

        Assert.Equal(2, sk.Count);
        Assert.Equal("m1", sk[0].ExternalMsgId);
        Assert.False(sk[0].IsEcho);
        // Tin của trang trong gói lịch sử vẫn phải là TIẾNG VỌNG — không thì trợ lý đọc lại lịch sử
        // rồi tưởng khách hỏi, trả lời đè lên câu nhân viên đã gửi.
        Assert.True(sk[1].IsEcho);
        // Cả hai tin thuộc CÙNG một hội thoại, dù người gửi khác nhau.
        Assert.All(sk, x => Assert.Equal("conv-1", x.ExternalUserId));
    }

    /// Một tin hỏng trong trang không được làm mất những tin còn lại.
    [Fact]
    public void Tin_hong_giua_trang_khong_nuot_tin_con_lai()
    {
        var sk = Bo().Parse($$"""
        {
          "page_id": "{{Page}}", "event_type": "messaging",
          "data": {
            "conversation": { "id": "c", "type": "INBOX",
                              "from": { "id": "{{Khach}}", "name": "J" } },
            "messages": [
              { "id": "m1", "attachments": [] },
              { "id": "m2", "message": "con nguyen", "type": "INBOX",
                "from": { "id": "{{Khach}}" }, "attachments": [] }
            ]
          }
        }
        """);
        Assert.Equal("m2", Assert.Single(sk).ExternalMsgId);
    }

    [Fact]
    public void Mang_tin_rong_thi_khong_sinh_gi()
        => Assert.Empty(Bo().Parse(
            "{\"page_id\":\"" + Page + "\",\"event_type\":\"messaging\",\"data\":{\"messages\":[]}}"));

    /// <summary>
    /// Tên trang phải khớp ĐÚNG mã trang. Phần lớn <c>from</c> trong các thân này là KHÁCH — lấy
    /// bừa <c>name</c> đầu tiên gặp được là đặt tên trang bằng tên một khách hàng.
    /// </summary>
    [Fact]
    public void Doc_ten_trang_tu_bai_dang_cua_chinh_trang()
        => Assert.Equal("nF store VN", PancakeChatAdapter.DocTenTrang(
            "{\"posts\":["
            + "{\"id\":\"p1\",\"from\":{\"id\":\"" + Khach + "\",\"name\":\"Khach La\"}},"
            + "{\"id\":\"p2\",\"from\":{\"id\":\"" + Page + "\",\"name\":\"nF store VN\"}}]}", Page));

    [Fact]
    public void Doc_ten_trang_tu_hoi_thoai()
        => Assert.Equal("Ho tro ki thuat", PancakeChatAdapter.DocTenTrang(
            "{\"conversations\":[{\"id\":\"c1\",\"from\":{\"id\":\"" + Page
            + "\",\"name\":\"Ho tro ki thuat\"}}]}", Page));

    /// Không có object nào mang đúng mã trang → null, để người khai tự gõ. Bịa tên là để lại một
    /// nhãn sai trong hộp thư mà sau không ai truy được nguồn.
    [Theory]
    [InlineData("""{"posts":[{"id":"p1","from":{"id":"nguoi-khac","name":"Ai Do"}}]}""")]
    [InlineData("""{"posts":[]}""")]
    [InlineData("{}")]
    [InlineData("khong phai json")]
    [InlineData("")]
    [InlineData(null)]
    public void Khong_doan_bua_ten_trang(string? json)
        => Assert.Null(PancakeChatAdapter.DocTenTrang(json, Page));

    /// Trang có mã trùng nhưng KHÔNG có tên thì vẫn phải trả null, không trả chuỗi rỗng.
    [Fact]
    public void Ma_trung_ma_khong_co_ten_thi_null()
        => Assert.Null(PancakeChatAdapter.DocTenTrang(
            "{\"posts\":[{\"from\":{\"id\":\"" + Page + "\"}}]}", Page));

    /// Pancake là kênh có đường lấy lại hội thoại cũ — hỏi bằng ChatHistoryImport.Supports, không phải
    /// Supports (vốn chỉ nói về bộ kéo Meta).
    [Fact]
    public void Pancake_co_duong_lay_hoi_thoai_cu()
    {
        Assert.True(ChatHistoryImport.Supports(ChatChannel.Pancake));
        Assert.False(MetaHistoryImporter.Supports(ChatChannel.Pancake));
        Assert.True(ChatHistoryImport.Supports(ChatChannel.Messenger));
        Assert.False(ChatHistoryImport.Supports(ChatChannel.Telegram));
    }

    /// <summary>
    /// <b>Gói lịch sử phải mang cờ IsHistory.</b> Thiếu nó, lõi coi tin ba năm trước là tin VỪA
    /// TỚI: trợ lý sinh một câu trả lời cho từng tin rồi gửi thẳng cho khách hôm nay, bot câm 30
    /// phút, và cả năm hội thoại đóng dấu giờ nhập nên dồn vào một phút.
    ///
    /// <para>Đã xảy ra thật 05/11/2026 — hàng đợi gửi bắt đầu đẩy câu trả lời cho tin cũ, chỉ
    /// không tới tay khách vì lệnh gửi lúc đó đang hỏng vì lý do khác. Thoát nạn nhờ may.</para>
    /// </summary>
    [Fact]
    public void Goi_lich_su_phai_mang_co_IsHistory()
    {
        var lichSu = Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","tourkit_lich_su":true,"data":{
          "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
          "messages":[{"id":"m1","message":"cu","type":"INBOX","from":{"id":"KHACH"},"attachments":[]}]}}
        """));
        Assert.True(Assert.Single(lichSu).IsHistory);

        // Webhook thật KHÔNG có cờ → phải là tin sống, nếu không trợ lý sẽ im với khách đang chờ.
        Assert.False(Assert.Single(Bo().Parse(Goi())).IsHistory);
    }

    /// <summary>
    /// <c>action</c> là trường BẮT BUỘC của lệnh gửi Pancake. Thiếu nó, Pancake trả HTTP 200 kèm
    /// <c>Missing required field: 'action'</c> và tin KHÔNG tới khách — gặp thật 05/11/2026, ba
    /// dòng hàng đợi hỏng liên tiếp trước khi ai đó để ý.
    /// </summary>
    [Fact]
    public void Lenh_gui_phai_kem_action_reply_inbox()
    {
        var than = System.Text.Json.JsonSerializer.Serialize(
            PancakeChatAdapter.ThanGuiChu("xin chao"));
        Assert.Contains("\"action\":\"reply_inbox\"", than);
        Assert.Contains("\"message\":\"xin chao\"", than);
    }

    /// Gửi tệp cũng là một lệnh gửi — thiếu action thì hỏng y hệt.
    [Fact]
    public void Lenh_gui_tep_cung_kem_action()
    {
        var than = System.Text.Json.JsonSerializer.Serialize(
            PancakeChatAdapter.ThanGuiTep("https://x/a.png", "anh mau"));
        Assert.Contains("\"action\":\"reply_inbox\"", than);
        Assert.Contains("anh mau", than);
        Assert.Contains("https://x/a.png", than);
    }

    /// Không có chú thích thì gửi mỗi đường dẫn, không gửi chuỗi rỗng kèm xuống dòng.
    [Fact]
    public void Gui_tep_khong_chu_thich_thi_chi_co_duong_dan()
        => Assert.Equal("https://x/a.png",
            PancakeChatAdapter.ThanGuiTep("https://x/a.png", null).Message);

    /// <summary>
    /// Đính kèm Pancake có thể mang phần tử KHÔNG phải object, hoặc <c>image</c> là chuỗi. Đánh
    /// chỉ mục lên một <c>JsonValue</c> thì .NET ném <c>InvalidOperationException</c>, mà hàm này
    /// chạy lúc dựng màn hình hội thoại — nên lỗi hiện ra thành <b>GET conversations/{id} → 500</b>,
    /// nhân viên bấm vào hội thoại là trang trắng. Gặp thật 05/11/2026.
    /// </summary>
    [Theory]
    [InlineData("""["chi la chuoi"]""")]
    [InlineData("""[{"type":"photo","image":"https://x/a.png"}]""")]
    [InlineData("""[null]""")]
    [InlineData("""[123]""")]
    public void Dinh_kem_la_du_lieu_la_thi_khong_nem(string json)
        => ChatAttachment.Read(ChatChannel.Pancake, ChatKind.Image, json, chieu: 0);

    /// <summary>
    /// Tin chiều RA mang đính kèm hình dạng của KÊNH (mảng), không phải object do mình dựng — xảy
    /// ra với tin "tiếng vọng": nhân viên trả lời từ app Pancake, hoặc tin cũ kéo về. Ép
    /// <c>AsObject()</c> lên mảng thì ném, và vì hàm này chạy lúc dựng màn hình hội thoại, lỗi
    /// hiện ra thành <b>GET conversations/{id} → 500</b> — bấm vào hội thoại là trang trắng.
    /// Gặp thật 05/11/2026.
    /// </summary>
    [Fact]
    public void Tin_chieu_ra_mang_dinh_kem_hinh_dang_kenh_thi_van_doc_duoc()
        => Assert.Equal("https://x/a.png", Assert.Single(ChatAttachment.Read(
            ChatChannel.Pancake, ChatKind.Image,
            """[{"type":"photo","image":{"url":"https://x/a.png"}}]""", chieu: 1)).Url);

    /// Tin chiều ra do CHÍNH MÌNH dựng vẫn phải đọc theo đường cũ — đừng chữa lỗi trên bằng cách
    /// làm hỏng đường thường.
    [Fact]
    public void Tin_chieu_ra_do_minh_dung_van_doc_nhu_cu()
    {
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Pancake, ChatKind.File,
            """{"ten":"bao-gia.pdf","kich":2048,"url":"https://x/bao-gia.pdf"}""", chieu: 1));
        Assert.Equal("bao-gia.pdf", f.Name);
        Assert.Equal(2048, f.Size);
    }

    /// <summary>
    /// <c>attachments[]</c> của Pancake KHÔNG chỉ chứa tệp. Đo trên dữ liệu thật 05/11/2026 trong
    /// 15 loại: <c>replied_message</c> 119 lượt, <c>reaction</c> 82, <c>system_message</c> 34,
    /// <c>styled_message</c> 9 — quá nửa số lượt không phải tệp.
    ///
    /// <para>Không lọc thì tin chữ bình thường có kèm <c>styled_message</c> (chỉ là chữ in đậm)
    /// bị đoán thành tin TỆP, và hộp thư hiện một đính kèm rỗng không bấm được.</para>
    /// </summary>
    [Theory]
    [InlineData("replied_message")]
    [InlineData("reaction")]
    [InlineData("system_message")]
    [InlineData("zalo_system_message")]
    [InlineData("styled_message")]
    [InlineData("ad_click")]
    [InlineData("template")]
    [InlineData("pzl_chat_recommended")]
    public void Dinh_kem_khong_phai_tep_thi_bo_qua(string loai)
        => Assert.Empty(ChatAttachment.Read(ChatChannel.Pancake, ChatKind.File,
            $"[{{\"type\":\"{loai}\",\"url\":\"https://x/a.bin\"}}]", chieu: 0));

    /// Tin chữ kèm styled_message phải ra TIN CHỮ, không phải tin tệp.
    [Fact]
    public void Chu_in_dam_van_la_tin_chu()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
          "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},"original_message":"xin chao",
                     "attachments":[{"type":"styled_message","styles":[]}]}}}
        """)));
        Assert.Equal(ChatKind.Text, sk.Kind);
        Assert.Null(sk.AttachmentJson);
    }

    /// Tin CHỈ có một lượt thả cảm xúc thì bỏ hẳn — ghi vào là một dòng trắng trong hội thoại.
    [Fact]
    public void Tin_chi_co_tha_cam_xuc_thi_bo_han()
        => Assert.Empty(Bo().Parse(Thay("""
           {"page_id":"PAGE","event_type":"messaging","data":{
             "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
             "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},
                        "attachments":[{"type":"reaction","emoji":"❤"}]}}}
           """)));

    /// <summary>
    /// Đường dẫn tệp nằm ở BỐN chỗ tuỳ loại. Ảnh Zalo để ở <c>url</c> — đọc mỗi
    /// <c>image.url</c> (bản đầu) là mất sạch 106 lượt ảnh trong dữ liệu thật, hộp thư hiện
    /// "(không có chữ)" thay cho tấm ảnh khách gửi.
    /// </summary>
    [Theory]
    [InlineData("""[{"type":"photo","url":"https://z/a.jpg","origin_url":"https://z/b.jpg"}]""", "https://z/a.jpg")]
    [InlineData("""[{"type":"photo","origin_url":"https://z/b.jpg"}]""", "https://z/b.jpg")]
    [InlineData("""[{"type":"sticker","link":"https://f/s.png"}]""", "https://f/s.png")]
    [InlineData("""[{"type":"document","url":"https://p/x.pdf","name":"bao-gia.pdf"}]""", "https://p/x.pdf")]
    [InlineData("""[{"type":"photo","image":{"url":"https://m/c.png"}}]""", "https://m/c.png")]
    [InlineData("""[{"type":"share","media":{"image":{"src":"https://e/d.jpg"}}}]""", "https://e/d.jpg")]
    public void Doc_duong_dan_tep_o_moi_cho(string json, string mong)
        => Assert.Equal(mong, Assert.Single(
            ChatAttachment.Read(ChatChannel.Pancake, ChatKind.Image, json, chieu: 0)).Url);

    /// Tên tệp lấy từ `name` (document có tên thật), rồi mới tới title/description.
    [Fact]
    public void Lay_dung_ten_tep()
        => Assert.Equal("bao-gia.pdf", Assert.Single(ChatAttachment.Read(
            ChatChannel.Pancake, ChatKind.File,
            """[{"type":"document","url":"https://p/x.pdf","name":"bao-gia.pdf"}]""", chieu: 0)).Name);

    /// <summary>
    /// <c>thumb_url</c> của document thật trong dữ liệu là chuỗi cụt <c>"https:"</c>. Nhận vào
    /// là hộp thư hiện một nút bấm chết.
    /// </summary>
    [Theory]
    [InlineData("""[{"type":"photo","url":"https:"}]""")]
    [InlineData("""[{"type":"photo","url":""}]""")]
    [InlineData("""[{"type":"photo","url":"khong-phai-http"}]""")]
    public void Duong_dan_cut_thi_khong_tinh_la_tep(string json)
        => Assert.Empty(ChatAttachment.Read(ChatChannel.Pancake, ChatKind.Image, json, chieu: 0));

    /// <summary>
    /// Hội thoại NHÓM: mỗi tin một thành viên khác nhau, phải giữ tên riêng của người gửi. Thiếu
    /// thì cả nhóm hiện cùng một tên và cùng một chữ cái đại diện — nhân viên đọc lại không biết
    /// ai nói câu nào.
    /// </summary>
    [Fact]
    public void Nhom_giu_ten_tung_nguoi_gui()
    {
        var sk = Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"g1","type":"INBOX","from":{"id":"pzl_g_2","name":"SUPPORT TOURKIT"}},
          "messages":[
            {"id":"a","original_message":"cau mot","type":"INBOX","attachments":[],
             "from":{"id":"pzl_u_1","name":"Trần Đức Duy"}},
            {"id":"b","original_message":"cau hai","type":"INBOX","attachments":[],
             "from":{"id":"pzl_u_2","displayName":"Ms Lan"}}]}}
        """));
        Assert.Equal("Trần Đức Duy", sk[0].SenderName);
        Assert.Equal("Ms Lan", sk[1].SenderName);
        // Nhãn cả dòng vẫn là tên NHÓM.
        Assert.All(sk, x => Assert.Equal("SUPPORT TOURKIT", x.DisplayName));
    }

    /// Hội thoại MỘT-MỘT: người gửi chính là khách, nhắc lại tên dưới mỗi tin là thừa.
    [Fact]
    public void Mot_mot_thi_khong_ghi_ten_nguoi_gui()
        => Assert.Null(Assert.Single(Bo().Parse(Thay("""
           {"page_id":"PAGE","event_type":"messaging","data":{
             "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH","name":"Chị Hoa"}},
             "message":{"id":"m","original_message":"alo","type":"INBOX","attachments":[],
                        "from":{"id":"KHACH","name":"Chị Hoa"}}}}
           """))).SenderName);

    /// <summary>
    /// Nhắc tên: <c>original_message</c> chỉ mang MÃ (<c>@6043324489577600945</c>), tên thật nằm
    /// ở <c>message_tags</c>. Không dịch thì hộp thư hiện một dãy 19 chữ số giữa câu, chẳng ai
    /// biết đang gọi ai. Chuỗi dưới lấy nguyên từ dữ liệu thật 05/11/2026.
    /// </summary>
    [Fact]
    public void Doi_ma_nhac_ten_thanh_ten_that()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"g1","type":"INBOX","from":{"id":"g","name":"Nhom"}},
          "message":{"id":"m","type":"INBOX","attachments":[],"from":{"id":"KHACH","name":"Kế Toán"},
            "original_message":"Công nợ mới, chị @6043324489577600945 làm hồ sơ giúp em nhé",
            "message_tags":[{"id":"6043324489577600945","len":9,"name":"@Hoai Thu","pos":44,
                             "type":"pzl_mention"}]}}}
        """)));
        Assert.Equal("Công nợ mới, chị @Hoai Thu làm hồ sơ giúp em nhé", sk.Text);
        // KHÔNG được ra "@@Hoai Thu": `name` của Pancake đã kèm sẵn dấu @.
        Assert.DoesNotContain("@@", sk.Text!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("khong co nhac ten", "khong co nhac ten")]
    public void Khong_co_the_thi_giu_nguyen_chu(string? chu, string? mong)
        => Assert.Equal(mong, PancakeChatAdapter.ThayNhacTen(chu, null));

    /// Nhiều người được nhắc trong một câu thì phải dịch hết.
    [Fact]
    public void Doi_duoc_nhieu_ma_trong_mot_cau()
        => Assert.Equal("@An và @Binh xem giúp",
            PancakeChatAdapter.ThayNhacTen("@111 và @222 xem giúp",
                System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"id":"111","name":"@An"},{"id":"222","name":"@Binh"}]""")!.AsArray()));

    /// Thẻ thiếu id hoặc thiếu tên thì bỏ qua, không làm hỏng phần chữ còn lại.
    [Fact]
    public void The_hong_thi_bo_qua()
        => Assert.Equal("@111 con nguyen",
            PancakeChatAdapter.ThayNhacTen("@111 con nguyen",
                System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"id":"111"},{"name":"@Thieu id"},"rac"]""")!.AsArray()));

    private static JsonObject Att(string json)
        => System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// Thông báo nền tảng ("X đã rời khỏi nhóm") có phần chữ RỖNG — không tự dựng câu thì tin bị
    /// bỏ hẳn và hội thoại mất dấu vết ai vào ai ra, trong khi bên Pancake vẫn thấy.
    /// Các chuỗi dưới lấy nguyên từ dữ liệu thật 05/11/2026.
    /// </summary>
    [Fact]
    public void Dung_cau_cho_thong_bao_roi_nhom()
        => Assert.Equal("Trà My đã rời khỏi nhóm", PancakeChatAdapter.MoTaThongBao(Att("""
           {"type":"zalo_system_message","data":{"type":"msg_member_leave_group",
            "implementer":{"id":"305251965292323411","name":"Trà My"},
            "update_members":[{"dName":"Trà My","id":"305251965292323411"}]}}
           """)));

    [Fact]
    public void Dung_cau_cho_moi_ra_khoi_nhom()
        => Assert.Equal("Huynhphuong đã mời Kim Quỳnh ra khỏi nhóm",
            PancakeChatAdapter.MoTaThongBao(Att("""
            {"type":"zalo_system_message","data":{"type":"remove_member",
             "implementer":{"name":"Huynhphuong"},
             "update_members":[{"dName":"Kim Quỳnh"}]}}
            """)));

    [Fact]
    public void Dung_cau_cho_them_vao_nhom()
        => Assert.Equal("Phạm Minh Tourkit đã thêm Hỗ Trợ Kt Tourkit vào nhóm",
            PancakeChatAdapter.MoTaThongBao(Att("""
            {"type":"zalo_system_message","data":{"type":"members_join_group",
             "implementer":{"name":"Phạm Minh Tourkit"},
             "update_members":[{"dName":"Hỗ Trợ Kt Tourkit"}]}}
            """)));

    /// Pancake cho sẵn chữ ở dạng đơn giản — dùng thẳng.
    [Fact]
    public void Thong_bao_co_san_chu_thi_dung_thang()
        => Assert.Equal("Tác nhân AI sẽ phản hồi.", PancakeChatAdapter.MoTaThongBao(
            Att("""{"type":"system_message","message":"Tác nhân AI sẽ phản hồi."}""")));

    /// Việc chưa biết thì trả null — thà bỏ còn hơn ghi một dòng trống không ai hiểu.
    [Theory]
    [InlineData("""{"type":"zalo_system_message","data":{"type":"unsupported_message"}}""")]
    [InlineData("""{"type":"zalo_system_message","data":{"type":"viec_la_hoac_moi"}}""")]
    [InlineData("""{"type":"photo","url":"https://x/a.jpg"}""")]
    public void Khong_dung_noi_cau_thi_tra_null(string json)
        => Assert.Null(PancakeChatAdapter.MoTaThongBao(Att(json)));

    /// <summary>
    /// Thông báo nền tảng phải ghi là HỆ THỐNG, không phải lời khách: coi là tin khách thì hội
    /// thoại nhảy lên đầu, tính chưa đọc, và trợ lý đi trả lời một câu không ai hỏi.
    /// </summary>
    [Fact]
    public void Thong_bao_vao_hop_thu_duoi_danh_nghia_he_thong()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"g1","type":"INBOX","from":{"id":"g","name":"Nhom"}},
          "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},"original_message":"",
            "attachments":[{"type":"zalo_system_message","data":{"type":"msg_member_leave_group",
              "implementer":{"name":"Huyền Tourkit"},
              "update_members":[{"dName":"Huyền Tourkit"}]}}]}}}
        """)));
        Assert.True(sk.IsSystemNotice);
        Assert.Equal("Huyền Tourkit đã rời khỏi nhóm", sk.Text);
        Assert.Null(sk.SenderName);   // không phải lời của ai cả
    }

    /// <summary>
    /// Thẻ ngân hàng Zalo: số tài khoản là thứ nhân viên cần đọc và chép, mã QR là thứ khách cần
    /// quét. Lọc bỏ cả cụm (bản đầu) thì hộp thư chỉ còn một dòng trắng.
    /// </summary>
    [Fact]
    public void The_ngan_hang_ra_chu_va_ma_QR()
    {
        var sk = Assert.Single(Bo().Parse(Thay("""
        {"page_id":"PAGE","event_type":"messaging","data":{
          "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
          "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},"original_message":"",
            "attachments":[{"type":"chat.webcontent","action":"zinstant.bankcard","bank_card":{
              "bank_name":"Ngân hàng VPBank","bank_number":"461493637",
              "qr_code":"https://group-qr.zdn.vn/d/1/220/abc"}}]}}}
        """)));
        Assert.Equal("Ngân hàng VPBank · 461493637", sk.Text);

        var tep = Assert.Single(ChatAttachment.Read(ChatChannel.Pancake, sk.Kind,
            sk.AttachmentJson!, chieu: 0));
        Assert.Equal("https://group-qr.zdn.vn/d/1/220/abc", tep.Url);
        Assert.Equal("Mã QR chuyển khoản", tep.Name);
    }

    /// Có chữ thật thì chữ đó là nội dung chính — đừng để thẻ/thông báo đè lên.
    [Fact]
    public void Co_chu_that_thi_khong_de_len()
        => Assert.Equal("chuyển khoản giúp em nhé", Assert.Single(Bo().Parse(Thay("""
           {"page_id":"PAGE","event_type":"messaging","data":{
             "conversation":{"id":"c1","type":"INBOX","from":{"id":"KHACH"}},
             "message":{"id":"m","type":"INBOX","from":{"id":"KHACH"},
               "original_message":"chuyển khoản giúp em nhé",
               "attachments":[{"type":"chat.webcontent","bank_card":{
                 "bank_name":"VPBank","bank_number":"1","qr_code":"https://q/1"}}]}}}
           """))).Text);

    /// Phần tử hỏng nằm GIỮA không được nuốt mất phần tử tốt đứng sau.
    [Fact]
    public void Dinh_kem_hong_khong_nuot_cai_con_lai()
        => Assert.Equal("https://x/a.png", Assert.Single(ChatAttachment.Read(
            ChatChannel.Pancake, ChatKind.Image,
            """["rac", {"type":"photo","image":{"url":"https://x/a.png"}}]""", chieu: 0)).Url);

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
