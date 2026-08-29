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
                  -- Ảnh ĐỔI sang url khác thì xoá cờ về 0: những lần hỏng trước là của url cũ.
                  -- Không xoá thì khách từng có một ảnh chết sẽ mang số đếm đó mãi, và lần đổi
                  -- ảnh sau bị bỏ qua ngay từ lượt đầu mà không ai hiểu tại sao.
                  avatar_state = CASE
                      WHEN NULLIF(EXCLUDED.avatar_url, '') IS NOT NULL
                       AND EXCLUDED.avatar_url IS DISTINCT FROM chat_contacts.avatar_url
                      THEN 0 ELSE chat_contacts.avatar_state END,
                  updated_utc  = now()
            """, new { tenant, kenh = (short)kenh, id = externalId, ten = tenHienThi, anh = anhDaiDien });
    }

    /// <summary>
    /// Có cần đi hỏi nhà cung cấp về khách này không — <b>thiếu tên hoặc thiếu ảnh</b> thì cần.
    ///
    /// <para>Hỏi mỗi tin là mỗi lượt khách nhắn lại tốn một lượt gọi ra nhà cung cấp, mà tên thì
    /// gần như không đổi. Nên chỉ hỏi khi còn thiếu, và có được rồi thì thôi.</para>
    ///
    /// <para>Trước 28/08/2026 chỗ này có một lỗ: Meta ký hạn vào URL ảnh đại diện, mà đã có ảnh
    /// thì không bao giờ hỏi lại — nên đến hạn là hộp thư hiện một dãy ảnh vỡ vĩnh viễn. Nay ảnh
    /// đại diện được tải luôn về kho của mình lúc lấy hồ sơ, url lưu lại là url của mình, không
    /// còn hạn nào. Xem <c>ChatInboundService.MirrorAvatarAsync</c>.</para>
    ///
    /// <para>⚠️ <b>Còn lại một hạn chế nhỏ:</b> khách ĐỔI ảnh đại diện thì mình vẫn hiện ảnh cũ,
    /// vì có ảnh rồi là thôi không hỏi nữa. Chữa thì cần một cột ghi mốc lần hỏi cuối để hỏi lại
    /// định kỳ — chưa làm: ảnh cũ vẫn là ảnh của đúng người đó, khác hẳn ảnh vỡ.</para>
    /// </summary>
    public async Task<bool> NeedsContactProfileAsync(string tenant, ChatChannel kenh, string externalId,
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

    /// <summary>Một khách còn giữ ảnh đại diện trỏ thẳng ra máy chủ của kênh, vừa nhận về để soi.</summary>
    /// <param name="Tries">Đã thử bấy nhiêu lần TRƯỚC lượt này — xem <see cref="MediaToMirror"/>.</param>
    public record AvatarToMirror(string TenantId, short Channel, string ExternalId, string AvatarUrl,
        short Tries);

    /// <summary>
    /// <b>NHẬN</b> một mẻ khách còn giữ ảnh đại diện của NHÀ CUNG CẤP — thứ sẽ hết hạn và thành
    /// ảnh vỡ ở mọi dòng trong hộp thư.
    ///
    /// <para><b>Nhận ra bằng đoạn đầu url</b> (<c>khoCuaMinh</c>, xem
    /// <c>IChatFileStorage.PublicBase</c>): cột này là <c>text</c> trần, không có chỗ cắm dấu
    /// "đã soi" như đính kèm tin. Lọc thêm <c>LIKE 'http%'</c> để bỏ qua Telegram — ảnh Telegram
    /// vốn đã đi qua đường proxy của mình nên lưu dạng tương đối, không có hạn.</para>
    ///
    /// <para>Cách nhận, cách giãn nhịp và điều kiện dừng đều giống hệt
    /// <see cref="ClaimMediaAsync"/> — đọc lý do ở đó. Khác đúng một chỗ: không dùng tới giá trị
    /// <see cref="MirrorDone"/>, vì soi xong thì <c>avatar_url</c> đã trỏ về kho của mình nên
    /// dòng đó tự rơi khỏi điều kiện.</para>
    ///
    /// <para>Bảng này không có chỉ mục riêng cho câu hỏi trên — cố ý, xem ghi chú ở
    /// <see cref="ChatDb"/>: vị từ "đã thuộc kho của mình" phải so với một đoạn url lấy từ cấu
    /// hình lúc chạy, mà chỉ mục có điều kiện thì đòi hằng. Đổi lại bảng này mỗi khách một dòng,
    /// nhỏ hơn bảng tin nhắn nhiều bậc.</para>
    /// </summary>
    public async Task<IReadOnlyList<AvatarToMirror>> ClaimAvatarsAsync(
        string? tenant, string? khoCuaMinh, int limit, short tranTang, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QueryAsync<AvatarToMirror>(
            """
            WITH lay AS (
              SELECT tenant_id, channel, external_id, avatar_state AS tries
                FROM chat_contacts
               WHERE avatar_state >= 0
                 AND avatar_state <= @tran
                 AND (@tenant::text IS NULL OR tenant_id = @tenant::text)
                 AND avatar_url LIKE 'http%'
                 AND (@kho::text IS NULL OR avatar_url NOT LIKE @kho::text || '%')
               ORDER BY avatar_state, tenant_id, channel, external_id
               LIMIT @limit
                 FOR UPDATE SKIP LOCKED
            )
            UPDATE chat_contacts k
               SET avatar_state = CASE WHEN k.avatar_state + 1 >= @toiDa
                                       THEN @boHan ELSE k.avatar_state + 1 END
              FROM lay
             WHERE k.tenant_id = lay.tenant_id AND k.channel = lay.channel
               AND k.external_id = lay.external_id
            RETURNING k.tenant_id AS "TenantId", k.channel AS "Channel",
                      k.external_id AS "ExternalId", k.avatar_url AS "AvatarUrl",
                      lay.tries AS "Tries"
            """, new { tenant, kho = khoCuaMinh, limit, tran = tranTang, toiDa = MirrorMaxTries, boHan = MirrorGaveUp });
        return r.AsList();
    }

    /// <summary>
    /// Thôi không thử tải ảnh đại diện của khách này nữa — url đã hết hạn hoặc bị gỡ.
    /// Cùng lý do với <see cref="GiveUpMediaAsync"/>.
    /// </summary>
    public async Task GiveUpAvatarAsync(string tenant, ChatChannel kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_contacts SET avatar_state = @bo
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
            """, new { tenant, kenh = (short)kenh, id = externalId, bo = MirrorGaveUp });
    }

    /// <summary>
    /// Đổi ảnh đại diện của khách sang url mới (bản đã nằm trong kho của mình).
    ///
    /// <para>Xoá luôn cờ về 0: url mới không dính dáng gì tới những lần hỏng của url cũ. Không
    /// xoá thì khách nào từng có một ảnh chết sẽ mang theo số đếm đó mãi, và tới lần đổi ảnh sau
    /// là bị bỏ qua ngay từ lượt đầu.</para>
    /// </summary>
    public async Task SetContactAvatarAsync(string tenant, ChatChannel kenh, string externalId,
        string url, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_contacts
               SET avatar_url = @url, avatar_state = 0, updated_utc = now()
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
            """, new { tenant, kenh = (short)kenh, id = externalId, url });
    }

    /// <summary>
    /// Ghi nguồn khách đến cho hội thoại — <b>chỉ khi chưa có</b>.
    ///
    /// <para>COALESCE chứ không đè: khách quay lại qua một quảng cáo khác thì nguồn ĐẦU TIÊN mới
    /// là cái đã kéo họ tới. Đè lên là hỏng số liệu quy công quảng cáo, mà hỏng âm thầm — không
    /// ai nhìn ra một con số quy sai.</para>
    /// </summary>
    public async Task SetReferralAsync(string tenant, long hoiThoaiId, ChatReferral r,
        CancellationToken ct = default)
    {
        if (r.Source is null && r.Ref is null && r.AdId is null) return;
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET referral_source = COALESCE(referral_source, @nguon),
                   referral_ref    = COALESCE(referral_ref,    @tref),
                   referral_ad_id  = COALESCE(referral_ad_id,  @ad)
             WHERE tenant_id = @tenant AND id = @id
            """, new { tenant, id = hoiThoaiId, nguon = r.Source, tref = r.Ref, ad = r.AdId });
    }
    // ── Cảm xúc ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi hoặc GỠ một cảm xúc. Một người chỉ giữ MỘT cảm xúc trên một tin — thả cái mới là đè
    /// cái cũ, đúng như hành vi của Messenger.
    /// </summary>
    public async Task SetReactionAsync(string tenant, ChatChannel kenh, ChatReaction cx,
        string aiTha, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (cx.Removed)
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
                       emoji = cx.Emoji, ten = cx.Name });
    }

    /// <summary>Cảm xúc của các tin trong một hội thoại, để đính kèm lúc liệt kê tin.</summary>
    public async Task<IReadOnlyList<ChatReactionRow>> ReactionsByConversationAsync(string tenant,
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
    /// <param name="matTiep">Riêng hay công khai. ⚠️ Nằm TRONG khoá duy nhất: một khách vừa nhắn
    /// riêng vừa bình luận dưới bài là hai hội thoại tách hẳn nhau. Gộp lại thì câu người trực gõ
    /// để trả lời riêng sẽ đi ra công khai dưới bài viết.</param>
    /// <param name="maBaiViet">Mã bài viết, khi đây là luồng bình luận. Cũng nằm trong khoá: cùng
    /// một người bình luận dưới hai bài là hai luồng khác nhau.</param>
    public async Task<ChatConversation> GetOrCreateConversationAsync(string tenant, ChatChannel kenh,
        string externalId, string accountId, CancellationToken ct = default,
        ChatSurface matTiep = ChatSurface.DirectMessage, string? maBaiViet = null)
    {
        await using var c = await _db.OpenAsync(ct);
        // ON CONFLICT DO UPDATE (không phải DO NOTHING) để câu lệnh LUÔN trả về dòng — DO NOTHING
        // thì lần chạy đồng thời thứ hai trả rỗng và phải SELECT thêm một vòng.
        return await c.QuerySingleAsync<ChatConversation>("""
            INSERT INTO chat_conversations
              (tenant_id, channel, contact_external_id, account_id, surface, source_thread_id)
            VALUES (@tenant, @kenh, @id, @accountId, @matTiep, @maBai)
            ON CONFLICT (tenant_id, channel, account_id, contact_external_id, surface, source_thread_id)
              DO UPDATE SET tenant_id = EXCLUDED.tenant_id
            RETURNING *
            """, new { tenant, kenh = (short)kenh, id = externalId, accountId,
                       matTiep = (short)matTiep, maBai = maBaiViet ?? "" });
    }

    /// <summary>
    /// Một hội thoại, KÈM tên và ảnh của khách.
    ///
    /// <para>⚠️ Phải ghép <c>chat_contacts</c> y như câu liệt kê. Trước 28/08/2026 hàm này chỉ
    /// <c>SELECT *</c> trên một bảng, nên <c>DisplayName</c> luôn rỗng và giao diện rơi về mã số:
    /// danh sách bên trái hiện "Thắng Triệu" còn đầu khung chat ngay cạnh hiện
    /// "4951953868228330" — cùng một khách, hai cái tên, trên cùng một màn hình.</para>
    /// </summary>
    public async Task<ChatConversation?> GetConversationAsync(string tenant, long id, CancellationToken ct = default,
        string? nguoiDung = null)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatConversation>("""
            SELECT v.*, ct.display_name, ct.avatar_url, ct.blocked_utc,
                   EXISTS (SELECT 1 FROM chat_conversation_follows f
                            WHERE f.tenant_id = v.tenant_id AND f.conversation_id = v.id
                              AND f.username = @nguoiDung) AS followed
              FROM chat_conversations v
              LEFT JOIN chat_contacts ct
                ON ct.tenant_id = v.tenant_id AND ct.channel = v.channel
               AND ct.external_id = v.contact_external_id
             WHERE v.id = @id AND v.tenant_id = @tenant
            """, new { id, tenant, nguoiDung });
    }

    /// <summary>
    /// Hội thoại chứa một tin. Proxy tệp Telegram cần cả hai thứ trong một lượt hỏi: <b>tin có
    /// thuộc công ty này không</b> (id là số tăng dần, đoán được) và <b>tin tới qua tài khoản nào</b>
    /// — vì <c>file_id</c> của Telegram gắn với TỪNG bot, đổi bằng token bot khác là họ trả lỗi.
    /// </summary>
    public async Task<ChatConversation?> GetConversationByMessageAsync(string tenant, long messageId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatConversation>("""
            SELECT v.* FROM chat_conversations v
              JOIN chat_messages m ON m.conversation_id = v.id
            WHERE m.id = @messageId AND m.tenant_id = @tenant
            """, new { messageId, tenant });
    }

    /// <summary>
    /// Thời điểm một tin, tra theo mã của nhà cung cấp.
    ///
    /// <para>Chỉ Instagram cần: kênh đó báo "khách đã xem" bằng <b>mã tin cuối đã đọc</b> chứ không
    /// bằng mốc thời gian, mà luật đánh dấu hàng loạt lại chạy theo thời gian. Không tìm thấy thì trả
    /// <c>null</c> — chỗ gọi phải BỎ QUA, đoán một mốc là đánh dấu thừa lên tin khách chưa hề mở.</para>
    /// </summary>
    public async Task<DateTime?> GetMessageSentAtAsync(string tenant, long conversationId,
        string externalMsgId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<DateTime?>("""
            SELECT created_utc FROM chat_messages
            WHERE tenant_id = @tenant AND conversation_id = @conversationId
              AND external_msg_id = @externalMsgId
            LIMIT 1
            """, new { tenant, conversationId, externalMsgId });
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
        bool chiChuaDoc = false, bool chiTheoDoi = false, ConvCursor? sau = null, int limit = 60, string? nguoiDung = null,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<ChatConversation>("""
            SELECT v.*, ct.display_name, ct.avatar_url, ct.blocked_utc,
                   r.last_read_at AS my_last_read_at,
                   EXISTS (SELECT 1 FROM chat_conversation_follows f2
                            WHERE f2.tenant_id = v.tenant_id AND f2.conversation_id = v.id
                              AND f2.username = @nguoiDung) AS followed
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
              AND (NOT @chiTheoDoi OR EXISTS (
                    SELECT 1 FROM chat_conversation_follows f
                     WHERE f.tenant_id = v.tenant_id AND f.conversation_id = v.id
                       AND f.username = @nguoiDung))
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
            """, new { tenant, trangThai, chiCuaToi, kenh, giaoCho, chuaDoc = chiChuaDoc, chiTheoDoi, nguoiDung,
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
    public async Task<int> LinkCrmAsync(string tenant, short kenh, string externalId,
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

    /// <summary><paramref name="tag"/> phải ĐÃ chuẩn hoá (xem <c>ChatRules.NormalizeSlug</c>).</summary>
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
    /// <summary>
    /// Đếm hội thoại cho các chip lọc.
    ///
    /// <para>⚠️ <paramref name="kenh"/> chỉ kẹp <b>đếm theo TRẠNG THÁI</b>, KHÔNG kẹp đếm theo kênh.
    /// Hai con số đó trả lời hai câu khác nhau: chip trạng thái nói "trong kênh đang xem có bao
    /// nhiêu việc mới", còn dải kênh nói "mỗi kênh có bao nhiêu" — kẹp cả hai thì chọn một kênh là
    /// mọi kênh khác về 0 và người dùng mất đường quay lại.</para>
    ///
    /// <para>Trước 28/08/2026 chỗ này không nhận kênh: lọc sang Telegram mà chip vẫn hiện số của
    /// cả sáu kênh — danh sách một đằng, con số một nẻo, ngay cạnh nhau trên cùng màn hình.</para>
    /// </summary>
    public async Task<ChatInboxCounts> CountAsync(string tenant, string? chiCuaToi,
        string? nguoiDung = null, short? kenh = null, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = (await c.QueryAsync<RowCount>("""
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
            // Dải kênh luôn đếm ĐỦ MỌI KÊNH — xem ghi chú ở chữ ký hàm.
            theoKenh[r.Channel] = theoKenh.GetValueOrDefault(r.Channel) + r.So;
            if (kenh is { } k && r.Channel != k) continue;
            theoTrangThai[r.Status] = theoTrangThai.GetValueOrDefault(r.Status) + r.So;
        }

        // Tổng và chưa đọc đi theo chip trạng thái: chúng đứng cùng chỗ và nói về cùng một danh sách.
        var trongKenh = kenh is { } kk ? rows.Where(r => r.Channel == kk).ToList() : rows;
        return new ChatInboxCounts(theoTrangThai, theoKenh,
            trongKenh.Sum(r => r.Unread), trongKenh.Sum(r => r.So));
    }

    private class RowCount
    {
        public short Status { get; set; }
        public short Channel { get; set; }
        public int So { get; set; }
        public int Unread { get; set; }
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
    public async Task<int> ClaimConversationAsync(string tenant, long id, string username,
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
    public async Task AppendAuditAsync(string tenant, long? hoiThoaiId, string username,
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
    public async Task<string?> AssigneeOfAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT assigned_username FROM chat_conversations WHERE id = @id AND tenant_id = @tenant",
            new { id, tenant });
    }

    /// <summary>
    /// Giao/gỡ giao KHÔNG kiểm ai đang giữ — dùng cho <b>nhả việc</b> và <b>chuyển việc</b>, là
    /// hai thao tác cố ý đè lên người đang giữ. Nhận việc thì dùng <see cref="ClaimConversationAsync"/>.
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

    /// <summary>
    /// Đánh dấu hội thoại CHƯA đọc, cho riêng người đang thao tác.
    ///
    /// <para><b>ĐẶT mốc chứ không XOÁ dòng.</b> Xoá thì phép tính chưa đọc lùi về cột chung
    /// <c>agent_last_read_at</c> — vốn có thể vẫn mới vì người khác vừa mở — và hội thoại vẫn
    /// hiện là đã đọc. Người dùng bấm nút, không thấy gì đổi, và không có lỗi nào để lần ra.</para>
    ///
    /// <para>Trả <c>false</c> khi hội thoại chưa có tin nào của khách: lúc đó không có gì để đánh
    /// dấu chưa đọc, và tự nghĩ ra một mốc là nói dối dữ liệu.</para>
    /// </summary>
    public async Task<bool> MarkUnreadAsync(string tenant, long id, string username,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var soDong = await c.ExecuteAsync("""
            INSERT INTO chat_conversation_reads (tenant_id, conversation_id, username, last_read_at)
            SELECT @tenant, @id, @username, m.created_utc - interval '1 millisecond'
              FROM chat_messages m
              JOIN chat_conversations c ON c.id = m.conversation_id
                                       AND c.id = @id AND c.tenant_id = @tenant
             WHERE m.conversation_id = @id AND m.tenant_id = @tenant AND m.direction = 0
             ORDER BY m.created_utc DESC
             LIMIT 1
            ON CONFLICT (tenant_id, conversation_id, username)
            DO UPDATE SET last_read_at = EXCLUDED.last_read_at
            """, new { tenant, id, username });
        return soDong > 0;
    }

    /// <summary>
    /// Chặn / bỏ chặn một khách. Ghi vào DANH BẠ chứ không vào hội thoại: khách nhắn lại qua
    /// một hội thoại khác (bình luận dưới bài chẳng hạn) thì vẫn phải bị chặn.
    ///
    /// <para>⚠️ Chỉ có tác dụng TRONG hộp thư của mình — xem ghi chú ở <see cref="ChatDb"/>.</para>
    /// </summary>
    public async Task SetContactBlockedAsync(string tenant, ChatChannel kenh, string externalId,
        bool chan, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_contacts
               SET blocked_utc = CASE WHEN @chan THEN now() ELSE NULL END,
                   blocked_by  = CASE WHEN @chan THEN @username ELSE NULL END,
                   updated_utc = now()
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
            """, new { tenant, kenh = (short)kenh, id = externalId, chan, username });
    }

    /// <summary>
    /// Xoá MỀM một tin khỏi hộp thư của mình. Dùng lại cột <c>deleted_utc</c> đã có sẵn từ đợt
    /// bình luận (khách tự xoá bình luận của họ) — đừng thêm cột thứ hai cùng nghĩa.
    ///
    /// <para>⚠️ Chỉ xoá ở PHÍA MÌNH. Không nền tảng nào cho doanh nghiệp thu hồi tin đã gửi.</para>
    /// </summary>
    public async Task<bool> SoftDeleteMessageAsync(string tenant, long conversationId,
        long messageId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages SET deleted_utc = now()
             WHERE id = @id AND tenant_id = @tenant AND conversation_id = @conv
               AND deleted_utc IS NULL
            """, new { id = messageId, tenant, conv = conversationId }) > 0;
    }

    /// <summary>
    /// Sửa nội dung một tin CHƯA gửi đi.
    ///
    /// <para>Điều kiện trạng thái kiểm NGAY TRONG câu lệnh chứ không chỉ ở tầng trên: kiểm rồi
    /// mới ghi là có cửa sổ để worker gửi nhặt đúng tin đó lên giữa hai lượt. Danh sách trạng
    /// thái phải khớp <see cref="ChatRules.CoTheSuaTin"/>.</para>
    /// </summary>
    public async Task<bool> EditPendingMessageAsync(string tenant, long conversationId,
        long messageId, string body, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages SET body = @body
             WHERE id = @id AND tenant_id = @tenant AND conversation_id = @conv
               AND state IN (0, 4) AND deleted_utc IS NULL
            """, new { id = messageId, tenant, conv = conversationId, body }) > 0;
    }

    /// <summary>Bật/tắt theo dõi một hội thoại cho riêng một người.</summary>
    public async Task SetFollowAsync(string tenant, long id, string username, bool theoDoi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (theoDoi)
            await c.ExecuteAsync("""
                INSERT INTO chat_conversation_follows (tenant_id, conversation_id, username)
                VALUES (@tenant, @id, @username)
                ON CONFLICT (tenant_id, conversation_id, username) DO NOTHING
                """, new { tenant, id, username });
        else
            await c.ExecuteAsync("""
                DELETE FROM chat_conversation_follows
                 WHERE tenant_id = @tenant AND conversation_id = @id AND username = @username
                """, new { tenant, id, username });
    }

    // ── Xoá dữ liệu theo yêu cầu (Meta Data Deletion Callback) ──────────────

    /// <summary>Kết quả một lượt xoá: đếm để trả lời "đã xoá những gì".</summary>
    public record KetQuaXoa(int SoHoiThoai, int SoTin);

    /// <summary>
    /// Xoá SẠCH dữ liệu của một người trên một kênh, ở MỌI công ty.
    ///
    /// <para><b>Vì sao không khoá theo công ty.</b> Meta chỉ gửi sang mã người dùng, không nói
    /// người đó đã nhắn cho công ty nào. Mà một người hoàn toàn có thể đã nhắn cho hai công ty
    /// khác nhau cùng dùng hệ này. Xoá thiếu một chỗ là lời hứa "đã xoá" thành lời nói dối.</para>
    ///
    /// <para><b>Xoá THẬT, không xoá mềm.</b> Mọi chỗ khác trong hệ đều xoá mềm để giữ lịch sử
    /// nghiệp vụ — riêng đường này thì không: đây là yêu cầu xoá dữ liệu cá nhân, giữ lại "cho có
    /// dấu vết" đúng là thứ người ta yêu cầu bỏ đi.</para>
    ///
    /// <para>Chạy trong MỘT giao dịch: xoá được nửa chừng rồi hỏng thì còn tệ hơn chưa xoá —
    /// không biết phần nào đã đi, phần nào còn.</para>
    /// </summary>
    public async Task<KetQuaXoa> DeleteContactDataAsync(ChatChannel kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var giaoDich = await c.BeginTransactionAsync(ct);

        var tham = new { kenh = (short)kenh, id = externalId };

        // Đếm TRƯỚC khi xoá — xoá xong thì không còn gì mà đếm.
        var soHoiThoai = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM chat_conversations WHERE channel = @kenh AND contact_external_id = @id",
            tham, giaoDich);
        var soTin = await c.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM chat_messages m
              JOIN chat_conversations v ON v.id = m.conversation_id
             WHERE v.channel = @kenh AND v.contact_external_id = @id
            """, tham, giaoDich);

        // chat_messages và chat_conversation_reads tự đi theo nhờ ON DELETE CASCADE của
        // chat_conversations; ba bảng còn lại khoá theo (tenant, kênh, mã ngoài) nên phải xoá tay.
        await c.ExecuteAsync(
            "DELETE FROM chat_conversations WHERE channel = @kenh AND contact_external_id = @id",
            tham, giaoDich);
        await c.ExecuteAsync(
            "DELETE FROM chat_contact_tags WHERE channel = @kenh AND external_id = @id",
            tham, giaoDich);
        await c.ExecuteAsync(
            "DELETE FROM chat_contact_notes WHERE channel = @kenh AND external_id = @id",
            tham, giaoDich);
        await c.ExecuteAsync(
            "DELETE FROM chat_reactions WHERE channel = @kenh AND actor_external_id = @id",
            tham, giaoDich);
        // Danh bạ xoá SAU CÙNG: nó là chỗ giữ tên và ảnh đại diện, tức phần nhận dạng rõ nhất.
        await c.ExecuteAsync(
            "DELETE FROM chat_contacts WHERE channel = @kenh AND external_id = @id",
            tham, giaoDich);

        await giaoDich.CommitAsync(ct);
        return new(soHoiThoai, soTin);
    }

    /// <summary>Ghi lại một yêu cầu xoá đã làm xong, để người đó tra tiến độ bằng mã.</summary>
    public async Task RecordDeletionAsync(string code, ChatChannel kenh, string externalId,
        KetQuaXoa kq, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_deletion_requests
              (code, channel, external_id, done_utc, so_hoi_thoai, so_tin)
            VALUES (@code, @kenh, @id, now(), @hoiThoai, @tin)
            ON CONFLICT (code) DO NOTHING
            """, new { code, kenh = (short)kenh, id = externalId,
                       hoiThoai = kq.SoHoiThoai, tin = kq.SoTin });
    }

    public record YeuCauXoa(string Code, DateTime RequestedUtc, DateTime? DoneUtc,
        int SoHoiThoai, int SoTin);

    /// <summary>Tra một yêu cầu xoá theo mã. Trang tra cứu CÔNG KHAI dùng hàm này.</summary>
    public async Task<YeuCauXoa?> GetDeletionAsync(string code, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<YeuCauXoa>("""
            SELECT code AS "Code", requested_utc AS "RequestedUtc", done_utc AS "DoneUtc",
                   so_hoi_thoai AS "SoHoiThoai", so_tin AS "SoTin"
              FROM chat_deletion_requests WHERE code = @code
            """, new { code });
    }

    // ── Tin nhắn ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi một tin. Trả <c>null</c> khi <paramref name="externalMsgId"/> đã có — tức webhook gửi
    /// lại, KHÔNG phải tin mới. Chống trùng dựa vào chỉ mục duy nhất ở CSDL chứ không phải kiểm
    /// trước rồi ghi: hai lần gửi song song thì cách kiểm-rồi-ghi vẫn lọt.
    /// </summary>
    /// <param name="createdUtc">Thời điểm THẬT của tin. Để <c>null</c> ở tin trực tiếp — cột tự
    /// đóng dấu giờ ghi, lệch vài giây không ai thấy. Chỉ NHẬP LỊCH SỬ mới cần truyền: bỏ qua là
    /// cả năm hội thoại cũ dồn vào một phút và dòng thời gian đảo lộn hết.</param>
    public async Task<long?> AppendMessageAsync(string tenant, long conversationId, ChatChannel kenh,
        ChatDirection chieu, ChatSender nguoiGui, string? username, ChatKind loai, string? noiDung,
        string? attachmentJson, string? externalMsgId, ChatState trangThai, CancellationToken ct = default,
        DateTime? createdUtc = null, string? buttonsJson = null, string? parentExternalId = null)
    {
        await using var c = await _db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long?>("""
            INSERT INTO chat_messages
              (tenant_id, conversation_id, channel, direction, sender_kind, sender_username,
               kind, body, attachment, external_msg_id, state, created_utc, buttons,
               parent_external_id)
            VALUES (@tenant, @conv, @kenh, @chieu, @nguoiGui, @username,
                    @loai, @noiDung, @att::jsonb, @ext, @tt,
                    COALESCE(@luc, NOW() AT TIME ZONE 'utc'), @nut::jsonb, @cha)
            ON CONFLICT (tenant_id, channel, external_msg_id) WHERE external_msg_id IS NOT NULL
              DO NOTHING
            RETURNING id
            """, new { tenant, conv = conversationId, kenh = (short)kenh, chieu = (short)chieu,
                       nguoiGui = (short)nguoiGui, username, loai = (short)loai, noiDung,
                       att = attachmentJson, ext = externalMsgId, tt = (short)trangThai,
                       luc = createdUtc, nut = buttonsJson, cha = parentExternalId });

        if (id is null) _log.LogDebug("[chat] bỏ tin trùng ext={Ext} conv={Conv}", externalMsgId, conversationId);
        return id;
    }

    /// <summary>
    /// Người bình luận SỬA hoặc XOÁ bình luận của họ.
    ///
    /// <para><b>Xoá mềm</b>: giữ dòng lại, chỉ đóng dấu <c>deleted_utc</c>. Người trực có thể đã
    /// đọc câu đó và đã làm gì đó theo nó — xoá sạch khỏi CSDL là để lịch sử nói dối rằng chuyện
    /// đó chưa từng xảy ra, và người trực không hiểu vì sao mình nhớ một câu không có thật.</para>
    ///
    /// <para>Tìm theo mã của nhà cung cấp trong phạm vi công ty + kênh, KHÔNG theo hội thoại:
    /// gói sửa/xoá của Meta không chở mã bài viết, nên không suy ra được hội thoại nào.</para>
    /// </summary>
    /// <returns>Id hội thoại chứa bình luận đó, hoặc <c>null</c> nếu không có dòng nào khớp —
    /// chuyện thường: khách sửa một bình luận có từ trước khi công ty nối kênh.</returns>
    public async Task<long?> ApplyCommentChangeAsync(string tenant, ChatChannel kenh,
        CommentChange doi, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long?>("""
            UPDATE chat_messages
               SET body        = CASE WHEN @xoa THEN body ELSE @chu END,
                   deleted_utc = CASE WHEN @xoa THEN NOW() AT TIME ZONE 'utc' ELSE deleted_utc END
             WHERE tenant_id = @tenant AND channel = @kenh AND external_msg_id = @ext
            RETURNING conversation_id
            """, new { tenant, kenh = (short)kenh, ext = doi.ExternalMsgId,
                       chu = doi.NewText, xoa = doi.Removed });
    }

    /// <summary>
    /// Tính lại mốc hoạt động và dòng xem trước TỪ CHÍNH các tin đang có.
    ///
    /// <para>Dùng sau khi nhập lịch sử. Không dùng <see cref="TouchConversationAsync"/> được:
    /// hàm đó đóng dấu giờ HIỆN TẠI, mà tin vừa nhập là tin cũ — hội thoại chết ba năm sẽ nhảy
    /// lên đầu hộp thư như vừa có người nhắn.</para>
    ///
    /// <para>Đọc lại từ bảng tin thay vì nhận mốc từ chỗ gọi: lịch sử về theo từng mảnh không
    /// theo thứ tự nào, nên chỗ gọi không biết mảnh mình cầm có phải mảnh mới nhất không.</para>
    /// </summary>
    public async Task RecomputeActivityAsync(string tenant, long conversationId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_conversations c
               SET last_activity_at  = COALESCE(m.moc, c.last_activity_at),
                   last_preview      = COALESCE(m.xem, c.last_preview),
                   contact_replied_at = COALESCE(m.khach, c.contact_replied_at)
              FROM (
                    SELECT MAX(created_utc) AS moc,
                           MAX(created_utc) FILTER (WHERE direction = 0) AS khach,
                           (SELECT LEFT(COALESCE(body, ''), 200) FROM chat_messages
                              WHERE conversation_id = @conv AND tenant_id = @tenant
                              ORDER BY created_utc DESC, id DESC LIMIT 1) AS xem
                      FROM chat_messages
                     WHERE conversation_id = @conv AND tenant_id = @tenant
                   ) m
             WHERE c.id = @conv AND c.tenant_id = @tenant
            """, new { tenant, conv = conversationId });
    }

    /// <summary>
    /// Một tin của khách còn đính kèm trỏ ra URL của nền tảng, vừa được nhận về để soi.
    /// </summary>
    /// <param name="Tries">Đã thử soi bấy nhiêu lần TRƯỚC lượt này. 0 = tin chưa ai đụng tới.</param>
    public record MediaToMirror(long Id, string TenantId, long ConversationId, short Channel, short Kind,
        string Attachment, short Tries);

    /// <summary>
    /// Cách mã hoá cột cờ <c>chat_messages.media_state</c> và <c>chat_contacts.avatar_state</c> —
    /// <b>một cột mang cả ba thông tin</b> thay vì ba cột riêng.
    ///
    /// <para>Số <b>không âm</b> là số lần đã thử và có nghĩa "còn trong hàng chờ"; số <b>âm</b> là
    /// điểm dừng. Nhờ vậy vị từ của chỉ mục có điều kiện chỉ là <c>media_state >= 0</c> — không
    /// phải nhét hằng số "tối đa mấy lần" vào DDL, nên đổi <see cref="MirrorMaxTries"/> không kéo
    /// theo phải dựng lại chỉ mục.</para>
    /// </summary>
    public const short MirrorDone = -1;

    /// <summary>Thôi, đừng thử nữa. Xem <see cref="MirrorDone"/> để biết cách mã hoá.</summary>
    public const short MirrorGaveUp = -2;

    /// <summary>
    /// Thử tối đa bấy nhiêu lượt rồi bỏ.
    ///
    /// <para>Khoảng cách giữa các lượt KHÔNG lưu trong CSDL: nó chính là nhịp của vòng quét
    /// (<c>ChatMediaBackfillWorker</c>), vì mỗi vòng quét chỉ thử mỗi tin đúng một lần — xem
    /// <see cref="ClaimMediaAsync"/>. Bớt được một cột mốc thời gian mà vẫn có đủ giãn cách.</para>
    /// </summary>
    public const int MirrorMaxTries = 5;

    /// <summary>
    /// Truyền làm trần tầng khi <b>không</b> muốn chặn tầng nào — mẻ đầu của một vòng quét, và
    /// mọi lượt gọi tay. Xem <c>tranTang</c> của <see cref="ClaimMediaAsync"/>.
    /// </summary>
    public const short AnyTier = short.MaxValue;

    /// <summary>
    /// <b>NHẬN</b> một mẻ tin còn đính kèm chưa soi — không phải chỉ đọc: mỗi dòng lấy ra đều
    /// được đánh dấu ngay trong cùng câu lệnh.
    ///
    /// <para><b>Vì sao phải nhận chứ không chỉ liệt kê.</b> Tin soi HỎNG cố ý không bị ghi đè
    /// (giữ nguyên gói gốc, vì <c>file_id</c> của Telegram nằm trong đó). Bản trước vì thế phải
    /// mang theo một cái mốc "chạy tiếp từ id nào" để khỏi kẹt tại chỗ — nhưng mốc đó reset về 0
    /// mỗi vòng, nên những ảnh đã chết hẳn vẫn được tải lại đủ mỗi sáu tiếng, mãi mãi, và phần
    /// đã chết thì chỉ tăng theo thời gian. Nay dấu nằm trong CSDL nên tiến độ không bao giờ lùi.</para>
    ///
    /// <para><b>Lấy và đếm trong CÙNG một câu lệnh:</b> mỗi dòng lấy ra là <c>media_state + 1</c>
    /// ngay tại chỗ, và tới lượt thứ <see cref="MirrorMaxTries"/> thì thành
    /// <see cref="MirrorGaveUp"/>. Không có cột mốc thời gian nào cả — giãn cách giữa hai lượt
    /// thử chính là nhịp của vòng quét, nhờ <c>ORDER BY media_state</c> cộng với
    /// <paramref name="tranTang"/>.</para>
    ///
    /// <para>⚠️ <b><paramref name="tranTang"/> phải chặn ở ĐÂY, không phải ở chỗ gọi.</b> Bản đầu
    /// để vòng quét tự nhận ra "đã sang tầng thử lại" rồi dừng — nhưng lúc nhận ra thì mẻ đó đã
    /// tải xong rồi, tức là tin ở tầng trên vẫn ăn thêm một lượt thử oan. Đo được ngay trên dữ
    /// liệu thật (28/08/2026): một khách duy nhất trong hàng chờ mà cờ nhảy thẳng 0 → 2 trong
    /// một vòng, tức là mất một nửa số lần thử chỉ vì mạng chập vài giây.</para>
    ///
    /// <para><b><c>FOR UPDATE SKIP LOCKED</c></b> để hai tiến trình web chạy song song (hoặc một
    /// lượt gọi tay đúng lúc worker đang chạy) không giành nhau cùng một tin — cùng lối với
    /// <c>ClaimOutboxAsync</c>, mỗi dòng chỉ một bên lấy được.</para>
    ///
    /// <para>Trong cùng số lần thử thì cũ trước — mới sau, cố ý: URL của nền tảng hết hạn theo
    /// tuổi tin, nên tin cũ nhất là tin sắp mất trước nhất. Chạy nửa chừng bị ngắt thì phần đã
    /// cứu cũng là phần cần nhất. Chỉ lấy <c>direction = 0</c> — tệp mình gửi đi vốn đã nằm
    /// trong kho của mình.</para>
    ///
    /// <para>⚠️ Câu <c>WHERE</c> và <c>ORDER BY</c> của CTE phải KHỚP chỉ mục <c>ix_msg_media_cho</c>
    /// (<see cref="ChatDb"/>). Lệch một chữ là Postgres bỏ chỉ mục và quay ra quét cả bảng tin
    /// nhắn — vẫn chạy đúng, chỉ chậm dần tới lúc không ai chịu nổi. <c>ChatSchemaGuardTests</c>
    /// canh chỗ này.</para>
    ///
    /// <para><c>tenant = null</c> nghĩa là MỌI công ty, cùng lối với <c>ClaimOutboxAsync</c>: việc
    /// này do worker nền làm cho cả máy chủ chứ không ai đứng ra bấm cho từng công ty. Mỗi dòng
    /// mang theo <c>TenantId</c> của chính nó để tệp được ghi vào đúng kho công ty đó — ảnh khách
    /// của hai công ty KHÔNG bao giờ dùng chung một đối tượng.</para>
    /// </summary>
    /// <param name="tranTang">
    /// Chỉ nhận tin đã thử TỐI ĐA bấy nhiêu lần. <see cref="AnyTier"/> = không chặn (mẻ đầu của
    /// một vòng quét, và mọi lượt gọi tay).
    /// </param>
    public async Task<IReadOnlyList<MediaToMirror>> ClaimMediaAsync(string? tenant, int limit,
        short tranTang, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QueryAsync<MediaToMirror>(
            """
            WITH lay AS (
              -- Giữ lại cờ CŨ để trả về: chỗ gọi cần biết tin này đã thử mấy lần TRƯỚC lượt
              -- này, mà RETURNING của UPDATE thì chỉ thấy giá trị mới.
              SELECT id, media_state AS tries
                FROM chat_messages
               WHERE media_state >= 0 AND direction = 0 AND attachment IS NOT NULL
                 AND media_state <= @tran
                 AND (@tenant::text IS NULL OR tenant_id = @tenant::text)
               ORDER BY media_state, id
               LIMIT @limit
                 FOR UPDATE SKIP LOCKED
            )
            UPDATE chat_messages m
               SET media_state = CASE WHEN m.media_state + 1 >= @toiDa
                                      THEN @boHan ELSE m.media_state + 1 END
              FROM lay
             WHERE m.id = lay.id
            RETURNING m.id AS "Id", m.tenant_id AS "TenantId", m.conversation_id AS "ConversationId",
                      m.channel AS "Channel", m.kind AS "Kind", m.attachment::text AS "Attachment",
                      lay.tries AS "Tries"
            """, new { tenant, limit, tran = tranTang, toiDa = MirrorMaxTries, boHan = MirrorGaveUp });
        return r.AsList();
    }

    /// <summary>
    /// Thôi không thử soi tin này nữa — url đã hết hạn hoặc tệp đã bị gỡ khỏi nền tảng.
    ///
    /// <para>Gọi khi nhà cung cấp trả một mã lỗi <b>không thể cứu</b> (xem
    /// <c>ChatMediaMirror.KetQuaSoi.HetCuu</c>). Không có bước này thì mỗi tấm ảnh đã chết vẫn
    /// ngốn đủ <see cref="MirrorMaxTries"/> lượt tải — nhân với vài nghìn tấm của một hộp thư lâu
    /// năm thì đó là phần lớn công việc mà chẳng cứu được gì.</para>
    ///
    /// <para>Đính kèm GIỮ NGUYÊN: url tuy hết hạn nhưng vẫn là dấu vết duy nhất còn lại của tệp
    /// đó, và với Telegram thì <c>file_id</c> trong đó có thể sống lại nếu bot được nối lại.</para>
    /// </summary>
    public async Task GiveUpMediaAsync(long messageId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE chat_messages SET media_state = @bo WHERE id = @id",
            new { id = messageId, bo = MirrorGaveUp });
    }

    /// <summary>
    /// Thay phần đính kèm của một tin bằng bản ĐÃ SOI về kho riêng.
    ///
    /// <para>Ghi đè hẳn thay vì thêm cột: hình dạng mới tự nhận diện được (khoá <c>tk</c>, xem
    /// <see cref="ChatAttachment"/>), nên tin cũ chưa soi và tin mới đã soi sống chung một cột
    /// mà không lẫn. Thêm cột thì mọi chỗ đọc đính kèm đều phải nhớ hỏi hai nơi.</para>
    ///
    /// <para>Đặt cờ <see cref="MirrorDone"/> trong CÙNG câu lệnh — hai việc này không được rời nhau:
    /// ghi đính kèm mà quên đánh dấu thì tin đã cứu xong vẫn nằm trong hàng chờ và bị tải lại,
    /// còn đánh dấu mà chưa ghi xong thì mất tệp. Nhờ vậy tin nhận MỚI (soi ngay lúc tới, không
    /// qua vòng quét) cũng tự rơi khỏi chỉ mục chờ.</para>
    /// </summary>
    public async Task SetAttachmentAsync(string tenant, long messageId, string attachmentJson,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(
            """
            UPDATE chat_messages
               SET attachment = @att::jsonb, media_state = @xong
             WHERE id = @id AND tenant_id = @tenant
            """,
            new { id = messageId, tenant, att = attachmentJson, xong = MirrorDone });
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
              -- send_after nằm ở hàng đợi GỬI, không ở bảng tin. Ghép vào đây để giao diện có
              -- mốc CÓ THẨM QUYỀN mà đếm ngược nút Thu hồi — suy ra từ created_utc cộng cấu
              -- hình thì sai ngay khi quản trị đổi số giây, và lệch khi đồng hồ máy khách sai.
              -- Chỉ ghép dòng CÒN CHỜ (status = 0): tin đã gửi rồi thì không còn gì để thu hồi.
              SELECT m.*, o.send_after
                FROM chat_messages m
                LEFT JOIN chat_outbox o
                  ON o.message_id = m.id AND o.tenant_id = m.tenant_id AND o.status = 0
               WHERE m.conversation_id = @conv AND m.tenant_id = @tenant
               ORDER BY m.created_utc DESC LIMIT @limit
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
    /// Luật đầy đủ ở <see cref="ChatRules.CanAdvanceState"/>; ở đây chặn ngay trong SQL vì cập nhật hàng
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

    /// <param name="hoanGiay">
    /// Giữ tin lại bấy nhiêu giây trước khi gửi — cửa sổ để người trực rút lại. 0 = gửi ngay.
    /// Tính bằng <c>ChatRules.HoanGuiGiay</c>, đừng tự nhân chia ở chỗ gọi.
    /// </param>
    public async Task EnqueueOutboxAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default, int hoanGiay = 0)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_outbox (tenant_id, conversation_id, message_id, send_after)
            VALUES (@tenant, @conv, @msg,
                    CASE WHEN @hoan > 0 THEN now() + make_interval(secs => @hoan) END)
            """, new { tenant, conv = conversationId, msg = messageId, hoan = hoanGiay });
    }

    /// <summary>
    /// Rút một tin khỏi hàng đợi gửi — thu hồi THẬT, vì tin chưa hề rời máy chủ.
    ///
    /// <para>Điều kiện <c>status = 0</c> và <c>send_after &gt; now()</c> kiểm NGAY TRONG câu
    /// lệnh chứ không ở tầng trên: worker có thể vừa nhặt đúng tin đó lên giữa hai lượt. Trả
    /// <c>false</c> nghĩa là muộn rồi — chỗ gọi phải nói thật với người dùng, đừng báo thành
    /// công cho một việc đã không xảy ra.</para>
    /// </summary>
    public async Task<bool> CancelOutboxAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            WITH bo AS (
              DELETE FROM chat_outbox
               WHERE tenant_id = @tenant AND conversation_id = @conv AND message_id = @msg
                 AND status = 0 AND send_after IS NOT NULL AND send_after > now()
              RETURNING message_id
            )
            UPDATE chat_messages
               SET deleted_utc = now(), state = 4,
                   error_message = 'Người trực đã thu hồi trước khi gửi'
             WHERE id = (SELECT message_id FROM bo) AND tenant_id = @tenant
               AND conversation_id = @conv
            """, new { tenant, conv = conversationId, msg = messageId }) > 0;
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
               SELECT id FROM chat_outbox
                WHERE status = 0 AND (send_after IS NULL OR send_after <= now())
                ORDER BY send_after NULLS FIRST, created_utc
                LIMIT @n FOR UPDATE SKIP LOCKED)
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
