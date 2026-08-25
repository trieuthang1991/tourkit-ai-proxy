using TourkitAiProxy.Domain.Digest;
namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Tách chuỗi "nhiều nơi nhận, cách nhau bằng dấu" mà người dùng gõ tay trong ô cấu hình tác vụ.
///
/// <para><b>Vì sao có kiểu khai này, khác hẳn các cảnh báo còn lại.</b> Cảnh báo thu tiền hay nhắc
/// chăm khách đều gửi cho <i>người phụ trách</i> — hệ thống tự tra ra. Nhưng cảnh báo doanh thu bất
/// thường thì <b>không ai phụ trách</b>: doanh thu là số của cả công ty. Và nó mang số liệu tài
/// chính toàn công ty, nên phải để người có thẩm quyền chỉ đích danh ai được nhận, chứ không phải
/// "ai bật email thì nhận".</para>
///
/// <para><b>Chấp nhận mọi dấu ngăn thường gặp</b> (phẩy, chấm phẩy, xuống dòng, tab) vì người dùng
/// hay dán từ Excel hoặc Zalo. Bắt đúng một dấu duy nhất thì họ dán vào, thấy "đã lưu", rồi sáng
/// hôm sau không nhận được gì mà không hiểu vì sao.</para>
///
/// <para><b>Bỏ dòng sai, KHÔNG ném lỗi.</b> Một địa chỉ gõ nhầm không đáng làm hỏng cả lượt gửi cho
/// những người còn lại. Số bỏ được đếm và nói ra trong tóm tắt lần chạy.</para>
/// </summary>
public static class AlertRecipients
{
    /// <summary>
    /// Dấu ngăn — CỐ Ý KHÔNG có DẤU CÁCH.
    ///
    /// <para>Bản đầu có dấu cách, và test bắt ngay: <c>+84 987 654 321</c> bị xé thành bốn mảnh
    /// "84"/"987"/"654"/"321". Người Việt viết số điện thoại tách nhóm là chuyện thường, nên dấu
    /// cách phải nằm TRONG một giá trị chứ không phải giữa hai giá trị.</para>
    ///
    /// <para>Đánh đổi: ai dán danh sách email chỉ ngăn bằng dấu cách sẽ ra một chuỗi vô nghĩa rồi
    /// bị loại. Chấp nhận — kiểu dán đó hiếm, còn số điện thoại có dấu cách thì phổ biến; và mất
    /// một email thì hiện ra ngay ở tóm tắt, chứ số điện thoại bị xé thì gửi nhầm trong im lặng.</para>
    /// </summary>
    private static readonly char[] Seps = { ',', ';', '\n', '\r', '\t' };

    private static IEnumerable<string> Split(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(Seps, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                 .Where(x => x.Length > 0);

    /// Email hợp lệ tối thiểu: có '@' và có chấm sau '@'. Cố ý không dùng regex RFC —
    /// nó vừa dài vừa loại oan địa chỉ nội bộ, mà sai thật thì worker báo lại ngay.
    public static List<string> Emails(string? raw)
        => Split(raw)
            .Where(x => x.Contains('@') && x.LastIndexOf('.') > x.IndexOf('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// Chat id Telegram là SỐ, và có thể ÂM (id của nhóm bắt đầu bằng '-'). Bản đầu lọc
    /// <c>char.IsDigit</c> trần sẽ cắt mất dấu trừ → gửi vào một nhóm khác hoặc lỗi "chat not found".
    public static List<string> TelegramChatIds(string? raw)
        => Split(raw)
            .Select(x => x.StartsWith('-') ? "-" + new string(x.Skip(1).Where(char.IsDigit).ToArray())
                                           : new string(x.Where(char.IsDigit).ToArray()))
            .Where(x => x.Length > 1 || (x.Length == 1 && char.IsDigit(x[0])))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// Số Zalo — chuẩn hoá về 0xxxxxxxxx ngay lúc đọc, cùng bộ luật với ô "Nơi nhận của tôi"
    /// (<see cref="DigestPhone"/>), để hai chỗ khai không cho ra hai kết quả khác nhau.
    /// <remarks>
    /// Lọc bằng <see cref="DigestPhone.IsValid"/> chứ KHÔNG phải "Normalize khác null".
    /// <c>Normalize</c> cố ý trả lại nguyên bản khi không rút được chữ số nào (để chỗ nhập một ô
    /// còn báo được lỗi cho người dùng), nên chuỗi rác như "khongphaiso" vẫn qua — test bắt được
    /// đúng ca đó. Ở đây không có ai để hỏi lại, nên phải loại thẳng.
    /// </remarks>
    public static List<string> ZaloPhones(string? raw)
        => Split(raw)
            .Where(DigestPhone.IsValid)
            .Select(x => DigestPhone.Normalize(x)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
