using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Khôi phục hội thoại CŨ của Messenger / Instagram.
///
/// <para>Graph trả hình dạng <b>khác hẳn</b> webhook, nên đây là đường bóc thứ hai — và là chỗ
/// dễ sai im lặng: bóc nhầm chiều tin thì cả hội thoại hiện như do chính công ty độc thoại, còn
/// bỏ sót dấu thời gian thì mấy năm lịch sử dồn hết vào một phút.</para>
/// </summary>
public class MetaHistoryTests
{
    /// <summary>Gói do MetaHistoryImporter tự xếp vào hàng đợi, chở tin THẬT của Graph.</summary>
    private static string Goi(string tin) => """
        {"tourkit_lich_su":{"khach":"PSID-9","ten":"Anh Minh","cuaToi":"PAGE-1","tin":[
        """ + tin + "]}}";

    [Fact]
    public void Chieu_tin_doc_tu_from_id_chu_khong_phai_tu_chuoi_cuaToi()
    {
        // So với "cuaToi" thì hỏng ở Instagram: mình gửi lên chuỗi "me", còn from.id Graph trả
        // về là id thật. Mọi tin sẽ bị coi là của khách, và trợ lý thấy một hội thoại chỉ toàn
        // câu hỏi chưa ai trả lời.
        var sk = MetaMessagingParser.Read(Goi("""
            {"id":"m_1","message":"Bên mình còn tour Đà Lạt không?",
             "from":{"id":"PSID-9","name":"Anh Minh"},"created_time":"2026-03-02T08:15:00+0000"},
            {"id":"m_2","message":"Dạ còn ạ, em gửi giá ngay",
             "from":{"id":"PAGE-1","name":"TourKit"},"created_time":"2026-03-02T08:16:30+0000"}
            """), ChatChannel.Messenger);

        Assert.Equal(2, sk.Count);
        Assert.All(sk, e => Assert.True(e.IsHistory));
        Assert.All(sk, e => Assert.Equal("PSID-9", e.ExternalUserId));

        Assert.False(sk[0].IsEcho);
        Assert.True(sk[1].IsEcho);

        // Giờ THẬT, đọc từ created_time.
        Assert.Equal(new DateTime(2026, 3, 2, 8, 15, 0, DateTimeKind.Utc), sk[0].SentUtc);
        Assert.Equal(new DateTime(2026, 3, 2, 8, 16, 30, DateTimeKind.Utc), sk[1].SentUtc);
    }

    [Fact]
    public void Anh_video_va_tep_nam_o_BA_cho_khac_nhau_trong_Graph()
    {
        // Ảnh ở image_data.url, video ở video_data.url, tệp/âm thanh ở file_url ngoài cùng.
        // Chỉ đọc một chỗ là mất hẳn hai loại kia mà không có lỗi nào hiện ra.
        var anh = MetaMessagingParser.Read(Goi("""
            {"id":"m_a","from":{"id":"PSID-9"},"created_time":"2026-03-02T08:15:00+0000",
             "attachments":{"data":[{"id":"1","mime_type":"image/jpeg",
               "image_data":{"url":"https://cdn/a.jpg"}}]}}
            """), ChatChannel.Messenger);
        Assert.Equal(ChatKind.Image, Assert.Single(anh).Kind);
        Assert.Contains("a.jpg", anh[0].AttachmentJson);

        var video = MetaMessagingParser.Read(Goi("""
            {"id":"m_v","from":{"id":"PSID-9"},"created_time":"2026-03-02T08:15:00+0000",
             "attachments":{"data":[{"id":"2","mime_type":"video/mp4",
               "video_data":{"url":"https://cdn/b.mp4"}}]}}
            """), ChatChannel.Messenger);
        Assert.Contains("b.mp4", Assert.Single(video).AttachmentJson);

        var tep = MetaMessagingParser.Read(Goi("""
            {"id":"m_f","from":{"id":"PSID-9"},"created_time":"2026-03-02T08:15:00+0000",
             "attachments":{"data":[{"id":"3","name":"bao-gia.pdf",
               "mime_type":"application/pdf","file_url":"https://cdn/c.pdf"}]}}
            """), ChatChannel.Messenger);
        Assert.Equal(ChatKind.File, Assert.Single(tep).Kind);
        Assert.Contains("bao-gia.pdf", tep[0].AttachmentJson);
    }

    [Fact]
    public void Tin_rong_khong_chu_khong_dinh_kem_thi_BO_han()
    {
        // Graph trả cả những mục không có nội dung gì (tin đã xoá, tin hệ thống). Ghi vào là hộp
        // thư đầy dòng trống mà không ai biết là tin gì.
        Assert.Empty(MetaMessagingParser.Read(Goi("""
            {"id":"m_0","from":{"id":"PSID-9"},"created_time":"2026-03-02T08:15:00+0000"}
            """), ChatChannel.Messenger));
    }

    [Fact]
    public void Goi_lich_su_KHONG_lam_hong_duong_boc_webhook_thuong()
    {
        // Hai hình dạng đi chung một hàm. Nhận nhầm nhau là hoặc mất tin trực tiếp, hoặc lịch sử
        // chạy qua đường tin thường và mỗi câu khách hỏi năm ngoái kích một câu trả lời hôm nay.
        var thuong = MetaMessagingParser.Read("""
            {"entry":[{"messaging":[{"sender":{"id":"PSID-9"},"recipient":{"id":"PAGE-1"},
             "timestamp":1772000000000,"message":{"mid":"m_live","text":"Chào shop"}}]}]}
            """, ChatChannel.Messenger);

        var sk = Assert.Single(thuong);
        Assert.False(sk.IsHistory);
        Assert.Equal("Chào shop", sk.Text);
    }

    [Fact]
    public void Chi_Messenger_va_Instagram_lay_lai_duoc_hoi_thoai_cu()
    {
        // Bốn kênh kia KHÔNG có đường nào: Telegram Bot API không cho đọc quá khứ, Zalo không có
        // đầu đọc hội thoại, TikTok đòi tư cách Messaging Partner. WhatsApp có nhưng đi đường
        // khác hẳn — Meta tự ĐẨY về sau khi mình gọi smb_app_data, không phải mình đi đọc.
        Assert.True(MetaHistoryImporter.Supports(ChatChannel.Messenger));
        Assert.True(MetaHistoryImporter.Supports(ChatChannel.Instagram));

        foreach (var k in new[]
        {
            ChatChannel.Zalo, ChatChannel.Telegram, ChatChannel.TikTok,
            ChatChannel.WhatsApp, ChatChannel.Webchat,
        })
            Assert.False(MetaHistoryImporter.Supports(k));
    }
}
