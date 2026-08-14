using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Digest.Channels;

/// <summary>
/// Kênh Zalo OA — gửi tin tư vấn tới <c>ZaloUserId</c> qua OA của CHÍNH công ty đó.
///
/// <para><b>Per-tenant, không dùng OA chung.</b> Hạn mức Zalo tính theo từng OA và gói mua không
/// chuyển nhượng được, nên OA chung vừa sai về tiền vừa nguy hiểm: một công ty spam là cả hệ thống
/// bị khoá. Cấu hình lấy từ <c>dbo.TenantChannelSettings</c>.</para>
///
/// <para><b>Ràng buộc phải nói thẳng với người dùng:</b> tin tư vấn chỉ gửi được trong CỬA SỔ 48 GIỜ
/// kể từ lần người đó nhắn cho OA. Bản tin hằng ngày <b>sẽ hỏng ngoài cửa sổ đó</b> — đây là giới hạn
/// của Zalo, không phải lỗi mình. Gửi ổn định cần ZNS (mất phí + duyệt mẫu), để đợt sau.
/// Vì vậy kênh này là "gửi được thì tốt", hỏng chỉ ghi Warning, KHÔNG làm hỏng lượt chạy.</para>
/// </summary>
public class ZaloOaChannel : IDigestChannel
{
    private const string SendMessageUrl = "https://openapi.zalo.me/v3.0/oa/message/cs";

    private readonly IHttpClientFactory _http;
    private readonly TenantChannelSettingsStore _settings;
    private readonly ILogger<ZaloOaChannel> _log;

    public ZaloOaChannel(IHttpClientFactory http, TenantChannelSettingsStore settings, ILogger<ZaloOaChannel> log)
    { _http = http; _settings = settings; _log = log; }

    public string Id => "zalo";

    /// Chỉ kiểm phần rẻ (đã bật + có user id). Cấu hình OA phải đọc DB nên để trong SendAsync —
    /// IsConfigured chạy đồng bộ cho mọi người nhận, không nên đụng DB ở đó.
    public bool IsConfigured(DigestSubscription sub)
        => sub.ChannelZalo && !string.IsNullOrWhiteSpace(sub.ZaloUserId);

    public Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
        => SendToUserAsync(sub.TenantId, sub.ZaloUserId ?? "", m.Title, m.BodyMarkdown, sub.Username, ct);

    /// <summary>
    /// Gửi lõi — chỉ cần nơi nhận + nội dung, KHÔNG cần bản đăng ký. Tách ra để hàng đợi
    /// hàng đợi gửi được (worker bên toutkit-app). Token OA vẫn resolve NGAY LÚC GỬI
    /// (không lưu vào dòng hàng đợi) — token có hạn và công ty đổi được bất cứ lúc nào.
    /// </summary>
    public async Task<bool> SendToUserAsync(string tenantId, string zaloUserId, string title,
        string? bodyMd, string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zaloUserId))
        {
            _log.LogWarning("[digest/zalo] thiếu Zalo user id tenant={T} user={U} — bỏ qua", tenantId, username);
            return false;
        }

        try
        {
            var cfg = await _settings.GetZaloConfigAsync(tenantId, ct);
            if (cfg == null)
            {
                _log.LogWarning("[digest/zalo] tenant={T} chưa khai OA Zalo — bỏ qua kênh này", tenantId);
                return false;
            }

            // Zalo không nhận HTML; gửi text thuần, bỏ ** ** của markdown.
            var text = $"{title}\n\n{bodyMd?.Replace("**", "")}";
            if (text.Length > 2000) text = text[..1997] + "…";

            var body = JsonSerializer.Serialize(new
            {
                recipient = new { user_id = zaloUserId },
                message = new { text },
            });

            var c = _http.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post, SendMessageUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("access_token", cfg.AccessToken);

            using var resp = await c.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            // Zalo trả HTTP 200 kèm error != 0 khi hỏng — KHÔNG được tin mỗi status code.
            var errCode = 0;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Number)
                    errCode = e.GetInt32();
            }
            catch { /* body không phải JSON → coi như hỏng, đã có status bên dưới */ }

            if (resp.IsSuccessStatusCode && errCode == 0) return true;

            _log.LogWarning("[digest/zalo] gửi hỏng http={Status} error={Err} tenant={T} user={U}: {Body}",
                (int)resp.StatusCode, errCode, tenantId, username, raw.Length > 300 ? raw[..300] : raw);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[digest/zalo] lỗi mạng tenant={T} user={U}", tenantId, username);
            return false;
        }
    }
}
