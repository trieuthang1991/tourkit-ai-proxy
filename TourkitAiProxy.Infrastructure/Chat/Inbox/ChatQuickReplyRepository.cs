// Services/Chat/Inbox/ChatQuickReplyRepository.cs
using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <param name="Trigger">Chuỗi gõ sau dấu "/" — đã chuẩn hoá, không dấu, không khoảng trắng.</param>
/// <param name="Buttons">Nút gắn kèm, dạng JSON. <c>null</c> = mẫu chỉ có chữ.
///
/// <para>Đây là nguồn nút THỰC TẾ cho nhân viên: gõ <c>/tuyen</c> là gửi luôn câu hỏi kèm ba
/// nút chọn tuyến, khỏi soạn lại mỗi lần. Nút bấm về sẽ thành một câu của khách và trợ lý xử
/// tiếp như thường — xem <see cref="ChatButton"/>.</para></param>
public record QuickReply(long Id, string Trigger, string Body, string? Buttons = null);

/// <summary>
/// Mẫu trả lời nhanh, theo TỪNG CÔNG TY (không theo từng nhân viên): cả đội trực chat dùng chung
/// một bộ câu, sửa một mẫu là cả đội thấy ngay, không phải dạy lại từng người.
/// </summary>
public class ChatQuickReplyRepository
{
    private readonly ChatDb _db;
    public ChatQuickReplyRepository(ChatDb db) { _db = db; }

    public bool Configured => _db.Configured;

    /// <summary>
    /// Bỏ dấu, hạ chữ thường, thay khoảng trắng bằng gạch nối.
    ///
    /// <para><b>Bỏ dấu là bắt buộc.</b> Nhân viên đang gõ nhanh cho khách sẽ gõ <c>/gia</c> chứ
    /// không dừng lại bật bộ gõ để ra <c>/giá</c>. Giữ nguyên dấu thì mẫu gần như không ai dùng.</para>
    /// </summary>
    public static string NormalizeTrigger(string tho)
    {
        // Dùng CHUNG với nhãn khách (ChatRules.NormalizeSlug) — cùng vấn đề, cùng lời giải. Viết
        // lại lần hai là hai chỗ lệch nhau: "khach-vip" bên này, "khach vip" bên kia, rồi lọc theo
        // nhãn trả về rỗng mà không ai hiểu tại sao.
        var s = ChatRules.NormalizeSlug(tho);
        // CỐ Ý không truyền tên tham số: endpoint trả thẳng Message này cho người dùng, mà
        // ArgumentException tự nối thêm "(Parameter 'tho')" — người khai mẫu đọc phải tên biến
        // trong mã nguồn thì vừa khó hiểu vừa lộ nội bộ.
        if (s.Length == 0)
            throw new ArgumentException("Lệnh gọi mẫu không được rỗng");
        return s;
    }

    public async Task<List<QuickReply>> ListAsync(string tenant, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<QuickReply>(
            "SELECT id, trigger, body, buttons FROM chat_quick_replies WHERE tenant_id = @tenant ORDER BY trigger",
            new { tenant })).ToList();
    }

    /// <param name="buttonsJson">Nút kèm mẫu. <c>null</c> XOÁ nút đang có — cố ý, vì màn hình
    /// sửa mẫu luôn gửi lên trạng thái đầy đủ, và "bỏ hết nút" phải làm được.</param>
    public async Task<long> UpsertAsync(string tenant, string trigger, string body,
        CancellationToken ct = default, string? buttonsJson = null)
    {
        var tg = NormalizeTrigger(trigger);
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO chat_quick_replies (tenant_id, trigger, body, buttons)
            VALUES (@tenant, @tg, @body, @nut::jsonb)
            ON CONFLICT (tenant_id, lower(trigger))
              DO UPDATE SET body = EXCLUDED.body, buttons = EXCLUDED.buttons, updated_utc = now()
            RETURNING id
            """, new { tenant, tg, body, nut = buttonsJson });
    }

    public async Task<bool> DeleteAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM chat_quick_replies WHERE tenant_id = @tenant AND id = @id",
            new { tenant, id }) > 0;
    }
}
