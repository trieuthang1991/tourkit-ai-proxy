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

    /// Canonical hóa params JSON → string deterministic cho cache key:
    ///   sort key alphabet, lowercase value (trừ marketName giữ case), trim.
    public static string CanonicalParams(JsonElement? p)
    {
        if (p == null || p.Value.ValueKind != JsonValueKind.Object) return "";
        var pairs = new List<string>();
        foreach (var prop in p.Value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var val = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => prop.Value.GetRawText()
            };
            val = val.Trim();
            // lowercase trừ marketName (giữ case cho readability)
            if (!string.Equals(prop.Name, "marketName", StringComparison.OrdinalIgnoreCase))
                val = val.ToLowerInvariant();
            pairs.Add($"{prop.Name}={val}");
        }
        return string.Join(";", pairs);
    }

    /// L1 cache key: tenant + username + câu hỏi đã normalize.
    /// Username bắt buộc để tránh cross-user leak khi phân quyền data khác nhau trong cùng tenant.
    public static string L1Key(string tenantId, string username, string? question)
        => $"{tenantId}|{username}|{Normalize(question)}";

    /// L2 cache key: tenant + username + tên tool + canonical params.
    /// Username bắt buộc để tránh cross-user leak khi phân quyền data khác nhau trong cùng tenant.
    public static string L2Key(string tenantId, string username, string toolName, JsonElement? prms)
        => $"{tenantId}|{username}|{toolName}|{CanonicalParams(prms)}";
}
