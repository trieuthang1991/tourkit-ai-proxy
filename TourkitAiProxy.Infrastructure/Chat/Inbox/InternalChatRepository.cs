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

    /// sender_kind của chat_messages: 3 = hệ thống (xem ChatDb).
    private const short SystemSenderKind = 3;
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
        public long? LastMessageId { get; set; }
        public string? DataJson { get; set; }
        public int? OwnerUserId { get; set; }
    }

    /// JSON đã lưu → phần tử để trả ra API; hỏng (không thể xảy ra vì cột là jsonb) thì coi như không có.
    private static System.Text.Json.JsonElement? ParseData(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { using var d = System.Text.Json.JsonDocument.Parse(json); return d.RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return null; }
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
        public DateTime? EditedUtc { get; set; }
        public short SenderKind { get; set; }
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
    /// <returns><c>Created</c> = phòng VỪA được tạo ở lượt này (chưa từng có) — để chỗ gọi làm việc
    /// chỉ-một-lần như thêm người phụ trách cơ hội.</returns>
    /// <param name="ownerUserId">Trưởng nhóm do CRM ký (phòng đơn/công ty, đã kiểm chữ ký ở chỗ gọi); null =
    /// người tạo là trưởng nhóm. Phòng đã có: đồng bộ sang người này nếu họ đang ở phòng.</param>
    public async Task<(long Id, bool Created, OwnerSync Owner)> CreateOrGetRoomAsync(string tenant, int creatorUserId, string roomKey,
        string entityType, string? entityId, string? title, IReadOnlyCollection<int> memberIds,
        string? dataJson = null, int? ownerUserId = null, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Phòng ĐÃ CÓ thì không ghi gì (DO NOTHING → không trả dòng → đọc lại). Trước đây DO UPDATE ở
        // mọi lần bấm Chat: mỗi cú bấm là một dòng chết trong bảng hội thoại. Hai người bấm cùng lúc:
        // người thua chờ người thắng commit rồi đọc lại ra đúng dòng đó (read committed).
        var created = true;
        var id = await c.ExecuteScalarAsync<long?>(new CommandDefinition("""
            INSERT INTO chat_conversations (tenant_id, channel, account_id, contact_external_id, surface,
                                            source_thread_id, entity_type, entity_id, title, entity_data, last_activity_at,
                                            owner_user_id)
            VALUES (@tenant, 7, '', @roomKey, 0, '', @entityType, @entityId, @title, @dataJson::jsonb, now(),
                    CASE WHEN @entityType = 'direct' THEN NULL ELSE COALESCE(@ownerUserId::integer, @creatorUserId) END)
            ON CONFLICT (tenant_id, channel, account_id, contact_external_id, surface, source_thread_id) DO NOTHING
            RETURNING id
            """, new { tenant, roomKey, entityType, entityId, title, dataJson, ownerUserId, creatorUserId }, tx, cancellationToken: ct));
        if (id is null)
        {
            created = false;
            id = await c.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT id FROM chat_conversations
                 WHERE tenant_id = @tenant AND channel = 7 AND account_id = '' AND contact_external_id = @roomKey
                   AND surface = 0 AND source_thread_id = ''
                """, new { tenant, roomKey }, tx, cancellationToken: ct));
            // Phòng chưa có tên mà lượt này mang tên (vd phòng cơ hội tạo từ API trần) → đặt tên, MỘT lần.
            if (!string.IsNullOrWhiteSpace(title))
                await c.ExecuteAsync(new CommandDefinition("""
                    UPDATE chat_conversations SET title = @title
                     WHERE id = @id AND tenant_id = @tenant AND title IS NULL
                    """, new { id, tenant, title }, tx, cancellationToken: ct));
        }

        // Người khác CHỈ được thêm lúc phòng vừa tạo. Phòng đã có thì chỉ người bấm (vào lại nếu đã rời):
        // thêm cả danh sách ở mọi lần bấm là kéo lại người đã bị bỏ khỏi phòng / đã tự rời.
        // Trưởng nhóm do CRM ký luôn có mặt lúc tạo phòng (người tạo đơn có thể không phải người bấm).
        var ids = created
            ? memberIds.Append(creatorUserId).Concat(ownerUserId is int o0 ? new[] { o0 } : Array.Empty<int>()).Distinct().ToArray()
            : new[] { creatorUserId };
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO chat_room_members (conversation_id, tenant_id, user_id)
            -- DISTINCT: mảng có trùng thì Postgres ném "ON CONFLICT DO UPDATE cannot affect row a
            -- second time" (đo trên PG 17, 19/10/2026) — chặn ở đây kể cả khi chỗ gọi quên lọc.
            SELECT DISTINCT @id, @tenant, u FROM unnest(@ids) AS u
            -- Chỉ ghi khi người đó VÀO LẠI (đã rời): mốc vào tính lại từ bây giờ. Đang là thành viên thì
            -- không đụng gì — bấm Chat lại không sinh dòng chết.
            ON CONFLICT (conversation_id, user_id) DO UPDATE
               SET joined_utc = now(), left_utc = NULL
             WHERE chat_room_members.left_utc IS NOT NULL AND chat_room_members.tenant_id = @tenant
            """, new { id, tenant, ids }, tx, cancellationToken: ct));
        var sync = default(OwnerSync);
        if (!created)
        {
            if (ownerUserId is int o) sync = await SyncOwnerCoreAsync(c, tx, tenant, id.Value, o, ct);
            // Phòng mất trưởng nhóm (mọi người đã rời rồi có người vào lại) → người vào lại nhận quyền.
            // Chỉ ghi khi đang NULL — bấm Chat lại phòng bình thường không sinh dòng chết.
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE chat_conversations SET owner_user_id = @creatorUserId
                 WHERE id = @id AND tenant_id = @tenant AND channel = 7 AND owner_user_id IS NULL
                   AND entity_type IS DISTINCT FROM 'direct'
                """, new { id, tenant, creatorUserId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return (id.Value, created, sync);
    }

    /// <summary>
    /// Phòng đơn/công ty: trưởng nhóm bám người tạo bản ghi ở CRM (chữ ký đã kiểm ở chỗ gọi) — chốt 27/10/2026:
    /// <list type="bullet">
    /// <item>Đang ở phòng → nhận quyền (chỉ ghi khi KHÁC người đang giữ).</item>
    /// <item>CHƯA TỪNG ở phòng (phòng cũ tạo khi chưa có luật này) → thêm vào phòng + nhận quyền.</item>
    /// <item>Từng ở rồi rời / bị bỏ → KHÔNG kéo lại; quyền ở người được chuyển lúc họ rời.</item>
    /// </list>
    /// </summary>
    public async Task<OwnerSync> SyncOwnerAsync(string tenant, long roomId, int ownerUserId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await SyncOwnerCoreAsync(c, null, tenant, roomId, ownerUserId, ct);
    }

    /// Added = vừa thêm người tạo bản ghi vào phòng (chỗ gọi ghi tin hệ thống); Changed = trưởng nhóm vừa đổi.
    public readonly record struct OwnerSync(bool Added, bool Changed);

    private sealed class OwnerSyncRow
    {
        public bool Added { get; set; }
        public bool Changed { get; set; }
    }

    private static async Task<OwnerSync> SyncOwnerCoreAsync(Npgsql.NpgsqlConnection c, System.Data.Common.DbTransaction? tx,
        string tenant, long roomId, int ownerUserId, CancellationToken ct)
    {
        // Một câu. "Chưa từng ở" = không có DÒNG nào (kể cả dòng đã rời) — có dòng left_utc là đã rời, không kéo lại.
        // CTE cùng câu thấy dữ liệu TRƯỚC khi chèn → điều kiện nhận quyền phải OR với chính dòng vừa chèn.
        var r = await c.QuerySingleAsync<OwnerSyncRow>(new CommandDefinition("""
            WITH v AS (
              SELECT id FROM chat_conversations
               WHERE id = @roomId AND tenant_id = @tenant AND channel = 7
                 AND entity_type IN ('order', 'company') AND deleted_utc IS NULL),
            ins AS (
              INSERT INTO chat_room_members (conversation_id, tenant_id, user_id)
              SELECT v.id, @tenant, @ownerUserId FROM v
               WHERE NOT EXISTS (SELECT 1 FROM chat_room_members mb
                                  WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                                    AND mb.user_id = @ownerUserId)
              ON CONFLICT (conversation_id, user_id) DO NOTHING
              RETURNING user_id),
            up AS (
              UPDATE chat_conversations c SET owner_user_id = @ownerUserId
                FROM v WHERE c.id = v.id AND c.tenant_id = @tenant
                 AND c.owner_user_id IS DISTINCT FROM @ownerUserId
                 AND (EXISTS (SELECT 1 FROM ins)
                      OR EXISTS (SELECT 1 FROM chat_room_members mb
                                  WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                                    AND mb.user_id = @ownerUserId AND mb.left_utc IS NULL))
              RETURNING 1)
            SELECT EXISTS (SELECT 1 FROM ins) AS added, EXISTS (SELECT 1 FROM up) AS changed
            """, new { tenant, roomId, ownerUserId }, tx, cancellationToken: ct));
        return new OwnerSync(r.Added, r.Changed);
    }

    /// <summary>
    /// Phòng của một người, mới hoạt động trước. Hai vòng gọi: phòng (kèm số chưa đọc), rồi
    /// thành viên của đúng các phòng đó — không hỏi thành viên từng phòng một.
    ///
    /// <para>Số chưa đọc có TRẦN 100: phòng bị bỏ quên lâu không được kéo chậm cả danh sách, và
    /// giao diện chỉ cần hiện "99+".</para>
    /// </summary>
    /// <param name="beforeAt">Con trỏ trang sau: mốc hoạt động + mã của phòng CUỐI trang trước (không OFFSET).</param>
    /// <param name="like">Mẫu tìm tên phòng / mã bản ghi (<see cref="InternalRoomSearch.LikePattern"/>); null = không tìm.</param>
    /// <param name="nameIds">Mã những người có tên khớp chuỗi tìm (chỗ gọi tra danh bạ) — phòng có họ cũng khớp.</param>
    public async Task<IReadOnlyList<InternalRoom>> ListRoomsAsync(string tenant, int userId, int limit,
        DateTime? beforeAt = null, long? beforeId = null, string? like = null, int[]? nameIds = null,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // Đi TỪ phòng của người này (ix_room_members_user, vài chục dòng) — MATERIALIZED để planner
        // không đổi sang đi chỉ mục hoạt động của cả công ty rồi lọc kênh 7 (review 19/10/2026: công
        // ty nhiều hội thoại khách là quét gần hết, mà mỗi tin mới mọi thành viên đều gọi lại câu này).
        // Chưa đọc tính từ mốc MUỘN NHẤT trong (đọc lần cuối, vào phòng, xoá phía tôi) — GREATEST bỏ qua NULL.
        // Phòng đã "xoá phía tôi" chỉ hiện lại khi có hoạt động SAU mốc xoá.
        var rows = (await c.QueryAsync<RoomRow>(new CommandDefinition("""
            WITH mb AS MATERIALIZED (
              SELECT conversation_id, joined_utc, hidden_utc FROM chat_room_members
               WHERE tenant_id = @tenant AND user_id = @userId AND left_utc IS NULL)
            SELECT v.id, v.title, v.entity_type, v.entity_id, v.contact_external_id AS room_key,
                   v.last_preview, v.last_activity_at, v.entity_data::text AS data_json, v.owner_user_id,
                   (SELECT count(*)::int FROM (
                      SELECT 1 FROM chat_messages m
                       WHERE m.conversation_id = v.id
                         AND m.created_utc > GREATEST(r.last_read_at, mb.joined_utc, mb.hidden_utc)
                         AND m.sender_user_id IS DISTINCT FROM @userId
                         AND m.deleted_utc IS NULL
                         AND m.sender_kind <> 3   -- tin hệ thống ("A đã thêm B") không tính chưa đọc
                       LIMIT 100) x) AS unread_count,
                   -- Mã tin mới nhất (kể cả tin hệ thống / đã thu hồi): widget so với mã trong sự kiện để
                   -- không cộng chưa đọc hai lần cho cùng một tin. Đi ix_msg_conv lùi, một dòng.
                   (SELECT x.id FROM chat_messages x WHERE x.conversation_id = v.id
                     ORDER BY x.created_utc DESC, x.id DESC LIMIT 1) AS last_message_id
              FROM mb
              -- LATERAL + LIMIT 1 ép thứ tự: mỗi phòng của người này → tra ĐÚNG một dòng theo khoá
              -- chính. Thiếu LIMIT 1 thì Postgres kéo phẳng subquery thành JOIN thường rồi lại đi
              -- ix_conv_tenant_hoatdong của CẢ công ty để khỏi sắp xếp (đo trên PG 17, 19/10/2026).
              CROSS JOIN LATERAL (
                SELECT c.id, c.title, c.entity_type, c.entity_id, c.contact_external_id,
                       c.last_preview, c.last_activity_at, c.entity_data, c.owner_user_id
                  FROM chat_conversations c
                 WHERE c.id = mb.conversation_id AND c.tenant_id = @tenant AND c.channel = 7
                 LIMIT 1) v
              LEFT JOIN chat_conversation_reads r
                ON r.tenant_id = @tenant AND r.conversation_id = v.id AND r.user_id = @userId
             WHERE (mb.hidden_utc IS NULL OR v.last_activity_at > mb.hidden_utc)
               -- Trang sau: con trỏ (mốc hoạt động, mã) — khớp đúng thứ tự sắp xếp bên dưới; không đếm bỏ N dòng
               -- đầu (trang càng sâu càng chậm, và lệch khi có phòng mới nhảy lên đầu giữa hai lần tải).
               AND (@beforeAt::timestamptz IS NULL
                    OR (v.last_activity_at, v.id) < (@beforeAt::timestamptz, @beforeId::bigint))
               -- Tìm: tên phòng / mã bản ghi, hoặc phòng có người mang tên khớp (mã do chỗ gọi tra danh bạ).
               AND (@like::text IS NULL
                    OR v.title ILIKE @like ESCAPE '\' OR v.entity_id ILIKE @like ESCAPE '\'
                    OR EXISTS (SELECT 1 FROM chat_room_members o
                                WHERE o.conversation_id = v.id AND o.tenant_id = @tenant AND o.left_utc IS NULL
                                  AND o.user_id <> @userId AND o.user_id = ANY(@nameIds)))
             ORDER BY v.last_activity_at DESC, v.id DESC
             LIMIT @limit
            """, new { tenant, userId, limit, beforeAt = AsUtc(beforeAt), beforeId = beforeId ?? long.MaxValue,
                       like, nameIds = nameIds ?? Array.Empty<int>() }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return Array.Empty<InternalRoom>();

        var ids = rows.Select(r => r.Id).ToArray();
        var members = (await c.QueryAsync<MemberRow>(new CommandDefinition("""
            SELECT conversation_id, user_id, joined_utc
              FROM chat_room_members
             WHERE tenant_id = @tenant AND conversation_id = ANY(@ids) AND left_utc IS NULL
            """, new { tenant, ids }, cancellationToken: ct)))
            .ToLookup(m => m.ConversationId, m => new InternalMember(m.UserId, m.JoinedUtc));

        return rows.Select(r => new InternalRoom(r.Id, r.Title, r.EntityType ?? "direct", r.EntityId,
            r.RoomKey, r.LastPreview, r.LastActivityAt, r.UnreadCount, members[r.Id].ToList(), r.LastMessageId,
            ParseData(r.DataJson), r.OwnerUserId)).ToList();
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
                   m.created_utc, m.deleted_utc, m.edited_utc, m.sender_kind, m.attachment::text AS attachment
              FROM chat_messages m
             WHERE m.conversation_id = @roomId AND m.tenant_id = @tenant
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
               -- "Xoá phía tôi": tin cũ hơn mốc xoá không hiện lại với người này.
               AND m.created_utc > COALESCE((SELECT h.hidden_utc FROM chat_room_members h
                                              WHERE h.conversation_id = @roomId AND h.tenant_id = @tenant
                                                AND h.user_id = @userId), '-infinity'::timestamptz)
               AND (@beforeUtc::timestamptz IS NULL
                    OR (m.created_utc, m.id) < (@beforeUtc::timestamptz, @beforeId::bigint))
             ORDER BY m.created_utc DESC, m.id DESC
             LIMIT @limit
            """, new { tenant, roomId, userId, beforeUtc = AsUtc(beforeUtc), beforeId = beforeId ?? long.MaxValue, limit },
            cancellationToken: ct))).ToList();
        if (rows.Count == 0 && !await IsMemberOrEmptyAsync(c, tenant, roomId, userId, ct)) return null;
        var rx = await ReactionsOfAsync(c, tenant, rows, ct);
        return rows.Select(m => ToMessage(m, rx.GetValueOrDefault(m.Id))).ToList();
    }

    /// <summary>Tin MỚI hơn một mã, cũ trước — để widget bù tin khi có sự kiện hoặc vừa nối lại.</summary>
    public async Task<IReadOnlyList<InternalMessage>?> ListMessagesAfterAsync(string tenant, long roomId, int userId,
        long afterId, int limit, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<MessageRow>(new CommandDefinition("""
            SELECT m.id, m.conversation_id AS room_id, m.sender_user_id, m.sender_username, m.body,
                   m.created_utc, m.deleted_utc, m.edited_utc, m.sender_kind, m.attachment::text AS attachment
              FROM chat_messages m
             WHERE m.conversation_id = @roomId AND m.tenant_id = @tenant AND m.id > @afterId
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL)
               -- "Xoá phía tôi": tin cũ hơn mốc xoá không hiện lại với người này.
               AND m.created_utc > COALESCE((SELECT h.hidden_utc FROM chat_room_members h
                                              WHERE h.conversation_id = @roomId AND h.tenant_id = @tenant
                                                AND h.user_id = @userId), '-infinity'::timestamptz)
             ORDER BY m.id
             LIMIT @limit
            """, new { tenant, roomId, userId, afterId, limit }, cancellationToken: ct))).ToList();
        if (rows.Count == 0 && !await IsMemberOrEmptyAsync(c, tenant, roomId, userId, ct)) return null;
        var rx = await ReactionsOfAsync(c, tenant, rows, ct);
        return rows.Select(m => ToMessage(m, rx.GetValueOrDefault(m.Id))).ToList();
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
        var preview = InternalMessagePreview.Of(body, attachment);
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
    // Nhóm / theo đơn / theo công ty: thành viên nào cũng được đổi tên, thêm người, tự rời.
    // Bỏ NGƯỜI KHÁC / chuyển quyền / xoá phòng: chỉ trưởng nhóm (owner_user_id — InternalRoomOwner, 27/10/2026).
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

    private sealed class LeaveRow
    {
        public bool Ok { get; set; }
        public int? NewOwner { get; set; }
    }

    /// <summary>
    /// Bỏ một người khỏi phòng (bỏ chính mình = rời phòng). Xoá mềm: tin cũ của họ vẫn còn.
    /// Bỏ NGƯỜI KHÁC: chỉ trưởng nhóm. Tự rời: ai cũng được; trưởng nhóm rời thì quyền sang người vào
    /// phòng sớm nhất trong số còn lại (cùng câu — không có quãng phòng không có trưởng nhóm).
    /// </summary>
    /// <returns>Ok = đã bỏ; NewOwner = trưởng nhóm mới khi chính trưởng nhóm vừa rời.</returns>
    public async Task<(bool Ok, int? NewOwner)> RemoveMemberAsync(string tenant, long roomId, int userId, int targetUserId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (targetUserId != userId)
            return (await c.ExecuteAsync(new CommandDefinition("""
                UPDATE chat_room_members t SET left_utc = now()
                 WHERE t.conversation_id = @roomId AND t.tenant_id = @tenant
                   AND t.user_id = @targetUserId AND t.left_utc IS NULL
                   AND EXISTS (SELECT 1 FROM chat_conversations v
                                WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
                                  AND v.entity_type IS DISTINCT FROM 'direct'
                                  AND v.owner_user_id = @userId)
                   AND EXISTS (SELECT 1 FROM chat_room_members mb
                                WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                                  AND mb.user_id = @userId AND mb.left_utc IS NULL)
                """, new { tenant, roomId, userId, targetUserId }, cancellationToken: ct)) > 0, null);

        // CTE cùng câu thấy dữ liệu TRƯỚC khi đổi → người rời vẫn "đang ở" trong n, nên loại bằng user_id.
        // Người vào cùng lúc (cả nhóm tạo một lượt) → xếp tiếp theo mã người cho ổn định.
        var r = await c.QuerySingleAsync<LeaveRow>(new CommandDefinition("""
            WITH t AS (
              UPDATE chat_room_members SET left_utc = now()
               WHERE conversation_id = @roomId AND tenant_id = @tenant AND user_id = @userId AND left_utc IS NULL
                 AND EXISTS (SELECT 1 FROM chat_conversations v
                              WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
                                AND v.entity_type IS DISTINCT FROM 'direct')
              RETURNING conversation_id),
            n AS (
              SELECT mb.user_id FROM chat_room_members mb
               WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant AND mb.left_utc IS NULL
                 AND mb.user_id <> @userId AND EXISTS (SELECT 1 FROM t)
               ORDER BY mb.joined_utc, mb.user_id LIMIT 1),
            o AS (
              UPDATE chat_conversations v SET owner_user_id = (SELECT user_id FROM n)
                FROM t WHERE v.id = t.conversation_id AND v.tenant_id = @tenant AND v.channel = 7
                 AND v.owner_user_id = @userId
              RETURNING v.owner_user_id)
            SELECT EXISTS (SELECT 1 FROM t) AS ok, (SELECT owner_user_id FROM o) AS new_owner
            """, new { tenant, roomId, userId }, cancellationToken: ct));
        return (r.Ok, r.NewOwner);
    }

    /// <summary>
    /// Chuyển quyền trưởng nhóm — chỉ trưởng nhóm hiện tại, cho một thành viên đang ở phòng. Phòng đơn/công
    /// ty không chuyển tay (quyền bám người tạo bản ghi ở CRM). false = không được phép.
    /// </summary>
    public async Task<bool> TransferOwnerAsync(string tenant, long roomId, int userId, int targetUserId,
        CancellationToken ct = default)
    {
        if (targetUserId == userId) return false;
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE chat_conversations v SET owner_user_id = @targetUserId
             WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7 AND v.deleted_utc IS NULL
               AND v.owner_user_id = @userId
               AND COALESCE(v.entity_type, '') NOT IN ('direct', 'order', 'company')
               AND EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                              AND mb.user_id = @targetUserId AND mb.left_utc IS NULL)
            """, new { tenant, roomId, userId, targetUserId }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Xoá phòng — chỉ trưởng nhóm. Xoá MỀM: giữ tin (dấu vết), đánh dấu deleted_utc, mọi thành viên rời
    /// phòng (nên mọi đường đọc/ghi vốn kiểm thành viên tự chặn, không phải sửa từng câu), và khoá phòng
    /// đổi đuôi để mở lại bản ghi đó (đơn/công ty) ra phòng MỚI chứ không lôi phòng đã xoá về.
    /// </summary>
    /// <returns>Những người vừa bị đưa ra (để báo); rỗng = không được phép / không có phòng.</returns>
    public async Task<int[]> DeleteRoomAsync(string tenant, long roomId, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<int>(new CommandDefinition("""
            WITH v AS (
              UPDATE chat_conversations SET deleted_utc = now(),
                     contact_external_id = contact_external_id || '#xoa:' || id::text
               WHERE id = @roomId AND tenant_id = @tenant AND channel = 7 AND deleted_utc IS NULL
                 AND entity_type IS DISTINCT FROM 'direct' AND owner_user_id = @userId
                 AND EXISTS (SELECT 1 FROM chat_room_members mb
                              WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                                AND mb.user_id = @userId AND mb.left_utc IS NULL)
              RETURNING id),
            m AS (
              UPDATE chat_room_members mb SET left_utc = now()
                FROM v WHERE mb.conversation_id = v.id AND mb.tenant_id = @tenant AND mb.left_utc IS NULL
              RETURNING mb.user_id)
            SELECT user_id FROM m
            """, new { tenant, roomId, userId }, cancellationToken: ct))).ToArray();
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

    /// <summary>
    /// Ghi một dòng thông báo hệ thống vào phòng ("A đã thêm B…") — để thay đổi phòng có dấu vết.
    /// sender_kind = 3, không có người gửi nên không ai sửa/thu hồi được; không tính chưa đọc.
    /// Đẩy xem trước + mốc hoạt động như tin thường. Trả mã tin + thành viên để bus báo.
    /// </summary>
    public async Task<AppendResult> AppendSystemMessageAsync(string tenant, long roomId, string body,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<AppendRow>(new CommandDefinition("""
            WITH m AS (
              INSERT INTO chat_messages (tenant_id, conversation_id, channel, direction, sender_kind, kind, body, state)
              SELECT @tenant, v.id, 7, 1, 3, 0, @body, 1
                FROM chat_conversations v WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7
              RETURNING id, created_utc),
            u AS (
              UPDATE chat_conversations v SET last_preview = left(@body, 200), last_activity_at = m.created_utc
                FROM m WHERE v.id = @roomId AND v.tenant_id = @tenant AND v.channel = 7)
            SELECT m.id, m.created_utc,
                   (SELECT array_agg(user_id) FROM chat_room_members
                     WHERE conversation_id = @roomId AND tenant_id = @tenant AND left_utc IS NULL) AS members
              FROM m
            """, new { tenant, roomId, body }, cancellationToken: ct));
        // Không có dòng = phòng không thuộc công ty này / không phải phòng nội bộ → không ghi gì.
        return r is null ? new AppendResult(null, null, Array.Empty<int>())
                         : new AppendResult(r.Id, r.CreatedUtc, r.Members ?? Array.Empty<int>());
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

    /// <summary>
    /// Thu hồi tin CỦA CHÍNH MÌNH — xoá mềm với MỌI người, giữ dòng (như tin khách tự xoá).
    /// Trả mã phòng để bus báo cả phòng; null = không phải tin của mình / đã thu hồi.
    /// </summary>
    public async Task<long?> DeleteOwnMessageAsync(string tenant, long messageId, int userId,
        CancellationToken ct = default)
    {
        // Một câu: thu hồi + tính lại dòng xem trước của phòng (không thì chữ đã thu hồi vẫn hiện trên
        // danh sách của MỌI người). CTE cùng câu thấy dữ liệu TRƯỚC khi đổi → tự loại tin vừa thu hồi.
        // Người đã rời phòng không được đổi lịch sử.
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition("""
            WITH d AS (
              UPDATE chat_messages t SET deleted_utc = now()
               WHERE t.id = @messageId AND t.tenant_id = @tenant AND t.channel = 7
                 AND t.sender_user_id = @userId AND t.deleted_utc IS NULL
                 AND EXISTS (SELECT 1 FROM chat_room_members mb
                              WHERE mb.conversation_id = t.conversation_id AND mb.tenant_id = @tenant
                                AND mb.user_id = @userId AND mb.left_utc IS NULL)
              RETURNING conversation_id),
            p AS (
              UPDATE chat_conversations v SET last_preview = (
                  SELECT left(COALESCE(NULLIF(x.body, ''), CASE x.kind WHEN 1 THEN '[Ảnh] ' ELSE '[Tệp] ' END
                                       || COALESCE(x.attachment->>'ten', '')), 200)
                    FROM chat_messages x
                   WHERE x.conversation_id = v.id AND x.id <> @messageId AND x.deleted_utc IS NULL
                   ORDER BY x.created_utc DESC, x.id DESC LIMIT 1)
                FROM d WHERE v.id = d.conversation_id AND v.tenant_id = @tenant AND v.channel = 7)
            SELECT conversation_id FROM d
            """, new { tenant, messageId, userId }, cancellationToken: ct));
    }

    /// <summary>
    /// Sửa chữ của tin CỦA CHÍNH MÌNH (tin chưa thu hồi). Ghi mốc sửa để ai cũng thấy "đã sửa".
    /// Trả mã phòng; null = không sửa được.
    /// </summary>
    public async Task<long?> EditOwnMessageAsync(string tenant, long messageId, int userId, string body,
        CancellationToken ct = default)
    {
        // Như thu hồi: một câu, kèm dòng xem trước — chỉ đổi khi tin vừa sửa là tin MỚI NHẤT của phòng.
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition("""
            WITH e AS (
              UPDATE chat_messages t SET body = @body, edited_utc = now()
               WHERE t.id = @messageId AND t.tenant_id = @tenant AND t.channel = 7
                 AND t.sender_user_id = @userId AND t.deleted_utc IS NULL
                 AND EXISTS (SELECT 1 FROM chat_room_members mb
                              WHERE mb.conversation_id = t.conversation_id AND mb.tenant_id = @tenant
                                AND mb.user_id = @userId AND mb.left_utc IS NULL)
              RETURNING conversation_id),
            p AS (
              UPDATE chat_conversations v SET last_preview = left(@body, 200)
                FROM e WHERE v.id = e.conversation_id AND v.tenant_id = @tenant AND v.channel = 7
                 AND @messageId = (SELECT x.id FROM chat_messages x
                                    WHERE x.conversation_id = v.id AND x.deleted_utc IS NULL
                                    ORDER BY x.created_utc DESC, x.id DESC LIMIT 1))
            SELECT conversation_id FROM e
            """, new { tenant, messageId, userId, body }, cancellationToken: ct));
    }

    /// <summary>
    /// "Xoá phía tôi" — xoá MỀM, riêng người này: ẩn phòng khỏi danh sách của họ và ẩn tin cũ hơn mốc.
    /// Có tin mới thì phòng tự hiện lại (chỉ với tin mới). Đánh dấu đã đọc luôn để không còn số chưa
    /// đọc treo. false = không phải thành viên.
    /// </summary>
    public async Task<bool> HideRoomAsync(string tenant, long roomId, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            WITH h AS (
              UPDATE chat_room_members SET hidden_utc = now()
               WHERE conversation_id = @roomId AND tenant_id = @tenant AND user_id = @userId AND left_utc IS NULL
              RETURNING conversation_id),
            r AS (
              INSERT INTO chat_conversation_reads (tenant_id, conversation_id, user_id, last_read_at)
              SELECT @tenant, conversation_id, @userId, now() FROM h
              ON CONFLICT (tenant_id, conversation_id, user_id) DO UPDATE SET last_read_at = now())
            SELECT EXISTS (SELECT 1 FROM h)
            """, new { tenant, roomId, userId }, cancellationToken: ct));
    }

    /// <summary>
    /// Phòng có khoá này đã có chưa, và người hỏi có đang ở trong đó không — CHỈ ĐỌC. Bấm Chat trên
    /// một bản ghi chỉ hỏi câu này; phòng chỉ được tạo lúc gửi tin đầu tiên (bấm nhầm không để lại gì).
    /// Một câu, đi chỉ mục duy nhất <c>ux_conv_scope_surface</c>.
    /// </summary>
    /// <returns>Data: tham số tự do của phòng — CHỈ trả cho thành viên (người ngoài chỉ biết phòng có hay không).</returns>
    public async Task<(long? Id, bool Member, System.Text.Json.JsonElement? Data)> FindRoomByKeyAsync(string tenant,
        string roomKey, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<(long Id, bool Member, string? Data)>(new CommandDefinition("""
            SELECT v.id,
                   EXISTS (SELECT 1 FROM chat_room_members mb
                            WHERE mb.conversation_id = v.id AND mb.tenant_id = @tenant
                              AND mb.user_id = @userId AND mb.left_utc IS NULL) AS member,
                   v.entity_data::text AS data
              FROM chat_conversations v
             WHERE v.tenant_id = @tenant AND v.channel = 7 AND v.account_id = ''
               AND v.contact_external_id = @roomKey AND v.surface = 0 AND v.source_thread_id = ''
            """, new { tenant, roomKey, userId }, cancellationToken: ct));
        return r.Id == 0 ? (null, false, null) : (r.Id, r.Member, r.Member ? ParseData(r.Data) : null);
    }

    /// <summary>
    /// Thay tham số tự do của phòng (null = xoá) — chỉ thành viên, chỉ ghi khi KHÁC giá trị đang có (mở lại
    /// bản ghi với cùng tham số không sinh dòng chết). Trả false = không phải thành viên / phòng không có.
    /// </summary>
    public async Task<(bool Ok, bool Changed)> SetRoomDataAsync(string tenant, long roomId, int userId, string? dataJson,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<(bool Member, int Changed)>(new CommandDefinition("""
            WITH me AS (SELECT 1 FROM chat_room_members mb
                         WHERE mb.conversation_id = @roomId AND mb.tenant_id = @tenant
                           AND mb.user_id = @userId AND mb.left_utc IS NULL),
                 up AS (UPDATE chat_conversations SET entity_data = @dataJson::jsonb
                         WHERE id = @roomId AND tenant_id = @tenant AND channel = 7 AND EXISTS (SELECT 1 FROM me)
                           AND entity_data IS DISTINCT FROM @dataJson::jsonb
                        RETURNING 1)
            SELECT EXISTS (SELECT 1 FROM me) AS member, (SELECT count(*)::int FROM up) AS changed
            """, new { tenant, roomId, userId, dataJson }, cancellationToken: ct));
        return (r.Member, r.Changed > 0);
    }

    /// <summary>
    /// Đã là thành viên phòng có khoá này chưa — để đường mở phòng theo cơ hội chỉ hỏi quyền CRM ở
    /// LẦN ĐẦU vào phòng. Đã là thành viên thì mở lại / gửi tin không tốn lượt gọi CRM nào.
    /// </summary>
    public async Task<bool> IsMemberByKeyAsync(string tenant, string roomKey, int userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
              SELECT 1 FROM chat_conversations v
                JOIN chat_room_members mb ON mb.conversation_id = v.id AND mb.tenant_id = @tenant
               WHERE v.tenant_id = @tenant AND v.channel = 7 AND v.account_id = ''
                 AND v.contact_external_id = @roomKey AND v.surface = 0 AND v.source_thread_id = ''
                 AND mb.user_id = @userId AND mb.left_utc IS NULL)
            """, new { tenant, roomKey, userId }, cancellationToken: ct));
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

    private static InternalMessage ToMessage(MessageRow m, IReadOnlyList<InternalReaction>? rx = null) => new(m.Id, m.RoomId,
        m.SenderUserId, m.SenderUsername, m.DeletedUtc is null ? m.Body : null, m.CreatedUtc, m.DeletedUtc,
        m.DeletedUtc is null ? ReadAttachment(m.Attachment) : null, m.DeletedUtc is null ? m.EditedUtc : null,
        m.SenderKind == SystemSenderKind, m.DeletedUtc is null ? rx : null);

    private sealed class ReactionRow
    {
        public string ExternalMsgId { get; set; } = "";
        public string ActorExternalId { get; set; } = "";
        public string? Emoji { get; set; }
    }

    /// <summary>
    /// Cảm xúc của MỘT TRANG tin (≤ 100) — một câu, đi khoá chính của chat_reactions
    /// (tenant_id, channel, external_msg_id, …). Bỏ tin thu hồi / tin hệ thống (không hiện cảm xúc).
    /// </summary>
    private static async Task<Dictionary<long, IReadOnlyList<InternalReaction>>> ReactionsOfAsync(
        Npgsql.NpgsqlConnection c, string tenant, IReadOnlyList<MessageRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(m => m.DeletedUtc is null && m.SenderKind != SystemSenderKind)
            .Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (ids.Length == 0) return new();
        var list = await c.QueryAsync<ReactionRow>(new CommandDefinition("""
            SELECT external_msg_id, actor_external_id, emoji
              FROM chat_reactions
             WHERE tenant_id = @tenant AND channel = 7 AND external_msg_id = ANY(@ids) AND emoji IS NOT NULL
             ORDER BY created_utc, actor_external_id
            """, new { tenant, ids }, cancellationToken: ct));
        var kq = new Dictionary<long, IReadOnlyList<InternalReaction>>();
        foreach (var g in list.GroupBy(r => r.ExternalMsgId))
        {
            if (!long.TryParse(g.Key, out var mid)) continue;
            var rs = g.Select(r => (Emoji: r.Emoji!, UserId: int.TryParse(r.ActorExternalId, out var u) ? u : 0))
                .Where(r => r.UserId > 0);
            kq[mid] = InternalReactions.Group(rs);
        }
        return kq;
    }

    /// <summary>
    /// Thả / đổi / gỡ cảm xúc của MỘT người trên một tin nội bộ (emoji null = gỡ). Một câu: chỉ tin kênh 7
    /// còn hiện (chưa thu hồi, không phải dòng hệ thống) trong phòng người này đang ở. Thả lại đúng biểu
    /// tượng đang có thì không ghi gì. Trả mã phòng; null = không thả được (không phải thành viên / tin không
    /// có / đã thu hồi).
    /// </summary>
    public async Task<long?> SetReactionAsync(string tenant, long messageId, int userId, string? emoji,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var mid = messageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var uid = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sql = emoji is null
            ? """
              WITH m AS (
                SELECT t.conversation_id FROM chat_messages t
                 WHERE t.id = @messageId AND t.tenant_id = @tenant AND t.channel = 7
                   AND t.deleted_utc IS NULL AND t.sender_kind <> 3
                   AND EXISTS (SELECT 1 FROM chat_room_members mb
                                WHERE mb.conversation_id = t.conversation_id AND mb.tenant_id = @tenant
                                  AND mb.user_id = @userId AND mb.left_utc IS NULL)),
              d AS (
                DELETE FROM chat_reactions r USING m
                 WHERE r.tenant_id = @tenant AND r.channel = 7 AND r.external_msg_id = @mid AND r.actor_external_id = @uid
                RETURNING 1)
              SELECT conversation_id FROM m
              """
            : """
              WITH m AS (
                SELECT t.conversation_id FROM chat_messages t
                 WHERE t.id = @messageId AND t.tenant_id = @tenant AND t.channel = 7
                   AND t.deleted_utc IS NULL AND t.sender_kind <> 3
                   AND EXISTS (SELECT 1 FROM chat_room_members mb
                                WHERE mb.conversation_id = t.conversation_id AND mb.tenant_id = @tenant
                                  AND mb.user_id = @userId AND mb.left_utc IS NULL)),
              up AS (
                INSERT INTO chat_reactions (tenant_id, channel, external_msg_id, actor_external_id, emoji)
                SELECT @tenant, 7, @mid, @uid, @emoji FROM m
                ON CONFLICT (tenant_id, channel, external_msg_id, actor_external_id) DO UPDATE
                   SET emoji = EXCLUDED.emoji, created_utc = now()
                 WHERE chat_reactions.emoji IS DISTINCT FROM EXCLUDED.emoji
                RETURNING 1)
              SELECT conversation_id FROM m
              """;
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition(sql,
            new { tenant, messageId, userId, mid, uid, emoji }, cancellationToken: ct));
    }

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
