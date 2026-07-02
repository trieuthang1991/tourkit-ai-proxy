using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Deals;

/// Lấy CƠ HỘI BÁN HÀNG thật từ TourKit (booking-ticket), qua session JWT (auto re-login 401):
///   • ListOpenAsync — /api/ai/booking-tickets (lọc bỏ Hủy) → danh sách nhẹ cho heuristic.
///   • GetContextAsync — /api/booking-tickets/{id} + /{id}/comments → hồ sơ text (HÀNH ĐỘNG SALE) + fingerprint.
public class DealOpportunityClient
{
    private const int CancelStatus = 5;   // TourKit BookingTicketStatus: 5 = Hủy
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly ILogger<DealOpportunityClient> _log;

    public DealOpportunityClient(TourKitApiClient api, TkSessionStore sessions, ILogger<DealOpportunityClient> log)
    {
        _api = api; _sessions = sessions; _log = log;
    }

    public record DealPage(List<DealOpportunity> Items, int Total);

    /// Danh sách cơ hội phân trang — KHÔNG filter status (raw upstream) cho list-view có pagination.
    /// Total = upstream count (chưa trừ Hủy/chốt). Frontend tự filter chip nếu cần.
    /// `keyword`: optional - khi user gõ ô tìm trên FE → truyền xuống TourKit upstream filter
    /// thật (theo tên KH/SĐT/mã đơn) thay vì lọc client-side trên trang hiện tại.
    /// `trangThai/nguon/nhanVienPhuTrach`: optional int ID → upstream `/api/ai/booking-tickets`
    /// hỗ trợ filter thật theo enum. 0 = không filter (default upstream behavior).
    public async Task<DealPage> ListPagedAsync(
        string sessionId, int pageIndex, int pageSize, CancellationToken ct,
        string? keyword = null, int? trangThai = null, int? nguon = null, int? nhanVienPhuTrach = null,
        int? rank = null, int? minRank = null, int? maxRank = null,
        string? startDate = null, long? minPrice = null, long? maxPrice = null,
        string? statusesCsv = null)
    {
        if (pageIndex < 1) pageIndex = 1;
        var path = $"/api/ai/booking-tickets?pageIndex={pageIndex}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(keyword))
            path += "&keyword=" + Uri.EscapeDataString(keyword.Trim());
        if (trangThai is > 0)        path += $"&trangThai={trangThai}";
        // Lọc NHIỀU trạng thái 1 request (upstream IN (...)) — tránh gọi mỗi status 1 lần.
        if (!string.IsNullOrWhiteSpace(statusesCsv)) path += "&statusesCsv=" + Uri.EscapeDataString(statusesCsv);
        if (nguon is > 0)            path += $"&nguon={nguon}";
        if (nhanVienPhuTrach is > 0) path += $"&nhanVienPhuTrach={nhanVienPhuTrach}";
        // rank: -1=chưa chấm, >0=đã chấm bất kỳ (sentinel) — 0 bỏ qua
        if (rank is not null && rank != 0) path += $"&rank={rank}";
        if (minRank is > 0) path += $"&minRank={minRank}";
        if (maxRank is > 0) path += $"&maxRank={maxRank}";
        // tuổi cơ hội (startDate=hôm nay−N) + giá trị (minPrice/maxPrice trên TotalPrice) → server-side toàn DB
        if (!string.IsNullOrWhiteSpace(startDate)) path += "&startDate=" + Uri.EscapeDataString(startDate);
        if (minPrice is > 0) path += $"&minPrice={minPrice}";
        if (maxPrice is > 0) path += $"&maxPrice={maxPrice}";
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<DealOpportunity>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                list.Add(Map(it));
            }
        }
        var total = GetInt(data, "total") ?? GetInt(data, "count") ?? list.Count;
        return new DealPage(list, total);
    }

    private static DealOpportunity Map(JsonElement it)
    {
        var createdAt = GetStr(it, "createdAt");
        return new DealOpportunity(
            Id:                 GetInt(it, "id") ?? 0,
            Code:               GetStr(it, "code"),
            CustomerName:       GetStr(it, "customerName") ?? "(không tên)",
            Phone:              GetStr(it, "phone"),
            Title:              GetStr(it, "title"),
            TotalPrice:         GetLong(it, "totalPrice") ?? 0,
            Status:             GetInt(it, "status") ?? 0,
            StatusName:         GetStr(it, "statusName"),
            Source:             GetInt(it, "source") ?? 0,
            SourceName:         GetStr(it, "sourceName"),
            MarketName:         GetStr(it, "marketName"),
            Assignees:          GetStr(it, "assignees"),
            AssigneeEmail:      GetStr(it, "assigneeEmail"),
            CreatedAt:          createdAt ?? "",
            AgeDays:            AgeDays(createdAt),
            // Cooling fields — null/0/false nếu upstream chưa deploy bản có fields này (backward-compat)
            LatestComment:      GetStr(it, "latestComment"),
            LatestCommentBy:    GetStr(it, "latestCommentBy"),
            LatestCommentDate:  GetStr(it, "latestCommentDate"),
            LastInteractionAt:  GetStr(it, "lastInteractionAt"),
            CoolingDays:        GetInt(it, "coolingDays") ?? 0,
            IsCooling:          GetBool(it, "isCooling"));
    }

    /// Danh sách cơ hội ĐANG MỞ (bỏ Hủy). Lọc tùy chọn theo người phụ trách / nguồn (client-side, substring).
    public async Task<List<DealOpportunity>> ListOpenAsync(
        string sessionId, string? assignee, string? source, int pageSize, CancellationToken ct)
    {
        var path = $"/api/ai/booking-tickets?pageIndex=1&pageSize={pageSize}";
        var data = await GetAsync(sessionId, path, ct);

        var list = new List<DealOpportunity>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                var status = GetInt(it, "status") ?? 0;
                if (status == CancelStatus) continue;            // bỏ deal đã Hủy

                var statusName = GetStr(it, "statusName");
                var sn = DealHeuristic.Normalize(statusName);    // bỏ deal đã CHỐT/thành công (không còn là cơ hội mở)
                if (sn.Length > 0 && (sn.Contains("chot don") || sn.Contains("thanh cong")
                        || sn.Contains("hoan thanh") || sn.Contains("hoan tat") || sn.Contains("da ban"))) continue;

                var createdAt = GetStr(it, "createdAt");
                var age = AgeDays(createdAt);
                var d = new DealOpportunity(
                    Id:           GetInt(it, "id") ?? 0,
                    Code:         GetStr(it, "code"),
                    CustomerName: GetStr(it, "customerName") ?? "(không tên)",
                    Phone:        GetStr(it, "phone"),
                    Title:        GetStr(it, "title"),
                    TotalPrice:   GetLong(it, "totalPrice") ?? 0,
                    Status:       status,
                    StatusName:   statusName,
                    Source:       GetInt(it, "source") ?? 0,
                    SourceName:   GetStr(it, "sourceName"),
                    MarketName:   GetStr(it, "marketName"),
                    Assignees:    GetStr(it, "assignees"),
                    AssigneeEmail: GetStr(it, "assigneeEmail"),
                    CreatedAt:    createdAt ?? "",
                    AgeDays:      age);

                if (!string.IsNullOrWhiteSpace(assignee) &&
                    (d.Assignees == null || !d.Assignees.Contains(assignee, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.IsNullOrWhiteSpace(source) &&
                    (d.SourceName == null || !d.SourceName.Contains(source, StringComparison.OrdinalIgnoreCase))) continue;

                list.Add(d);
            }
        }
        return list;
    }

    /// Hồ sơ 1 deal cho AI chấm: detail + timeline comments (hành động Sale). Fingerprint = hash để cache.
    public record DealContext(string Profile, string Fingerprint, int CommentCount);

    /// Wrapper: deal metadata (từ list) + context text đã compose. Trả từ <see cref="GetContextsAsync"/> batch.
    public record DealWithContext(DealOpportunity Deal, DealContext Context);

    /// <summary>
    /// BATCH — call upstream <c>/api/ai/booking-tickets/context?ids=1,2,3</c> → gộp base deal + comments +
    /// customer profile trong 1 HTTP. DÙNG CHUNG cho MỌI luồng AI deal review (batch analyze + workflow
    /// Pass 1/Pass 2/Cooling) → fingerprint đồng nhất.
    ///
    /// Cap 50 id/call (upstream truncate). Trả empty nếu ids rỗng / upstream 0 rows.
    /// Profile TEXT compose ở proxy (không upstream) — nếu đổi format prompt sau này chỉ đụng file này.
    /// </summary>
    public async Task<List<DealWithContext>> GetContextsAsync(string sessionId, IEnumerable<int> ids, CancellationToken ct)
    {
        var idList = ids.Where(i => i > 0).Distinct().Take(50).ToList();
        if (idList.Count == 0) return new List<DealWithContext>();
        var csv = string.Join(",", idList);
        var path = "/api/ai/booking-tickets/context?ids=" + Uri.EscapeDataString(csv);
        var data = await GetAsync(sessionId, path, ct);

        var result = new List<DealWithContext>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                result.Add(BuildFromContext(it));
        return result;
    }

    /// Chuyển 1 <c>AiBookingTicketContext</c> (JSON) → <see cref="DealWithContext"/>: parse deal metadata +
    /// compose Profile text (giống <see cref="GetContextAsync"/> per-deal cũ) + fingerprint.
    private static DealWithContext BuildFromContext(JsonElement e)
    {
        var createdAt = GetStr(e, "createdAt");
        var deal = new DealOpportunity(
            Id:                 GetInt(e, "id") ?? 0,
            Code:               GetStr(e, "code"),
            CustomerName:       GetStr(e, "customerName") ?? "(không tên)",
            Phone:              GetStr(e, "phone"),
            Title:              GetStr(e, "title"),
            TotalPrice:         GetLong(e, "totalPrice") ?? 0,
            Status:             GetInt(e, "status") ?? 0,
            StatusName:         GetStr(e, "statusName"),
            Source:             GetInt(e, "source") ?? 0,
            SourceName:         GetStr(e, "sourceName"),
            MarketName:         GetStr(e, "marketName"),
            Assignees:          GetStr(e, "assignees"),
            AssigneeEmail:      GetStr(e, "assigneeEmail"),
            CreatedAt:          createdAt ?? "",
            AgeDays:            GetInt(e, "ageDays") ?? AgeDays(createdAt),
            LatestComment:      null,       // list này KHÔNG có latest single, dùng full Comments[] bên dưới
            LatestCommentBy:    null,
            LatestCommentDate:  null,
            LastInteractionAt:  GetStr(e, "lastInteractionAt"),
            CoolingDays:        GetInt(e, "coolingDays") ?? 0,
            IsCooling:          GetBool(e, "isCooling"));

        // Compose Profile text (mirror GetContextAsync per-deal, giữ prompt shape ổn định)
        var sb = new StringBuilder();
        sb.Append("CƠ HỘI: ").Append(deal.Title ?? deal.Code ?? $"#{deal.Id}").Append('\n');
        sb.Append("Khách: ").Append(deal.CustomerName);
        if (!string.IsNullOrWhiteSpace(deal.Phone)) sb.Append(" · ").Append(deal.Phone);
        sb.Append('\n');
        sb.Append("Giá trị: ").Append(deal.TotalPrice.ToString("#,##0", CultureInfo.InvariantCulture)).Append(" đ\n");
        sb.Append("Trạng thái: ").Append(deal.StatusName ?? deal.Status.ToString()).Append('\n');
        sb.Append("Nguồn: ").Append(deal.SourceName ?? "?");
        if (!string.IsNullOrWhiteSpace(deal.MarketName)) sb.Append(" · Thị trường: ").Append(deal.MarketName);
        sb.Append('\n');
        sb.Append("Người phụ trách: ").Append(string.IsNullOrWhiteSpace(deal.Assignees) ? "(chưa giao)" : deal.Assignees).Append('\n');
        sb.Append("Tuổi cơ hội: ").Append(deal.AgeDays).Append(" ngày kể từ tạo\n");

        var content = GetStr(e, "content");
        if (!string.IsNullOrWhiteSpace(content)) sb.Append("Nội dung phiếu: ").Append(content!.Trim()).Append('\n');

        // Comments timeline — upstream đã strip HTML + cap 30
        int commentCount = 0;
        if (e.TryGetProperty("comments", out var cArr) && cArr.ValueKind == JsonValueKind.Array && cArr.GetArrayLength() > 0)
        {
            sb.Append("\nLỊCH SỬ HÀNH ĐỘNG CỦA SALE (mới→cũ):\n");
            foreach (var c in cArr.EnumerateArray())
            {
                var who = GetStr(c, "userName") ?? "NV";
                var when = GetStr(c, "date") ?? "";
                var note = GetStr(c, "content") ?? "";
                if (string.IsNullOrWhiteSpace(note)) continue;
                commentCount++;
                sb.Append("• [").Append(FmtDate(when)).Append("] ").Append(who).Append(": ").Append(note.Trim()).Append('\n');
            }
            if (commentCount == 0) sb.Append("(Chưa có ghi chú hành động nào — Sale chưa chăm sóc)\n");
        }
        else
        {
            sb.Append("\n(Chưa có ghi chú hành động nào — Sale chưa chăm sóc)\n");
        }

        // Customer profile enrich — upstream đã compose join theo IdKhachHang
        if (e.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object)
        {
            sb.Append("\nHỒ SƠ KHÁCH HÀNG (lịch sử CRM):\n");
            var grp = GetStr(cust, "groupName");
            var tours = GetInt(cust, "totalTours") ?? 0;
            var revFmt = GetStr(cust, "totalRevenueFormatted");
            var rankName = GetStr(cust, "rankName");
            var lastCare = GetStr(cust, "lastCareDateFormatted");
            var cnote = GetStr(cust, "note") ?? "";
            if (!string.IsNullOrWhiteSpace(grp)) sb.Append("• Nhóm: ").Append(grp).Append('\n');
            sb.Append("• Đã mua: ").Append(tours).Append(" tour");
            if (!string.IsNullOrWhiteSpace(revFmt)) sb.Append(" · Tổng chi: ").Append(revFmt);
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(rankName)) sb.Append("• Hạng AI của khách: ").Append(rankName).Append('\n');
            if (!string.IsNullOrWhiteSpace(lastCare)) sb.Append("• Chăm sóc cuối: ").Append(lastCare).Append('\n');
            if (!string.IsNullOrWhiteSpace(cnote)) sb.Append("• Nhu cầu/ghi chú: ").Append(cnote.Length > 200 ? cnote[..200] : cnote).Append('\n');
            if (tours == 0) sb.Append("• (Khách MỚI — chưa có giao dịch nào trong CRM)\n");
        }

        var profile = sb.ToString().Trim();
        return new DealWithContext(deal, new DealContext(profile, Fingerprint(profile), commentCount));
    }

    public async Task<DealContext> GetContextAsync(string sessionId, DealOpportunity deal, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("CƠ HỘI: ").Append(deal.Title ?? deal.Code ?? $"#{deal.Id}").Append('\n');
        sb.Append("Khách: ").Append(deal.CustomerName);
        if (!string.IsNullOrWhiteSpace(deal.Phone)) sb.Append(" · ").Append(deal.Phone);
        sb.Append('\n');
        sb.Append("Giá trị: ").Append(deal.TotalPrice.ToString("#,##0", CultureInfo.InvariantCulture)).Append(" đ\n");
        sb.Append("Trạng thái: ").Append(deal.StatusName ?? deal.Status.ToString()).Append('\n');
        sb.Append("Nguồn: ").Append(deal.SourceName ?? "?");
        if (!string.IsNullOrWhiteSpace(deal.MarketName)) sb.Append(" · Thị trường: ").Append(deal.MarketName);
        sb.Append('\n');
        sb.Append("Người phụ trách: ").Append(string.IsNullOrWhiteSpace(deal.Assignees) ? "(chưa giao)" : deal.Assignees).Append('\n');
        sb.Append("Tuổi cơ hội: ").Append(deal.AgeDays).Append(" ngày kể từ tạo\n");

        // Detail (nội dung phiếu) — best-effort
        try
        {
            var detail = await GetAsync(sessionId, $"/api/booking-tickets/{deal.Id}", ct);
            var content = GetStr(detail, "noiDungPhieu") ?? GetStr(detail, "content") ?? GetStr(detail, "description");
            if (!string.IsNullOrWhiteSpace(content)) sb.Append("Nội dung phiếu: ").Append(content!.Trim()).Append('\n');
        }
        catch (Exception ex) { _log.LogDebug(ex, "Deal {Id} detail bỏ qua", deal.Id); }

        // Comments = lịch sử HÀNH ĐỘNG của Sale (gọi/ghi chú/cập nhật), theo thời gian
        int commentCount = 0;
        try
        {
            var comments = await GetAsync(sessionId, $"/api/booking-tickets/{deal.Id}/comments", ct);
            var arr = comments.ValueKind == JsonValueKind.Array ? comments
                    : (comments.ValueKind == JsonValueKind.Object && comments.TryGetProperty("items", out var ci) ? ci : default);
            if (arr.ValueKind == JsonValueKind.Array)
            {
                sb.Append("\nLỊCH SỬ HÀNH ĐỘNG CỦA SALE (mới→cũ):\n");
                foreach (var c in arr.EnumerateArray())
                {
                    var who = GetStr(c, "userName") ?? GetStr(c, "createdBy") ?? GetStr(c, "tenComment") ?? "NV";
                    var when = GetStr(c, "insDttm") ?? GetStr(c, "createdAt") ?? "";
                    var note = GetStr(c, "noiDungComment") ?? GetStr(c, "content") ?? GetStr(c, "noiDung") ?? "";
                    note = StripHtml(note);
                    if (string.IsNullOrWhiteSpace(note)) continue;
                    commentCount++;
                    sb.Append("• [").Append(FmtDate(when)).Append("] ").Append(who).Append(": ").Append(note.Trim()).Append('\n');
                    if (commentCount >= 30) break;
                }
                if (commentCount == 0) sb.Append("(Chưa có ghi chú hành động nào — Sale chưa chăm sóc)\n");
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Deal {Id} comments bỏ qua", deal.Id); }

        // ── Enrich: HỒ SƠ KHÁCH HÀNG (lịch sử CRM) — join theo SĐT (cách hệ thống định danh KH,
        //    giống FindDuplicatePhone lúc tạo tour). Cho AI biết khách là VIP mua lại hay lead lạ
        //    → chấm sát hơn. Reuse /api/ai/customers (đã có totalTours/totalRevenue/rank/lastCare).
        //    Best-effort: lỗi/không khớp → bỏ qua, không phá scoring. Block này vào profile →
        //    fingerprint đổi → chấm lại đúng khi hồ sơ KH xuất hiện/thay đổi.
        if (!string.IsNullOrWhiteSpace(deal.Phone))
        {
            try
            {
                var digits = new string(deal.Phone.Where(char.IsDigit).ToArray());
                if (digits.Length >= 6)
                {
                    var env = await GetAsync(sessionId, "/api/ai/customers?pageSize=3&filter=" + Uri.EscapeDataString(digits), ct);
                    if (env.ValueKind == JsonValueKind.Object && env.TryGetProperty("items", out var citems)
                        && citems.ValueKind == JsonValueKind.Array && citems.GetArrayLength() > 0)
                    {
                        // Khớp ĐÚNG SĐT nếu có nhiều kết quả (tránh nhầm KH trùng tên); else lấy đầu.
                        var cust = citems[0];
                        foreach (var it in citems.EnumerateArray())
                        {
                            var ph = new string((GetStr(it, "phone") ?? "").Where(char.IsDigit).ToArray());
                            if (ph == digits) { cust = it; break; }
                        }
                        var tours    = GetInt(cust, "totalTours") ?? 0;
                        var revFmt   = GetStr(cust, "totalRevenueFormatted");
                        var grp      = GetStr(cust, "groupName");
                        var rankName = GetStr(cust, "rankName");
                        var lastCare = GetStr(cust, "lastCareDateFormatted");
                        var cnote    = StripHtml(GetStr(cust, "note") ?? "");

                        sb.Append("\nHỒ SƠ KHÁCH HÀNG (lịch sử CRM):\n");
                        if (!string.IsNullOrWhiteSpace(grp)) sb.Append("• Nhóm: ").Append(grp).Append('\n');
                        sb.Append("• Đã mua: ").Append(tours).Append(" tour");
                        if (!string.IsNullOrWhiteSpace(revFmt)) sb.Append(" · Tổng chi: ").Append(revFmt);
                        sb.Append('\n');
                        if (!string.IsNullOrWhiteSpace(rankName)) sb.Append("• Hạng AI của khách: ").Append(rankName).Append('\n');
                        if (!string.IsNullOrWhiteSpace(lastCare)) sb.Append("• Chăm sóc cuối: ").Append(lastCare).Append('\n');
                        if (!string.IsNullOrWhiteSpace(cnote)) sb.Append("• Nhu cầu/ghi chú: ").Append(cnote.Length > 200 ? cnote[..200] : cnote).Append('\n');
                        if (tours == 0) sb.Append("• (Khách MỚI — chưa có giao dịch nào trong CRM)\n");
                    }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Deal {Id} enrich hồ sơ KH bỏ qua", deal.Id); }
        }

        var profile = sb.ToString().Trim();
        return new DealContext(profile, Fingerprint(profile), commentCount);
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────
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

    private static int AgeDays(string? createdIso)
    {
        if (DateTime.TryParse(createdIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return Math.Max(0, (int)(DateTime.UtcNow.Date - d.Date).TotalDays);
        return 0;
    }
    private static string FmtDate(string iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM HH:mm") : iso;
    private static string StripHtml(string s)
        => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ").Trim();
    private static string Fingerprint(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

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
    private static bool GetBool(JsonElement el, string name)
        => Find(el, name, out var v) && v.ValueKind == JsonValueKind.True;
}
