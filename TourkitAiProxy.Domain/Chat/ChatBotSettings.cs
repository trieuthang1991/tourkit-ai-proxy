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
public record ChatBotSettings(
    bool Enabled = true,
    string? Persona = null,
    string? Greeting = null,
    int MuteMinutes = 30,
    int HistoryTurns = 12)
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
