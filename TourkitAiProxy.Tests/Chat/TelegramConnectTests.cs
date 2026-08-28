using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Nối bot Telegram bằng MỘT nút.
///
/// <para>Trước 27/08 khai bot Telegram là: tự nghĩ ra một chuỗi bí mật, copy URL webhook trên màn
/// hình, rồi tự gõ lệnh <c>setWebhook</c> ngoài trình duyệt. Không công ty du lịch nào làm nổi —
/// đúng lý do đã phải đổi cách nối của Zalo. Nay chỉ còn một ô bot token.</para>
///
/// <para>Phần chạm mạng Telegram không test được ở đây (không có CI gọi ra ngoài), nên những gì
/// <b>hỏng im lặng</b> được canh ở mức mã nguồn — đúng lối đã dùng cho Messenger.</para>
/// </summary>
public class TelegramConnectTests
{
    private const string TepAdapter = "TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs";
    private const string TepEndpoint = "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs";

    // ── Chuỗi bí mật webhook ────────────────────────────────────────────────

    [Fact]
    public void Chuoi_bi_mat_do_MAY_CHU_sinh_va_khong_lap_lai()
    {
        var a = TelegramChatAdapter.NewWebhookSecret();
        var b = TelegramChatAdapter.NewWebhookSecret();
        Assert.NotEqual(a, b);

        // Telegram chỉ nhận A-Z a-z 0-9 _ - cho secret_token, dài 1-256. Lọt ký tự khác là
        // setWebhook bị từ chối, mà lời từ chối của họ không nói rõ vướng ở đâu.
        Assert.InRange(a.Length, 32, 256);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-',
            $"Ký tự '{c}' không hợp lệ trong secret_token của Telegram"));
    }

    [Fact]
    public void Nguoi_dung_KHONG_con_phai_tu_nghi_chuoi_bi_mat()
    {
        // Ô này từng bắt người khai tự đặt rồi tự dán vào lệnh setWebhook. Còn để lại thì họ vẫn
        // tưởng phải làm tay, mà giá trị họ gõ sẽ ĐÈ lên chuỗi máy chủ vừa sinh → webhook chết.
        var src = ChatSchemaGuardTests.DocFile(TepEndpoint);
        var i = src.IndexOf("ChatChannel.Telegram, \"Telegram\"", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy khai báo ô nhập của Telegram");
        var khoi = src.Substring(i, System.Math.Min(700, src.Length - i));
        Assert.DoesNotContain("new FieldSpec(\"webhookSecret\"", khoi);
    }

    // ── setWebhook ──────────────────────────────────────────────────────────

    [Fact]
    public void Khai_du_allowed_updates_khong_thi_hong_IM_LANG()
    {
        // ⚠️ Bẫy "sửa đủ hai chỗ": Telegram CHỈ gửi các loại nằm trong allowed_updates, và danh
        // sách MẶC ĐỊNH đã bỏ sẵn message_reaction. Viết mã bóc cảm xúc mà quên khai ở đây thì
        // không bao giờ có gói tin nào tới — không lỗi, không log, chỉ là một thứ không xảy ra.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var i = src.IndexOf("allowed_updates", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không khai allowed_updates lúc setWebhook");
        var khoi = src.Substring(System.Math.Max(0, i - 200), System.Math.Min(600, src.Length - i));
        foreach (var loai in new[]
                 { "message", "edited_message", "callback_query", "message_reaction", "my_chat_member" })
            Assert.Contains($"\"{loai}\"", khoi);
    }

    [Fact]
    public void Noi_bot_phai_xac_thuc_token_TRUOC_khi_dang_ky_webhook()
    {
        // getMe là phép thử duy nhất biết token có thật không. Đăng ký webhook trước rồi mới biết
        // token sai thì đã trỏ một URL công khai vào một bot không tồn tại, và bản ghi rác nằm lại.
        var src = ChatSchemaGuardTests.DocFile(TepAdapter);
        var vtGetMe = src.IndexOf("\"getMe\"", System.StringComparison.Ordinal);
        var vtSet = src.IndexOf("\"setWebhook\"", System.StringComparison.Ordinal);
        Assert.True(vtGetMe > 0, "Không gọi getMe");
        Assert.True(vtSet > 0, "Không gọi setWebhook");
        Assert.True(vtGetMe < vtSet, "getMe phải đứng TRƯỚC setWebhook");
    }

    [Fact]
    public void Go_ket_noi_phai_goi_deleteWebhook()
    {
        // Không gỡ thì Telegram nện vào URL cũ mãi mãi: mỗi lượt là một dòng từ chối trong log,
        // và bot vẫn tưởng nó đang được dùng. Đối chiếu dự án tham khảo mới lộ ra chỗ này.
        Assert.Contains("deleteWebhook", ChatSchemaGuardTests.DocFile(TepAdapter));

        // Lõi phải gọi DisconnectAsync — hợp đồng CHUNG của mọi kênh, không phải tên một kênh.
        //
        // ⚠️ Bản trước bắt lõi phải nhắc tới "DisconnectBotAsync" (hàm riêng của Telegram). Đến
        // khi có người tổng quát hoá đúng cách — mỗi adapter tự cài DisconnectAsync, lõi gọi đa
        // hình và không còn biết tên kênh nào — test đỏ, dù hành vi giữ nguyên và code TỐT HƠN.
        // Chính giao diện IChatChannelAdapter đã dặn: phải sửa lõi thì trừu tượng hoá đã sai;
        // guard mà đòi lõi biết tên kênh là guard đi ngược lại lời dặn đó.
        var loi = ChatSchemaGuardTests.DocFile(TepEndpoint);
        Assert.Contains("DisconnectAsync", loi);

        // Và Telegram phải thực sự cài nó, nếu không lõi gọi vào chỗ rỗng.
        Assert.Contains("DisconnectAsync", ChatSchemaGuardTests.DocFile(TepAdapter));
    }
}
