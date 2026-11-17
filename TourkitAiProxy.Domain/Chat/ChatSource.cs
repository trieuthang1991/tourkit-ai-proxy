namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// <b>Nguồn</b> của một hội thoại — nơi khách thật sự đang nhắn từ đó (Facebook, Zalo,
/// Instagram…), KHÁC với <see cref="ChatChannel"/> là đường tin đi vào hệ thống.
///
/// <para>Hai thứ này trùng nhau ở mọi kênh nối thẳng: tin vào qua kênh Zalo thì nguồn là Zalo.
/// Chúng TÁCH ra ở <b>cổng gom</b> (Pancake): mọi hội thoại đều vào qua một kênh duy nhất, còn
/// nguồn thật nằm ở từng Trang — Trang này của Facebook, Trang kia của Zalo. Dải lọc dựng theo
/// kênh vì thế gộp tất cả vào một tab "Pancake" và người trực mất hẳn thông tin họ cần nhất:
/// khách này nhắn từ đâu.</para>
///
/// <para><b>Khoá máy, chữ thường, không dấu</b> — nó đi trên URL (<c>?source=facebook</c>) và
/// được so bằng ở cả máy chủ lẫn giao diện. Tên hiển thị ("Facebook") là việc của giao diện;
/// đổi tên hiển thị không được chạm vào khoá này.</para>
///
/// <para>⚠️ Giao diện giữ một bản sao của bảng này (<c>NGUON_CUA_KENH</c> trong
/// <c>wwwroot/pages/chat-inbox.jsx</c>) vì nó phải tự tính nguồn cho từng tab mà không gọi thêm
/// một lượt về máy chủ. Hai bản lệch nhau thì tab đếm một đằng, danh sách lọc ra một nẻo — có
/// chốt <c>ChatSourceTests</c> so hai bảng, sửa bên này nhớ sửa cả bên kia.</para>
/// </summary>
public static class ChatSource
{
    /// <summary>Nguồn của hội thoại qua cổng gom mà <b>không biết</b> nền tảng thật.</summary>
    /// <remarks>
    /// Để nguyên tên cổng chứ KHÔNG đoán bừa là Facebook. Trang Pancake thiếu <c>platform</c> là
    /// chuyện thường (nối bằng khoá một-trang, hoặc Pancake thêm nền tảng mới mà mình chưa biết),
    /// và nói sai nguồn còn tệ hơn nói "chưa rõ": người trực chọn giọng và hạn trả lời theo nguồn.
    /// </remarks>
    public const string ChuaRo = "pancake";

    /// <summary>
    /// Kênh này có phải <b>cổng gom</b> không — một "tài khoản" của nó là một Trang thuộc nền
    /// tảng khác, nên nguồn phải tra theo TỪNG TÀI KHOẢN chứ không suy ra từ kênh.
    /// </summary>
    public static bool LaCongGom(ChatChannel kenh) => kenh == ChatChannel.Pancake;

    /// <inheritdoc cref="LaCongGom(ChatChannel)"/>
    public static bool LaCongGom(short kenh) => LaCongGom((ChatChannel)kenh);

    /// <summary>
    /// Nguồn mặc định của một kênh — dùng cho kênh nối thẳng, và cho hội thoại của cổng gom mà
    /// không tra ra tài khoản.
    /// </summary>
    public static string CuaKenh(ChatChannel kenh) => kenh switch
    {
        ChatChannel.Zalo => "zalo",
        // Kênh tên "Messenger" nhưng nguồn là Facebook: khách nhắn từ Trang Facebook, và Trang đó
        // cũng có thể đang đổ về qua Pancake. Hai đường cùng một nguồn thì phải gộp chung một tab,
        // không thì cùng một Trang hiện ở hai chỗ với hai cái tên.
        ChatChannel.Messenger => "facebook",
        ChatChannel.Webchat => "web",
        ChatChannel.Telegram => "telegram",
        ChatChannel.Instagram => "instagram",
        ChatChannel.WhatsApp => "whatsapp",
        ChatChannel.TikTok => "tiktok",
        ChatChannel.Internal => "internal",
        ChatChannel.Pancake => ChuaRo,
        _ => ChuaRo,
    };

    /// <inheritdoc cref="CuaKenh(ChatChannel)"/>
    public static string CuaKenh(short kenh) => CuaKenh((ChatChannel)kenh);

    /// <summary>
    /// Chuẩn hoá chuỗi nền tảng do nhà cung cấp trả về thành khoá nguồn. Rỗng → <c>null</c>.
    ///
    /// <para>Nền tảng LẠ không bị nuốt: trả về chính chuỗi đó (đã hạ chữ thường). Pancake thêm
    /// kênh mới thì tab hiện tên lạ — xấu nhưng đúng; bỏ qua thì hội thoại rơi vào tab "chưa rõ"
    /// mà không ai biết vì sao.</para>
    /// </summary>
    public static string? Chuan(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        return s switch
        {
            // Pancake gọi Trang Facebook là "facebook"; webhook của Meta gọi là "messenger"; vài
            // chỗ viết tắt "fb". Cả ba là MỘT nguồn.
            "facebook" or "messenger" or "fb" or "facebook_page" => "facebook",
            "instagram" or "ig" or "instagram_official" => "instagram",
            "tiktok" or "tik_tok" or "tiktok_business" => "tiktok",
            "zalo" or "zalo_oa" or "zalooa" => "zalo",
            "whatsapp" or "wa" => "whatsapp",
            "telegram" or "tele" => "telegram",
            _ => s,
        };
    }

    /// <summary>
    /// Nguồn của MỘT tài khoản: nền tảng đã ghi lúc nối trang nếu có, không thì nguồn của kênh.
    /// </summary>
    public static string Cua(short kenh, string? nenTang) => Chuan(nenTang) ?? CuaKenh(kenh);

    /// <inheritdoc cref="Cua(short, string?)"/>
    public static string Cua(ChatChannel kenh, string? nenTang) => Cua((short)kenh, nenTang);
}

/// <summary>
/// Bộ lọc theo <see cref="ChatSource">nguồn</see>, đã nở ra thành thứ SQL kẹp được.
///
/// <para>Phải là hai tập chứ không phải một chuỗi nguồn, vì nguồn nằm ở hai nơi khác nhau: kênh
/// nối thẳng mang nguồn trên chính cột <c>channel</c>, còn Trang của cổng gom mang nguồn trong
/// bảng khai báo kênh mà <c>chat_conversations</c> không nối tới. Nở ra ở tầng điểm cuối (nơi đã
/// có sẵn bản đồ tài khoản) rồi mới xuống SQL là một lần đọc, không phải một lần JOIN cho mỗi
/// dòng.</para>
///
/// <para>Hai tập là <b>HOẶC</b>: hội thoại thuộc một trong các kênh đó, HOẶC của một trong các
/// tài khoản đó. Cả hai cùng rỗng = không hội thoại nào khớp (đúng: nguồn lạ thì danh sách
/// trống), KHÁC hẳn với không lọc.</para>
/// </summary>
/// <param name="Kenh">Kênh nối thẳng thuộc nguồn này — lấy TRỌN kênh.</param>
/// <param name="TaiKhoan">Mã tài khoản (Trang) của cổng gom thuộc nguồn này.</param>
public readonly record struct NguonLoc(short[] Kenh, string[] TaiKhoan);

/// <summary>
/// Hai thứ hộp thư cần biết về một tài khoản kênh, và luôn cần CÙNG LÚC.
/// </summary>
/// <param name="Ten">Tên gợi nhớ của Trang/OA/bot. Thiếu thì là chính mã tài khoản — không bao
/// giờ rỗng.</param>
/// <param name="NenTang">Nền tảng thật bên dưới cổng gom ("facebook", "zalo"…). <c>null</c> khi
/// không biết, và null thì phải để yên: bịa ra một nền tảng là nói dối người trực về nguồn gốc
/// của khách.</param>
public readonly record struct MoTaTaiKhoan(string Ten, string? NenTang);

/// <summary>
/// Quy mọi thứ hộp thư cần về <see cref="ChatSource">nguồn</see>: đếm theo nguồn, nguồn của từng
/// tài khoản, danh sách nguồn đã khai, và nở một nguồn thành bộ lọc SQL.
///
/// <para><b>Cố ý nằm ở máy chủ, không ở giao diện.</b> Giao diện chỉ còn việc vẽ: nó nhận thẳng
/// bảng "nguồn → số hội thoại" và danh sách nguồn cần hiện. Để nó tự tính thì bảng quy đổi
/// kênh → nguồn phải có hai bản (C# và JavaScript), và hai bản đó sẽ lệch nhau — lúc ấy tab đếm
/// một đằng còn danh sách lọc ra một nẻo, không có gì trên màn hình nói là đang sai.</para>
/// </summary>
public static class ChatSourceMap
{
    /// <summary>Khoá "{kênh}:{mã tài khoản}" mà bộ đếm dùng → cặp (kênh, mã). Hỏng thì null.</summary>
    /// <remarks>Cắt ở dấu hai chấm ĐẦU TIÊN: mã tài khoản của vài kênh có chứa dấu hai chấm.</remarks>
    public static (short Kenh, string TaiKhoan)? TachKhoa(string khoa)
    {
        var i = khoa.IndexOf(':');
        if (i <= 0 || i == khoa.Length - 1) return null;
        return short.TryParse(khoa.AsSpan(0, i), out var k) ? (k, khoa[(i + 1)..]) : null;
    }

    /// <summary>
    /// Nguồn của từng tài khoản, giữ nguyên khoá "{kênh}:{mã}" của bộ đếm.
    /// </summary>
    /// <param name="khoaThem">Khoá tài khoản chỉ còn thấy trong BỘ ĐẾM — Trang đã gỡ khai báo mà
    /// hội thoại cũ vẫn còn. Thiếu chúng thì hàng chip Trang bỏ sót đúng những Trang đang có tin,
    /// và tab đếm ra một con số không chip nào cộng lại thành.</param>
    public static Dictionary<string, string> TheoTaiKhoan(
        IReadOnlyDictionary<(short Kenh, string TaiKhoan), MoTaTaiKhoan> moTa,
        IEnumerable<string>? khoaThem = null)
    {
        var ra = new Dictionary<string, string>(moTa.Count);
        foreach (var (khoa, m) in moTa)
            ra[khoa.Kenh + ":" + khoa.TaiKhoan] = ChatSource.Cua(khoa.Kenh, m.NenTang);
        foreach (var khoa in khoaThem ?? [])
            if (!ra.ContainsKey(khoa) && TachKhoa(khoa) is { } k)
                ra[khoa] = ChatSource.CuaKenh(k.Kenh);
        return ra;
    }

    /// <summary>
    /// Số hội thoại theo nguồn, dựng từ hai bộ đếm sẵn có — không thêm lượt đọc CSDL nào.
    ///
    /// <para>Phần dôi ra giữa "tổng của kênh" và "tổng của các tài khoản trong kênh" về nguồn
    /// mặc định của kênh. Dôi ra là chuyện thật: hội thoại tạo trước 24/08/2026 không ghi mã tài
    /// khoản. Bỏ phần đó đi thì tổng các tab nhỏ hơn số ở tab "Tất cả" mà không ai giải thích
    /// được.</para>
    /// </summary>
    public static Dictionary<string, int> Dem(
        IReadOnlyDictionary<short, int> theoKenh,
        IReadOnlyDictionary<string, int> theoTaiKhoan,
        IReadOnlyDictionary<(short Kenh, string TaiKhoan), MoTaTaiKhoan> moTa)
    {
        var ra = new Dictionary<string, int>();
        var daTinh = new Dictionary<short, int>();   // kênh → số đã quy được về tài khoản
        foreach (var (khoa, so) in theoTaiKhoan)
        {
            if (TachKhoa(khoa) is not { } k) continue;
            var nenTang = moTa.TryGetValue((k.Kenh, k.TaiKhoan), out var m) ? m.NenTang : null;
            var nguon = ChatSource.Cua(k.Kenh, nenTang);
            ra[nguon] = ra.GetValueOrDefault(nguon) + so;
            daTinh[k.Kenh] = daTinh.GetValueOrDefault(k.Kenh) + so;
        }
        foreach (var (kenh, so) in theoKenh)
        {
            var conLai = so - daTinh.GetValueOrDefault(kenh);
            if (conLai <= 0) continue;
            var nguon = ChatSource.CuaKenh(kenh);
            ra[nguon] = ra.GetValueOrDefault(nguon) + conLai;
        }
        return ra;
    }

    /// <summary>
    /// Nguồn công ty ĐÃ KHAI — để tab hiện ngay sau khi nối Trang, trước khi có tin nào.
    ///
    /// <para>Kênh đang tắt (<c>Chat:HiddenChannels</c>) không góp mặt ở đây: tắt nghĩa là không
    /// mời nối, nên không dựng sẵn một tab trống cho nó. Hội thoại CŨ của kênh tắt thì vẫn đếm
    /// bình thường ở <see cref="Dem"/> — tab vẫn mọc nếu thật sự có tin, vì giấu tab đi là chôn
    /// tin nhắn thật của khách.</para>
    /// </summary>
    public static List<string> DaKhai(
        IReadOnlyDictionary<(short Kenh, string TaiKhoan), MoTaTaiKhoan> moTa,
        IReadOnlySet<ChatChannel> kenhAn)
    {
        var ra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (khoa, m) in moTa)
        {
            var kenh = (ChatChannel)khoa.Kenh;
            if (kenh == ChatChannel.Internal || kenhAn.Contains(kenh)) continue;
            ra.Add(ChatSource.Cua(khoa.Kenh, m.NenTang));
        }
        return [.. ra];
    }

    /// <summary>
    /// Nở một nguồn thành bộ lọc SQL kẹp được. Nguồn rỗng/không hợp lệ → <c>null</c> (không lọc).
    ///
    /// <para>Kênh nối thẳng vào theo TRỌN KÊNH, Trang của cổng gom vào theo từng mã. Kênh tắt vẫn
    /// vào: người trực bấm tab "Facebook" là muốn xem mọi hội thoại Facebook, kể cả cái vào bằng
    /// đường nối thẳng đã thôi không mời nối nữa.</para>
    /// </summary>
    public static NguonLoc? No(string? nguon,
        IReadOnlyDictionary<(short Kenh, string TaiKhoan), MoTaTaiKhoan> moTa)
    {
        var n = ChatSource.Chuan(nguon);
        if (n is null) return null;

        var kenh = new List<short>();
        foreach (var k in Enum.GetValues<ChatChannel>())
        {
            if (k == ChatChannel.Internal || ChatSource.LaCongGom(k)) continue;
            if (ChatSource.CuaKenh(k) == n) kenh.Add((short)k);
        }

        var taiKhoan = new List<string>();
        foreach (var (khoa, m) in moTa)
        {
            if (!ChatSource.LaCongGom(khoa.Kenh)) continue;
            if (ChatSource.Cua(khoa.Kenh, m.NenTang) == n) taiKhoan.Add(khoa.TaiKhoan);
        }

        return new NguonLoc([.. kenh], [.. taiKhoan]);
    }
}
