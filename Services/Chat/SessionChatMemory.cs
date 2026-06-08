// Services/Chat/SessionChatMemory.cs
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Chat;

/// <summary>
/// Bộ nhớ chat per-session, lưu cùng TkSession xuống đĩa.
/// Phục vụ câu follow-up: "còn X thì sao" kế thừa tool + params lần trước.
/// LastChatData: data panel phải gần nhất → khi follow-up không gọi lại tool, vẫn giữ chart/table cũ.
/// </summary>
public record SessionChatMemory(
    DateTime LastUpdated,
    string? LastTool,                          // tên tool gần nhất, vd "cashflow"
    Dictionary<string, string>? LastParams,    // params string-only (JSON serialize gọn)
    string? LastMarketName,                    // tên thị trường gần nhất user nhắc
    int? LastMarketId,                         // id đã resolve của market đó
    string? LastDataTitle,                     // title của ChatData gần nhất (cho "lặp lại bảng vừa rồi")
    ChatData? LastChatData,                    // FULL data panel phải gần nhất — để follow-up text-only vẫn hiện chart/table
    List<ChatTurn> History                     // tối đa 10 turn gần nhất (đã trim)
)
{
    /// Khởi tạo bộ nhớ trống cho phiên mới.
    public static SessionChatMemory Empty() => new(
        LastUpdated:   DateTime.UtcNow,
        LastTool:      null,
        LastParams:    null,
        LastMarketName: null,
        LastMarketId:  null,
        LastDataTitle: null,
        LastChatData:  null,
        History:       new List<ChatTurn>()
    );
}
