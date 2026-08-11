# Phương án test — Bug so sánh (L2 cache key nuốt cột so sánh)

**Ngày:** 2026-08-05 · **Phạm vi:** `Services/Chat/JsonPlannerAgent.cs` (fix đưa `compareShift` vào L2 key)

## 1. Bug là gì (để biết test phải chứng minh điều gì)

L2 cache key **cũ** = `tenant|user|tool|params-kỳ-chính` — **không** gồm ý so sánh. Vì lookup L2 chạy
**trước** khối compare, nên:

1. Hỏi *"Doanh thu tháng này"* → tool `cashflow` params `{month:8}` → chạy đủ → lưu L2 key `…|cashflow|month=8`.
2. Hỏi *"Doanh thu tháng này so với tháng trước"* → planner vẫn ra `cashflow` + `{month:8}` → **cùng key** →
   **L2 HIT** → trả nguyên văn câu (1), **mất cột so sánh**.

**Fix:** L2 key mới = key cũ + `"|cmp=" + compareShift` → câu so sánh và câu thường rơi 2 ô cache khác nhau.
Gói trong helper `JsonPlannerAgent.L2CacheKey(...)` (1 nguồn, dùng chung `RunAsync` + `StreamAsync`).

**Cái gì test phải chứng minh:**
- (A) Câu so sánh ≠ câu thường ⇒ **key khác** ⇒ không đè nhau.
- (B) Câu **y hệt** lặp lại ⇒ **key giống** ⇒ cache vẫn tăng tốc (không phá tính năng cache).
- (C) End-to-end: đúng cặp câu gây bug → câu 2 **thật sự** có cột so sánh, **không** phải bản sao câu 1.

---

## 2. Tầng 1 — Unit test (TỰ ĐỘNG, không cần DB) ✅ đã có

Chạy: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
File: `TourkitAiProxy.Tests/CompareIntentTests.cs`

### 2a. Bộ phân loại `DetectCompareIntent`

| # | Input | Kỳ vọng | Chứng minh |
|---|-------|---------|-----------|
| 1 | "Doanh thu tháng này so với tháng trước" | `PrevMonth` | (A) |
| 2 | "so sánh doanh thu tháng trước" | ≠ None | (A) |
| 3 | "so với cùng kỳ năm ngoái" | `PrevYear` | (A) |
| 4 | "quý này so với quý trước" | ≠ None | (A) |
| 5 | "compare revenue last month" | ≠ None | (A) |
| 6 | "Doanh thu tháng này" | `None` | (A) |
| 7 | "Top khách hàng tháng này" | `None` | (A) |
| 8 | "" (rỗng) | `None` | biên |
| 9 | "năm ngoái" vs "tháng trước" | 2 giá trị KHÁC | hướng dịch đúng |

### 2b. Chính cái L2 KEY (`JsonPlannerAgent.L2CacheKey`)

| # | Tình huống | Kỳ vọng | Chứng minh |
|---|-----------|---------|-----------|
| 10 | cùng params, `None` vs `PrevMonth` | key **khác** | (A) |
| 11 | cùng input y hệt (2 lần) | key **giống** | (B) |
| 12 | question→shift→key cho cặp câu bug | 2 key **khác** | (C) ở mức key |
| 13 | cùng câu, khác user | key **khác** | không rò cross-user |

> Giới hạn thành thật của Tầng 1: nó chứng minh **key sinh ra đúng**, **chưa** chứng minh toàn bộ
> `RunAsync` (planner AI → dispatch CRM → analysis AI) ghép cột so sánh đúng. Cái đó cần Tầng 2/3.

---

## 3. Tầng 2 — Integration test (TÙY CHỌN, cần dựng harness)

Mục tiêu: chạy thẳng `JsonPlannerAgent.RunAsync` với **AI giả** + **CRM giả**, phát 2 câu theo thứ tự,
kiểm chứng câu 2 có `Data.Compare != null` và reply khác câu 1.

**Vì sao chưa làm ngay:** `RunAsync` phụ thuộc `TourKitApiClient` (class cụ thể, gọi HTTP thật) và
`TkSessionStore` (đọc memory từ DB). Để test cần:
- Bọc upstream sau interface (vd `ITourKitApiClient`) để cắm bản giả trả JSON envelope cố định.
- `IAiProvider` giả (đã inject sẵn qua `AgentInput.Provider`) trả JSON planner + văn phân tích cố định.
- `TkSessionStore` giả/in-memory cho `GetMemory`/`UpdateMemory`.

**Case sẽ phủ (nếu dựng):**

| # | Kịch bản (theo thứ tự) | Kỳ vọng |
|---|------------------------|---------|
| I1 | "doanh thu tháng này" → "…so với tháng trước" | câu 2 có `Compare`, reply ≠ câu 1, **không** L2 HIT ở câu 2 |
| I2 | Thứ tự ĐẢO: "…so với tháng trước" trước → "doanh thu tháng này" | câu 2 **không** dính `Compare` của câu 1 |
| I3 | "doanh thu tháng này" ×2 (y hệt) | câu 2 = **L2 HIT** (nhanh, không gọi AI lần 2) |
| I4 | "…so với tháng trước" ×2 | câu 2 = L2 HIT, vẫn có `Compare` |
| I5 | Lặp cho path `StreamAsync` | y như I1–I4 |

> Chi phí: ~2–4h dựng seam + harness. **Đề xuất:** làm khi đụng vào Phần 3 (lúc đó sẽ refactor
> memory/cache sẵn) — gộp 1 lần cho đỡ phí. Nếu bạn muốn làm ngay, báo tôi.

---

## 4. Tầng 3 — Manual E2E trên app thật (BẮT BUỘC để chắc 100%)

Đây là bằng chứng cuối. Cần app chạy + nối được DB/CRM staging (`TourKit:BaseUrl`).

**Chuẩn bị:** đăng nhập `/assistant` (hoặc `/travai`). Bật trace bằng `?debug=1` trên URL để đọc bước.

### Kịch bản chính (repro đúng bug)

| Bước | Gõ | Kỳ vọng ĐÚNG (sau fix) | Dấu hiệu SAI (bug cũ) |
|------|-----|------------------------|------------------------|
| 1 | "Doanh thu tháng này" | Ra số + bảng kỳ này | — |
| 2 | "Doanh thu tháng này so với tháng trước" | Reply nhắc **cả 2 kỳ** + panel có **cột/nhãn so sánh** | Lặp **y nguyên** câu 1, **không** có cột so sánh |

**Cách xác nhận bằng trace/log (không đoán mò):**
- Câu 2 trace phải có bước `l2_cache_lookup` = **MISS** (không phải HIT) và có `compare_dispatch = ok`.
- Log: `grep "L2 cache hit"` — câu 2 **không** được xuất hiện; `grep "Compare dispatch"` — phải có.

### Case phụ (chống hồi quy — fix không được làm hỏng cái khác)

| # | Gõ | Kỳ vọng |
|---|-----|---------|
| E1 | Hỏi "doanh thu tháng này" **2 lần y hệt** | Lần 2 nhanh (L2 HIT), cùng nội dung |
| E2 | "doanh thu tháng này" → "Số liệu này lấy từ đâu?" | Câu 2 = câu nêu **nguồn** (short-circuit), **không** lặp số, **không** lộ tên tool |
| E3 | "doanh thu tháng này" → "còn tháng trước thì sao?" | Follow-up chạy đúng (kỳ tháng trước), không lỗi |
| E4 | "…so với cùng kỳ năm ngoái" | Panel so sánh với **năm ngoái** (khác "tháng trước") |
| E5 | Lặp bước 1–2 trên `/travai` (voice, dùng StreamAsync) | Y như kịch bản chính |
| E6 | 2 user khác nhau cùng tenant hỏi cùng câu | Không rò dữ liệu chéo (mỗi user cache riêng) |

### Case biên cần để ý (đã biết, chấp nhận được)
- Câu có chữ "so sánh" nhưng tool **không có tham số ngày** (vd hỏi danh sách khách) → khối compare bỏ qua
  (`HasDateParams=false`), nhưng key vẫn mang `cmp=…` → câu đó tách ô cache riêng. **Vô hại**, chỉ giảm
  tái sử dụng cache chút xíu, không bao giờ trả sai.

---

## 5. Kết luận trạng thái hiện tại

- ✅ Tầng 1 (unit): **307 passed / 0 failed**, đã khóa (A)(B)(C)-ở-mức-key.
- ⏳ Tầng 2 (integration): **chưa dựng** — đề xuất gộp với Phần 3.
- ⏳ Tầng 3 (manual E2E): **chưa chạy** (máy dev không nối được DB staging) — cần chạy trên bản deploy.

**Thành thật:** unit test đã chứng minh key không còn đè nhau; nhưng "câu 2 hiện đúng cột so sánh trên UI"
chỉ được xác nhận 100% sau khi chạy **Tầng 3** trên app thật.
