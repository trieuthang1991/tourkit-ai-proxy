// Services/Chat/CrmToolRunner.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Services.Chat;

/// <summary>Một lệnh gọi tool CRM đã quy đổi tên → mã và dựng đường dẫn.</summary>
public sealed record CrmToolCall(ChatTool Tool, string Path, JsonElement? ResolvedParams);

/// <summary>
/// Báo hiệu: lần gọi đầu 401, đã đăng nhập lại, mà lần thử lại cũng hỏng (mọi loại lỗi). Chỉ tín hiệu này mới
/// được ghi upstream_persistent_error — lỗi 400/404/500 ở lần đầu vẫn ném nguyên, không ghi.
/// </summary>
public sealed class CrmRetryFailedException : Exception
{
    public CrmRetryFailedException(Exception inner) : base(inner.Message, inner) { }
}

/// <summary>Kết quả một lệnh gọi tool CRM: dữ liệu thô + khung hiển thị đã dựng.</summary>
public sealed record CrmToolOutcome(ChatTool Tool, string Path, JsonElement Data, ChatData View, bool FromCache);

/// <summary>
/// Đường chạy tool CRM dùng chung cho trợ lý (NativeToolUseAgent, JsonPlannerAgent) và MCP:
/// quy đổi tên → mã (thị trường, nhân viên) → dựng đường dẫn → đệm "d|" theo người dùng → gọi API (401 thì đăng nhập lại
/// đúng một lần) → dựng ChatData. Rút ra từ JsonPlannerAgent để mọi lối vào cùng một hành vi — trước đây
/// chỉ JsonPlannerAgent đổi được tên, nên cùng câu hỏi mà đường Anthropic không lọc theo thị trường.
/// </summary>
public class CrmToolRunner
{
    private readonly TourKitApiClient? _api;
    private readonly TkSessionStore? _sessions;
    private readonly ChatCache? _cache;
    private readonly ILogger<CrmToolRunner>? _log;
    private readonly Func<Task<List<(int Id, string Name)>>>? _marketsOverride;   // seam cho test
    private readonly Func<bool, string, Task<JsonElement>>? _fetchOverride;        // seam cho test: (đã re-login?, path)

    // Dữ liệu CRM đệm 30 phút theo (tenant|người dùng|đường dẫn): key xác định trọn vẹn kết quả.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    // Danh mục thị trường/nhân viên theo tenant — giữ trong RAM, đổi chậm nên TTL dài.
    private readonly ConcurrentDictionary<string, (List<(int Id, string Name)> List, DateTime Exp)> _markets = new();
    private readonly ConcurrentDictionary<string, (List<(int Id, string Name)> List, DateTime Exp)> _employees = new();
    private static readonly TimeSpan MarketTtl = TimeSpan.FromHours(6);

    public CrmToolRunner(TourKitApiClient api, TkSessionStore sessions, ChatCache cache, ILogger<CrmToolRunner> log)
    {
        _api = api; _sessions = sessions; _cache = cache; _log = log;
    }

    private CrmToolRunner(Func<Task<List<(int Id, string Name)>>> markets, Func<bool, string, Task<JsonElement>>? fetch = null)
    { _marketsOverride = markets; _fetchOverride = fetch; }

    /// <summary>Chỉ cho test: runner không chạm TourKit, danh sách thị trường lấy từ <paramref name="markets"/>.</summary>
    internal static CrmToolRunner ForTest(Func<Task<List<(int Id, string Name)>>> markets) => new(markets);

    /// <summary>Chỉ cho test: thay lần gọi API (không đệm, không phiên) để kiểm luồng 401 → thử lại.</summary>
    internal static CrmToolRunner ForTest(Func<bool, string, Task<JsonElement>> fetch)
        => new(() => Task.FromResult(new List<(int, string)>()), fetch);

    // ─── Chạy một tool ───────────────────────────────────────────────────────────

    public async Task<CrmToolOutcome> RunAsync(string sessionId, string tenantId, ChatTool tool,
        JsonElement? prms, CancellationToken ct)
    {
        prms = await ResolveNamesAsync(sessionId, prms, ct);
        var path = ChatTools.BuildPath(tool, prms);
        // Báo cáo CRM theo quyền người gọi → khoá đệm gồm cả người dùng; không rõ người dùng thì không đệm.
        var cacheKey = _fetchOverride == null ? DataCacheKey(tenantId, _sessions!.Get(sessionId)?.Username, path) : null;

        if (cacheKey != null && _cache!.TryGet<JsonElement>(cacheKey, out var cached))
            return new CrmToolOutcome(tool, path, cached, ChatDataBuilder.Build(tool, cached), true);

        var data = await FetchAsync(sessionId, path, ct);
        if (cacheKey != null && IsUsableData(data)) _cache!.Set(cacheKey, data, CacheTtl);   // không đệm kết quả rỗng
        return new CrmToolOutcome(tool, path, data, ChatDataBuilder.Build(tool, data), false);
    }

    /// <summary>
    /// Khoá đệm dữ liệu CRM: tenant + người dùng + đường dẫn. Giữ tiền tố "d|{tenant}|" để nút "Xóa cache" vẫn xoá được.
    /// Trả null (không đệm) khi không biết người dùng — không bao giờ rơi về khoá chung cả công ty.
    /// </summary>
    internal static string? DataCacheKey(string tenantId, string? username, string path)
    {
        var user = username?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(user) ? null : $"d|{tenantId}|u:{user}|{path}";
    }

    private async Task<JsonElement> FetchAsync(string sessionId, string path, CancellationToken ct)
    {
        if (_fetchOverride != null)
        {
            try { return await _fetchOverride(false, path); }
            catch (TourKitApiException ex) when (ex.Status == 401)
            {
                try { return await _fetchOverride(true, path); }
                catch (Exception ex2) { throw new CrmRetryFailedException(ex2); }
            }
        }

        var jwt = await _sessions!.GetValidJwtAsync(sessionId, ct);
        try { return await _api!.GetAsync(jwt, path, ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            // JWT hết hạn giữa chừng → đăng nhập lại 1 lần rồi thử lại; lần thử lại hỏng thì báo bằng tín hiệu riêng.
            try
            {
                jwt = await _sessions.ForceReloginAsync(sessionId, ct);
                return await _api!.GetAsync(jwt, path, ct);
            }
            catch (Exception ex2) { throw new CrmRetryFailedException(ex2); }
        }
    }

    public async Task<JsonElement?> ResolveNamesAsync(string sessionId, JsonElement? prms, CancellationToken ct)
    {
        prms = await ResolveMarketAsync(sessionId, prms, ct);
        return await ResolveEmployeeAsync(sessionId, prms, ct);
    }

    internal static bool IsUsableData(JsonElement d)
    {
        if (d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return false;
        if (d.ValueKind == JsonValueKind.Array) return true;
        if (d.ValueKind == JsonValueKind.Object)
            return d.TryGetProperty("items", out _) || d.EnumerateObject().Any();
        return false;
    }

    // ─── Resolver thị trường: tên -> marketId ────────────────────────────────────

    private async Task<List<(int Id, string Name)>> GetMarketsAsync(string sessionId, CancellationToken ct)
    {
        if (_marketsOverride != null) return await _marketsOverride();

        var tenant = _sessions!.Get(sessionId)?.TenantId ?? "";
        if (_markets.TryGetValue(tenant, out var c) && c.Exp > DateTime.UtcNow) return c.List;

        JsonElement data;
        var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
        try { data = await _api!.GetAsync(jwt, "/api/tours/markets", ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            jwt = await _sessions.ForceReloginAsync(sessionId, ct);
            data = await _api!.GetAsync(jwt, "/api/tours/markets", ct);
        }

        var list = new List<(int, string)>();
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("id", out var idp) && idp.TryGetInt32(out var id)
                    && m.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String)
                    list.Add((id, np.GetString()!));

        _markets[tenant] = (list, DateTime.UtcNow.Add(MarketTtl));
        _log?.LogInformation("Loaded {N} markets cho tenant {T}", list.Count, tenant);
        return list;
    }

    // Chuẩn hoá: thường hoá, bỏ dấu tiếng Việt, đ->d, bỏ dấu câu, gộp khoảng trắng.
    internal static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static int? MatchMarket(List<(int Id, string Name)> markets, string query)
    {
        var q = Norm(query);
        if (q.Length == 0) return null;
        foreach (var m in markets) if (Norm(m.Name) == q) return m.Id;
        foreach (var m in markets) { var n = Norm(m.Name); if (n.Contains(q) || q.Contains(n)) return m.Id; }
        var qt = q.Split(' ');
        foreach (var m in markets)
        {
            var mt = Norm(m.Name).Split(' ').ToHashSet();
            if (qt.Length > 1 && qt.All(t => mt.Contains(t))) return m.Id;
        }
        return null;
    }

    private async Task<JsonElement?> ResolveMarketAsync(string sessionId, JsonElement? prms, CancellationToken ct)
    {
        if (prms is not { ValueKind: JsonValueKind.Object } obj) return prms;
        if (!obj.TryGetProperty("marketName", out var mn) || mn.ValueKind != JsonValueKind.String) return prms;

        var name = mn.GetString()?.Trim() ?? "";
        var dict = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals("marketName", StringComparison.OrdinalIgnoreCase)) continue;
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        if (name.Length > 0 && !dict.ContainsKey("marketId"))
        {
            var markets = await GetMarketsAsync(sessionId, ct);
            var id = MatchMarket(markets, name);
            if (id.HasValue) { dict["marketId"] = id.Value; _log?.LogInformation("[CrmTool] thi truong '{Name}' -> marketId={Id}", name, id); }
            else _log?.LogInformation("[CrmTool] khong khop thi truong '{Name}'", name);
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    // ─── Resolver nhân viên: tên -> employeeId (cho employee_performance) ─────────
    // Nguồn = chính API thống kê hiệu suất /api/ai/employee-performance: nó trả TẤT CẢ nhân viên
    // (theo quyền) kèm employeeId + fullName → đúng phạm vi report có thể lọc tới, không cần endpoint khác.
    // Cache theo (tenant, người dùng) (MarketTtl 6h) vì danh sách theo quyền. Dùng lại MatchMarket (fuzzy (Id,Name)).

    private static bool PropCI(JsonElement obj, string name, out JsonElement val)
    {
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { val = p.Value; return true; }
        val = default;
        return false;
    }

    private async Task<List<(int Id, string Name)>> GetEmployeesAsync(string sessionId, CancellationToken ct)
    {
        var sess = _sessions!.Get(sessionId);
        var tenant = sess?.TenantId ?? "";
        // Danh sách nhân viên trả theo quyền người gọi → đệm theo (tenant, người dùng); không rõ người dùng thì không đệm.
        var user = sess?.Username?.Trim().ToLowerInvariant();
        var empKey = string.IsNullOrEmpty(user) ? null : $"{tenant}|{user}";
        if (empKey != null && _employees.TryGetValue(empKey, out var c) && c.Exp > DateTime.UtcNow) return c.List;

        JsonElement data;
        var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
        try { data = await _api!.GetAsync(jwt, "/api/ai/employee-performance", ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            jwt = await _sessions.ForceReloginAsync(sessionId, ct);
            data = await _api!.GetAsync(jwt, "/api/ai/employee-performance", ct);
        }

        // Envelope {section,title,items[...]}; mỗi item có employeeId + fullName.
        var list = new List<(int, string)>();
        if (PropCI(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                if (it.ValueKind == JsonValueKind.Object
                    && PropCI(it, "employeeId", out var idp) && idp.TryGetInt32(out var id)
                    && PropCI(it, "fullName", out var np) && np.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(np.GetString()))
                    list.Add((id, np.GetString()!));

        if (empKey != null) _employees[empKey] = (list, DateTime.UtcNow.Add(MarketTtl));
        _log?.LogInformation("Loaded {N} nhân viên (từ employee-performance) cho tenant {T}", list.Count, tenant);
        return list;
    }

    private async Task<JsonElement?> ResolveEmployeeAsync(string sessionId, JsonElement? prms, CancellationToken ct)
    {
        if (prms is not { ValueKind: JsonValueKind.Object } obj) return prms;
        if (!obj.TryGetProperty("employeeName", out var en) || en.ValueKind != JsonValueKind.String) return prms;

        var name = en.GetString()?.Trim() ?? "";
        var dict = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals("employeeName", StringComparison.OrdinalIgnoreCase)) continue;  // bỏ name khỏi query cuối
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        if (name.Length > 0 && !dict.ContainsKey("employeeId"))
        {
            var employees = await GetEmployeesAsync(sessionId, ct);
            var id = MatchMarket(employees, name);   // fuzzy match (Id,Name) — dùng chung với thị trường
            if (id.HasValue) { dict["employeeId"] = id.Value; _log?.LogInformation("[CrmTool] nhân viên '{Name}' -> employeeId={Id}", name, id); }
            else _log?.LogInformation("[CrmTool] không khớp nhân viên '{Name}'", name);
        }

        return JsonSerializer.SerializeToElement(dict);
    }
}
