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
    public void Qua_24_gio_thi_NHAN_VIEN_van_nhan_duoc_toi_7_ngay()
    {
        // Meta mở sẵn cửa này (nhãn HUMAN_AGENT) để nhân viên xử nốt việc dở. Trước 28/08/2026
        // mình chặn thẳng ở mốc 24 giờ, tức tự bỏ 6 ngày nền tảng vẫn cho phép: khách nhắn tối
        // thứ Sáu, nhân viên vào sáng thứ Hai là ô soạn đã khoá.
        foreach (var kenh in new[] { ChatChannel.Messenger, ChatChannel.Instagram })
        {
            var w = ChatRules.ComputeSendWindow(kenh, Now.AddDays(-3), Now, ChatSender.Agent);
            Assert.True(w.Open);
            Assert.Equal(MetaSendTag.HumanAgent, w.Tag);
            // Còn lại đếm tới mốc 7 ngày chứ không phải 24 giờ.
            Assert.InRange(w.Left.TotalDays, 3.9, 4.1);
        }
    }

    [Fact]
    public void Cua_7_ngay_chi_danh_cho_nguoi_that_KHONG_cho_bot()
    {
        // Đính nhãn HUMAN_AGENT cho tin của bot là vi phạm chính sách Meta và có thể bị khoá
        // quyền nhắn tin của cả Trang. Mặc định của hàm phải là CHẶT (bot), không phải lỏng.
        foreach (var ai in new[] { ChatSender.Ai, ChatSender.System })
        {
            var w = ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddDays(-3), Now, ai);
            Assert.False(w.Open);
            Assert.Equal(MetaSendTag.None, w.Tag);
            Assert.Contains("trợ lý không được tự trả lời", w.Reason);
            Assert.Contains("Nhân viên vẫn nhắn tay được", w.Reason);
        }

        // Quên truyền tham số = coi như bot. Chỗ gọi nào sót thì MẤT quyền, không phải được thêm.
        Assert.False(ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddDays(-3), Now).Open);
    }

    [Fact]
    public void Qua_7_ngay_thi_nguoi_that_cung_khong_gui_duoc()
    {
        var w = ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddDays(-7.1), Now, ChatSender.Agent);
        Assert.False(w.Open);
        Assert.Equal(MetaSendTag.None, w.Tag);
        Assert.Contains("7 ngày", w.Reason);
    }

    [Fact]
    public void WhatsApp_KHONG_co_cua_7_ngay_vi_khong_co_nhan_HUMAN_AGENT()
    {
        // WhatsApp ngoài 24 giờ phải đi bằng MẪU TIN đã được duyệt, không có nhãn nào cả. Gộp ba
        // kênh "của Meta" thành một luật là chỗ dễ sai nhất ở đây.
        var w = ChatRules.ComputeSendWindow(ChatChannel.WhatsApp, Now.AddDays(-3), Now, ChatSender.Agent);
        Assert.False(w.Open);
        Assert.Equal(MetaSendTag.None, w.Tag);
        Assert.Contains("mẫu tin WhatsApp", w.Reason);
        Assert.DoesNotContain("ZNS", w.Reason);
    }

    [Fact]
    public void Trong_24_gio_thi_KHONG_dinh_nhan_du_la_nhan_vien_gui()
    {
        // Đính nhãn khi không cần là tự lấy hạn mức của một loại tin khác mà chẳng được gì.
        var w = ChatRules.ComputeSendWindow(ChatChannel.Messenger, Now.AddHours(-2), Now, ChatSender.Agent);
        Assert.True(w.Open);
        Assert.Equal(MetaSendTag.None, w.Tag);
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

    // ── Tách câu hỏi cuối — nền cho gợi ý trả lời ───────────────────────────

    private static ChatMessage Tin(short huong, string? body,
        short state = (short)ChatState.Sent, short kind = (short)ChatKind.Text,
        DateTime? luc = null)
        => new() { Direction = huong, Body = body, State = state, Kind = kind,
                   CreatedUtc = luc ?? new DateTime(2026, 9, 11, 9, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Tach_cau_hoi_cuoi__tin_khach_moi_nhat_la_cau_hoi__phan_truoc_la_lich_su()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
            Tin((short)ChatDirection.In,  "Tháng 10, 4 người"),
        };

        var (cauHoi, _, truoc) = ChatRules.TachCauHoiCuoi(ds);

        Assert.Equal("Tháng 10, 4 người", cauHoi);
        Assert.Equal(2, truoc.Count);
        Assert.Equal("Dạ anh đi tháng mấy ạ?", truoc[1].Body);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__minh_vua_tra_loi_xong_thi_KHONG_co_gi_de_goi_y()
    {
        // Tin mới nhất là của MÌNH → khách chưa nói gì thêm. Gợi ý lúc này là gợi ý trả lời cho
        // một câu đã được trả lời — sinh ra câu thứ hai chồng lên câu thứ nhất.
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
        };

        Assert.Null(ChatRules.TachCauHoiCuoi(ds).CauHoi);
    }

    /// <summary>
    /// Tin của mình ĐANG XẾP HÀNG GỬI cũng tính là "mình đã nói".
    ///
    /// <para>Đây là chỗ dễ sai nhất và nó không tự lộ ra. <c>BuildConversationPrompt</c> cố ý bỏ
    /// tin <c>Pending</c> khỏi phần lịch sử — vì tin chưa tới tay khách thì đưa vào là bot tưởng
    /// mình đã nói rồi. Nhưng ở ĐÂY luật phải ngược lại: câu trả lời của bot vừa được xếp hàng
    /// (ChatInboundService lưu nó với trạng thái Pending) mà mình lờ đi thì tin khách lại thành
    /// tin mới nhất, nút Gợi ý sinh tiếp một câu nữa — khách nhận hai câu trả lời khác nhau cho
    /// cùng một câu hỏi, cách nhau vài giây.</para>
    /// </summary>
    [Fact]
    public void Tach_cau_hoi_cuoi__tin_minh_dang_xep_hang_gui_cung_tinh_la_da_noi()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Tour Nhật bao nhiêu tiền?"),
            Tin((short)ChatDirection.Out, "Dạ để em kiểm tra giúp anh ạ", state: (short)ChatState.Pending),
        };

        Assert.Null(ChatRules.TachCauHoiCuoi(ds).CauHoi);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__bo_qua_tin_hong_va_tin_khong_co_chu()
    {
        // Tin hỏng thì khách KHÔNG nhận được, nên nó không phải "mình đã nói". Ảnh và sticker
        // không có chữ để đưa vào nhắc — cùng luật với BuildConversationPrompt.
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Câu thật"),
            Tin((short)ChatDirection.In,  null, kind: (short)ChatKind.Image),
            Tin((short)ChatDirection.Out, "gửi hỏng", state: (short)ChatState.Failed),
        };

        var (cauHoi, _, truoc) = ChatRules.TachCauHoiCuoi(ds);

        Assert.Equal("Câu thật", cauHoi);
        Assert.Empty(truoc);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__hoi_thoai_rong_tra_null_chu_khong_nem()
    {
        var (cauHoi, _, truoc) = ChatRules.TachCauHoiCuoi(System.Array.Empty<ChatMessage>());
        Assert.Null(cauHoi);
        Assert.Empty(truoc);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__tra_luon_moc_gio_cua_cau_hoi()
    {
        // Mốc giờ là thứ luật "bot có đang định trả lời không" dựa vào — thiếu nó thì chỗ gọi
        // phải tự đi lọc lại danh sách tin, tức có hai luật lọc ở hai nơi.
        var luc = new DateTime(2026, 9, 11, 8, 30, 0, DateTimeKind.Utc);
        var ds = new[] { Tin((short)ChatDirection.In, "Khách hỏi", luc: luc) };

        Assert.Equal(luc, ChatRules.TachCauHoiCuoi(ds).HoiLuc);
    }

    // ── Ranh giới: bot tự trả lời ↔ nhân viên xin gợi ý ─────────────────────

    /// <summary>
    /// Hai đường cùng sinh ra câu trả lời cho khách, nên phải có đúng MỘT chỗ nói khi nào đường
    /// nào được nói. Không có luật này thì khách nhận hai câu khác nhau cho một câu hỏi.
    ///
    /// <para>Cửa sổ thời gian là mấu chốt. Bot chỉ trả lời trong khoảng chục giây sau khi khách
    /// nhắn (4 giây gộp tin cộng vài giây gọi AI). Quá cửa đó mà vẫn chưa có câu trả lời nào
    /// nghĩa là bot KHÔNG trả lời nữa — hết lượt AI, nhà cung cấp hỏng, hoặc bị tắt giữa chừng —
    /// và đó chính là lúc nhân viên cần nút Gợi ý nhất.</para>
    /// </summary>
    [Fact]
    public void Khach_vua_nhan_va_bot_dang_bat_thi_BOT_lo__goi_y_phai_nhuong()
    {
        var ht = new ChatConversation();
        Assert.True(ChatRules.BotDangDinhTraLoi(ht, botBat: true, Now.AddSeconds(-3), Now));
    }

    [Fact]
    public void Bot_tat_thi_goi_y_lam_viec_ngay_khong_phai_cho()
    {
        var ht = new ChatConversation();
        Assert.False(ChatRules.BotDangDinhTraLoi(ht, botBat: false, Now.AddSeconds(-3), Now));
    }

    [Fact]
    public void Bot_dang_nhuong_nguoi_that_thi_goi_y_lam_viec()
    {
        // Nhân viên vừa trả lời → BotResumeAt còn hiệu lực → bot câm → gợi ý là nguồn duy nhất.
        var ht = new ChatConversation { BotResumeAt = Now.AddMinutes(10) };
        Assert.False(ChatRules.BotDangDinhTraLoi(ht, botBat: true, Now.AddSeconds(-3), Now));
    }

    [Fact]
    public void Qua_cua_so_ma_bot_van_chua_noi_gi_thi_coi_nhu_bot_KHONG_tra_loi()
    {
        // Ca quý nhất của nút Gợi ý: bot bật, được phép nói, nhưng im vì hết lượt AI hoặc nhà
        // cung cấp hỏng. Không có vế thời gian này thì nhân viên vĩnh viễn nhận câu "bot đang
        // trả lời" cho một con bot không bao giờ trả lời.
        var ht = new ChatConversation();
        Assert.False(ChatRules.BotDangDinhTraLoi(ht, botBat: true,
            Now - ChatRules.CuaSoBotTraLoi.Add(TimeSpan.FromSeconds(1)), Now));
    }

    [Fact]
    public void Hoi_thoai_dong_hoac_khach_bi_chan_thi_bot_khong_lo__goi_y_lam_viec()
    {
        Assert.False(ChatRules.BotDangDinhTraLoi(
            new ChatConversation { Status = (short)ChatStatus.Closed }, true, Now.AddSeconds(-3), Now));
        Assert.False(ChatRules.BotDangDinhTraLoi(
            new ChatConversation { BlockedUtc = Now }, true, Now.AddSeconds(-3), Now));
    }

    [Fact]
    public void Khong_co_cau_hoi_nao_thi_bot_cung_khong_lo()
    {
        Assert.False(ChatRules.BotDangDinhTraLoi(new ChatConversation(), true, null, Now));
    }

    // ── Tóm tắt đoạn chat để gửi sang CRM ───────────────────────────────────

    [Fact]
    public void Tom_tat_cham_soc__ghi_ro_ai_noi_cau_nao()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật tháng 10"),
            Tin((short)ChatDirection.Out, "Dạ để em gửi anh lịch trình ạ"),
        };

        var ra = ChatRules.TomTatChamSoc(ds);

        Assert.Contains("Khách: Cho hỏi tour Nhật tháng 10", ra);
        Assert.Contains("Nhân viên: Dạ để em gửi anh lịch trình ạ", ra);
    }

    /// <summary>
    /// Cắt theo GIỚI HẠN KÝ TỰ, không theo số tin — và cắt từ đầu, giữ phần cuối.
    ///
    /// <para>Cột nội dung chăm sóc bên CRM có trần độ dài. Một hội thoại chăm khách vài tuần thừa
    /// sức vượt; gửi quá trần thì CRM từ chối và dòng hàng đợi hỏng, mà lúc đó người bấm nút đã
    /// rời máy từ lâu. Giữ phần CUỐI vì đó là phần gần chốt nhất.</para>
    /// </summary>
    [Fact]
    public void Tom_tat_cham_soc__cat_theo_tran_ky_tu_va_noi_ro_da_cat()
    {
        var ds = System.Linq.Enumerable.Range(0, 60)
            .Select(i => Tin((short)ChatDirection.In, "Câu số " + i + " " + new string('x', 80)))
            .ToArray();

        var ra = ChatRules.TomTatChamSoc(ds, tranKyTu: 600);

        Assert.True(ra.Length <= 600, $"Dài {ra.Length} ký tự, vượt trần 600");
        Assert.Contains("Câu số 59", ra);          // giữ phần cuối
        Assert.DoesNotContain("Câu số 0 ", ra);    // bỏ phần đầu
        Assert.Contains("…", ra);                  // và nói ra là đã cắt
    }

    [Fact]
    public void Tom_tat_cham_soc__bo_tin_hong_va_tin_khong_chu()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Câu thật"),
            Tin((short)ChatDirection.In,  null, kind: (short)ChatKind.Image),
            Tin((short)ChatDirection.Out, "gửi hỏng", state: (short)ChatState.Failed),
        };

        var ra = ChatRules.TomTatChamSoc(ds);

        Assert.Contains("Câu thật", ra);
        Assert.DoesNotContain("gửi hỏng", ra);
    }

    [Fact]
    public void Tom_tat_cham_soc__hoi_thoai_rong_tra_chuoi_rong_chu_khong_nem()
    {
        Assert.Equal("", ChatRules.TomTatChamSoc(System.Array.Empty<ChatMessage>()));
    }

    // ── Tóm tắt cho Cơ hội bán hàng ─────────────────────────────────────────

    [Fact]
    public void Tom_tat_co_hoi__N_tin_cuoi__ghi_ro_ai_noi__kem_duong_dan()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Tin cũ nhất, phải bị cắt"),
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
            Tin((short)ChatDirection.In,  null, kind: (short)ChatKind.Image),
            Tin((short)ChatDirection.In,  "Tháng 10, 4 người"),
        };

        var ra = ChatRules.TomTatChoCoHoi(ds, 3, "https://travelai.vn/chat-inbox?hoi-thoai=53");

        Assert.DoesNotContain("Tin cũ nhất", ra);
        Assert.Contains("Khách: Cho hỏi tour Nhật", ra);
        Assert.Contains("Nhân viên: Dạ anh đi tháng mấy ạ?", ra);
        Assert.Contains("Khách: Tháng 10, 4 người", ra);
        Assert.EndsWith("https://travelai.vn/chat-inbox?hoi-thoai=53", ra.TrimEnd());
    }

    /// <summary>
    /// Đường dẫn về hội thoại phải còn KỂ CẢ khi không trích được câu nào.
    ///
    /// <para>Phiếu Cơ hội tạo từ một hội thoại toàn ảnh vẫn cần lối quay về hội thoại đó — thiếu
    /// nó thì người xử lý phiếu chỉ có một ô nội dung trống và không có cách nào tìm lại nguồn.</para>
    /// </summary>
    [Fact]
    public void Tom_tat_co_hoi__khong_co_tin_chu_thi_van_con_duong_dan()
    {
        var ra = ChatRules.TomTatChoCoHoi(System.Array.Empty<ChatMessage>(), 5, "https://x/y");
        Assert.Contains("https://x/y", ra);
    }
}
