# Mail templates cho hàng đợi `dbo.OutboundMails`

Proxy (workflow `deal-auto-review`) **không** soạn HTML — chỉ enqueue 1 dòng vào `dbo.OutboundMails` với
`TemplateCode` + `[Params]` (JSON). **Worker (CEO viết)** đọc dòng `Status=0`, load template HTML theo
`TemplateCode`, replace tham số, resolve người nhận, gửi SMTP, cập nhật `Status`.

Template lưu/quản lý ở đâu là tùy worker (file, DB, embedded resource...). Thư mục này chỉ chứa **mẫu khởi đầu**.

## `deal-cooling-alert`

File mẫu: [`deal-cooling-alert.sample.html`](deal-cooling-alert.sample.html).

**Tham số (`[Params]` JSON) proxy luôn cung cấp** — ổn định, versioned theo `TemplateCode`:

| Key | Kiểu | Mô tả |
|---|---|---|
| `dealId` | int | Id cơ hội (nối `BookingTickets.id` để worker resolve NV) |
| `dealCode` | string? | Mã phiếu |
| `customerName` | string | Tên khách |
| `phone` | string? | SĐT khách |
| `title` | string? | Tiêu đề cơ hội |
| `totalPriceFormatted` | string | Giá trị đã format (vd "32.000.000 đ") |
| `statusName` | string? | Tên trạng thái |
| `sourceName` | string? | Nguồn |
| `assigneeNames` | string? | Tên NV phụ trách (để chào; worker resolve email riêng) |
| `coolingDays` | int | Số ngày nguội |
| `lastInteractionAt` | string? | Lần chạm gần nhất (ISO) |
| `hasReview` | bool | Deal đã được AI chấm chưa → ẩn/hiện khối gợi ý |
| `winRate` | int? | % khả năng chốt (nếu `hasReview`) |
| `level` | string? | `cao`/`trung_binh`/`thap` |
| `nextAction` | string? | Hành động AI gợi ý làm tiếp |

## Hợp đồng worker (tóm tắt)

1. Poll: `SELECT TOP N * FROM dbo.OutboundMails WHERE Status=0 AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME()) ORDER BY CreatedUtc` — **giờ UTC** (so sánh bằng `DateTime.UtcNow`).
2. Render: load template theo `TemplateCode` → replace `{{key}}` từ `[Params]`. `Subject` lấy từ template hoặc cột `Subject`.
3. `Kind='deal-cooling-alert'`: đọc `Data.dealId` → tenant DB `BookingTickets.NguoiPhuTrachs` → `Users.email` (1 deal nhiều NV → gửi nhiều / Cc).
4. Gửi xong → `Status=1 (Sent)`, `ProcessedUtc=SYSUTCDATETIME()`. Lỗi → `Status=2 (Failed)`, `ErrorMessage`, `RetryCount++`.
5. Cancel khi deal hết nguội (tùy chọn, phase sau) → `Status=3 (Cancelled)` theo `SourceId`.

**Status (TINYINT):** `0=Pending 1=Sent 2=Failed 3=Cancelled 4=Skipped`.
