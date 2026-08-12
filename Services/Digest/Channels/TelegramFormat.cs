using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Đổi markdown tối giản của bản tin sang Telegram HTML (parse_mode=HTML), cắt 4096 ký tự.
///
/// <para><b>Thứ tự quan trọng: ESCAPE TRƯỚC, đổi <c>**x**</c> thành thẻ SAU.</b> Làm ngược thì
/// chính thẻ <c>&lt;b&gt;</c> mình vừa tạo cũng bị escape → Telegram in ra chữ "&lt;b&gt;" thay vì
/// in đậm. Nhờ thứ tự này mà thẻ HTML người dùng gõ vào (tên khách, tiêu đề deal) bị vô hiệu hoá,
/// còn thẻ mình chủ động tạo thì giữ nguyên.</para>
/// </summary>
public static class TelegramFormat
{
    /// Telegram giới hạn 4096 ký tự/tin nhắn — vượt là API trả lỗi, mất cả bản tin.
    private const int MaxLen = 4096;

    public static string ToTelegramHtml(string title, string bodyMarkdown)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        var body = Esc(bodyMarkdown ?? "");
        body = Regex.Replace(body, @"\*\*(.+?)\*\*", "<b>$1</b>");
        var text = $"<b>{Esc(title ?? "")}</b>\n\n{body}";
        return text.Length <= MaxLen ? text : text[..(MaxLen - 3)] + "…";
    }
}
