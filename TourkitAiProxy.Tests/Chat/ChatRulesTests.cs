using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Luật của hộp thư chat. Ba thứ ở đây sai là hỏng THẬT, không phải chuyện đẹp xấu:
///
/// <list type="bullet">
/// <item><b>Cửa sổ gửi</b> — gửi khi đã đóng thì Zalo/Meta từ chối và tin BIẾN MẤT trong im lặng.
/// Nhân viên tưởng đã trả lời khách, khách thì không nhận được gì.</item>
/// <item><b>Bot câm khi người thật vào</b> — sai thì khách đọc được hai câu trả lời, một của nhân
/// viên một của máy, có khi mâu thuẫn nhau ngay trước mặt.</item>
/// <item><b>Gộp tin liên tiếp</b> — sai thì mỗi dòng khách gõ là một câu trả lời rời rạc, và tốn
/// gấp mấy lần lượt AI.</item>
/// </list>
/// </summary>
public class ChatRulesTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    // ── Cửa sổ gửi ──────────────────────────────────────────────────────────

    [Fact]
    public void Zalo_con_trong_48_gio_thi_gui_duoc()
    {
        var w = ChatRules.ComputeSendWindow(ChatChannel.Zalo, Now.AddHours(-47.9), Now);
        Assert.True(w.Open);
        Assert.Equal("", w.Reason);
    }

    [Fact]
    public void Zalo_qua_48_gio_thi_dong_va_noi_ro_ly_do()
    {
        var w = ChatRules.ComputeSendWindow(ChatChannel.Zalo, Now.AddHours(-48.1), Now);
        Assert.False(w.Open);
        // Lý do phải đọc được, và phải chỉ đường đi tiếp — không chỉ báo "lỗi".
        Assert.Contains("48 giờ", w.Reason);
        Assert.Contains("ZNS", w.Reason);
    }

    [Fact]
    public void Messenger_han_24_gio_chu_khong_phai_48()
    {
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddHours(-23.9), Now).Open);
        Assert.False(ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddHours(-24.1), Now).Open);
    }

    [Fact]
    public void Khach_chua_nhan_gi_thi_DONG_chu_khong_phai_mo()
    {
        // Ca dễ làm sai nhất: null nghĩa là chưa ai mở lời, tức cửa sổ CHƯA TỪNG mở.
        // Coi là "mở" thì lỗi bị đẩy xuống tận lúc gọi API, sau khi nhân viên đã gõ xong tin.
        var w = ChatRules.ComputeSendWindow(ChatChannel.Zalo, null, Now);
        Assert.False(w.Open);
        Assert.Contains("Khách chưa nhắn", w.Reason);
    }

    [Fact]
    public void Webchat_khong_co_gioi_han_thoi_gian()
    {
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Webchat, null, Now).Open);
    }

    [Fact]
    public void Telegram_khong_co_cua_so_nen_chu_dong_nhan_truoc_duoc()
    {
        // Khác Zalo/Messenger: Telegram cho nhắn lại lúc nào cũng được, miễn khách chưa chặn bot.
        // Áp luật 24h cho nó là tự khoá tay mình vô cớ.
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Telegram, null, Now).Open);
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Telegram, Now.AddDays(-30), Now).Open);
    }

    [Fact]
    public void Moi_kenh_mot_han_rieng_khong_dung_chung_mot_luat()
    {
        var luc = Now.AddHours(-30);   // quá 24h nhưng chưa quá 48h
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Zalo, luc, Now).Open);        // 48h → còn
        Assert.False(ChatRules.ComputeSendWindow(ChatChannel.Messenger, luc, Now).Open);  // 24h → hết
        Assert.True(ChatRules.ComputeSendWindow(ChatChannel.Telegram, luc, Now).Open);    // không hạn
    }

    // ── Bot câm ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dang_trong_han_nhuong_nguoi_that_thi_bot_cam()
    {
        var c = new ChatConversation { BotResumeAt = Now.AddMinutes(10) };
        Assert.False(ChatRules.BotMayReply(c, Now));
    }

    [Fact]
    public void Het_han_nhuong_thi_bot_noi_lai()
    {
        var c = new ChatConversation { BotResumeAt = Now.AddMinutes(-1) };
        Assert.True(ChatRules.BotMayReply(c, Now));
    }

    [Fact]
    public void Hoi_thoai_da_dong_thi_bot_cam()
    {
        var c = new ChatConversation { Status = (short)ChatStatus.Closed };
        Assert.False(ChatRules.BotMayReply(c, Now));
    }

    [Fact]
    public void Giao_viec_cho_ai_do_KHONG_lam_bot_cam()
    {
        // Cố ý: giao việc không có nghĩa người đó đang ngồi trước màn hình. Câm ngay lúc giao thì
        // khách bị bỏ rơi cho tới khi nhân viên mở máy.
        var c = new ChatConversation { AssignedUsername = "an" };
        Assert.True(ChatRules.BotMayReply(c, Now));
    }

    // ── Gộp tin liên tiếp ───────────────────────────────────────────────────

    [Fact]
    public void Khach_dang_go_tiep_thi_chua_xu_ly()
    {
        Assert.False(ChatRules.DueAt(Now.AddSeconds(-2), Now));
    }

    [Fact]
    public void Im_du_lau_thi_xu_ly_ca_cum()
    {
        Assert.True(ChatRules.DueAt(Now.AddSeconds(-5), Now));
    }

    [Fact]
    public void Ghep_cum_noi_bang_xuong_dong_va_bo_dong_rong()
    {
        var s = ChatRules.JoinBurst(new[] { "cho hỏi tour Đà Nẵng", "  ", null, "đi 4 ngày" });
        Assert.Equal("cho hỏi tour Đà Nẵng\nđi 4 ngày", s);
    }

    // ── Tóm tắt hiện ở danh sách ────────────────────────────────────────────

    [Fact]
    public void Tom_tat_gop_khoang_trang_va_bo_xuong_dong()
    {
        Assert.Equal("a b c", ChatRules.Summarize("a\nb   c"));
    }

    [Fact]
    public void Tom_tat_dai_thi_cat_va_them_dau_ba_cham()
    {
        var s = ChatRules.Summarize(new string('x', 200));
        Assert.Equal(121, s.Length);   // 120 ký tự + dấu …
        Assert.EndsWith("…", s);
    }
}
