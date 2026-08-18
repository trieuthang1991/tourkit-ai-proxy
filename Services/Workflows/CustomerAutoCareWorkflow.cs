using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// <summary>
/// Nhắc chăm lại khách ngủ quên (S6). Tìm khách đáng chăm mà lâu không ai đụng tới → gom thành MỘT
/// danh sách trong Bảng tin để nhân viên gọi.
///
/// <para><b>KHÔNG gửi gì cho khách.</b> Lộ trình gốc hình dung tính năng này tự soạn và gửi thư
/// chăm sóc. Đo dữ liệu thật (15/08, hai tenant): <b>số điện thoại có ở 100/100 khách, email chỉ
/// 14/100</b>. Gửi thư tự động vừa với tới được một phần bảy tệp khách, vừa là thứ rủi ro nhất cả
/// lộ trình vì nó đi ra ngoài công ty. Việc đúng với dữ liệu đang có là NHẮC GỌI — và gọi thì phải
/// người gọi, đúng nguyên tắc "người dùng quyết định thay vì tự động".</para>
///
/// <para><b>KHÔNG gọi AI</b> — luật thuần, không tốn lượt.</para>
///
/// <para>MỘT thẻ cho mỗi NHÂN VIÊN, không phải mỗi khách một thẻ: hai mươi thẻ mỗi sáng thì Bảng
/// tin thành nơi không ai mở. Trước 18/08 gom hết vào một thẻ chung cho cả công ty — danh sách
/// chung thì không ai thấy đó là việc của mình, mà đây là danh sách để GỌI ĐIỆN. Khách chưa gán
/// người phụ trách thì bỏ qua (đếm vào tóm tắt), không đổ vào thẻ chung.</para>
/// </summary>
public class CustomerAutoCareWorkflow : IScheduledWorkflow
{
    private const int KeepInsightDays = 90;
    /// Lấy tối đa bao nhiêu khách về để lọc. CRM phân trang; quét cả tệp mỗi lần là quá tốn.
    private const int FetchPageSize = 200;
    /// <summary>
    /// Trần số trang mỗi nhóm lọc. 30 trang × 200 = 6.000 khách — thừa cho mọi công ty đã đo
    /// (erp 1.204 khách cần nhắc, vnexpresstour 3.207). Có trần vì đây là tác vụ chạy nền: một
    /// tenant dữ liệu bất thường không được phép kéo vô hạn rồi làm chậm CRM của mọi người.
    /// Chạm trần thì NÓI RA trong tóm tắt, không im lặng cắt bớt.
    /// </summary>
    private const int MaxPages = 30;

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly NotifyLedgerRepository _ledger;
    private readonly ILogger<CustomerAutoCareWorkflow> _log;

    public CustomerAutoCareWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, NotifyLedgerRepository ledger,
        ILogger<CustomerAutoCareWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _ledger = ledger; _log = log; }

    public string Type => "customer-auto-care";
    public string Label => "Nhắc chăm lại khách ngủ quên";
    public string Description => "Tìm khách đã từng mua mà lâu không ai chăm → gom thành một danh sách trong Bảng tin để nhân viên gọi. KHÔNG gửi gì cho khách. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    /// Có ngưỡng "im bao lâu" + hạng nào đáng chăm → công ty phải khai cho hợp ngành mình.
    public bool HasCompanyRules => true;

    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['customer-auto-care'] bên workflow-options.jsx.
    private record Options(int QuietDays, List<string> Ranks, bool RequireBought, int MaxLeads,
        int RemindGapDays, int MaxReminders);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(QuietDays: 90, Ranks: new List<string>(), RequireBought: true, MaxLeads: 20,
            RemindGapDays: 7, MaxReminders: 3);
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            int Num(string k, int dv, int lo, int hi)
                => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                    ? Math.Clamp(n, lo, hi) : dv;
            bool Bit(string k, bool dv)
                => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : dv;
            List<string> Strs(string k, List<string> dv)
            {
                if (!r.TryGetProperty(k, out var arr) || arr.ValueKind != JsonValueKind.Array) return dv;
                var o = new List<string>();
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        o.Add(e.GetString()!);
                return o;   // rỗng = MỌI hạng, là ý hợp lệ chứ không phải "chưa khai"
            }
            return def with
            {
                QuietDays = Num("quietDays", def.QuietDays, 7, 3650),
                Ranks = Strs("ranks", def.Ranks),
                RequireBought = Bit("requireBought", def.RequireBought),
                MaxLeads = Num("maxLeads", def.MaxLeads, 1, 50),
                RemindGapDays = Num("remindGapDays", def.RemindGapDays, 0, 365),
                MaxReminders = Num("maxReminders", def.MaxReminders, 0, 20),
            };
        }
        catch (JsonException) { return def; }
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new(false, null, "TenantId rỗng — kiểm tra dbo.UserWorkflows");

        var opt = ParseOptions(optionsJson);

        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động (trang Tự động hóa) — cần nó để đọc danh sách khách.");

        string jwt;
        try
        {
            var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
            jwt = await _sessions.GetValidJwtAsync(sid, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[customer-auto-care] tenant={T} đăng nhập tài khoản tự động thất bại", tenantId);
            return new(false, null, $"Đăng nhập tài khoản tự động thất bại: {ex.Message}");
        }

        // ── Lọc NGAY TRONG CRM, đừng kéo cả tệp về ───────────────────────────────────
        // Trước đây chỉ gọi ?pageSize=200 không lọc → lấy 200 khách MỚI NHẤT. Đo staging 18/08: tệp
        // khách 22.079 người, tức phủ 0,6% — và phủ nhầm đầu, vì khách CŨ mới là nhóm dễ ngủ quên.
        // Thêm CareFilter thì cả tệp "im ≥90 ngày" còn 131 người, một lời gọi lấy hết, 577ms (lời
        // gọi không lọc là 640ms). Phủ 100% mà không tăng tải.
        //
        // ⚠️ TUYỆT ĐỐI không "nâng pageSize cho nhiều lên" thay vì lọc — đó mới là cách làm sập hệ
        // đang chạy: bên trong SearchAsync có vài mệnh đề IN theo id của trang (ngày chăm gần nhất,
        // gộp doanh thu, tra người phụ trách), nâng số bản ghi là nhân số tham số lên theo, chạm
        // trần 2100 tham số của SQL Server. Còn CareFilter đúng là truy vấn màn Khách hàng của CRM
        // vẫn chạy hằng ngày — kiểu tải đã có, không phải kiểu mới.
        var buckets = AutoCareRule.CareFilterBuckets(opt.QuietDays);
        var raw = new List<JsonElement>();
        int pagesRead = 0; bool truncated = false;
        foreach (var bucket in buckets)
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                JsonElement res;
                try
                {
                    res = await _api.GetAsync(jwt,
                        $"/api/ai/customers?pageSize={FetchPageSize}&pageIndex={page}&careFilter={bucket}", ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[customer-auto-care] tenant={T} đọc nhóm CSKH {B} trang {P} lỗi",
                        tenantId, bucket, page);
                    return new(false, null, $"Không đọc được danh sách khách: {ex.Message}");
                }

                pagesRead++;
                var n = 0;
                if (res.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray()) { raw.Add(it); n++; }
                }
                // Trang không đầy = hết dữ liệu của nhóm này.
                if (n < FetchPageSize) break;
                if (page == MaxPages) truncated = true;
            }
        }

        var customers = new List<CareCustomer>();
        int noCareDate = 0;
        // Xem PaymentWatchdogRule.ResolveOwner: thiếu HẲN trường = API cũ (giữ hành vi cũ),
        // có trường mà rỗng = khách chưa gán ai (bỏ qua). Gộp hai cái là im lặng toàn bộ.
        bool apiHasStaff = false;
        {
            foreach (var it in raw)
            {
                if (!TryInt(it, "id", out var id)) continue;
                var last = TryDate(it, "lastCareDate", out var d) ? d : (DateTime?)null;
                if (last == null) noCareDate++;
                // staffChargeUserName = NV phụ trách khách (customers.INS_UID phía CRM), 98,5% có.
                // KHÔNG dùng `assignee`: tên nghe như người phụ trách nhưng ruột là NGƯỜI TẠO
                // (IdCreator, chỉ 74%) — bàn giao khách xong nó vẫn trỏ về người cũ.
                if (it.TryGetProperty("staffChargeUserName", out _)) apiHasStaff = true;

                customers.Add(new CareCustomer(
                    id,
                    Str(it, "fullName") ?? $"Khách #{id}",
                    Str(it, "phone"), Str(it, "email"),
                    Str(it, "rankName"),
                    Dec(it, "totalRevenue"), (int)Dec(it, "totalTours"),
                    last,
                    StaffUserName: Str(it, "staffChargeUserName")));
            }
        }

        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        // KHÔNG truyền MaxLeads vào đây — cắt phải làm SAU khi lọc “đã nhắc rồi”, không thì
        // 20 khách sộp nhất đã được nhắc sẽ che mất khách thứ 21 chưa nhắc lần nào.
        var leads = AutoCareRule.Find(customers, todayVn, opt.Ranks, opt.QuietDays, opt.RequireBought);

        if (leads.Count == 0)
        {
            var why = noCareDate == customers.Count && customers.Count > 0
                // Nói thẳng nguyên nhân gốc: "0 khách" ở đây không có nghĩa là chăm sóc tốt.
                ? " — không khách nào có ngày chăm sóc gần nhất trong CRM, nên chưa chấm được"
                : "";
            return new(true, $"Quét {customers.Count} khách → không ai tới hạn chăm lại{why}.", null);
        }

        var day = todayVn.ToString("yyyy-MM-dd");

        // ── Chặn nhắc đi nhắc lại ────────────────────────────────────────────────────
        // Khoá chống trùng của Bảng tin có NGÀY nằm trong nó (autocare:{ngày}:{người}) nên chỉ chặn
        // được "chạy lại trong cùng ngày". Sang ngày mới là khoá mới, mà lastCareDate chỉ đổi khi
        // có người thật sự gọi — nên cùng một khách nằm lại trong danh sách MỖI SÁNG, vô hạn.
        // Đo thật trên staging: 9/10 khách của lượt trước xuất hiện nguyên vẹn ở lượt sau, và danh
        // sách phình từ 10 lên 17 vì khách cũ chưa ai gọi vẫn nằm đó. Vài tuần là không ai mở thẻ.
        //
        // Đếm qua dbo.NotifyLedger — sổ dùng chung, đếm theo ĐỐI TƯỢNG chứ không theo thông báo.
        // Ở đây bắt buộc phải vậy: một thẻ chứa N khách nên đếm thông báo không nói được khách nào
        // đã bị nhắc mấy lần. Tác vụ mới cần chặn lặp thì dùng sổ này, đừng dựng bảng riêng.
        //
        // StateStamp = ngày chăm sóc gần nhất. Đổi = đã có người gọi thật → bộ đếm về 0, vòng đời
        // mới. Không có nó thì khách được chăm xong vẫn mang án "đã nhắc đủ 3 lần, thôi", rồi nửa
        // năm sau ngủ quên mà không ai biết.
        const string LedgerScope = "customer-auto-care";
        static string Stamp(CareLead l, IReadOnlyDictionary<int, DateTime?> careDates)
            => careDates.TryGetValue(l.Id, out var d) && d != null ? d.Value.ToString("yyyy-MM-dd") : "";

        var careDateOf = customers.ToDictionary(c => c.Id, c => c.LastCareDate);
        var nowUtc = DateTime.UtcNow;
        var marks = await _ledger.GetAsync(tenantId, LedgerScope,
            leads.Select(l => NotifyLedgerRepository.Subject("customer", l.Id)).ToList(), ct);

        var reminderSkips = new Dictionary<string, int>(StringComparer.Ordinal);
        var fresh = new List<CareLead>();
        foreach (var l in leads)
        {
            var key = NotifyLedgerRepository.Subject("customer", l.Id);
            marks.TryGetValue(key, out var mark);
            var (skip, reason) = NotifyThrottle.Decide(mark, Stamp(l, careDateOf), nowUtc,
                opt.RemindGapDays, opt.MaxReminders);
            if (skip)
            {
                // Gom theo LÝ DO chứ không chỉ đếm tổng: "bỏ 12 khách" không nói được nên chỉnh ô
                // nào, còn "9 vừa nhắc hôm kia · 3 đã đủ 3 lần" thì đọc là biết sửa gì.
                var r = reason ?? "khác";
                reminderSkips[r] = reminderSkips.GetValueOrDefault(r) + 1;
                continue;
            }
            fresh.Add(l);
        }
        var remindedBefore = leads.Count - fresh.Count;
        var qualified = leads.Count;      // tới hạn, TRƯỚC khi bỏ "đã nhắc" và trước khi cắt
        var freshCount = fresh.Count;     // còn lại sau khi bỏ "đã nhắc", vẫn TRƯỚC khi cắt
        // Cắt ở ĐÂY: lọc trước, cắt sau. Danh sách đã xếp khách chi nhiều lên đầu từ AutoCareRule.
        leads = fresh.Take(Math.Max(1, opt.MaxLeads)).ToList();

        if (leads.Count == 0)
            return new(true, $"Quét {customers.Count} khách → {remindedBefore} khách tới hạn nhưng đã nhắc rồi "
                           + $"({string.Join(" · ", reminderSkips.Select(kv => $"{kv.Value} {kv.Key}"))}), không có ai mới.", null);

        // ── Chia theo NGƯỜI PHỤ TRÁCH ────────────────────────────────────────────────
        // Trước đây gom hết vào MỘT thẻ cho cả công ty. Danh sách chung thì không ai thấy đó là
        // việc của mình — mà đây là danh sách để GỌI ĐIỆN, không ai gọi thì tính năng vô nghĩa.
        // Nay mỗi nhân viên một thẻ, chỉ chứa khách của họ.
        //
        // Không xác định được người phụ trách thì BỎ QUA, không rơi về mức công ty (chốt 18/08).
        // Ở đây bỏ qua gần như không mất gì — đo staging 08/2026: 32.529/33.026 khách (98,5%) đã
        // gán người; 496 còn lại là khách dạng lead chưa ai nhận, mà chưa ai nhận thì cũng chưa
        // có ai để nhắc.
        //
        // apiHasStaff=false = API chưa nâng cấp → giữ hành vi cũ (một thẻ chung) chứ không im lặng.
        int noOwner = 0, created = 0, dedupedCards = 0;
        foreach (var g in leads.GroupBy(l => PaymentWatchdogRule.ResolveOwner(l.StaffUserName, apiHasStaff)))
        {
            var (owner, skip) = g.Key;
            if (skip) { noOwner += g.Count(); continue; }

            var mine = g.ToList();
            var lines2 = string.Join("\n", mine.Select(l => $"- {l.Text}"));
            var body2 = $"Những khách này đã từng mua nhưng lâu rồi không ai liên hệ. "
                      + $"Xếp theo mức đã chi, gọi từ trên xuống.\n\n{lines2}";

            // Khoá kèm TÊN NGƯỜI: mỗi người một khoá riêng. Dùng chung khoá theo ngày thì thẻ của
            // người đầu tiên chặn mất thẻ của TẤT CẢ những người sau trong cùng ngày.
            var key = owner.Length == 0 ? $"autocare:{day}" : $"autocare:{day}:{owner}";
            var insId = await _insights.InsertAsync(new AgentInsight(
                Id: 0, TenantId: tenantId,
                Username: owner,
                Kind: "auto-care", Severity: 1,
                Title: $"{mine.Count} khách cũ lâu chưa ai gọi lại",
                Body: body2,
                DataJson: JsonSerializer.Serialize(new
                {
                    day, count = mine.Count,
                    ids = mine.Select(l => l.Id).ToArray(),
                }),
                AlertKey: key,
                IsRead: false, CreatedUtc: DateTime.UtcNow), ct);

            if (insId == null) dedupedCards++; else created++;

            // Chỉ ghi sổ khi thẻ THẬT SỰ được tạo. Thẻ bị chặn vì trùng trong ngày thì chưa ai
            // nhìn thấy gì, đếm là tính oan một lần nhắc — ba lần "oan" là khách rơi khỏi danh
            // sách mà chưa bao giờ được nhắc thật.
            if (insId != null)
                await _ledger.MarkAsync(tenantId, LedgerScope,
                    mine.Select(l => (NotifyLedgerRepository.Subject("customer", l.Id),
                                      (string?)Stamp(l, careDateOf))).ToList(), ct);
        }

        await _insights.PruneAsync(KeepInsightDays, ct);
        await _ledger.PruneAsync(ct: ct);

        // Trình bày theo ĐÚNG thứ tự xử lý: tới hạn → bỏ đã nhắc → cắt → chia thẻ.
        // Bản đầu ghi "→ {leads.Count} khách cần gọi lại ... Bỏ {remindedBefore} đã nhắc rồi", mà
        // leads.Count là con số SAU KHI CẮT còn remindedBefore là TRƯỚC — đọc ra "20 cần gọi, bỏ 31"
        // thì không tài nào lần ra thực ra có 51 khách tới hạn. Hai con số cùng một câu phải cùng
        // một mốc, không thì người đọc tự dựng ra một bức tranh sai mà vẫn thấy hợp lý.
        var summary = $"Quét {customers.Count} khách → {qualified} khách tới hạn"
                    + (remindedBefore > 0
                        ? $" (bỏ {remindedBefore} đã nhắc rồi: {string.Join(" · ", reminderSkips.Select(kv => $"{kv.Value} {kv.Key}"))})"
                        : "")
                    + (freshCount > leads.Count
                        ? $" → lấy {leads.Count} khách chi nhiều nhất (còn {freshCount - leads.Count} để lượt sau)" : "")
                    + $" → {created} thẻ cho {created} nhân viên"
                    + (dedupedCards > 0 ? $", {dedupedCards} đã nhắc hôm nay" : "")
                    + (noOwner > 0 ? $", BỎ QUA {noOwner} khách chưa gán người phụ trách" : "")
                    + (noCareDate > 0 ? $". {noCareDate} khách chưa có ngày chăm sóc nên bỏ qua" : "")
                    + (truncated ? $". CHẠM TRẦN {MaxPages} trang — còn khách chưa quét tới, hạ 'im bao nhiêu ngày' hoặc để lần sau" : "") + ".";
        _log.LogInformation("[customer-auto-care] tenant={T} {Sum}", tenantId, summary);
        return new(true, summary, null);
    }

    // ── helpers JSON ─────────────────────────────────────────────────────────────
    private static bool TryGet(JsonElement el, string name, out JsonElement v)
    {
        v = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        return false;
    }
    private static string? Str(JsonElement el, string n)
        => TryGet(el, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal Dec(JsonElement el, string n)
        => TryGet(el, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
    private static bool TryInt(JsonElement el, string n, out int val)
    {
        val = 0;
        return TryGet(el, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out val);
    }
    private static bool TryDate(JsonElement el, string n, out DateTime val)
    {
        val = default;
        if (!TryGet(el, n, out var v) || v.ValueKind != JsonValueKind.String) return false;
        // AssumeUniversal: TryParse trần trả Kind=Local rồi lệch múi giờ khi trừ ngày.
        return DateTime.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out val);
    }
}
