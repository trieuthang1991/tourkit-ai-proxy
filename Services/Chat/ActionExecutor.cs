using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TourkitAiProxy.Models;              // ActionExecuteRequest, ActionResult, ActionChoice
using TourkitAiProxy.Services.Crm;        // CrmActionQueueRepository, CrmActionInput, CrmActionKind

namespace TourkitAiProxy.Services.Chat;

/// Thực thi 1 hành động đã xác nhận. Định tuyến theo ActionKind (tra ActionTools.Find).
/// Task 8a: chỉ implement nhánh CrmQueue (assign_task / create_appointment). Review/deal (Internal)
/// và mail (Mail) throw NotImplementedException("8b") — sẽ điền ở task sau, giữ file compile + test được.
public class ActionExecutor
{
    private readonly CrmActionQueueRepository _crmQueue;
    private readonly ActionResolver _resolver;
    private readonly ILogger<ActionExecutor> _log;

    /// Chống thực thi trùng khi user bấm "Xác nhận" 2 lần / SSE retry gửi lại cùng actionId.
    /// Chỉ cache khi ENQUEUE thành công — kết quả "không tìm thấy/mơ hồ" KHÔNG cache để user
    /// sửa tên rồi thử lại với cùng actionId vẫn re-resolve được.
    private static readonly ConcurrentDictionary<string, ActionResult> _done = new();

    public ActionExecutor(CrmActionQueueRepository crmQueue, ActionResolver resolver, ILogger<ActionExecutor> log)
    { _crmQueue = crmQueue; _resolver = resolver; _log = log; }

    // ─── Pure payload builders (test được — xem ActionExecutorTests) ─────────────

    /// Dựng PayloadJson khớp CreateOrUpdateTaskingRequest cho assign-task.
    /// LƯU Ý: workflowId nullable — proxy KHÔNG resolve workflow (không có endpoint list ổn định);
    /// truyền workflowName THÔ để worker app-side tự resolve/đặt default.
    public static string BuildAssignTaskPayload(
        int? workflowId, string? workflowName, string name, string? content, string staffsInChargeCsv,
        int prioritized, DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        int? bookingTicketId)
        => JsonSerializer.Serialize(new
        {
            id = 0, workflowId, workflowName, name, content,
            staffsInCharge = staffsInChargeCsv,
            prioritized, status = 1,
            startDate = startUtc, endDate = endUtc,
            appointmentReminder = reminderMinutes,
            bookingTicketId
        });

    /// Dựng PayloadJson khớp CreateCustomerCareRequest cho create-appointment.
    public static string BuildAppointmentPayload(
        int customerId, string careTitle, string? careDetail,
        DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        string? customerName, string? customerPhone, int? bookingTicketId)
        => JsonSerializer.Serialize(new
        {
            customerId, careTitle, careDetail,
            careStartTime = startUtc, careEndTime = endUtc,
            status = 1, appointmentReminder = reminderMinutes,
            bookingTicketId, customerName, customerPhone
        });

    /// Map "cao|tb|thap" → Prioritized (0..3).
    public static int MapPriority(string? p) => (p ?? "").Trim().ToLowerInvariant() switch
    {
        "cao" or "high" => 1,
        "tb" or "trung binh" or "trung bình" or "medium" => 2,
        "thap" or "thấp" or "low" => 3,
        _ => 0
    };

    // ─── Execute ───────────────────────────────────────────────────────────────

    /// Thực thi hành động đã xác nhận. Trả ActionResult. Idempotent theo ActionId.
    public async Task<ActionResult> ExecuteAsync(
        ActionExecuteRequest req, string tenantId, string jwt, string username,
        string? sessionId, CancellationToken ct)
    {
        var tool = ActionTools.Find(req.Action)
            ?? throw new InvalidOperationException($"Unknown action: {req.Action}");

        switch (tool.Kind)
        {
            case ActionKind.CrmQueue:
                return await ExecuteCrmQueueAsync(req, tenantId, username, jwt, ct);
            case ActionKind.Internal:
            case ActionKind.Mail:
                throw new NotImplementedException("8b"); // review/deal/mail — task 8b
            default:
                throw new InvalidOperationException($"Unhandled kind {tool.Kind}");
        }
    }

    private async Task<ActionResult> ExecuteCrmQueueAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        if (_done.TryGetValue(req.ActionId, out var cached)) return cached;

        var (result, success) = req.Action.ToLowerInvariant() switch
        {
            "assign_task" => await ExecuteAssignTaskAsync(req, tenantId, username, jwt, ct),
            "create_appointment" => await ExecuteCreateAppointmentAsync(req, tenantId, username, jwt, ct),
            _ => throw new InvalidOperationException($"Unhandled CrmQueue action: {req.Action}")
        };

        if (success) _done[req.ActionId] = result;
        return result;
    }

    private async Task<(ActionResult Result, bool Success)> ExecuteAssignTaskAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        var p = req.Params ?? new Dictionary<string, object?>();
        var name = Str(p, "name") ?? "Việc mới";
        var content = Str(p, "content");
        var workflowName = Str(p, "workflowName");
        var staffNamesRaw = Str(p, "staffNames");
        var prioritized = MapPriority(Str(p, "prioritized"));
        var startUtc = ParseUtc(Str(p, "startDate"));
        var dueUtc = ParseUtc(Str(p, "dueDate"));
        var reminderMinutes = Int(p, "reminderMinutes") ?? 0;
        var bookingTicketId = Int(p, "bookingTicketId");

        var staffIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(staffNamesRaw))
        {
            foreach (var raw in staffNamesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var outcome = await _resolver.ResolveStaffAsync(jwt, raw, ct);
                if (outcome.Ambiguous is { Count: > 0 })
                    return (new ActionResult(req.Action,
                        $"Tên nhân viên \"{raw}\" khớp nhiều người, vui lòng nói rõ hơn (vd họ tên đầy đủ)."), false);
                if (outcome.Id is null)
                    return (new ActionResult(req.Action, $"Không tìm thấy nhân viên tên \"{raw}\"."), false);
                staffIds.Add(outcome.Id.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        var staffCsv = string.Join(',', staffIds);

        var payload = BuildAssignTaskPayload(
            null, workflowName, name, content, staffCsv, prioritized, startUtc, dueUtc, reminderMinutes, bookingTicketId);

        await _crmQueue.EnqueueAsync(new CrmActionInput(tenantId, username, CrmActionKind.AssignTask, payload), ct);
        _log.LogInformation(
            "[ActionExecutor] enqueue assign_task tenant={Tenant} user={User} name={Name}",
            tenantId, username, name);

        return (new ActionResult(req.Action, "✅ Đã đưa vào hàng đợi — hệ thống sẽ tạo việc trong CRM ít phút nữa."), true);
    }

    private async Task<(ActionResult Result, bool Success)> ExecuteCreateAppointmentAsync(
        ActionExecuteRequest req, string tenantId, string username, string jwt, CancellationToken ct)
    {
        var p = req.Params ?? new Dictionary<string, object?>();
        var careTitle = Str(p, "careTitle") ?? "Lịch hẹn";
        var careDetail = Str(p, "careDetail");
        var startUtc = ParseUtc(Str(p, "startTime"));
        var endUtc = ParseUtc(Str(p, "endTime"));
        var reminderMinutes = Int(p, "reminderMinutes") ?? 0;
        var bookingTicketId = Int(p, "bookingTicketId");

        int customerId;
        var customerName = Str(p, "customerName");
        var customerIdParam = Int(p, "customerId");
        if (customerIdParam is { } cid)
        {
            customerId = cid;
        }
        else if (!string.IsNullOrWhiteSpace(customerName))
        {
            var outcome = await _resolver.ResolveCustomerAsync(jwt, customerName, ct);
            if (outcome.Ambiguous is { Count: > 0 })
                return (new ActionResult(req.Action,
                    $"Tên khách hàng \"{customerName}\" khớp nhiều người, vui lòng nói rõ hơn."), false);
            if (outcome.Id is null)
                return (new ActionResult(req.Action, $"Không tìm thấy khách hàng tên \"{customerName}\"."), false);
            customerId = outcome.Id.Value;
            customerName = outcome.Label ?? customerName;
        }
        else
        {
            return (new ActionResult(req.Action, "Thiếu thông tin khách hàng để tạo lịch hẹn."), false);
        }

        var customerPhone = Str(p, "customerPhone");

        var payload = BuildAppointmentPayload(
            customerId, careTitle, careDetail, startUtc, endUtc, reminderMinutes,
            customerName, customerPhone, bookingTicketId);

        await _crmQueue.EnqueueAsync(new CrmActionInput(tenantId, username, CrmActionKind.CreateAppointment, payload), ct);
        _log.LogInformation(
            "[ActionExecutor] enqueue create_appointment tenant={Tenant} user={User} customerId={CustomerId}",
            tenantId, username, customerId);

        return (new ActionResult(req.Action, "✅ Đã đưa vào hàng đợi — hệ thống sẽ tạo lịch hẹn trong CRM."), true);
    }

    // ─── Loose param readers (Params dict values đến từ JSON deserialize → JsonElement,
    //     hoặc string/số thô khi construct trực tiếp trong test) ────────────────────

    private static string? Str(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        if (v is JsonElement je)
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => je.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.GetRawText()
            };
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? Int(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var n2)) return n2;
            return null;
        }
        if (v is int i) return i;
        return int.TryParse(v.ToString(), out var n3) ? n3 : null;
    }

    private static DateTime? ParseUtc(string? s)
        => !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(
               s, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
}
