using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Cấu hình kênh gửi CỦA CÔNG TY (<c>dbo.TenantChannelSettings</c>) — khác hoàn toàn "nơi nhận của
/// tôi": đây là TÀI KHOẢN GỬI ĐI, quản trị khai một lần cho cả công ty.
///
/// <para><b>Vì sao quay lại làm per-tenant</b> (bản 14/08 từng gỡ để dùng OA chung): đi gặp khách
/// hàng 17/08 thì <b>không công ty nào chịu dùng OA chung</b> — tin Zalo hiện tên OA của người gửi,
/// nên gửi bằng OA của bên cung cấp dịch vụ nghĩa là khách của họ thấy tên người khác. Giả định
/// "gom về một mối cho tiện" của bản trước sai ngay từ gốc.</para>
///
/// <para><b>KHÔNG có đường rơi ngầm về OA chung.</b> Chưa khai gì thì kênh Zalo coi như chưa dùng
/// được, và nói thẳng ra. Công ty nào dùng OA của bên cung cấp thì vẫn phải nhập <b>khoá được
/// cấp</b> — tức mọi trường hợp đều có thao tác khai báo rõ ràng, không có trạng thái "im lặng gửi
/// bằng danh nghĩa người khác".</para>
///
/// <para><b>Mẫu ZNS khai theo TỪNG CHỨC NĂNG</b> (<c>templates</c>): Zalo duyệt mẫu theo nội dung,
/// nên bản tin sáng và cảnh báo tiền là hai mẫu khác nhau — dùng chung một mã mẫu thì Zalo từ chối,
/// hoặc tệ hơn là gửi được nhưng nội dung nói sai chuyện.</para>
///
/// <para><b>⚠️ Hợp nhất khi lưu, KHÔNG ghi đè cả cục.</b> Cột <c>ConfigJson</c> có hai chủ: phần khai
/// tay do người dùng nhập ở giao diện, còn <c>refreshToken</c>/<c>accessToken</c> do WORKER xoay vòng
/// và ghi lại sau mỗi lần làm mới. Ghi đè trọn ConfigJson từ giao diện sẽ xoá mất token worker vừa
/// làm mới → kênh Zalo chết ngay sau lần lưu cấu hình kế tiếp, mà không lỗi nào hiện lên.</para>
/// </summary>
public class TenantChannelSettingsStore
{
    public const string ChannelZalo = "zalo";

    /// <summary>
    /// Những khoá do WORKER làm chủ — giao diện KHÔNG được đụng tới. Tên phải khớp đúng
    /// <c>ZaloTokenStore</c> bên toutkit-app; lệch tên là proxy xoá mất token worker vừa làm mới.
    /// </summary>
    private static readonly string[] WorkerOwnedKeys = { "accessTokenEnc", "refreshTokenEnc", "refreshedUtc" };

    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantChannelSettingsStore> _log;

    public TenantChannelSettingsStore(TourkitAiDb db, ILogger<TenantChannelSettingsStore> log)
    { _db = db; _log = log; }

    /// Hai cách dùng Zalo, KHÔNG có cách thứ ba (không có "để trống cho hệ thống tự lo").
    public const string ModeOwnOa = "own";        // OA riêng của công ty
    public const string ModeProvided = "provided"; // dùng OA của bên cung cấp, bằng khoá được cấp

    /// Cấu hình Zalo của công ty. Bí mật đã giải mã — CHỈ dùng nội bộ, KHÔNG trả ra client.
    public record ZaloConfig(string Mode, string? OaId, string? AppId, string? SecretKey,
        string? RefreshTokenSeed, string? ProvisionKey, IReadOnlyDictionary<string, string> Templates)
    {
        /// <summary>
        /// Đã khai đủ để gửi được chưa. Thiếu thì kênh Zalo KHÔNG bật được — và phải nói ra,
        /// tuyệt đối không lặng lẽ gửi bằng OA của người khác.
        /// <para><b>OA riêng cần CẢ refresh token ban đầu.</b> App ID + Secret không đủ: Zalo cấp
        /// access token bằng cách đổi refresh token, và refresh token đầu tiên chỉ lấy được qua
        /// bước cấp quyền OA trên trang Zalo. Bỏ sót ô này thì công ty khai xong tưởng đã chạy, mà
        /// worker không bao giờ lấy nổi token.</para>
        /// </summary>
        public bool IsUsable => Mode == ModeOwnOa
            ? !string.IsNullOrWhiteSpace(OaId) && !string.IsNullOrWhiteSpace(AppId)
              && !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(RefreshTokenSeed)
            : !string.IsNullOrWhiteSpace(ProvisionKey);

        /// Mã mẫu ZNS cho một chức năng (vd "sale-brief"). Chưa khai → null.
        public string? TemplateFor(string feature)
            => Templates.TryGetValue(feature, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    public async Task<ZaloConfig?> GetZaloAsync(string tenantId, CancellationToken ct = default)
    {
        var json = await ReadRawAsync(tenantId, ChannelZalo, ct);
        if (json == null) return null;
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            if (o == null) return null;
            var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (o["templates"] is JsonObject t)
                foreach (var kv in t)
                    if (kv.Value is not null) templates[kv.Key] = kv.Value.ToString();

            return new ZaloConfig(
                Str(o, "mode") ?? ModeOwnOa,
                Str(o, "oaId"), Str(o, "appId"),
                // Bí mật lưu mã hoá Crypton như mọi thông tin đăng nhập khác trong repo này.
                Crypton.Decrypt(Str(o, "secretKeyEnc") ?? ""),
                // Hạt giống refresh token: worker chỉ đọc khi trong DB chưa có token nào.
                Crypton.Decrypt(Str(o, "refreshTokenSeedEnc") ?? ""),
                Crypton.Decrypt(Str(o, "provisionKeyEnc") ?? ""),
                templates);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantChannel] đọc cấu hình Zalo hỏng, tenant={T}", tenantId);
            return null;
        }
    }

    /// <summary>
    /// Lưu phần khai tay, GIỮ NGUYÊN các khoá của worker. <paramref name="secretKey"/> để null =
    /// không đổi bí mật đang lưu (giao diện không đọc lại được bí mật nên không thể gửi lại).
    /// </summary>
    public async Task SaveZaloAsync(string tenantId, string mode, string? oaId, string? appId,
        string? secretKey, string? refreshTokenSeed, string? provisionKey,
        IReadOnlyDictionary<string, string> templates, CancellationToken ct = default)
    {
        var existing = await ReadRawAsync(tenantId, ChannelZalo, ct);
        var o = (existing != null ? JsonNode.Parse(existing)?.AsObject() : null) ?? new JsonObject();

        o["mode"] = mode == ModeProvided ? ModeProvided : ModeOwnOa;
        o["oaId"] = oaId?.Trim();
        o["appId"] = appId?.Trim();
        if (!string.IsNullOrWhiteSpace(secretKey))
            o["secretKeyEnc"] = Crypton.Encrypt(secretKey.Trim());
        if (!string.IsNullOrWhiteSpace(refreshTokenSeed))
            o["refreshTokenSeedEnc"] = Crypton.Encrypt(refreshTokenSeed.Trim());
        if (!string.IsNullOrWhiteSpace(provisionKey))
            o["provisionKeyEnc"] = Crypton.Encrypt(provisionKey.Trim());

        var t = new JsonObject();
        foreach (var kv in templates)
            if (!string.IsNullOrWhiteSpace(kv.Value)) t[kv.Key] = kv.Value.Trim();
        o["templates"] = t;

        // Khoá của worker: đã có sẵn trong `o` vì ta parse từ bản cũ và chỉ ghi đè khoá của mình.
        // Liệt kê ra đây để người sau biết chúng tồn tại và ĐỪNG xoá.
        _ = WorkerOwnedKeys;

        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.TenantChannelSettings AS T
USING (SELECT @tenantId AS TenantId, @channel AS Channel) AS S
    ON T.TenantId = S.TenantId AND T.Channel = S.Channel
WHEN MATCHED THEN UPDATE SET ConfigJson = @json, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Channel, ConfigJson, UpdatedUtc)
VALUES (@tenantId, @channel, @json, SYSUTCDATETIME());",
            new { tenantId, channel = ChannelZalo, json = o.ToJsonString() });
    }

    /// Xoá cấu hình → công ty quay về dùng OA chung.
    public async Task<bool> DeleteZaloAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var n = await c.ExecuteAsync(
            "DELETE FROM dbo.TenantChannelSettings WHERE TenantId = @tenantId AND Channel = @channel",
            new { tenantId, channel = ChannelZalo });
        return n > 0;
    }

    private async Task<string?> ReadRawAsync(string tenantId, string channel, CancellationToken ct)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            return await c.ExecuteScalarAsync<string?>(
                "SELECT ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId = @tenantId AND Channel = @channel",
                new { tenantId, channel });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantChannel] đọc DB lỗi tenant={T} channel={C}", tenantId, channel);
            return null;
        }
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
