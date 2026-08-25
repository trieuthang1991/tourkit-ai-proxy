// Services/Chat/Inbox/ChatQuickReplyRepository.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <param name="Trigger">Chuỗi gõ sau dấu "/" — đã chuẩn hoá, không dấu, không khoảng trắng.</param>
public record QuickReply(long Id, string Trigger, string Body);

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
    public static string ChuanHoaTrigger(string tho)
    {
        var s = (tho ?? "").Trim().TrimStart('/').ToLowerInvariant();
        s = s.Replace('đ', 'd');
        s = new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"[\s-]+", "-").Trim('-');
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
            "SELECT id, trigger, body FROM chat_quick_replies WHERE tenant_id = @tenant ORDER BY trigger",
            new { tenant })).ToList();
    }

    public async Task<long> UpsertAsync(string tenant, string trigger, string body,
        CancellationToken ct = default)
    {
        var tg = ChuanHoaTrigger(trigger);
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO chat_quick_replies (tenant_id, trigger, body)
            VALUES (@tenant, @tg, @body)
            ON CONFLICT (tenant_id, lower(trigger))
              DO UPDATE SET body = EXCLUDED.body, updated_utc = now()
            RETURNING id
            """, new { tenant, tg, body });
    }

    public async Task<bool> DeleteAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM chat_quick_replies WHERE tenant_id = @tenant AND id = @id",
            new { tenant, id }) > 0;
    }
}
