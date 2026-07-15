# Thiết kế: Dựng giá tour từ bảng giá NCC thật (Tour Price Catalog)

**Ngày:** 2026-07-15
**Trạng thái:** Đã chốt thiết kế (user duyệt 2026-07-15) — sẵn sàng viết plan triển khai
**Phạm vi:** Thay việc **AI tự bịa giá** trong Wizard báo giá bằng việc **chọn dòng giá có thật** từ bảng giá nhà cung cấp của chính công ty đó.

---

## 1. Vấn đề

Wizard hiện tại ([wwwroot/pages/wizard.jsx:182](../../../wwwroot/pages/wizard.jsx)) bảo AI tự nghĩ ra số tiền:

```
- giá là số nguyên VND cho cả đoàn ${totalPax}, không dấu phẩy/dot
```

Prompt chỉ nhét vào **tên** của tối đa 40 NCC (`nccBlock`, dòng 170) — **không kèm giá**. Trong khi
`TourKitNccClient.ProviderServicesAsync` ([Services/TourKit/TourKitNccClient.cs:29](../../../Services/TourKit/TourKitNccClient.cs))
đã có sẵn giá hợp đồng thật từ TourKit mà không ai dùng.

Hệ quả (user xác nhận cả 3): giá sai thực tế · lịch trình/dịch vụ không sát · không tái dùng được gì.

**Gốc chung:** AI không được thấy dữ liệu thật của công ty, nên nó bịa.

---

## 2. Dữ liệu thật — đã đo, không đoán

> Mọi con số dưới đây đo trực tiếp trên DB production (283 tenant) + bản `TopTour` khôi phục về local
> ngày 2026-07-15. Ghi lại vì **ba giả định ban đầu đều sai**, và spec này chỉ đáng tin ở mức các số này còn đúng.

### 2.1 Toàn nền tảng (280 tenant có bảng giá)

| Chỉ số | Giá trị |
|---|---|
| Nhà cung cấp | 95.991 |
| Dòng giá | 299.064 |
| **Dòng CÓ giá** | **119.911 — 40,1%** |
| NCC có điền `City` | 21.007 — 21,9% |

Phân bố theo công ty: **64 công ty điền ≥75%** · 47 công ty 50–74% · 50 công ty 25–49% ·
102 công ty 1–24% · 17 công ty gần như trống.

**Kết luận:** bảng giá NCC **không bị bỏ hoang**. Nhưng chất lượng **rất lệch giữa các tenant** —
thiết kế phải chạy được cả khi tenant không có giá nào.

### 2.2 Tenant chăm dữ liệu (`TopTour`, 13.775 dòng có giá / 988 NCC / 18 loại DV)

| Chỉ số | Giá trị | Ý nghĩa thiết kế |
|---|---|---|
| `City` được điền | 78% tổng · **98% với khách sạn** | `City` **dùng được**. Nó trống ở chỗ vốn không có địa điểm (vé máy bay, bảo hiểm) |
| `City` phân biệt | **45 giá trị, toàn tên tỉnh chuẩn** | không rác → lọc thẳng được |
| `contract_price` hợp lệ | **99,7%** (25/9.460 dòng < 50k) | **giá là trục lọc đáng tin nhất** |
| `price_name` có nội dung | 63,5% | không đủ tin để làm khóa |
| `ngay_di` (mùa vụ có cấu trúc) | **9,3%** | mùa vụ **không** nằm ở cột có cấu trúc |
| Bóc hạng sao từ tên NCC | **59%** (346/588) | chỉ làm **lọc phụ**, không bao giờ lọc cứng |
| Điểm đến trong tên NCC | 41% | không đủ để thay `City` |

### 2.3 Kích thước rổ — con số quyết định kiến trúc

| Rổ (Tỉnh × Loại DV) | Số **dòng giá** | Số **NCC** |
|---|---|---|
| Khách sạn / Khánh Hòa | 1.013 | **57** |
| Khách sạn / Đà Nẵng | 700 | **42** |
| Khách sạn / Hà Nội | 658 | **59** |
| LandTour | 438 | **72** |

Trên cả **106 rổ**: NCC nhiều nhất **72**, trung bình **9,4**, **số rổ vượt 100 NCC = 0**.

**Đơn vị truy xuất đúng là NHÀ CUNG CẤP, không phải dòng giá.** 1.013 dòng ở Khánh Hòa =
57 khách sạn × ~18 loại phòng. Đếm nhầm đơn vị → tưởng cần vector search.

### 2.4 Phát hiện quan trọng nhất: mùa vụ nằm trong chữ tự do

4.493/9.460 dòng khách sạn **không có tên phòng** nhưng **có `description`**. Đọc ra thì
`description` không phải loại phòng — nó là **điều kiện áp giá**:

```
"Mùa thấp điểm (5, 6, 9)"      "Lễ 2/9/2024"        "(CN – Thứ 5)"
"T6-T7"                        "Trên 10 phòng"      "Ưu đãi đặt sớm 60 ngày"
"FIT"                          "NỘI ĐỊA"            "-Giá KHUYẾN MÃI - ĐÃ bao gồm gói Spa"
```

Cùng một khách sạn có nhiều giá khác nhau **theo mùa / thứ trong tuần / lễ / số lượng / loại khách**,
và **toàn bộ chiều đó chỉ tồn tại dưới dạng tiếng Việt viết tay**. Đây là lý do
`Bel Marina Hội An` có 150 dòng: 24 phòng × ~6 điều kiện.

→ Chọn giá **không phải** "chọn phòng". Là "chọn phòng **và** khớp điều kiện". SQL không lọc nổi;
LLM đọc 18 dòng thì được. **Đây là chỗ AI thật sự đáng tiền trong tính năng này.**

---

## 3. Quyết định kiến trúc

### 3.1 KHÔNG dùng vector/embedding (v1)

| Lý do | Bằng chứng |
|---|---|
| Rổ đã nhét vừa prompt | max **72** NCC/rổ, TB 9,4 |
| Embedding **làm nhòe con số** — đúng thứ cần phân biệt | `"4 sao"` vs `"5 sao"`, `"16 chỗ"` vs `"45 chỗ"` cho vector gần như trùng nhau; giá chênh vài lần |
| Thêm phụ thuộc | Anthropic **không có** API embedding → phải cắm thêm key OpenAI; phải re-embed mỗi lần sync |
| DB local nhúng (sqlite-vec/LanceDB) **vỡ kiến trúc** | Catalog do **Worker** ghi, **Web** đọc — 2 process, deploy tách. File local worker ghi thì web không thấy. Đúng cái bẫy đã trả giá để thoát ở fix multi-tenant 2026-06-09 |

**Nhưng đây KHÔNG phải quyết định vĩnh viễn.** User nêu đúng một phản biện chưa bác được:
*số lượng sẽ tăng dần, nhét prompt không có dư địa*. Đo trên `TopTour` (988 NCC, cỡ trung) không đại diện
cho `DTour` (5.163 NCC). Nên:

- Retrieval nằm sau interface **`ITourPriceRetriever`**.
- Retriever **đếm ứng viên trước khi cắt**; vượt ngưỡng → log `retrieval_overflow{tenant, city, category, count}`.
- Khi có tenant thật chạm ngưỡng → số liệu quyết cần xếp hạng **từ khóa** hay **embedding**, ghép vào sau
  interface, **cùng SQL Server đó**, không đập lại gì.

Vector chỉ thắng thuyết phục khi corpus là **văn bản không cấu trúc** (PDF chương trình tour, email NCC).
Nguồn v1 là API + DB có cấu trúc sẵn → chưa tới lúc.

### 3.2 Trục lọc chính là GIÁ, không phải hạng sao

`Stars` bóc từ tên chỉ đúng **59%** → làm lọc cứng sẽ **âm thầm bỏ sót 41%**. `contract_price` sạch
**99,7%**, và sale luôn có ngân sách (`"4 sao Đà Nẵng tầm 2tr/đêm"`). Lọc `tỉnh + loại + khoảng giá ±25%`:

- **xác định** — không nhòe, tra là ra
- **không phụ thuộc tenant to hay nhỏ** — thêm 10.000 khách sạn thì dải giá vẫn thế, band vẫn cắt ra chừng đó
- hạng sao thực chất **là** giá: tên không ghi `"4*"` thì giá vẫn khai ra

### 3.3 AI KHÔNG BAO GIỜ ghi số tiền

AI chỉ trả **`pricingId`**. Server tra `contract_price` từ catalog rồi tự tính. Giá bịa bị chặn
**bằng cấu trúc**, không phải bằng kỷ luật prompt.

---

## 4. Thành phần

```
TourKit.Api  ──GET /api/ai/provider-prices (MỚI, phân trang)──┐
                                                              ▼
                         TourPriceCatalogSyncWorkflow (PerTenant, 1 lần/ngày, service account)
                                                              │ upsert
                                                              ▼
                              dbo.TourPriceCatalog  (TourKit_Push, PK TenantId+PricingId)
                                                              │
                                    ITourPriceRetriever ◄─────┘
                                            │ StructuredPriceRetriever (v1)
                                            ▼
                                  TourPricingService  ── tầng A → tầng B → composer
                                            ▼
                                        Wizard (FE)
```

### 4.1 `dbo.TourPriceCatalog`

PK `(TenantId, PricingId)` — theo convention `dbo.Mails`.

| Cột | Nguồn | Ghi chú |
|---|---|---|
| `TenantId`, `PricingId` | `provider_service_pricing.id` | khóa để server tra ngược giá thật |
| `ProviderServiceId`, `ProviderId` | | |
| `ProviderName`, `ProviderCode` | `providers` | **mang phần lớn ngữ nghĩa** (hạng sao + địa danh) |
| `City` | `providers.city` | tên tỉnh thô |
| `CityNorm` | tự sinh | bỏ dấu + thường hóa — tái dùng logic ở `ChatAgentService.MatchMarket` |
| `CategoryId`, `CategoryName` | `services` | |
| `PriceName`, `Description` | `provider_service_pricing` | **`Description` BẮT BUỘC giữ** — chứa điều kiện mùa vụ |
| `ContractPrice`, `PublicPrice` | | trục lọc chính |
| `Stars` | bóc từ `ProviderName` (regex `[1-5]\*` / `[1-5] sao`) | nullable, 59% — **lọc phụ** |
| `IsActive`, `SyncedUtc` | tự sinh | NCC ngừng → tắt cờ, **không xóa dòng** |

Index: `(TenantId, CategoryId, CityNorm)` · `(TenantId, CategoryId, ContractPrice)`

**Ước tính quy mô:** ~120.000 dòng có giá toàn nền tảng hiện nay. Nhỏ với SQL Server.

### 4.2 Endpoint mới upstream — `GET /api/ai/provider-prices`

**Repo khác (`toutkit-app`) — user đã duyệt.**

Lý do: đường hiện tại phải gọi `providers-by-service` cho từng loại rồi `providers/{id}/services` cho
**từng NCC** → `DTour` = **5.163 lời gọi/lần sync**, × 280 tenant. Một endpoint phân trang →
**~30 lời gọi**. Không có nó, sync tự đâm vào đúng cái tường scale cần tránh.

- JOIN sẵn `providers + provider_services + provider_service_pricing + services`
- Trả kèm `City` (hiện `providers/{id}/services` **không có**) → hết cảnh ghép 2 endpoint
- Mở luôn các cột API đang cắt: `ngay_di`, `ngay_ve`, `quantity`, `amount_of_people`, `contract_price_kt`
  — **có sẵn trong DB**, chỉ sửa SELECT ở `TourService.cs:3200-3224` + thêm field DTO. **Không đổi schema.**
- Phân trang `pageIndex`/`pageSize`, envelope `{success,data,message}` như mọi endpoint khác

### 4.3 `TourPriceCatalogSyncWorkflow`

`IScheduledWorkflow`, `Scope = PerTenant`, mặc định **1 lần/ngày**. Đăng ký trong
[`WorkflowStackRegistration`](../../../Services/Bootstrap/WorkflowStackRegistration.cs) → web + worker tự pickup.

- Auth: `TenantServiceAccountStore` + `TkSessionStore.GetOrCreateServiceSessionAsync` — **không cần user online**
- Phân trang qua endpoint mới; upsert; dòng không còn thấy → `IsActive = 0`
- **Rải lịch theo hash(tenantId)** — 280 tenant KHÔNG được bắn cùng 3h sáng
- **Giới hạn concurrency** — đúng bài học Vbee tuần này
- `AiCallContext.Push("tour-price-sync", tenantId)` — **STRICT**: chạy nền không có HttpContext, thiếu là
  bypass quota + log `feature=unknown`

**Loại trừ khi sync** (config, mặc định bật):
- Loại DV khớp `"vé máy bay"` (chuẩn hóa bỏ dấu) — chứa **tên hành khách thật**
  (`"TRINH/XUAN PHONG MR (ADT)"`, `"D5F8MS 1. DINH VAN QUYNH"`). Bê sang DB khác = mang PII đi
  không lý do; mà vé theo từng chuyến, tái dùng vô nghĩa.
- `ContractPrice < 50.000` — rác nhập tay (25/9.460 dòng: `25đ`, `330đ`, `700đ`).

### 4.4 `ITourPriceRetriever`

```csharp
public interface ITourPriceRetriever
{
    Task<PriceCandidates> FindProvidersAsync(PriceQuery q, CancellationToken ct);
    Task<IReadOnlyList<CatalogRow>> RowsForProviderAsync(string tenantId, int providerId, int categoryId, CancellationToken ct);
}
```

`StructuredPriceRetriever` (v1):
1. `destination` → tỉnh qua **`DestinationMap`** (bảng tĩnh ~60 dòng)
2. lọc `TenantId + CategoryId + CityNorm` (+ `ContractPrice` band ±25% nếu có ngân sách)
3. gom theo `ProviderId`; sắp theo độ gần ngân sách; **cắt 60**
4. `PriceCandidates.TotalBeforeCap` → vượt **80** thì log `retrieval_overflow`

### 4.5 `DestinationMap` — điểm đến → tỉnh

`City` là **tỉnh**, sale nói **điểm đến**. Không có bảng này thì lọc trượt sạch:

```
Nha Trang → Khánh Hòa     Hội An → Quảng Nam      Sapa → Lào Cai
Phú Quốc → Kiên Giang     Mũi Né → Bình Thuận     Đà Lạt → Lâm Đồng
Cát Bà → Hải Phòng        Hạ Long → Quảng Ninh    Quy Nhơn → Bình Định
```

Tập đóng (45 tỉnh trong dữ liệu, 63 toàn quốc) → bảng tĩnh, tra là ra, **không cần đoán, không cần AI**.
Không khớp → không lọc theo tỉnh (rộng hơn còn hơn sai).

### 4.6 Hai tầng chọn giá

**Tầng A — chọn NCC.** Danh sách ứng viên (≤60, thực tế ~10–45): `ProviderName` (có sẵn hạng sao +
địa danh), `City`, `Stars`, khoảng giá min–max, số dòng. AI chọn `providerId`.

**Tầng B — chọn dòng giá.** Nạp ~18 dòng của đúng NCC đó, **kèm `Description`**. Prompt nêu rõ
ngày khởi hành + thứ trong tuần + số khách. AI đọc `"Mùa thấp điểm (5,6,9)"` / `"T6-T7"` / `"Lễ 2/9"` /
`"Trên 10 phòng"` rồi chọn `pricingId`.

Cả 2 tầng dùng **native tool-calling khi provider = anthropic**, JSON-prompt cho provider khác — theo
đúng pattern in-service routing của Visa/Deal/Tour (xem CLAUDE.md § "Native function-calling").

### 4.7 Nguồn giá trên mỗi dòng

| `source` | Nghĩa | UI |
|---|---|---|
| `contract` | có `pricingId`, **server tính từ DB** | bình thường + hiện `Description` cạnh giá |
| `estimated` | không tìm ra dòng khớp → AI ước lượng | **badge vàng "ước lượng — chưa xác minh"** |

Tenant không nhập giá (như `VNE` 0,4%) → tất cả `estimated`. Tính năng **vẫn chạy**, chỉ là không có gì
để dựa vào — và người dùng **thấy rõ** điều đó.

---

## 5. Rủi ro đã biết

| Rủi ro | Xử lý |
|---|---|
| **Mùa vụ chỉ có trong chữ tự do** (`ngay_di` 9,3%) → AI đọc sai điều kiện | UI **hiện nguyên `Description`** của dòng đã chọn cạnh giá → NV liếc một cái là biết `"Lễ 2/9"` có áp cho đoàn 15/7 không. **Không giấu quyết định của AI sau lưng người dùng** |
| `Stars` chỉ 59% | không bao giờ làm lọc cứng — chỉ hiển thị + xếp hạng phụ |
| Tenant không có giá | rơi về `estimated`, badge vàng |
| Rổ phình khi tenant lớn dần | `retrieval_overflow` log → có số thật rồi mới quyết vector/từ khóa |
| Sync 280 tenant | rải theo hash(tenantId) + giới hạn concurrency |
| **VAT không tồn tại ở bất kỳ tầng dữ liệu nào** | NV tự điền, AI không đoán |

---

## 6. Phi mục tiêu (v1)

- **Tham chiếu báo giá cũ** (`dbo.TourQuotes`) — toàn hệ thống **23 báo giá**, `TopTour` **0**. Không đủ
  dữ liệu để làm gì. **Spec riêng, sau.**
- Vector/embedding — xem §3.1. Có chỗ chờ, chưa xây.
- Upload file bảng giá (Excel/PDF) — user không chọn ở bước khảo sát nguồn.
- Ghi ngược giá lên TourKit — catalog **chỉ đọc**.

---

## 7. Test

Repo có sẵn `TourkitAiProxy.Tests` (xUnit). Test **logic thuần**, đúng như phần còn lại của repo:

- `DestinationMap` — `"Hội An"` → `Quảng Nam`, bỏ dấu, không khớp → null
- Bóc `Stars` — `"Khách Sạn 4* DTX Nha Trang"` → 4; `"Affa Boutique Hotel"` → null
- Khoảng giá + xếp hạng theo độ gần ngân sách
- Lọc loại trừ: `"Vé máy bay HHK"` bị chặn, `ContractPrice < 50k` bị chặn
- `StructuredPriceRetriever` trên repository giả — gồm ca `TotalBeforeCap > 80` phải log overflow

Sync/IMAP/FE kiểm bằng tay (không có hạ tầng integration test).

---

## 8. Thứ tự triển khai

Tách 2 mảng — mảng 1 tự kiểm chứng được mà chưa cần đụng FE:

1. **Catalog + sync** — endpoint upstream, bảng, workflow, repository. Nghiệm thu: *catalog của `TopTour`
   khớp số đếm trực tiếp trong DB (13.775 dòng có giá, trừ vé máy bay)*.
2. **Retriever + Wizard** — `DestinationMap`, `StructuredPriceRetriever`, 2 tầng chọn, composer, `source`
   badge, hiện `Description`.

---

## Phụ lục: ba lần đo, ba lần sai

Ghi lại để người sau khỏi đi vào cùng vết xe:

| Kết luận vội | Thực tế | Vì sao sai |
|---|---|---|
| *"Bảng giá NCC rỗng — 91/93 dòng giá 0"* | Toàn nền tảng **40,1% có giá**; 64 công ty điền ≥75% | Vơ đũa từ **đúng 1 tenant** (`VNE` 0,4% — gần bét bảng) |
| *"`City` vô dụng — chỉ 21,9%"* | `TopTour` **78%**, riêng khách sạn **98%**, 45 tên tỉnh sạch | Lấy trung bình toàn nền tảng, bị tenant bỏ bê kéo xuống. `City` trống ở chỗ **vốn không có địa điểm** |
| *"Rổ 1.013 dòng — phải có vector"* | Rổ đó là **57 khách sạn** × 18 phòng. Max toàn hệ **72 NCC** | Đếm **dòng giá** thay vì đếm **nhà cung cấp** — sai đơn vị truy xuất |

**Bài học:** đo trước khi thiết kế, và **một tenant không phải là nền tảng**. Con số trong §2 chỉ đúng
tại 2026-07-15 — đo lại trước khi dựa vào chúng để quyết định lớn.
