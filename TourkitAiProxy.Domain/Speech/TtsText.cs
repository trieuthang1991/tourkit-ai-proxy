namespace TourkitAiProxy.Domain.Speech;

/// <summary>
/// Cắt bớt chữ trước khi gửi sang engine đọc, khi vượt giới hạn của nhà cung cấp.
///
/// <para><b>Vì sao không chỉ là <c>Substring</c>:</b> bản cũ cắt thẳng ở đúng ký tự thứ N, nên câu
/// đứt giữa từ và người nghe không biết là còn nữa — bấm "Nghe" một bản tin dài sẽ nghe hết nửa câu
/// rồi im, tưởng bản tin chỉ có thế. Kiểu hỏng im lặng này tốn thời gian nhất để phát hiện, vì
/// không có lỗi nào hiện lên cả.</para>
///
/// <para>Ở đây KHÔNG nâng giới hạn — giới hạn là của nhà cung cấp. Chỉ làm hai việc: cắt vào chỗ
/// nghỉ tự nhiên, và trả cờ <c>Truncated</c> để nơi gọi còn nói ra được.</para>
/// </summary>
public static class TtsText
{
    /// Chỉ lùi tối đa 40% độ dài cho phép để tìm chỗ nghỉ. Không có mức này thì một câu đầu rất
    /// ngắn ("Ừ.") sẽ hút điểm cắt về đầu bài và nuốt gần hết nội dung — chữa một lỗi, tạo lỗi tệ hơn.
    private const double MaxBackoff = 0.40;

    public record Result(string Text, bool Truncated);

    public static Result Cap(string? text, int maxChars)
    {
        var s = (text ?? "").Trim();
        if (s.Length <= maxChars) return new Result(s, false);

        var floor = (int)(maxChars * (1 - MaxBackoff));
        var window = s.Substring(0, maxChars);

        // 1) Ưu tiên hết câu — chỗ nghỉ tự nhiên nhất khi đọc.
        var end = window.LastIndexOfAny(new[] { '.', '!', '?' });
        if (end >= floor) return new Result(window.Substring(0, end + 1).TrimEnd(), true);

        // 2) Không có thì hết từ — miễn đừng đứt giữa chữ.
        var space = window.LastIndexOf(' ');
        if (space >= floor) return new Result(window.Substring(0, space).TrimEnd(), true);

        // 3) Một từ dài quá không cho cắt: thà cắt cứng còn hơn trả về rỗng.
        return new Result(window, true);
    }
}
