// Services/Chat/Channels/ChatHistoryImport.cs
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Những thứ DÙNG CHUNG cho mọi bộ kéo hội thoại cũ: kết quả một lượt, trần an toàn, và câu hỏi
/// "kênh này lấy lại được không".
///
/// <para><b>Vì sao tách ra.</b> Ba thứ này từng nằm trong <see cref="MetaHistoryImporter"/>, nên
/// khi có bộ kéo thứ hai (<see cref="PancakeHistoryImporter"/>) thì Pancake phải trả về
/// <c>MetaHistoryImporter.KetQua</c> và endpoint phải hỏi một lớp mang tên Meta xem Pancake có
/// làm được không. Tên nói sai chỗ là cái bẫy cho người sau: ai đọc cũng tưởng chỉ Meta mới có
/// lịch sử, rồi thêm kênh thứ ba lại chép thêm một bản hằng số nữa.</para>
/// </summary>
public static class ChatHistoryImport
{
    /// <summary>Trần hội thoại mỗi lượt bấm. Còn nữa thì giao diện mời bấm tiếp.</summary>
    public const int MaxConversations = 200;

    /// <summary>Trần tin mỗi hội thoại. Đủ ngữ cảnh bán hàng, không phải bản sao lưu.</summary>
    public const int MaxMessagesPerConversation = 200;

    /// <param name="SoHoiThoai">Số hội thoại đã đọc.</param>
    /// <param name="SoTin">Số tin đã xếp vào hàng đợi — <b>chưa</b> phải số tin ghi được, vì tin
    /// đã có sẵn sẽ bị chống trùng loại bỏ ở bước sau.</param>
    /// <param name="ConNua">Còn hội thoại chưa đọc vì chạm mức chặn. Giao diện nói ra để người
    /// dùng biết bấm thêm lượt nữa, thay vì tưởng đã lấy hết.</param>
    public record KetQua(int SoHoiThoai, int SoTin, bool ConNua, string? Loi);

    /// <summary>
    /// Kênh nào có đường lấy lại hội thoại cũ. <b>Hỏi ở đây</b>, đừng hỏi
    /// <see cref="MetaHistoryImporter.Supports"/> — hàm đó chỉ nói "bộ kéo Meta làm được không",
    /// nên Pancake (có bộ kéo riêng) sẽ trả false và nút "Lấy hội thoại cũ" biến mất khỏi giao
    /// diện mà không có lỗi nào.
    /// </summary>
    public static bool Supports(ChatChannel kenh)
        => MetaHistoryImporter.Supports(kenh) || kenh is ChatChannel.Pancake;
}
