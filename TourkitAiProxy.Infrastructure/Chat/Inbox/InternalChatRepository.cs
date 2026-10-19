using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <summary>
/// Chat nội bộ giữa nhân viên: phòng là một dòng <c>chat_conversations</c> với channel = 7, tin là
/// dòng <c>chat_messages</c>, thành viên ở <c>chat_room_members</c>, đã-đọc dùng lại
/// <c>chat_conversation_reads</c>.
///
/// <para><b>Kiểm thành viên nằm TRONG chính câu đọc/ghi</b> (EXISTS trên chat_room_members), không
/// tách hàm riêng: tách ra là thêm một vòng gọi CSDL cho mỗi tin gửi và mỗi lần mở phòng.</para>
///
/// <para>⚠️ Đọc vào lớp dòng có thuộc tính set được rồi mới đổi sang record — Dapper không dựng
/// được record vị trí khi có cột <c>integer[]</c> (xem ghi chú ở <see cref="ChatAssignRepository"/>).</para>
/// </summary>
public class InternalChatRepository
{
    private readonly ChatDb _db;

    public InternalChatRepository(ChatDb db) => _db = db;
    public bool Configured => _db.Configured;

    private sealed class RoomRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? EntityType { get; set; }
        public string? EntityId { get; set; }
        public string RoomKey { get; set; } = "";
        public string? LastPreview { get; set; }
        public DateTime LastActivityAt { get; set; }
        public int UnreadCount { get; set; }
    }

    private sealed class MemberRow
    {
        public long ConversationId { get; set; }
        public int UserId { get; set; }
        public DateTime JoinedUtc { get; set; }
    }

    private sealed class MessageRow
    {
        public long Id { get; set; }
        public long RoomId { get; set; }
        public int? SenderUserId { get; set; }
        public string? SenderUsername { get; set; }
        public string? Body { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? DeletedUtc { get; set; }
        public string? Attachment { get; set; }
    }

    private sealed class AppendRow
    {
        public long? Id { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public int[]? Members { get; set; }
    }

    /// <summary>
    /// Tạo phòng, hoặc trả phòng đã có cùng khoá. Một giao dịch, hai câu: upsert phòng rồi thêm
    /// thành viên bằng MỘT câu unnest. Thành viên đã rời (left_utc) được thêm lại thì vào lại.
    /// </summary>
    /// <param name="memberIds">Đã gồm người tạo, đã bỏ trùng ở chỗ gọi.</param>
    public async Task<long> CreateOrGetRoomAsync(string tenant, int creatorUserId, string roomKey,
        string entityType, string? entityId, string? title, IReadOnlyCollection<int> memberIds,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO chat_conversations (tenant_id, channel, account_id, contact_external_id, surface,
                                            source_thread_id, entity_type, entity_id, title, last_activity_at)
            VALUES (@tenant, 7, '', @roomKey, 0, '', @entityType, @entityId, @title, now())
            ON CONFLICT (tenant_id, channel, account_id, contact_external_id, surface, source_thread_id)
            DO UPDATE SET title = COALESCE(chat_conversations.title, EXCLUDED.title)
            RETURNING id
            """, new { tenant, roomKey, entityType, entityId, title }, tx, cancellationToken: ct));

        var ids = memberIds.Append(creatorUserId).Distinct().ToArray();
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO chat_room_members (conversation_id, tenant_id, user_id)
            -- DISTINCT: mảng có trùng thì Postgres ném "ON CONFLICT DO UPDATE cannot affect row a
            -- second time" (đo trên PG 17, 19/10/2026) — chặn ở đây kể cả khi chỗ gọi quên lọc.
            SELECT DISTINCT @id, @tenant, u FROM unnest(@ids) AS u
            ON CONFLICT (conversation_id, user_id) DO UPDATE SET left_utc = NULL
             WHERE chat_room_members.tenant_id = @tenant
            """, new { id, tenant, ids }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Phòng của một người, mới hoạt động trước. Hai vòng gọi: phòng (kèm số chưa đọc), rồi
    /// thành viên của đúng các phòng đó — không hỏi thành viên từng phòng một.
    ///
    /// <para>Số chưa đọc có TRẦN 100: phòng bị bỏ quên lâu không được kéo chậm cả danh sách, và
    /// giao diện chỉ cần hiện "99+".</para>
    /// </summary>
    public async Task<IReadOnlyList<InternalRoom>> ListRoomsAsync(string tenant, int userId, int limit,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<RoomRow>(new CommandDefinition("""
            SELECT v.id, v.title, v.entity_type, v.entity_id, v.contact_external_id AS room_key,
                   v.last_preview, v.last_activity_at,
                   (SELECT count(*)::int FROM (
                      SELECT 1 FROM chat_messages m
                       WHERE m.conversation_id = v.id
                         AND m.created_utc > COALESCE(r.last_read_at, mb.joined_utc)
                         AND m.sender_user_id IS DISTINCT FROM @userId
                         AND m.deleted_utc IS NULL
                       LIMIT 100) x) AS unread_count
              FROM chat_room_members mb
              JOIN chat_conversations v
                ON v.id = mb.conversation_id AND v.tenant_id = @tenant AND v.channel = 7
              LEFT JOIN chat_conversation_reads r
                ON r.tenant_id = @tenant AND r.conversation_id = v.id AND r.user_id = @userId
             WHERE mb.tenant_id = @tenant AND mb.user_id = @userId AND mb.left_utc IS NULL
             ORDER BY v.last_activity_at DESC, v.id DESC
             LIMIT @limit
            """, new { tenant, userId, limit }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return Array.Empty<InternalRoom>();

        var ids = rows.Select(r => r.Id).ToArray();
        var members = (await c.QueryAsync<MemberRow>(new CommandDefinition("""
            SELECT conversation_id, user_id, joined_utc
              FROM chat_room_members
             WHERE tenant_id = @tenant AND conversation_id = ANY(@ids) AND left_utc IS NULL
            """, new { tenant, ids }, cancellationToken: ct)))
            .ToLookup(m => m.ConversationId, m => new InternalMember(m.UserId, m.JoinedUtc));

        return rows.Select(r => new InternalRoom(r.Id, r.Title, r.EntityType ?? "direct", r.EntityId,
            r.RoomKey, r.LastPreview, r.LastActivityAt, r.UnreadCount, members[r.Id].ToList())).ToList();
    }

    /// <summary>
    /// Lịch sử, mới nhất trước, phân trang con trỏ (created_utc, id) — khớp chỉ mục ix_msg_conv.
    /// Trả <c>null</c> khi người hỏi không phải thành viên (chỗ gọi trả 403).
    /// </summary>
    public async Task<IReadOnlyList<InternalMessage>?> ListMessagesAsync(string tenant, long roomId, int userId,
        DateTime? beforeUtc, long? beforeId, int limit, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<MessageRow>(new CommandDefinition("""
            SELECT m.id, m.conversation_id AS room_id, m.sender_user_id, m.sender_username, m.body,
                   m.created_utc, m.deleted_utc, m.attachment::text AS attachment
              FROM chat_messages m
             WHERE m.conversation_id = @roomId AND m.tenant_id = @tenant
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
               AND (@beforeUtc::timestamptz IS NULL
                    OR (m.created_utc, m.id) < (@beforeUtc::timestamptz, @beforeId::bigint))
             ORDER BY m.created_utc DESC, m.id DESC
             LIMIT @limit
            """, new { tenant, roomId, userId, beforeUtc = AsUtc(beforeUtc), beforeId = beforeId ?? long.MaxValue, limit },
            cancellationToken: ct))).ToList();
        if (rows.Count == 0 && !await IsMemberOrEmptyAsync(c, tenant, roomId, userId, ct)) return null;
        return rows.Select(ToMessage).ToList();
    }

    /// <summary>Tin MỚI hơn một mã, cũ trước — để widget bù tin khi có sự kiện hoặc vừa nối lại.</summary>
    public async Task<IReadOnlyList<InternalMessage>?> ListMessagesAfterAsync(string tenant, long roomId, int userId,
        long afterId, int limit, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<MessageRow>(new CommandDefinition("""
            SELECT m.id, m.conversation_id AS room_id, m.sender_user_id, m.sender_username, m.body,
                   m.created_utc, m.deleted_utc, m.attachment::text AS attachment
              FROM chat_messages m
             WHERE m.conversation_id = @roomId AND m.tenant_id = @tenant AND m.id > @afterId
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
             ORDER BY m.id
             LIMIT @limit
            """, new { tenant, roomId, userId, afterId, limit }, cancellationToken: ct))).ToList();
        if (rows.Count == 0 && !await IsMemberOrEmptyAsync(c, tenant, roomId, userId, ct)) return null;
        return rows.Select(ToMessage).ToList();
    }

    /// <summary>
    /// Ghi tin — MỘT câu: kiểm thành viên, chèn tin, đẩy xem trước của phòng, và trả luôn danh sách
    /// thành viên để bus biết giao cho ai. Không phải thành viên thì không chèn gì, <c>Id</c> null.
    ///
    /// <para>direction = 1 và không đính kèm nên vòng soi tệp (ix_msg_media_cho, direction = 0) không
    /// bao giờ chạm tới; không ghi chat_outbox nên worker gửi ra kênh cũng không.</para>
    /// </summary>
    public async Task<AppendResult> AppendMessageAsync(string tenant, long roomId, int senderUserId,
        string senderUsername, string? body, InternalAttachment? attachment = null, CancellationToken ct = default)
    {
        // kind: 0 chữ · 1 ảnh · 2 tệp (ChatKind). Xem trước của phòng: chữ nếu có, không thì tên tệp.
        short kind = attachment is null ? (short)0 : attachment.Kind == "anh" ? (short)1 : (short)2;
        var att = attachment is null ? null : System.Text.Json.JsonSerializer.Serialize(
            new { url = attachment.Url, ten = attachment.Name, kich = attachment.Size, loai = attachment.Kind });
        var preview = !string.IsNullOrEmpty(body) ? body
            : attachment is null ? "" : (kind == 1 ? "[Ảnh] " : "[Tệp] ") + attachment.Name;
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleAsync<AppendRow>(new CommandDefinition("""
            WITH tv AS (
              SELECT array_agg(user_id) AS members FROM chat_room_members
               WHERE conversation_id = @roomId AND tenant_id = @tenant AND left_utc IS NULL),
            m AS (
              INSERT INTO chat_messages (tenant_id, conversation_id, channel, direction, sender_kind,
                                         sender_username, sender_user_id, kind, body, attachment, state)
              SELECT @tenant, @roomId, 7, 1, 2, @senderUsername, @senderUserId, @kind, @body, @att::jsonb, 1
               WHERE EXISTS (SELECT 1 FROM tv WHERE @senderUserId = ANY(tv.members))
              RETURNING id, created_utc),
            u AS (
              UPDATE chat_conversations v
                 SET last_preview = left(@preview, 200), last_activity_at = m.created_utc
                FROM m
               WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7)
            SELECT m.id, m.created_utc, tv.members FROM tv LEFT JOIN m ON true
            """, new { tenant, roomId, senderUserId, senderUsername, body, kind, att, preview }, cancellationToken: ct));
        return new AppendResult(r.Id, r.CreatedUtc, r.Members ?? Array.Empty<int>());
    }

    // ── Cấu hình phòng ─────────────────────────────────────────────────────
    //
    // Luật (chốt 19/10/2026, mức vừa phải): phòng 1-1 KHOÁ — không đổi tên, không thêm/bỏ người.
    // Nhóm / theo đơn / theo công ty: thành viên nào cũng được đổi tên, thêm, bỏ người, tự rời.
    // Mỗi hàm là MỘT câu: điều kiện "người làm là thành viên" + "không phải 1-1" nằm trong câu.

    /// <summary>Đổi tên phòng. false = không phải thành viên, hoặc là phòng 1-1.</summary>
    public async Task<bool> RenameRoomAsync(string tenant, long roomId, int userId, string? title,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE chat_conversations v SET title = @title
             WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
               AND v.entity_type IS DISTINCT FROM 'direct'
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
            """, new { tenant, roomId, userId, title }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Thêm người. Người đã rời được thêm lại thì vào lại, mốc vào tính lại từ bây giờ (đếm chưa
    /// đọc từ lúc vào lại, không dội cả lịch sử lúc vắng mặt). 0 = không được phép.
    /// </summary>
    public async Task<int> AddMembersAsync(string tenant, long roomId, int userId, IReadOnlyCollection<int> ids,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO chat_room_members (conversation_id, tenant_id, user_id)
            SELECT DISTINCT @roomId, @tenant, u FROM unnest(@ids) AS u
             WHERE EXISTS (SELECT 1 FROM chat_conversations v
                            WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
                              AND v.entity_type IS DISTINCT FROM 'direct')
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
            ON CONFLICT (conversation_id, user_id) DO UPDATE
               SET joined_utc = CASE WHEN chat_room_members.left_utc IS NULL
                                     THEN chat_room_members.joined_utc ELSE now() END,
                   left_utc = NULL
             WHERE chat_room_members.tenant_id = @tenant
            """, new { tenant, roomId, userId, ids = ids.Distinct().ToArray() }, cancellationToken: ct));
    }

    /// <summary>Bỏ một người khỏi phòng (bỏ chính mình = rời phòng). Xoá mềm: tin cũ của họ vẫn còn.</summary>
    public async Task<bool> RemoveMemberAsync(string tenant, long roomId, int userId, int targetUserId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE chat_room_members t SET left_utc = now()
             WHERE t.conversation_id = @roomId AND t.tenant_id = @tenant
               AND t.user_id = @targetUserId AND t.left_utc IS NULL
               AND EXISTS (SELECT 1 FROM chat_conversations v
                            WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
                              AND v.entity_type IS DISTINCT FROM 'direct')
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
            """, new { tenant, roomId, userId, targetUserId }, cancellationToken: ct)) > 0;
    }

    /// <summary>Thành viên hiện tại — chỉ để bus biết báo cho ai sau khi cấu hình phòng đổi.</summary>
    public async Task<int[]> MembersAsync(string tenant, long roomId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<int>(new CommandDefinition("""
            SELECT user_id FROM chat_room_members
             WHERE conversation_id = @roomId AND tenant_id = @tenant AND left_utc IS NULL
            """, new { tenant, roomId }, cancellationToken: ct))).ToArray();
    }

    /// <summary>Đánh dấu đã đọc cho riêng một người. false = không phải thành viên.</summary>
    public async Task<bool> MarkReadAsync(string tenant, long roomId, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO chat_conversation_reads (tenant_id, conversation_id, user_id, last_read_at)
            SELECT @tenant, @roomId, @userId, now()
             WHERE EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
            ON CONFLICT (tenant_id, conversation_id, user_id) DO UPDATE SET last_read_at = now()
            """, new { tenant, roomId, userId }, cancellationToken: ct)) > 0;
    }

    /// <summary>Thu hồi tin CỦA CHÍNH MÌNH — xoá mềm, giữ dòng (như tin khách tự xoá).</summary>
    public async Task<bool> DeleteOwnMessageAsync(string tenant, long messageId, int userId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE chat_messages SET deleted_utc = now()
             WHERE id = @messageId AND tenant_id = @tenant AND channel = 7
               AND sender_user_id = @userId AND deleted_utc IS NULL
            """, new { tenant, messageId, userId }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Có phải thành viên phòng không — CHỈ cho đường tải tệp lên: phải hỏi TRƯỚC khi đẩy byte lên kho,
    /// không thì người ngoài phòng vẫn đổ được tệp vào kho rồi mới bị từ chối. Đường đọc/ghi tin
    /// không dùng hàm này (kiểm gộp trong câu, xem ghi chú lớp).
    /// </summary>
    public async Task<bool> IsMemberForUploadAsync(string tenant, long roomId, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await IsMemberOrEmptyAsync(c, tenant, roomId, userId, ct);
    }

    /// Chỉ chạy khi câu đọc trả RỖNG (hiếm): phân biệt "phòng chưa có tin" với "không phải thành viên".
    private static Task<bool> IsMemberOrEmptyAsync(Npgsql.NpgsqlConnection c, string tenant, long roomId,
        int userId, CancellationToken ct)
        => c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM chat_room_members
                            WHERE conversation_id = @roomId AND tenant_id = @tenant
                              AND user_id = @userId AND left_utc IS NULL)
            """, new { tenant, roomId, userId }, cancellationToken: ct));

    private static InternalMessage ToMessage(MessageRow m) => new(m.Id, m.RoomId, m.SenderUserId,
        m.SenderUsername, m.DeletedUtc is null ? m.Body : null, m.CreatedUtc, m.DeletedUtc,
        m.DeletedUtc is null ? ReadAttachment(m.Attachment) : null);

    /// Đọc gói đính kèm {url, ten, kich, loai}. Hỏng/thiếu url thì bỏ đính kèm, KHÔNG làm hỏng tin.
    public static InternalAttachment? ReadAttachment(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object
                || !r.TryGetProperty("url", out var u) || u.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            var ten = r.TryGetProperty("ten", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()! : "tệp";
            var kich = r.TryGetProperty("kich", out var k) && k.TryGetInt64(out var kv) ? kv : 0;
            var loai = r.TryGetProperty("loai", out var l) && l.GetString() == "anh" ? "anh" : "tep";
            return new InternalAttachment(u.GetString()!, ten, kich, loai);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// Npgsql từ chối DateTime Kind=Unspecified cho timestamptz. Toàn hệ quy ước UTC
    /// (docs/datetime-convention.md) nên Unspecified được hiểu là UTC.
    private static DateTime? AsUtc(DateTime? d) => d switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } u => u,
        { Kind: DateTimeKind.Local } l => l.ToUniversalTime(),
        { } x => DateTime.SpecifyKind(x, DateTimeKind.Utc),
    };
}
