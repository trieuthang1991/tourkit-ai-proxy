using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <summary>
/// Cấu hình phân công + đội trực chat.
///
/// <para>Tách khỏi <see cref="ChatRepository"/> vì đó đã 1.000 dòng và đây là chuyện khác:
/// kia là hội thoại và tin nhắn, đây là luật chia việc.</para>
/// </summary>
public class ChatAssignRepository
{
    private readonly ChatDb _db;
    public ChatAssignRepository(ChatDb db) => _db = db;
    public bool Configured => _db.Configured;

    /// Trả null khi công ty chưa cấu hình — chỗ gọi phải hiểu null là "giữ nguyên hành vi cũ",
    /// KHÔNG phải "chế độ thủ công". Hai thứ khác nhau ở luật xem.
    public async Task<ChatAssignSettings?> LayCauHinhAsync(string tenant, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatAssignSettings>("""
            SELECT tenant_id, mode, scope_own_only, auto_assign_on_reply,
                   member_ids, rotation_last_user_id
              FROM chat_assign_settings WHERE tenant_id = @tenant
            """, new { tenant });
    }

    /// Ghi ĐÈ cả cấu hình lẫn đội trực trong MỘT lệnh. Tách hai lượt ghi thì có khoảnh khắc
    /// chế độ đã là xoay vòng mà đội trực còn rỗng — và trong khoảnh khắc đó mọi hội thoại tới
    /// đều rơi về hàng chờ, im lặng.
    public async Task LuuCauHinhAsync(string tenant, short mode, bool scopeOwnOnly,
        bool autoAssignOnReply, int[] memberIds, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_assign_settings
                   (tenant_id, mode, scope_own_only, auto_assign_on_reply, member_ids, updated_utc)
            VALUES (@tenant, @mode, @scopeOwnOnly, @autoAssignOnReply, @memberIds, now())
            ON CONFLICT (tenant_id) DO UPDATE
               SET mode = EXCLUDED.mode,
                   scope_own_only = EXCLUDED.scope_own_only,
                   auto_assign_on_reply = EXCLUDED.auto_assign_on_reply,
                   member_ids = EXCLUDED.member_ids,
                   updated_utc = now()
            """, new { tenant, mode, scopeOwnOnly, autoAssignOnReply, memberIds });
        // ⚠️ KHÔNG đụng rotation_last_user_id: sửa cấu hình mà đặt lại con trỏ thì mỗi lần
        // quản trị bấm Lưu là vòng quay bắt đầu lại từ người đầu danh sách.
    }
}
