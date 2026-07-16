using System.Globalization;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Chat;

/// Resolve tên người/khách/deal/workflow → id qua các tool đọc /api/ai/*.
/// Trả ResolveOutcome: Id (khớp 1), Ambiguous (khớp >1 → clarify), hoặc cả 2 null (không thấy).
/// KHÔNG tự lookup session/JWT — caller (ActionExecutor/endpoint) truyền jwt đã resolve sẵn,
/// theo đúng convention "backend re-resolve tên→id" ở docs/superpowers/specs/2026-07-14-assistant-action-tools-design.md §3.3.
public class ActionResolver
{
    private const int PageSize = 20;

    private readonly TourKitApiClient _api;
    private readonly ILogger<ActionResolver> _log;

    public ActionResolver(TourKitApiClient api, ILogger<ActionResolver> log)
    {
        _api = api;
        _log = log;
    }

    // ─── Chuẩn hoá + fuzzy match (thuần, test được — xem ActionResolverTests) ─────

    /// Chuẩn hóa tên để so khớp: lowercase, bỏ dấu, đ→d, gộp khoảng trắng.
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant().Replace('đ', 'd');
        var formD = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var noMark = sb.ToString().Normalize(NormalizationForm.FormC);
        return string.Join(' ', noMark.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// True nếu `query` là tập con token của `candidate` (khớp lỏng tên).
    public static bool TokenSubsetMatch(string query, string candidate)
    {
        var q = Norm(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var c = new HashSet<string>(Norm(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return q.Length > 0 && q.All(c.Contains);
    }

    // ─── Resolvers (gọi TourKit.Api /api/ai/*, lọc bằng TokenSubsetMatch) ─────────

    /// Tên khách hàng → customerId. Nguồn: /api/ai/customers?filter={name} (AiCustomerItem: id, fullName, phone).
    public async Task<ResolveOutcome> ResolveCustomerAsync(string jwt, string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new ResolveOutcome(null, null);

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt,
                $"/api/ai/customers?pageSize={PageSize}&filter=" + Uri.EscapeDataString(name), ct);
        }
        catch (TourKitApiException ex)
        {
            _log.LogWarning(ex, "[ActionResolver] resolve customer '{Name}' lỗi gọi TourKit", name);
            return new ResolveOutcome(null, null);
        }

        var candidates = new List<(int Id, string Label, string? Hint)>();
        if (PropCI(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetId(it, "id", out var id)) continue;
                var full = GetStr(it, "fullName");
                // Khớp theo tên (token-subset) HOẶC theo MÃ/định danh khác: user có thể nhập "KH_00041133"
                // (mã KH) hoặc SĐT — model đưa vào customerName/customerId. Server đã filter, ta match thêm
                // exact (không dấu, case-insensitive) trên MỌI field chuỗi (code/phone/email…) để bắt đúng.
                var matches = (!string.IsNullOrWhiteSpace(full) && TokenSubsetMatch(name, full!))
                    || AnyFieldExactMatch(it, name);
                if (!matches) continue;
                candidates.Add((id, string.IsNullOrWhiteSpace(full) ? name : full!,
                    GetStr(it, "phone") ?? GetStr(it, "email") ?? GetStr(it, "code")));
            }

        return Pick(candidates);
    }

    /// So khớp query với BẤT KỲ field chuỗi nào của item theo exact (chuẩn hóa không dấu, bỏ khoảng
    /// trắng, case-insensitive) — dùng để bắt mã KH ("KH_00041133"), SĐT, email khi user/model không đưa
    /// tên. Chỉ exact để tránh false-positive (không dùng contains).
    private static bool AnyFieldExactMatch(JsonElement it, string query)
    {
        if (it.ValueKind != JsonValueKind.Object) return false;
        var q = Norm(query).Replace(" ", "");
        if (q.Length == 0) return false;
        foreach (var p in it.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var v = Norm(p.Value.GetString() ?? "").Replace(" ", "");
            if (v.Length > 0 && v == q) return true;
        }
        return false;
    }

    /// Tên nhân viên → employeeId (staffId). Nguồn: /api/ai/employee-performance?employeeName={name}
    /// (AiEmployeePerformanceItem: employeeId, fullName) — cùng nguồn resolver marketName/employeeName
    /// hiện có trong JsonPlannerAgent.ResolveEmployeeAsync (đọc nhân viên trong phạm vi report).
    public async Task<ResolveOutcome> ResolveStaffAsync(string jwt, string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new ResolveOutcome(null, null);

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt,
                "/api/ai/employee-performance?employeeName=" + Uri.EscapeDataString(name), ct);
        }
        catch (TourKitApiException ex)
        {
            _log.LogWarning(ex, "[ActionResolver] resolve staff '{Name}' lỗi gọi TourKit", name);
            return new ResolveOutcome(null, null);
        }

        var candidates = new List<(int Id, string Label, string? Hint)>();
        if (PropCI(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetId(it, "employeeId", out var id)) continue;
                var full = GetStr(it, "fullName");
                // Match theo:
                //  1) fullName token-subset — user nói tên hiển thị chuẩn ("Nguyễn Văn A")
                //  2) AnyFieldExactMatch — bắt user_name (login) / mã / email khi tenant chưa cập nhật
                //     full_name chuẩn (nhiều DB chỉ có user_name = "sale01"). Voice TRAVAI: user
                //     đọc tên tài khoản của đồng nghiệp vẫn resolve được.
                var matchesFull = !string.IsNullOrWhiteSpace(full) && TokenSubsetMatch(name, full!);
                if (!matchesFull && !AnyFieldExactMatch(it, name)) continue;
                // Label ưu tiên fullName (hiển thị đẹp trên thẻ xác nhận); fallback userName nếu
                // fullName rỗng để user vẫn nhận diện được ai đang được chọn.
                var label = !string.IsNullOrWhiteSpace(full) ? full!
                    : (GetStr(it, "userName") ?? name);
                candidates.Add((id, label, GetStr(it, "branch") ?? GetStr(it, "email") ?? GetStr(it, "userName")));
            }

        return Pick(candidates);
    }

    /// Tên khách/tiêu đề của cơ hội bán hàng → dealId (bookingTicketId). Nguồn:
    /// /api/ai/booking-tickets?keyword={name} (AiBookingTicketItem: id, customerName, title, code, statusName).
    public async Task<ResolveOutcome> ResolveDealAsync(string jwt, string query, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new ResolveOutcome(null, null);

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt,
                $"/api/ai/booking-tickets?pageSize={PageSize}&keyword=" + Uri.EscapeDataString(query), ct);
        }
        catch (TourKitApiException ex)
        {
            _log.LogWarning(ex, "[ActionResolver] resolve deal '{Query}' lỗi gọi TourKit", query);
            return new ResolveOutcome(null, null);
        }

        var candidates = new List<(int Id, string Label, string? Hint)>();
        if (PropCI(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetId(it, "id", out var id)) continue;
                var customerName = GetStr(it, "customerName");
                var title = GetStr(it, "title");
                var matches = (!string.IsNullOrWhiteSpace(customerName) && TokenSubsetMatch(query, customerName!))
                    || (!string.IsNullOrWhiteSpace(title) && TokenSubsetMatch(query, title!));
                if (!matches) continue;

                var label = !string.IsNullOrWhiteSpace(title)
                    ? $"{customerName ?? "(không tên)"} — {title}"
                    : customerName ?? "(không tên)";
                candidates.Add((id, label, GetStr(it, "code") ?? GetStr(it, "statusName")));
            }

        return Pick(candidates);
    }

    /// Toàn bộ nhân viên (không lọc tên) — nguồn cho dropdown "Người phụ trách" trên thẻ xác nhận UI
    /// (assign_task). Nguồn: /api/ai/employee-performance KHÔNG filter → trả TẤT CẢ nhân viên theo
    /// quyền (mirror JsonPlannerAgent.GetEmployeesAsync). Không cache — tần suất gọi thấp (chỉ khi
    /// build proposal assign_task, không phải hot path đọc số liệu).
    public async Task<List<StaffCandidate>> ListStaffAsync(string jwt, CancellationToken ct)
    {
        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt, "/api/ai/employee-performance", ct);
        }
        catch (TourKitApiException ex)
        {
            _log.LogWarning(ex, "[ActionResolver] list staff lỗi gọi TourKit");
            return new List<StaffCandidate>();
        }

        var list = new List<StaffCandidate>();
        if (PropCI(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetId(it, "employeeId", out var id)) continue;
                var full = GetStr(it, "fullName");
                if (string.IsNullOrWhiteSpace(full)) continue;
                list.Add(new StaffCandidate(id, full!, GetStr(it, "branch") ?? GetStr(it, "email")));
            }
        return list;
    }

    /// Tên workflow/board (giao việc) → workflowId. KHÔNG có endpoint đọc danh sách task-workflow
    /// đã xác minh (grep docs/ai-api-guide.md + ChatTools chỉ thấy workFlowId là PARAM lọc của tool
    /// `tasks`, không có tool liệt kê workflow/board). Trả graceful "không thấy" thay vì đoán path —
    /// wiring thật (endpoint đọc board list bên TourKit.Api) cần task/người sau xác nhận rồi bổ sung.
    public async Task<ResolveOutcome> ResolveWorkflowAsync(string jwt, string name, CancellationToken ct)
    {
        await Task.CompletedTask;
        _log.LogInformation(
            "[ActionResolver] ResolveWorkflowAsync('{Name}') — chưa có endpoint liệt kê workflow/board đã xác minh, trả không thấy",
            name);
        return new ResolveOutcome(null, null);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static ResolveOutcome Pick(List<(int Id, string Label, string? Hint)> candidates)
    {
        if (candidates.Count == 0) return new ResolveOutcome(null, null);
        if (candidates.Count == 1) return new ResolveOutcome(candidates[0].Id, candidates[0].Label, Hint: candidates[0].Hint);
        var choices = candidates
            .Select(c => new ActionChoice(c.Id.ToString(), c.Label, c.Hint))
            .ToList();
        return new ResolveOutcome(null, null, choices);
    }

    private static bool PropCI(JsonElement el, string name, out JsonElement v)
    {
        v = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        return false;
    }

    private static bool TryGetId(JsonElement el, string name, out int id)
    {
        id = 0;
        return PropCI(el, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out id);
    }

    private static string? GetStr(JsonElement el, string name)
        => PropCI(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public record ResolveOutcome(int? Id, string? Label, List<ActionChoice>? Ambiguous = null, string? Hint = null);

/// 1 nhân viên cho dropdown "Người phụ trách" (ActionResolver.ListStaffAsync).
public record StaffCandidate(int Id, string Name, string? Hint);
