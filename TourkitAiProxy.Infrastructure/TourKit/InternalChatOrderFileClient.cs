using System.Net.Http.Json;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.TourKit;

/// <summary>
/// Báo file vừa gửi trong phòng chat của đơn sang TourKit.Api để lưu vào tab Thảo luận của đơn.
/// ai-proxy không nối DB của từng công ty nên phải đi qua API, dùng chung khoá và công tắc với
/// <see cref="InternalChatPushClient"/>.
///
/// Lỗi chỉ ghi log, không ném ra: TourKit.Api chậm hay chết không được làm hỏng việc gửi tin.
/// </summary>
public sealed class InternalChatOrderFileClient
{
    private const string Path = "/api/internal-chat/order-file";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<InternalChatOrderFileClient> _log;
    private readonly string? _apiKey;

    public bool Available => _apiKey is { Length: > 0 };

    public InternalChatOrderFileClient(IHttpClientFactory factory, IConfiguration cfg,
        ILogger<InternalChatOrderFileClient> log)
    {
        _factory = factory;
        _log = log;

        var key = cfg["InternalChat:Notify:ApiKey"];
        if (!string.IsNullOrEmpty(key) && key.StartsWith("ENC:", StringComparison.Ordinal))
            key = Crypton.Decrypt(key.Substring(4));
        _apiKey = cfg.GetValue("InternalChat:Notify:Enabled", false) && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
    }

    /// <summary>Không nhận CancellationToken của request: token đó bị huỷ ngay khi tin nhắn trả về người gửi.</summary>
    public async Task SendAsync(string tenantId, long messageId, int tourType, int tourId, int userId,
        InternalAttachment attachment)
    {
        if (!Available) return;
        try
        {
            var payload = new { tenantId, tourId, tourType, userId, fileUrl = attachment.Url };
            var http = _factory.CreateClient("tourkit");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var msg = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(payload) };
            msg.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
            using var resp = await http.SendAsync(msg, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                _log.LogWarning("[chat-order-file] {Tenant} tin {Msg} đơn {Type}-{Id}: HTTP {Code} — {Body}",
                    tenantId, messageId, tourType, tourId, (int)resp.StatusCode, err.Length <= 300 ? err : err[..300]);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat-order-file] {Tenant} tin {Msg} đơn {Type}-{Id}: không lưu được file vào đơn",
                tenantId, messageId, tourType, tourId);
        }
    }
}
