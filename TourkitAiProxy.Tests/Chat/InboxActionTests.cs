using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh các quyết định thiết kế của nhóm hành động hộp thư — những chỗ mà làm sai thì
/// <b>không có lỗi nào hiện lên</b>, chỉ có nút bấm không ra kết quả.
///
/// <para><b>Vì sao canh bằng mã nguồn chứ không bằng hàm thuần.</b> Bản đầu của bộ test này gọi
/// một hàm <c>ChatRules.MocChuaDoc</c> viết riêng cho việc lùi mốc đọc — nhưng câu lệnh thật lại
/// tự trừ khoảng thời gian ngay trong SQL, nên hàm đó <b>không chỗ nào gọi</b>. Ba test xanh mà
/// canh một hàm chết: đổi <c>1 millisecond</c> thành <c>1 second</c> trong SQL thì vẫn xanh. Guard
/// không bao giờ đỏ được thì tệ hơn không có guard, vì nó tạo cảm giác đã được canh.</para>
/// </summary>
public class InboxActionTests
{
    private static string Repo()
        => ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    /// <summary>Bóc thân một phương thức của repository ra để soi câu SQL bên trong.</summary>
    private static string Than(string ten, int daiToiDa = 1400)
    {
        var i = Repo().IndexOf(ten, System.StringComparison.Ordinal);
        Assert.True(i > 0, $"Không thấy {ten} trong ChatRepository");
        var s = Repo();
        return s.Substring(i, System.Math.Min(daiToiDa, s.Length - i));
    }

    [Fact]
    public void Danh_dau_chua_doc_phai_DAT_moc_chu_khong_XOA_dong()
    {
        // Xoá dòng đọc thì phép tính chưa đọc lùi về cột chung agent_last_read_at — vốn có thể vẫn
        // mới vì người khác vừa mở — và hội thoại vẫn hiện là ĐÃ đọc. Người dùng bấm nút, không
        // thấy gì đổi, và không có lỗi nào để lần ra.
        var than = Than("MarkUnreadAsync");

        Assert.Contains("INSERT INTO chat_conversation_reads", than);
        Assert.Contains("ON CONFLICT", than);
        Assert.DoesNotContain("DELETE FROM chat_conversation_reads", than);
    }

    [Fact]
    public void Moc_chua_doc_phai_lui_ve_TRUOC_tin_cuoi_cua_khach()
    {
        // Danh sách tính chưa đọc bằng phép so LỚN HƠN THỰC SỰ:
        //     contact_replied_at > last_read_at
        // nên đặt mốc BẰNG ĐÚNG thời điểm tin cuối là không đủ — bấm nút mà không có gì xảy ra.
        // Phải trừ đi một khoảng, và khoảng đó phải NHỎ: trừ cả phút thì những tin khách gửi
        // trước đó vài giây cũng thành chưa đọc oan.
        var than = Than("MarkUnreadAsync");

        var m = Regex.Match(than, @"created_utc\s*-\s*interval\s*'(\d+)\s*(millisecond|second)s?'");
        Assert.True(m.Success,
            "Câu lệnh phải trừ một khoảng khỏi created_utc của tin cuối, nếu không hội thoại "
            + "vẫn hiện là đã đọc — xem phép so trong ListConversationsAsync.");

        var so = int.Parse(m.Groups[1].Value);
        var giay = m.Groups[2].Value == "second" ? so : so / 1000.0;
        Assert.True(giay > 0, "Khoảng trừ phải lớn hơn 0");
        Assert.True(giay <= 1, "Trừ quá 1 giây là kéo theo tin cũ thành chưa đọc oan");
    }

    [Fact]
    public void Danh_dau_chua_doc_phai_khoa_dung_cong_ty()
    {
        // Không khoá tenant thì một id đoán được của công ty khác cũng đánh dấu được — và vì
        // bảng đọc không có khoá ngoại sang hội thoại, câu lệnh vẫn chạy trơn tru.
        var than = Than("MarkUnreadAsync");
        Assert.Contains("c.tenant_id = @tenant", than);
        Assert.Contains("m.tenant_id = @tenant", than);
    }

    [Fact]
    public void Chi_lay_tin_CUA_KHACH_lam_moc()
    {
        // Lấy tin cuối bất kỳ thì câu trả lời của chính nhân viên cũng thành mốc, và hội thoại
        // vừa được trả lời xong lại nhảy lên "chưa đọc".
        Assert.Contains("m.direction = 0", Than("MarkUnreadAsync"));
    }

    // ── Chặn khách ──────────────────────────────────────────────────────────

    [Fact]
    public void Khach_bi_chan_thi_bot_KHONG_duoc_tra_loi()
    {
        // Bot trả lời một người đã bị công ty chặn là mâu thuẫn ngay trước mắt khách: hộp thư ẩn
        // họ đi trong khi trợ lý vẫn tiếp chuyện.
        var bayGio = new System.DateTime(2026, 8, 28, 10, 0, 0, System.DateTimeKind.Utc);

        Assert.True(TourkitAiProxy.Domain.Chat.ChatRules.BotMayReply(
            new TourkitAiProxy.Domain.Chat.ChatConversation(), bayGio));
        Assert.False(TourkitAiProxy.Domain.Chat.ChatRules.BotMayReply(
            new TourkitAiProxy.Domain.Chat.ChatConversation { BlockedUtc = bayGio }, bayGio));
    }

    [Fact]
    public void Duong_gui_cung_phai_chan_chu_khong_chi_bot()
    {
        // Người trực vẫn mở được hội thoại cũ ra gõ tay. Chặn mỗi ở bot thì tin của người vẫn đi.
        var than = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs");
        Assert.Contains("BlockedUtc", than);
    }

    [Fact]
    public void Chan_khach_ghi_vao_DANH_BA_chu_khong_phai_hoi_thoai()
    {
        // Khách nhắn lại qua một hội thoại khác (bình luận dưới bài chẳng hạn) thì vẫn phải bị
        // chặn. Ghi cờ lên hội thoại là chặn hụt.
        var than = Than("SetContactBlockedAsync");
        Assert.Contains("UPDATE chat_contacts", than);
        Assert.Contains("blocked_utc", than);
    }

    // ── Xoá tin / sửa tin ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, true)]    // chờ gửi — sửa được, đây là ca hữu ích nhất
    [InlineData(4, true)]    // gửi hỏng — sửa rồi gửi lại
    [InlineData(1, false)]   // đã gửi
    [InlineData(2, false)]   // đã nhận
    [InlineData(3, false)]   // đã xem
    public void Chi_sua_duoc_tin_CHUA_ra_khoi_may(short trangThai, bool mong)
    {
        // Tin đã đi rồi thì khách đã thấy bản gốc VĨNH VIỄN — không nền tảng nào cho sửa lại phía
        // họ. Sửa bản của mình lúc đó là làm hộp thư nói dối về thứ khách thật sự nhận được, và
        // đó là kiểu sai không ai phát hiện ra cho tới lúc đối chất với khách.
        Assert.Equal(mong, TourkitAiProxy.Domain.Chat.ChatRules.CoTheSuaTin(trangThai));
    }

    [Fact]
    public void Sua_tin_phai_kiem_trang_thai_NGAY_TRONG_cau_lenh()
    {
        // Kiểm ở tầng trên rồi mới ghi là có cửa sổ: worker gửi có thể vừa nhặt đúng tin đó lên
        // giữa hai lượt. Điều kiện phải nằm trong chính câu UPDATE.
        var than = Than("EditPendingMessageAsync");
        Assert.Contains("UPDATE chat_messages", than);
        Assert.Contains("state IN (0, 4)", than);
        Assert.Contains("tenant_id = @tenant", than);
    }

    [Fact]
    public void Xoa_tin_la_xoa_MEM_chu_khong_xoa_dong()
    {
        // Người trực có thể đã đọc và đã hành động theo câu đó. Xoá sạch thì lịch sử nói dối rằng
        // chuyện đó chưa từng xảy ra — cùng lý do với bình luận khách tự xoá.
        var than = Than("SoftDeleteMessageAsync");
        Assert.Contains("UPDATE chat_messages", than);
        Assert.Contains("deleted_utc", than);
        Assert.DoesNotContain("DELETE FROM chat_messages", than);
    }

    [Fact]
    public void Tin_da_xoa_phai_ra_TOI_giao_dien()
    {
        // Cột deleted_utc có từ đợt bình luận, nhưng ChatMessage không mang nó nên dấu xoá ghi vào
        // CSDL rồi nằm im — hộp thư vẫn hiện nguyên nội dung. Lời hứa "hiện đã bị xoá" trong chú
        // thích schema chỉ thành thật khi cả ba tầng cùng mang cờ này.
        Assert.Contains("public DateTime? DeletedUtc",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs"));
        Assert.Contains("deleted = m.DeletedUtc is not null",
            ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs"));
        Assert.Contains("Tin đã bị xoá",
            ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx"));
    }

    [Fact]
    public void Giao_dien_phai_noi_ro_xoa_chi_o_phia_minh()
    {
        // Không nói thì nhân viên tưởng đã thu hồi được câu lỡ tay và không đi xin lỗi khách.
        // Đây là hậu quả thật, không phải chuyện chữ nghĩa.
        var ui = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("KHÁCH VẪN THẤY", ui);
    }
}
