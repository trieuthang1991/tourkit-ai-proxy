// Domain/Chat/ChatBotSettings.cs
using System.Text;

namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Cấu hình trợ lý chat, <b>theo TỪNG CÔNG TY</b>.
///
/// <para>Trước 28/08/2026 mọi công ty dùng chung đúng một lời dặn nằm trong <c>appsettings.json</c>
/// của máy chủ — nghĩa là không công ty nào khai được "bên em chuyên tour Nhật, giọng trang trọng,
/// không nhận đoàn dưới 10 khách". Cấu hình một sản phẩm nhiều khách hàng bằng file cấu hình máy
/// chủ thì chỉ đúng khi có đúng một khách hàng.</para>
/// </summary>
/// <param name="Enabled">Bot có tự trả lời không. Tắt thì tin vẫn vào hộp thư, chỉ là không ai
/// trả lời hộ — dùng khi công ty muốn người thật trực toàn bộ.</param>
/// <param name="Persona">Lời dặn RIÊNG của công ty. <b>NỐI THÊM</b> vào khung an toàn, không thay
/// thế — xem <see cref="BuildSystemPrompt"/>.</param>
/// <param name="Greeting">Câu chào cho khách nhắn LẦN ĐẦU. Rỗng = không chào, vào thẳng trả lời.</param>
/// <param name="MuteMinutes">Nhân viên trả lời xong thì bot câm bấy nhiêu phút.</param>
/// <param name="HistoryTurns">Bot đọc lại bao nhiêu tin gần nhất để hiểu ngữ cảnh.</param>
/// <param name="TourLookup">Cho trợ lý TRA dữ liệu tour thật (tên tour, khoảng giá, giá theo ngày
/// khởi hành, lịch + số chỗ còn) trước khi trả lời.
///
/// <para><b>Mặc định TẮT</b>, và đây là công tắc DUY NHẤT — cờ máy chủ <c>Features:ChatTourLookup</c>
/// đã bỏ (18/09/2026), tra tour đi theo <c>Features:Chat</c>. Bật ô này là đổi luật trong khung an
/// toàn: từ "cấm nói mọi số" thành "được nói đúng số trong bảng vừa lấy về". Công ty chưa bật thì
/// KHÔNG một lượt gọi API nào phát ra. Xem <see cref="BuildSystemPrompt"/>.</para>
///
/// <para>Thứ nhạy cảm không nằm ở tour mà ở các trường đi kèm trong API nội bộ — tên và số điện
/// thoại khách đã đặt, hoa hồng, doanh thu. Chặn chúng là việc của danh sách trắng trong
/// <c>ChatTourLookup</c>, không phải việc của một ô tích.</para></param>
/// <param name="TourLookupByUser">Tra tour <b>theo quyền của nhân viên</b> thay vì xem cả kho.
///
/// <para><b>Mặc định TẮT = xem CẢ KHO, không check quyền.</b> Danh mục tour về bản chất là thứ
/// công ty vẫn đem đi chào khách, nên mặc định rộng là đúng với việc bán hàng: khách hỏi tour nào
/// cũng tư vấn được, không phụ thuộc người trực hôm đó được phân công những tour nào.</para>
///
/// <para>Bật lên thì phạm vi bám theo quyền một người cụ thể — công ty nào chia tour theo nhóm
/// bán và không muốn người nhóm này chào tour của nhóm kia thì bật.</para>
///
/// <para><b>Phạm vi do Ô NÀY quyết, không do ngữ cảnh lượt gọi.</b> Bản đầu (18/09/2026) suy phạm
/// vi từ ngữ cảnh — nhân viên bấm Gợi ý thì tự động theo quyền người đó, bot tự trả lời thì đi
/// tài khoản dịch vụ. Sai: công ty không khai gì mà hai lượt trả lời cùng một câu hỏi lại ra hai
/// kết quả khác nhau, tuỳ ai bấm. Nay tắt ô là CẢ HAI đường đều xem cả kho, bật ô là cả hai đường
/// đều theo quyền.</para>
///
/// <para>Bật ô mà là lượt <b>bot tự trả lời</b> (không ai online) thì lấy quyền <b>người phụ trách
/// hội thoại</b>. Hội thoại chưa gán ai — hoặc người đó chưa từng đăng nhập — thì rơi về cả kho:
/// thà tư vấn rộng hơn ý muốn còn hơn để bot im trước câu hỏi của khách thật.</para></param>
public record ChatBotSettings(
    bool Enabled = true,
    string? Persona = null,
    string? Greeting = null,
    int MuteMinutes = ChatRules.BotCamPhutMacDinh,
    int HistoryTurns = 12,
    bool TourLookup = false,
    bool TourLookupByUser = false)
{
    public static readonly ChatBotSettings Default = new();

    /// <summary>Chặn để một lời dặn dài bất thường không nuốt sạch hạn mức token mỗi lượt.</summary>
    public const int MaxPersonaChars = 4000;

    /// <summary>
    /// Đọc lại quá nhiều tin thì vừa tốn tiền vừa loãng: model bám vào chuyện từ tuần trước thay vì
    /// câu khách vừa hỏi. Quá ít thì bot mất trí nhớ giữa chừng.
    /// </summary>
    public const int MinHistoryTurns = 2;
    public const int MaxHistoryTurns = 40;

    /// <summary>Kẹp mọi giá trị về khoảng dùng được. Gọi ở CẢ lúc đọc lẫn lúc ghi.</summary>
    public ChatBotSettings Normalized() => this with
    {
        Persona = Cat(Persona, MaxPersonaChars),
        Greeting = Cat(Greeting, 500),
        MuteMinutes = Math.Clamp(MuteMinutes, 0, 24 * 60),
        HistoryTurns = Math.Clamp(HistoryTurns, MinHistoryTurns, MaxHistoryTurns),
    };

    private static string? Cat(string? s, int n)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= n ? t : t[..n];
    }

    /// <summary>
    /// Ghép lời dặn cuối cùng cho model.
    ///
    /// <para>⚠️ <b>Lời dặn của công ty NỐI THÊM, tuyệt đối không thay thế khung.</b> Khung chứa các
    /// luật chống bịa (giá tour, lịch khởi hành, số chỗ còn, khuyến mãi) — bot này <b>không đọc dữ
    /// liệu thật của công ty</b>, nên bỏ khung đi là nó bắt đầu bịa giá và hứa giữ chỗ với khách
    /// thật. Đó là loại hỏng không rút lại được: khách đã đọc rồi.</para>
    ///
    /// <para>Và lời dặn của công ty <b>đặt TRƯỚC</b> khung: phần cuối là phần model bám chặt nhất,
    /// nên các luật cấm phải nằm cuối để một câu vô ý trong phần công ty tự viết không đè được lên.</para>
    /// </summary>
    public string BuildSystemPrompt(string khung)
    {
        if (string.IsNullOrWhiteSpace(Persona)) return khung;

        var sb = new StringBuilder();
        sb.AppendLine("Thông tin riêng của công ty (dùng để trả lời cho đúng giọng và đúng nghiệp vụ):");
        sb.AppendLine(Persona!.Trim());
        sb.AppendLine();
        sb.AppendLine("--- Các luật dưới đây LUÔN thắng phần trên ---");
        sb.Append(khung);
        return sb.ToString();
    }
}
