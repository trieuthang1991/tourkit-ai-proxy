using System.Diagnostics;
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

    /// BaseUrl đang dùng (vd "https://mobile-test-api-2.tourkit.vn"). Phục vụ trace/debug, KHÔNG có JWT.
    public string BaseUrl => _factory.CreateClient("tourkit").BaseAddress?.ToString().TrimEnd('/') ?? "";

    /// POST /api/auth/login — body {tenantId, username, password}. Trả JWT.
    public async Task<TkLoginResult> LoginAsync(string tenantId, string username, string password, CancellationToken ct)
    {
        var http = _factory.CreateClient("tourkit");
        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("/api/auth/login",
                new { tenantId, username, password }, ct);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "[TourKit] LOGIN tenant={T} user={U} ({Ms}ms) không kết nối được upstream: {Err}",
                tenantId, username, sw.ElapsedMilliseconds, ex.Message);
            throw new TourKitApiException("Không kết nối được hệ thống. Vui lòng thử lại sau.", 502);
        }
        sw.Stop();

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = SafeParse(body);
        var root = doc?.RootElement;

        if (!resp.IsSuccessStatusCode || root is null || !GetBool(root.Value, "success"))
        {
            var msg = root is not null ? GetString(root.Value, "message") : null;
            _log.LogWarning("[TourKit] LOGIN FAIL tenant={T} user={U} HTTP={H} ({Ms}ms): {Msg}",
                tenantId, username, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg ?? "(no message)");
            throw new TourKitApiException(
                msg ?? $"Đăng nhập TourKit thất bại (HTTP {(int)resp.StatusCode})",
                resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? 401 : 502);
        }

        if (!root.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new TourKitApiException("Login TourKit trả về thiếu data", 502);

        var token = GetString(data, "token");
        if (string.IsNullOrEmpty(token))
            throw new TourKitApiException("Login TourKit không trả về token", 502);

        _log.LogInformation("[TourKit] LOGIN OK tenant={T} user={U} ({Ms}ms)",
            tenantId, username, sw.ElapsedMilliseconds);
        return new TkLoginResult(token, GetString(data, "fullName"), GetString(data, "companyName"));
    }

    /// GET {pathAndQuery} với Bearer JWT. Trả về phần `data` (đã Clone — an toàn sau dispose).
    public async Task<JsonElement> GetAsync(string jwt, string pathAndQuery, CancellationToken ct)
    {
        var http = _factory.CreateClient("tourkit");
        using var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "[TourKit] GET {Path} ({Ms}ms) không kết nối được upstream: {Err}",
                pathAndQuery, sw.ElapsedMilliseconds, ex.Message);
            throw new TourKitApiException("Không kết nối được hệ thống. Vui lòng thử lại sau.", 502);
        }
        sw.Stop();

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("[TourKit] GET {Path} → 401 ({Ms}ms) — JWT hết hạn/không hợp lệ",
                pathAndQuery, sw.ElapsedMilliseconds);
            throw new TourKitApiException("Phiên TourKit hết hạn hoặc không hợp lệ", 401);
        }

        using var doc = SafeParse(body)
            ?? throw new TourKitApiException($"TourKit trả về không phải JSON (HTTP {(int)resp.StatusCode})", 502);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode || !GetBool(root, "success"))
        {
            var msg = GetString(root, "message");
            _log.LogWarning("[TourKit] GET {Path} → HTTP {H} ({Ms}ms) success=false: {Msg}",
                pathAndQuery, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg ?? "(no message)");
            // Propagate mã HTTP THẬT khi upstream trả non-2xx (vd 403/404) — để caller như
            // GetPermissionsAsync phân biệt "từ chối dứt khoát" (401/403) vs transient. Riêng
            // 2xx-nhưng-success=false (envelope lỗi logic) giữ 502 (200 không phải mã lỗi hợp lệ để trả lại).
            var status = !resp.IsSuccessStatusCode ? (int)resp.StatusCode : 502;
            throw new TourKitApiException(msg ?? $"TourKit lỗi (HTTP {(int)resp.StatusCode})", status);
        }

        _log.LogDebug("[TourKit] GET {Path} → 200 ({Ms}ms) bytes={Len}",
            pathAndQuery, sw.ElapsedMilliseconds, body.Length);
        if (root.TryGetProperty("data", out var data))
            return data.Clone();
        return default;
    }

    /// GET /api/auth/permissions (Bearer JWT) → mã Function_Code của phòng ban user.
    /// Trả:
    ///   • non-null (kể cả LIST RỖNG) = LẤY THÀNH CÔNG — rỗng nghĩa là user thật sự không có quyền nào.
    ///   • null = LẤY LỖI (transient/upstream down) sau khi retry — caller PHẢI thử lại lần sau,
    ///            KHÔNG được cache rỗng như thể authoritative (nếu không sẽ khoá nhầm user vì 1 blip).
    /// Gate xử lý: rỗng/thiếu quyền → chặn (fail-closed); null → giữ trạng thái "chưa loaded" để tự lấy lại.
    public async Task<List<string>?> GetPermissionsAsync(string jwt, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var data = await GetAsync(jwt, "/api/auth/permissions", ct);
                return ParsePermissions(data);   // 200 OK — kể cả mảng rỗng = thành công
            }
            catch (TourKitApiException ex) when (ex.Status is 401 or 403)
            {
                // 401/403 = câu trả lời DỨT KHOÁT của upstream (token/không quyền truy cập endpoint),
                // không phải transient → coi như "không có quyền", KHÔNG retry.
                _log.LogWarning("[TourKit] GetPermissions {Status} — coi như không có quyền", ex.Status);
                return new List<string>();
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning("[TourKit] GetPermissions lỗi (lần {N}/{Max}) — retry: {Err}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(200 * attempt, ct);   // backoff 200ms, 400ms
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TourKit] GetPermissions cạn retry — trả null (sẽ lấy lại sau)");
                return null;
            }
        }
        return null;
    }

    /// Pure: rút mảng `permissions` (camelCase hoặc PascalCase) từ envelope → List trimmed, bỏ rỗng.
    public static List<string> ParsePermissions(JsonElement data)
    {
        var list = new List<string>();
        if (data.ValueKind != JsonValueKind.Object) return list;
        if (!data.TryGetProperty("permissions", out var arr) &&
            !data.TryGetProperty("Permissions", out arr)) return list;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
        return list;
    }

    /// POST {pathAndQuery} với Bearer JWT + body JSON. Trả `data` (Clone). Throw TourKitApiException
    /// nếu success=false / non-2xx (message từ MResponse.Fail bên TourKit.Api được surface nguyên văn).
    public async Task<JsonElement> PostAsync(string jwt, string pathAndQuery, object body, CancellationToken ct)
    {
        var http = _factory.CreateClient("tourkit");
        using var req = new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "[TourKit] POST {Path} ({Ms}ms) không kết nối được upstream: {Err}",
                pathAndQuery, sw.ElapsedMilliseconds, ex.Message);
            throw new TourKitApiException("Không kết nối được hệ thống. Vui lòng thử lại sau.", 502);
        }
        sw.Stop();

        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("[TourKit] POST {Path} → 401 ({Ms}ms) — JWT hết hạn/không hợp lệ",
                pathAndQuery, sw.ElapsedMilliseconds);
            throw new TourKitApiException("Phiên TourKit hết hạn hoặc không hợp lệ", 401);
        }

        using var doc = SafeParse(respBody)
            ?? throw new TourKitApiException($"TourKit trả về không phải JSON (HTTP {(int)resp.StatusCode})", 502);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode || !GetBool(root, "success"))
        {
            var msg = GetString(root, "message");
            _log.LogWarning("[TourKit] POST {Path} → HTTP {H} ({Ms}ms) success=false: {Msg}",
                pathAndQuery, (int)resp.StatusCode, sw.ElapsedMilliseconds, msg ?? "(no message)");
            throw new TourKitApiException(msg ?? $"TourKit lỗi (HTTP {(int)resp.StatusCode})",
                resp.StatusCode == System.Net.HttpStatusCode.BadRequest ? 400 : 502);
        }

        _log.LogDebug("[TourKit] POST {Path} → 200 ({Ms}ms) bytes={Len}",
            pathAndQuery, sw.ElapsedMilliseconds, respBody.Length);
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
