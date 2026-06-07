// Services/Chat/AgentGuardrails.cs
using System.Text.RegularExpressions;
using System.Globalization;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Chat;

public static class AgentGuardrails
{
    // ---------------------------------------------------------------
    // StripEmDash: thay em-dash (U+2014) + en-dash (U+2013) thanh hyphen
    // ---------------------------------------------------------------

    /// Xoa em-dash va en-dash, thay bang hyphen thuong.
    public static string StripEmDash(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        return input.Replace('—', '-').Replace('–', '-');
    }

    // ---------------------------------------------------------------
    // TruncateInput: cat input qua maxLen ky tu
    // ---------------------------------------------------------------

    /// Cat input neu vuot qua maxLen. Tra ve (text, wasTruncated).
    public static (string Text, bool Truncated) TruncateInput(string? input, int maxLen = 1500)
    {
        if (string.IsNullOrEmpty(input)) return ("", false);
        if (input.Length <= maxLen) return (input, false);
        return (input[..maxLen].TrimEnd(), true);
    }

    // ---------------------------------------------------------------
    // IsTooShort: phan hoi <30 ky tu = qua ngan
    // ---------------------------------------------------------------

    /// Phan hoi qua ngan (<30 ky tu sau Trim) thi yeu cau retry.
    public static bool IsTooShort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text.Trim().Length < 30;
    }
}
