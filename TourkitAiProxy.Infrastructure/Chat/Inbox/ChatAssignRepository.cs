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

    /// <summary>
    /// Gán hội thoại cho người kế tiếp trong đội trực — MỘT câu lệnh.
    ///
    /// <para><b>Điều kiện là "chưa ai phụ trách", KHÔNG phải "vừa tạo mới".</b>
    /// <c>GetOrCreateConversationAsync</c> chạy lại ở mỗi tin khách gửi, và hội thoại đóng rồi
    /// khách nhắn lại vẫn phải có người. Điều kiện này bao cả hai, và chạy bao nhiêu lần cũng ra
    /// một kết quả.</para>
    ///
    /// <para><b>Vì sao một câu lệnh:</b> đọc con trỏ rồi mới ghi thì hai tin tới cùng lúc sẽ gán
    /// hai người khác nhau, cái sau đè cái trước, khách nhận hai lời chào. Ở đây lượt thứ hai
    /// chặn trên khoá dòng của <c>chat_assign_settings</c>, và <c>assigned_user_id IS NULL</c>
    /// nằm trong chính câu UPDATE nên CSDL quyết định ai thắng.</para>
    ///
    /// <para>⚠️ Trường hợp hai lượt xảy ra đúng cùng lúc trên CÙNG một hội thoại: lượt thua vẫn
    /// đẩy con trỏ lên một nấc dù không gán được ai — tức một người bị bỏ lượt. Chấp nhận: hậu
    /// quả là lệch một lượt trong vòng, KHÔNG bao giờ là hai người cùng một hội thoại.</para>
    ///
    /// <para>Trả <c>null</c> khi: chế độ không phải xoay vòng · đội trực rỗng · hội thoại đã có
    /// người. Ba trường hợp đều là "không làm gì", chỗ gọi không cần phân biệt.</para>
    /// </summary>
    public async Task<int?> GanXoayVongAsync(
        string tenant, long conversationId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int?>("""
            WITH doi AS (
                SELECT unnest(member_ids) AS user_id
                  FROM chat_assign_settings
                 WHERE tenant_id = @tenant
            ),
            chon AS (
                UPDATE chat_assign_settings s
                   SET rotation_last_user_id = COALESCE(
                         (SELECT MIN(user_id) FROM doi
                           WHERE user_id > COALESCE(s.rotation_last_user_id, 0)),
                         (SELECT MIN(user_id) FROM doi)),
                       updated_utc = now()
                 WHERE s.tenant_id = @tenant
                   AND s.mode = 2
                   AND EXISTS (SELECT 1 FROM doi)
                   AND EXISTS (SELECT 1 FROM chat_conversations v
                                WHERE v.id = @id AND v.tenant_id = @tenant
                                  AND v.assigned_user_id IS NULL)
                RETURNING s.rotation_last_user_id AS user_id
            )
            UPDATE chat_conversations v
               SET assigned_user_id = chon.user_id,
                   status = CASE WHEN v.status = 2 THEN v.status ELSE 1 END
              FROM chon
             WHERE v.id = @id AND v.tenant_id = @tenant AND v.assigned_user_id IS NULL
            RETURNING v.assigned_user_id
            """, new { tenant, id = conversationId });
    }
}
