// Services/Chat/Inbox/ChatRules.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Domain.Chat;

/// <summary>Kết quả tính cửa sổ gửi của một kênh.</summary>
/// <param name="Open">Còn gửi được không.</param>
/// <param name="Left">Còn bao lâu nữa thì đóng (0 khi đã đóng).</param>
/// <param name="Reason">Câu nói cho NGƯỜI ĐỌC hiểu vì sao — hiện thẳng lên giao diện.</param>
public record SendWindow(bool Open, TimeSpan Left, string Reason);

/// <summary>
/// Luật thuần của hộp thư chat: cửa sổ gửi, bot có được trả lời không, gộp tin nhắn liên tiếp.
///
/// <para><b>Vì sao tách thành hàm thuần:</b> sai ở đây hỏng thật chứ không phải chuyện đẹp xấu —
/// gửi khi hết cửa sổ thì tin BIẾN MẤT trong im lặng, bot nói đè lên nhân viên thì khách đọc được
/// hai câu trả lời mâu thuẫn. Tách ra mới test hết ca biên được.</para>
/// </summary>
public static class ChatRules
{
    /// <summary>
    /// Bỏ dấu, hạ chữ thường, nối bằng gạch nối. Dùng cho <b>cả</b> lệnh gọi mẫu trả lời nhanh
    /// <b>lẫn</b> nhãn khách.
    ///
    /// <para><b>Bỏ dấu là bắt buộc.</b> Nhân viên đang gõ nhanh cho khách sẽ gõ <c>gia</c> chứ
    /// không dừng lại bật bộ gõ để ra <c>giá</c>. Giữ nguyên dấu thì gõ hai lần ra hai giá trị
    /// khác nhau, và lọc theo nhãn trả về rỗng mà không ai hiểu tại sao.</para>
    ///
    /// <para><b>Một hàm cho cả hai chỗ</b>, không viết lại lần hai: cùng vấn đề mà hai cách
    /// chuẩn hoá là "khach-vip" bên này và "khach vip" bên kia.</para>
    ///
    /// <para>Trả <b>chuỗi rỗng</b> khi không còn ký tự nào dùng được — <b>không ném</b>. Hàm thuần
    /// thì trả giá trị; ném từ đây là ép mọi chỗ gọi phải bọc <c>try</c>, kể cả chỗ chỉ muốn lọc
    /// bỏ. Chỗ nào coi rỗng là lỗi thì tự báo bằng câu nói của mình.</para>
    /// </summary>
    public static string ChuanHoaSlug(string? tho)
    {
        var s = (tho ?? "").Trim().TrimStart('/').ToLowerInvariant();
        // đ → d phải làm TRƯỚC khi bóc dấu: nó không phải nguyên âm có dấu, FormD không tách ra.
        s = s.Replace('đ', 'd');
        s = new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        return Regex.Replace(s, @"[\s-]+", "-").Trim('-');
    }

    /// Zalo: gửi tin tự do trong 48 giờ kể từ tin cuối CỦA KHÁCH.
    public static readonly TimeSpan CuaSoZalo = TimeSpan.FromHours(48);

    /// Messenger: 24 giờ.
    public static readonly TimeSpan CuaSoMessenger = TimeSpan.FromHours(24);

    /// Nhân viên trả lời xong thì bot câm bấy lâu.
    public static readonly TimeSpan BotCamMacDinh = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Còn gửi được cho khách không.
    ///
    /// <para><b>Chưa có tin nào của khách = ĐÓNG</b>, không phải mở. Cửa sổ mở ra khi KHÁCH nhắn
    /// tới; mình chủ động mở lời trước thì cả Zalo lẫn Meta đều chặn. Mặc định "mở" ở ca này là
    /// đẩy lỗi xuống tận lúc gọi API, lúc đó nhân viên đã gõ xong tin rồi.</para>
    /// </summary>
    public static SendWindow TinhCuaSo(ChatChannel kenh, DateTime? khachNhanLuc, DateTime nowUtc)
    {
        // Telegram và web không giới hạn thời gian — nhắn lại lúc nào cũng được. Đây là khác biệt
        // THẬT giữa các kênh, đừng áp một luật chung cho tất cả.
        //
        // ⚠️ TikTok nằm ở đây vì lý do KHÁC: hạn trả lời của họ không có trong tài liệu công khai.
        // Khoá ô soạn theo một con số tự đoán là tự khoá tay nhân viên vì một luật có thể không
        // tồn tại. Để mở, và nếu TikTok từ chối thì câu lỗi của họ hiện lên — thà biết muộn còn
        // hơn chặn nhầm. Tra ra hạn thật thì chuyển xuống dưới.
        if (kenh is ChatChannel.Webchat or ChatChannel.Telegram or ChatChannel.TikTok)
            return new(true, TimeSpan.MaxValue, "");

        // Zalo 48h; Messenger, Instagram và WhatsApp đều 24h.
        var han = kenh == ChatChannel.Zalo ? CuaSoZalo : CuaSoMessenger;
        var gio = (int)han.TotalHours;

        if (khachNhanLuc is null)
            return new(false, TimeSpan.Zero,
                $"Khách chưa nhắn gì nên chưa gửi được. {TenKenh(kenh)} chỉ cho trả lời trong {gio} giờ "
                + "kể từ tin của khách — mình không được chủ động mở lời.");

        var conLai = khachNhanLuc.Value + han - nowUtc;
        if (conLai <= TimeSpan.Zero)
            return new(false, TimeSpan.Zero,
                $"Đã quá {gio} giờ kể từ tin cuối của khách nên {TenKenh(kenh)} không cho gửi nữa. "
                + "Muốn liên hệ lại thì dùng tin theo mẫu (ZNS) hoặc gọi điện.");

        return new(true, conLai, "");
    }

    private static string TenKenh(ChatChannel k) => k switch
    {
        ChatChannel.Zalo => "Zalo",
        ChatChannel.Messenger => "Messenger",
        ChatChannel.Instagram => "Instagram",
        ChatChannel.WhatsApp => "WhatsApp",
        ChatChannel.TikTok => "TikTok",
        ChatChannel.Telegram => "Telegram",
        ChatChannel.Webchat => "Chat trên web",
        _ => "Kênh này",
    };

    /// <summary>
    /// Bot có được tự trả lời hội thoại này không.
    ///
    /// <para>Ba lý do câm, đều cố ý: đang trong thời hạn nhường người thật · hội thoại đã đóng ·
    /// và <b>không</b> chặn theo "đã giao cho ai" — giao việc không có nghĩa người đó đang ngồi
    /// trước màn hình, nên vẫn để bot trả lời cho tới khi họ thật sự gõ một câu.</para>
    /// </summary>
    public static bool BotDuocTraLoi(ChatConversation hoiThoai, DateTime nowUtc)
    {
        if (hoiThoai.Status == (short)ChatStatus.DaDong) return false;
        if (hoiThoai.BotResumeAt is { } moc && moc > nowUtc) return false;
        return true;
    }

    /// <summary>
    /// Đã im đủ lâu để xử lý cụm tin chưa.
    ///
    /// <para>Khách hay gõ nhiều dòng liền: "cho hỏi tour Đà Nẵng" / "đi 4 ngày" / "2 người lớn".
    /// Trả lời từng dòng là ba câu rời rạc, đọc rất ngớ ngẩn và tốn ba lượt AI. Chờ im lặng rồi
    /// gộp cả cụm thành một lượt.</para>
    /// </summary>
    /// <param name="tinCuoiLuc">Thời điểm tin gần nhất của khách.</param>
    /// <param name="imLang">Ngưỡng im lặng, mặc định 4 giây.</param>
    public static bool DenLucXuLy(DateTime tinCuoiLuc, DateTime nowUtc, TimeSpan? imLang = null)
        => nowUtc - tinCuoiLuc >= (imLang ?? TimeSpan.FromSeconds(4));

    /// <summary>
    /// Ghép cụm tin của khách thành một câu hỏi cho AI.
    ///
    /// <para>Nối bằng xuống dòng chứ không phải dấu cách: "đi 4 ngày" và "2 người lớn" là hai ý
    /// riêng, dính liền thành một dòng sẽ đọc như một câu lủng củng.</para>
    /// </summary>
    public static string GhepCum(IEnumerable<string?> cacTin)
        => string.Join("\n", cacTin.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));

    /// <summary>
    /// Rút gọn để hiện ở danh sách hội thoại. Cắt giữa chừng thì thêm dấu … cho người đọc biết
    /// là còn nữa.
    /// </summary>
    public static string TomTat(string? noiDung, int toiDa = 120)
    {
        var s = (noiDung ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= toiDa ? s : s[..toiDa].TrimEnd() + "…";
    }

    /// <summary>
    /// Có được cập nhật trạng thái tin từ <paramref name="dangCo"/> sang <paramref name="moi"/> không.
    ///
    /// <para><b>Chỉ tiến, không lùi.</b> Nền tảng không bảo đảm thứ tự webhook: "đã nhận" hoàn toàn
    /// có thể tới sau "đã xem" (hai webhook hai đường mạng, hoặc bị gửi lại). Ghi đè mù thì tin đang
    /// "đã xem" tụt về "đã nhận" — nhân viên thấy dấu tích chạy ngược, tưởng khách bỏ đọc.</para>
    ///
    /// <para><b>Hỏng KHÔNG phải mức cao nhất</b> dù số lớn nhất: gửi được rồi mà báo hỏng là vô
    /// nghĩa. Chỉ tin còn đang chờ mới hỏng được.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Tin còn trong hàng đợi thì chỉ đi được sang "đã gửi" hoặc "hỏng".</b> Kịch bản thật
    /// đã dựng được trên staging: nhân viên bấm gửi lúc 10:00:00 (tin vào hàng đợi), worker gửi lúc
    /// 10:00:03 vì nhịp 5 giây; khách đọc một tin CŨ lúc 10:00:01 → nền tảng báo mốc nước 10:00:01,
    /// mà mốc quét theo <c>created_utc</c> nên trúng luôn tin vừa tạo còn chưa rời khỏi hệ thống.
    /// Để lọt thì nhân viên thấy "khách đã xem" một tin khách chưa hề nhận.</para>
    /// </remarks>
    public static bool KhongLui(ChatState dangCo, ChatState moi)
    {
        if (moi == ChatState.Hong) return dangCo == ChatState.Cho;
        if (dangCo == ChatState.Hong) return false;   // đã hỏng thì không tự sống lại
        // Chưa gửi đi thì không thể "đã nhận"/"đã xem" — xem <remarks>.
        if (dangCo == ChatState.Cho) return moi == ChatState.DaGui;
        return (short)moi > (short)dangCo;
    }
}
