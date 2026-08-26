// Services/Chat/Inbox/ChatRepository.cs
using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

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
        string? tenHienThi, string? anhDaiDien = null, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_contacts (tenant_id, channel, external_id, display_name, avatar_url)
            VALUES (@tenant, @kenh, @id, @ten, @anh)
            ON CONFLICT (tenant_id, channel, external_id) DO UPDATE
              SET display_name = COALESCE(NULLIF(EXCLUDED.display_name, ''), chat_contacts.display_name),
                  avatar_url   = COALESCE(NULLIF(EXCLUDED.avatar_url, ''), chat_contacts.avatar_url),
                  updated_utc  = now()
            """, new { tenant, kenh = (short)kenh, id = externalId, ten = tenHienThi, anh = anhDaiDien });
    }

    /// <summary>
    /// Có cần đi hỏi nhà cung cấp về khách này không — <b>thiếu tên hoặc thiếu ảnh</b> thì cần.
    ///
    /// <para>Hỏi mỗi tin là mỗi lượt khách nhắn lại tốn một lượt gọi ra nhà cung cấp, mà tên thì
    /// gần như không đổi. Nên chỉ hỏi khi còn thiếu, và có được rồi thì thôi.</para>
    ///
    /// <para>⚠️ <b>Hạn chế đã biết:</b> Meta ký hạn vào URL ảnh đại diện nên nó sẽ hết hạn sau
    /// một thời gian, lúc đó hộp thư hiện ảnh vỡ và chỗ này KHÔNG tự lấy lại (đã có ảnh nên coi
    /// như đủ). Chữa đúng thì cần một cột riêng ghi mốc lần hỏi cuối — chưa làm, vì thêm cột là
    /// đụng lược đồ, và ảnh vỡ thì xấu chứ không sai dữ liệu.</para>
    /// </summary>
    public async Task<bool> CanLayHoSoAsync(string tenant, ChatChannel kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>("""
            SELECT NOT EXISTS (
              SELECT 1 FROM chat_contacts
              WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
                AND display_name IS NOT NULL AND display_name <> ''
                AND avatar_url IS NOT NULL AND avatar_url <> ''
            )
            """, new { tenant, kenh = (short)kenh, id = externalId });
    }

    /// <summary>
    /// Ghi nguồn khách đến cho hội thoại — <b>chỉ khi chưa có</b>.
    ///
    /// <para>COALESCE chứ không đè: khách quay lại qua một quảng cáo khác thì nguồn ĐẦU TIÊN mới
    /// là cái đã kéo họ tới. Đè lên là hỏng số liệu quy công quảng cáo, mà hỏng âm thầm — không
    /// ai nhìn ra một con số quy sai.</para>
    /// </summary>
    public async Task GhiNguonAsync(string tenant, long hoiThoaiId, ChatReferral r,
        CancellationToken ct = default)
    {
        if (r.Nguon is null && r.Ref is null && r.AdId is null) return;
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET referral_source = COALESCE(referral_source, @nguon),
                   referral_ref    = COALESCE(referral_ref,    @tref),
                   referral_ad_id  = COALESCE(referral_ad_id,  @ad)
             WHERE tenant_id = @tenant AND id = @id
            """, new { tenant, id = hoiThoaiId, nguon = r.Nguon, tref = r.Ref, ad = r.AdId });
    }
    // ── Cảm xúc ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi hoặc GỠ một cảm xúc. Một người chỉ giữ MỘT cảm xúc trên một tin — thả cái mới là đè
    /// cái cũ, đúng như hành vi của Messenger.
    /// </summary>
    public async Task ThaCamXucAsync(string tenant, ChatChannel kenh, ChatReaction cx,
        string aiTha, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (cx.Bo)
        {
            await c.ExecuteAsync("""
                DELETE FROM chat_reactions
                WHERE tenant_id = @tenant AND channel = @kenh
                  AND external_msg_id = @mid AND actor_external_id = @ai
                """, new { tenant, kenh = (short)kenh, mid = cx.ExternalMsgId, ai = aiTha });
            return;
        }

        await c.ExecuteAsync("""
            INSERT INTO chat_reactions
              (tenant_id, channel, external_msg_id, actor_external_id, emoji, reaction_name)
            VALUES (@tenant, @kenh, @mid, @ai, @emoji, @ten)
            ON CONFLICT (tenant_id, channel, external_msg_id, actor_external_id) DO UPDATE
              SET emoji = EXCLUDED.emoji, reaction_name = EXCLUDED.reaction_name,
                  created_utc = now()
            """, new { tenant, kenh = (short)kenh, mid = cx.ExternalMsgId, ai = aiTha,
                       emoji = cx.BieuTuong, ten = cx.Ten });
    }

    /// <summary>Cảm xúc của các tin trong một hội thoại, để đính kèm lúc liệt kê tin.</summary>
    public async Task<IReadOnlyList<ChatReactionRow>> CamXucTheoHoiThoaiAsync(string tenant,
        long hoiThoaiId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatReactionRow>("""
            SELECT r.external_msg_id, r.actor_external_id, r.emoji, r.reaction_name
            FROM chat_reactions r
            JOIN chat_messages m
              ON m.tenant_id = r.tenant_id AND m.channel = r.channel
             AND m.external_msg_id = r.external_msg_id
            WHERE r.tenant_id = @tenant AND m.conversation_id = @hoiThoai
            """, new { tenant, hoiThoai = hoiThoaiId })).ToList();
    }
    // ── Hội thoại ───────────────────────────────────────────────────────────

    /// <summary>Tìm hội thoại của khách trên kênh, chưa có thì tạo.</summary>
    /// <param name="accountId">Tài khoản (Trang/OA/bot) vừa nhận tin này. GHI MỘT LẦN lúc tạo —
    /// những lần sau KHÔNG ghi đè, kể cả khi tới từ tài khoản khác: một cuộc trò chuyện thuộc về
    /// đúng tài khoản khách đã nhắn LẦN ĐẦU, đổi ngầm giữa chừng sẽ làm nhân viên trả lời sai danh
    /// nghĩa mà không hay.</param>
    public async Task<ChatConversation> GetOrCreateConversationAsync(string tenant, ChatChannel kenh,
        string externalId, string accountId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // ON CONFLICT DO UPDATE (không phải DO NOTHING) để câu lệnh LUÔN trả về dòng — DO NOTHING
        // thì lần chạy đồng thời thứ hai trả rỗng và phải SELECT thêm một vòng.
        return await c.QuerySingleAsync<ChatConversation>("""
            INSERT INTO chat_conversations (tenant_id, channel, contact_external_id, account_id)
            VALUES (@tenant, @kenh, @id, @accountId)
            ON CONFLICT (tenant_id, channel, account_id, contact_external_id)
              DO UPDATE SET tenant_id = EXCLUDED.tenant_id
            RETURNING *
            """, new { tenant, kenh = (short)kenh, id = externalId, accountId });
    }

    public async Task<ChatConversation?> GetConversationAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatConversation>(
            "SELECT * FROM chat_conversations WHERE id = @id AND tenant_id = @tenant", new { id, tenant });
    }

    /// <summary>Id tin đoán được (số tăng dần) — proxy tệp Telegram phải tự kiểm chủ trước khi
    /// đổi file_id thành đường tải thật, không tin vào việc id khó đoán.</summary>
    public async Task<bool> MessageBelongsToTenantAsync(string tenant, long messageId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM chat_messages WHERE id = @messageId AND tenant_id = @tenant)",
            new { messageId, tenant });
    }

    /// <param name="chiCuaToi">Chỉ hội thoại giao cho người này, cộng hội thoại chưa giao ai. Dùng
    /// cho tài khoản không có quyền xem toàn công ty — kẹp ở SQL chứ không lọc phía client.</param>
    /// <param name="kenh">Lọc theo kênh (dải kênh bên trái giao diện). Null = mọi kênh.</param>
    /// <param name="giaoCho">Lọc theo người phụ trách do NGƯỜI DÙNG chọn ("Của tôi"). Khác hẳn
    /// <paramref name="chiCuaToi"/> vốn là kẹp QUYỀN: cái này lọc đúng một người, cái kia còn cho
    /// thấy phần chưa ai nhận. Gộp hai thứ lại thì "Của tôi" sẽ hiện cả việc của người khác.</param>
    /// <param name="chiChuaDoc">Chỉ hội thoại khách nhắn sau lần mình mở gần nhất.</param>
    /// <param name="nguoiDung">Người đang xem — mốc "đã đọc" lấy theo người này, không phải theo
    /// cả công ty. Null thì lùi về mốc chung cũ.</param>
    public async Task<List<ChatConversation>> ListConversationsAsync(string tenant, short? trangThai,
        string? chiCuaToi, string? timKiem, short? kenh = null, string? giaoCho = null,
        bool chiChuaDoc = false, ConvCursor? sau = null, int limit = 60, string? nguoiDung = null,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatConversation>("""
            SELECT v.*, ct.display_name, ct.avatar_url, r.last_read_at AS my_last_read_at
            FROM chat_conversations v
            LEFT JOIN chat_contacts ct
              ON ct.tenant_id = v.tenant_id AND ct.channel = v.channel AND ct.external_id = v.contact_external_id
            LEFT JOIN chat_conversation_reads r
              ON r.tenant_id = v.tenant_id AND r.conversation_id = v.id AND r.username = @nguoiDung
            WHERE v.tenant_id = @tenant
              AND (@trangThai IS NULL OR v.status = @trangThai)
              AND (@chiCuaToi IS NULL OR v.assigned_username = @chiCuaToi OR v.assigned_username IS NULL)
              AND (@kenh IS NULL OR v.channel = @kenh)
              AND (@giaoCho IS NULL OR v.assigned_username = @giaoCho)
              -- Mốc RIÊNG của người đang xem, lùi về mốc chung cũ khi họ chưa mở lần nào.
              AND (NOT @chuaDoc OR (v.contact_replied_at IS NOT NULL
                   AND (COALESCE(r.last_read_at, v.agent_last_read_at) IS NULL
                        OR v.contact_replied_at > COALESCE(r.last_read_at, v.agent_last_read_at))))
              AND (@tim IS NULL OR ct.display_name ILIKE @tim OR v.last_preview ILIKE @tim
                   OR v.contact_external_id ILIKE @tim)
              AND (@sauLuc::timestamptz IS NULL
                   OR (v.last_activity_at, v.id) < (@sauLuc::timestamptz, @sauId::bigint))
            ORDER BY v.last_activity_at DESC, v.id DESC
            LIMIT @limit
            """, new { tenant, trangThai, chiCuaToi, kenh, giaoCho, chuaDoc = chiChuaDoc, nguoiDung,
                       tim = string.IsNullOrWhiteSpace(timKiem) ? null : $"%{timKiem.Trim()}%",
                       sauLuc = sau?.LastActivityAt, sauId = sau?.Id,
                       limit = Math.Clamp(limit, 1, 200) })).ToList();
    }

    /// <summary>
    /// Nối (hoặc gỡ nối, khi <paramref name="crmCustomerId"/> là <c>null</c>) khách chat với
    /// khách trong CRM. Trả số dòng đổi được — 0 nghĩa là không có hồ sơ khách nào khớp.
    ///
    /// <para><b>Nối TAY, không đoán tự động.</b> Ghép theo tên sai thường xuyên (trùng tên là
    /// chuyện bình thường ở khách du lịch); ghép theo số điện thoại thì Zalo/Messenger không cho
    /// biết số trừ khi khách tự nhắn. Nối tay đúng 100% và làm được ngay.</para>
    /// </summary>
    public async Task<int> NoiCrmAsync(string tenant, short kenh, string externalId,
        int? crmCustomerId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_contacts
               SET crm_customer_id = @crmCustomerId, updated_utc = now()
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @externalId
            """, new { tenant, kenh, externalId, crmCustomerId });
    }

    // ── Nhãn và ghi chú của khách ───────────────────────────────────────────

    /// <summary>
    /// Nhãn của một khách. Theo KHÁCH chứ không theo hội thoại — khách nhắn lại sau ba tháng vẫn
    /// còn nhãn cũ; gắn theo hội thoại thì mỗi lần mở hội thoại mới là mất hết.
    /// </summary>
    public async Task<List<string>> ListTagsAsync(string tenant, short kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<string>("""
            SELECT tag FROM chat_contact_tags
            WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @externalId
            ORDER BY tag
            """, new { tenant, kenh, externalId })).ToList();
    }

    /// <summary><paramref name="tag"/> phải ĐÃ chuẩn hoá (xem <c>ChatRules.ChuanHoaSlug</c>).</summary>
    public async Task AddTagAsync(string tenant, short kenh, string externalId, string tag,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_contact_tags (tenant_id, channel, external_id, tag)
            VALUES (@tenant, @kenh, @externalId, @tag)
            ON CONFLICT (tenant_id, channel, external_id, tag) DO NOTHING
            """, new { tenant, kenh, externalId, tag });
    }

    public async Task<int> RemoveTagAsync(string tenant, short kenh, string externalId, string tag,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            DELETE FROM chat_contact_tags
            WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @externalId AND tag = @tag
            """, new { tenant, kenh, externalId, tag });
    }

    public async Task<List<ChatNote>> ListNotesAsync(string tenant, short kenh, string externalId,
        int limit = 50, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatNote>("""
            SELECT id, username, noi_dung, created_utc FROM chat_contact_notes
            WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @externalId
            ORDER BY created_utc DESC, id DESC
            LIMIT @limit
            """, new { tenant, kenh, externalId, limit = Math.Clamp(limit, 1, 200) })).ToList();
    }

    public async Task<long> AddNoteAsync(string tenant, short kenh, string externalId,
        string username, string noiDung, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO chat_contact_notes (tenant_id, channel, external_id, username, noi_dung)
            VALUES (@tenant, @kenh, @externalId, @username, @noiDung)
            RETURNING id
            """, new { tenant, kenh, externalId, username, noiDung });
    }

    /// <summary>Xoá ghi chú. Kẹp tenant để id đoán được cũng không xoá được của công ty khác.</summary>
    public async Task<int> RemoveNoteAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM chat_contact_notes WHERE id = @id AND tenant_id = @tenant",
            new { id, tenant });
    }

    /// <summary>Hồ sơ khách của một hội thoại. Panel bên phải đọc cái này.</summary>
    public async Task<ChatContact?> GetContactAsync(string tenant, short kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatContact>("""
            SELECT * FROM chat_contacts
            WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @externalId
            """, new { tenant, kenh, externalId });
    }

    /// <summary>
    /// Bộ đếm cho giao diện: theo trạng thái, theo kênh, và số chưa đọc.
    ///
    /// <para><b>MỘT truy vấn cho cả ba.</b> Giao diện hỏi lại 4 giây một lần, nên mỗi bộ đếm một
    /// truy vấn là nhân ba số lần đụng CSDL cho cùng một bảng, cùng một điều kiện.</para>
    /// </summary>
    public async Task<ChatInboxCounts> CountAsync(string tenant, string? chiCuaToi,
        string? nguoiDung = null, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<DemDong>("""
            SELECT v.status, v.channel, COUNT(*)::int AS so,
                   COUNT(*) FILTER (WHERE v.contact_replied_at IS NOT NULL
                        AND (COALESCE(r.last_read_at, v.agent_last_read_at) IS NULL
                             OR v.contact_replied_at > COALESCE(r.last_read_at, v.agent_last_read_at)))::int
                        AS chua_doc
            FROM chat_conversations v
            LEFT JOIN chat_conversation_reads r
              ON r.tenant_id = v.tenant_id AND r.conversation_id = v.id AND r.username = @nguoiDung
            WHERE v.tenant_id = @tenant
              AND (@chiCuaToi IS NULL OR v.assigned_username = @chiCuaToi OR v.assigned_username IS NULL)
            GROUP BY v.status, v.channel
            """, new { tenant, chiCuaToi, nguoiDung })).ToList();

        var theoTrangThai = new Dictionary<short, int>();
        var theoKenh = new Dictionary<short, int>();
        foreach (var r in rows)
        {
            theoTrangThai[r.Status] = theoTrangThai.GetValueOrDefault(r.Status) + r.So;
            theoKenh[r.Channel] = theoKenh.GetValueOrDefault(r.Channel) + r.So;
        }
        return new ChatInboxCounts(theoTrangThai, theoKenh, rows.Sum(r => r.ChuaDoc), rows.Sum(r => r.So));
    }

    private class DemDong
    {
        public short Status { get; set; }
        public short Channel { get; set; }
        public int So { get; set; }
        public int ChuaDoc { get; set; }
    }

    /// <summary>
    /// Nhận việc <b>NGUYÊN TỬ</b>. Trả số dòng đổi được: <c>0</c> nghĩa là <b>người khác đã nhận
    /// trước</b>, chỗ gọi phải trả 409 chứ không im lặng báo thành công.
    ///
    /// <para>Điều kiện nằm TRONG chính câu <c>UPDATE</c>, không phải đọc-rồi-ghi trong C#: giữa
    /// lần đọc và lần ghi có một khe, hai nhân viên bấm cách nhau 100ms là cả hai cùng lọt. Khi đó
    /// cả hai đều thấy "của tôi" và cùng trả lời một khách — khách nhận hai câu trả lời khác nhau
    /// từ một công ty.</para>
    ///
    /// <para>Nhận lại việc mình <b>đang giữ</b> vẫn tính là thành công: giao diện có thể gửi lại
    /// (bấm hai lần, mạng chập chờn), báo 409 cho chính người đang giữ là vô nghĩa.</para>
    /// </summary>
    public async Task<int> NhanViecAsync(string tenant, long id, string username,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET assigned_username = @username,
                   status = CASE WHEN status = 2 THEN status ELSE 1 END
             WHERE id = @id AND tenant_id = @tenant
               AND (assigned_username IS NULL OR assigned_username = @username)
            """, new { id, tenant, username });
    }

    // ── Nhật ký thao tác ────────────────────────────────────────────────────

    /// <summary>
    /// Ghi một dòng nhật ký.
    ///
    /// <para><b>Không bao giờ ném.</b> Nhật ký hỏng không được làm hỏng thao tác chính: nhân viên
    /// bấm đóng hội thoại mà nhận lỗi chỉ vì bảng nhật ký có vấn đề là đổi một sự cố ghi chép
    /// thành một sự cố vận hành.</para>
    ///
    /// <para><paramref name="chiTiet"/> là JSON và <b>KHÔNG được chứa nội dung tin</b> — tin đã
    /// nằm ở <c>chat_messages</c>, chép lại là nhân đôi dữ liệu khách và nhân đôi chỗ phải xoá
    /// khi khách yêu cầu xoá dữ liệu.</para>
    /// </summary>
    public async Task GhiNhatKyAsync(string tenant, long? hoiThoaiId, string username,
        string hanhDong, string? chiTiet = null, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync("""
                INSERT INTO chat_audit (tenant_id, conversation_id, username, hanh_dong, chi_tiet)
                VALUES (@tenant, @hoiThoaiId, @username, @hanhDong, @chiTiet::jsonb)
                """, new { tenant, hoiThoaiId, username, hanhDong, chiTiet });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[chat/nhật ký] không ghi được {HD} hội thoại {H}", hanhDong, hoiThoaiId);
        }
    }

    /// <summary>Nhật ký của một hội thoại, mới nhất trước.</summary>
    public async Task<List<ChatAuditRow>> ListAuditAsync(string tenant, long hoiThoaiId,
        int limit = 50, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatAuditRow>("""
            SELECT id, conversation_id, username, hanh_dong, chi_tiet, created_utc
            FROM chat_audit
            WHERE tenant_id = @tenant AND conversation_id = @hoiThoaiId
            ORDER BY created_utc DESC, id DESC
            LIMIT @limit
            """, new { tenant, hoiThoaiId, limit = Math.Clamp(limit, 1, 200) })).ToList();
    }

    /// <summary>Ai đang giữ hội thoại này. Dùng để nói tên trong lỗi 409, không đoán mò.</summary>
    public async Task<string?> AiDangGiuAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT assigned_username FROM chat_conversations WHERE id = @id AND tenant_id = @tenant",
            new { id, tenant });
    }

    /// <summary>
    /// Giao/gỡ giao KHÔNG kiểm ai đang giữ — dùng cho <b>nhả việc</b> và <b>chuyển việc</b>, là
    /// hai thao tác cố ý đè lên người đang giữ. Nhận việc thì dùng <see cref="NhanViecAsync"/>.
    /// </summary>
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

    /// <summary>
    /// Đánh dấu người này đã đọc tới giờ.
    ///
    /// <para><b>KHÔNG đụng <c>chat_conversations.agent_last_read_at</c> nữa.</b> Ghi vào cột chung
    /// đó nghĩa là A mở hội thoại thì B cũng mất dấu chưa đọc — đúng cái lỗi bảng này sinh ra để
    /// sửa. Cột cũ vẫn nằm đó làm mốc ban đầu cho người chưa có dòng nào.</para>
    /// </summary>
    public async Task MarkReadAsync(string tenant, long id, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_conversation_reads (tenant_id, conversation_id, username, last_read_at)
            VALUES (@tenant, @id, @username, now())
            ON CONFLICT (tenant_id, conversation_id, username)
            DO UPDATE SET last_read_at = now()
            """, new { id, tenant, username });
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

    /// <summary>
    /// Ghi mã tin của nền tảng cho tin MÌNH GỬI, sau khi gửi thành công.
    ///
    /// <para>Đây là thứ duy nhất đối chiếu được khi nền tảng báo lại "đã nhận"/"đã xem".
    /// Không lưu thì mọi báo lại đều không biết là của tin nào.</para>
    ///
    /// <para><b>Lệnh RIÊNG, cố ý không gộp vào <see cref="SetMessageStateAsync"/></b>: trạng thái
    /// đổi nhiều lần trong đời một tin (gửi → nhận → xem), còn mã nền tảng chỉ ghi đúng một lần.
    /// Gộp lại thì lần cập nhật trạng thái nào quên truyền mã sẽ xoá mất mã bằng <c>null</c>.</para>
    /// </summary>
    public async Task SetExternalMsgIdAsync(string tenant, long messageId, string? maNenTang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(maNenTang)) return;   // kênh không trả mã — không có gì để ghi
        await using var c = await _db.OpenAsync(ct);
        // external_msg_id IS NULL để không đè lên mã đã ghi — gửi lại sau lỗi tạm thời có thể
        // chạy hàm này hai lần.
        await c.ExecuteAsync("""
            UPDATE chat_messages SET external_msg_id = @ma
             WHERE id = @id AND tenant_id = @tenant AND external_msg_id IS NULL
            """, new { id = messageId, tenant, ma = maNenTang });
    }

    /// <summary>
    /// Nền tảng báo mọi tin gửi trước <paramref name="denLuc"/> đã đạt <paramref name="moi"/>.
    /// Trả về số dòng thật sự đổi.
    /// </summary>
    /// <remarks>
    /// <para><b>Chỉ tin MÌNH GỬI</b> (<c>direction = 1</c>): "khách đã xem" nói về tin của mình.
    /// Quên kẹp thì tin của chính khách cũng bị đánh dấu — vô nghĩa, và làm hỏng bộ đếm chưa đọc.</para>
    /// <para><b>Chỉ tiến, không lùi</b> (<c>state &lt; @moi</c>): nền tảng không bảo đảm thứ tự.
    /// Luật đầy đủ ở <see cref="ChatRules.KhongLui"/>; ở đây chặn ngay trong SQL vì cập nhật hàng
    /// loạt không đọc từng dòng ra được.</para>
    /// <para><b>Bỏ qua tin hỏng</b> (<c>state &lt;&gt; 4</c>): tin gửi hỏng thì không thể được xem.</para>
    /// <para><b>Bỏ qua tin còn trong hàng đợi</b> (<c>state &gt; 0</c>): mốc quét theo
    /// <c>created_utc</c>, nên tin nhân viên vừa bấm gửi — còn chưa rời khỏi hệ thống — vẫn lọt vào
    /// khoảng mốc nước và bị đánh dấu "đã xem". Nhân viên sẽ thấy khách đã xem một tin khách chưa hề
    /// nhận, rồi worker gửi xong lại đặt về "đã gửi" nên dấu tích còn chạy ngược nữa.</para>
    /// </remarks>
    public async Task<int> MarkStateWatermarkAsync(string tenant, long conversationId, ChatState moi,
        DateTime denLuc, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages
               SET state = @moi
             WHERE tenant_id = @tenant AND conversation_id = @conv
               AND direction = 1
               AND created_utc <= @denLuc
               AND state > 0 AND state < @moi AND state <> 4
            """, new { tenant, conv = conversationId, moi = (short)moi, denLuc });
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

    // ── Hàng đợi sự kiện VÀO ─────────────────────────────────────────────────

    /// <summary>Ghi sự kiện webhook xuống CSDL. Trả <c>null</c> khi trùng (kênh gửi lại).</summary>
    public async Task<long?> EnqueueInboundAsync(string tenant, ChatChannel kenh, string accountId,
        string? providerEventId, string rawBody, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long?>("""
            INSERT INTO chat_inbound_events (tenant_id, channel, account_id, provider_event_id, raw_body)
            VALUES (@tenant, @kenh, @accountId, @ext, @raw)
            ON CONFLICT (tenant_id, channel, provider_event_id) WHERE provider_event_id IS NOT NULL
              DO NOTHING
            RETURNING id
            """, new { tenant, kenh = (short)kenh, accountId, ext = providerEventId, raw = rawBody });
    }

    public record InboundRow(long Id, string TenantId, short Channel, string AccountId,
        string RawBody, int RetryCount);

    public async Task<List<InboundRow>> ClaimInboundAsync(int soLuong, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<InboundRow>("""
            UPDATE chat_inbound_events SET status = 3
             WHERE id IN (
               SELECT id FROM chat_inbound_events WHERE status = 0
                ORDER BY created_utc LIMIT @n FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, channel, account_id, raw_body, retry_count
            """, new { n = Math.Clamp(soLuong, 1, 50) })).ToList();
    }

    /// <param name="thuLai">true = trả về hàng đợi để thử lần sau (lỗi tạm thời).</param>
    public async Task FinishInboundAsync(long id, bool thanhCong, bool thuLai, string? loi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_inbound_events
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
