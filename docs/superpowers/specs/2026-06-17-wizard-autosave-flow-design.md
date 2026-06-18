# Wizard báo giá — Auto-lưu DB + đồng bộ giá B2→B3 (bỏ Redis tạm)

- **Ngày:** 2026-06-17
- **Phạm vi:** `tourkit-ai-proxy/wwwroot` — `pages/wizard.jsx`, `steps/step2.jsx`
- **Trạng thái:** đã duyệt (brainstorm + 4 câu xác nhận với user)

## Vấn đề

1. **Lưu tạm qua Redis** (`POST /api/v1/tour-quotes/draft`, TTL 24h) + banner *"CHƯA LƯU vào HỆ THỐNG · Đang ở Redis tạm · sẽ mất nếu hết 24h hoặc Redis restart"* + nút **"LƯU NGAY VÀO HỆ THỐNG"** → rườm rà, dễ mất, user thấy thừa.
2. **Sửa ở Bước 2 không sang Bước 3:** `Step2Itinerary` chỉ sửa `itinerary` (không có `rows`/`setRows`). `rows` (Bảng tính giá / Bước 3) chỉ derive **1 lần lúc generate** → sửa hoạt động / giá / chọn NCC ở B2 không phản ánh sang B3; bấm "Tiếp tục" cũng không đổi.

## Giải pháp (đã duyệt)

### A. Auto-lưu vào bản ghi tour DB — bỏ Redis tạm
- **Auto-save khi sửa:** debounce ~1.5s khi `request`/`itinerary`/`rows`/`marketing` đổi → `POST /api/v1/tours` upsert cùng `currentTour.id` (TenantStore — bản ghi bền, không TTL). Lần đầu chưa có id → tạo, giữ id cho các lần sau.
- **Lưu ngay khi qua bước:** 2→3, và 3→4 (sau khi sinh marketing) gọi save ngay (không chờ debounce) cho chắc.
- **Gỡ:** `POST /api/v1/tour-quotes/draft` autosave, state `draftStatus`/`quoteId`/`lastDraftAt`/`lastCommitAt`, `autosaveTimerRef`/`skipNextAutosaveRef`, khối UI `wiz-save-bar`, nút "LƯU NGAY VÀO HỆ THỐNG".
- **Thêm chỉ báo:** state `saveState` (`idle|saving|saved`) + `lastSavedAt` → hiển thị **"Đang lưu…"** / **"✓ Đã lưu HH:MM"** nhỏ ở stepbar (xanh, không nút).
- ⇒ "Lưu 1 lần rồi sửa lại" = tự lưu lại, chỉ báo nhảy giờ mới; user không bao giờ phải bấm.

### B. Tính lại `rows` khi bấm "Tiếp tục: Bảng tính giá" (2→3)
- Step2 `onNext`: re-derive `rows` từ `itinerary` hiện tại (đúng mapping như `handleGenerate`: mỗi activity → 1 row `{type, service, supplier, qty, priceNet, vat, markup, verified, costType, dayIdx}`).
- **Merge giữ chỉnh tay:** với hoạt động còn tồn tại (khớp theo key `dayIdx + index` / `service`), giữ `markup` & `vat` user đã sửa ở B3; cập nhật `priceNet` + `costType` + `supplier` từ B2.
- ⇒ Sửa B2 → qua B3 thấy đúng số; không mất markup đã chỉnh.

### C. Dọn UI
- Xóa `wiz-save-bar` + nút "LƯU NGAY"; thay bằng chỉ báo "Đã lưu HH:MM".

## Không làm (YAGNI)
- Không nút "Lưu" thủ công (auto-save đủ).
- Không đổi TenantStore Redis-or-file (đó là store **bền**, không phải lớp "tạm" cần bỏ).
- Không đụng `/api/v1/tour-quotes` (vẫn dùng cho share link công khai `/q/{id}` ở Bước 4).

## Verify
- Sửa hoạt động → chỉ báo "Đang lưu… → ✓ Đã lưu HH:MM"; reload thấy giữ nguyên (đã vào `/api/v1/tours`).
- Sửa giá/NCC ở B2 → "Tiếp tục" → B3 thấy số mới; markup chỉnh tay ở B3 không bị mất khi quay lại B2 rồi sang B3.
- Không còn banner Redis / nút "LƯU NGAY".
