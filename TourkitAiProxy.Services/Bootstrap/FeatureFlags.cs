namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Cờ bật/tắt tính năng CHƯA RA MẮT, đọc từ <c>appsettings.json</c> mục <c>Features</c>.
///
/// <para>Đây KHÔNG phải phân quyền. Quyền (<c>CH_HT_XEM</c>…) trả lời "người này được xem gì";
/// cờ ở đây trả lời "tính năng này đã ra mắt chưa" — tắt là tắt cho tất cả, kể cả admin.</para>
///
/// <para><b>Thiếu key = TẮT.</b> Cố ý sai theo hướng an toàn: quên khai báo lúc deploy thì hậu quả
/// là tính năng bị ẩn (phiền, nhưng sửa 1 dòng); nếu mặc định bật thì quên khai báo nghĩa là bản
/// tin GỬI THẬT cho khách trên bản public.</para>
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// Cụm bản tin: bản tin sáng (<c>sale-brief</c>), bản tin điều hành (<c>ceo-brief</c>),
    /// canh thanh toán (<c>payment-watchdog</c>) và Bảng tin trong app.
    ///
    /// <para>MỘT cờ cho cả cụm vì với người dùng chúng là một tính năng: 3 tác vụ đều ghi vào Bảng
    /// tin, và Bảng tin là chỗ đọc lại. Tách cờ riêng sẽ đẻ ra trạng thái vô nghĩa — bật bản tin
    /// mà không có chỗ nào xem nó.</para>
    /// </summary>
    public static bool Digest(IConfiguration cfg) => cfg.GetValue("Features:Digest", false);

    /// <summary>
    /// Hộp thư chat đa kênh: nhận tin khách nhắn từ Zalo OA (sau này thêm kênh), bot trả lời, nhân
    /// viên tiếp quản.
    ///
    /// <para>Cờ RIÊNG, không dựa vào cờ nào khác — nó có CSDL riêng và không ghi vào Bảng tin.</para>
    ///
    /// <para>Tắt thì webhook + API hộp thư không map (trả 404 tường minh), worker gửi không chạy,
    /// và mục menu biến mất. Tức là <b>khách nhắn tới cũng không có gì xảy ra</b> — đúng ý nghĩa
    /// của một tính năng chưa ra mắt.</para>
    /// </summary>
    public static bool Chat(IConfiguration cfg) => cfg.GetValue("Features:Chat", false);

    /// <summary>
    /// Lấy lại các đoạn hội thoại CŨ của Messenger / Instagram (có từ trước lúc nối).
    ///
    /// <para>PHỤ THUỘC <see cref="Chat"/>: không có hộp thư thì lấy lịch sử về cũng không có
    /// chỗ nào đọc.</para>
    ///
    /// <para><b>Cờ riêng vì đây là việc TỐN HẠN MỨC.</b> Một Trang bán hàng lâu năm có thể có
    /// hàng chục nghìn tin; gọi Graph quá nhiều là Facebook chặn tạm cả ứng dụng, mà lúc đó
    /// <b>tin trực tiếp cũng ngừng về</b> — tức lấy lịch sử làm hỏng chính việc đang chạy. Bật
    /// có ý thức, và vẫn phải người dùng tự bấm chứ không tự chạy lúc nối.</para>
    /// </summary>
    public static bool ChatHistoryImport(IConfiguration cfg)
        => Chat(cfg) && cfg.GetValue("Features:ChatHistoryImport", false);

    /// <summary>
    /// Kiểm tra sẵn sàng khởi hành (tác vụ <c>tour-readiness</c>): quét tour sắp đi, tour nào còn
    /// thiếu thì ghi cảnh báo vào Bảng tin.
    ///
    /// <para>PHỤ THUỘC <see cref="Digest"/>: nó ghi vào Bảng tin, mà Bảng tin nằm sau cờ Digest —
    /// bật riêng cái này trong khi Digest tắt thì thẻ vẫn được ghi ra nhưng
    /// <c>/api/v1/insights</c> trả 404, tức là cảnh báo nằm đó không ai đọc được. Nên điều kiện
    /// chạy là CẢ HAI cùng bật.</para>
    /// </summary>
    public static bool TourReadiness(IConfiguration cfg)
        => Digest(cfg) && cfg.GetValue("Features:TourReadiness", false);

    /// <summary>
    /// Thẻ chuẩn bị gặp khách (action <c>prepare_meeting</c> của trợ lý).
    ///
    /// <para>Cờ RIÊNG, không dựa vào cờ nào khác: nó chạy theo yêu cầu trong khung chat và không
    /// ghi vào đâu cả, nên bật/tắt độc lập được.</para>
    ///
    /// <para>Tắt thì tool BIẾN MẤT khỏi danh mục gửi cho AI (AI không biết là có nó để mà gọi),
    /// và đường thực thi vẫn chặn thêm một lần — vì client cũ có thể gửi thẳng tên action.</para>
    /// </summary>
    public static bool MeetingBrief(IConfiguration cfg)
        => cfg.GetValue("Features:MeetingBrief", false);

    /// <summary>
    /// Canh doanh thu bất thường (tác vụ <c>anomaly-watchdog</c>).
    ///
    /// <para>PHỤ THUỘC <see cref="Digest"/> vì nó ghi cảnh báo vào Bảng tin — bật riêng khi Digest
    /// tắt thì cảnh báo nằm đó mà <c>/api/v1/insights</c> trả 404, không ai đọc được.</para>
    /// </summary>
    public static bool AnomalyWatchdog(IConfiguration cfg)
        => Digest(cfg) && cfg.GetValue("Features:AnomalyWatchdog", false);

    /// <summary>
    /// Tự chăm sóc khách (tác vụ <c>customer-auto-care</c>).
    ///
    /// <para>Cờ này QUAN TRỌNG hơn các cờ khác: tính năng duy nhất chạm tới KHÁCH HÀNG THẬT. Mọi
    /// thứ khác chỉ ghi vào Bảng tin cho người trong công ty đọc; cái này soạn thư gửi ra ngoài.
    /// Vì vậy nó vẫn nằm sau cờ kể cả khi đã ra mắt các phần khác.</para>
    ///
    /// <para>PHỤ THUỘC <see cref="Digest"/>: danh sách thư chờ duyệt hiện trong Bảng tin.</para>
    /// </summary>
    public static bool AutoCare(IConfiguration cfg)
        => Digest(cfg) && cfg.GetValue("Features:AutoCare", false);
}
