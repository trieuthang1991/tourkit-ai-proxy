using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh chỗ lấy tên + ảnh đại diện của khách trên Zalo.
///
/// <para><b>Chuyện đã xảy ra (phát hiện 02/10/2026).</b> Chú thích ở <c>IChatChannelAdapter</c>
/// viết "Zalo và Telegram gửi sẵn tên ngay trong gói tin webhook nên không tốn lượt gọi nào".
/// Đúng với Telegram, <b>sai với Zalo</b>: gói tin nhắn của Zalo chỉ có <c>sender.id</c>. Vì tin
/// vào câu đó mà <c>ZaloChatAdapter</c> không cài <c>ContactProfileAsync</c>, rơi về bản mặc định
/// của interface — một hàm trả thẳng <c>null</c>. Hậu quả: hộp thư Zalo hiện một dãy số thay cho
/// tên khách và không bao giờ có ảnh, <b>không lỗi, không log</b>, nên suốt nhiều tuần không ai
/// nhìn ra. Người dùng mô tả đúng triệu chứng: "chỉ nhận được thông tin khi tin nhắn đến".</para>
///
/// <para>Nhóm test này giữ ba thứ: hàm còn đó, gọi đúng đường, và không ném ra ngoài.</para>
/// </summary>
public class ZaloContactProfileGuardTests
{
    private static string Nguon()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Channels/ZaloChatAdapter.cs");

    [Fact]
    public void Zalo_PHAI_cai_ContactProfileAsync()
    {
        // Chốt chặn cho đúng lỗi trên: thiếu hàm này thì hộp thư hiện mã người dùng, mà vì bản
        // mặc định của interface trả null một cách hợp lệ nên KHÔNG có gì đỏ lên để báo.
        Assert.Contains("public async Task<ContactProfile?> ContactProfileAsync", Nguon());
    }

    [Fact]
    public void Phai_goi_dung_duong_user_detail()
    {
        var src = Nguon();
        Assert.Contains("v3.0/oa/user/detail", src);
        // Zalo nhận tham số dưới dạng MỘT chuỗi JSON trong query `data`. Viết "?user_id=" thì
        // Zalo trả lỗi tham số chứ không trả hồ sơ — và vì hàm nuốt lỗi, nó hỏng trong im lặng
        // y hệt lúc chưa có hàm. Đây là cái bẫy thứ hai của chính tính năng này.
        Assert.Contains("user/detail?data=", src);
        Assert.DoesNotContain("user/detail?user_id=", src);
    }

    [Fact]
    public void Phai_doc_dung_ten_truong_Zalo_tra_ve()
    {
        var src = Nguon();
        // Zalo trả display_name + avatar. Đọc nhầm sang "name"/"picture" kiểu Meta là ra null
        // mà vẫn coi như gọi thành công.
        Assert.Contains("display_name", src);
        Assert.Contains("\"avatar\"", src);
    }

    [Fact]
    public void Phai_doc_truong_error_cua_Zalo()
    {
        // Zalo trả HTTP 200 kèm error != 0 khi hỏng. Không đọc trường đó là tưởng thành công rồi
        // ghi đè hồ sơ khách bằng giá trị rỗng — tệ hơn cả không gọi.
        var i = Nguon().IndexOf("ContactProfileAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0, "không tìm thấy ContactProfileAsync");
        var than = Nguon()[i..];
        Assert.Contains("[\"error\"]", than[..System.Math.Min(2000, than.Length)]);
    }

    [Fact]
    public void Khong_duoc_nem_ra_ngoai()
    {
        // Cùng bài học với TelegramProfilePhotoTests: ContactProfileAsync chạy GIỮA đường nhận
        // tin. Ném ra là hỏng cả sự kiện, tức tin của khách KHÔNG vào hộp thư — đổi một lỗi nhỏ
        // (thiếu tên) lấy một lỗi to (mất tin).
        var i = Nguon().IndexOf("public async Task<ContactProfile?> ContactProfileAsync",
            System.StringComparison.Ordinal);
        Assert.True(i > 0, "không tìm thấy ContactProfileAsync");
        var than = Nguon()[i..];
        than = than[..System.Math.Min(2000, than.Length)];
        Assert.Contains("try", than);
        Assert.Contains("catch (Exception", than);
    }
}
