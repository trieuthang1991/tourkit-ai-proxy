using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Kênh nào bản cài đặt này KHÔNG mời nối — đọc từ <c>Chat:HiddenChannels</c>.
///
/// <para>Ca dùng thật (10/11/2026): Facebook chưa duyệt ứng dụng Meta cho TourKit, nên tin
/// Facebook đi vòng qua Pancake. Bày tab Messenger ra là mời nhân viên bấm vào một kênh không
/// nối được, rồi hỏi nhau vì sao.</para>
///
/// <para><b>Vì sao KHÔNG dùng cờ <c>Features:*</c>.</b> Cờ đó trả lời "tính năng đã ra mắt chưa",
/// và repo này đã BỎ bốn cờ con của chat (<c>ChatHistoryImport</c>, <c>ChatAssign</c>,
/// <c>ChatCoHoi</c>, <c>ChatTourLookup</c>) vì cùng một lý do: cờ con cho một điều kiện TẠM THỜI
/// thì điều kiện hết mà cờ ở lại vĩnh viễn. Đây là chuyện khác — người vận hành khai bản cài đặt
/// của mình mời những kênh nào, giống khai OA hay Trang. Nó là cấu hình, không phải cờ ra mắt.</para>
///
/// <para><b>Thiếu khoá = KHÔNG ẩn gì</b> — ngược hướng an toàn của <c>FeatureFlags</c>, và cố ý.
/// Cờ ra mắt sai hướng thì thứ chưa ra mắt lọt ra bản public, nên phải mặc định TẮT. Ẩn một kênh
/// thì không lộ gì cho ai cả; mặc định ẩn chỉ đẻ ra một cách hỏng câm — ai quên khai lúc deploy là
/// mất sạch kênh đang chạy thật mà không có lấy một dòng log.</para>
///
/// <para><b>Ẩn KHÁC gỡ.</b> Đường nhận tin vẫn chạy nguyên: Meta/Zalo đánh dấu một địa chỉ nhận
/// lỗi là hỏng, tới lúc duyệt xong phải đi khai lại từ đầu. Hội thoại cũ cũng vẫn đọc và trả lời
/// được ở mục "Tất cả" — ẩn cái tab không được phép chôn dữ liệu thật của khách.</para>
/// </summary>
public static class ChatChannelVisibility
{
    public const string Key = "Chat:HiddenChannels";

    public static IReadOnlySet<ChatChannel> Hidden(IConfiguration cfg)
        => Parse(cfg.GetSection(Key).Get<string[]>());

    public static bool IsHidden(IConfiguration cfg, ChatChannel channel)
        => Hidden(cfg).Contains(channel);

    /// <summary>
    /// Hàm THUẦN để test. Nhận tên kênh (<c>"Messenger"</c>) hoặc số (<c>"1"</c>) — người khai
    /// cấu hình đọc tên dễ hơn số, nhưng bản ghi cũ và máy sinh cấu hình thì tiện số hơn.
    ///
    /// <para>Tên lạ bị BỎ QUA chứ không ném lỗi: gõ sai một chữ trong file cấu hình mà làm cả máy
    /// chủ không khởi động được là đổi một phiền nhỏ lấy một sự cố lớn.</para>
    /// </summary>
    public static IReadOnlySet<ChatChannel> Parse(IEnumerable<string?>? raw)
    {
        var ra = new HashSet<ChatChannel>();
        if (raw == null) return ra;
        foreach (var o in raw)
        {
            if (string.IsNullOrWhiteSpace(o)) continue;
            var s = o.Trim();
            if (Enum.TryParse<ChatChannel>(s, ignoreCase: true, out var kenh)
                && Enum.IsDefined(typeof(ChatChannel), kenh))
                ra.Add(kenh);
        }
        return ra;
    }
}
