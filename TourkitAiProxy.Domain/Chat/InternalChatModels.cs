namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Khoá phòng chat nội bộ, ghi vào <c>chat_conversations.contact_external_id</c> (channel = 7).
///
/// <para>Chống trùng nằm ở chỉ mục duy nhất sẵn có <c>ux_conv_scope_surface</c>, nên khoá phải
/// SINH RA GIỐNG HỆT cho cùng một phòng: 1-1 xếp hai mã tăng dần (A mở với B hay B mở với A là
/// cùng phòng), đơn/công ty mỗi thứ đúng một phòng. Ghi chuỗi rỗng là mỗi công ty chỉ tạo được
/// đúng một phòng — dòng thứ hai vỡ chỉ mục.</para>
/// </summary>
public static class InternalRoomKey
{
    public const short Channel = (short)ChatChannel.Internal;

    public static bool IsValidType(string? t) => t is "direct" or "order" or "company" or "group";

    public static string Direct(int a, int b)
    {
        if (a == b) throw new ArgumentException("Phòng 1-1 cần hai người khác nhau");
        return a < b ? $"direct:{a}:{b}" : $"direct:{b}:{a}";
    }

    public static string ForEntity(string entityType, string entityId)
    {
        if (entityType is not ("order" or "company"))
            throw new ArgumentException($"Loại phòng theo ngữ cảnh không hợp lệ: {entityType}");
        var id = entityId?.Trim();
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Thiếu mã đơn/công ty");
        return $"{entityType}:{id}";
    }

    public static string Group() => "group:" + Guid.NewGuid().ToString("N");
}

public record InternalMember(int UserId, DateTime JoinedUtc);

public record InternalRoom(long Id, string? Title, string EntityType, string? EntityId, string RoomKey,
    string? LastPreview, DateTime LastActivityAt, int UnreadCount, IReadOnlyList<InternalMember> Members);

public record InternalMessage(long Id, long RoomId, int? SenderUserId, string? SenderUsername,
    string? Body, DateTime CreatedUtc, DateTime? DeletedUtc);

public record CreateRoomRequest(string? Title, List<int>? MemberUserIds, string EntityType = "direct",
    string? EntityId = null);

public record SendInternalMessageRequest(string? Body);

/// <summary>Kết quả ghi tin. <c>Id</c> null = người gửi không phải thành viên (chỗ gọi trả 403).
/// <c>Members</c> để bus biết giao sự kiện cho ai — lấy ngay trong câu ghi, không hỏi lại.</summary>
public record AppendResult(long? Id, DateTime? CreatedUtc, int[] Members);
