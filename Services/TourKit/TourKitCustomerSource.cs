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
    /// birthdayThisMonth, startDate, endDate, sortOrder. Trả `(items, total)` để FE phân trang đúng.
    public async Task<CustomerPage> ListAsync(string sessionId, CustomerFilter f, int pageIndex, int pageSize, CancellationToken ct)
    {
        if (pageIndex < 1) pageIndex = 1;
        var qs = new List<string> { $"pageIndex={pageIndex}", $"pageSize={pageSize}" };
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
        if (f.Rank is not null && f.Rank != 0)            qs.Add("rank=" + f.Rank);   // -1=chưa review, 1..6=hạng, >6=đã review bất kỳ

        var path = "/api/ai/customers?" + string.Join("&", qs);
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<Customer>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                list.Add(MapLight(it));

        // Upstream AI envelope: {section, title, count, total, summary, items[]}
        // `total` = full match count (toàn DB), `count` = số rows trong page hiện tại.
        // Fallback `count` rồi cuối cùng items.Count nếu upstream cũ chưa có total.
        var total = GetInt(data, "total") ?? GetInt(data, "count") ?? list.Count;
        return new CustomerPage(list, total);
    }

    public record CustomerPage(List<Customer> Items, int Total);

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
        string? SortOrder = null,        // tên cột (mới nhất/doanh thu...)
        int? Rank = null                 // upstream customers.[Rank]: -1=chưa review, 1..6=hạng A..F, >6=đã review bất kỳ
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

    /// <summary>
    /// BATCH — call upstream /api/ai/customers/context?ids=1,2,3 → Customer[] có ĐẦY ĐỦ Purchases + CareLogs.
    /// **NGUỒN DUY NHẤT** cho MỌI luồng AI review (page endpoint, batch service, workflow auto-review) →
    /// fingerprint đồng nhất giữa các luồng, không re-review nhầm.
    ///
    /// Cap 50 id/call (upstream tự truncate). Caller phân batch nếu &gt;50.
    /// KH id không tồn tại/không có quyền → im lặng bỏ (dict trả về không có key đó).
    /// </summary>
    public async Task<List<Customer>> GetContextsAsync(string sessionId, IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(50).ToList();
        if (idList.Count == 0) return new List<Customer>();
        var csv = string.Join(",", idList);
        var path = "/api/ai/customers/context?ids=" + Uri.EscapeDataString(csv);
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<Customer>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                list.Add(MapContext(it));
        return list;
    }

    /// Map AiCustomerContext (upstream JSON) → Customer đầy đủ. DÙNG CHUNG cho page/batch/workflow.
    private static Customer MapContext(JsonElement e)
    {
        var id = (GetInt(e, "id") ?? 0).ToString();
        var tours = GetInt(e, "totalTours") ?? 0;
        var totalRevenue = GetLong(e, "totalRevenue") ?? 0;
        var lastCareDate = GetStr(e, "lastCareDateFormatted") ?? GetStr(e, "lastCareDate");
        var lastPurchase = GetStr(e, "lastPurchaseDate");   // upstream trả ISO
        var lastDaysAgo = GetInt(e, "lastPurchaseDaysAgo");
        var avgBetween = GetInt(e, "avgDaysBetweenOrders");

        // Purchases — sort desc by DepartureDate (canonical → fingerprint ổn định).
        var purchases = new List<TourPurchase>();
        if (e.TryGetProperty("purchases", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pArr.EnumerateArray())
            {
                var date = GetStr(p, "departureDate") ?? "";
                purchases.Add(new TourPurchase(
                    Date: date.Length >= 10 ? date[..10] : date,
                    Destination: GetStr(p, "tourTitle") ?? GetStr(p, "tourCode") ?? "Tour",
                    Nights: 0, Pax: 0,
                    Amount: GetLong(p, "totalAmount") ?? 0,
                    Channel: null));
            }
            purchases = purchases.OrderByDescending(x => x.Date).ToList();
        }

        // CareLogs — upstream đã StripHtml + top 30 mới nhất. Sort desc by Date.
        var careLogs = new List<CareLog>();
        if (e.TryGetProperty("careLogs", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cArr.EnumerateArray())
            {
                var date = GetStr(c, "date") ?? "";
                careLogs.Add(new CareLog(
                    Date: date.Length >= 10 ? date[..10] : date,
                    Channel: "comment",                                    // upstream comment thuần, không phân kênh
                    Summary: GetStr(c, "content") ?? "",
                    Sentiment: "neutral",                                  // KHÔNG chạy sentiment — để default ổn định fingerprint
                    Outcome: GetStr(c, "userName")));                      // dùng userName làm outcome tag (ai ghi)
            }
            careLogs = careLogs.OrderByDescending(x => x.Date).ToList();
        }

        return new Customer(
            Id:        id,
            Code:      GetStr(e, "code"),
            Name:      GetStr(e, "fullName") ?? "(không tên)",
            Phone:     GetStr(e, "phone"),
            Email:     GetStr(e, "email"),
            Age:       null,
            Gender:    GetStr(e, "genderName") ?? GetStr(e, "gender"),
            Location:  GetStr(e, "address"),
            Segment:   GetStr(e, "groupName") ?? GetStr(e, "customerTypeName") ?? Segment(tours, totalRevenue),
            CreatedAt: GetStr(e, "createdAt") ?? "",
            Source:    GetStr(e, "customerSourceName"),
            Metrics:   new CustomerMetrics(
                TotalTours: tours,
                TotalSpent: totalRevenue,
                Aov: tours > 0 ? totalRevenue / tours : 0,
                LastPurchaseDate: lastPurchase,
                LastPurchaseDaysAgo: lastDaysAgo,
                AvgDaysBetweenOrders: avgBetween,
                CareInteractions: careLogs.Count,
                LastCareDaysAgo: null,   // dẫn xuất được từ lastCareDate nhưng để null cho gọn (dùng lastCareDate là đủ)
                ComplaintCount: 0,       // TODO: đếm từ careLogs sentiment khi bật sentiment analysis
                CancelCount: 0,
                LastCareDate: lastCareDate),
            Purchases: purchases,
            CareLogs:  careLogs,
            Note:      GetStr(e, "note"));
    }

    // ─── Mapping ──────────────────────────────────────────────────────────────────
    private static Customer MapLight(JsonElement e)
    {
        var tours = GetInt(e, "totalTours") ?? 0;
        var spent = GetLong(e, "totalRevenue") ?? 0;
        // Upstream /api/ai/customers (AiCustomerItem) trả lastCareDateFormatted (dd/MM/yyyy) + note (HTML).
        // Note: docs/ai-api-guide.md §7c (2026-06-10).
        var lastCare = GetStr(e, "lastCareDateFormatted");
        return new Customer(
            Id: (GetInt(e, "id") ?? 0).ToString(),
            Code: GetStr(e, "code"),
            Name: GetStr(e, "fullName") ?? "(không tên)",
            Phone: GetStr(e, "phone"), Email: GetStr(e, "email"),
            Age: null, Gender: GetStr(e, "genderName") ?? GetStr(e, "gender"),
            Location: GetStr(e, "address"),
            Segment: Segment(tours, spent),
            CreatedAt: GetStr(e, "createdAt") ?? "",
            Source: GetStr(e, "customerSourceName"),
            Metrics: new CustomerMetrics(tours, spent, tours > 0 ? spent / tours : 0, null, null, null, 0, null, 0, 0,
                LastCareDate: string.IsNullOrWhiteSpace(lastCare) ? null : lastCare),
            Purchases: new(), CareLogs: new(),
            Note: GetStr(e, "note")
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

        // CustomerDetailResponse có Note; LastCareDate KHÔNG đảm bảo (xem docs §7c: detail có LinkFB/SellerName/...
        // không list LastCareDate). Cứ thử GetStr — nếu upstream có thì lấy, không thì null.
        var lastCare = GetStr(d, "lastCareDateFormatted") ?? GetStr(d, "lastCareDate");
        return new Customer(
            Id: id,
            Code: GetStr(d, "code") ?? GetStr(d, "customerCode"),
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
                ComplaintCount: 0, CancelCount: 0,
                LastCareDate: string.IsNullOrWhiteSpace(lastCare) ? null : lastCare),
            Purchases: purchases.OrderByDescending(p => p.Date).ToList(),
            CareLogs: new(),
            Note: GetStr(d, "note")
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
