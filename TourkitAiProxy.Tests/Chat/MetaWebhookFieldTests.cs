using Xunit;
using TourkitAiProxy.Services.Chat.Channels;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh luật <b>"sửa ĐỦ HAI chỗ"</b> của cụm Meta: một sự kiện chỉ tới hộp thư khi <i>vừa</i> được
/// đăng ký bên Meta <i>vừa</i> được bộ bóc đọc.
///
/// <para><b>Vì sao cần test chứ không cần cẩn thận hơn.</b> Quy tắc này đã được viết ra thành văn
/// trong <c>docs/superpowers/plans/2026-08-26-chat-da-kenh-ra-soat-action.md</c> — và vẫn lọt:
/// <c>message_reactions</c> được bóc trong <see cref="MetaMessagingParser"/> từ 27/08, Instagram
/// đăng ký đủ, còn Trang Facebook thì quên. Suốt từ đó khách thả tim trên Messenger là hộp thư
/// không hiện gì — <b>không lỗi, không log, không dấu vết</b>. Một quy ước mà người phải nhớ thì
/// sớm muộn cũng quên; test thì không.</para>
///
/// <para><b>Vì sao so hai danh sách với nhau</b> thay vì ghi cứng danh sách đúng: ghi cứng thì mỗi
/// lần Meta thêm trường lại phải sửa test, và người sửa sẽ sửa cho qua chuyện. So chéo thì test chỉ
/// kêu khi hai kênh dùng chung một bộ bóc mà lệch nhau — đúng cái sai cần bắt.</para>
/// </summary>
public class MetaWebhookFieldTests
{
    /// <summary>
    /// Trường mà CẢ Trang Facebook lẫn tài khoản Instagram đều phải đăng ký, vì
    /// <see cref="MetaMessagingParser"/> dùng chung cho hai kênh và có nhánh đọc chúng.
    ///
    /// <para>Danh sách này CỐ Ý ngắn: chỉ gồm thứ hai bên gọi giống hệt nhau. Những trường Meta đặt
    /// tên khác nhau giữa hai kênh (<c>message_reads</c> ↔ <c>messaging_seen</c>,
    /// <c>messaging_referrals</c> ↔ <c>messaging_referral</c>, <c>feed</c> ↔ <c>comments</c>) hay
    /// chỉ một bên có (<c>message_deliveries</c> — Meta không cấp cho Instagram) thì không so được
    /// bằng phép so tên, nên để ngoài.</para>
    /// </summary>
    private static readonly string[] BatBuocCaHai =
    {
        "messages",
        "messaging_postbacks",
        "messaging_optins",
        // Nhánh m["reaction"] trong MetaMessagingParser — chính là trường đã lọt.
        "message_reactions",
    };

    [Theory]
    [InlineData("messages")]
    [InlineData("messaging_postbacks")]
    [InlineData("messaging_optins")]
    [InlineData("message_reactions")]
    public void Trang_Facebook_phai_dang_ky_moi_su_kien_bo_boc_dung_chung_doc(string truong)
        => Assert.Contains(truong, MessengerChatAdapter.PageEvents);

    [Theory]
    [InlineData("messages")]
    [InlineData("messaging_postbacks")]
    [InlineData("messaging_optins")]
    [InlineData("message_reactions")]
    public void Instagram_phai_dang_ky_moi_su_kien_bo_boc_dung_chung_doc(string truong)
        => Assert.Contains(truong, InstagramChatAdapter.AccountEvents);

    [Fact]
    public void Hai_kenh_khong_duoc_lech_nhau_o_phan_dung_chung()
    {
        // Phép so chéo thật sự: bên nào thiếu một trường mà bên kia có, TRONG danh sách dùng
        // chung, thì đó là dấu hiệu ai đó vừa thêm sự kiện cho một kênh và quên kênh còn lại.
        var thieuOTrang = BatBuocCaHai
            .Where(t => InstagramChatAdapter.AccountEvents.Contains(t)
                     && !MessengerChatAdapter.PageEvents.Contains(t))
            .ToList();
        var thieuOInstagram = BatBuocCaHai
            .Where(t => MessengerChatAdapter.PageEvents.Contains(t)
                     && !InstagramChatAdapter.AccountEvents.Contains(t))
            .ToList();

        Assert.True(thieuOTrang.Count == 0,
            "Instagram có đăng ký nhưng Trang Facebook thì không: " + string.Join(", ", thieuOTrang)
            + ". Sự kiện đó sẽ KHÔNG BAO GIỜ tới từ Messenger, và không có lỗi nào hiện lên.");
        Assert.True(thieuOInstagram.Count == 0,
            "Trang Facebook có đăng ký nhưng Instagram thì không: " + string.Join(", ", thieuOInstagram)
            + ". Sự kiện đó sẽ KHÔNG BAO GIỜ tới từ Instagram, và không có lỗi nào hiện lên.");
    }

    [Fact]
    public void Bo_boc_doc_nhanh_cam_xuc_thi_hai_kenh_phai_dang_ky_truong_do()
    {
        // Buộc chặt vào MÃ THẬT chứ không vào trí nhớ: chừng nào bộ bóc còn đọc m["reaction"] thì
        // hai danh sách còn phải mang "message_reactions". Ai bỏ nhánh bóc đi thì test tự hết đòi.
        var nguon = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Channels/MetaMessagingParser.cs");
        if (!nguon.Contains("m[\"reaction\"]")) return;

        Assert.Contains("message_reactions", MessengerChatAdapter.PageEvents);
        Assert.Contains("message_reactions", InstagramChatAdapter.AccountEvents);
    }
}
