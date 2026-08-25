using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Domain.Digest;

/// <summary>
/// Đổi nội dung bản tin (markdown-lite: **in đậm** + gạch đầu dòng + emoji tiêu đề) sang câu chữ
/// ĐỌC ĐƯỢC cho TTS. Đọc thẳng markdown lên loa nghe "sao sao Cơ hội gạch... đê 1 chấm 234 đê" —
/// nên bỏ ký hiệu, đổi emoji/đơn vị thành lời, ghép dòng thành câu. THUẦN → test được.
/// Dùng cho nút "Nghe" ở Bảng tin; là 1 NGUỒN để không lệch cách đọc giữa các chỗ.
/// </summary>
public static class BriefNarration
{
    // \p{So}=Symbol-other (✅⚠…), \p{Cs}=surrogate (emoji astral như 📞 là cặp surrogate), FE0F=variation, 2022=bullet •
    private static readonly Regex Decor = new(@"[\p{So}\p{Cs}️•]", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(\d)\s*đ\b", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string ToSpeakable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            line = Bold.Replace(line, "$1");            // **x** → x
            line = Decor.Replace(line, "");             // bỏ emoji + ký hiệu trang trí
            if (line.StartsWith("- ")) line = line[2..]; // bỏ gạch đầu dòng
            line = line.Trim();
            if (line.Length == 0) continue;

            line = Money.Replace(line, "$1 đồng");       // 1.234đ → 1.234 đồng
            line = line.Replace("%", " phần trăm");
            line = line.Replace(" · ", ", ");   // "·" ngăn cụm → phẩy cho giọng đọc nghỉ tự nhiên
            line = MultiSpace.Replace(line, " ").Trim();
            if (line.Length == 0) continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            // Chưa có dấu kết câu → thêm chấm để giọng đọc ngắt nghỉ giữa các mục.
            if (line[^1] is not ('.' or '!' or '?' or ':' or ';')) sb.Append('.');
        }
        return sb.ToString().Trim();
    }
}
