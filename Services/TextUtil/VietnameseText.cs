using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.TextUtil;

/// <summary>
/// Chuẩn hóa tiếng Việt để so khớp: thường hóa + bỏ dấu + đ→d + gộp khoảng trắng.
///
/// 1 NGUỒN cho code mới. Repo đang có 4 bản sao cũ (ActionResolver.Norm,
/// ActionResolutionMemory.Norm, AgentCacheKeys.Normalize, JsonPlannerAgent.Norm) —
/// chưa migrate (ngoài phạm vi); đừng tạo thêm bản thứ 6.
///
/// LƯU Ý namespace: KHÔNG đặt "TourkitAiProxy.Services.Text" — segment "Text" sẽ che kiểu
/// DocumentFormat.OpenXml.Wordprocessing.Text mà DocxExtractor dùng (CS0118). Dùng "TextUtil".
/// </summary>
public static class VietnameseText
{
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant();
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            // Bỏ dấu thanh/dấu phụ (combining marks).
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            // đ/Đ không phân rã được bằng FormD → map tay.
            sb.Append(ch == 'đ' ? 'd' : ch);
        }
        // Gộp khoảng trắng thừa ("Hà  Nội" → "ha noi").
        return string.Join(' ', sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
