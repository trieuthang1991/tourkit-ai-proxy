# Quy ước xử lý DateTime (UTC) — BẮT BUỘC

> Lý do tồn tại file này: bug "giờ chạy hiện 7 tiếng trước dù vừa chạy" (VN = UTC+7). Gốc rễ là DateTime
> bị lệch múi giờ ở 3 điểm: lưu DB, serialize ra client, và parse chuỗi. File này là **1 nguồn** quy ước.

## Nguyên tắc vàng

**Lưu UTC → truyền kèm `Z` → frontend tự đổi sang giờ local để hiển thị.**

- Backend **không bao giờ** lưu/truyền giờ local. Mọi mốc thời gian trong DB là **UTC**.
- Client (`new Date(...)`) chỉ parse đúng khi chuỗi có hậu tố `Z` (hoặc offset). Thiếu `Z` → nó coi là **giờ local** → lệch +7h.

## 1. LƯU xuống DB → luôn UTC

| Làm | KHÔNG làm |
|-----|-----------|
| `DateTime.UtcNow` | ❌ `DateTime.Now` / `DateTime.Today` (giờ local) |
| SQL `SYSUTCDATETIME()` | ❌ SQL `GETDATE()` / `SYSDATETIME()` (giờ server local) |

`DateTime.Now`/`Today` **chỉ** được dùng cho thứ KHÔNG lưu DB và KHÔNG so sánh UTC: tên file export,
nhãn "hôm nay" trong prompt/UI. Nếu lỡ lưu xuống DB là **bug**.

## 2. PARSE chuỗi ngày rồi LƯU → ép UTC

`DateTime.Parse` / `TryParse` **mặc định trả về `Kind=Local`** (tự cộng offset máy) → nếu lưu thẳng vào
`DATETIME2` thì **lưu giờ local, sai chuẩn**.

```csharp
// ❌ SAI — chuỗi "….Z" (UTC) bị đổi sang local trước khi lưu
var dt = DateTime.TryParse(iso, out var d) ? d : DateTime.UtcNow;   // d.Kind = Local

// ✅ ĐÚNG — giữ UTC
var dt = DateTime.TryParse(iso, CultureInfo.InvariantCulture,
    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
    ? d : DateTime.UtcNow;   // d.Kind = Utc
```

## 3. ĐỌC từ DB → Dapper trả `Kind=Unspecified`

Cột `DATETIME2` đọc qua Dapper có `Kind=Unspecified` → **không tự có `Z`** khi serialize. Phải đánh dấu UTC.

### 3a. Field kiểu `DateTime`/`DateTime?` trả qua JSON HTTP → ĐÃ TỰ ĐỘNG

[`Services/Json/UtcDateTimeConverter.cs`](../Services/Json/UtcDateTimeConverter.cs) (đăng ký global ở
`Program.cs` qua `ConfigureHttpJsonOptions`) tự serialize **mọi `DateTime`** kèm `Z`. **Không cần làm gì thêm**
cho field DateTime-typed trong response.

### 3b. Tự build chuỗi bằng `ToString("o")` → PHẢI `SpecifyKind`

Converter ở 3a **không** áp dụng cho chuỗi tự build. Khi `.ToString("o")` một DateTime đọc từ SQL:

```csharp
// ❌ SAI — Unspecified → chuỗi KHÔNG có Z
CreatedAt = row.CreatedAt.ToString("o")

// ✅ ĐÚNG
CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc).ToString("o")
```

`DateTime.UtcNow.ToString("o")` thì OK sẵn (`Kind=Utc` → có `Z`).

## 4. Frontend

- Parse: `new Date(isoCoZ)` — chuỗi từ backend phải có `Z` (xem mục 3).
- Hiển thị "x phút trước" / ngày: dùng `window.tourkitUtil.fmtAgo` / `fmtDate` (1 nguồn, `lib/util.js`).
- **Không** tự format giờ ở backend để gửi sang client hiển thị (mất context timezone). Gửi ISO-UTC, để client format.

## Checklist khi THÊM cột thời gian mới

1. [ ] Lưu bằng `DateTime.UtcNow` hoặc `SYSUTCDATETIME()` (mục 1).
2. [ ] Nếu nhận chuỗi ngày từ ngoài để lưu → parse `AssumeUniversal | AdjustToUniversal` (mục 2).
3. [ ] Trả client: field `DateTime` → tự có `Z` (3a); nếu `ToString("o")` → `SpecifyKind(Utc)` (3b).
4. [ ] Frontend dùng `tourkitUtil.fmtAgo/fmtDate`, không tự cộng/trừ giờ.

## Nợ kỹ thuật đã biết (chưa fix — tự-triệt-tiêu)

`Mail.ReceivedAt` và `Visa.CreatedAt/UpdatedAt` hiện **lưu giờ LOCAL** (mục 2 chưa áp dụng) NHƯNG đọc ra
dạng string không-Z (mục 3b chưa áp dụng) → hai cái sai **triệt tiêu nhau** → hiển thị vẫn đúng.
⚠️ Sửa cho chuẩn phải làm **cả 2 phía cùng lúc + migrate row cũ (-7h)**, nếu sửa lẻ 1 phía sẽ **vỡ hiển thị**.
Xem commit `6ca74bc` để biết bối cảnh.
