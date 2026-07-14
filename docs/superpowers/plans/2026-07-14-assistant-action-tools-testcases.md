# Test Cases — Trợ lý hành động (/assistant + /travai)

> Bộ test kiểm thử tính năng action tools. Cột **Loại**: `AUTO` (unit đã có / chạy `dotnet test`) · `SMOKE` (đã chạy runtime) · `MANUAL` (cần login + thao tác tay). **Ưu tiên**: P0 (chặn merge) · P1 (quan trọng) · P2 (nên có).
>
> Cách chạy: `dotnet run` → mở `http://localhost:5080/assistant` (hoặc `/travai`) → đăng nhập (token hoặc form). Cần: session TourKit hợp lệ, `appsettings.json` có AI key + DB + (Gmail App Password nếu test mail).

---

## 0. Đã kiểm (không cần làm lại)

| ID | Loại | Case | Kết quả |
|----|------|------|---------|
| S1 | AUTO | 143 unit test (payload builder, MapPriority, **ParseUtc UTC+7**, Norm/TokenSubset, catalog flags) | ✅ 143 pass / 2 skip |
| S2 | SMOKE | App boot + tạo bảng `dbo.CrmActionQueue` | ✅ log schema xác nhận |
| S3 | SMOKE | `/healthz` 200; `/providers` 200 (read path sống) | ✅ |
| S4 | SMOKE | `POST /assistant/action/execute` thiếu session → 401 | ✅ |
| S5 | SMOKE | `GET /workflows/crm-queue` thiếu session → 401 | ✅ |

---

## 1. Định tuyến planner (action vs read) — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| R1 | MANUAL | Hỏi số liệu thuần: "doanh thu tháng này" | Vẫn ra **bảng + chart** như cũ (planner chọn `tool`, KHÔNG rơi vào nhánh action) |
| R2 | MANUAL | "so sánh doanh thu 3 tháng gần đây" | Chart time-series bình thường (regression read path) |
| R3 | MANUAL | "đánh giá khách Nguyễn Văn A" | Planner chọn `action=review_customer` (log `?debug=1` hoặc SSE `kind:action-result`) |
| R4 | MANUAL | "giao việc gọi lại khách cho Minh" | Planner chọn `action=assign_task` |
| R5 | MANUAL | Câu mơ hồ nửa đọc nửa làm: "khách A mua gì" | Route hợp lý (đọc), KHÔNG execute nhầm action |

## 2. Run-through actions (chạy thẳng, hiện kết quả) — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| T1 | MANUAL | "cho tôi thông tin khách **X**" → rồi "đánh giá khách này" | Lượt 2 nhớ ngữ cảnh (customerId lượt 1), hiện **thẻ hạng A–D** + gợi ý; KHÔNG hỏi lại tên |
| T2 | MANUAL | Kiểm DB sau T1 | Có row mới/cập nhật trong `dbo.Reviews` (rank sync về CRM qua worker) |
| T3 | MANUAL | "đánh giá lại khách X" | forceFresh=true → chấm mới (không trả cache) |
| T4 | MANUAL | "chấm điểm deal của khách **B**" | Thẻ điểm deal + row `dbo.DealScores` |
| T5 | MANUAL | "kiểm tra mail mới" (hộp thư đã cấu hình) | Sync + tóm tắt "N mail mới: …" + `ActionDataCard` mail-list |
| T6 | MANUAL | Quota: xem `dbo.AiUsageHistory` sau T1/T4 | Có dòng `feature=assistant-action`, đúng tenant (KHÔNG `unknown/null`) |

## 3. Confirm-first actions (thẻ xác nhận) — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| C1 | MANUAL | "trả lời khách **C**, xin lỗi và hẹn xử lý hôm nay" | Hiện **thẻ xác nhận** với nháp AI (sửa được); **CHƯA gửi** |
| C2 | MANUAL | Sửa nội dung nháp → bấm **Xác nhận** | Gọi `/action/execute` → SMTP gửi thật → "✅ Đã gửi"; mail status → `da_phan_hoi` |
| C3 | MANUAL | Bấm **Hủy** trên thẻ C1 | Thẻ biến mất, KHÔNG gửi gì |
| C4 | MANUAL | "soạn email mới cho abc@x.com về báo giá tour Hàn" | Thẻ xác nhận có to/subject/text (nháp AI) → xác nhận → gửi |
| C5 | MANUAL | "giao việc gọi lại khách A cho Minh, hạn ngày mai" | Thẻ: việc/giao cho/hạn → xác nhận → "✅ đã vào hàng đợi" |
| C6 | MANUAL | Sau C5 mở `/workflows` card "Hàng đợi CRM" | Có 1 row Kind=Giao việc, Trạng thái **Chờ ⏳**, nội dung đúng |
| C7 | MANUAL | "đặt lịch hẹn tư vấn với khách A ngày mai 9h" | Thẻ lịch hẹn → xác nhận → row create-appointment trong hàng đợi |

## 4. Payload CRM đúng (worker sync được) — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| P1 | MANUAL | Sau C5, đọc `PayloadJson` row assign-task (SQL) | Có `name, staffsInCharge`(CSV id đã resolve), `workflowName`(thô), `workflowId:null`, `endDate`, `prioritized`, `status:1` |
| P2 | MANUAL | **DateTime UTC+7**: C5 "hạn mai 9h" | `endDate` trong payload = 02:00Z (9h VN − 7) — KHÔNG phải 09:00Z |
| P3 | MANUAL | Sau C7, đọc `PayloadJson` create-appointment | `customerId`(resolve), `careTitle`, `careStartTime/careEndTime` (UTC đúng), `status:1` |

## 5. Resolve tên → id — P1

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| N1 | MANUAL | "giao việc cho **Minh**" (2 NV tên Minh) | **Thẻ clarify** liệt kê 2 người (kèm hint), KHÔNG tự đoán |
| N2 | MANUAL | Chọn 1 người ở N1 | Gửi lại → resolve đúng → tiếp thẻ xác nhận |
| N3 | MANUAL | "đánh giá khách **Zzz Không Tồn Tại**" | "Không tìm thấy khách …" — KHÔNG crash |
| N4 | MANUAL | "giao việc cho Minh, Hoa" (2 người) | `staffsInCharge` = CSV 2 id |

## 6. Edge cases — P1/P2

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| E1 | MANUAL | "đánh giá khách này" khi CHƯA nhắc khách nào | Hỏi lại "khách nào?" — KHÔNG đoán |
| E2 | MANUAL | Bấm **Xác nhận** 2 lần nhanh (C5) | Idempotent — chỉ 1 row hàng đợi (theo actionId) |
| E3 | MANUAL | "trả lời khách C" khi hộp thư **chưa cấu hình** | "Chưa cấu hình hộp thư" — KHÔNG throw 500 |
| E4 | MANUAL | Gửi mail khi App Password sai (SMTP fail) | Báo lỗi + cho gửi lại; status **KHÔNG** đổi `da_phan_hoi` |
| E5 | MANUAL | Review đã fresh (fingerprint trùng) → "đánh giá khách X" lần 2 (không forceFresh) | Trả cache / báo "đã có đánh giá gần đây" |
| E6 | MANUAL | Hết quota tenant → chạy review | 429 `{error, quota}` (middleware), UI chip đỏ |

## 7. Bảo mật / tenant — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| SEC1 | SMOKE✅ | Endpoint action thiếu session | 401 (đã verify S4/S5) |
| SEC2 | MANUAL | `/action/execute` với body chứa `tenantId` giả | Bị bỏ qua — tenant lấy từ session, KHÔNG từ body |
| SEC3 | MANUAL | Session tenant A xem `/workflows/crm-queue` | Chỉ thấy row tenant A (không leak tenant B) |
| SEC4 | MANUAL | Session hết hạn giữa chừng | Auto re-login (GetValidJwtAsync) hoặc 401 rõ ràng |

## 8. Frontend / Voice — P1

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| F1 | MANUAL | `/assistant` thẻ review (T1) render | `ActionDataCard` kind=customer-review: badge hạng, điểm mạnh/lo ngại |
| F2 | MANUAL | `/assistant` prod bundle (`build-frontend.ps1` rồi F5) | KHÔNG trắng trang, KHÔNG lỗi React #130 (component có trong bundle) |
| F3 | MANUAL | **/travai voice**: "đánh giá khách A" | JARVIS **đọc** kết quả (TTS) + hiện thẻ — hands-free |
| F4 | MANUAL | **/travai voice**: "giao việc cho Minh" | JARVIS đọc "kiểm tra và bấm Xác nhận" + thẻ; **KHÔNG** auto-execute, **KHÔNG** nghe "xác nhận" bằng giọng (chỉ tap) |
| F5 | MANUAL | `/travai` bấm Xác nhận trên thẻ | Gọi execute → đọc kết quả |

## 9. Regression — P0

| ID | Loại | Bước | Mong đợi |
|----|------|------|----------|
| G1 | MANUAL | Toàn bộ luồng đọc số liệu cũ (5–6 câu hỏi các loại) | Hoạt động y như trước khi thêm action |
| G2 | MANUAL | `/mail`, `/customers`, `/deals` (feature cũ) | Không bị ảnh hưởng |
| G3 | AUTO✅ | `dotnet test` full | 143 pass / 2 skip |

---

## Giới hạn đã biết (không phải bug — ghi để khỏi test nhầm)

- **Path Anthropic native-tool CHƯA nhận action** (default là JSON-prompt). Nếu đổi `Providers:Default=anthropic` thì action bị bỏ qua im lặng → phase 2. Test action = giữ default provider.
- **`send_mail_reply` cần `mailId`** (lấy từ ngữ cảnh mail vừa liệt kê). Chưa có tìm mail bằng mô tả tự do.
- **Draft mail sinh ở bước đề xuất** đã ghi nháp + đổi status `dang_xu_ly` trước khi bấm Xác nhận (giống UX manual hiện có) — không phải bug.
- **Đồng bộ CRM thật** (POST /api/tasks, /api/customer-care) do **worker app-side** làm — proxy chỉ enqueue. Row hàng đợi ở trạng thái Chờ tới khi worker chạy.

## Ưu tiên chạy (nếu ít thời gian)
P0 tối thiểu: **R1, R3, T1, C1→C2, C5→C6, P2 (UTC+7), SEC3, G1**. Đủ bao phủ: read path còn sống · run-through · confirm-first · enqueue · giờ đúng · không leak tenant · không vỡ feature cũ.
