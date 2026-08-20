// Services/Chat/Inbox/ChatRepository.cs
using Dapper;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Kho dữ liệu hộp thư chat (PostgreSQL, Dapper).
///
/// <para><b>Mọi hàm kẹp <c>tenant_id</c>.</b> Không có ngoại lệ — hộp thư chứa tin nhắn thật của
/// khách, lọt tenant là lộ dữ liệu công ty khác.</para>
/// </summary>
public class ChatRepository
{
    private readonly ChatDb _db;
    private readonly ILogger<ChatRepository> _log;

    public ChatRepository(ChatDb db, ILogger<ChatRepository> log) { _db = db; _log = log; }

    public bool Configured => _db.Configured;

    // ── Liên hệ ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi/cập nhật danh tính khách theo kênh.
    ///
    /// <para>Tên hiển thị chỉ ghi đè khi có giá trị mới: webhook đôi khi không kèm tên, đè bừa sẽ
    /// xoá mất tên đã lấy được từ lần trước.</para>
    /// </summary>
    public async Task UpsertContactAsync(string tenant, ChatChannel kenh, string externalId,
        string? tenHienThi, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_contacts (tenant_id, channel, external_id, display_name)
            VALUES (@tenant, @kenh, @id, @ten)
            ON CONFLICT (tenant_id, channel, external_id) DO UPDATE
              SET display_name = COALESCE(NULLIF(EXCLUDED.display_name, ''), chat_contacts.display_name),
                  updated_utc  = now()
            """, new { tenant, kenh = (short)kenh, id = externalId, ten = tenHienThi });
    }

    // ── Hội thoại ───────────────────────────────────────────────────────────

    /// Tìm hội thoại của khách trên kênh, chưa có thì tạo.
    public async Task<ChatConversation> GetOrCreateConversationAsync(string tenant, ChatChannel kenh,
        string externalId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // ON CONFLICT DO UPDATE (không phải DO NOTHING) để câu lệnh LUÔN trả về dòng — DO NOTHING
        // thì lần chạy đồng thời thứ hai trả rỗng và phải SELECT thêm một vòng.
        return await c.QuerySingleAsync<ChatConversation>("""
            INSERT INTO chat_conversations (tenant_id, channel, contact_external_id)
            VALUES (@tenant, @kenh, @id)
            ON CONFLICT (tenant_id, channel, contact_external_id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id
            RETURNING *
            """, new { tenant, kenh = (short)kenh, id = externalId });
    }

    public async Task<ChatConversation?> GetConversationAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatConversation>(
            "SELECT * FROM chat_conversations WHERE id = @id AND tenant_id = @tenant", new { id, tenant });
    }

    /// <param name="chiCuaToi">Chỉ hội thoại giao cho người này, cộng hội thoại chưa giao ai. Dùng
    /// cho tài khoản không có quyền xem toàn công ty — kẹp ở SQL chứ không lọc phía client.</param>
    public async Task<List<ChatConversation>> ListConversationsAsync(string tenant, short? trangThai,
        string? chiCuaToi, string? timKiem, int limit = 60, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatConversation>("""
            SELECT v.*, ct.display_name
            FROM chat_conversations v
            LEFT JOIN chat_contacts ct
              ON ct.tenant_id = v.tenant_id AND ct.channel = v.channel AND ct.external_id = v.contact_external_id
            WHERE v.tenant_id = @tenant
              AND (@trangThai IS NULL OR v.status = @trangThai)
              AND (@chiCuaToi IS NULL OR v.assigned_username = @chiCuaToi OR v.assigned_username IS NULL)
              AND (@tim IS NULL OR ct.display_name ILIKE @tim OR v.last_preview ILIKE @tim
                   OR v.contact_external_id ILIKE @tim)
            ORDER BY v.last_activity_at DESC
            LIMIT @limit
            """, new { tenant, trangThai, chiCuaToi,
                       tim = string.IsNullOrWhiteSpace(timKiem) ? null : $"%{timKiem.Trim()}%",
                       limit = Math.Clamp(limit, 1, 200) })).ToList();
    }

    public async Task<Dictionary<short, int>> CountByStatusAsync(string tenant, string? chiCuaToi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(short status, int so)>("""
            SELECT status, COUNT(*)::int FROM chat_conversations
            WHERE tenant_id = @tenant AND (@chiCuaToi IS NULL OR assigned_username = @chiCuaToi OR assigned_username IS NULL)
            GROUP BY status
            """, new { tenant, chiCuaToi });
        return rows.ToDictionary(r => r.status, r => r.so);
    }

    public async Task AssignAsync(string tenant, long id, string? username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // Giao việc thì đẩy trạng thái sang "đang xử lý" — trừ khi đã đóng, vì gán người cho việc
        // đã đóng không có nghĩa mở lại nó.
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET assigned_username = @username,
                   status = CASE WHEN status = 2 THEN status ELSE 1 END
             WHERE id = @id AND tenant_id = @tenant
            """, new { id, tenant, username });
    }

    public async Task SetStatusAsync(string tenant, long id, ChatStatus tt, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET status = @tt, archived_at = CASE WHEN @tt = 2 THEN now() ELSE NULL END
             WHERE id = @id AND tenant_id = @tenant
            """, new { id, tenant, tt = (short)tt });
    }

    /// Cho bot câm tới thời điểm sau. phut=0 nghĩa là bỏ câm ngay.
    public async Task PauseBotAsync(string tenant, long id, int phut, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations SET bot_resume_at = CASE WHEN @phut <= 0 THEN NULL
                                                              ELSE now() + (@phut || ' minutes')::interval END
             WHERE id = @id AND tenant_id = @tenant
            """, new { id, tenant, phut });
    }

    public async Task MarkAgentReadAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(
            "UPDATE chat_conversations SET agent_last_read_at = now() WHERE id = @id AND tenant_id = @tenant",
            new { id, tenant });
    }

    // ── Tin nhắn ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi một tin. Trả <c>null</c> khi <paramref name="externalMsgId"/> đã có — tức webhook gửi
    /// lại, KHÔNG phải tin mới. Chống trùng dựa vào chỉ mục duy nhất ở CSDL chứ không phải kiểm
    /// trước rồi ghi: hai lần gửi song song thì cách kiểm-rồi-ghi vẫn lọt.
    /// </summary>
    public async Task<long?> AppendMessageAsync(string tenant, long conversationId, ChatChannel kenh,
        ChatDirection chieu, ChatSender nguoiGui, string? username, ChatKind loai, string? noiDung,
        string? attachmentJson, string? externalMsgId, ChatState trangThai, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long?>("""
            INSERT INTO chat_messages
              (tenant_id, conversation_id, channel, direction, sender_kind, sender_username,
               kind, body, attachment, external_msg_id, state)
            VALUES (@tenant, @conv, @kenh, @chieu, @nguoiGui, @username,
                    @loai, @noiDung, @att::jsonb, @ext, @tt)
            ON CONFLICT (tenant_id, channel, external_msg_id) WHERE external_msg_id IS NOT NULL
              DO NOTHING
            RETURNING id
            """, new { tenant, conv = conversationId, kenh = (short)kenh, chieu = (short)chieu,
                       nguoiGui = (short)nguoiGui, username, loai = (short)loai, noiDung,
                       att = attachmentJson, ext = externalMsgId, tt = (short)trangThai });

        if (id is null) _log.LogDebug("[chat] bỏ tin trùng ext={Ext} conv={Conv}", externalMsgId, conversationId);
        return id;
    }

    /// Cập nhật mốc hoạt động + dòng xem trước. Tin của khách thì cập nhật thêm mốc tính cửa sổ gửi.
    public async Task TouchConversationAsync(string tenant, long id, string? xemTruoc, bool laTinCuaKhach,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET last_activity_at   = now(),
                   last_preview       = COALESCE(@xemTruoc, last_preview),
                   contact_replied_at = CASE WHEN @khach THEN now() ELSE contact_replied_at END,
                   agent_replied_at   = CASE WHEN @khach THEN agent_replied_at ELSE now() END
             WHERE id = @id AND tenant_id = @tenant
            """, new { id, tenant, xemTruoc, khach = laTinCuaKhach });
    }

    public async Task<List<ChatMessage>> ListMessagesAsync(string tenant, long conversationId,
        int limit = 100, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // Lấy N tin MỚI NHẤT rồi đảo lại cho đúng thứ tự đọc — lấy N tin đầu thì mở hội thoại dài
        // ra là thấy chuyện từ hôm kia.
        var rows = await c.QueryAsync<ChatMessage>("""
            SELECT * FROM (
              SELECT * FROM chat_messages
               WHERE conversation_id = @conv AND tenant_id = @tenant
               ORDER BY created_utc DESC LIMIT @limit
            ) t ORDER BY created_utc
            """, new { conv = conversationId, tenant, limit = Math.Clamp(limit, 1, 300) });
        return rows.ToList();
    }

    /// Các tin của khách chưa được bot xử lý, dùng để gộp cụm.
    public async Task<List<ChatMessage>> ListPendingInboundAsync(string tenant, long conversationId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatMessage>("""
            SELECT * FROM chat_messages
             WHERE conversation_id = @conv AND tenant_id = @tenant
               AND direction = 0 AND processed_utc IS NULL
             ORDER BY created_utc
            """, new { conv = conversationId, tenant })).ToList();
    }

    public async Task MarkProcessedAsync(string tenant, IEnumerable<long> ids, CancellationToken ct = default)
    {
        var arr = ids.ToArray();
        if (arr.Length == 0) return;
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(
            "UPDATE chat_messages SET processed_utc = now() WHERE tenant_id = @tenant AND id = ANY(@ids)",
            new { tenant, ids = arr });
    }

    public async Task SetMessageStateAsync(string tenant, long messageId, ChatState tt, string? loi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_messages SET state = @tt, error_message = @loi, processed_utc = now()
             WHERE id = @id AND tenant_id = @tenant
            """, new { id = messageId, tenant, tt = (short)tt, loi });
    }

    // ── Hàng đợi gửi ────────────────────────────────────────────────────────

    public async Task EnqueueOutboxAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_outbox (tenant_id, conversation_id, message_id)
            VALUES (@tenant, @conv, @msg)
            """, new { tenant, conv = conversationId, msg = messageId });
    }

    public record OutboxRow(long Id, string TenantId, long ConversationId, long MessageId, int RetryCount);

    public async Task<List<OutboxRow>> ClaimOutboxAsync(int soLuong, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // FOR UPDATE SKIP LOCKED: nhiều tiến trình cùng rút mà không giẫm lên nhau, không cần khoá
        // toàn bảng. Đây là cách làm hàng đợi chuẩn của PostgreSQL.
        return (await c.QueryAsync<OutboxRow>("""
            UPDATE chat_outbox SET status = 3
             WHERE id IN (
               SELECT id FROM chat_outbox WHERE status = 0
                ORDER BY created_utc LIMIT @n FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, conversation_id, message_id, retry_count
            """, new { n = Math.Clamp(soLuong, 1, 50) })).ToList();
    }

    /// <param name="thuLai">true = trả về hàng đợi để thử lần sau (lỗi tạm thời).</param>
    public async Task FinishOutboxAsync(long id, bool thanhCong, bool thuLai, string? loi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_outbox
               SET status = CASE WHEN @ok THEN 1 WHEN @thuLai THEN 0 ELSE 2 END,
                   retry_count = retry_count + CASE WHEN @thuLai THEN 1 ELSE 0 END,
                   error_message = @loi,
                   processed_utc = CASE WHEN @thuLai THEN NULL ELSE now() END
             WHERE id = @id
            """, new { id, ok = thanhCong, thuLai, loi });
    }

    /// Xoá tin cũ hơn N ngày. Chat sinh nhiều hơn Bảng tin nhiều lần nên phải dọn định kỳ.
    public async Task<int> PruneAsync(int giuNgay, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM chat_conversations WHERE last_activity_at < now() - (@n || ' days')::interval",
            new { n = giuNgay });
    }
}
