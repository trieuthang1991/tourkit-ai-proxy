using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nút bấm dưới tin nhắn.
///
/// <para><b>Vượt giới hạn nút là nền tảng từ chối CẢ TIN</b>, không phải cắt bớt nút — khách không
/// nhận được gì. Và mỗi kênh một con số khác nhau thật, nên đây là chỗ phải có test.</para>
/// </summary>
public class ChatButtonTests
{
    private static ChatButton TraLoi(string chu) => new(chu);
    private static ChatButton LienKet(string chu) => new(chu, "https://tourkit.vn/x");

    [Fact]
    public void Moi_kenh_mot_gioi_han_rieng_khong_dung_chung_mot_so()
    {
        // Meta cho 13 nút trả lời nhanh nhưng chỉ 3 nút trong khung nút (khung mới chứa được liên
        // kết). Áp một con số chung là hoặc tự bó tay mình, hoặc để tin biến mất ở kênh chặt nhất.
        Assert.Equal(13, ChatRules.MaxButtons(ChatChannel.Messenger, coLienKet: false));
        Assert.Equal(3, ChatRules.MaxButtons(ChatChannel.Messenger, coLienKet: true));
        Assert.Equal(5, ChatRules.MaxButtons(ChatChannel.Zalo, false));
        Assert.Equal(0, ChatRules.MaxButtons(ChatChannel.TikTok, false));
    }

    [Fact]
    public void WhatsApp_KHONG_nhan_nut_mo_lien_ket_trong_tin_thuong()
    {
        // Nút liên kết của WhatsApp chỉ sống trong mẫu đã duyệt. Gửi kèm trong tin thường thì Meta
        // từ chối — nên phải chặn từ đây và nói rõ đường đi tiếp.
        Assert.Equal(3, ChatRules.MaxButtons(ChatChannel.WhatsApp, coLienKet: false));
        Assert.Equal(0, ChatRules.MaxButtons(ChatChannel.WhatsApp, coLienKet: true));

        var (nut, canhBao) = ChatRules.FitButtons(ChatChannel.WhatsApp,
            new[] { TraLoi("Có"), LienKet("Xem tour") });

        Assert.Empty(nut);
        Assert.Contains("không nhận nút mở liên kết", canhBao);
        Assert.Contains("tin mẫu", canhBao);
    }

    [Fact]
    public void Cat_bot_nut_thi_phai_NOI_RA_chu_khong_im_lang()
    {
        // Im lặng cắt thì nhân viên soạn năm nút, khách thấy ba, và không ai biết vì sao.
        var (nut, canhBao) = ChatRules.FitButtons(ChatChannel.Messenger,
            new[] { LienKet("A"), TraLoi("B"), TraLoi("C"), TraLoi("D"), TraLoi("E") });

        Assert.Equal(3, nut.Count);
        Assert.Equal(new[] { "A", "B", "C" }, nut.Select(x => x.Label));
        Assert.Contains("chỉ nhận 3 nút", canhBao);
        Assert.Contains("bỏ 2 nút cuối", canhBao);
    }

    [Fact]
    public void Vua_gioi_han_thi_KHONG_canh_bao_thua()
    {
        var (nut, canhBao) = ChatRules.FitButtons(ChatChannel.Zalo,
            new[] { TraLoi("1"), TraLoi("2"), TraLoi("3"), TraLoi("4"), TraLoi("5") });
        Assert.Equal(5, nut.Count);
        Assert.Null(canhBao);
    }

    [Fact]
    public void Kenh_khong_co_nut_thi_van_gui_duoc_CHU()
    {
        var (nut, canhBao) = ChatRules.FitButtons(ChatChannel.TikTok, new[] { TraLoi("Có") });
        Assert.Empty(nut);
        Assert.Contains("Tin vẫn gửi", canhBao);
    }

    [Fact]
    public void Meta_co_lien_ket_thi_dung_KHUNG_NUT_khong_thi_dung_TRA_LOI_NHANH()
    {
        // Chọn nhầm cơ chế thì tin vẫn đi nhưng hỏng khó thấy: nhét liên kết vào quick_replies là
        // Meta bỏ luôn phần liên kết.
        var khung = JsonNode.Parse(JsonSerializer.Serialize(
            MetaButtonBuilder.Build("Chọn tuyến", new[] { LienKet("Xem tour"), TraLoi("Gọi lại") })))!;
        Assert.Equal("button", khung["attachment"]!["payload"]!["template_type"]!.ToString());
        Assert.Equal("web_url", khung["attachment"]!["payload"]!["buttons"]![0]!["type"]!.ToString());
        Assert.Equal("postback", khung["attachment"]!["payload"]!["buttons"]![1]!["type"]!.ToString());

        var nhanh = JsonNode.Parse(JsonSerializer.Serialize(
            MetaButtonBuilder.Build("Chọn tuyến", new[] { TraLoi("Nhật"), TraLoi("Hàn") })))!;
        Assert.Null(nhanh["attachment"]);
        Assert.Equal(2, nhanh["quick_replies"]!.AsArray().Count);
    }

    [Fact]
    public void Payload_nut_tra_loi_nhanh_CHINH_LA_chu_tren_nut()
    {
        // Bot bên mình là trợ lý AI, không phải máy chạy luồng — nên khách bấm nút = khách nói câu
        // đó. Bộ bóc tin vốn đã ghi lượt bấm bằng chữ trên nút, nên vòng này khép kín.
        var m = MetaButtonBuilder.QuickReplyMessage("Chọn tuyến", new[] { TraLoi("Nhật Bản") });
        var nut = m["quick_replies"]!.AsArray()[0]!;
        Assert.Equal("Nhật Bản", nut["title"]!.ToString());
        Assert.Equal("Nhật Bản", nut["payload"]!.ToString());
    }

    [Fact]
    public void Khong_co_nut_thi_gui_chu_tran_khong_kem_khung_rong()
    {
        var m = JsonNode.Parse(JsonSerializer.Serialize(
            MetaButtonBuilder.Build("Chào anh", Array.Empty<ChatButton>())))!;
        Assert.Equal("Chào anh", m["text"]!.ToString());
        Assert.Null(m["quick_replies"]);
        Assert.Null(m["attachment"]);
    }

    [Fact]
    public void Telegram_cat_callback_data_theo_BYTE_khong_theo_ky_tu()
    {
        // callback_data chặn ở 64 BYTE. Tiếng Việt có dấu tốn 2–3 byte một ký tự, nên một nhãn 25
        // chữ cái đã vượt — và Telegram từ chối CẢ TIN.
        var dai = string.Concat(Enumerable.Repeat("Đặt tour Nhật Bản ", 6));
        var cat = TelegramChatAdapter.CatTheoByte(dai, 64);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cat) <= 64);

        // KHÔNG được xẻ đôi một ký tự: chuỗi cắt ra phải mã hoá lại y nguyên.
        Assert.Equal(cat, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(cat)));
        Assert.DoesNotContain('�', cat);
        Assert.StartsWith("Đặt tour", cat);
    }

    [Fact]
    public void Telegram_nhan_ngan_thi_giu_nguyen()
    {
        Assert.Equal("Có", TelegramChatAdapter.CatTheoByte("Có", 64));
    }
}
