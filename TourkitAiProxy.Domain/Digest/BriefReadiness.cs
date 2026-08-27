namespace TourkitAiProxy.Domain.Digest;

/// <summary>Vì sao một người đang đăng ký mà bản tin không tới được.</summary>
public enum BriefReadinessReason
{
    /// Không còn phiên đăng nhập nào trong cơ sở dữ liệu — hết hạn 30 ngày không dùng, hoặc chưa
    /// từng đăng nhập. <b>Không phân biệt được hai ca này</b> vì dòng đã bị dọn, nên câu chữ phải
    /// nói ở mức "phiên đã hết hạn", không được khẳng định "chưa đăng nhập lần nào".
    NoSession,

    /// Có phiên nhưng xin lại chìa khoá thất bại — thường là đổi mật khẩu hoặc khoá tài khoản bên
    /// CRM. Bảo họ đăng nhập lại là bảo làm một việc chắc chắn hỏng; phải chỉ sang CRM.
    ReloginFailed,
}

/// <summary>
/// Người đang đăng ký bản tin nhưng KHÔNG nhận được: xếp loại, dựng lời nhắc, chọn kênh gửi.
/// <b>Hàm THUẦN</b> — tách khỏi workflow để test được, và để hai bản tin (nhân viên bán hàng và
/// giám đốc) dùng chung một cách xử lý thay vì mỗi nơi một kiểu.
///
/// <para><b>Vì sao có cụm này.</b> Trước 27/08/2026 bản tin lặng lẽ bỏ qua người thiếu phiên rồi
/// ghi nhật ký <i>"chưa đăng nhập lần nào"</i> — sai với cả hai nguyên nhân thật. Người mất bản tin
/// không bao giờ biết mình đang mất, và không có lỗi nào để ai lần ra.</para>
///
/// <para><b>Báo MỘT lần rồi TẮT đăng ký.</b> Không nhắc lại theo chu kỳ: nhắc xong thì tắt, lượt sau
/// khỏi kiểm lại và không có lá thư thứ hai. Người dùng đăng nhập, thấy lý do trên thẻ "Bản tin của
/// tôi", tự bật lại — lúc đó ba cột trạng thái được xoá về rỗng.</para>
///
/// <para><b>Chỉ đi qua THƯ và trong app.</b> Người cần đọc lời nhắc chính là người không mở app nên
/// thư là đường duy nhất tới được họ; dòng trong app để giải thích khi họ quay lại. Telegram bỏ vì
/// lời nhắc hành chính không nên chen vào kênh chat, Zalo bỏ vì ZNS chỉ chở được mẫu đã duyệt.</para>
/// </summary>
public static class BriefReadiness
{
    /// <summary>
    /// Loại của dòng ghi vào Bảng tin. <b>Phải KHÁC loại bản tin thật</b> — ghi cùng loại thì lượt
    /// sau hệ thống tưởng "hôm nay gửi rồi" và bản tin thật không bao giờ tới.
    /// </summary>
    public const string ReminderKind = "brief-login-required";

    /// <summary>Mã lưu xuống cột <c>NotReadyReason</c>. <b>Đừng đổi chữ</b> — giao diện đọc mã này
    /// để hiện dải cảnh báo, đổi là dữ liệu cũ mồ côi.</summary>
    public static string ReasonCode(BriefReadinessReason r) => r switch
    {
        BriefReadinessReason.NoSession => "thieu-phien",
        BriefReadinessReason.ReloginFailed => "dang-nhap-lai-hong",
        _ => "khong-ro",
    };

    /// <summary>Chữ cho NGƯỜI DÙNG đọc. Mã lạ (dữ liệu cũ, hoặc phiên bản sau ghi vào) vẫn phải ra
    /// một câu đọc được — ô trống thì người ta không biết phải làm gì.</summary>
    public static string ReasonLabel(string? code) => code switch
    {
        "thieu-phien" => "Phiên đăng nhập đã hết hạn",
        "dang-nhap-lai-hong" => "Không đăng nhập lại được vào CRM",
        _ => "Chưa lấy được dữ liệu của bạn",
    };

    /// <summary>
    /// Việc DUY NHẤT người dùng cần làm, viết cho màn hình trong app.
    ///
    /// <para>Khác câu trong thư: đọc được dòng này nghĩa là họ ĐANG đăng nhập, nên phần "đăng
    /// nhập lại" đã xong rồi — bảo họ làm lần nữa là thừa. Việc còn lại chỉ là bật công tắc.</para>
    /// </summary>
    public static string ActionLabel(string? code) => code switch
    {
        "thieu-phien" =>
            "Bạn vừa đăng nhập nên phần đó đã xong — bật lại công tắc bên dưới để nhận tiếp.",
        "dang-nhap-lai-hong" =>
            "Kiểm tra tài khoản của bạn bên CRM (mật khẩu vừa đổi, hoặc đang bị khoá), rồi bật lại "
            + "công tắc bên dưới.",
        _ => "Bật lại công tắc bên dưới để nhận tiếp.",
    };

    /// <summary>
    /// Bản sao của đăng ký, <b>chỉ giữ kênh thư</b>, để dựng dòng hàng đợi cho lời nhắc.
    /// </summary>
    /// <remarks>Trả bản SAO — bản gốc còn dùng để gửi bản tin thật, sửa vào đó là hỏng cả hai.</remarks>
    public static DigestSubscription ChannelsForReminder(DigestSubscription s) => s with
    {
        ChannelTelegram = false,
        ChannelZalo = false,
    };

    /// <summary>Có gửi thư nhắc được không. Không thì người đó chỉ biết khi tự mở app — chỗ gọi
    /// phải ghi việc này vào tóm tắt lượt chạy, đừng nuốt.</summary>
    public static bool CanRemindByMail(DigestSubscription s)
        => s.ChannelEmail && !string.IsNullOrWhiteSpace(s.Email);

    /// <summary>
    /// Dựng lời nhắc. Nói ba việc, thiếu việc nào cũng hụt: <b>vì sao</b> không có bản tin,
    /// <b>đăng ký đã bị tạm tắt</b>, và <b>làm gì để bật lại</b>.
    /// </summary>
    /// <param name="sinceUtc">Hỏng từ khi nào — để nói được "mấy hôm nay" thay vì một mốc trống.
    /// Null thì bỏ câu đó, không bịa số.</param>
    public static DigestMessage BuildReminder(BriefReadinessReason reason, string briefType,
        DateTime? sinceUtc, DateTime nowUtc)
    {
        var tenBanTin = briefType == BriefTypes.Ceo ? "Bản tin điều hành" : "Bản tin sáng";

        var soNgay = sinceUtc is { } tu ? (int)Math.Floor((nowUtc - tu).TotalDays) : 0;
        var cauLo = soNgay >= 1
            ? $"Bạn đã không nhận được {tenBanTin.ToLowerInvariant()} trong {soNgay} ngày qua."
            : $"Sáng nay {tenBanTin.ToLowerInvariant()} của bạn không gửi được.";

        var cauViSao = reason switch
        {
            // KHÔNG nói "bạn chưa đăng nhập lần nào": dòng phiên đã bị dọn nên không phân biệt được,
            // mà khẳng định sai với người đã dùng nhiều tháng thì họ mất tin vào cả bản tin.
            BriefReadinessReason.NoSession =>
                "Lý do: phiên đăng nhập của bạn đã hết hạn, nên hệ thống không lấy được số liệu "
                + "của riêng bạn. Bản tin luôn dùng quyền của chính bạn để không ai đọc nhầm việc "
                + "của người khác.",
            BriefReadinessReason.ReloginFailed =>
                "Lý do: hệ thống không đăng nhập lại vào CRM bằng tài khoản của bạn được. Thường là "
                + "do mật khẩu vừa đổi, hoặc tài khoản đang bị khoá bên CRM.",
            _ => "Lý do: chưa lấy được dữ liệu của bạn.",
        };

        var cauLamGi = reason == BriefReadinessReason.ReloginFailed
            ? "Bạn kiểm tra lại tài khoản bên CRM (đăng nhập thử, hoặc nhờ quản trị mở khoá), rồi "
              + "đăng nhập vào TRAV-AI một lần."
            : "Bạn chỉ cần đăng nhập vào TRAV-AI một lần như bình thường.";

        var body = string.Join("\n\n", new[]
        {
            cauLo,
            cauViSao,
            // Hệ thống vừa tự đổi cấu hình của người dùng — phải nói ra. Không nói thì họ đăng nhập
            // lại, tưởng xong, rồi sáng mai vẫn không có bản tin.
            $"Để khỏi gửi nhắc mỗi ngày, hệ thống đã **tạm tắt** đăng ký {tenBanTin.ToLowerInvariant()} "
            + "của bạn.",
            cauLamGi + " Sau đó mở **Tự động hoá → Bản tin của tôi** và **bật lại** — sáng hôm sau "
            + "bản tin chạy tiếp như cũ.",
        });

        var title = $"{tenBanTin} đang tạm dừng — cần bạn đăng nhập lại";
        return new DigestMessage(title, body, SaleBriefBuilder.ToHtml(body), ReminderKind, Severity: 1);
    }
}
