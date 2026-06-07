// Services/Chat/AgentCacheKeys.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace TourkitAiProxy.Services.Chat;

public static class AgentCacheKeys
{
    /// Chuẩn hóa câu hỏi cho L1 cache key: lowercase + bỏ dấu + gộp whitespace.
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = input.Trim().ToLowerInvariant();
        // bỏ dấu tiếng Việt
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c);
        }
        var noDiacritic = sb.ToString().Replace('đ', 'd').Replace('Đ', 'd').Normalize(NormalizationForm.FormC);
        // gộp whitespace
        return System.Text.RegularExpressions.Regex.Replace(noDiacritic, @"\s+", " ").Trim();
    }
}
