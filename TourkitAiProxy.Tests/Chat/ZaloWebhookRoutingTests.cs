using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Định tuyến webhook Zalo khi TourKit sở hữu MỘT ứng dụng cho mọi khách hàng.
///
/// <para>Trước đây mỗi công ty một ứng dụng riêng nên <c>app_id</c> phân biệt được ai với ai, và
/// tên công ty còn nằm sẵn trên URL webhook. Nay <c>app_id</c> <b>giống hệt nhau ở mọi khách</b>
/// và URL không mang tên công ty nữa — khoá định tuyến duy nhất còn lại là <b>id OA</b>.</para>
///
/// <para>Zalo không đặt id OA vào một chỗ cố định, và đó chính là cái bẫy: lấy nhầm đầu thì tra ra
/// id của KHÁCH, không khớp công ty nào, tin rơi vào hư không mà chỉ còn một dòng log.</para>
/// </summary>
public class ZaloWebhookRoutingTests
{
    [Fact]
    public void Tin_khach_gui_thi_OA_la_NGUOI_NHAN()
    {
        var tho = """
            {"app_id":"9","event_name":"user_send_text","timestamp":"1",
             "sender":{"id":"khach-123"},"recipient":{"id":"oa-777"},
             "message":{"msg_id":"m1","text":"chào"}}
            """;
        Assert.Equal("oa-777", ZaloChatAdapter.IdOaCuaSuKien(tho));
    }

    [Fact]
    public void Tieng_vong_OA_gui_thi_OA_la_NGUOI_GUI()
    {
        // Nhân viên trả lời từ app Zalo OA. Ở đây hai đầu ĐẢO NGƯỢC so với tin khách gửi — dùng
        // chung một quy tắc cho cả hai là tra ra id khách rồi không khớp công ty nào.
        var tho = """
            {"app_id":"9","event_name":"oa_send_text","timestamp":"1",
             "sender":{"id":"oa-777"},"recipient":{"id":"khach-123"},
             "message":{"msg_id":"m2","text":"vâng"}}
            """;
        Assert.Equal("oa-777", ZaloChatAdapter.IdOaCuaSuKien(tho));
    }

    [Fact]
    public void Khach_da_xem_thi_OA_van_la_NGUOI_NHAN()
    {
        var tho = """
            {"app_id":"9","event_name":"user_seen_message","timestamp":"1",
             "sender":{"id":"khach-123"},"recipient":{"id":"oa-777"}}
            """;
        Assert.Equal("oa-777", ZaloChatAdapter.IdOaCuaSuKien(tho));
    }

    [Fact]
    public void Su_kien_gan_nhan_thi_lay_thang_truong_oa_id()
    {
        // Nhóm sự kiện này KHÔNG có sender/recipient — chỉ có oa_id. Không đọc trường đó trước là
        // rơi xuống nhánh sender/recipient rồi trả null.
        var tho = """{"app_id":"9","event_name":"add_user_to_tag","oa_id":"oa-777","timestamp":"1"}""";
        Assert.Equal("oa-777", ZaloChatAdapter.IdOaCuaSuKien(tho));
    }

    [Theory]
    [InlineData("")]
    [InlineData("khong-phai-json")]
    [InlineData("{}")]
    [InlineData("""{"event_name":"user_send_text"}""")]
    public void Than_tin_hong_thi_tra_null_chu_khong_nem(string tho)
    {
        // Đường webhook là CÔNG KHAI: ném ở đây là biến một gói tin rác thành lỗi 500.
        Assert.Null(ZaloChatAdapter.IdOaCuaSuKien(tho));
    }

    [Fact]
    public void Duong_webhook_dung_chung_KHONG_mang_ten_cong_ty()
    {
        // Mang tên công ty trên URL thì mỗi khách lại phải tự dán một URL khác vào cổng Zalo —
        // đúng cái việc mà ứng dụng dùng chung sinh ra để bỏ đi.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        Assert.Contains("\"/api/v1/chat/webhook/zalo\"", src);
    }

    [Fact]
    public void Webhook_dung_chung_LUON_tra_200_ke_ca_khi_tu_choi()
    {
        // Zalo nói thẳng: "Webhook của bạn chỉ được thiết lập khi trả về http code 200 OK". Họ gọi
        // thử URL lúc lưu bằng một gói RỖNG — không chữ ký, không id OA. Trả 401 ở đó là KHÔNG BAO
        // GIỜ lưu được URL, tức là cả tính năng chết trước khi bắt đầu. Đã mất một buổi vì chỗ này.
        //
        // Trả 200 không nới lỏng gì: từ chối vẫn là không ghi gì vào hộp thư.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("MapPost(\"/api/v1/chat/webhook/zalo\"", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy đường webhook dùng chung");

        var het = src.IndexOf("MapGet(\"/api/v1/chat/webhook/zalo\"", i, System.StringComparison.Ordinal);
        var than = het > i ? src[i..het] : src[i..];
        Assert.DoesNotContain("Results.Unauthorized()", than);
    }

    [Fact]
    public void Co_ca_duong_GET_cho_luot_goi_thu()
    {
        // Zalo có thể gọi thử bằng GET tuỳ lúc; không có đường GET là 405 và lượt kiểm cũng trượt.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        Assert.Contains("MapGet(\"/api/v1/chat/webhook/zalo\"", src);
    }
    [Fact]
    public void Van_kiem_chu_ky_sau_khi_tra_ra_cong_ty()
    {
        // Tra được công ty KHÔNG có nghĩa là tin thật: id OA không phải bí mật, ai biết đường dẫn
        // cũng đoán được. Bỏ bước kiểm chữ ký là mở cửa cho người ngoài bơm tin vào hộp thư.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Channels/ZaloChatAdapter.cs");
        var i = src.IndexOf("XacMinhDungChungAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0);
        var than = src.Substring(i, System.Math.Min(1200, src.Length - i));
        Assert.Contains("VerifyAsync(", than);
    }
}
