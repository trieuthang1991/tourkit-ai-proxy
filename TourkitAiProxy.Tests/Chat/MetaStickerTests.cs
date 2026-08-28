using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nhãn dán của Messenger/Instagram.
///
/// <para>Gói tin dưới đây <b>chép từ dữ liệu thật</b> trong hộp thư staging (28/08/2026), không
/// phải bịa theo tài liệu: khách bấm một cái like, Meta trả về HAI mục cùng URL, cùng
/// <c>sticker_id</c>, chỉ khác <c>type</c>. Tài liệu của Meta không nói chỗ này.</para>
/// </summary>
public class MetaStickerTests
{
    /// URL rút gọn nhưng giữ nguyên hình dạng (có tham số hết hạn oe=).
    private const string UrlThat =
        "https://scontent.xx.fbcdn.net/v/t39.1997-6/39178562_150519.png?stp=cp0&oe=6A963ACB";

    private static string GoiNhanDan() => """
    {"entry":[{"messaging":[{
      "sender":{"id":"PSID-9"},"recipient":{"id":"PAGE-1"},"timestamp":1772000000000,
      "message":{"mid":"m_1","attachments":[
        {"type":"image","payload":{"url":"https://scontent.xx.fbcdn.net/v/t39.1997-6/39178562_150519.png?stp=cp0&oe=6A963ACB","sticker_id":369239263222822}},
        {"type":"sticker","payload":{"url":"https://scontent.xx.fbcdn.net/v/t39.1997-6/39178562_150519.png?stp=cp0&oe=6A963ACB","sticker_id":369239263222822}}
      ]}
    }]}]}
    """;

    [Fact]
    public void Mot_cai_like_KHONG_duoc_thanh_hai_tep()
    {
        // Meta trả kèm bản "image" cho tích hợp đời cũ. Không lọc thì khách gửi một cái like, hộp
        // thư hiện hai — nhân viên tưởng khách bấm nhầm hai lần.
        var sk = Assert.Single(MetaMessagingParser.Read(GoiNhanDan(), ChatChannel.Messenger));

        var tep = ChatAttachment.Read(ChatChannel.Messenger, sk.Kind, sk.AttachmentJson, 0);
        Assert.Single(tep);
        Assert.Equal(UrlThat, tep[0].Url);
    }

    [Fact]
    public void Nhan_dan_phai_ghi_dung_LOAI_chu_khong_phai_anh()
    {
        // Chỉ nhìn mục ĐẦU thì type="image" → mọi nhãn dán bị ghi là ảnh, rồi giao diện vẽ nó to
        // bằng tấm ảnh khách chụp hộ chiếu. Phải quét CẢ mảng.
        var sk = Assert.Single(MetaMessagingParser.Read(GoiNhanDan(), ChatChannel.Messenger));
        Assert.Equal(ChatKind.Sticker, sk.Kind);
    }

    [Fact]
    public void Nhieu_anh_THAT_thi_khong_bi_gop_oan()
    {
        // Lọc theo URL, nên ba tấm ảnh khác nhau vẫn ra ba tệp. Lọc theo sticker_id hay theo số
        // lượng thì ca này hỏng.
        var goi = """
        {"entry":[{"messaging":[{
          "sender":{"id":"PSID-9"},"recipient":{"id":"PAGE-1"},"timestamp":1772000000000,
          "message":{"mid":"m_2","attachments":[
            {"type":"image","payload":{"url":"https://cdn/a.jpg"}},
            {"type":"image","payload":{"url":"https://cdn/b.jpg"}},
            {"type":"image","payload":{"url":"https://cdn/c.jpg"}}
          ]}
        }]}]}
        """;

        var sk = Assert.Single(MetaMessagingParser.Read(goi, ChatChannel.Messenger));
        Assert.Equal(ChatKind.Image, sk.Kind);

        var tep = ChatAttachment.Read(ChatChannel.Messenger, sk.Kind, sk.AttachmentJson, 0);
        Assert.Equal(3, tep.Count);
        Assert.Equal(new[] { "https://cdn/a.jpg", "https://cdn/b.jpg", "https://cdn/c.jpg" },
            tep.Select(x => x.Url));
    }

    [Fact]
    public void Nhan_dan_Instagram_di_chung_mot_duong()
    {
        // Hai kênh dùng chung MetaMessagingParser — sửa một bên mà quên bên kia là lệch im lặng.
        var sk = Assert.Single(MetaMessagingParser.Read(GoiNhanDan(), ChatChannel.Instagram));
        Assert.Equal(ChatKind.Sticker, sk.Kind);
        Assert.Single(ChatAttachment.Read(ChatChannel.Instagram, sk.Kind, sk.AttachmentJson, 0));
    }

    [Fact]
    public void Vi_tri_van_doc_duoc_khong_bi_bo_lam_trung()
    {
        // Vị trí không có url; lọc trùng theo url mà không tách nhánh này ra thì vị trí biến mất.
        var goi = """
        {"entry":[{"messaging":[{
          "sender":{"id":"PSID-9"},"recipient":{"id":"PAGE-1"},"timestamp":1772000000000,
          "message":{"mid":"m_3","attachments":[
            {"type":"location","payload":{"coordinates":{"lat":21.03,"long":105.85}}}
          ]}
        }]}]}
        """;

        var sk = Assert.Single(MetaMessagingParser.Read(goi, ChatChannel.Messenger));
        var tep = Assert.Single(ChatAttachment.Read(ChatChannel.Messenger, sk.Kind, sk.AttachmentJson, 0));
        Assert.Equal(21.03, tep.Lat);
        Assert.Equal(105.85, tep.Lon);
    }
}
