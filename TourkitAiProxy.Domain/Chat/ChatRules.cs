// Services/Chat/Inbox/ChatRules.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Nhãn Meta bắt buộc đính khi gửi NGOÀI cửa sổ 24 giờ.
///
/// <para><c>HumanAgent</c> chỉ hợp lệ khi <b>người thật</b> đang gõ. Meta cấp nhãn này để nhân
/// viên xử nốt việc dở, không phải để bot nhắn tiếp — đính nhãn cho tin của bot là vi phạm chính
/// sách và có thể bị khoá quyền nhắn tin của cả Trang.</para>
/// </summary>
public enum MetaSendTag : short { None = 0, HumanAgent = 1 }

/// <summary>Kết quả tính cửa sổ gửi của một kênh.</summary>
/// <param name="Open">Còn gửi được không.</param>
/// <param name="Left">Còn bao lâu nữa thì đóng (0 khi đã đóng).</param>
/// <param name="Reason">Câu nói cho NGƯỜI ĐỌC hiểu vì sao — hiện thẳng lên giao diện.</param>
/// <param name="Tag">Nhãn phải đính lúc gọi API. <c>None</c> ở gần hết mọi ca.</param>
public record SendWindow(bool Open, TimeSpan Left, string Reason,
    MetaSendTag Tag = MetaSendTag.None);

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
    public static string NormalizeSlug(string? tho)
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
    public static readonly TimeSpan ZaloWindow = TimeSpan.FromHours(48);

    /// Messenger: 24 giờ.
    public static readonly TimeSpan MetaWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Messenger và Instagram: <b>7 ngày</b> cho NGƯỜI THẬT trả lời, bằng nhãn <c>HUMAN_AGENT</c>.
    ///
    /// <para>Đây không phải ngoại lệ mình tự nghĩ ra — Meta mở sẵn cửa này để nhân viên xử nốt
    /// việc dở sau khi cửa sổ 24 giờ đóng. Trước 28/08/2026 mình chặn thẳng ở mốc 24 giờ, tức
    /// <b>tự bỏ 6 ngày</b> mà nền tảng vẫn cho phép: khách nhắn tối thứ Sáu, nhân viên vào sáng
    /// thứ Hai là ô soạn đã khoá, dù Messenger vẫn nhận tin bình thường.</para>
    ///
    /// <para><b>Không áp cho WhatsApp.</b> WhatsApp không có nhãn này; ngoài 24 giờ phải gửi
    /// bằng mẫu tin đã được duyệt. Gộp chung ba kênh "Meta" thành một luật là sai.</para>
    /// </summary>
    public static readonly TimeSpan MetaHumanAgentWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Số nút TỐI ĐA mỗi kênh nhận. <c>0</c> = kênh không có nút.
    ///
    /// <para><b>Vượt giới hạn là nền tảng TỪ CHỐI CẢ TIN</b>, không phải cắt bớt nút — khách
    /// không nhận được gì. Vì thế cắt ở đây trước khi gọi API, và cắt thì phải nói cho người gửi
    /// biết chứ đừng lặng lẽ bỏ.</para>
    ///
    /// <para>Con số khác nhau THẬT: Meta cho 3 nút trong khung nút nhưng 13 nút trả lời nhanh;
    /// WhatsApp 3; Zalo 5; Telegram không giới hạn thực tế. Áp một con số chung là hoặc tự bó
    /// tay mình, hoặc để tin biến mất ở kênh chặt nhất.</para>
    /// </summary>
    public static int MaxButtons(ChatChannel kenh, bool coLienKet) => kenh switch
    {
        // Nút mở liên kết phải đi trong "khung nút" (button template) — khung đó chỉ chứa 3.
        // Không có liên kết thì đi bằng trả lời nhanh, thoải mái hơn nhiều.
        ChatChannel.Messenger or ChatChannel.Instagram => coLienKet ? 3 : 13,
        // WhatsApp: nút tương tác tối đa 3, và KHÔNG nhận nút mở liên kết trong cùng một tin.
        ChatChannel.WhatsApp => coLienKet ? 0 : 3,
        ChatChannel.Zalo => 5,
        ChatChannel.Telegram => 8,
        // TikTok không có nút nào cả.
        _ => 0,
    };

    /// <summary>
    /// Cắt danh sách nút cho vừa kênh. Trả thêm câu giải thích khi có nút bị bỏ — im lặng cắt
    /// thì nhân viên soạn năm nút, khách thấy ba, và không ai biết vì sao.
    /// </summary>
    public static (IReadOnlyList<ChatButton> Nut, string? CanhBao) FitButtons(
        ChatChannel kenh, IReadOnlyList<ChatButton> nut)
    {
        if (nut.Count == 0) return (nut, null);

        var coLienKet = nut.Any(x => x.IsLink);
        var toiDa = MaxButtons(kenh, coLienKet);

        if (toiDa == 0)
            return (Array.Empty<ChatButton>(), coLienKet && kenh == ChatChannel.WhatsApp
                ? "WhatsApp không nhận nút mở liên kết trong tin thường. Gửi đường dẫn bằng chữ, "
                  + "hoặc dùng tin mẫu đã duyệt."
                : $"{ChannelName(kenh)} không có nút bấm. Tin vẫn gửi, nhưng chỉ phần chữ.");

        if (nut.Count <= toiDa) return (nut, null);

        return (nut.Take(toiDa).ToList(),
            $"{ChannelName(kenh)} chỉ nhận {toiDa} nút nên đã bỏ {nut.Count - toiDa} nút cuối.");
    }

    /// <summary>
    /// Đọc danh sách nút từ JSON đã lưu. <b>Hỏng thì trả RỖNG, không ném</b>: tin vẫn phải gửi
    /// được dù phần nút hỏng — mất mấy cái nút còn hơn mất cả câu trả lời cho khách.
    /// </summary>
    public static IReadOnlyList<ChatButton> ReadButtons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ChatButton>();
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonArray ds)
                return Array.Empty<ChatButton>();

            var ra = new List<ChatButton>();
            foreach (var x in ds)
            {
                var chu = x?["chu"]?.ToString();
                if (string.IsNullOrWhiteSpace(chu)) continue;   // nút không có chữ thì vô nghĩa

                var url = x?["url"]?.ToString();
                // CHỈ nhận http(s). Nền tảng từ chối các lược đồ khác, mà javascript: thì là lỗ
                // hổng — nút do người dùng tự đặt nên đây là dữ liệu KHÔNG tin được.
                if (!string.IsNullOrWhiteSpace(url)
                    && !url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = null;

                ra.Add(new(chu!.Trim(), string.IsNullOrWhiteSpace(url) ? null : url));
            }
            return ra;
        }
        catch { return Array.Empty<ChatButton>(); }
    }

    /// Nhân viên trả lời xong thì bot câm bấy lâu.
    public static readonly TimeSpan DefaultBotMute = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Còn gửi được cho khách không.
    ///
    /// <para><b>Chưa có tin nào của khách = ĐÓNG</b>, không phải mở. Cửa sổ mở ra khi KHÁCH nhắn
    /// tới; mình chủ động mở lời trước thì cả Zalo lẫn Meta đều chặn. Mặc định "mở" ở ca này là
    /// đẩy lỗi xuống tận lúc gọi API, lúc đó nhân viên đã gõ xong tin rồi.</para>
    /// </summary>
    /// <param name="nguoiGui">Ai đang gửi. Chỉ <see cref="ChatSender.Agent"/> mới được dùng cửa
    /// 7 ngày của Messenger/Instagram — xem <see cref="MetaSendTag"/>. Mặc định là bot, tức
    /// <b>chặt hơn</b>: chỗ gọi nào quên truyền thì mất quyền, không phải được thêm quyền.</param>
    public static SendWindow ComputeSendWindow(ChatChannel kenh, DateTime? khachNhanLuc,
        DateTime nowUtc, ChatSender nguoiGui = ChatSender.Ai)
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
        var han = kenh == ChatChannel.Zalo ? ZaloWindow : MetaWindow;
        var gio = (int)han.TotalHours;

        if (khachNhanLuc is null)
            return new(false, TimeSpan.Zero,
                $"Khách chưa nhắn gì nên chưa gửi được. {ChannelName(kenh)} chỉ cho trả lời trong {gio} giờ "
                + "kể từ tin của khách — mình không được chủ động mở lời.");

        var conLai = khachNhanLuc.Value + han - nowUtc;
        if (conLai > TimeSpan.Zero) return new(true, conLai, "");

        // Hết cửa sổ thường. Messenger/Instagram còn một cửa nữa: NGƯỜI THẬT được trả lời tới
        // 7 ngày bằng nhãn HUMAN_AGENT. Bot thì không — xem MetaSendTag.
        if (kenh is ChatChannel.Messenger or ChatChannel.Instagram)
        {
            var conLaiNguoiThat = khachNhanLuc.Value + MetaHumanAgentWindow - nowUtc;
            if (conLaiNguoiThat > TimeSpan.Zero)
                return nguoiGui == ChatSender.Agent
                    ? new(true, conLaiNguoiThat, "", MetaSendTag.HumanAgent)
                    : new(false, TimeSpan.Zero,
                        $"Đã quá {gio} giờ kể từ tin cuối của khách nên trợ lý không được tự trả lời nữa. "
                        + "Nhân viên vẫn nhắn tay được, trong 7 ngày kể từ tin của khách.");

            return new(false, TimeSpan.Zero,
                $"Đã quá 7 ngày kể từ tin cuối của khách nên {ChannelName(kenh)} không cho gửi nữa. "
                + "Muốn liên hệ lại thì gọi điện hoặc nhắn qua kênh khác.");
        }

        return new(false, TimeSpan.Zero,
            $"Đã quá {gio} giờ kể từ tin cuối của khách nên {ChannelName(kenh)} không cho gửi nữa. "
            + (kenh == ChatChannel.WhatsApp
                ? "Muốn liên hệ lại thì dùng mẫu tin WhatsApp đã được duyệt hoặc gọi điện."
                : "Muốn liên hệ lại thì dùng tin theo mẫu (ZNS) hoặc gọi điện."));
    }

    /// <summary>Tên kênh cho câu nói với người dùng. Công khai vì tầng endpoint cũng cần —
    /// để mỗi chỗ tự đặt tên riêng thì cùng một kênh có hai cái tên trên hai màn hình.</summary>
    public static string ChannelName(ChatChannel k) => k switch
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
    public static bool BotMayReply(ChatConversation hoiThoai, DateTime nowUtc)
    {
        if (hoiThoai.Status == (short)ChatStatus.Closed) return false;
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
    public static bool DueAt(DateTime tinCuoiLuc, DateTime nowUtc, TimeSpan? imLang = null)
        => nowUtc - tinCuoiLuc >= (imLang ?? TimeSpan.FromSeconds(4));

    /// <summary>
    /// Ghép cụm tin của khách thành một câu hỏi cho AI.
    ///
    /// <para>Nối bằng xuống dòng chứ không phải dấu cách: "đi 4 ngày" và "2 người lớn" là hai ý
    /// riêng, dính liền thành một dòng sẽ đọc như một câu lủng củng.</para>
    /// </summary>
    public static string JoinBurst(IEnumerable<string?> cacTin)
        => string.Join("\n", cacTin.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));

    /// <summary>
    /// Rút gọn để hiện ở danh sách hội thoại. Cắt giữa chừng thì thêm dấu … cho người đọc biết
    /// là còn nữa.
    /// </summary>
    public static string Summarize(string? noiDung, int toiDa = 120)
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
    public static bool CanAdvanceState(ChatState dangCo, ChatState moi)
    {
        if (moi == ChatState.Failed) return dangCo == ChatState.Pending;
        if (dangCo == ChatState.Failed) return false;   // đã hỏng thì không tự sống lại
        // Chưa gửi đi thì không thể "đã nhận"/"đã xem" — xem <remarks>.
        if (dangCo == ChatState.Pending) return moi == ChatState.Sent;
        return (short)moi > (short)dangCo;
    }
}
