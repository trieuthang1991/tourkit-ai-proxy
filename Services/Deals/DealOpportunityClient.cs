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
}
