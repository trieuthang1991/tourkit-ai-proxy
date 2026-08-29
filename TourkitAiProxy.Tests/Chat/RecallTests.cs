using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh cơ chế thu hồi tin.
///
/// <para><b>Điều quan trọng nhất phải giữ:</b> nút "Thu hồi" chỉ được phép tồn tại ở nơi nó nói
/// thật. Meta (Messenger · Instagram · WhatsApp) <b>không cấp API thu hồi cho phía doanh nghiệp</b>
/// — thu hồi là tính năng của ứng dụng người dùng. Nên cách duy nhất để nút đó không nói dối là
/// giữ tin lại vài giây trước khi gửi, và rút nó khỏi hàng đợi trong quãng đó.</para>
///
/// <para>ChatbotX có nút <c>delete-message</c> nhưng đọc mã thì nó chỉ xoá trong CSDL của họ —
/// khách vẫn thấy nguyên tin. Đó chính là kiểu nói dối mà bộ test này canh không cho lặp lại.</para>
/// </summary>
public class RecallTests
{
    private static string Repo()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    private static string Than(string ten, int dai = 1400)
    {
        var s = Repo();
        var i = s.IndexOf(ten, System.StringComparison.Ordinal);
        Assert.True(i > 0, $"Không thấy {ten} trong ChatRepository");
        return s.Substring(i, System.Math.Min(dai, s.Length - i));
    }

    // ── Luật thuần ──────────────────────────────────────────────────────────

    [Fact]
    public void Chi_hoan_tin_cua_NGUOI_THAT()
    {
        // Trợ lý đã chờ 4 giây gộp tin trước khi soạn; hoãn thêm nữa là khách ngồi nhìn màn hình
        // trống lâu hơn. Mà trợ lý cũng không phải thứ gõ nhầm — guardrail đã lọc trước rồi.
        Assert.Equal(5, ChatRules.HoanGuiGiay(ChatSender.Agent, 5));
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.Ai, 5));
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.System, 5));
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.Customer, 5));
    }

    [Fact]
    public void Dat_0_la_tat_han_tinh_nang()
        => Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.Agent, 0));

    [Theory]
    [InlineData(-5, 0)]      // cấu hình sai dấu — đừng biến thành lỗi lúc chạy
    [InlineData(999, 60)]    // trần: giữ khách chờ hơn một phút đã là quá nhiều
    public void Kep_gia_tri_cau_hinh_hong(int caiDat, int mong)
        => Assert.Equal(mong, ChatRules.HoanGuiGiay(ChatSender.Agent, caiDat));

    // ── Hàng đợi phải tôn trọng giờ hẹn ─────────────────────────────────────

    [Fact]
    public void Worker_gui_KHONG_duoc_nhat_tin_chua_toi_gio()
    {
        // Đây là toàn bộ cơ chế thu hồi. Thiếu điều kiện này thì tin vẫn đi ngay và nút Thu hồi
        // thành nút trang trí — bấm được nhưng không bao giờ kịp.
        // Neo vào CHỮ KÝ chứ không vào tên trần: tên này còn nằm trong chú thích của ClaimMediaAsync
        // ("cùng lối với ClaimOutboxAsync"), mà chú thích đó đứng trước trong file.
        var than = Than("Task<List<OutboxRow>> ClaimOutboxAsync");
        Assert.Contains("send_after IS NULL OR send_after <= now()", than);
    }

    [Fact]
    public void Huy_khoi_hang_doi_phai_khoa_ca_cong_ty_LAN_hoi_thoai()
    {
        // Chỉ khoá message_id thì một id đoán được của công ty khác cũng huỷ được tin của họ.
        var than = Than("CancelOutboxAsync");
        Assert.Contains("tenant_id = @tenant", than);
        Assert.Contains("conversation_id = @conv", than);
    }

    [Fact]
    public void Chi_huy_duoc_tin_CHUA_toi_gio_gui()
    {
        // Tin đã tới giờ (hoặc đang được worker xử lý) thì huỷ là nói dối: nó có thể đã rời máy
        // chủ giữa chừng. Điều kiện phải nằm trong chính câu lệnh, không chỉ ở tầng trên.
        var than = Than("CancelOutboxAsync");
        Assert.Contains("send_after > now()", than);
        Assert.Contains("status = 0", than);
    }

    [Fact]
    public void Giao_dien_phai_dem_nguoc_theo_moc_CUA_MAY_CHU()
    {
        // Suy ra từ createdUtc + số giây trong cấu hình là sai ngay khi quản trị đổi cấu hình,
        // và lệch luôn khi đồng hồ máy khách chạy sai. Mốc có thẩm quyền là send_after.
        Assert.Contains("public DateTime? SendAfterUtc",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs"));
        Assert.Contains("send_after", Than("ListMessagesAsync", 900));
        Assert.Contains("sendAfterUtc",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs"));
    }

    // ── Thu hồi THẬT: chỉ Telegram ──────────────────────────────────────────

    [Fact]
    public void Chi_Telegram_thu_hoi_that_duoc()
    {
        // Nếu mai có người cho Messenger cài IMessageRecaller thì test này đỏ, và người đó phải
        // dừng lại đọc: Meta KHÔNG có API thu hồi cho doanh nghiệp, cài vào là hứa suông với
        // người trực — và họ sẽ không đi xin lỗi khách vì tưởng đã rút lại được.
        Assert.True(typeof(IMessageRecaller).IsAssignableFrom(typeof(TelegramChatAdapter)));

        foreach (var t in new[] { typeof(MessengerChatAdapter), typeof(InstagramChatAdapter),
                                  typeof(WhatsAppChatAdapter), typeof(ZaloChatAdapter),
                                  typeof(TikTokChatAdapter) })
            Assert.False(typeof(IMessageRecaller).IsAssignableFrom(t),
                $"{t.Name} không có API thu hồi cho phía doanh nghiệp — cài IMessageRecaller là hứa suông");
    }

    [Fact]
    public void Cua_so_thu_hoi_cua_Telegram_dung_48_gio()
    {
        // Telegram cho bot xoá tin của chính nó trong 48 giờ. Khai rộng hơn là nút hiện ra rồi
        // bấm vào báo lỗi; khai hẹp hơn là tự bỏ mất quãng còn cứu được.
        //
        // Đọc mã nguồn chứ không dựng đối tượng: adapter cần bốn phụ thuộc mà bộ test này không
        // có, và dựng bằng null là thứ vỡ ngay khi ai đó thêm một phép kiểm trong hàm dựng.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs");
        Assert.Contains("RecallWindow => TimeSpan.FromHours(48)", src);
        Assert.Contains("\"deleteMessage\"", src);
    }
}
