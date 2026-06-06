using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TourkitAiProxy.Services.TourKit;

/// Lỗi khi gọi TourKit.Api (login sai, endpoint trả success=false, HTTP lỗi…).
public class TourKitApiException : Exception
{
    public int Status { get; }
    public TourKitApiException(string message, int status = 502) : base(message) => Status = status;
}

/// Kết quả login: JWT + thông tin hiển thị.
public record TkLoginResult(string Token, string? FullName, string? CompanyName);

/// <summary>
/// Client gọi TourKit.Api (toutkit-app). Response chuẩn bọc { success, data, message, totalCount }
/// (xem TourKit.Common/MResponse.cs) — client tự unwrap, throw TourKitApiException nếu success=false.
///
/// BaseUrl đọc từ config "TourKit:BaseUrl" (mặc định Production https://mobile-api.tourkit.vn).
/// Auth: JWT Bearer lấy từ POST /api/auth/login.
/// </summary>
public class TourKitApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<TourKitApiClient> _log;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TourKitApiClient(IHttpClientFactory factory, ILogger<TourKitApiClient> log)
    {
        _factory = factory; _log = log;
    }

    /// POST /api/auth/login — body {tenantId, username, password}. Trả JWT.
    public async Task<TkLoginResult> LoginAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        var http = _factory.CreateClient("tourkit");
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("/api/auth/login",
                new { tenantId, username, password }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TourKitApiException($"Không kết nối được TourKit.Api ({ex.Message})", 502);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = SafeParse(body);
        var root = doc?.RootElement;

        if (!resp.IsSuccessStatusCode || root is null || !GetBool(root.Value, "success"))
        {
            var msg = root is not null ? GetString(root.Value, "message") : null;
            throw new TourKitApiException(
                msg ?? $"Đăng nhập TourKit thất bại (HTTP {(int)resp.StatusCode})",
                resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? 401 : 502);
        }

        if (!root.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new TourKitApiException("Login TourKit trả về thiếu data", 502);

        var token = GetString(data, "token");
        if (string.IsNullOrEmpty(token))
            throw new TourKitApiException("Login TourKit không trả về token", 502);

        return new TkLoginResult(token, GetString(data, "fullName"), GetString(data, "companyName"));
    }

    /// GET {pathAndQuery} với Bearer JWT. Trả về phần `data` (đã Clone — an toàn sau dispose).
    public async Task<JsonElement> GetAsync(string jwt, string pathAndQuery, CancellationToken ct)
    {
        var http = _factory.CreateClient("tourkit");
        using var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            throw new TourKitApiException($"Không kết nối được TourKit.Api ({ex.Message})", 502);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new TourKitApiException("Phiên TourKit hết hạn hoặc không hợp lệ", 401);

        using var doc = SafeParse(body)
            ?? throw new TourKitApiException($"TourKit trả về không phải JSON (HTTP {(int)resp.StatusCode})", 502);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode || !GetBool(root, "success"))
            throw new TourKitApiException(
                GetString(root, "message") ?? $"TourKit lỗi (HTTP {(int)resp.StatusCode})", 502);

        if (root.TryGetProperty("data", out var data))
            return data.Clone();
        return default;
    }

    // ─── helpers ────────────────────────────────────────────────────────────────
    private static JsonDocument? SafeParse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonDocument.Parse(s); } catch { return null; }
    }

    private static bool GetBool(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p)
           && (p.ValueKind == JsonValueKind.True);

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
