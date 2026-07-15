# Định nghĩa "deal nguội" cấu hình được — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Nguội" = trạng-thái-đủ-điều-kiện + `CoolingDays ≥ ngưỡng` (1 nguồn backend), cấu hình `coolingStatuses` per-tenant, + chip lọc "Nguội" server-side trên trang Deals.

**Architecture:** Tách helper thuần `DealCooling` (verdict + IsClosedWon). `/deals` endpoint đọc option `deal-auto-review` (coolingStatuses+coolingDays) → override `isCooling` per-item + hỗ trợ `cooling=true` lọc. `DealAutoReviewWorkflow` cooling pass dùng cùng helper. FE badge/KPI đọc `isCooling`; thêm chip "Nguội".

**Tech Stack:** ASP.NET Core 8 minimal API, xUnit (TourkitAiProxy.Tests), React no-build (Babel), Dapper (WorkflowRepository).

**Spec:** [docs/superpowers/specs/2026-07-15-deal-cooling-definition-design.md](../specs/2026-07-15-deal-cooling-definition-design.md)

---

## File Structure

- **Create** `Services/Deals/DealCooling.cs` — helper thuần: `CancelStatus`, `IsClosedWon`, `IsCooling` verdict.
- **Create** `TourkitAiProxy.Tests/DealCoolingTests.cs` — 9 test case pure-logic.
- **Modify** `Services/Workflows/DealAutoReviewWorkflow.cs` — thêm `CoolingStatuses` vào options; cooling pass dùng `DealCooling.IsCooling`; bỏ `IsClosedWon` local (dùng `DealCooling.IsClosedWon`).
- **Modify** `Endpoints/DealEndpoints.cs` — `/deals`: đọc cooling config, override `isCooling` verdict per-item, thêm param `cooling`.
- **Modify** `wwwroot/pages/deals.jsx` — badge/KPI dùng `isCooling`; chip "Nguội" → `cooling=true`.
- **Modify** `wwwroot/pages/workflows.jsx` — card `deal-auto-review`: multi-select `coolingStatuses`.

---

### Task 1: Helper `DealCooling` + tests (TDD)

**Files:**
- Create: `Services/Deals/DealCooling.cs`
- Test: `TourkitAiProxy.Tests/DealCoolingTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `TourkitAiProxy.Tests/DealCoolingTests.cs`:
```csharp
using System.Collections.Generic;
using TourkitAiProxy.Services.Deals;
using Xunit;

public class DealCoolingTests
{
    static readonly int[] Empty = System.Array.Empty<int>();

    // CASE 1: trạng thái mở + CoolingDays ≥ ngưỡng → nguội
    [Fact] public void Open_and_stale_is_cooling()
        => Assert.True(DealCooling.IsCooling(status: 3, statusName: "Đang xử lý", coolingDays: 10, threshold: 7, coolingStatuses: Empty));

    // CASE 2: mở nhưng CoolingDays < ngưỡng → KHÔNG
    [Fact] public void Open_but_fresh_not_cooling()
        => Assert.False(DealCooling.IsCooling(3, "Đang xử lý", coolingDays: 3, threshold: 7, coolingStatuses: Empty));

    // CASE 3 (bug gốc): "Hoàn thành"/"Đã chốt" + stale → KHÔNG nguội
    [Theory]
    [InlineData("Hoàn thành")] [InlineData("Đã chốt")] [InlineData("Đã chốt đơn")]
    [InlineData("Thành công")] [InlineData("Đã bán")]
    public void Closed_won_never_cooling(string statusName)
        => Assert.False(DealCooling.IsCooling(3, statusName, coolingDays: 40, threshold: 7, coolingStatuses: Empty));

    // CASE 4: Hủy (status=5) → KHÔNG
    [Fact] public void Cancelled_not_cooling()
        => Assert.False(DealCooling.IsCooling(status: 5, statusName: "Hủy", coolingDays: 40, threshold: 7, coolingStatuses: Empty));

    // CASE 5: coolingStatuses rỗng → keyword fallback (đóng/hủy loại trừ, còn lại tính)
    [Fact] public void Empty_config_uses_keyword_fallback()
    {
        Assert.True(DealCooling.IsCooling(2, "Chờ xử lý", 10, 7, Empty));       // mở → nguội
        Assert.False(DealCooling.IsCooling(2, "Đã chốt", 10, 7, Empty));        // chốt → không
    }

    // CASE 6: coolingStatuses có giá trị → CHỈ status trong list tính; ngoài list (kể cả mở) KHÔNG
    [Fact] public void Explicit_list_only_those_statuses()
    {
        var list = new[] { 2, 3 };
        Assert.True(DealCooling.IsCooling(2, "Chờ xử lý", 10, 7, list));        // 2 ∈ list → nguội
        Assert.False(DealCooling.IsCooling(4, "Đang giao dịch", 10, 7, list)); // 4 ∉ list → không (dù mở)
    }

    // CASE 7: list chứa status "đã chốt" (tenant cố ý) → vẫn tính (list thắng keyword)
    [Fact] public void Explicit_list_wins_over_keyword()
        => Assert.True(DealCooling.IsCooling(6, "Đã chốt", 10, 7, new[] { 6 }));

    // CASE 8: CoolingDays = 0 (thiếu) → KHÔNG
    [Fact] public void Zero_cooling_days_not_cooling()
        => Assert.False(DealCooling.IsCooling(3, "Đang xử lý", coolingDays: 0, threshold: 7, coolingStatuses: Empty));

    // CASE 9: IsClosedWon bắt đúng, KHÔNG bắt nhầm "chưa chốt"/"sắp chốt"
    [Theory]
    [InlineData("Đã chốt", true)] [InlineData("Hoàn thành", true)] [InlineData("Thành công", true)]
    [InlineData("Chưa chốt", false)] [InlineData("Sắp chốt", false)] [InlineData("Đang xử lý", false)]
    public void IsClosedWon_matches_correctly(string statusName, bool expected)
        => Assert.Equal(expected, DealCooling.IsClosedWon(statusName));
}
```

- [ ] **Step 2: Run — verify FAIL**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter DealCoolingTests`
Expected: FAIL — `DealCooling` không tồn tại (compile error).

- [ ] **Step 3: Implement helper**

Create `Services/Deals/DealCooling.cs`:
```csharp
namespace TourkitAiProxy.Services.Deals;

/// <summary>
/// Định nghĩa "deal nguội" — 1 NGUỒN cho UI badge/KPI + alert workflow.
/// Nguội ⟺ trạng thái ĐỦ ĐIỀU KIỆN theo dõi VÀ CoolingDays ≥ ngưỡng (lâu không tương tác).
/// KHÔNG dùng tuổi deal (age) — đó là khái niệm khác.
/// </summary>
public static class DealCooling
{
    public const int CancelStatus = 5;   // TourKit BookingTicketStatus: 5 = Hủy

    /// Deal đã CHỐT/thành công (không còn cơ hội mở). Chỉ dùng cho case coolingStatuses RỖNG.
    public static bool IsClosedWon(string? statusName)
    {
        var sn = DealHeuristic.Normalize(statusName);
        return sn.Length > 0 && (sn.Contains("chot don") || sn.Contains("da chot") || sn.Contains("thanh cong")
            || sn.Contains("hoan thanh") || sn.Contains("hoan tat") || sn.Contains("da ban"));
    }

    /// Verdict "nguội" theo policy per-tenant.
    /// coolingStatuses RỖNG → eligible = KHÔNG chốt-thắng (keyword) VÀ không Hủy.
    /// coolingStatuses CÓ giá trị → eligible = status ∈ coolingStatuses (list thắng hoàn toàn).
    public static bool IsCooling(int status, string? statusName, int coolingDays, int threshold,
                                 IReadOnlyCollection<int> coolingStatuses)
    {
        if (coolingDays < threshold || threshold <= 0) return false;
        bool eligible = coolingStatuses is { Count: > 0 }
            ? coolingStatuses.Contains(status)
            : (status != CancelStatus && !IsClosedWon(statusName));
        return eligible;
    }
}
```

- [ ] **Step 4: Run — verify PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter DealCoolingTests`
Expected: PASS (all 9 cases + Theory rows).

- [ ] **Step 5: Commit**

```bash
git add Services/Deals/DealCooling.cs TourkitAiProxy.Tests/DealCoolingTests.cs
git commit -m "feat(deal): DealCooling helper — verdict nguội 1 nguồn + 9 test"
```

---

### Task 2: Thêm `CoolingStatuses` vào options + workflow dùng helper

**Files:**
- Modify: `Services/Workflows/DealAutoReviewWorkflow.cs`

- [ ] **Step 1: Thêm field `CoolingStatuses` vào record `DealAutoReviewOptions`**

Sửa record (dòng ~417): thêm `List<int> CoolingStatuses` vào cuối param list:
```csharp
public sealed record DealAutoReviewOptions(
    List<int> Statuses, int CreatedWithinDays, bool AutoReview, bool ReReview, int ReviewMax, int MaxAutoReviews,
    int CoolingDays, int MinWinRateToNotify, int MaxNotifications, int NotifyMinGapHours,
    List<int> CoolingStatuses)
```

- [ ] **Step 2: Parse `coolingStatuses` trong `Parse(...)`**

Trong `def` (dòng ~423) thêm `CoolingStatuses: new List<int>()`. Trong khối parse thành công, trước `return new DealAutoReviewOptions(...)`, thêm:
```csharp
var coolingStatuses = new List<int>();
if (r.TryGetProperty("coolingStatuses", out var carr) && carr.ValueKind == JsonValueKind.Array)
    foreach (var e in carr.EnumerateArray())
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) coolingStatuses.Add(n);
```
Và thêm `CoolingStatuses: coolingStatuses` vào cuối `new DealAutoReviewOptions(...)` (cả nhánh return def lẫn return parsed — def dùng `new List<int>()`).

- [ ] **Step 3: Cooling pass dùng `DealCooling.IsCooling`**

Tại cooling pass (dòng ~258-261), thay:
```csharp
var cooling = openDeals
    .Where(d => d.Status != CancelStatus && !IsClosedWon(d.StatusName))
    .Where(d => d.IsCooling && d.CoolingDays >= opt.CoolingDays)
    .ToList();
```
bằng:
```csharp
var cooling = openDeals
    .Where(d => DealCooling.IsCooling(d.Status, d.StatusName, d.CoolingDays, opt.CoolingDays, opt.CoolingStatuses))
    .ToList();
```

- [ ] **Step 4: Xóa `IsClosedWon` local + const `CancelStatus` (dùng `DealCooling`)**

Xóa method `private static bool IsClosedWon(...)` (dòng ~407-413). Các chỗ còn dùng `IsClosedWon(...)` (dòng 144, 226) → đổi thành `DealCooling.IsClosedWon(...)`. Const `CancelStatus` (dòng 27) còn dùng ở dòng 144/226 → đổi thành `DealCooling.CancelStatus` rồi xóa const local (hoặc giữ const local trỏ `DealCooling.CancelStatus` nếu nhiều chỗ). Thêm `using` nếu cần (cùng namespace `TourkitAiProxy.Services.Deals`? Workflow ở `Services.Workflows` → thêm `using TourkitAiProxy.Services.Deals;` nếu chưa có).

- [ ] **Step 5: Build + chạy test options hiện có**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → Expected: Build succeeded.
Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter DealAutoReviewOptions` → Expected: PASS (test cũ không vỡ).

- [ ] **Step 6: Commit**

```bash
git add Services/Workflows/DealAutoReviewWorkflow.cs
git commit -m "feat(deal): coolingStatuses option + cooling pass dùng DealCooling"
```

---

### Task 3: `/deals` endpoint — verdict 1 nguồn + filter `cooling`

**Files:**
- Modify: `Endpoints/DealEndpoints.cs`

- [ ] **Step 1: Đọc cooling config cho tenant (options deal-auto-review)**

Trong handler `/deals` (dòng 31), thêm param `bool? cooling` vào signature + inject `WorkflowRepository workflows`. Sau khi có `sess`, đọc option:
```csharp
// Cooling policy per-tenant từ deal-auto-review (PerTenant → Username=""). Chưa cấu hình → default.
var cfg = await workflows.GetConfigAsync(sess.TenantId, "", "deal-auto-review", ctx.RequestAborted);
var coolOpt = DealAutoReviewOptions.Parse(cfg?.OptionsJson);
```
> LƯU Ý: xác nhận tên method đọc config trong `WorkflowRepository` (dòng ~122 `SELECT ... OptionsJson`). Nếu tên khác `GetConfigAsync`, dùng đúng tên đó. Nếu là PerTenant, key Username = "".

- [ ] **Step 2: Override `isCooling` verdict khi map item**

Trong khối `res.Items.Select(it => ...)`, thay `it.IsCooling` trong object trả về (dòng 145) bằng verdict tính lại:
```csharp
var coolingVerdict = DealCooling.IsCooling(it.Status, it.StatusName, it.CoolingDays,
                                           coolOpt.CoolingDays, coolOpt.CoolingStatuses);
```
(khai báo trước `return new {...}`), rồi trong object đổi `it.CoolingDays, it.IsCooling,` → `it.CoolingDays, IsCooling = coolingVerdict,`.

- [ ] **Step 3: Lọc `cooling=true` (server-side)**

Sau khi build `items` (dòng ~150), trước khi trả:
```csharp
if (cooling == true)
    items = items.Where(x => (bool)x.GetType().GetProperty("IsCooling")!.GetValue(x)!).ToList();
```
> Anonymous type + reflection xấu. THAY vào đó: tính `coolingVerdict` ra 1 biến ngoài object, gom vào list tuple `(item, isCooling)` để lọc sạch. Chi tiết: đổi `.Select` trả `new { obj = new {...}, isCooling = coolingVerdict }`, lọc theo `.isCooling`, rồi `.Select(x => x.obj)`. (Người thực thi chọn cách gọn: đơn giản nhất là thêm field `_cooling` vào chính anonymous object rồi lọc theo nó — nhưng field đó lộ ra JSON; chấp nhận hoặc đặt tên trùng `IsCooling` và lọc `x.IsCooling`.)
>
> **Cách sạch khuyến nghị:** giữ `IsCooling = coolingVerdict` trong object; lọc bằng cách tách verdict ra `Dictionary<int,bool>` trước Select, rồi `if (cooling==true) items = items.Where(x => verdictById[x.Id]).ToList();`.

- [ ] **Step 4: Build + smoke test thủ công**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → Build succeeded.
Test thủ công (app chạy): `GET /api/v1/deals?cooling=true` (header X-Session-Id) → trả chỉ deal nguội; deal "Hoàn thành" KHÔNG có trong kết quả + `isCooling=false` ở list thường.

- [ ] **Step 5: Commit**

```bash
git add Endpoints/DealEndpoints.cs
git commit -m "feat(deal): /deals verdict nguội 1 nguồn + filter cooling=true"
```

---

### Task 4: Frontend `deals.jsx` — badge/KPI + chip Nguội

**Files:**
- Modify: `wwwroot/pages/deals.jsx`

- [ ] **Step 1: Badge dùng `isCooling` (bỏ riskFlag)**

Dòng 147 (card): `{item.riskFlag === 'nguoi' && ...}` → `{item.isCooling && ...}`.
Dòng 722 (bảng): `{(it.isCooling || it.riskFlag === 'nguoi') && ...}` → `{it.isCooling && ...}`.
Dòng 449 (KPI): `nguoi: boardItems.filter(it => (it.isCooling || it.riskFlag === 'nguoi')).length` → dùng `list`/`items` (có isCooling từ /deals): đổi nguồn KPI nguội sang đếm trên toàn `total` server nếu cần; tối thiểu: `nguoi: items.filter(it => it.isCooling).length` (trên trang). *(Ghi chú: KPI nguội chính xác toàn DB cần server trả count; MVP đếm trang — nhãn giữ "Nguội".)*

- [ ] **Step 2: State + chip "Nguội"**

Thêm state: `const [coolingOnly, setCoolingOnly] = _dS(false);`
Trong `DealFilters` props + render, thêm chip sau các chip Win:
```jsx
<SC.FilterChip on={coolingOnly} onClick={() => setCoolingOnly(v => !v)}>🔥 Nguội</SC.FilterChip>
```
(truyền `coolingOnly`, `setCoolingOnly` xuống `DealFilters`).

- [ ] **Step 3: `loadList` gửi `cooling=true`**

Trong `loadList`, sau các param filter, thêm:
```js
const co = overrides.coolingOnly ?? coolingOnly;
if (co) params.set('cooling', 'true');
```
Thêm effect reload khi `coolingOnly` đổi (mirror `rankFilter` effect, dòng 321-326): skip mount-fire, về page 1 hoặc `loadList({ page:1, coolingOnly })`.
Thêm `coolingOnly` vào deps reset (dòng 317) + `advCount`/`hasActiveFilter` (dòng 642) để nút "Xóa bộ lọc" reset cả nó.

- [ ] **Step 4: Verify live (reload dev Babel)**

Mở `/deals`, bật chip "Nguội" → chỉ deal nguội; deal "Hoàn thành" hết badge nguội. 0 console error.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/pages/deals.jsx
git commit -m "feat(deal): badge nguội theo isCooling + chip lọc Nguội server-side"
```

---

### Task 5: Frontend `workflows.jsx` — config `coolingStatuses`

**Files:**
- Modify: `wwwroot/pages/workflows.jsx`

- [ ] **Step 1: Nạp danh sách trạng thái tenant**

Trong card `deal-auto-review`, fetch statuses từ `/api/v1/deals` (lookups.statuses) HOẶC `/api/ai/reference` (như deals.jsx dùng `lookups.statuses`). Tái dùng pattern có sẵn (kiểm tra workflows.jsx đã có cách nạp reference chưa; nếu chưa, gọi `/api/v1/deals?pageSize=1` lấy `data.lookups.statuses`).

- [ ] **Step 2: Multi-select `coolingStatuses` trong form options**

Thêm control multi-select (checkbox list hoặc chips) cho `coolingStatuses` (int[]), lưu vào OptionsJson của workflow. Nhãn: "Cảnh báo nguội cho deal ở trạng thái (để trống = mọi trạng thái đang mở)". Bind vào cùng cơ chế lưu options đang có (PUT `/api/v1/workflows/deal-auto-review`).

- [ ] **Step 3: Verify live**

Mở `/workflows`, card deal-auto-review → chọn vài trạng thái → Lưu → reload thấy giữ. `GET /api/v1/workflows` trả options có `coolingStatuses`.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/workflows.jsx
git commit -m "feat(deal): config coolingStatuses trong card deal-auto-review"
```

---

## Self-Review

- **Spec coverage:** §2 định nghĩa → Task 1. §3 coolingStatuses → Task 2+5. §4 verdict 1 nguồn → Task 1+2+3. §5 FE badge → Task 4. §5b chip lọc → Task 3+4. §6 config UI → Task 5. §7 đọc config → Task 3. §9 test → Task 1. ✅ Đủ.
- **Placeholder:** Task 3 Step 1 (tên method WorkflowRepository) + Task 5 Step 1 (cách nạp statuses) cần xác nhận đúng tên/pattern lúc thực thi — đã ghi chú rõ, không phải placeholder logic.
- **Type nhất quán:** `DealCooling.IsCooling(int, string?, int coolingDays, int threshold, IReadOnlyCollection<int>)` dùng nhất quán ở Task 1/2/3. `DealAutoReviewOptions.CoolingStatuses: List<int>` dùng ở Task 2/3.

## Execution Handoff — chọn cách chạy
