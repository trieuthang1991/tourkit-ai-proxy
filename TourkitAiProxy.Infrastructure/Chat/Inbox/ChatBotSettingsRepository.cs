// Infrastructure/Chat/Inbox/ChatBotSettingsRepository.cs
using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <summary>
/// Cấu hình trợ lý chat theo từng công ty.
///
/// <para><b>Nằm trong CSDL của hộp thư chat (PostgreSQL)</b>, không phải <c>dbo.TenantChannelSettings</c>
/// bên SQL Server — bảng đó dùng chung với cụm bản tin và worker của <c>toutkit-app</c>. Cụm chat
/// tách hẳn, và cấu hình của nó cũng vậy: chung bảng là hai đội cùng sửa một chỗ vì hai lý do khác
/// nhau, rồi khoá của bên này bị bên kia dọn mất.</para>
///
/// <para><b>Đọc rất thường xuyên</b> — mỗi tin khách nhắn tới là một lượt. Nhớ tạm trong bộ nhớ 60
/// giây: đủ để một cụm tin dồn dập không đánh 5 lượt truy vấn, mà vẫn đủ nhanh để người vừa bấm Lưu
/// thấy hiệu lực gần như ngay.</para>
/// </summary>
public class ChatBotSettingsRepository
{
    private static readonly TimeSpan NhoTam = TimeSpan.FromSeconds(60);

    private readonly ChatDb _db;
    private readonly Dictionary<string, (ChatBotSettings Val, DateTime HetHan)> _cache = new();
    private readonly object _khoa = new();

    public ChatBotSettingsRepository(ChatDb db) { _db = db; }

    public bool Configured => _db.Configured;

    public async Task<ChatBotSettings> GetAsync(string tenant, CancellationToken ct = default)
    {
        lock (_khoa)
            if (_cache.TryGetValue(tenant, out var c) && c.HetHan > DateTime.UtcNow)
                return c.Val;

        var v = await DocAsync(tenant, ct);

        lock (_khoa) _cache[tenant] = (v, DateTime.UtcNow + NhoTam);
        return v;
    }

    private async Task<ChatBotSettings> DocAsync(string tenant, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QueryFirstOrDefaultAsync<Dong>("""
            SELECT enabled, persona, greeting, mute_minutes AS MuteMinutes,
                   history_turns AS HistoryTurns
              FROM chat_bot_settings WHERE tenant_id = @tenant
            """, new { tenant });

        // Chưa khai = dùng mặc định. KHÔNG tự chèn một dòng lúc đọc: đọc mà ghi thì mọi công ty
        // từng có một tin nhắn đều mọc ra một dòng cấu hình họ chưa hề đụng tới.
        return r is null
            ? ChatBotSettings.Default
            : new ChatBotSettings(r.Enabled, r.Persona, r.Greeting, r.MuteMinutes, r.HistoryTurns)
                .Normalized();
    }

    public async Task SaveAsync(string tenant, ChatBotSettings v, CancellationToken ct = default)
    {
        var n = v.Normalized();
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_bot_settings
              (tenant_id, enabled, persona, greeting, mute_minutes, history_turns)
            VALUES (@tenant, @Enabled, @Persona, @Greeting, @MuteMinutes, @HistoryTurns)
            ON CONFLICT (tenant_id) DO UPDATE SET
              enabled       = EXCLUDED.enabled,
              persona       = EXCLUDED.persona,
              greeting      = EXCLUDED.greeting,
              mute_minutes  = EXCLUDED.mute_minutes,
              history_turns = EXCLUDED.history_turns,
              updated_utc   = now()
            """, new { tenant, n.Enabled, n.Persona, n.Greeting, n.MuteMinutes, n.HistoryTurns });

        // Dọn bộ nhớ tạm NGAY: người vừa bấm Lưu sẽ thử lại luôn, chờ 60 giây mới thấy hiệu lực
        // thì họ tưởng nút Lưu hỏng và bấm thêm mấy lần nữa.
        lock (_khoa) _cache.Remove(tenant);
    }

    private sealed class Dong
    {
        public bool Enabled { get; set; }
        public string? Persona { get; set; }
        public string? Greeting { get; set; }
        public int MuteMinutes { get; set; }
        public int HistoryTurns { get; set; }
    }
}
