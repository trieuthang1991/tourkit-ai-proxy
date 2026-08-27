using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Bóc gói tin Telegram.
///
/// <para>Cụm này <b>hỏng im lặng</b>: loại đính kèm không khớp nhánh nào thì tin vẫn được ghi,
/// chỉ là ghi rỗng — không lỗi, không log, nhân viên thấy một dòng trắng trong hộp thư và không
/// biết khách vừa gửi gì. Nên phải chốt bằng test trên gói tin thật.</para>
/// </summary>
public class TelegramEventTests
{
    internal static TelegramChatAdapter A()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PushDb"] = "Server=khong-dung;Database=x;",
            }).Build();
        var db = new TourkitAiProxy.Infrastructure.Db.TourkitAiDb(cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Db.TourkitAiDb>.Instance);
        var cred = new TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TourkitAiProxy.Infrastructure.Chat.Channels.ChannelCredentialStore>.Instance);
        // IHttpClientFactory null: Parse không gọi ra mạng.
        return new TelegramChatAdapter(null!, cred, cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramChatAdapter>.Instance);
    }

    private static string Goi(string phanThem) =>
        """{"update_id":1,"message":{"message_id":77,"date":1700000000,"""
        + """ "from":{"id":9,"first_name":"Lan"},"chat":{"id":"9"}, """
        + phanThem + "}}";

    // ── Đính kèm khách gửi ──────────────────────────────────────────────────

    [Fact]
    public void Khach_gui_video_khong_duoc_thanh_tin_rong()
    {
        // Trước đây `video` không khớp nhánh nào nên rơi vào ChatKind.Chu với nội dung null:
        // một dòng trắng trong hộp thư, không lỗi, không log.
        var sk = Assert.Single(A().Parse(Goi(
            """ "video":{"file_id":"vid-1","file_name":"tour.mp4","file_size":1234,"mime_type":"video/mp4"} """)));
        Assert.Equal(ChatKind.Tep, sk.Kind);
        Assert.Contains("vid-1", sk.AttachmentJson);

        // Và phải bóc ra được tệp thật, không chỉ giữ JSON thô.
        var tep = Assert.Single(ChatAttachment.Doc(ChatChannel.Telegram, sk.Kind, sk.AttachmentJson, 0));
        Assert.Equal("vid-1", tep.FileId);
        Assert.Equal("tour.mp4", tep.Ten);
    }

    [Fact]
    public void Khach_gui_bai_hat_la_am_thanh_khong_phai_tep()
    {
        // `audio` (bài hát, ghi âm gửi kèm) khác `voice` (bấm giữ nói) nhưng cùng là âm thanh —
        // hiện thành "tệp" thì nhân viên phải tải về mới biết có nghe được không.
        var sk = Assert.Single(A().Parse(Goi(
            """ "audio":{"file_id":"au-1","file_size":222,"mime_type":"audio/mpeg"} """)));
        Assert.Equal(ChatKind.AmThanh, sk.Kind);
        Assert.Contains("au-1", sk.AttachmentJson);
    }

    [Fact]
    public void Khach_gui_video_tron_cung_la_tep()
    {
        // video_note = ô video tròn Telegram, gói riêng một trường.
        var sk = Assert.Single(A().Parse(Goi("""
            "video_note":{"file_id":"vn-1","file_size":99}
            """)));
        Assert.Equal(ChatKind.Tep, sk.Kind);
        Assert.Contains("vn-1", sk.AttachmentJson);
    }

    [Fact]
    public void Loai_khong_nhan_ra_thi_BO_QUA_chu_khong_ghi_dong_trang()
    {
        // Telegram còn hàng chục loại nữa (poll, dice, game, invoice…). Ghi tin rỗng cho mỗi loại
        // lạ là dồn rác vào hộp thư và đẩy hội thoại lên đầu danh sách mà chẳng có gì để đọc.
        Assert.Empty(A().Parse(Goi("""
            "poll":{"id":"p1","question":"Đi Đà Lạt hay Nha Trang?"}
            """)));
    }

    [Fact]
    public void Tin_chu_binh_thuong_van_chay()
    {
        var sk = Assert.Single(A().Parse(Goi(""" "text":"Cho hỏi tour Đà Nẵng" """)));
        Assert.Equal(ChatKind.Chu, sk.Kind);
        Assert.Equal("Cho hỏi tour Đà Nẵng", sk.Text);
        Assert.Equal("Lan", sk.DisplayName);
        // Telegram đánh số tin theo từng cuộc trò chuyện, không toàn cục — phải ghép chat id vào.
        Assert.Equal("9:77", sk.ExternalMsgId);
    }

    // ── Khách bấm nút ───────────────────────────────────────────────────────

    [Fact]
    public void Bam_nut_ghi_lai_bang_CHU_TREN_NUT()
    {
        // Ghi callback_data kỹ thuật thì nhân viên đọc lại hội thoại thấy "MENU_TOUR_DN" —
        // không phải thứ khách nhìn thấy lúc bấm.
        var tho = """
            {"update_id":2,"callback_query":{"id":"cb-1","data":"MENU_TOUR_DN",
             "from":{"id":9,"first_name":"Lan"},
             "message":{"message_id":5,"date":1700000000,"chat":{"id":"9"},
              "reply_markup":{"inline_keyboard":[[{"text":"Tour Đà Nẵng","callback_data":"MENU_TOUR_DN"}]]}}}}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.Equal("9", sk.ExternalUserId);
        Assert.Equal("Tour Đà Nẵng", sk.Text);
    }

    [Fact]
    public void Bam_nut_khong_doc_duoc_chu_thi_lui_ve_ma_nut()
    {
        // Nút do hệ thống khác gửi (hoặc tin cũ Telegram không kèm reply_markup): thà hiện mã
        // nút còn hơn một dòng trống — nhân viên vẫn đoán được khách vừa chọn gì.
        var tho = """
            {"update_id":2,"callback_query":{"id":"cb-2","data":"XAC_NHAN",
             "from":{"id":9,"first_name":"Lan"},
             "message":{"message_id":5,"date":1700000000,"chat":{"id":"9"}}}}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.Equal("XAC_NHAN", sk.Text);
    }

    // ── Cảm xúc ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tha_cam_xuc_KHONG_thanh_tin_moi()
    {
        var tho = """
            {"update_id":3,"message_reaction":{"chat":{"id":"9"},"message_id":77,
             "user":{"id":9,"first_name":"Lan"},"date":1700000000,
             "old_reaction":[],"new_reaction":[{"type":"emoji","emoji":"❤"}]}}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.NotNull(sk.Reaction);
        Assert.Equal("9:77", sk.Reaction!.ExternalMsgId);
        Assert.Equal("❤", sk.Reaction.BieuTuong);
        Assert.False(sk.Reaction.Bo);
        // Không được là một tin trong hội thoại: "❤️" hiện như một câu khách vừa nói thì dòng
        // thời gian loạn và mọi thứ đếm theo tin đều lệch.
        Assert.Null(sk.Text);
        Assert.Null(sk.ExternalMsgId);
    }

    [Fact]
    public void Go_cam_xuc_phai_doc_duoc_la_GO()
    {
        // Telegram báo GỠ bằng cách gửi new_reaction RỖNG (khác hẳn Meta dùng action="unreact").
        // Đọc nhầm là cảm xúc đã gỡ vẫn hiện mãi trên màn hình.
        var tho = """
            {"update_id":4,"message_reaction":{"chat":{"id":"9"},"message_id":77,
             "user":{"id":9,"first_name":"Lan"},"date":1700000000,
             "old_reaction":[{"type":"emoji","emoji":"❤"}],"new_reaction":[]}}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.True(sk.Reaction!.Bo);
        Assert.Equal("9:77", sk.Reaction.ExternalMsgId);
    }

    // ── Khách đến từ đâu ────────────────────────────────────────────────────

    [Fact]
    public void Lenh_start_kem_tham_so_la_NGUON_khach_den_khong_phai_cau_noi()
    {
        // t.me/bot?start=fb_ads_hue là cách DUY NHẤT Telegram cho biết khách đến từ đâu, và nó
        // chỉ tới MỘT LẦN. Để nguyên thành tin chữ thì hộp thư có một câu "/start fb_ads_hue"
        // vô nghĩa, còn dữ liệu bán hàng thì mất vĩnh viễn.
        var sk = Assert.Single(A().Parse(Goi(""" "text":"/start fb_ads_hue" """)));
        Assert.NotNull(sk.Referral);
        Assert.Equal("fb_ads_hue", sk.Referral!.Ref);
        Assert.Null(sk.Text);
    }

    [Fact]
    public void Lenh_start_KHONG_kem_tham_so_thi_khong_bia_ra_nguon()
    {
        // Khách bấm "Bắt đầu" trong chính Telegram cũng gửi /start — không có nguồn nào cả.
        // Ghi bừa một nguồn rỗng là làm bẩn báo cáo "khách đến từ đâu".
        var sk = A().Parse(Goi(""" "text":"/start" """));
        Assert.Empty(sk.Where(x => x.Referral is not null));
    }

    // ── Đang gõ + hồ sơ khách ───────────────────────────────────────────────

    [Fact]
    public void Bao_dang_go_va_ho_so_khach_phai_duoc_cai_dat()
    {
        // Hai hàm này có mặc định "không làm gì" trong giao diện kênh — quên cài thì không có
        // lỗi nào, chỉ là khách Telegram không bao giờ thấy ba chấm và không bao giờ có ảnh.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs");
        Assert.Contains("BaoDangGoAsync", src);
        Assert.Contains("sendChatAction", src);
        Assert.Contains("HoSoKhachAsync", src);
        Assert.Contains("getUserProfilePhotos", src);

        // ⚠️ Bấm nút mà không trả lời Telegram thì nút QUAY VÒNG mãi trên máy khách, dù mình đã
        // xử lý xong. Chỉ biết được điều này khi đọc dự án tham khảo.
        Assert.Contains("answerCallbackQuery", src);
    }

    [Fact]
    public void Anh_dai_dien_KHONG_duoc_mang_bot_token_ra_trinh_duyet()
    {
        // Đường tải tệp của Telegram có dạng .../file/bot<TOKEN>/<đường dẫn> — token nằm NGAY
        // TRONG URL. Dự án tham khảo lưu thẳng chuỗi đó làm ảnh đại diện, tức là phát bot token
        // cho mọi trình duyệt mở hộp thư. Ở đây ảnh phải đi qua máy chủ.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs");
        var i = src.IndexOf("HoSoKhachAsync", System.StringComparison.Ordinal);
        var than = src.Substring(i, System.Math.Min(2500, src.Length - i));
        Assert.DoesNotContain("/file/bot", than);
    }

    [Fact]
    public void Proxy_tep_phai_dung_token_cua_CHINH_bot_do()
    {
        // file_id của Telegram gắn với TỪNG bot: đổi bằng token của bot khác thì Telegram trả
        // lỗi, và giao diện hiện "chưa tải được" cho mọi tệp khách gửi. Trước 27/08 proxy dùng
        // Telegram:BotToken — bot DÙNG CHUNG của bản tin sáng, không phải bot của công ty.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("/messages/{msgId:long}/file", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy đường proxy tệp Telegram");
        // Cửa sổ đủ rộng để trùm cả nhánh WhatsApp chèn giữa — kênh nào cũng có cách giấu tệp riêng.
        var than = src.Substring(i, System.Math.Min(3000, src.Length - i));
        Assert.Contains("TokenTelegramAsync", than);
        // Soi ĐÚNG dạng mã chứ không soi chữ: guard đọc văn bản thô nên bắt cả chú thích, mà chú
        // thích ngay trên nó có nhắc tên khoá cũ để người sau biết vì sao đổi.
        Assert.DoesNotContain(""" cfg["Telegram:BotToken"] """.Trim(), than);

        // ...và chỗ tra token phải hỏi khoá của TỪNG tài khoản trước, chỉ lùi về bot dùng chung
        // khi tài khoản chưa có khoá riêng (tương thích bản một-bot cũ).
        var j = src.IndexOf("private static async Task<string?> TokenTelegramAsync",
            System.StringComparison.Ordinal);
        Assert.True(j > 0, "Không thấy hàm tra bot token theo tài khoản");
        var hamTra = src.Substring(j, System.Math.Min(900, src.Length - j));
        Assert.Contains("cred.GetAsync", hamTra);
        Assert.Contains("botToken", hamTra);
    }
}
