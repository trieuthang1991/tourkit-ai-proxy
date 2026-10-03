namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Thang cảm xúc hội thoại <b>5 bậc</b> — do chủ dự án đặt ra ngày 12/09/2026.
///
/// <para>Số nhỏ là xấu, số lớn là tốt, 3 ở giữa. Thứ tự này KHÔNG được đảo: mọi phép so sánh
/// trong mã đều dựa vào nó (“dưới 3 thì cảnh báo”, “xa 3 nhất thì mạnh nhất”).</para>
/// </summary>
public enum SentimentLevel : short
{
    VeryNegative = 1,
    Negative     = 2,
    Neutral      = 3,
    Positive     = 4,
    VeryPositive = 5,
}

/// <summary>Một bậc trên thang, kèm chữ hiển thị và việc nên làm.</summary>
/// <param name="Level">Điểm 1–5.</param>
/// <param name="Label">Tên trạng thái, hiện thẳng ra màn hình.</param>
/// <param name="Icon">Biểu tượng đại diện của bậc — dùng trên nhãn. KHÁC với biểu tượng khách
/// thả: một bậc nhận nhiều biểu tượng khác nhau.</param>
/// <param name="Signals">Dấu hiệu để AI nhận ra bậc này qua lời khách.</param>
/// <param name="Action">Việc gợi ý cho người trực.</param>
public record SentimentInfo(SentimentLevel Level, string Label, string Icon, string Signals, string Action);

/// <summary>
/// Luật chấm cảm xúc hội thoại. <b>Thuần</b> — không chạm CSDL, không gọi AI, không đọc giờ hệ
/// thống; nhờ vậy kiểm được bằng test thường.
///
/// <para><b>Hai nguồn tín hiệu, đừng lẫn.</b> Lớp này lo nguồn RẺ: biểu tượng khách thả lên tin
/// và biểu tượng khách gõ trong tin. Không tốn lượt AI nào, chạy được trên mọi kênh ngay hôm nay.
/// Nguồn kia — đọc cả đoạn hội thoại rồi chấm — tốn tiền và chưa làm; khi làm thì cũng trả về
/// <see cref="SentimentLevel"/> này, không đẻ thang thứ hai.</para>
///
/// <para><b>⚠️ Cảm xúc thả LỆ THUỘC NỀN TẢNG</b> — xem <see cref="ScoreReaction"/>. Đây là chỗ
/// dễ chấm sai nhất của cả lớp.</para>
/// </summary>
public static class ConversationSentiment
{
    /// <summary>Cả năm bậc, theo thứ tự 1→5. Nguồn duy nhất cho mọi chỗ hiển thị.</summary>
    public static readonly IReadOnlyList<SentimentInfo> Levels = new[]
    {
        new SentimentInfo(SentimentLevel.VeryNegative, "Rất tiêu cực", "🤬",
            "Tức giận, khiếu nại, đòi hoàn tiền, chỉ trích mạnh, muốn ngừng sử dụng",
            "Cảnh báo phụ trách xử lý ngay"),
        new SentimentInfo(SentimentLevel.Negative, "Tiêu cực", "😟",
            "Không hài lòng, thất vọng, phàn nàn, nghi ngờ giải pháp",
            "Chăm sóc/Kinh doanh cần chủ động xử lý"),
        new SentimentInfo(SentimentLevel.Neutral, "Trung tính", "😐",
            "Hỏi thông tin, trao đổi bình thường, chưa thể hiện thích hay không thích",
            "Tiếp tục khai thác nhu cầu"),
        new SentimentInfo(SentimentLevel.Positive, "Tích cực", "🙂",
            "Quan tâm, phản hồi tốt, đồng ý với tư vấn, có tín hiệu mua",
            "Đề xuất bước tiếp theo, đẩy chuyển đổi"),
        new SentimentInfo(SentimentLevel.VeryPositive, "Rất tích cực", "😍",
            "Rất hài lòng, cảm ơn, khen ngợi, muốn mua/đăng ký/giới thiệu",
            "Chốt bán / bán thêm / báo giá"),
    };

    public static SentimentInfo Info(SentimentLevel level) => Levels[(int)level - 1];

    /// <summary>
    /// Tên cảm xúc của <b>Meta</b> (Messenger và Instagram) → bậc.
    ///
    /// <para>Meta gửi kèm một TÊN do họ định nghĩa (<c>"love"</c>, <c>"angry"</c>…) bên cạnh
    /// biểu tượng. Tên đáng tin hơn hẳn biểu tượng: nó là tập đóng, không đổi theo phiên bản ứng
    /// dụng, và không dính bộ chọn biến thể.</para>
    /// </summary>
    private static readonly Dictionary<string, SentimentLevel> MetaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["angry"]   = SentimentLevel.VeryNegative,
        ["sad"]     = SentimentLevel.Negative,
        ["dislike"] = SentimentLevel.Negative,
        // "wow" là NGẠC NHIÊN, và ngạc nhiên không có dấu. Khách có thể wow vì tour đẹp, cũng có
        // thể wow vì giá cao gấp đôi chỗ khác. Đoán sai hướng còn tệ hơn không đoán.
        ["wow"]     = SentimentLevel.Neutral,
        ["like"]    = SentimentLevel.Positive,
        ["smile"]   = SentimentLevel.Positive,
        ["haha"]    = SentimentLevel.Positive,
        ["love"]    = SentimentLevel.VeryPositive,
        ["care"]    = SentimentLevel.VeryPositive,
    };

    /// <summary>
    /// Biểu tượng → bậc.
    ///
    /// <para>Phủ bộ cảm xúc thả được của <b>Telegram</b> và những biểu tượng khách hay GÕ trong
    /// tin. Biểu tượng lạ trả <c>null</c> — <b>không đoán bừa về 3</b>: “không nhận ra” và
    /// “trung tính” là hai chuyện khác nhau, gộp lại thì một hộp thư toàn biểu tượng lạ trông
    /// như ai cũng bình thản.</para>
    ///
    /// <para>😮 và 🤔 cố ý để TRUNG TÍNH, cùng lý do với <c>"wow"</c> ở trên.</para>
    /// </summary>
    private static readonly Dictionary<string, SentimentLevel> ByEmoji = new()
    {
        // 1 — rất tiêu cực
        ["🤬"] = SentimentLevel.VeryNegative, ["😡"] = SentimentLevel.VeryNegative,
        ["😠"] = SentimentLevel.VeryNegative, ["💢"] = SentimentLevel.VeryNegative,
        ["🤮"] = SentimentLevel.VeryNegative, ["💩"] = SentimentLevel.VeryNegative,
        // 2 — tiêu cực
        ["😟"] = SentimentLevel.Negative, ["😢"] = SentimentLevel.Negative,
        ["😭"] = SentimentLevel.Negative, ["😞"] = SentimentLevel.Negative,
        ["😔"] = SentimentLevel.Negative, ["😕"] = SentimentLevel.Negative,
        ["🙁"] = SentimentLevel.Negative, ["☹"] = SentimentLevel.Negative,
        ["👎"] = SentimentLevel.Negative, ["😤"] = SentimentLevel.Negative,
        ["🥱"] = SentimentLevel.Negative,
        // 3 — trung tính
        ["😐"] = SentimentLevel.Neutral, ["😶"] = SentimentLevel.Neutral,
        ["🤔"] = SentimentLevel.Neutral, ["😮"] = SentimentLevel.Neutral,
        ["😯"] = SentimentLevel.Neutral, ["🤨"] = SentimentLevel.Neutral,
        ["😱"] = SentimentLevel.Neutral, ["🤯"] = SentimentLevel.Neutral,
        // 4 — tích cực
        ["🙂"] = SentimentLevel.Positive, ["😊"] = SentimentLevel.Positive,
        ["😀"] = SentimentLevel.Positive, ["😃"] = SentimentLevel.Positive,
        ["😄"] = SentimentLevel.Positive, ["😁"] = SentimentLevel.Positive,
        ["👍"] = SentimentLevel.Positive, ["👌"] = SentimentLevel.Positive,
        ["✌"] = SentimentLevel.Positive, ["😆"] = SentimentLevel.Positive,
        ["🎉"] = SentimentLevel.Positive, ["👏"] = SentimentLevel.Positive,
        // 5 — rất tích cực
        ["😍"] = SentimentLevel.VeryPositive, ["🥰"] = SentimentLevel.VeryPositive,
        ["❤"] = SentimentLevel.VeryPositive, ["😘"] = SentimentLevel.VeryPositive,
        ["🤩"] = SentimentLevel.VeryPositive, ["💯"] = SentimentLevel.VeryPositive,
        ["🔥"] = SentimentLevel.VeryPositive, ["💖"] = SentimentLevel.VeryPositive,
        ["💕"] = SentimentLevel.VeryPositive,
    };

    /// <summary>
    /// Chấm một lượt khách THẢ cảm xúc — <b>theo đúng nền tảng đã gửi nó</b>.
    ///
    /// <para><b>Vì sao phải phân biệt kênh.</b> Trường <c>Name</c> của
    /// <see cref="ChatReaction"/> mang hai thứ HOÀN TOÀN khác nhau tuỳ kênh:</para>
    /// <list type="bullet">
    ///   <item><b>Meta</b> (Messenger/Instagram): tên cảm xúc do Meta định nghĩa — <c>"love"</c>,
    ///     <c>"angry"</c>… Đây là thứ đáng tin nhất, chấm theo nó trước.</item>
    ///   <item><b>Telegram</b>: <c>custom_emoji_id</c> — một dãy số định danh biểu tượng riêng của
    ///     người dùng Premium. Đem tra vào bảng tên là vô nghĩa, và tệ hơn: nếu lỡ có một mã trùng
    ///     một chữ trong bảng thì chấm sai mà không ai hiểu tại sao.</item>
    /// </list>
    ///
    /// <para>Với biểu tượng riêng của Telegram (có <c>custom_emoji_id</c> mà không có emoji chuẩn)
    /// thì trả <c>null</c>: người ta dán một con mèo nhảy múa lên tin, không ai biết nó khen hay
    /// chê, kể cả người thật.</para>
    /// </summary>
    public static SentimentLevel? ScoreReaction(ChatChannel channel, string? emoji, string? name)
        => channel switch
        {
            // Tên trước, biểu tượng sau: Meta luôn gửi cả hai khi THẢ, nhưng tên ổn định hơn.
            ChatChannel.Messenger or ChatChannel.Instagram
                => (name is { Length: > 0 } n && MetaNames.TryGetValue(n, out var b)
                        ? (SentimentLevel?)b
                        : null)
                   ?? ScoreEmoji(emoji),

            // Telegram: CHỈ biểu tượng. Name ở đây là custom_emoji_id, tuyệt đối không tra tên.
            ChatChannel.Telegram => ScoreEmoji(emoji),

            // Kênh khác chưa gửi cảm xúc thả. Tới khi có thì thêm nhánh ở đây, đừng để rơi vào
            // một nhánh chung đoán bừa theo tên.
            _ => ScoreEmoji(emoji),
        };

    /// <summary>
    /// Chấm MỘT biểu tượng. <c>null</c> khi rỗng hoặc không nhận ra.
    ///
    /// <para>Cắt bỏ <b>bộ chọn biến thể</b> U+FE0F trước khi tra. Không cắt thì "❤️" (❤ + FE0F,
    /// đúng thứ Meta gửi) không khớp với "❤" trong bảng — một lỗi nhìn không ra, vì hai chuỗi
    /// hiện lên màn hình y hệt nhau.</para>
    /// </summary>
    public static SentimentLevel? ScoreEmoji(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        var clean = emoji.Trim().Replace("️", "").Replace("︎", "");
        return ByEmoji.TryGetValue(clean, out var lv) ? lv : null;
    }

    /// <summary>
    /// Chấm cả một tin khách GÕ: quét mọi biểu tượng trong chuỗi rồi lấy cái <b>mạnh nhất</b>,
    /// tức xa bậc 3 nhất.
    ///
    /// <para><b>Vì sao lấy mạnh nhất chứ không lấy cái đầu hay cái cuối.</b> Khách viết
    /// "😊 nhưng giá này thì 😡" — cái đầu là xã giao, cái sau mới là điều họ muốn nói. Lấy cái
    /// mạnh nhất bắt được cả hai thứ tự viết.</para>
    ///
    /// <para><b>Hoà thì nghiêng về phía xấu.</b> Cùng khoảng cách tới 3 (ví dụ có cả 😡 và 😍)
    /// thì trả bậc thấp hơn. Bỏ sót một khách đang giận đắt hơn nhiều so với chăm hơi kỹ một
    /// khách đang vui.</para>
    ///
    /// <para><c>null</c> khi trong tin không có biểu tượng nào nhận ra được — <b>không</b> phải
    /// bậc 3.</para>
    /// </summary>
    public static SentimentLevel? ScoreText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        SentimentLevel? strongest = null;
        var farthest = -1;

        // Duyệt theo CỤM KÝ TỰ chứ không theo char: một biểu tượng chiếm hai char (cặp thay thế),
        // duyệt theo char thì mỗi biểu tượng vỡ làm đôi và không khớp gì cả.
        var walker = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (walker.MoveNext())
        {
            if (ScoreEmoji(walker.GetTextElement()) is not { } lv) continue;
            var far = System.Math.Abs((int)lv - (int)SentimentLevel.Neutral);
            // > chứ không >=: cái mạnh nhất ĐẦU TIÊN thắng, trừ khi có cái mạnh hơn thật sự.
            // Hoà thì nhánh dưới quyết, và nó nghiêng về phía xấu.
            if (far > farthest) { farthest = far; strongest = lv; }
            else if (far == farthest && strongest is { } cur && lv < cur) strongest = lv;
        }
        return strongest;
    }

    /// <summary>
    /// THỐNG KÊ cả cuộc hội thoại: trung bình của mọi tín hiệu đã thu được.
    ///
    /// <para>Chủ dự án chốt 12/09/2026: <i>"trong cuộc hội thoại toàn tích cực thì cung bậc cảm
    /// xúc phải happy"</i>. Nên đây là trung bình, KHÔNG phải tín hiệu gần nhất — bản đầu ghi đè
    /// mỗi lần có tín hiệu mới, và một khách khen mười câu rồi lỡ thả một mặt buồn là cả hội thoại
    /// thành tiêu cực.</para>
    ///
    /// <para><c>count = 0</c> → <c>null</c> (chưa có tín hiệu nào), không phải bậc 3.</para>
    ///
    /// <para><b>Làm tròn NỬA VỀ PHÍA XẤU.</b> Trung bình đúng 3,5 thì trả bậc 3 chứ không phải 4:
    /// cùng lý do với luật hoà ở <see cref="ScoreText"/> — bỏ sót một khách đang không hài lòng
    /// đắt hơn nhiều so với chăm hơi kỹ một khách đang vui.</para>
    /// </summary>
    public static SentimentLevel? Aggregate(int sum, int count)
    {
        if (count <= 0) return null;
        // Math.Round mặc định làm tròn NỬA VỀ SỐ CHẴN (3,5 → 4 nhưng 2,5 → 2) — không đoán được
        // và sai hướng. Tự tính: cộng 0,5 rồi lấy trần ngược lại cho nửa về phía thấp.
        var tb = (double)sum / count;
        var lam = (int)System.Math.Ceiling(tb - 0.5);
        return (SentimentLevel)System.Math.Clamp(lam, 1, 5);
    }

    /// <summary>
    /// Có cần báo người phụ trách không. Bậc 1 và 2 thì có.
    ///
    /// <para>Ngưỡng nằm ở ĐÂY, một chỗ duy nhất, thay vì rải <c>level &lt;= 2</c> khắp nơi: đổi
    /// chính sách là đổi một dòng, và không có chỗ nào bị bỏ quên.</para>
    /// </summary>
    public static bool NeedsEscalation(SentimentLevel level) => level <= SentimentLevel.Negative;
}
