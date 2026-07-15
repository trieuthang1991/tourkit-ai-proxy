# Thiết kế: Định nghĩa "deal nguội" cấu hình được — 1 nguồn backend

**Ngày:** 2026-07-15
**Trạng thái:** Draft — chờ duyệt
**Liên quan:** [2026-06-28-deal-auto-review-alert-design.md](2026-06-28-deal-auto-review-alert-design.md)

## 1. Vấn đề

Trên `wwwroot/pages/deals.jsx`, badge "nguội" hiện khi `isCooling || riskFlag === 'nguoi'` (dòng 449 KPI, 722 bảng, 147 card). Cả 2 tín hiệu **không xét trạng thái deal**:

| Tín hiệu | Nguồn | Công thức | Xét trạng thái? |
|----------|-------|-----------|-----------------|
| `isCooling` | Upstream TourKit.Api | `CoolingDays ≥ 7` (dựa `LastInteractionAt`) | ❌ |
| `riskFlag='nguoi'` | `DealHeuristic.RiskFlag` | `ageDays ≥ 21` (thuần tuổi) | ❌ |

**Hệ quả (bug thực tế):** deal trạng thái **"Hoàn thành"** vẫn hiện "nguội" (vì lâu không chạm hoặc tạo ≥ 21 ngày).

**Nghịch lý:** `DealAutoReviewWorkflow` (dòng 259) LỌC đúng trước khi enqueue alert:
```csharp
.Where(d => d.Status != CancelStatus && !IsClosedWon(d.StatusName))
.Where(d => d.IsCooling && d.CoolingDays >= opt.CoolingDays)
```
→ **email cảnh báo không gửi** cho deal đã đóng (đúng), nhưng **UI badge + KPI vẫn hiện nguội** (sai). Hai nơi lệch định nghĩa.

**Vấn đề thiết kế:** "nguội" đang OR 2 định nghĩa khác bản chất — tuổi≥21 (thô: deal cũ nhưng vừa tương tác vẫn báo nguội) vs `isCooling` (lâu-không-tương-tác, đúng nghĩa).

## 2. Định nghĩa mới

> Một deal là **NGUỘI** khi và chỉ khi:
> **(a)** trạng thái **đủ điều kiện theo dõi**, VÀ
> **(b)** `CoolingDays ≥ coolingDays` (số ngày không tương tác, từ `LastInteractionAt` upstream).

- **Bỏ hoàn toàn** tín hiệu tuổi (`riskFlag='nguoi'` theo `ageDays≥21`) khỏi khái niệm "nguội". Tuổi deal vẫn hiển thị riêng ("· N ngày" ở dòng deal) — 2 khái niệm tách bạch.
- Threshold dùng **`coolingDays` cấu hình per-tenant** (mặc định 7), áp lên số `CoolingDays` thô — KHÔNG dựa cờ `isCooling` bool cứng của upstream.

## 3. Cấu hình trạng thái (`coolingStatuses`)

- Kiểu **inclusion**: `coolingStatuses: int[]` — danh sách status ID mà tenant muốn cảnh báo nguội.
- Lưu trong **OptionsJson của workflow `deal-auto-review`** (nơi `coolingDays` đã có — ít bề mặt mới).
- Logic eligible:
  - `coolingStatuses` **rỗng** → eligible = mọi trạng thái **TRỪ** chốt-thắng (keyword `IsClosedWon`) và hủy (`CancelStatus = 5`). → zero-config vẫn chạy đúng như hiện tại.
  - `coolingStatuses` **có giá trị** → eligible = `statusId ∈ coolingStatuses` (list thắng hoàn toàn; không áp thêm keyword fallback — tôn trọng lựa chọn tenant).

## 4. Verdict 1 nguồn (single source of truth)

Tách helper thuần **`DealCooling`** (`Services/Deals/DealCooling.cs`):
```csharp
public static class DealCooling
{
    public const int CancelStatus = 5;
    public static bool IsClosedWon(string? statusName);           // dời từ DealAutoReviewWorkflow
    // Verdict "nguội": eligible-status && CoolingDays >= coolingDays
    public static bool IsCooling(int status, string? statusName, int coolingDays,
                                 int coolingDaysThreshold, IReadOnlyCollection<int> coolingStatuses);
}
```

Áp helper ở **mọi nơi phát sinh "nguội"**:
- **`/deals` list + `/deals/board`**: khi map deal item, set field `isCooling` = `DealCooling.IsCooling(...)` (ghi đè cờ upstream thô). FE nhận cờ SẠCH.
- **`DealAutoReviewWorkflow` cooling pass**: thay đoạn `.Where(...)` hardcode bằng `DealCooling.IsCooling(...)` (cùng helper, cùng policy).

→ UI badge + KPI + alert **dùng chung 1 verdict**, hết lệch.

## 5. Frontend (`deals.jsx`)

- Badge nguội (dòng 147, 722) + KPI (dòng 449): đổi thành **chỉ** `item.isCooling` (bỏ `|| riskFlag === 'nguoi'`).
- Không còn phụ thuộc `riskFlag` cho "nguội" (riskFlag có thể vẫn tồn tại cho mục đích khác nếu cần, nhưng không dính "nguội").

## 5b. Bộ lọc "Nguội" trên trang Deals (`deals.jsx`)

Thêm khả năng **lọc chỉ deal nguội** — tận dụng verdict `isCooling` 1 nguồn ở §4.

- **UI:** thêm 1 chip **"Nguội"** vào hàng filter (`DealFilters`, cạnh các chip Win). State `coolingOnly: bool` (hoặc mở rộng `rankFilter` thêm giá trị `'cooling'` — chọn cách ít đụng logic rank nhất → dùng state riêng `coolingOnly`).
- **Lọc SERVER-SIDE** (toàn DB, phân trang đúng — KHÔNG lọc client-side trên 1 trang 50 dòng, gây hiểu nhầm): bật chip → `loadList` set param `cooling=true` → `/api/v1/deals?cooling=true`.
- **Backend `/api/v1/deals`** khi `cooling=true`:
  - **Ưu tiên:** nếu upstream `/api/ai/booking-tickets` có tham số lọc cooling → truyền thẳng (rẻ nhất, phân trang upstream lo).
  - **Fallback (nếu upstream chưa hỗ trợ):** fetch tập deal MỞ giới hạn (`pageSize` ≤ 200, mirror cooling pass của alert) → áp `DealCooling.IsCooling(...)` → lọc → phân trang proxy-side. Cap 200: nếu vượt → log warning + trả 200 đầu (số deal nguội thực tế thường nhỏ hơn).
- **Kết hợp filter khác:** `cooling=true` AND các filter đang bật (status/source/staff/win…) — giao nhau bình thường.
- **Empty state:** khi lọc nguội mà rỗng → dùng lại empty-state "không khớp bộ lọc" + nút Xóa bộ lọc (đã có, dòng 647-655).

## 6. Config UI (`workflows.jsx` — card `deal-auto-review`)

- Thêm **multi-select trạng thái** cho `coolingStatuses`, nguồn options = danh sách trạng thái thật của tenant (`/api/ai/reference` Lookups.TourStatuses — như deals.jsx đã dùng).
- `coolingDays` (input số) đã có — giữ.
- Nhãn rõ: "Cảnh báo nguội cho deal ở trạng thái: [multi-select] — để trống = mọi trạng thái đang mở".

## 7. Đọc config cho badge ở `/deals`

- `/deals` endpoint đọc `OptionsJson` của workflow `deal-auto-review` (per-tenant) để lấy `{coolingStatuses, coolingDays}`.
- Chưa cấu hình workflow → default `{coolingStatuses: [], coolingDays: 7}`.
- (Đọc nhẹ: 1 SELECT `dbo.UserWorkflows` per request list — cache ngắn nếu cần, nhưng list deal vốn không phải hot-path cực đại.)

## 8. Edge cases
- Deal thiếu `CoolingDays` (upstream chưa trả) → `CoolingDays=0` → không bao giờ nguội (an toàn).
- `coolingStatuses` chứa status không tồn tại (tenant đổi pipeline) → chỉ là không khớp deal nào ở status đó — vô hại.
- Upstream chưa trả field `isCooling`/`coolingDays` → verdict = false toàn bộ (không báo nhầm).

## 9. Testing (pure logic — theo convention repo)
`DealCoolingTests` (xUnit) phủ:
1. Trạng thái mở + CoolingDays ≥ ngưỡng → nguội.
2. Trạng thái mở + CoolingDays < ngưỡng → KHÔNG nguội.
3. Trạng thái "Hoàn thành"/"Đã chốt" + CoolingDays cao → KHÔNG nguội (bug gốc).
4. Trạng thái Hủy (5) → KHÔNG nguội.
5. `coolingStatuses` rỗng → dùng keyword fallback (đóng/hủy loại trừ).
6. `coolingStatuses` có giá trị → chỉ status trong list nguội; status ngoài list (kể cả mở) KHÔNG nguội.
7. `coolingStatuses` chứa status "đã chốt" (tenant cố ý) → vẫn tính (list thắng keyword).
8. CoolingDays=0 / thiếu → KHÔNG nguội.
9. `IsClosedWon` bắt đúng "đã chốt/hoàn thành/thành công/đã bán", KHÔNG bắt nhầm "chưa chốt/sắp chốt".

## 10. Ngoài phạm vi (YAGNI)
- KHÔNG đổi cách upstream tính `LastInteractionAt`/`CoolingDays` (giữ nguyên nguồn upstream).
- KHÔNG thêm nhãn "Tồn lâu" cho tuổi (chỉ bỏ tuổi khỏi "nguội"; hiển thị tuổi giữ nguyên).
- KHÔNG viết worker gửi mail (vẫn là việc riêng phía toutkit-app).
