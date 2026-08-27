using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Cấu hình trợ lý chat và trí nhớ hội thoại.
///
/// <para>Hai thứ này sửa hai lỗi thật của bản trước 28/08/2026: mọi công ty dùng chung một lời dặn
/// nằm trong file cấu hình máy chủ, và bot chỉ nhận đúng cụm tin vừa tới nên không hiểu câu hỏi nối
/// tiếp.</para>
/// </summary>
public class ChatBotSettingsTests
{
    private static ChatMessage Tin(short chieu, string chu, ChatState tt = ChatState.Sent)
        => new() { Direction = chieu, Body = chu, State = (short)tt };

    private static ChatMessage Khach(string chu) => Tin((short)ChatDirection.In, chu, ChatState.Delivered);
    private static ChatMessage Minh(string chu, ChatState tt = ChatState.Sent) => Tin((short)ChatDirection.Out, chu, tt);

    // ── Lời dặn ─────────────────────────────────────────────────────────────

    [Fact]
    public void Loi_dan_cong_ty_NOI_THEM_chu_khong_thay_the_khung()
    {
        // Khung chứa luật chống bịa giá tour. Cho công ty thay cả khung là họ vô tình xoá luật đó,
        // rồi bot bắt đầu bịa giá và hứa giữ chỗ với KHÁCH THẬT — hỏng không rút lại được.
        const string khung = "TUYỆT ĐỐI KHÔNG được bịa giá tour.";
        var ra = new ChatBotSettings(Persona: "Bên em chuyên tour Nhật.").BuildSystemPrompt(khung);

        Assert.Contains("Bên em chuyên tour Nhật.", ra);
        Assert.Contains(khung, ra);
    }

    [Fact]
    public void Khung_dat_SAU_loi_dan_cong_ty()
    {
        // Phần cuối là phần model bám chặt nhất, nên các luật cấm phải nằm cuối — một câu vô ý
        // trong phần công ty tự viết ("cứ báo giá cho khách đi") không được đè lên.
        const string khung = "TUYỆT ĐỐI KHÔNG được bịa giá tour.";
        var ra = new ChatBotSettings(Persona: "Cứ báo giá luôn cho khách.").BuildSystemPrompt(khung);

        Assert.True(ra.IndexOf("Cứ báo giá luôn") < ra.IndexOf(khung));
    }

    [Fact]
    public void Khong_khai_loi_dan_rieng_thi_giu_nguyen_khung()
    {
        const string khung = "khung";
        Assert.Equal(khung, ChatBotSettings.Default.BuildSystemPrompt(khung));
        Assert.Equal(khung, new ChatBotSettings(Persona: "   ").BuildSystemPrompt(khung));
    }

    [Fact]
    public void Gia_tri_ngoai_khoang_bi_kep_lai()
    {
        // Đọc lại 500 tin thì vừa tốn tiền vừa loãng — model bám vào chuyện tuần trước thay vì câu
        // khách vừa hỏi. Đọc 0 tin thì mất hẳn trí nhớ.
        Assert.Equal(ChatBotSettings.MaxHistoryTurns, new ChatBotSettings(HistoryTurns: 500).Normalized().HistoryTurns);
        Assert.Equal(ChatBotSettings.MinHistoryTurns, new ChatBotSettings(HistoryTurns: 0).Normalized().HistoryTurns);
        Assert.Equal(0, new ChatBotSettings(MuteMinutes: -5).Normalized().MuteMinutes);

        var dai = new string('x', ChatBotSettings.MaxPersonaChars + 500);
        Assert.Equal(ChatBotSettings.MaxPersonaChars, new ChatBotSettings(Persona: dai).Normalized().Persona!.Length);
    }

    [Fact]
    public void Chuoi_rong_ve_null_chu_khong_giu_khoang_trang()
    {
        // Ô nhập để trống gửi lên "" hoặc "  ". Giữ nguyên thì BuildSystemPrompt tưởng có lời dặn
        // và chèn cả cái tiêu đề rỗng vào prompt.
        Assert.Null(new ChatBotSettings(Persona: "   ").Normalized().Persona);
        Assert.Null(new ChatBotSettings(Greeting: "").Normalized().Greeting);
    }

    // ── Trí nhớ hội thoại ───────────────────────────────────────────────────

    [Fact]
    public void Bot_doc_lai_doan_hoi_thoai_chu_khong_chi_cau_vua_hoi()
    {
        // Đây là lỗi cũ: khách hỏi "Thế còn tháng 10?" mà bot không biết đang nói về tour nào.
        var ra = ChatRules.BuildConversationPrompt(new[]
        {
            Khach("Tour Nhật bao nhiêu tiền ạ?"),
            Minh("Dạ em kiểm tra rồi báo lại anh ngay."),
        }, "Thế còn tháng 10?", 12);

        Assert.Contains("Tour Nhật bao nhiêu tiền ạ?", ra);
        Assert.Contains("Dạ em kiểm tra rồi báo lại anh ngay.", ra);
        Assert.Contains("Thế còn tháng 10?", ra);

        // Câu MỚI phải tách hẳn khỏi phần lịch sử — nối thẳng vào cuối thì model coi nó là một dòng
        // nữa để đọc, không phải câu đang phải trả lời.
        Assert.Contains("Khách vừa nhắn:", ra);
        Assert.True(ra.IndexOf("Khách vừa nhắn:") > ra.IndexOf("Tour Nhật bao nhiêu"));
    }

    [Fact]
    public void Tin_HONG_va_tin_CHO_GUI_khong_vao_ngu_canh()
    {
        // Khách chưa hề đọc chúng. Đưa vào là bot tưởng mình đã nói rồi và trả lời tiếp như thể
        // khách đã biết.
        var ra = ChatRules.BuildConversationPrompt(new[]
        {
            Khach("Cho em hỏi tour Hàn"),
            Minh("Câu này gửi hỏng", ChatState.Failed),
            Minh("Câu này còn đang chờ gửi", ChatState.Pending),
        }, "Alo?", 12);

        Assert.DoesNotContain("gửi hỏng", ra);
        Assert.DoesNotContain("đang chờ gửi", ra);
        Assert.Contains("tour Hàn", ra);
    }

    [Fact]
    public void Chi_lay_phan_DUOI_khi_hoi_thoai_dai()
    {
        var dai = Enumerable.Range(1, 50).Select(i => Khach("tin " + i)).ToList();
        var ra = ChatRules.BuildConversationPrompt(dai, "mới nhất", soLuot: 3);

        Assert.Contains("tin 50", ra);
        Assert.Contains("tin 48", ra);
        Assert.DoesNotContain("tin 47", ra);
        Assert.DoesNotContain("tin 1:", ra);
    }

    [Fact]
    public void Hoi_thoai_moi_tinh_thi_gui_nguyen_cau_hoi_khong_them_khung_rong()
    {
        // Khách nhắn lần đầu: không có gì để nhắc lại. Bọc thêm "Đoạn hội thoại từ đầu tới giờ:"
        // rồi để trống là dạy model rằng có một đoạn hội thoại nó không được thấy.
        Assert.Equal("Chào shop", ChatRules.BuildConversationPrompt(Array.Empty<ChatMessage>(), "Chào shop", 12));
    }

    [Fact]
    public void Nhan_vien_va_tro_ly_ghi_CHUNG_mot_nhan()
    {
        // Với khách thì cả hai đều là công ty. Tách ra chỉ mời model bắt chước giọng của một trong hai.
        var ra = ChatRules.BuildConversationPrompt(new[] { Minh("Dạ em gửi giá ngay") }, "Cảm ơn", 12);
        Assert.Contains("Mình: Dạ em gửi giá ngay", ra);
    }
}
