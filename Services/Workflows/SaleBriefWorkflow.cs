using System.Globalization;
using System.Text.Json;
using Dapper;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Bản tin sáng cho nhân viên bán hàng. Chạy mỗi 60' rồi tự chọn ai "đến giờ".
///
/// <para><b>Mỗi người lấy dữ liệu bằng PHIÊN CỦA CHÍNH MÌNH</b> — không dùng tài khoản tự động.
/// Đây là điểm khác plan gốc và là quyết định có chủ đích: tài khoản tự động có quyền xem toàn
/// công ty, nếu lọc sai một dòng thì nhân viên A đọc được cơ hội của nhân viên B. Dùng token của
/// chính họ thì <b>CRM tự chặn</b> — lọc sai cũng chỉ thiếu, không lộ.</para>
///
/// <para>Điều kiện thay thế: người đó phải từng đăng nhập ít nhất 1 lần. Nhẹ hơn tưởng —
/// <see cref="TkSessionStore"/> giữ mật khẩu (mã hoá) và tự đăng nhập lại khi token hết hạn,
/// lưu tới 30 ngày. Không có phiên → bỏ qua người đó và ghi rõ lý do, KHÔNG fail cả lượt.</para>
///
/// <para><b>Không gọi AI</b> → không tốn lượt. Nguồn nào lỗi thì mục đó rỗng, bản tin vẫn gửi:
/// thà thiếu một mục còn hơn sáng ra không có bản tin nào.</para>
/// </summary>
public class SaleBriefWorkflow : IScheduledWorkflow
{
    private const int SilentDaysMin = 3;      // im lặng bao nhiêu ngày thì đưa vào "cần gọi lại"
    private const int VipSleepDays = 60;      // khách hạng A/B bao lâu không mua lại thì nhắc
    private const int StaleQuoteDays = 5;     // báo giá bao lâu không ai sửa thì nhắc
    private const int HygieneStuckDays = 14;  // cơ hội kẹt 1 trạng thái bao lâu thì coi là cần dọn

    private readonly DigestSubscriptionRepository _subs;
    private readonly TkSessionStore _sessions;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TourKitApiClient _api;
    private readonly TourkitAiDb _db;
    private readonly MailRepository _mails;
    private readonly DigestDispatcher _dispatcher;
    private readonly ILogger<SaleBriefWorkflow> _log;

    public SaleBriefWorkflow(DigestSubscriptionRepository subs, TkSessionStore sessions,
        TkSessionRepository sessionRepo, TourKitApiClient api, TourkitAiDb db,
        MailRepository mails, DigestDispatcher dispatcher, ILogger<SaleBriefWorkflow> log)
    {
        _subs = subs; _sessions = sessions; _sessionRepo = sessionRepo; _api = api;
        _db = db; _mails = mails; _dispatcher = dispatcher; _log = log;
    }

    public string Type => "sale-brief";
    public string Label => "Bản tin sáng cho nhân viên bán hàng";
    public string Description => "Mỗi sáng gom việc cần làm (cơ hội cần gọi, lịch hẹn, việc, báo giá) gửi từng người đã đăng ký. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var utcNow = DateTime.UtcNow;
        var todayVn = DigestDue.NowVn(utcNow).Date;

        var due = (await _subs.ListEnabledAsync(tenantId, BriefTypes.Sale, ct))
            .Where(s => DigestDue.IsDue(s, utcNow)).ToList();
        if (due.Count == 0)
            return new(true, "Chưa tới giờ gửi của ai (0 đăng ký đến hạn).", null);

        // Hộp thư là số của CẢ CÔNG TY nên đọc 1 lần, dùng cho mọi người nhận.
        int mailPending = 0, mailQuote = 0;
        bool mailOk = true;
        try
        {
            var c = _mails.Counts(tenantId);
            mailPending = c.ByStatus.TryGetValue("moi", out var m) ? m : 0;
            mailQuote = c.ByCategory.TryGetValue("xin_bao_gia", out var q) ? q : 0;
        }
        catch (Exception ex)
        {
            mailOk = false;
            _log.LogWarning("[sale-brief] tenant={T} đọc hộp thư lỗi: {Err}", tenantId, ex.Message);
        }

        int sent = 0, noSession = 0, failed = 0;
        var parts = new List<string>();

        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var session = await _sessionRepo.GetByUserAsync(tenantId, sub.Username, ct);
                if (session == null)
                {
                    noSession++;
                    _log.LogInformation("[sale-brief] tenant={T} user={U} chưa đăng nhập lần nào — bỏ qua",
                        tenantId, sub.Username);
                    continue;
                }

                // Token của CHÍNH người này; tự đăng nhập lại nếu hết hạn (user không thấy gì).
                var jwt = await _sessions.GetValidJwtAsync(session.Id, ct);
                var crmUserId = await _sessions.EnsureCrmUserIdAsync(session.Id, ct);

                var input = await BuildInputAsync(tenantId, sub.Username, session.FullName,
                    crmUserId, jwt, todayVn, mailPending, mailQuote, mailOk, ct);

                var msg = SaleBriefBuilder.Build(input, todayVn);
                var chSummary = await _dispatcher.SendAsync(sub, msg, ct);
                await _subs.MarkSentAsync(tenantId, sub.Username, BriefTypes.Sale, utcNow, todayVn, ct);
                sent++;
                parts.Add($"{sub.Username}[{chSummary}]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "[sale-brief] tenant={T} user={U} gửi lỗi", tenantId, sub.Username);
                parts.Add($"{sub.Username}[LỖI: {ex.Message}]");
            }
        }

        var summary = $"{due.Count} đăng ký đến giờ → gửi {sent}"
                    + (noSession > 0 ? $", bỏ qua {noSession} (chưa đăng nhập lần nào)" : "")
                    + (failed > 0 ? $", lỗi {failed}" : "")
                    + (parts.Count > 0 ? ". " + string.Join(" · ", parts) : "");
        _log.LogInformation("[sale-brief] tenant={T} {Sum}", tenantId, summary);
        return new(true, summary, null);
    }

    /// <summary>
    /// Gom dữ liệu cho 1 người. MỖI nguồn bọc try riêng: nguồn nào lỗi thì mục đó rỗng,
    /// các mục còn lại vẫn có. Bản tin thiếu một mục vẫn hơn không có bản tin.
    /// </summary>
    private async Task<SaleBriefInput> BuildInputAsync(string tenantId, string user, string? fullName,
        int? crmUserId, string jwt, DateTime todayVn,
        int mailPending, int mailQuote, bool mailOk, CancellationToken ct)
    {
        var cooling = new List<DealLine>();
        var hygiene = new List<DealLine>();
        var appts = new List<ApptLine>();
        var tasks = new List<TaskLine>();
        var vips = new List<CustomerLine>();
        var quotes = new List<QuoteLine>();
        var payments = new List<PaymentAlert>();
        int overdueAppt = 0, overdueTask = 0;

        // Điểm AI đã chấm (nếu có) — chỉ để hiện % khả năng chốt, thiếu thì để 0.
        var winRates = await LoadWinRatesAsync(tenantId, ct);

        // ── Cơ hội bán hàng: cần gọi lại + cần dọn ────────────────────────────
        await Safe("booking-tickets", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/booking-tickets?pageIndex=1&pageSize=100", ct);
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                var code = Str(it, "code") ?? "";
                var title = Str(it, "title") ?? Str(it, "customerName") ?? code;
                var silent = Int(it, "coolingDays");
                var status = Str(it, "statusName");
                var wr = winRates.TryGetValue(code, out var w) ? w : 0;
                var line = new DealLine(0, title, Str(it, "customerName"), wr, silent, status);

                if (Bool(it, "isCooling") || silent >= SilentDaysMin) cooling.Add(line);
                if (silent >= HygieneStuckDays) hygiene.Add(line);
            }
            // Nguội lâu nhất lên đầu — đó là cái dễ mất nhất.
            cooling.Sort((a, b) => b.SilentDays.CompareTo(a.SilentDays));
            hygiene.Sort((a, b) => b.SilentDays.CompareTo(a.SilentDays));
        });

        // ── Lịch hẹn hôm nay + quá hạn ────────────────────────────────────────
        await Safe("appointments", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=1&pageIndex=1&pageSize=50", ct);
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                appts.Add(new ApptLine(
                    Str(it, "scheduleTimeFormatted") ?? "",
                    Str(it, "title") ?? "Lịch hẹn",
                    Str(it, "customerName")));
            }
            var od = await _api.GetAsync(jwt, "/api/ai/appointments?dateFilter=3&pageIndex=1&pageSize=50", ct);
            overdueAppt = Items(od).Count(it => Mine(it, user, fullName, crmUserId));
        });

        // ── Việc cần làm hôm nay + trễ hạn ────────────────────────────────────
        await Safe("tasks", async () =>
        {
            var d = await _api.GetAsync(jwt, "/api/ai/tasks?tabFilter=3&pageIndex=1&pageSize=50", ct);
            foreach (var it in Items(d))
                tasks.Add(new TaskLine(Str(it, "name") ?? Str(it, "code") ?? "Công việc",
                                       Str(it, "priorityName"), IsOverdue: false));
            var od = await _api.GetAsync(jwt, "/api/ai/tasks?tabFilter=2&pageIndex=1&pageSize=50", ct);
            var late = Items(od).ToList();
            overdueTask = late.Count;
            // Việc trễ chèn LÊN ĐẦU và đánh dấu — đó là thứ cần làm trước.
            for (int i = late.Count - 1; i >= 0; i--)
                tasks.Insert(0, new TaskLine(Str(late[i], "name") ?? "Công việc",
                                             Str(late[i], "priorityName"), IsOverdue: true));
        });

        // ── Tour sắp đi còn thiếu tiền (của mình) ─────────────────────────────
        await Safe("tours", async () =>
        {
            var to = todayVn.AddDays(7);
            var d = await _api.GetAsync(jwt,
                $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);
            var rows = new List<TourPaymentRow>();
            foreach (var it in Items(d))
            {
                if (!Mine(it, user, fullName, crmUserId)) continue;
                if (!TryDec(it, "actualRevenue", out var actual)) continue;   // xem PaymentWatchdogWorkflow
                if (!TryDate(it, "departureDate", out var dep)) continue;
                rows.Add(new TourPaymentRow(Int(it, "id"), Str(it, "title") ?? "Tour",
                    Str(it, "customerName"), Str(it, "sellerName"), dep, Dec(it, "revenue"), actual));
            }
            payments = PaymentWatchdogRule.Evaluate(rows, todayVn);
        });

        // ── Khách hạng A/B lâu không mua lại (bảng Reviews của proxy) ─────────
        await Safe("reviews", async () =>
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<(string CustomerId, string Rank, long GeneratedAt)>(@"
SELECT CustomerId, [Rank], GeneratedAt FROM dbo.Reviews
WHERE TenantId = @tenantId AND [Rank] IN ('A','B')", new { tenantId });
            foreach (var r in rows)
            {
                var days = (int)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(r.GeneratedAt)).TotalDays;
                if (days >= VipSleepDays) vips.Add(new CustomerLine(r.CustomerId, r.Rank, days));
            }
            vips.Sort((a, b) => b.DaysSinceLastBooking.CompareTo(a.DaysSinceLastBooking));
        });

        // ── Báo giá của mình lâu chưa cập nhật ────────────────────────────────
        await Safe("quotes", async () =>
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<(string? Title, string? CustomerName, DateTime UpdatedAt)>(@"
SELECT Title, CustomerName, UpdatedAt FROM dbo.TourQuotes
WHERE TenantId = @tenantId AND CreatedBy = @user
  AND UpdatedAt < DATEADD(DAY, -@days, SYSUTCDATETIME())
ORDER BY UpdatedAt ASC", new { tenantId, user, days = StaleQuoteDays });
            foreach (var r in rows)
                quotes.Add(new QuoteLine(r.Title ?? "Báo giá", r.CustomerName,
                    (int)(DateTime.UtcNow - r.UpdatedAt).TotalDays));
        });

        return new SaleBriefInput(user, fullName, cooling, appts, vips, quotes,
            mailPending, mailQuote, hygiene, payments, mailOk,
            TodayTasks: tasks, OverdueTaskCount: overdueTask, OverdueAppointments: overdueAppt);

        // Bọc từng nguồn: lỗi 1 nguồn KHÔNG được làm mất cả bản tin.
        async Task Safe(string name, Func<Task> f)
        {
            try { await f(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("[sale-brief] tenant={T} user={U} nguồn '{N}' lỗi: {Err}",
                    tenantId, user, name, ex.Message);
            }
        }
    }

    /// Map mã cơ hội → % khả năng chốt từ điểm AI đã lưu. Chưa chấm thì không có mặt.
    private async Task<Dictionary<string, int>> LoadWinRatesAsync(string tenantId, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<(string DealId, int? WinRate)>(
                "SELECT DealId, WinRate FROM dbo.DealScores WHERE TenantId = @tenantId", new { tenantId });
            foreach (var r in rows) map[r.DealId] = r.WinRate ?? 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[sale-brief] tenant={T} đọc điểm deal lỗi: {Err}", tenantId, ex.Message);
        }
        return map;
    }

    /// <summary>
    /// Dòng này có thuộc người nhận không? Người có quyền xem toàn công ty dùng token của chính
    /// mình VẪN thấy hết, nên phải lọc thêm. So theo id CRM trước (chắc nhất), rồi tới họ tên,
    /// cuối cùng là tên đăng nhập. KHÔNG có thông tin phụ trách → coi là KHÔNG phải của mình,
    /// thà thiếu còn hơn nhắc việc người khác.
    /// </summary>
    private static bool Mine(JsonElement it, string username, string? fullName, int? crmUserId)
    {
        if (crmUserId != null)
        {
            foreach (var f in new[] { "assigneeId", "nhanVienPhuTrachId", "sellerId", "insUid" })
                if (it.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number
                    && v.GetInt32() == crmUserId.Value) return true;
        }
        foreach (var f in new[] { "assignee", "assignees", "sellerName", "createdBy", "nhanVienPhuTrach" })
        {
            var s = Str(it, f);
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!string.IsNullOrWhiteSpace(fullName) && s!.Contains(fullName!, StringComparison.OrdinalIgnoreCase)) return true;
            if (s!.Contains(username, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── Đọc JSON (envelope /api/ai/* camelCase) ───────────────────────────────
    private static IEnumerable<JsonElement> Items(JsonElement d)
        => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("items", out var it)
           && it.ValueKind == JsonValueKind.Array
            ? it.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bool(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal Dec(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static bool TryDec(JsonElement e, string n, out decimal val)
    {
        val = 0m;
        if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetDecimal(); return true; }
        return false;
    }

    private static bool TryDate(JsonElement e, string n, out DateTime val)
    {
        val = default;
        return DateTime.TryParse(Str(e, n), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out val);
    }
}
