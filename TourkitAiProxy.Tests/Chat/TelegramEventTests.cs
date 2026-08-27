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
}
