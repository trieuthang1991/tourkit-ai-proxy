// Services/Chat/Channels/ChannelCredentialStore.cs
using System.Text.Json.Nodes;
using Dapper;
using TourkitAiProxy.Services.Chat.Inbox;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Khoá kết nối của từng kênh chat, theo từng công ty. Dùng lại bảng
/// <c>dbo.TenantChannelSettings</c> vốn đã tổng quát <c>(TenantId, Channel, ConfigJson)</c> —
/// không đẻ thêm bảng mới cho việc đã có chỗ chứa.
///
/// <para><b>Mọi giá trị đều mã hoá</b> (Crypton, hậu tố khoá <c>Enc</c>). Kể cả thứ không thật sự
/// bí mật như id trang: mã hoá tất cho đỡ phải nhớ cái nào cần cái nào không — quên một chỗ là lộ
/// token, mà token thì đủ để người khác nhắn tin dưới danh nghĩa công ty.</para>
///
/// <para><b>KHÔNG đụng kênh <c>zalo</c></b> — bản ghi đó do
/// <see cref="Digest.TenantChannelSettingsStore"/> làm chủ, có thêm khoá do worker xoay vòng. Hai
/// nơi cùng ghi một dòng là mất token của nhau.</para>
/// </summary>
public class ChannelCredentialStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<ChannelCredentialStore> _log;

    public ChannelCredentialStore(TourkitAiDb db, ILogger<ChannelCredentialStore> log)
    { _db = db; _log = log; }

    /// Tên bản ghi cấu hình cho từng kênh chat.
    public static string KeyOf(ChatChannel kenh) => kenh switch
    {
        ChatChannel.Messenger => "messenger",
        ChatChannel.Telegram => "telegram",
        ChatChannel.Webchat => "webchat",
        // Zalo có kho riêng — trả tên khác để lỡ gọi nhầm cũng không ghi đè bản ghi của nó.
        _ => "chat-" + kenh.ToString().ToLowerInvariant(),
    };

    /// Đọc khoá đã giải mã. Chưa khai → null.
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(string tenantId, ChatChannel kenh,
        CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var json = await c.ExecuteScalarAsync<string?>(
                "SELECT ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId=@t AND Channel=@c",
                new { t = tenantId, c = KeyOf(kenh) });
            if (string.IsNullOrWhiteSpace(json)) return null;

            var o = JsonNode.Parse(json)?.AsObject();
            if (o is null) return null;

            var ra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in o)
            {
                if (kv.Value is null) continue;
                var ten = kv.Key.EndsWith("Enc", StringComparison.Ordinal) ? kv.Key[..^3] : kv.Key;
                var giaTri = kv.Key.EndsWith("Enc", StringComparison.Ordinal)
                    ? Crypton.Decrypt(kv.Value.ToString())
                    : kv.Value.ToString();
                if (!string.IsNullOrWhiteSpace(giaTri)) ra[ten] = giaTri;
            }
            return ra;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/cred] đọc khoá kênh {K} hỏng, tenant={T}", kenh, tenantId);
            return null;
        }
    }

    /// <summary>
    /// Lưu khoá. Giá trị rỗng = <b>giữ nguyên</b> giá trị đang có, không xoá — giao diện không đọc
    /// lại được bí mật nên không thể gửi lại, gửi rỗng mà hiểu là "xoá" thì mỗi lần sửa một ô là
    /// mất sạch phần còn lại.
    /// </summary>
    public async Task SaveAsync(string tenantId, ChatChannel kenh, IDictionary<string, string?> giaTri,
        CancellationToken ct = default)
    {
        var hienCo = await GetAsync(tenantId, kenh, ct) ?? new Dictionary<string, string>();
        var gop = new Dictionary<string, string>(hienCo, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in giaTri)
            if (!string.IsNullOrWhiteSpace(kv.Value)) gop[kv.Key] = kv.Value!.Trim();

        var o = new JsonObject();
        foreach (var kv in gop) o[kv.Key + "Enc"] = Crypton.Encrypt(kv.Value);

        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            MERGE dbo.TenantChannelSettings AS t
            USING (SELECT @tenantId AS TenantId, @channel AS Channel) AS s
              ON t.TenantId = s.TenantId AND t.Channel = s.Channel
            WHEN MATCHED THEN UPDATE SET ConfigJson = @json, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (TenantId, Channel, ConfigJson, UpdatedUtc)
              VALUES (@tenantId, @channel, @json, SYSUTCDATETIME());
            """, new { tenantId, channel = KeyOf(kenh), json = o.ToJsonString() });
    }

    public async Task<bool> DeleteAsync(string tenantId, ChatChannel kenh, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM dbo.TenantChannelSettings WHERE TenantId=@t AND Channel=@c",
            new { t = tenantId, c = KeyOf(kenh) }) > 0;
    }
}
