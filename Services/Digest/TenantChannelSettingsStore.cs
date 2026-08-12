using System.Text.Json;
using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// dbo.TenantChannelSettings — TÀI KHOẢN GỬI ĐI của từng công ty.
///
/// <para>Đợt 1 chỉ có <c>Channel='zalo-oa'</c>. Vì sao chỉ Zalo mà không phải mọi kênh: chia theo
/// CHI PHÍ. Zalo OA tốn tiền thật (gói 1–6 triệu/năm) và hạn mức tính theo TỪNG OA — gói mua không
/// chuyển nhượng được — nên mỗi công ty phải tự khai OA của mình, vừa đúng về tiền vừa tránh chuyện
/// một công ty spam làm khoá OA của cả hệ thống. Telegram miễn phí và email dùng lại hộp thư
/// SmartMail đã cấu hình sẵn, nên KHÔNG bắt khai thêm.</para>
///
/// <para>Access token mã hoá Crypton, theo mẫu <c>MailAccountStore</c>. KHÔNG log token,
/// KHÔNG trả token về client.</para>
/// </summary>
public class TenantChannelSettingsStore
{
    public const string ZaloOa = "zalo-oa";

    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantChannelSettingsStore> _log;

    public TenantChannelSettingsStore(TourkitAiDb db, ILogger<TenantChannelSettingsStore> log)
    { _db = db; _log = log; }

    public record ZaloOaConfig(string OaId, string AccessToken);

    /// Đọc cấu hình OA Zalo của 1 công ty. Chưa khai / hỏng dữ liệu → null (kênh tự tắt).
    public async Task<ZaloOaConfig?> GetZaloConfigAsync(string tenantId, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var json = await c.ExecuteScalarAsync<string?>(
                "SELECT ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId = @tenantId AND Channel = @ch",
                new { tenantId, ch = ZaloOa });
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var oaId = doc.RootElement.TryGetProperty("oaId", out var o) ? o.GetString() : null;
            var enc  = doc.RootElement.TryGetProperty("accessTokenEnc", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(oaId) || string.IsNullOrWhiteSpace(enc)) return null;

            return new ZaloOaConfig(oaId!, Crypton.Decrypt(enc!));
        }
        catch (Exception ex)
        {
            // Nuốt có chủ đích: cấu hình kênh hỏng chỉ nên làm TẮT kênh đó, không được kéo sập cả
            // lượt gửi bản tin. Không log nội dung token.
            _log.LogWarning("[digest] đọc cấu hình Zalo OA của tenant={T} lỗi: {Err}", tenantId, ex.Message);
            return null;
        }
    }

    public async Task SaveZaloConfigAsync(string tenantId, string oaId, string accessToken, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { oaId, accessTokenEnc = Crypton.Encrypt(accessToken) });
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.TenantChannelSettings AS T
USING (SELECT @tenantId AS TenantId, @ch AS Channel) AS S
    ON T.TenantId = S.TenantId AND T.Channel = S.Channel
WHEN MATCHED THEN UPDATE SET ConfigJson = @json, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Channel, ConfigJson, UpdatedUtc)
     VALUES (@tenantId, @ch, @json, SYSUTCDATETIME());",
            new { tenantId, ch = ZaloOa, json });
        _log.LogInformation("[digest] tenant={T} đã lưu cấu hình Zalo OA (oaId={Oa})", tenantId, oaId);
    }

    public async Task<bool> RemoveZaloConfigAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var n = await c.ExecuteAsync(
            "DELETE FROM dbo.TenantChannelSettings WHERE TenantId = @tenantId AND Channel = @ch",
            new { tenantId, ch = ZaloOa });
        return n > 0;
    }
}
