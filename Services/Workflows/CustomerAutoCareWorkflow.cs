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

    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly ILogger<CustomerAutoCareWorkflow> _log;

    public CustomerAutoCareWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, ILogger<CustomerAutoCareWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _log = log; }

    public string Type => "customer-auto-care";
    public string Label => "Nhắc chăm lại khách ngủ quên";
    public string Description => "Tìm khách đã từng mua mà lâu không ai chăm → gom thành một danh sách trong Bảng tin để nhân viên gọi. KHÔNG gửi gì cho khách. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;
    /// Có ngưỡng "im bao lâu" + hạng nào đáng chăm → công ty phải khai cho hợp ngành mình.
    public bool HasCompanyRules => true;

    // ⚠️ default PHẢI khớp WORKFLOW_OPTIONS['customer-auto-care'] bên workflow-options.jsx.
    private record Options(int QuietDays, List<string> Ranks, bool RequireBought, int MaxLeads);

    private static Options ParseOptions(string? json)
    {
        var def = new Options(QuietDays: 90, Ranks: new List<string>(), RequireBought: true, MaxLeads: 20);
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

        JsonElement data;
        try
        {
            data = await _api.GetAsync(jwt, $"/api/ai/customers?pageSize={FetchPageSize}", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[customer-auto-care] tenant={T} đọc danh sách khách lỗi", tenantId);
            return new(false, null, $"Không đọc được danh sách khách: {ex.Message}");
        }

        var customers = new List<CareCustomer>();
        int noCareDate = 0;
        // Xem PaymentWatchdogRule.ResolveOwner: thiếu HẲN trường = API cũ (giữ hành vi cũ),
        // có trường mà rỗng = khách chưa gán ai (bỏ qua). Gộp hai cái là im lặng toàn bộ.
        bool apiHasStaff = false;
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
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
        var leads = AutoCareRule.Find(customers, todayVn, opt.Ranks, opt.QuietDays, opt.RequireBought, opt.MaxLeads);

        if (leads.Count == 0)
        {
            var why = noCareDate == customers.Count && customers.Count > 0
                // Nói thẳng nguyên nhân gốc: "0 khách" ở đây không có nghĩa là chăm sóc tốt.
                ? " — không khách nào có ngày chăm sóc gần nhất trong CRM, nên chưa chấm được"
                : "";
            return new(true, $"Quét {customers.Count} khách → không ai tới hạn chăm lại{why}.", null);
        }

        var day = todayVn.ToString("yyyy-MM-dd");

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
        }

        await _insights.PruneAsync(KeepInsightDays, ct);

        var summary = $"Quét {customers.Count} khách → {leads.Count} khách cần gọi lại "
                    + $"({created} thẻ mới cho {created} nhân viên"
                    + (dedupedCards > 0 ? $", {dedupedCards} đã nhắc hôm nay" : "")
                    + (noOwner > 0 ? $", BỎ QUA {noOwner} khách chưa gán người phụ trách" : "") + ")"
                    + (noCareDate > 0 ? $". {noCareDate} khách chưa có ngày chăm sóc nên bỏ qua" : "") + ".";
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
