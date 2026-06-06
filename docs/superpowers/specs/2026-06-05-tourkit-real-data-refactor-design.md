# Tái cấu trúc TourKit real-data + auth toàn cục — Design Spec

> Trạng thái: **đã brainstorm xong, chờ chủ dự án review spec → writing-plans.** Chưa code.
> Ngày: 2026-06-05. BaseUrl TourKit: `https://mobile-test-api-2.tourkit.vn` (staging, có `/api/ai/*`).

## Goal

Biến tourkit-ai-proxy thành sản phẩm **gated bằng login TourKit**, mọi dữ liệu nghiệp vụ (khách hàng, nhà cung cấp) **lấy thật từ TourKit CRM** qua session JWT; nháp tour AI lưu **Redis** theo công ty; gộp "Tour đã lưu" vào Wizard; đồng bộ spacing toàn app. Chỉ phần AI sinh nội dung là "ngoài".

## Quyết định kiến trúc đã chốt (brainstorm)

| # | Quyết định |
|---|---|
| Auth | **Gate TOÀN BỘ app**. Chưa login TourKit → màn login, không vào feature nào. |
| NCC | **Dùng API NCC có sẵn của TourKit** (`/api/tours/*`), KHÔNG tạo kho/CRUD mới. NCC có giá hợp đồng thật. |
| NCC mechanism | **Hybrid**: AI sinh có NCC trong context → code đối chiếu → đánh dấu ✓NCC vs ngoài → cho swap. |
| Khách hàng | **Lấy KH thật** từ TourKit (`/api/ai/customers` + `/api/customers/{id}/orders|debts`), bỏ `customers.seed.json`. |
| Nháp tour | **Redis** (theo tenant), bỏ localStorage TourCache. Redis nếu cấu hình, không thì file-backed (KHÔNG in-memory). |
| Gộp trang | Bỏ `/quotes`; Wizard có 2 tab: **Tạo tour | Tour đã lưu**. (Không có tab NCC — NCC ở TourKit.) |
| Spacing | Thống nhất thang `--space-*` + frame chung cho 4 trang; polish bằng design taste. |
| Phạm vi ghi | **TẤT CẢ read-only** với TourKit (không tạo/sửa CRM). Chỉ ghi nội bộ proxy (nháp tour, review) lên Redis/file. |

## Phases
- **A — Nền:** Global auth (FE gate + helper fetch) + Redis provider dùng chung + store bền vững.
- **B — Data thật:** Customer Review → KH thật; NCC client + wizard ưu tiên hybrid.
- **C — Wizard rework:** nháp tour Redis + gộp tab "Tour đã lưu".
- **D — Spacing + polish** (design taste).

---

## #2 — Auth toàn cục

**Backend:** không thêm endpoint (đã có `/api/v1/login`, `/login-token`, `/session`, `TkSessionStore`). Các proxy mới đọc `X-Session-Id` header → `TkSessionStore.GetValidJwtAsync` (auto re-login on 401) + lấy `TenantId`.

**Frontend:**
- `core/auth.jsx` → `window.tourkitAuth`:
  - `getSession()`, `login({username,password,domain})`, `loginToken(token)`, `logout()`, `onChange(cb)`.
  - `authedFetch(url, opts)` — tự gắn `X-Session-Id`, bắt 401 → `logout()` + phát event.
  - `<LoginGate>` — form + token paste (tách từ `assistant.jsx`).
- `app.jsx`: khi mount → validate `getSession()`. **Chưa hợp lệ → render `<LoginGate>` full-screen** thay cho `<Router>`. Topbar: user chip + Đăng xuất.
- `assistant.jsx`: **bỏ UI login riêng**, giả định đã có session (đọc qua `tourkitAuth`).
- Mọi feature gọi `tourkitAuth.authedFetch`.

**Lưu ý:** SmartMail + Wizard sau gate cũng cần login TourKit để vào trang (creds Gmail của SmartMail vẫn riêng, không liên quan login TourKit).

---

## #6 — Customer Review trên KH thật

**Backend:**
- `Services/TourKit/TourKitCustomerClient.cs` — qua `TourKitApiClient` + JWT:
  - `ListAsync(filter, pageIndex, pageSize)` → `GET /api/ai/customers` → `AiCustomerItem[]` (Id, FullName, Phone, TotalTours, TotalRevenue, CustomerGroup/Type/Source, Birthday, LastCareDate…).
  - `DetailAsync(id)` → `GET /api/customers/{id}`.
  - `OrdersAsync(id)` → `GET /api/customers/{id}/orders` → `CustomerOrderItem[]` (TourTitle, DepartureDate, TotalThu, ActualThu, Status).
  - `DebtsAsync(id)` → `GET /api/customers/{id}/debts` → `CustomerDebtItem[]` (Debt).
- `Services/Reviews/CustomerSource.cs` — thay `CustomerRepository` (seed). Map TourKit → model `Customer`/`Metrics` hiện có:
  - `TotalTours` ← AiCustomerItem.TotalTours; `TotalSpent` ← TotalRevenue.
  - `LastPurchaseDaysAgo` ← từ `max(orders.DepartureDate)`.
  - `Aov` ← TotalRevenue / max(TotalTours,1); lịch sử tuyến ← orders.TourTitle (cho `preferences`).
  - `Debt`/cảnh báo ← sum(debts.Debt).
- `ReviewService.ReviewAsync(Customer)` giữ nguyên (chấm hạng A–D). Fingerprint = SHA256 của Customer thật (đổi data → review stale).
- **Review storage key đổi sang `{tenant}:{customerId}`** (tránh trùng id giữa tenant). Lưu qua store bền vững (xem #5 store) thay `reviews.json` phẳng.

**Endpoints (giữ path cũ, nay cần session):**
- `GET /api/v1/customers` → list KH thật (paging/filter) + review status.
- `GET /api/v1/customers/{id}` → `{customer (đã build từ orders/debts), review}`.
- `/api/v1/reviews/*` (sync/batch/feedback) giữ nguyên, nguồn Customer = `CustomerSource`.

**Khiếu nại:** không có field cấu trúc → cảnh báo dựa **công nợ + độ trễ mua + khoảng cách CSKH** (đủ cho MVP).

---

## #4 — Wizard ưu tiên NCC thật (hybrid)

**Backend — `Services/TourKit/TourKitNccClient.cs` (read-only, qua JWT):**
- `CategoriesAsync()` → `GET /api/tours/service-categories` (loại DV: Khách sạn, Vận chuyển…).
- `ProvidersByServiceAsync(serviceId)` → `GET /api/tours/providers-by-service?serviceId=` → `ProviderByServiceItem` (Id, Name, City, MarketName, Phone).
- `ProviderServicesAsync(providerId, categoryId?)` → `GET /api/tours/providers/{id}/services` → `ProviderServiceItem` (Id, Name, **ContractPrice**, PublicPrice).
- `ProvidersAsync(marketId?)` → `GET /api/tours/providers?marketId=` (full / theo thị trường).

**Proxy endpoints:** `GET /api/v1/ncc/categories`, `/api/v1/ncc/providers?serviceId=`, `/api/v1/ncc/providers/{id}/services?categoryId=`, `/api/v1/ncc/providers?marketId=`.

**Bảng map activity-type → service-category** (code, build từ Categories): `HOTEL→Khách sạn`, `MEAL→Ăn uống/Nhà hàng`, `TRANSPORT→Vận chuyển`, `GUIDE→HDV`, `TICKET→Vé/Voucher`, `SIGHTSEEING→Tham quan`… (khớp tên category trả về, normalize bỏ dấu).

**Luồng wizard (hybrid):**
1. Route → `marketId` (resolver tên→id, mẫu `ChatAgentService.ResolveMarketAsync`).
2. Nạp **NCC catalog** cho tuyến: với mỗi category liên quan → providers + services (kèm ContractPrice).
3. Prompt AI sinh tour kèm danh sách NCC rút gọn: *"ưu tiên dùng các NCC sau (tên + giá hợp đồng) cho slot phù hợp; chỉ tạo nguồn ngoài khi không khớp."*
4. AI trả lịch trình → **code đối chiếu** từng activity với NCC catalog (match theo category + fuzzy tên) → gắn `ncc: {providerId, serviceId, contractPrice, verified:true}` hoặc `external:true`.
5. **Costing** dùng `ContractPrice` cho slot NCC (giá thật), ước lượng AI cho slot ngoài. Hiện chỉ % NCC coverage.
6. **UI swap:** mỗi activity hiện `✓ NCC` (xanh, kèm NCC + giá) hoặc badge `ngoài` + dropdown swap → `providers-by-service` của category đó → chọn → thay bằng NCC thật + giá.

---

## #5 — Nháp tour lên Redis (theo tenant)

**Redis provider dùng chung:** tách multiplexer khỏi `ChatCache` → `Services/Cache/RedisProvider.cs` (sở hữu `ConnectionMultiplexer?`, decrypt `Redis:ConnectionString` bằng Crypton, `AbortOnConnectFail=false`). `ChatCache` + store mới cùng dùng.

**Store bền vững:** `Services/Store/TenantTourStore.cs`:
- Redis nếu có → hash `tkai:tours:{tenant}` (field=tourId, value=json). KHÔNG TTL.
- Không có Redis → file-backed `data/saved-tours.json` (lock-guarded, mẫu `ReviewRepository`). **KHÔNG dùng in-memory** (mất khi restart = sai nghĩa "đã lưu").
- Review store cũng dùng pattern này, key `{tenant}:{customerId}`.

**Model `SavedTour`:** `{ id, request(route/days/nights/adults/children/budgetPerPax/preferences), itinerary, marketing, costing, nccCoveragePct, createdAt, createdBy }`.

**Endpoints:** `GET /api/v1/tours` (list tenant), `GET /api/v1/tours/{id}`, `POST /api/v1/tours` (lưu), `DELETE /api/v1/tours/{id}`.

**Frontend:** wizard generate xong → `POST /api/v1/tours` (thay `TourCache` write). Tab "Tour đã lưu" → `GET /api/v1/tours`. Bỏ `TourCache` trong `core/storage.js` (giữ `RequestHistory` recents client-side nếu muốn — nhẹ, không phải "đã lưu").

---

## #3 — Gộp "Tour đã lưu" vào Wizard

- Bỏ route `/quotes`, nav item, file `pages/quotes.jsx`.
- `pages/wizard.jsx` → có tab: **Tạo tour** (4 bước hiện tại) | **Tour đã lưu** (list từ Redis, mở lại / xoá).
- `app.jsx`: bỏ `<Route path="/quotes">` + `<Link>`.

---

## #1 — Đồng bộ spacing

- `styles.css`: định nghĩa thang `--space-1..8` (4/8/12/16/20/24/32/40) + chuẩn hóa `.page` padding, `.page-title-block`, gap card dùng chung → áp 4 trang.
- KHÔNG ép warm-console khắp nơi; chỉ thống nhất nhịp. Surface mới/đổi (LoginGate, wizard tabs, NCC swap, customer list) polish bằng **frontend-design** lúc implement.

---

## Error handling
- Proxy TourKit: `TourKitApiException` 401 → re-login 1 lần (đã có); lỗi khác → trả `{error, status}` cho FE toast.
- `authedFetch` 401 → logout + về LoginGate.
- Store: Redis down → fallback file (không chặn). Generate AI lỗi → giữ luồng fallback provider hiện có.

## Testing (xUnit — project `TourkitAiProxy.Tests` đã có)
- **Pure, có test:** map `AiCustomerItem`+orders → `Customer/Metrics`; logic match activity↔NCC (fuzzy + category map); `TenantTourStore` file-backed roundtrip + tenant keying; review key `{tenant}:{id}`.
- **Manual (staging):** mọi call TourKit thật, auth gate, wizard NCC swap, frontend.

## Config
- Đổi `TourKit:BaseUrl` → `https://mobile-test-api-2.tourkit.vn` (Program.cs default + appsettings.example).

## Out of scope (hoãn)
- Đẩy nháp AI → tour thật trong TourKit (`POST /api/tours`).
- Field khiếu nại có cấu trúc.
- NCC CRUD trong app (N/A — NCC ở TourKit).
- SmartMail không đổi (đã dùng Gmail thật).
