using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Instagram — kênh thứ tư.
///
/// <para>Đi CÙNG hợp đồng nhắn tin của Meta với Messenger, nên phần bóc tin dùng chung
/// (<see cref="MetaMessagingParser"/>). Bộ test này canh đúng những chỗ hai kênh <b>khác nhau
/// thật</b>: đường gửi, khoá ký, và cách báo "khách đã xem" — cả ba đều hỏng im lặng nếu áp
/// một luật chung.</para>
/// </summary>
public class InstagramEventTests
{
    private const string TepAdapter = "TourkitAiProxy.Services/Chat/Channels/InstagramChatAdapter.cs";

    private static InstagramChatAdapter A()
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
        return new InstagramChatAdapter(null!, cred, cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InstagramChatAdapter>.Instance);
    }

    // ── Bóc tin ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tin_khach_gui_gan_dung_kenh_Instagram()
    {
        // Gói tin của Instagram giống hệt Messenger, chỉ khác trường "object". Nếu gắn nhầm kênh
        // thì hội thoại rơi vào nhánh Messenger: cửa sổ gửi, đường gửi và khoá ký đều sai.
        var tho = """
            {"object":"instagram","entry":[{"id":"ig-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"ig-1"},"timestamp":1700000000000,
              "message":{"mid":"mid-1","text":"Tour Phú Quốc còn chỗ không ạ?"}}]}]}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.Equal(ChatChannel.Instagram, sk.Channel);
        Assert.Equal("khach-1", sk.ExternalUserId);
        Assert.Equal("Tour Phú Quốc còn chỗ không ạ?", sk.Text);
    }

    [Fact]
    public void Khach_da_xem_bao_bang_mid_chu_KHONG_bang_watermark()
    {
        // ⚠️ Khác biệt THẬT với Messenger. Messenger gửi {"read":{"watermark":<ms>}}; Instagram gửi
        // {"read":{"mid":"<tin cuối đã đọc>"}} — KHÔNG có watermark. Đọc theo lối Messenger thì giá
        // trị ra null, sự kiện rơi im lặng, và dấu tích Instagram đứng mãi ở "đã gửi".
        var tho = """
            {"object":"instagram","entry":[{"id":"ig-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"ig-1"},"timestamp":1700000000000,
              "read":{"mid":"mid-cuoi"}}]}]}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.NotNull(sk.Watermark);
        Assert.Equal(ChatState.DaXem, sk.Watermark!.State);
        // Mốc thời gian phải tra từ chính tin đó, nên ở đây chỉ mang mã tin sang cho lõi tra tiếp.
        Assert.Equal("mid-cuoi", sk.Watermark.ExternalMsgId);
    }

    [Fact]
    public void Instagram_KHONG_bao_da_nhan()
    {
        // Meta không có message_deliveries cho Instagram. Đừng "sửa" bằng cách tự nhảy trạng thái
        // khi gửi xong — như thế là nói dối nhân viên rằng khách đã nhận trong khi mình không biết.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        Assert.Contains("message_deliveries", src);   // phải NÓI RÕ là không có, không im lặng
    }

    // ── Cửa sổ gửi ──────────────────────────────────────────────────────────

    [Fact]
    public void Cua_so_gui_Instagram_la_24_gio_nhu_Messenger()
    {
        var moc = new DateTime(2026, 8, 27, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(ChatRules.TinhCuaSo(ChatChannel.Instagram, moc, moc.AddHours(23)).Open);
        Assert.False(ChatRules.TinhCuaSo(ChatChannel.Instagram, moc, moc.AddHours(25)).Open);
    }

    [Fact]
    public void Chua_co_tin_nao_cua_khach_thi_cua_so_DONG_va_noi_dung_ten_kenh()
    {
        var kq = ChatRules.TinhCuaSo(ChatChannel.Instagram, null, DateTime.UtcNow);
        Assert.False(kq.Open);
        // Câu báo phải gọi đúng tên kênh — "Kênh này" là dấu hiệu quên khai tên.
        Assert.Contains("Instagram", kq.Reason);
    }

    // ── Đường gửi và khoá ký ────────────────────────────────────────────────

    [Fact]
    public void Gui_qua_graph_instagram_com_bang_header_Bearer()
    {
        // Instagram KHÔNG nhận ?access_token= trên URL như Graph của Facebook, và cũng không nằm
        // trên cùng tên miền. Chép nguyên đường gửi của Messenger sang là mọi tin gửi đi đều hỏng.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        Assert.Contains("graph.instagram.com", src);
        Assert.Contains("Bearer", src);

        // Soi ĐÚNG hàm gửi, không soi cả file: bước dò tài khoản liên kết hỏi VỀ Trang nên đi qua
        // Graph của Facebook một cách hợp lệ. Hai tên miền cùng tồn tại là bình thường; cái sai là
        // gửi TIN qua graph.facebook.com.
        var i = src.IndexOf("private async Task<SendResult> GuiAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy hàm gửi");
        var than = src.Substring(i, System.Math.Min(1200, src.Length - i));
        Assert.DoesNotContain("graph.facebook.com", than);
    }

    [Fact]
    public void Dang_ky_du_truong_webhook()
    {
        // Bẫy "sửa đủ hai chỗ": bóc được mà không đăng ký thì không bao giờ có gói tin nào tới.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var i = src.IndexOf("SuKienTaiKhoan", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không ghi danh sách trường webhook nào");
        var khoi = src.Substring(System.Math.Max(0, i - 500), System.Math.Min(900, src.Length - i));
        foreach (var truong in new[]
                 { "messages", "messaging_postbacks", "messaging_seen", "messaging_referral" })
            Assert.Contains($"\"{truong}\"", khoi);
    }

    [Fact]
    public void Van_kiem_chu_ky_sau_khi_tra_ra_cong_ty()
    {
        // Id tài khoản Instagram nằm công khai; tra ra công ty KHÔNG chứng minh tin là thật.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        Assert.Contains("X-Hub-Signature-256", src);
        Assert.Contains("HMACSHA256", src);
    }

    [Fact]
    public void Duong_webhook_dung_chung_LUON_tra_200_ke_ca_khi_tu_choi()
    {
        // Instagram đi qua CHÍNH ứng dụng Meta của Messenger. Meta tự động ngừng gửi cho ứng dụng
        // nào trả lỗi liên tục — nên trả 401 cho một gói tin rác là tắt kênh của MỌI khách hàng
        // cùng lúc, không phải chỉ của người gửi gói đó.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("MapPost(\"/api/v1/chat/webhook/instagram\"", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Chưa có đường webhook dùng chung cho Instagram");

        var than = src.Substring(i, System.Math.Min(2000, src.Length - i));
        Assert.DoesNotContain("Unauthorized", than);
        Assert.DoesNotContain("StatusCode(401)", than);
        // Và mỗi lượt từ chối phải để lại dấu vết: hỏng im lặng thì không có chỗ nào lần ra.
        Assert.Contains("LogWarning", than);
    }
}
