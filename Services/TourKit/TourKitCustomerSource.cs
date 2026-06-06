using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.TourKit;

/// Lấy KHÁCH HÀNG THẬT từ TourKit CRM (thay data/customers.seed.json):
///   • ListAsync  — `/api/ai/customers` (nhẹ: tên + tổng tour/doanh thu, cho danh sách).
///   • GetFullAsync — `/api/customers/{id}` + `/orders` → build Customer đầy đủ (purchases + metrics) để chấm hạng.
/// Tất cả read-only, qua session JWT (auto re-login 401, mẫu ChatAgentService).
public class TourKitCustomerSource
{
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly ILogger<TourKitCustomerSource> _log;

    public TourKitCustomerSource(TourKitApiClient api, TkSessionStore sessions, ILogger<TourKitCustomerSource> log)
    {
        _api = api; _sessions = sessions; _log = log;
    }

    /// Lọc nâng cao — forward đầy đủ params /api/ai/customers theo schema mobile CustomerList.razor:
    /// search, customerTypeId, customerSourceId, sellerId(nv phụ trách), gender, careFilter,
    /// birthdayThisMonth, startDate, endDate, sortOrder.
    public async Task<List<Customer>> ListAsync(string sessionId, CustomerFilter f, int pageSize, CancellationToken ct)
    {
        var qs = new List<string> { "pageIndex=1", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(f.Search))         qs.Add("filter=" + Uri.EscapeDataString(f.Search.Trim()));
        if (f.CustomerTypeId   is > 0)                    qs.Add("customerTypeId=" + f.CustomerTypeId);
        if (f.CustomerSourceId is > 0)                    qs.Add("customerSourceId=" + f.CustomerSourceId);
        if (f.SellerId         is > 0)                    qs.Add("sellerId=" + f.SellerId);
        if (!string.IsNullOrWhiteSpace(f.Gender))         qs.Add("gender=" + Uri.EscapeDataString(f.Gender));
        if (!string.IsNullOrWhiteSpace(f.CareFilter))     qs.Add("careFilter=" + Uri.EscapeDataString(f.CareFilter));
        if (f.BirthdayThisMonth == true)                  qs.Add("birthdayThisMonth=true");
        if (!string.IsNullOrWhiteSpace(f.StartDate))      qs.Add("startDate=" + Uri.EscapeDataString(f.StartDate));
        if (!string.IsNullOrWhiteSpace(f.EndDate))        qs.Add("endDate=" + Uri.EscapeDataString(f.EndDate));
        if (!string.IsNullOrWhiteSpace(f.SortOrder))      qs.Add("sortOrder=" + Uri.EscapeDataString(f.SortOrder));

        var path = "/api/ai/customers?" + string.Join("&", qs);
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<Customer>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                list.Add(MapLight(it));
        return list;
    }

    /// Bộ filter mở rộng — bám CustomerSearchRequest của TourKit.Api.
    public record CustomerFilter(
        string? Search = null,
        int? CustomerTypeId = null,
        int? CustomerSourceId = null,
        int? SellerId = null,
        string? Gender = null,           // "M" | "F" | null
        string? CareFilter = null,       // mobile careFilters (0/1/2... theo TourKit)
        bool? BirthdayThisMonth = null,
        string? StartDate = null,        // yyyy-MM-dd
        string? EndDate = null,
        string? SortOrder = null         // tên cột (mới nhất/doanh thu...)
    );

    /// Lấy lookup data cho bộ lọc (loại KH / nguồn / NV phụ trách) qua /api/ai/reference.
    public async Task<JsonElement> GetLookupsAsync(string sessionId, CancellationToken ct)
        => await GetAsync(sessionId, "/api/ai/reference", ct);

    public async Task<Customer?> GetFullAsync(string sessionId, string id, CancellationToken ct)
    {
        JsonElement detail;
        try { detail = await GetAsync(sessionId, $"/api/customers/{Uri.EscapeDataString(id)}", ct); }
        catch (TourKitApiException ex) { _log.LogWarning("KH {Id} detail lỗi: {Msg}", id, ex.Message); return null; }

        var orders = new List<JsonElement>();
        try
        {
            var o = await GetAsync(sessionId, $"/api/customers/{Uri.EscapeDataString(id)}/orders", ct);
            if (o.ValueKind == JsonValueKind.Array) orders = o.EnumerateArray().ToList();
        }
        catch (Exception ex) { _log.LogWarning(ex, "KH {Id} orders lỗi", id); }

        return BuildFull(id, detail, orders);
    }

    // ─── Mapping ──────────────────────────────────────────────────────────────────
    private static Customer MapLight(JsonElement e)
    {
        var tours = GetInt(e, "totalTours") ?? 0;
        var spent = GetLong(e, "totalRevenue") ?? 0;
        return new Customer(
            Id: (GetInt(e, "id") ?? 0).ToString(),
            Name: GetStr(e, "fullName") ?? "(không tên)",
            Phone: GetStr(e, "phone"), Email: GetStr(e, "email"),
            Age: null, Gender: GetStr(e, "genderName") ?? GetStr(e, "gender"),
            Location: GetStr(e, "address"),
            Segment: Segment(tours, spent),
            CreatedAt: GetStr(e, "createdAt") ?? "",
            Source: GetStr(e, "customerSourceName"),
            Metrics: new CustomerMetrics(tours, spent, tours > 0 ? spent / tours : 0, null, null, null, 0, null, 0, 0),
            Purchases: new(), CareLogs: new()
        );
    }

    private static Customer BuildFull(string id, JsonElement d, List<JsonElement> orders)
    {
        var purchases = new List<TourPurchase>();
        long total = 0; DateTime? last = null; var dates = new List<DateTime>();
        foreach (var o in orders)
        {
            var amount = GetLong(o, "totalThu") ?? 0;
            total += amount;
            var dt = GetDate(o, "departureDate");
            if (dt.HasValue) { dates.Add(dt.Value); if (last == null || dt > last) last = dt; }
            purchases.Add(new TourPurchase(
                Date: dt?.ToString("yyyy-MM-dd") ?? "",
                Destination: GetStr(o, "tourTitle") ?? GetStr(o, "tourCode") ?? "Tour",
                Nights: 0, Pax: 0, Amount: amount, Channel: null));
        }
        var tours = orders.Count > 0 ? orders.Count : (GetInt(d, "totalTours") ?? 0);
        if (total == 0) total = GetLong(d, "totalRevenue") ?? 0;
        int? lastDaysAgo = last.HasValue ? (int)(DateTime.UtcNow.Date - last.Value.Date).TotalDays : null;

        int? avgBetween = null;
        if (dates.Count >= 2)
        {
            dates.Sort();
            var gaps = new List<int>();
            for (int i = 1; i < dates.Count; i++) gaps.Add((int)(dates[i] - dates[i - 1]).TotalDays);
            avgBetween = gaps.Count > 0 ? (int)gaps.Average() : null;
        }

        return new Customer(
            Id: id,
            Name: GetStr(d, "fullName") ?? GetStr(d, "name") ?? "(không tên)",
            Phone: GetStr(d, "phone") ?? GetStr(d, "phoneNumber"),
            Email: GetStr(d, "email"),
            Age: null, Gender: GetStr(d, "genderName") ?? GetStr(d, "gender"),
            Location: GetStr(d, "address") ?? GetStr(d, "city"),
            Segment: Segment(tours, total),
            CreatedAt: GetStr(d, "createdAt") ?? "",
            Source: GetStr(d, "customerSourceName"),
            Metrics: new CustomerMetrics(
                TotalTours: tours, TotalSpent: total, Aov: tours > 0 ? total / tours : 0,
                LastPurchaseDate: last?.ToString("yyyy-MM-dd"), LastPurchaseDaysAgo: lastDaysAgo,
                AvgDaysBetweenOrders: avgBetween, CareInteractions: 0, LastCareDaysAgo: null,
                ComplaintCount: 0, CancelCount: 0),
            Purchases: purchases.OrderByDescending(p => p.Date).ToList(),
            CareLogs: new()
        );
    }

    private static string Segment(int tours, long spent)
        => tours >= 5 || spent >= 100_000_000 ? "VIP" : tours <= 1 ? "Mới" : "Thường";

    // ─── TourKit call + JSON helpers ───────────────────────────────────────────────
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
        if (!Find(el, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dd)) return (long)dd;
        return null;
    }
    private static DateTime? GetDate(JsonElement el, string name)
        => Find(el, name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
