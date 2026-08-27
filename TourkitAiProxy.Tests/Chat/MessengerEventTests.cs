using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Các sự kiện Messenger ngoài "tin nhắn thường": cảm xúc, khách bấm nút, nguồn khách đến.
///
/// <para>Cả cụm này <b>hỏng im lặng</b>: bóc sai thì không có lỗi, không có log, chỉ là một thứ
/// không bao giờ hiện ra. Nên phải chốt bằng test trên gói tin thật.</para>
/// </summary>
public class MessengerEventTests
{
    private static MessengerChatAdapter A() => MessengerConnectTests.DungAdapterCong();

    // ── Cảm xúc ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tha_cam_xuc_KHONG_thanh_tin_moi()
    {
        // Ghi thành tin thì "❤️" hiện như một câu khách vừa nói, và mọi thứ đếm theo tin (chưa đọc,
        // xem trước, cửa sổ trả lời) đều lệch.
        var tho = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "reaction":{"mid":"mid-abc","action":"react","emoji":"❤","reaction":"love"}}]}]}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.NotNull(sk.Reaction);
        Assert.Equal("mid-abc", sk.Reaction!.ExternalMsgId);
        Assert.Equal("love", sk.Reaction.Name);
        Assert.False(sk.Reaction.Removed);

        // Không mang thân tin: nhánh xử lý cảm xúc phải về sớm, không tạo dòng nào trong hội thoại.
        Assert.Null(sk.Text);
        Assert.Null(sk.ExternalMsgId);
    }

    [Fact]
    public void Go_cam_xuc_phai_doc_duoc_la_GO()
    {
        // Meta KHÔNG gửi kèm emoji ở nhánh này. Xử lý chung một đường với "react" mà không đọc
        // action là cảm xúc đã gỡ vẫn hiện mãi trên màn hình.
        var tho = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "reaction":{"mid":"mid-abc","action":"unreact"}}]}]}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.True(sk.Reaction!.Removed);
        Assert.Equal("mid-abc", sk.Reaction.ExternalMsgId);
    }

    // ── Khách bấm nút ───────────────────────────────────────────────────────

    [Fact]
    public void Bam_nut_ghi_lai_bang_CHU_TREN_NUT()
    {
        // Ghi payload kỹ thuật thì nhân viên đọc lại hội thoại thấy "MENU_TOUR_DN" — không phải thứ
        // khách nhìn thấy lúc bấm.
        var tho = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "postback":{"mid":"mid-pb","title":"Xem tour Đà Nẵng","payload":"MENU_TOUR_DN"}}]}]}
            """;
        var sk = Assert.Single(A().Parse(tho));
        Assert.Equal("Xem tour Đà Nẵng", sk.Text);
        Assert.Equal("mid-pb", sk.ExternalMsgId);
    }

    // ── Nguồn khách đến ─────────────────────────────────────────────────────

    [Fact]
    public void Nguon_khach_doc_duoc_o_CA_BA_cho_Meta_dat()
    {
        // Meta gắn nguồn vào ba chỗ tuỳ đường khách vào. Chỉ đọc một chỗ là mất phần lớn ca — mà
        // mất là mất VĨNH VIỄN, không có API nào tra ngược "khách này đến từ quảng cáo nào".
        var quaQuangCao = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "postback":{"mid":"m1","title":"Bắt đầu","payload":"GET_STARTED",
                          "referral":{"source":"ADS","ref":"tour-he","ad_id":"ad-99"}}}]}]}
            """;
        var quayLai = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "referral":{"source":"SHORTLINK","ref":"qr-quay-le"}}]}]}
            """;
        var kemTin = """
            {"object":"page","entry":[{"id":"trang-1","messaging":[
             {"sender":{"id":"khach-1"},"recipient":{"id":"trang-1"},"timestamp":1700000000000,
              "referral":{"source":"ADS","ref":"tour-thu","ad_id":"ad-7"},
              "message":{"mid":"m2","text":"cho hỏi giá"}}]}]}
            """;

        var a = A();
        var q = Assert.Single(a.Parse(quaQuangCao));
        Assert.Equal("ADS", q.Referral!.Source);
        Assert.Equal("ad-99", q.Referral.AdId);

        var l = Assert.Single(a.Parse(quayLai));
        Assert.Equal("SHORTLINK", l.Referral!.Source);
        Assert.Equal("qr-quay-le", l.Referral.Ref);
        Assert.Null(l.Text);            // gói CHỈ có nguồn, không kèm tin

        var t = Assert.Single(a.Parse(kemTin));
        Assert.Equal("cho hỏi giá", t.Text);
        Assert.Equal("ad-7", t.Referral!.AdId);   // vừa là tin, vừa mang nguồn
    }

    [Fact]
    public void Ghi_nguon_MOT_LAN_roi_thoi()
    {
        // Khách quay lại qua một quảng cáo khác thì nguồn ĐẦU TIÊN mới là cái đã kéo họ tới. Đè lên
        // là hỏng số liệu quy công quảng cáo — mà hỏng âm thầm, không ai nhìn ra một con số quy sai.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
        var i = src.IndexOf("SetReferralAsync", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy SetReferralAsync");
        var than = src.Substring(i, System.Math.Min(900, src.Length - i));
        Assert.Contains("COALESCE(referral_source", than);
    }

    [Fact]
    public void Bao_da_xem_chi_khi_NGUOI_THAT_mo()
    {
        // Bot đọc mà cũng báo đã xem là nói dối khách: họ tưởng có nhân viên đang nhìn, rồi chờ.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/read\"", System.StringComparison.Ordinal);
        Assert.True(i > 0);
        Assert.Contains("MarkSeenAsync", src.Substring(i, System.Math.Min(1200, src.Length - i)));

        // Và KHÔNG gọi ở đường xử lý tin tự động.
        var svc = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs");
        Assert.DoesNotContain("MarkSeenAsync", svc);
    }
}
