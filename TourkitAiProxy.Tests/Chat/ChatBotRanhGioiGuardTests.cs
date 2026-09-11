using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Hai luật nghiệp vụ vừa chuẩn hoá ngày 11/09/2026, canh để chúng không lặng lẽ lệch lại.
///
/// <list type="number">
///   <item><b>Thời hạn bot nhường người thật là MỘT con số, lấy từ cấu hình công ty.</b> Nó từng
///     nằm rải ở bốn chỗ dưới dạng số 30 gõ tay, và hai đường trả lời của nhân viên dùng hai giá
///     trị khác nhau: trả lời từ app của kênh thì theo cài đặt, trả lời từ chính hộp thư — đường
///     dùng nhiều nhất — lại theo hằng số. Công ty chỉnh cài đặt xuống 5 phút sẽ thấy nó không có
///     tác dụng, và không có gì báo cho họ biết.</item>
///   <item><b>Bot và nút Gợi ý không bao giờ cùng trả lời một câu.</b> Ranh giới nằm ở đúng một
///     hàm thuần; chỗ gọi phải hỏi nó chứ không được tự suy luận lại bằng "bot có bật không".</item>
/// </list>
/// </summary>
public class ChatBotRanhGioiGuardTests
{
    private static string Endpoint()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");

    [Fact]
    public void Thoi_han_bot_cam_lay_tu_cau_hinh_cong_ty__khong_go_tay_con_so()
    {
        var src = BoChuThich(Endpoint());

        // Mọi lượt cho bot câm phải đi qua cài đặt hoặc qua hằng chung — không chỗ nào được gõ số.
        foreach (Match m in Regex.Matches(src, @"PauseBotAsync\([^;]*?\);", RegexOptions.Singleline))
        {
            var goi = m.Value;
            // Nhánh "bỏ câm ngay" truyền 0, và số 0 thì không phải một khoảng thời gian gõ tay.
            if (Regex.IsMatch(goi, @",\s*0\s*,")) continue;

            Assert.True(
                goi.Contains("MuteMinutes") || goi.Contains("BotCamPhutMacDinh") || goi.Contains("phut"),
                "Có lượt cho bot câm không lấy số phút từ cấu hình công ty:\n" + goi);
            Assert.False(Regex.IsMatch(goi, @",\s*\d+\s*,"),
                "Có lượt cho bot câm gõ thẳng số phút:\n" + goi);
        }
    }

    [Fact]
    public void Khong_con_con_so_30_go_tay_cho_thoi_han_bot_cam()
    {
        var src = BoChuThich(Endpoint());
        Assert.DoesNotContain("body.Minutes ?? 30", src);
        Assert.DoesNotContain("body.MuteMinutes ?? 30", src);

        var luat = BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatBotSettings.cs"));
        Assert.DoesNotContain("int MuteMinutes = 30", luat);
        Assert.Contains("ChatRules.BotCamPhutMacDinh", luat);
    }

    /// <summary>
    /// Đường gợi ý phải HỎI luật ranh giới, không được tự dựng lại điều kiện.
    ///
    /// <para>Tự suy luận bằng <c>cfg.Enabled</c> là bỏ mất hai vế quan trọng nhất: bot bật vẫn có
    /// thể đang câm (nhường người thật, hội thoại đã đóng), và bot bật vẫn có thể KHÔNG trả lời
    /// (hết lượt AI, nhà cung cấp hỏng) — ca sau không để lại dấu vết nào trên hội thoại, nên chỉ
    /// vế thời gian trong hàm luật mới nhận ra.</para>
    /// </summary>
    [Fact]
    public void Duong_goi_y_hoi_luat_ranh_gioi_chu_khong_tu_suy_luan()
    {
        var soan = BoChuThich(ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs"));

        Assert.Contains("ChatRules.BotDangDinhTraLoi", soan);
        Assert.Contains("GoiY.BotDangTraLoi", soan);
    }

    /// <summary>
    /// Chốt CỨNG của cả tính năng: xin gợi ý tuyệt đối không gửi gì cho khách.
    ///
    /// <para>Bộ sinh này dùng chung với worker, mà worker thì ghi tin rồi xếp hàng gửi. Một lượt
    /// chép nhầm vài dòng từ bên đó sang là bản nháp đi thẳng tới khách trước khi ai kịp đọc.</para>
    /// </summary>
    [Fact]
    public void Bo_soan_KHONG_ghi_tin_va_KHONG_xep_hang_gui()
    {
        var soan = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs");

        Assert.DoesNotContain("AppendMessageAsync", soan);
        Assert.DoesNotContain("EnqueueOutboxAsync", soan);
        Assert.DoesNotContain("SendAsync", soan);
        Assert.DoesNotContain("TouchConversationAsync", soan);
    }

    private static string BoChuThich(string src) => string.Join("\n",
        System.Linq.Enumerable.Where(src.Split('\n'),
            d => !d.TrimStart().StartsWith("//", System.StringComparison.Ordinal)
              && !d.TrimStart().StartsWith("///", System.StringComparison.Ordinal)));
}
