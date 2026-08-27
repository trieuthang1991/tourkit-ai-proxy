// Services/Chat/Channels/MetaButtonBuilder.cs
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Dựng phần <c>message</c> có NÚT cho nền tảng nhắn tin của Meta — dùng chung Messenger và Instagram.
///
/// <para><b>Meta có HAI cơ chế nút hoàn toàn khác nhau</b>, và chọn nhầm thì tin vẫn đi nhưng hỏng
/// theo kiểu khó thấy:</para>
/// <list type="table">
///   <listheader><term>Cơ chế</term><description>Đặc điểm</description></listheader>
///   <item>
///     <term><c>quick_replies</c></term>
///     <description>Tối đa <b>13</b>. Chỉ trả lời nhanh, KHÔNG chứa được liên kết. Hiện thành dải
///     nút ngang ô soạn và <b>biến mất sau khi bấm</b> — đúng cho câu hỏi chọn một trong nhiều.</description>
///   </item>
///   <item>
///     <term>khung nút (<c>button</c> template)</term>
///     <description>Tối đa <b>3</b>. Chứa được nút mở liên kết. Nút <b>nằm lại trong dòng tin mãi
///     mãi</b> và bấm lại được nhiều lần.</description>
///   </item>
/// </list>
///
/// <para>Nhét liên kết vào <c>quick_replies</c> thì Meta bỏ luôn phần liên kết; nhét 13 nút vào
/// khung nút thì Meta từ chối cả tin. Cả hai đều không có lỗi nào dễ đọc.</para>
///
/// <para><b>Payload của nút trả lời nhanh CHÍNH LÀ chữ trên nút.</b> Dự án tham chiếu nhét vào đó
/// một mã trỏ tới bước trong luồng, vì bot của họ là máy chạy luồng. Bot bên mình là trợ lý AI, nên
/// khách bấm nút = khách nói câu đó, và bộ bóc tin <b>vốn đã</b> ghi lượt bấm bằng chữ trên nút
/// (xem <see cref="MetaMessagingParser"/>). Một vòng khép kín, không thêm trạng thái nào.</para>
///
/// <para>Hàm THUẦN, không chạm mạng, có test.</para>
/// </summary>
public static class MetaButtonBuilder
{
    /// <summary>
    /// Dựng phần <c>message</c> đầy đủ. Có nút mở liên kết → khung nút; không có → trả lời nhanh.
    /// </summary>
    public static object Build(string text, IReadOnlyList<ChatButton> nut)
    {
        if (nut.Count == 0) return new { text };

        return nut.Any(x => x.IsLink)
            ? new
            {
                attachment = new
                {
                    type = "template",
                    payload = new
                    {
                        template_type = "button",
                        text,
                        buttons = nut.Select(ButtonNode).ToArray(),
                    },
                },
            }
            : QuickReplyMessage(text, nut);
    }

    /// <summary>Chỉ phần trả lời nhanh — Instagram dùng riêng vì kênh đó không có khung nút.</summary>
    public static JsonObject QuickReplyMessage(string text, IReadOnlyList<ChatButton> nut)
    {
        var m = new JsonObject { ["text"] = text };
        if (nut.Count == 0) return m;

        var ds = new JsonArray();
        foreach (var b in nut)
            ds.Add(new JsonObject
            {
                ["content_type"] = "text",
                // ⚠️ Nhãn quá 20 ký tự là Meta từ chối CẢ TIN, không phải cắt bớt chữ.
                ["title"] = Cat(b.Label, 20),
                // Payload là chính chữ trên nút — xem ghi chú ở lớp.
                ["payload"] = Cat(b.Label, 1000),
            });

        m["quick_replies"] = ds;
        return m;
    }

    private static object ButtonNode(ChatButton b) => b.IsLink
        ? new { type = "web_url", title = Cat(b.Label, 20), url = b.Url }
        : (object)new { type = "postback", title = Cat(b.Label, 20), payload = Cat(b.Label, 1000) };

    private static string Cat(string s, int n) => s.Length <= n ? s : s[..n];
}
