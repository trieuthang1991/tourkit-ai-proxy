using System.Text.Json;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Widget;

/// <summary>
/// Liên kết widget với 1 TourKit session — admin paste Crypton token TourKit → decrypt → login →
/// lưu session.Id vào dbo.WidgetTokens.TourKitSessionId. Tái dùng TkSessionStore cho re-login + persist.
///
/// Cross-tenant guard: TourKit token decode ra phải khớp tenant của admin đang setup widget
/// (tránh admin tenant A bind widget với CRM của tenant B → leak data).
/// </summary>
public class WidgetCrmLinkService
{
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly WidgetTokenRepository _repo;
    private readonly ILogger<WidgetCrmLinkService> _log;

    public WidgetCrmLinkService(
        TkSessionStore sessions,
        TourKitApiClient api,
        WidgetTokenRepository repo,
        ILogger<WidgetCrmLinkService> log)
    {
        _sessions = sessions; _api = api; _repo = repo; _log = log;
    }

    public record LinkResult(bool Ok, string? SessionId, string? Tenant, string? Error);

    /// Decrypt Crypton token TourKit → login → trả về sessionId. KHÔNG save vào WidgetTokens (caller làm).
    /// Cross-tenant check: nếu adminTenantId set + token.domain ≠ adminTenantId → reject.
    public async Task<LinkResult> LoginFromTokenAsync(string cryptonToken, string adminTenantId, CancellationToken ct)
    {
        string plain;
        try { plain = Crypton.Decrypt(cryptonToken.Trim()); }
        catch { plain = ""; }
        if (string.IsNullOrWhiteSpace(plain))
            return new(false, null, null, "Token TourKit không hợp lệ hoặc giải mã thất bại");

        string? username, password, domain;
        try
        {
            using var doc = JsonDocument.Parse(plain);
            var r = doc.RootElement;
            username = Field(r, "username");
            password = Field(r, "password");
            domain   = Field(r, "domain") ?? Field(r, "tenantId");
        }
        catch
        {
            return new(false, null, null, "Nội dung token không phải JSON {username,password,domain}");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(domain))
            return new(false, null, null, "Token thiếu username/password/domain");

        // Cross-tenant guard: admin tenant A không được link CRM của tenant B.
        if (!string.IsNullOrEmpty(adminTenantId) &&
            !string.Equals(domain, adminTenantId, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("[Widget CRM] cross-tenant link bị chặn: admin={Admin} token.domain={Domain}", adminTenantId, domain);
            return new(false, null, null,
                $"Token này thuộc tenant '{domain}' không khớp tenant của bạn ('{adminTenantId}')");
        }

        try
        {
            var s = await _sessions.CreateAsync(domain!, username!, password!, ct);
            _log.LogInformation("[Widget CRM] linked session={SessionId} tenant={Tenant} user={User}",
                s.Id, s.TenantId, username);
            return new(true, s.Id, s.TenantId, null);
        }
        catch (TourKitApiException ex)
        {
            return new(false, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Widget CRM] login fail");
            return new(false, null, null, "Lỗi server khi login TourKit: " + ex.Message);
        }
    }

    /// Test: lấy 5 tour mẫu từ /api/ai/tours để xác nhận session đang gọi đúng tenant + JWT còn hiệu lực.
    public async Task<(bool ok, string? error, int count, List<string> titles)> TestCrmAsync(
        string sessionId, CancellationToken ct)
    {
        var s = _sessions.Get(sessionId);
        if (s == null) return (false, "Session không tồn tại — link CRM lại", 0, new());

        try
        {
            var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
            var data = await _api.GetAsync(jwt, "/api/ai/tours?pageIndex=1&pageSize=5", ct);
            var titles = new List<string>();
            int count = 0;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                count = items.GetArrayLength();
                foreach (var it in items.EnumerateArray().Take(5))
                {
                    var name = TryStr(it, "tourName") ?? TryStr(it, "name") ?? TryStr(it, "title");
                    if (!string.IsNullOrEmpty(name)) titles.Add(name!);
                }
            }
            return (true, null, count, titles);
        }
        catch (TourKitApiException ex)
        {
            return (false, ex.Message, 0, new());
        }
        catch (Exception ex)
        {
            return (false, "Test CRM lỗi: " + ex.Message, 0, new());
        }
    }

    private static string? Field(JsonElement r, string name)
        => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? TryStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
