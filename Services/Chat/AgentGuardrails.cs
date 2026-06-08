// Services/Chat/AgentGuardrails.cs
using System.Text.RegularExpressions;
using System.Globalization;
using TourkitAiProxy.Models;
using System.Linq;

namespace TourkitAiProxy.Services.Chat;

public static class AgentGuardrails
{
    // Compiled regex dung chung, tranh interpreter overhead moi call
    private static readonly Regex _numberPattern = new(
        @"\b(\d{1,3}(?:[.,]\d{3})+|\d+\s*(?:ty|ti|trieu|tr|nghin|k))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _stripTrieu = new(@"trieu", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _stripNghin = new(@"nghin", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _digitsOnly = new(@"[^\d]", RegexOptions.Compiled);

    // Markdown patterns to strip (frontend không render markdown — phải gỡ trước khi trả)
    private static readonly Regex _mdBold       = new(@"\*\*([^*\n]+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex _mdItalicStar = new(@"(?<!\*)\*(?!\s|\*)([^*\n]+?)(?<!\s|\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex _mdItalicUnder = new(@"(?<!_)_([^_\n]+?)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex _mdHeading    = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex _mdCodeFence  = new(@"```[a-zA-Z]*\n?", RegexOptions.Compiled);
    private static readonly Regex _mdInlineCode = new(@"`([^`\n]+?)`", RegexOptions.Compiled);

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
    // StripMarkdown: go ** ## * _ ``` ` -- frontend khong render markdown
    // ---------------------------------------------------------------

    /// <summary>
    /// Gỡ markdown phổ biến (bold/italic/heading/code) thành text thuần.
    /// Frontend chỉ render plain text với `white-space: pre-wrap` nên markdown sẽ
    /// hiện như literal nếu không strip. Giữ nguyên xuống dòng và dấu gạch đầu dòng "-".
    /// </summary>
    public static string StripMarkdown(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        var s = input;
        // Code fence ``` (xóa marker, giữ nội dung)
        s = _mdCodeFence.Replace(s, "");
        // Inline code `xxx` → xxx
        s = _mdInlineCode.Replace(s, "$1");
        // Bold **xxx** → xxx
        s = _mdBold.Replace(s, "$1");
        // Italic *xxx* → xxx (cẩn thận không đụng bullet "* item" — pattern yêu cầu không space sau *)
        s = _mdItalicStar.Replace(s, "$1");
        // Italic _xxx_ → xxx
        s = _mdItalicUnder.Replace(s, "$1");
        // Heading "## Title" → "Title" (xóa dấu # đầu dòng)
        s = _mdHeading.Replace(s, "");
        return s;
    }

    // ---------------------------------------------------------------
    // TruncateInput: cat input qua maxLen ky tu
    // ---------------------------------------------------------------

    /// Cat input neu vuot qua maxLen. Tra ve (text, wasTruncated).
    public static (string Text, bool Truncated) TruncateInput(string? input, int maxLen = 1500)
    {
        if (string.IsNullOrEmpty(input)) return ("", false);
        if (maxLen <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLen), "maxLen phai > 0");
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

    // ---------------------------------------------------------------
    // ValidateNumbers: heuristic quet so AI noi, doi chieu voi stats
    // ---------------------------------------------------------------

    /// Quet so trong text AI, doi chieu voi stats server-side.
    /// Neu co so lon (>1000) lech >5x so voi tat ca stat - tra warning.
    /// Null = khong co van de.
    public static string? ValidateNumbers(string? text, IReadOnlyList<ChatStat>? stats)
    {
        if (string.IsNullOrWhiteSpace(text) || stats is null || stats.Count == 0) return null;

        // Tach so dang "1.000.000.000" hoac "200 trieu" hoac "5 ty"
        var matches = _numberPattern.Matches(text);

        if (matches.Count == 0) return null;

        var statValues = stats.Where(s => s.Value > 1000).Select(s => s.Value).ToList();
        if (statValues.Count == 0) return null;

        foreach (Match m in matches)
        {
            var parsed = ParseVndLike(m.Value);
            if (parsed <= 0) continue;

            // Cho phep lech toi <5x (AI thuong lam tron so); >=5x = drift canh bao
            bool nearAny = statValues.Any(v => parsed > v / 5.0 && parsed < v * 5.0);
            if (!nearAny)
                return $"AI co the tham chieu so khong khop stat (so {m.Value} khong gan stat nao)";
        }

        return null;
    }

    private static double ParseVndLike(string s)
    {
        s = s.Trim().ToLowerInvariant();
        double mult = 1;

        if (s.EndsWith("ty") || s.EndsWith("ti"))
        {
            mult = 1_000_000_000;
            s = s[..^2].Trim();
        }
        else if (s.Contains("trieu"))
        {
            mult = 1_000_000;
            s = _stripTrieu.Replace(s, "").Trim();
        }
        else if (s.EndsWith("tr"))
        {
            mult = 1_000_000;
            s = s[..^2].Trim();
        }
        else if (s.Contains("nghin"))
        {
            mult = 1_000;
            s = _stripNghin.Replace(s, "").Trim();
        }
        else if (s.EndsWith("k"))
        {
            mult = 1_000;
            s = s[..^1].Trim();
        }

        var digits = _digitsOnly.Replace(s, "");
        if (!double.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
            return 0;

        return n * mult;
    }
}
