using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nối Trang Facebook bằng MỘT nút — ứng dụng Meta cấp nền tảng, cùng lối đã làm cho Zalo OA.
///
/// <para>Meta đăng ký webhook theo <b>ứng dụng</b> chứ không theo Trang, nên mọi khách hàng dùng
/// chung một địa chỉ và khoá định tuyến duy nhất còn lại là <b>id Trang</b> ở <c>entry[].id</c>.</para>
///
/// <para>Phần lớn cụm này gọi ra Meta thật nên không có test tích hợp. Những gì kiểm được ở đây là
/// <b>logic thuần</b> (bóc id Trang, kho chọn Trang) và <b>các quyết định dễ bị sửa ngược</b> —
/// thứ tự đổi token, danh sách quyền, danh sách sự kiện đăng ký.</para>
/// </summary>
public class MessengerConnectTests
{
    private const string TepAdapter = "TourkitAiProxy.Services/Chat/Channels/MessengerChatAdapter.cs";

    // ── Bóc id Trang ────────────────────────────────────────────────────────

    [Fact]
    public void Tin_khach_gui_lay_duoc_id_Trang()
    {
        var tho = """
            {"object":"page","entry":[{"id":"trang-777","time":1,
             "messaging":[{"sender":{"id":"khach-1"},"recipient":{"id":"trang-777"},
             "timestamp":1,"message":{"mid":"m1","text":"chào"}}]}]}
            """;
        Assert.Equal("trang-777", MessengerChatAdapter.IdTrangCuaSuKien(tho));
    }

    [Fact]
    public void Tieng_vong_va_bao_da_doc_cung_lay_duoc_id_Trang()
    {
        // Khác Zalo: Meta để id Trang ở entry[].id cho MỌI loại sự kiện, hai đầu sender/recipient
        // đảo ngược thế nào cũng không ảnh hưởng. Test này chốt điều đó, để ai nhìn thấy
        // sender/recipient đảo chiều không đi "sửa" IdTrangCuaSuKien theo kiểu Zalo.
        var vong = """
            {"object":"page","entry":[{"id":"trang-777","messaging":[
             {"sender":{"id":"trang-777"},"recipient":{"id":"khach-1"},
              "message":{"mid":"m2","text":"vâng","is_echo":true}}]}]}
            """;
        var daDoc = """
            {"object":"page","entry":[{"id":"trang-777","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-777"},
              "read":{"watermark":1700000000000}}]}]}
            """;
        Assert.Equal("trang-777", MessengerChatAdapter.IdTrangCuaSuKien(vong));
        Assert.Equal("trang-777", MessengerChatAdapter.IdTrangCuaSuKien(daDoc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("không phải json")]
    [InlineData("""{"object":"page"}""")]
    [InlineData("""{"object":"page","entry":[]}""")]
    [InlineData("""{"object":"page","entry":[{"time":1}]}""")]
    public void Goi_khong_co_id_Trang_thi_tra_null(string tho)
    {
        // Meta gọi thử địa chỉ bằng gói rỗng lúc lưu. Ném ở đây là webhook trả 500 và Meta ngừng
        // gửi cho CẢ ứng dụng — tức là mọi khách hàng mất kênh, không riêng ai.
        Assert.Null(MessengerChatAdapter.IdTrangCuaSuKien(tho));
    }

    // ── Kho Trang chờ chọn ──────────────────────────────────────────────────

    [Fact]
    public void Chi_noi_duoc_Trang_NAM_TRONG_danh_sach()
    {
        // Chốt chặn an ninh của trang chọn Trang: nó CÔNG KHAI (Meta đá về bằng chuyển hướng
        // thường, không mang phiên). Không kiểm id Trang có trong danh sách thì ai cầm mã cũng nối
        // được Trang bất kỳ chỉ bằng cách đoán một id — mà id Trang thì ai cũng đọc được.
        var kho = new MessengerPageChoices();
        var ma = kho.Tao("cty-1", new[] { new TrangUngVien("trang-1", "Chi nhánh Q1", "tok-1") });

        Assert.NotNull(kho.Nhan(ma, "trang-1"));
        Assert.Null(kho.Nhan(ma, "trang-9"));
        Assert.Null(kho.Nhan(ma, ""));
        Assert.Null(kho.Nhan("mã-bịa", "trang-1"));
    }

    [Fact]
    public void Chon_duoc_NHIEU_Trang_trong_mot_luot()
    {
        // Cố ý KHÔNG dùng một lần, khác ChatOAuthStates: công ty nhiều chi nhánh nối vài Trang liền
        // tay. Bắt đăng nhập Facebook lại từ đầu cho mỗi Trang là hành người dùng.
        var kho = new MessengerPageChoices();
        var ma = kho.Tao("cty-1", new[]
        {
            new TrangUngVien("trang-1", "Q1", "tok-1"),
            new TrangUngVien("trang-2", "Q3", "tok-2"),
        });

        Assert.Equal("tok-1", kho.Nhan(ma, "trang-1")!.Value.Trang.AccessToken);
        Assert.Equal("tok-2", kho.Nhan(ma, "trang-2")!.Value.Trang.AccessToken);
        Assert.Equal(2, kho.Xem(ma)!.Value.Trang.Count);
    }

    [Fact]
    public void Kho_tra_ve_dung_cong_ty()
    {
        // Lấy nhầm tenant là lưu khoá Trang của công ty này vào công ty khác — từ đó họ đọc và trả
        // lời tin của khách bên kia.
        var kho = new MessengerPageChoices();
        var a = kho.Tao("cty-a", new[] { new TrangUngVien("t", "T", "k") });
        var b = kho.Tao("cty-b", new[] { new TrangUngVien("t", "T", "k") });
        Assert.Equal("cty-a", kho.Nhan(a, "t")!.Value.TenantId);
        Assert.Equal("cty-b", kho.Nhan(b, "t")!.Value.TenantId);
    }

    // ── Những quyết định dễ bị sửa ngược ────────────────────────────────────

    [Fact]
    public void Quyen_du_de_TU_bat_nhan_tin_cho_Trang()
    {
        // pages_manage_metadata là quyền gọi subscribed_apps — thứ biến cả bước nối thành một nút.
        // Bỏ nó đi thì luồng vẫn "thành công", chỉ là Trang không bao giờ gửi tin về, và không có
        // thông báo lỗi nào chỉ vào đây.
        Assert.Contains("pages_manage_metadata", MessengerChatAdapter.Quyen);
        Assert.Contains("pages_messaging", MessengerChatAdapter.Quyen);
        Assert.Contains("pages_show_list", MessengerChatAdapter.Quyen);
    }

    [Fact]
    public void Khong_xin_quyen_thua()
    {
        // Mỗi quyền thừa là một mục phải giải trình khi Meta duyệt ứng dụng, và một dòng đáng ngờ
        // trên màn hình khách bấm đồng ý. Dự án tham khảo xin 12 quyền vì họ còn quản bài đăng và
        // quảng cáo — mình không làm những việc đó.
        //
        // ⚠️ Test này TỪNG cấm cả business_management, và cấm sai. Thực tế 26/08/2026: Facebook cấp
        // pages_show_list bình thường nhưng /me/accounts trả RỖNG vì Trang do một Danh mục doanh
        // nghiệp sở hữu. "Ít quyền cho nhẹ khâu duyệt" là ý hay cho tới lúc nó chặn cả tính năng.
        Assert.DoesNotContain("pages_manage_posts", MessengerChatAdapter.Quyen);
        Assert.DoesNotContain("pages_manage_ads", MessengerChatAdapter.Quyen);
        Assert.DoesNotContain("pages_manage_engagement", MessengerChatAdapter.Quyen);
        Assert.True(MessengerChatAdapter.Quyen.Length <= 6,
            "Xin thêm quyền thì cân nhắc lại: mỗi cái là một mục Meta bắt giải trình.");
    }

    [Fact]
    public void Van_xin_business_management()
    {
        // Bỏ lại là lặp đúng lỗi ngày 26/08/2026: Facebook báo cấp quyền thành công, màn hình đồng ý
        // trông bình thường, mà danh sách Trang rỗng và KHÔNG có thông báo lỗi nào chỉ vào đây.
        // Tài liệu Messenger của Meta ghi nó là phụ thuộc của pages_show_list và pages_messaging.
        Assert.Contains("business_management", MessengerChatAdapter.Quyen);
    }

    [Fact]
    public void Doi_token_DAI_han_TRUOC_khi_lay_danh_sach_Trang()
    {
        // Page token lấy ra từ user token NGẮN hạn cũng chỉ sống vài giờ; lấy ra từ user token DÀI
        // hạn thì không hết hạn. Làm ngược thứ tự là vài giờ sau cả hộp thư ngừng gửi được, mà lỗi
        // Meta trả về chỉ nói "session expired" — không ai đoán ra nguyên nhân nằm ở thứ tự này.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        // Tìm theo chuỗi DỰNG URL chứ không theo tên trần: cả hai tên đều xuất hiện trong chú
        // thích ở trên, và chú thích thì không nói lên thứ tự chạy thật.
        var doiDai = src.IndexOf("grant_type=fb_exchange_token", System.StringComparison.Ordinal);
        var layTrang = src.IndexOf("{PhienBan}/me/accounts", System.StringComparison.Ordinal);
        Assert.True(doiDai > 0, "Không thấy bước đổi token dài hạn");
        Assert.True(layTrang > 0, "Không thấy bước lấy danh sách Trang");
        Assert.True(doiDai < layTrang,
            "Phải đổi user token sang bản DÀI hạn TRƯỚC khi gọi /me/accounts.");
    }

    [Fact]
    public void Dang_ky_du_su_kien_ma_Parse_boc()
    {
        // subscribed_fields quyết định Meta gửi loại sự kiện nào. Thiếu message_echoes là mất tin
        // nhân viên trả lời từ ứng dụng Meta; thiếu message_deliveries/message_reads là tin gửi đi
        // không bao giờ leo lên hai tích. Cả hai đều hỏng ÂM THẦM.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var i = src.IndexOf("SuKienTrang =", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy danh sách sự kiện đăng ký");
        var than = src.Substring(i, System.Math.Min(400, src.Length - i));

        foreach (var can in new[] { "messages", "message_echoes", "message_deliveries", "message_reads" })
            Assert.Contains(can, than);
    }

    [Fact]
    public void Duong_cap_quyen_dung_ung_dung_cap_nen_tang()
    {
        // Cả bước nối bắt đầu từ chuỗi này. Thiếu state là mất chốt chặn ghép lại công ty ở callback
        // (đường đó CÔNG KHAI, không mang phiên); thiếu response_type=code là Facebook trả token trên
        // fragment và máy chủ không bao giờ thấy.
        var fb = DungAdapter(new Dictionary<string, string?>
        {
            ["Chat:Messenger:AppId"] = "111",
            ["Chat:Messenger:AppSecret"] = "bí-mật",
        });
        Assert.True(fb.CoUngDungNenTang);

        var url = fb.DuongCapQuyen("https://travelai.vn/api/v1/chat/oauth/messenger/callback", "st-1");
        Assert.StartsWith("https://www.facebook.com/v21.0/dialog/oauth?", url);
        Assert.Contains("client_id=111", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=st-1", url);

        // Thiếu cái này thì xin thêm quyền về sau là vô nghĩa: Facebook nhớ lựa chọn cũ, bỏ qua màn
        // hình đồng ý, và người dùng không bao giờ được hỏi quyền mới. Chữa tay phải vào Facebook gỡ
        // ứng dụng — không khách hàng nào làm nổi. Đã dính thật ngày 26/08/2026.
        Assert.Contains("auth_type=rerequest", url);
        Assert.Contains("pages_manage_metadata", url);
        Assert.Contains(Uri.EscapeDataString("https://travelai.vn/api/v1/chat/oauth/messenger/callback"), url);
    }

    [Fact]
    public void Thieu_khoa_ung_dung_thi_KHONG_bao_la_noi_nhanh_duoc()
    {
        // Giao diện dựa vào cờ này để chọn giữa "một nút" và "khai tay 4 ô". Báo bừa là khách bấm nút
        // rồi rơi vào màn hình lỗi của Facebook, không hiểu vì sao.
        Assert.False(DungAdapter(new Dictionary<string, string?>()).CoUngDungNenTang);
        Assert.False(DungAdapter(new Dictionary<string, string?>
        {
            ["Chat:Messenger:AppId"] = "111",
        }).CoUngDungNenTang);
    }

    [Fact]
    public void Phien_ban_Graph_khong_tu_nhay_theo_ban_moi_cua_Meta()
    {
        // Đổi phiên bản là đổi hành vi của MỌI lệnh gọi Meta cùng lúc. Mặc định phải đứng yên; muốn
        // đổi thì khai Chat:Messenger:Version, một quyết định có chủ ý.
        Assert.Equal("v21.0", DungAdapter(new Dictionary<string, string?>()).PhienBan);
        Assert.Equal("v23.0", DungAdapter(new Dictionary<string, string?>
        {
            ["Chat:Messenger:Version"] = "v23.0",
        }).PhienBan);
    }

    /// <summary>Bộ nối tối thiểu cho các test chỉ bóc gói tin — dùng chung với MessengerEventTests.</summary>
    internal static MessengerChatAdapter DungAdapterCong() => DungAdapter(new Dictionary<string, string?>());

    private static MessengerChatAdapter DungAdapter(Dictionary<string, string?> khoa)
    {
        khoa["ConnectionStrings:PushDb"] = "Server=khong-dung;Database=x;";
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(khoa).Build();
        var db = new TourkitAiProxy.Infrastructure.Db.TourkitAiDb(cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Db.TourkitAiDb>.Instance);
        var cred = new TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore>.Instance);
        // IHttpClientFactory null: mấy phép kiểm ở đây không gọi ra mạng.
        return new MessengerChatAdapter(null!, cred, cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessengerChatAdapter>.Instance);
    }

    [Fact]
    public void Van_kiem_chu_ky_sau_khi_tra_ra_cong_ty()
    {
        // Tra ra công ty bằng id Trang KHÔNG chứng minh tin là thật — id Trang nằm công khai trên
        // chính trang Facebook đó. Bỏ bước kiểm chữ ký là mở cửa cho người ngoài bơm tin vào hộp thư.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var i = src.IndexOf("XacMinhDungChungAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0);
        var than = src.Substring(i, System.Math.Min(1200, src.Length - i));
        Assert.Contains("VerifyAsync(", than);
    }

    [Fact]
    public void Webhook_dung_chung_LUON_tra_200_ke_ca_khi_tu_choi()
    {
        // Nặng hơn ở Zalo: Meta tự động NGỪNG gửi webhook cho ứng dụng nào trả lỗi liên tục. Ứng
        // dụng là dùng chung, nên trả 401 cho tin rác là tắt kênh của MỌI khách hàng cùng lúc.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("MapPost(\"/api/v1/chat/webhook/messenger\"", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy đường webhook dùng chung của Messenger");

        var het = src.IndexOf("MapGet(\"/api/v1/chat/webhook/messenger\"", i, System.StringComparison.Ordinal);
        var than = het > i ? src[i..het] : src[i..];
        Assert.DoesNotContain("Results.Unauthorized()", than);
    }

    [Fact]
    public void Luu_tai_khoan_bang_CHINH_id_Trang()
    {
        // accountId phải là id Trang: webhook dùng chung tra ngược ra công ty bằng đúng id đó. Đặt
        // mã ngẫu nhiên như luồng khai tay là tin của khách không bao giờ tới nơi.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var i = src.IndexOf("NoiTrangAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0);
        var than = src.Substring(i, System.Math.Min(2000, src.Length - i));
        Assert.Contains("SaveAsync(tenantId, Channel, trang.PageId", than);
    }
}
