using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// WhatsApp và TikTok — hai kênh <b>không dùng chung được gì</b> với bốn kênh trước.
///
/// <para>Cả hai đều chưa kiểm bằng tài khoản thật (cần doanh nghiệp đã duyệt), nên bộ test này
/// canh đúng phần <b>thuần</b>: hình dạng gói tin, chữ ký, và những chỗ mà áp nhầm luật của kênh
/// khác sẽ hỏng im lặng.</para>
/// </summary>
public class WhatsAppTikTokTests
{
    private static IConfiguration Cfg(Dictionary<string, string?>? them = null)
    {
        var g = new Dictionary<string, string?>
        {
            ["ConnectionStrings:PushDb"] = "Server=khong-dung;Database=x;",
        };
        foreach (var kv in them ?? new()) g[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(g).Build();
    }

    private static TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore Kho(IConfiguration cfg)
    {
        var db = new TourkitAiProxy.Infrastructure.Db.TourkitAiDb(cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Db.TourkitAiDb>.Instance);
        return new TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore>.Instance);
    }

    private static WhatsAppChatAdapter Wa()
    {
        var cfg = Cfg();
        return new WhatsAppChatAdapter(null!, Kho(cfg), cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WhatsAppChatAdapter>.Instance);
    }

    private static TikTokChatAdapter Tt()
    {
        var cfg = Cfg();
        return new TikTokChatAdapter(null!, Kho(cfg), cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokChatAdapter>.Instance);
    }

    // ── WhatsApp ────────────────────────────────────────────────────────────

    [Fact]
    public void WhatsApp_ghep_ten_khach_tu_contacts_sang_messages()
    {
        // Tên khách nằm ở contacts[], TÁCH khỏi messages[]. Không ghép lại bằng số điện thoại thì
        // hộp thư hiện một dãy số, dù gói tin có sẵn tên.
        var tho = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"value":{
              "metadata":{"phone_number_id":"so-1"},
              "contacts":[{"wa_id":"84900000001","profile":{"name":"Chị Lan"}}],
              "messages":[{"from":"84900000001","id":"wamid-1","timestamp":"1700000000",
                           "type":"text","text":{"body":"Tour Nhật còn chỗ không ạ?"}}]}}]}]}
            """;
        var sk = Assert.Single(Wa().Parse(tho));
        Assert.Equal(ChatChannel.WhatsApp, sk.Channel);
        Assert.Equal("84900000001", sk.ExternalUserId);
        Assert.Equal("Chị Lan", sk.DisplayName);
        Assert.Equal("wamid-1", sk.ExternalMsgId);
    }

    [Fact]
    public void WhatsApp_bao_trang_thai_theo_MA_TIN_khong_theo_moc_nuoc()
    {
        // Messenger báo bằng watermark (mốc thời gian); WhatsApp báo bằng id TỪNG tin. Áp luật của
        // Messenger sang là giá trị ra null và sự kiện rơi im lặng.
        var tho = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"value":{
              "metadata":{"phone_number_id":"so-1"},
              "statuses":[{"id":"wamid-9","status":"read","timestamp":"1700000100",
                           "recipient_id":"84900000001"}]}}]}]}
            """;
        var sk = Assert.Single(Wa().Parse(tho));
        Assert.NotNull(sk.Watermark);
        Assert.Equal(ChatState.Seen, sk.Watermark!.State);
        Assert.Equal("wamid-9", sk.Watermark.ExternalMsgId);
        // Không được là một tin mới trong hội thoại.
        Assert.Null(sk.ExternalMsgId);
        Assert.Null(sk.Text);
    }

    [Fact]
    public void WhatsApp_tin_hong_KHONG_thanh_trang_thai()
    {
        // "failed" không map sang trạng thái nào: luật CanAdvanceState vốn chặn tin đã gửi được thành
        // hỏng, và ghi bừa một trạng thái ở đây là dấu tích chạy ngược.
        var tho = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"value":{
              "metadata":{"phone_number_id":"so-1"},
              "statuses":[{"id":"wamid-9","status":"failed","timestamp":"1700000100",
                           "recipient_id":"84900000001","errors":[{"code":131047}]}]}}]}]}
            """;
        Assert.Empty(Wa().Parse(tho));
    }

    [Fact]
    public void WhatsApp_khach_gui_anh_ra_dung_loai_va_giu_ma_tep()
    {
        var tho = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"value":{
              "metadata":{"phone_number_id":"so-1"},
              "messages":[{"from":"84900000001","id":"wamid-2","timestamp":"1700000000",
                           "type":"image","image":{"id":"media-1","mime_type":"image/jpeg",
                                                   "caption":"Hộ chiếu của em đây"}}]}}]}]}
            """;
        var sk = Assert.Single(Wa().Parse(tho));
        Assert.Equal(ChatKind.Image, sk.Kind);
        Assert.Equal("Hộ chiếu của em đây", sk.Text);

        var tep = Assert.Single(ChatAttachment.Read(ChatChannel.WhatsApp, sk.Kind, sk.AttachmentJson, 0));
        Assert.Equal("media-1", tep.FileId);
        // ⚠️ WhatsApp KHÔNG cho URL — có URL ở đây nghĩa là ai đó vừa nhét đường công khai vào,
        // mà đường tải của họ đòi khoá xác thực nên trình duyệt sẽ nhận 401.
        Assert.Null(tep.Url);
    }

    [Fact]
    public void WhatsApp_loai_chua_ho_tro_thi_bo_qua_chu_khong_ghi_dong_trang()
    {
        var tho = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"value":{
              "metadata":{"phone_number_id":"so-1"},
              "messages":[{"from":"84900000001","id":"wamid-3","timestamp":"1700000000",
                           "type":"order","order":{"catalog_id":"c1"}}]}}]}]}
            """;
        Assert.Empty(Wa().Parse(tho));
    }

    [Fact]
    public void WhatsApp_lay_id_so_o_metadata_khong_phai_entry_id()
    {
        // entry[].id là id WABA, KHÔNG phải id số điện thoại. Lấy nhầm là tra ra rỗng và tin rơi
        // vào hư không mà chỉ còn một dòng log.
        var tho = """
            {"entry":[{"id":"waba-1","changes":[{"value":{"metadata":{"phone_number_id":"so-1"}}}]}]}
            """;
        Assert.Equal("so-1", WhatsAppChatAdapter.PhoneNumberIdOfEvent(tho));
    }

    [Fact]
    public void Cua_so_gui_WhatsApp_la_24_gio()
    {
        var moc = new DateTime(2026, 8, 27, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.WhatsApp, moc, moc.AddHours(23)).Open);
        Assert.False(ChatRules.ComputeSendWindow(ChatChannel.WhatsApp, moc, moc.AddHours(25)).Open);
        Assert.Contains("WhatsApp", ChatRules.ComputeSendWindow(ChatChannel.WhatsApp, null, moc).Reason);
    }

    // ── TikTok ──────────────────────────────────────────────────────────────

    [Fact]
    public void TikTok_noi_dung_la_chuoi_JSON_long_phai_boc_hai_lan()
    {
        // ⚠️ Trường content là một CHUỖI, không phải đối tượng. Đọc thẳng thì luôn ra rỗng và
        // không có lỗi nào — hộp thư chỉ đơn giản không bao giờ có tin.
        var noi = """
            {"type":"text","message_id":"m-1","conversation_id":"hoi-1",
             "text":{"body":"Chào shop, tour Hàn bao nhiêu ạ?"},
             "from_user":{"id":"u-1","nickname":"Minh"}}
            """.Replace("\r", "").Replace("\n", " ");
        var tho = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "im_receive_msg",
            user_openid = "open-1",
            create_time = 1700000000,
            content = noi,
        });

        var sk = Assert.Single(Tt().Parse(tho));
        Assert.Equal(ChatChannel.TikTok, sk.Channel);
        Assert.Equal("Chào shop, tour Hàn bao nhiêu ạ?", sk.Text);
        Assert.Equal("Minh", sk.DisplayName);
        // ⚠️ Định danh gửi lại là mã HỘI THOẠI, không phải mã người dùng.
        Assert.Equal("hoi-1", sk.ExternalUserId);
    }

    [Fact]
    public void TikTok_tieng_vong_lay_ten_khach_o_NGUOI_NHAN()
    {
        // im_send_msg là tin CỦA MÌNH. Lấy from_user là hội thoại mang tên chính công ty mình.
        var noi = """
            {"type":"text","message_id":"m-2","conversation_id":"hoi-1",
             "text":{"body":"Dạ tour Hàn 12.9 triệu ạ"},
             "from_user":{"id":"shop","nickname":"TourKit"},
             "to_user":{"id":"u-1","nickname":"Minh"}}
            """.Replace("\r", "").Replace("\n", " ");
        var tho = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "im_send_msg",
            user_openid = "open-1",
            create_time = 1700000060,
            content = noi,
        });

        var sk = Assert.Single(Tt().Parse(tho));
        Assert.True(sk.IsEcho);
        Assert.Equal("Minh", sk.DisplayName);
    }

    [Fact]
    public void TikTok_su_kien_khong_phai_tin_nhan_thi_bo_qua()
    {
        var tho = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "authorization.removed",
            user_openid = "open-1",
            create_time = 1700000000,
            content = "{}",
        });
        Assert.Empty(Tt().Parse(tho));
    }

    [Fact]
    public void TikTok_chu_ky_dung_thi_qua()
    {
        var than = """{"event":"im_receive_msg"}""";
        var luc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var (dung, _) = TikTokChatAdapter.CheckSignature("bi-mat", than, Ky("bi-mat", than, luc), luc);
        Assert.True(dung);
    }

    [Fact]
    public void TikTok_goi_qua_5_giay_thi_TU_CHOI_va_noi_ro_la_qua_han()
    {
        // ⚠️ Máy chủ lệch giờ làm MỌI gói bị từ chối. Nếu nhật ký chỉ nói "chữ ký sai" thì người
        // tìm lỗi sẽ đi soi khoá bí mật suốt buổi trong khi lỗi nằm ở đồng hồ.
        var than = """{"event":"im_receive_msg"}""";
        var luc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var (dung, viSao) = TikTokChatAdapter.CheckSignature(
            "bi-mat", than, Ky("bi-mat", than, luc), luc.AddSeconds(30));
        Assert.False(dung);
        Assert.Contains("quá hạn", viSao);
        Assert.Contains("đồng hồ", viSao);
    }

    [Fact]
    public void TikTok_chu_ky_sai_thi_noi_dung_la_sai_chu_ky()
    {
        var than = """{"event":"im_receive_msg"}""";
        var luc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var (dung, viSao) = TikTokChatAdapter.CheckSignature(
            "bi-mat-khac", than, Ky("bi-mat", than, luc), luc);
        Assert.False(dung);
        Assert.Contains("không khớp", viSao);
    }

    [Fact]
    public void TikTok_khong_gioi_han_cua_so_gui()
    {
        // Hạn trả lời của TikTok không có trong tài liệu công khai. Khoá ô soạn theo một con số tự
        // đoán là tự khoá tay nhân viên vì một luật có thể không tồn tại.
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.TikTok, null, DateTime.UtcNow).Open);
    }

    /// <summary>Dựng header <c>TikTok-Signature</c> đúng cách họ ký: HMAC trên <c>"{t}.{thân}"</c>.</summary>
    private static string Ky(string biMat, string than, DateTimeOffset luc)
    {
        var t = luc.ToUnixTimeSeconds().ToString();
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(biMat));
        var s = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{than}"))).ToLowerInvariant();
        return $"t={t},s={s}";
    }

    // ── Nối bằng một nút ────────────────────────────────────────────────────

    private static WhatsAppChatAdapter Wa(Dictionary<string, string?> khai)
    {
        var cfg = Cfg(khai);
        return new WhatsAppChatAdapter(null!, Kho(cfg), cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WhatsAppChatAdapter>.Instance);
    }

    private static TikTokChatAdapter Tt(Dictionary<string, string?> khai)
    {
        var cfg = Cfg(khai);
        return new TikTokChatAdapter(null!, Kho(cfg), cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TikTokChatAdapter>.Instance);
    }

    [Fact]
    public void WhatsApp_xin_quyen_bang_config_id_KHONG_phai_scope()
    {
        // Đây là chỗ khác Messenger nhiều người chép nhầm nhất: WhatsApp đi bằng config_id của
        // luồng Embedded Signup. Truyền scope vào thì Meta BỎ QUA — người dùng đi hết luồng,
        // màn hình báo thành công, mà không quyền nào được cấp và mình không tra ra tài khoản.
        var wa = Wa(new Dictionary<string, string?>
        {
            ["Chat:WhatsApp:AppId"] = "222",
            ["Chat:WhatsApp:AppSecret"] = "bí-mật",
            ["Chat:WhatsApp:ConfigId"] = "cfg-9",
        });
        Assert.True(wa.HasPlatformApp);

        var url = wa.PermissionUrlFor("https://travelai.vn/api/v1/chat/oauth/whatsapp/callback", "st-2");
        Assert.Contains("client_id=222", url);
        Assert.Contains("config_id=cfg-9", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=st-2", url);

        // extras bật đúng luồng Embedded Signup. Thiếu nó thì Meta mở hộp thoại đăng nhập
        // thường, người dùng không được dẫn qua bước tạo/chọn tài khoản WhatsApp.
        Assert.Contains("sessionInfoVersion", Uri.UnescapeDataString(url));
    }

    [Fact]
    public void WhatsApp_thieu_ConfigId_thi_KHONG_bao_la_noi_nhanh_duoc()
    {
        // Giao diện dựa vào cờ này để chọn giữa "một nút" và "khai tay". Báo bừa là người dùng
        // bấm nút rồi rơi vào màn hình lỗi của Meta, không hiểu vì sao.
        Assert.False(Wa(new Dictionary<string, string?>
        {
            ["Chat:WhatsApp:AppId"] = "222",
            ["Chat:WhatsApp:AppSecret"] = "bí-mật",
        }).HasPlatformApp);
    }

    [Fact]
    public void WhatsApp_dang_ky_du_bon_truong_ke_ca_lich_su_chat_cu()
    {
        // history là đường DUY NHẤT trong sáu kênh lấy lại được đoạn hội thoại có từ TRƯỚC lúc
        // nối. Bỏ nó thì hộp thư vẫn có tin mới nên nhìn qua tưởng chạy đúng, mà lịch sử thì mất
        // hẳn — và không lấy lại được sau. smb_message_echoes cũng vậy: thiếu nó thì hộp thư chỉ
        // thấy câu khách hỏi mà không thấy câu nhân viên đã trả lời từ điện thoại.
        foreach (var can in new[] { "messages", "history", "smb_app_state_sync", "smb_message_echoes" })
            Assert.Contains(can, WhatsAppChatAdapter.WabaEvents);
    }

    [Fact]
    public void TikTok_xin_du_ba_quyen_nhan_tin()
    {
        var tt = Tt(new Dictionary<string, string?>
        {
            ["Chat:TikTok:ClientId"] = "ck-1",
            ["Chat:TikTok:ClientSecret"] = "bí-mật",
        });
        Assert.True(tt.HasPlatformApp);

        var url = tt.PermissionUrlFor("https://travelai.vn/api/v1/chat/oauth/tiktok/callback", "st-3");
        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/?", url);

        // TikTok dùng client_key, KHÔNG phải client_id như mọi OAuth khác.
        Assert.Contains("client_key=ck-1", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=st-3", url);

        var quyen = Uri.UnescapeDataString(url);
        foreach (var can in new[] { "message.list.read", "message.list.send", "message.list.manage" })
            Assert.Contains(can, quyen);

        // Thiếu cái này thì ĐỔI TÀI KHOẢN không được: bấm Kết nối là TikTok lặng lẽ nối lại đúng
        // tài khoản lần trước, không hiện màn hình chọn nào.
        Assert.Contains("disable_auto_auth=1", url);
    }

    [Fact]
    public void TikTok_thieu_ClientId_thi_KHONG_bao_la_noi_nhanh_duoc()
    {
        Assert.False(Tt(new Dictionary<string, string?>
        {
            ["Chat:TikTok:ClientSecret"] = "bí-mật",
        }).HasPlatformApp);
    }

    // ── Khôi phục hội thoại cũ ──────────────────────────────────────────────

    [Fact]
    public void Lich_su_doc_chieu_tin_tu_ma_luong_khong_can_biet_so_cua_minh()
    {
        // thread.id CHÍNH LÀ số của khách. Nhờ vậy from != thread.id nghĩa là tin của mình — và
        // không phải đi tìm số của công ty, thứ mà gói lịch sử không phải lúc nào cũng có.
        var goi = """
        {"entry":[{"changes":[{"field":"history","value":{
          "contacts":[{"wa_id":"84900111222","profile":{"name":"Chị Lan"}}],
          "history":[{"threads":[{"id":"84900111222","messages":[
            {"id":"wamid.A","from":"84900111222","timestamp":"1700000000",
             "type":"text","text":{"body":"Tour Nhật còn chỗ không ạ"}},
            {"id":"wamid.B","from":"84988000000","timestamp":"1700000600",
             "type":"text","text":{"body":"Dạ còn 4 chỗ chị nhé"}}
          ]}]}]}}]}]}
        """;

        var sk = Wa(new Dictionary<string, string?>()).Parse(goi);
        Assert.Equal(2, sk.Count);

        // Cả hai đều là tin CŨ — nếu không, mỗi câu khách hỏi năm ngoái sẽ kích một câu trả lời
        // của trợ lý gửi thẳng cho khách hôm nay.
        Assert.All(sk, e => Assert.True(e.IsHistory));
        Assert.All(sk, e => Assert.Equal("84900111222", e.ExternalUserId));

        Assert.False(sk[0].IsEcho);   // from == thread.id → khách nói
        Assert.True(sk[1].IsEcho);    // from != thread.id → mình nói

        // Giờ THẬT của tin, không phải giờ nhập. Sai chỗ này là cả năm hội thoại dồn vào một phút.
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), sk[0].SentUtc);
        Assert.Equal("Chị Lan", sk[0].DisplayName);
    }

    [Fact]
    public void Lich_su_bo_tin_Meta_khong_giai_ma_duoc()
    {
        // type="errors" không có nội dung dùng được. Ghi vào là hộp thư đầy dòng trống mà không
        // ai biết là tin gì.
        var goi = """
        {"entry":[{"changes":[{"field":"history","value":{"history":[{"threads":[
          {"id":"84900111222","messages":[
            {"id":"wamid.X","from":"84900111222","timestamp":"1700000000","type":"errors"}
          ]}]}]}}]}]}
        """;
        Assert.Empty(Wa(new Dictionary<string, string?>()).Parse(goi));
    }

    [Fact]
    public void Tieng_vong_lay_KHACH_o_truong_to_khong_phai_from()
    {
        // Ở tiếng vọng, from là số của CÔNG TY. Lấy nhầm đầu là hội thoại mang tên chính mình.
        var goi = """
        {"entry":[{"changes":[{"field":"smb_message_echoes","value":{
          "message_echoes":[{"id":"wamid.E","from":"84988000000","to":"84900111222",
            "timestamp":"1700000600","type":"text","text":{"body":"Dạ em gửi giá ngay"}}]
        }}]}]}
        """;

        var sk = Assert.Single(Wa(new Dictionary<string, string?>()).Parse(goi));
        Assert.Equal("84900111222", sk.ExternalUserId);
        Assert.True(sk.IsEcho);

        // KHÔNG phải tin cũ: nhân viên vừa gõ từ điện thoại xong. Đánh dấu nhầm là lịch sử thì
        // bot không bị câm và sẽ nói đè lên người thật.
        Assert.False(sk.IsHistory);
        Assert.Equal("Dạ em gửi giá ngay", sk.Text);
    }

    [Fact]
    public void Lich_su_co_anh_thi_giu_ma_tep_chu_khong_tai_ve()
    {
        // Gói lịch sử có thể chở hàng nghìn tệp. Tải hết ngay lúc nhận webhook là treo cả đường
        // nhận tin — mà webhook có hạn trả lời.
        var goi = """
        {"entry":[{"changes":[{"field":"history","value":{"history":[{"threads":[
          {"id":"84900111222","messages":[
            {"id":"wamid.I","from":"84900111222","timestamp":"1700000000","type":"image",
             "image":{"id":"media-77","mime_type":"image/jpeg","caption":"Hộ chiếu em đây"}}
          ]}]}]}}]}]}
        """;

        var sk = Assert.Single(Wa(new Dictionary<string, string?>()).Parse(goi));
        Assert.Equal(ChatKind.Image, sk.Kind);
        Assert.Equal("Hộ chiếu em đây", sk.Text);
        Assert.Contains("media-77", sk.AttachmentJson);
    }

    [Fact]
    public void Lich_su_loai_chua_ho_tro_van_giu_mot_dong_noi_ro_la_gi()
    {
        // Dòng trống giữa hội thoại khiến người đọc tưởng mất tin.
        var goi = """
        {"entry":[{"changes":[{"field":"history","value":{"history":[{"threads":[
          {"id":"84900111222","messages":[
            {"id":"wamid.L","from":"84900111222","timestamp":"1700000000","type":"location"}
          ]}]}]}}]}]}
        """;

        var sk = Assert.Single(Wa(new Dictionary<string, string?>()).Parse(goi));
        Assert.Equal("[location]", sk.Text);
    }
}
