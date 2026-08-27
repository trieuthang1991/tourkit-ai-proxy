// Services/Chat/Channels/ChannelCredentialStore.cs
using System.Text.Json.Nodes;
using Dapper;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Db;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.Chat.Channels;

/// <summary>
/// Khoá kết nối kênh chat, theo từng công ty — và theo từng TÀI KHOẢN (một công ty du lịch có thể
/// có nhiều Trang Facebook đại lý các chi nhánh, nhiều OA Zalo, nhiều bot Telegram cho từng đội
/// sale; ép về một tài khoản/kênh là sai với thực tế vận hành).
///
/// <para><b>Vẫn dùng chung bảng</b> <c>dbo.TenantChannelSettings</c> — cột <c>Channel</c> nay
/// mang dạng <c>"{prefix}:{accountId}"</c> (vd <c>"telegram:a1b2c3d4"</c>) thay vì chỉ tên kênh
/// trần. Không đẻ bảng mới cho việc bảng cũ đã tổng quát đủ (TenantId, Channel, ConfigJson).</para>
///
/// <para><b>Mọi giá trị đều mã hoá</b> (Crypton, hậu tố khoá <c>Enc</c>). Kể cả thứ không thật sự
/// bí mật như id trang: mã hoá tất cho đỡ phải nhớ cái nào cần cái nào không.</para>
///
/// <para><b>KHÔNG đụng kênh <c>zalo</c> trần</b> (không có hậu tố accountId) — bản ghi đó do
/// <see cref="Digest.TenantChannelSettingsStore"/> làm chủ cho BẢN TIN SÁNG, có khoá worker xoay
/// vòng riêng. Zalo của CHAT dùng tiền tố <c>chat-zalo</c> (xem <see cref="KeyOf"/>) — độc lập
/// hoàn toàn, mỗi bên tự quản access token của mình, không đọc/ghi chéo.</para>
/// </summary>
public class ChannelCredentialStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<ChannelCredentialStore> _log;

    public ChannelCredentialStore(TourkitAiDb db, ILogger<ChannelCredentialStore> log)
    { _db = db; _log = log; }

    /// Tiền tố bản ghi cho từng kênh chat. Zalo cố ý KHÔNG trả "zalo" trần — xem docstring lớp.
    public static string KeyOf(ChatChannel kenh) => kenh switch
    {
        ChatChannel.Zalo => "chat-zalo",
        ChatChannel.Messenger => "messenger",
        ChatChannel.Telegram => "telegram",
        _ => "chat-" + kenh.ToString().ToLowerInvariant(),
    };

    /// <summary>Danh sách mọi tài khoản đã khai cho một kênh, của một công ty.</summary>
    public async Task<List<ChatAccount>> ListAccountsAsync(string tenantId, ChatChannel kenh,
        CancellationToken ct = default)
    {
        var tienTo = KeyOf(kenh) + ":";
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var hang = await c.QueryAsync<(string Channel, string ConfigJson)>(
                "SELECT Channel, ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId=@t AND Channel LIKE @p",
                new { t = tenantId, p = tienTo + "%" });

            return hang.Select(h => new ChatAccount(h.Channel[tienTo.Length..], Decode(h.ConfigJson)))
                       .OrderBy(a => a.GiaTri.GetValueOrDefault("label", a.AccountId))
                       .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/cred] đọc danh sách tài khoản kênh {K} hỏng, tenant={T}", kenh, tenantId);
            return new List<ChatAccount>();
        }
    }

    /// <summary>
    /// Tài khoản này thuộc công ty nào. Dùng cho webhook DÙNG CHUNG: khi TourKit sở hữu một ứng
    /// dụng Zalo cho mọi khách hàng thì <c>app_id</c> giống hệt nhau ở mọi công ty, nên không còn
    /// phân biệt được bằng nó nữa — phải tra ngược từ id của OA.
    ///
    /// <para>Rẻ vì <c>accountId</c> của luồng kết nối mới CHÍNH LÀ id OA, nên đây là một phép so
    /// bằng trên cột <c>Channel</c>, không phải quét rồi giải mã từng dòng.</para>
    ///
    /// <para>Hai công ty cùng nối một OA là chuyện bất thường nhưng có thể xảy ra (một doanh
    /// nghiệp mở hai tenant). Lấy dòng đầu và <b>ghi cảnh báo</b> — im lặng thì tin của khách rơi
    /// vào công ty nào là hên xui, mà không ai biết để mà sửa.</para>
    /// </summary>
    public async Task<string?> FindTenantAsync(ChatChannel kenh, string accountId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var ds = (await c.QueryAsync<string>(
                "SELECT TenantId FROM dbo.TenantChannelSettings WHERE Channel=@c",
                new { c = KeyOf(kenh) + ":" + accountId })).ToList();

            if (ds.Count == 0) return null;
            if (ds.Count > 1)
                _log.LogWarning("[chat/cred] tài khoản {A} kênh {K} khai ở {N} công ty ({DS}) — lấy cái đầu",
                    accountId, kenh, ds.Count, string.Join(", ", ds));
            return ds[0];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/cred] tra công ty theo tài khoản {A} kênh {K} hỏng", accountId, kenh);
            return null;
        }
    }

    /// Đọc khoá đã giải mã của MỘT tài khoản. Chưa khai → null.
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(string tenantId, ChatChannel kenh,
        string accountId, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var json = await c.ExecuteScalarAsync<string?>(
                "SELECT ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId=@t AND Channel=@c",
                new { t = tenantId, c = KeyOf(kenh) + ":" + accountId });
            return string.IsNullOrWhiteSpace(json) ? null : Decode(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/cred] đọc khoá kênh {K} tài khoản {A} hỏng, tenant={T}", kenh, accountId, tenantId);
            return null;
        }
    }

    /// <summary>
    /// Lưu khoá cho MỘT tài khoản. Giá trị rỗng = <b>giữ nguyên</b> giá trị đang có, không xoá —
    /// giao diện không đọc lại được bí mật nên không thể gửi lại; ghi đè cả cục sẽ xoá mất
    /// access/refresh token mà chính adapter vừa làm mới lúc gửi tin trước đó.
    /// </summary>
    public async Task SaveAsync(string tenantId, ChatChannel kenh, string accountId,
        IDictionary<string, string?> giaTri, CancellationToken ct = default)
    {
        var hienCo = await GetAsync(tenantId, kenh, accountId, ct) ?? new Dictionary<string, string>();
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
            """, new { tenantId, channel = KeyOf(kenh) + ":" + accountId, json = o.ToJsonString() });
    }

    public async Task<bool> DeleteAsync(string tenantId, ChatChannel kenh, string accountId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM dbo.TenantChannelSettings WHERE TenantId=@t AND Channel=@c",
            new { t = tenantId, c = KeyOf(kenh) + ":" + accountId }) > 0;
    }

    private static Dictionary<string, string> Decode(string json)
    {
        var ra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (JsonNode.Parse(json)?.AsObject() is not { } o) return ra;
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
}

/// <param name="AccountId">Mã ngắn máy chủ tự sinh lúc tạo (8 ký tự hex) — KHÔNG phải id do
/// người dùng đặt tay, để khỏi phải lo trùng/ký tự lạ trong URL webhook.</param>
/// <param name="GiaTri">Khoá đã giải mã, gồm cả <c>label</c> (tên hiển thị người dùng đặt).</param>
public record ChatAccount(string AccountId, IReadOnlyDictionary<string, string> GiaTri);
