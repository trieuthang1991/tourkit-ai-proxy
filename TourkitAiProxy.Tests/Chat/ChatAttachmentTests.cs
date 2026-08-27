using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Bóc đính kèm của ba kênh về CÙNG một hình dạng. Đây là chỗ dễ sai âm thầm nhất trong cụm chat:
/// mỗi kênh gói tệp một kiểu, mà hỏng thì không có lỗi nào hiện lên — chỉ là khách gửi ảnh còn
/// nhân viên nhìn thấy một bong bóng trống.
/// </summary>
public class ChatAttachmentTests
{
    private const short KhachGui = 0;
    private const short MinhGui = 1;

    // ── Telegram ────────────────────────────────────────────────────────────

    [Fact]
    public void Telegram_anh_lay_co_LON_NHAT()
    {
        // Telegram gửi CÙNG một ảnh ở nhiều cỡ, xếp nhỏ trước. Lấy nhầm cỡ nhỏ thì nhân viên soi
        // ảnh hoá đơn / hộ chiếu khách gửi sẽ không đọc nổi chữ.
        var json = """
        [{"file_id":"nho","file_size":1200,"width":90},
         {"file_id":"to","file_size":85000,"width":1280}]
        """;
        var ra = ChatAttachment.Read(ChatChannel.Telegram, ChatKind.Image, json, KhachGui);

        var f = Assert.Single(ra);
        Assert.Equal("to", f.FileId);
        Assert.Equal(85000, f.Size);
        Assert.Null(f.Url);          // Telegram KHÔNG cho URL — phải đi qua máy chủ
        Assert.True(f.HasFile);
    }

    [Fact]
    public void Telegram_tep_lay_ten_va_co()
    {
        var json = """{"file_id":"abc","file_name":"bao-gia.pdf","file_size":40960}""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Telegram, ChatKind.File, json, KhachGui));

        Assert.Equal("bao-gia.pdf", f.Name);
        Assert.Equal(40960, f.Size);
        Assert.Equal("abc", f.FileId);
    }

    [Fact]
    public void Telegram_vi_tri_ra_toa_do_khong_phai_tep()
    {
        var json = """{"latitude":10.7769,"longitude":106.7009}""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Telegram, ChatKind.Location, json, KhachGui));

        Assert.Equal(10.7769, f.Lat!.Value, 4);
        Assert.Equal(106.7009, f.Lon!.Value, 4);
        Assert.False(f.HasFile);   // vị trí không có gì để tải
    }

    // ── Messenger ───────────────────────────────────────────────────────────

    [Fact]
    public void Messenger_lay_url_thang()
    {
        var json = """[{"type":"image","payload":{"url":"https://cdn.fb/a.jpg"}}]""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Messenger, ChatKind.Image, json, KhachGui));

        Assert.Equal("https://cdn.fb/a.jpg", f.Url);
        Assert.Null(f.FileId);
    }

    [Fact]
    public void Messenger_nhieu_dinh_kem_ra_nhieu_dong()
    {
        var json = """
        [{"type":"image","payload":{"url":"https://cdn.fb/1.jpg"}},
         {"type":"image","payload":{"url":"https://cdn.fb/2.jpg"}}]
        """;
        Assert.Equal(2, ChatAttachment.Read(ChatChannel.Messenger, ChatKind.Image, json, KhachGui).Count);
    }

    // ── Zalo ────────────────────────────────────────────────────────────────

    [Fact]
    public void Zalo_sticker_roi_ve_thumbnail_khi_khong_co_url()
    {
        var json = """[{"type":"sticker","payload":{"thumbnail":"https://zalo/s.png"}}]""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Zalo, ChatKind.Sticker, json, KhachGui));

        Assert.Equal("https://zalo/s.png", f.Url);
    }

    [Fact]
    public void Zalo_co_tep_doc_duoc_ca_khi_size_la_CHUOI()
    {
        // Zalo có chỗ trả số dạng chuỗi — tin vào đúng một kiểu là mất cỡ tệp trong im lặng.
        var json = """[{"type":"file","payload":{"url":"https://z/f.pdf","name":"hd.pdf","size":"2048"}}]""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Zalo, ChatKind.File, json, KhachGui));

        Assert.Equal("hd.pdf", f.Name);
        Assert.Equal(2048, f.Size);
    }

    // ── Tin MÌNH gửi ────────────────────────────────────────────────────────

    [Fact]
    public void Tin_minh_gui_doc_thang_khong_boc_theo_kenh()
    {
        // Tin mình gửi được ghi theo hình dạng chuẩn {ten,kich,url} lúc lưu, nên phải đọc thẳng —
        // bóc theo định dạng riêng của kênh sẽ ra rỗng và ảnh vừa gửi biến mất khỏi khung chat.
        var json = """{"ten":"anh.jpg","kich":5000,"url":"https://r2/x.jpg"}""";
        var f = Assert.Single(ChatAttachment.Read(ChatChannel.Telegram, ChatKind.Image, json, MinhGui));

        Assert.Equal("anh.jpg", f.Name);
        Assert.Equal("https://r2/x.jpg", f.Url);
    }

    // ── Hỏng thì im, không ném ──────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{khong-phai-json")]
    [InlineData("[]")]
    public void Json_rong_hay_hong_tra_danh_sach_rong(string? json)
    {
        // Một đính kèm lạ KHÔNG được phép làm hỏng cả khung chat.
        Assert.Empty(ChatAttachment.Read(ChatChannel.Zalo, ChatKind.Image, json, KhachGui));
        Assert.Empty(ChatAttachment.Read(ChatChannel.Messenger, ChatKind.Image, json, KhachGui));
    }
}
