// Services/Chat/Inbox/ChatRules.cs
namespace TourkitAiProxy.Services.Chat.Inbox;

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
        if (kenh is ChatChannel.Webchat or ChatChannel.Telegram)
            return new(true, TimeSpan.MaxValue, "");

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
}
