# NCC mẫu + Retriever chọn nguồn giá — Design (DRAFT, chờ duyệt)

> Trạng thái: **DRAFT** — đã chốt 5 quyết định qua brainstorm 2026-07-18, **chờ user duyệt** trước khi viết plan/implement. Nối tiếp mảng 1 (catalog + sync) đã xong.

## Mục tiêu
Tenant data thưa (vd `vnexpresstour` sync ra bỏ 99.6% → catalog gần rỗng) thì vẫn dựng được giá tour nhờ **NCC mẫu** — dataset hệ thống dựng từ dữ liệu TopTour. Khi phân tích giá, user chọn **nguồn**: NCC mẫu / NCC thật / cả 2 (ưu tiên thật).

## Quyết định đã chốt (brainstorm 2026-07-18)
1. **Phạm vi:** dựng NCC mẫu **+** retriever chọn nguồn (một phần mảng 2). *(câu 1 = B)*
2. **Lưu NCC mẫu:** dùng lại `dbo.TourPriceCatalog`, gán `TenantId` dành riêng `__sample__` → tái dùng schema + `TourPriceCatalogRepository` + `PriceCatalogRules`. *(câu 2 = A)*
3. **Dựng dữ liệu:** SEED một lần từ TopTour **local** → file seed (đã lọc theo luật) → loader import idempotent lúc startup vào rows `__sample__`. Cố định, ship được lên staging/prod, không cần TopTour online. *(câu 3 = A)*
4. **Option chọn nguồn:** theo **từng lần** phân tích giá (dropdown trong wizard), không phải setting per-tenant. *(câu 4 = A)*
5. **"Cả 2, ưu tiên thật" trộn theo (điểm đến + loại DV):** có NCC thật cho cặp đó → dùng thật; chỗ thật thiếu → lấp bằng mẫu; mỗi dòng gắn nhãn nguồn (`real`/`sample`). *(câu 5 = A)*

## Kiến trúc

### 1. NCC mẫu (dữ liệu)
- Hằng `SampleTenantId = "__sample__"` (reserved — không tenant thật nào trùng vì tenant thật là domain).
- **Extraction (dev-time, một lần):** chạy JOIN 4 bảng (như endpoint provider-prices) trên **DB TopTour local** → áp `PriceCatalogRules` (bóc sao, loại trừ vé máy bay + giá <50k) → xuất file seed `data/seed/tour-price-sample.json` (mảng `CatalogRow` với TenantId=`__sample__`). **CẦN: tên DB TopTour trong SQL instance local** (user cung cấp lúc implement).
- **Seeder (runtime):** `SampleCatalogSeeder` đọc seed → `UpsertBatchAsync` vào `__sample__` lúc startup, idempotent (chỉ ghi nếu rỗng hoặc seed version đổi). Không phụ thuộc TopTour online.

### 2. Retriever (mảng 2 — core)
- `PriceSource { Sample, Real, Both }` enum.
- `ITourPriceRetriever.RetrieveAsync(tenantId, query, source, ct)` → list `PriceCandidate` (CatalogRow + `Source` tag).
  - `Real`  → `WHERE TenantId=@tenant`.
  - `Sample`→ `WHERE TenantId='__sample__'`.
  - `Both`  → query real trước; với mỗi (CityNorm + CategoryId) mà real **không có dòng nào**, bổ sung dòng mẫu cho cặp đó. Mỗi dòng gắn `Source=real|sample`.
- Query filter core: tỉnh (CityNorm) + loại DV (CategoryId) + khoảng giá (±25% quanh ngân sách) — chi tiết filter/cap kế thừa mảng 2 §8.2 (DestinationMap điểm đến→tỉnh, cap 60, log overflow). *Phần này scope ở plan, có thể làm tối giản trước.*

### 3. Wizard / phân tích giá
- Thêm dropdown **Nguồn giá**: NCC mẫu / NCC thật / Cả 2 (ưu tiên thật) — mặc định "Cả 2".
- Giá trị `source` đi kèm request phân tích → retriever → đưa dòng ứng viên (kèm nhãn nguồn) cho AI dựng giá + badge nguồn ở UI.

## Không làm (YAGNI)
- Setting nguồn per-tenant (đã chọn per-request).
- Sync mẫu định kỳ / live từ TopTour (seed cố định là đủ; rebuild là việc dev-time khi cần).
- Bảng riêng cho mẫu (dùng reserved TenantId).

## Việc cần user cung cấp lúc implement
- **Tên DB TopTour trong SQL instance local** (để chạy extraction seed 1 lần).

## Câu hỏi mở / rủi ro
- Reserved `__sample__` phải được **loại khỏi mọi thống kê/quota/admin cross-tenant** (nó không phải tenant thật) — cần rà `AiUsageHistory`, admin views, quota để không đếm nhầm.
- Seed size: TopTour ~13.775 dòng giá → sau lọc còn ~? (đo lúc extract). File seed JSON có thể vài MB — cân nhắc nén hoặc để SQL seed.
- Mảng 2 retriever đầy đủ (DestinationMap, 2 tầng chọn giá, composer, sửa wizard.jsx) là khối lớn — plan sẽ tách bước, làm core source-selection trước.
