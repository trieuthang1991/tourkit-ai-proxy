using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Reviews;

/// Lấy KHÁCH HÀNG thật từ TourKit (`/api/ai/customers`) qua session JWT (auto re-login 401) →
/// map sang <see cref="Customer"/> để feed ReviewService. Dùng bởi workflow customer-auto-review.
///
/// Profile dựng từ DỮ LIỆU LIST (tổng tour/chi, hạng, nguồn, chăm sóc cuối, ghi chú) — KHÔNG fetch
/// detail purchases/careLogs (giữ nhẹ + ít call). Đủ tín hiệu cho review tổng quan; muốn sâu hơn thì
/// enrich detail sau (giống DealOpportunityClient.GetContextAsync).
public class CustomerReviewClient
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly ILogger<CustomerReviewClient> _log;

    public CustomerReviewClient(TourKitApiClient api, TkSessionStore sessions, ILogger<CustomerReviewClient> log)
    {
        _api = api; _sessions = sessions; _log = log;
    }

    /// 1 KH CRM + metadata workflow cần (đã map sang Customer + giữ Rank/CreatedAt/Assignee raw).
    public record CrmCustomer(Customer Customer, int? Rank, string CreatedAt, string? Assignee);

    /// Kéo 1 trang KH (mới nhất trước theo upstream). Caller phân trang (pageIndex tăng dần) để quét hết.
    public async Task<List<CrmCustomer>> ListAsync(string sessionId, int pageIndex, int pageSize, CancellationToken ct)
    {
        if (pageIndex < 1) pageIndex = 1;
        var path = $"/api/ai/customers?pageIndex={pageIndex}&pageSize={pageSize}";
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<CrmCustomer>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                list.Add(Map(it));
        return list;
    }

    private static CrmCustomer Map(JsonElement it)
    {
        var id        = GetInt(it, "id") ?? 0;
        var fullName  = GetStr(it, "fullName") ?? "(không tên)";
        var totalTours = GetInt(it, "totalTours") ?? 0;
        var totalRev  = GetLong(it, "totalRevenue") ?? 0;
        var lastCare  = GetStr(it, "lastCareDateFormatted") ?? GetStr(it, "lastCareDate");
        var createdAt = GetStr(it, "createdAt") ?? "";
        var segment   = GetStr(it, "groupName") ?? GetStr(it, "customerTypeName") ?? "Thường";

        var customer = new Customer(
            Id:        id.ToString(),
            Code:      GetStr(it, "code"),
            Name:      fullName,
            Phone:     GetStr(it, "phone"),
            Email:     GetStr(it, "email"),
            Age:       null,
            Gender:    GetStr(it, "genderName") ?? GetStr(it, "gender"),
            Location:  GetStr(it, "address"),
            Segment:   segment,
            CreatedAt: createdAt,
            Source:    GetStr(it, "customerSourceName"),
            Metrics:   new CustomerMetrics(
                TotalTours:          totalTours,
                TotalSpent:          totalRev,
                Aov:                 totalTours > 0 ? totalRev / totalTours : 0,
                LastPurchaseDate:    null,
                LastPurchaseDaysAgo: null,
                AvgDaysBetweenOrders: null,
                CareInteractions:    0,
                LastCareDaysAgo:     DaysAgo(GetStr(it, "lastCareDate")),
                ComplaintCount:      0,
                CancelCount:         0,
                LastCareDate:        lastCare),
            Purchases: new List<TourPurchase>(),
            CareLogs:  new List<CareLog>(),
            Note:      GetStr(it, "note"));

        return new CrmCustomer(customer, GetInt(it, "rank"), createdAt, GetStr(it, "assignee"));
    }

    private async Task<JsonElement> GetAsync(string sessionId, string path, CancellationToken ct)
    {
        var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
        try { return await _api.GetAsync(jwt, path, ct); }
        catch (TourKitApiException ex) when (ex.Status == 401)
        {
            jwt = await _sessions.ForceReloginAsync(sessionId, ct);
            return await _api.GetAsync(jwt, path, ct);
        }
    }

    private static int? DaysAgo(string? iso)
    {
        if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return Math.Max(0, (int)(DateTime.UtcNow.Date - d.Date).TotalDays);
        return null;
    }

    private static bool Find(JsonElement el, string name, out JsonElement v)
    {
        v = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        return false;
    }
    private static string? GetStr(JsonElement el, string name)
        => Find(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement el, string name)
        => Find(el, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static long? GetLong(JsonElement el, string name)
    {
        if (!Find(el, name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out var n)) return n;
        if (v.TryGetDouble(out var dd)) return (long)dd;
        return null;
    }
}
