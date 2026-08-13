# Mail templates cho hàng đợi `dbo.OutboundMails`

Proxy (workflow `deal-auto-review`, `sale-brief`, `ceo-brief`) **không** soạn HTML — chỉ enqueue 1 dòng vào `dbo.OutboundMails` với
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

## `daily-brief` (bản tin sáng — Đợt 1)

File mẫu: [`daily-brief.sample.html`](daily-brief.sample.html). Dùng cho CẢ `sale-brief` (nhân viên bán
hàng) và `ceo-brief` (giám đốc) — phân biệt qua `briefType`.

| Key | Kiểu | Mô tả |
|---|---|---|
| `title` | string | Tiêu đề bản tin, vd `"Bản tin sáng 12/08 — Nguyễn Văn A"` |
| `bodyHtml` | string | **TOÀN BỘ nội dung đã render sẵn thành HTML** — chèn nguyên vào thân mail |
| `briefType` | string | `sale-brief` \| `ceo-brief` |
| `date` | string | Ngày **Việt Nam** `dd/MM/yyyy` (khớp ngày người đọc thấy trên lịch của họ) |

⚠️ **`bodyHtml` ĐÃ escape ở proxy** ([`SaleBriefBuilder.ToHtml`](../../Services/Digest/SaleBriefBuilder.cs)) —
worker chèn NGUYÊN, **không escape lần nữa**, nếu không người đọc thấy `&lt;b&gt;` thay vì chữ in đậm.

Người nhận: đọc thẳng cột `ToEmail` (chính người dùng tự khai ở "Bản tin của tôi") — **không** phải resolve
từ CRM như `deal-cooling-alert`. `Username` để `null` nghĩa là gửi bằng hộp thư của công ty.
`SourceId` dạng `{briefType}:{username}:{yyyy-MM-dd}` → mỗi người mỗi ngày đúng 1 dòng, dễ đối soát khi có
người báo "hôm nay không nhận được mail".

Thiếu template này worker **vẫn gửi được** (tự render từ `Params`) — mẫu chỉ để mail đẹp + có chữ ký.

## Kênh gửi (`Channel`, TINYINT)

Từ 2026-08 hàng đợi này thành đa kênh (email/Telegram/Zalo) cho bản tin sáng, không chỉ email. Cột
`Channel` phân biệt dòng nào worker này (SMTP) được nhặt — enum
[`OutboundChannel`](../../Services/Digest/OutboundChannel.cs), **worker toutkit-app phải MIRROR đúng
bảng số** này:

| Số | Tên | Ai xử lý |
|---|---|---|
| 0 | `Email` (default) | Worker này (SMTP) |
| 1 | `Telegram` | Kênh khác (proxy tự gửi qua bot Telegram, KHÔNG qua hàng đợi này giai đoạn đầu / hoặc drainer riêng — xem plan) |
| 2 | `Zalo` | Kênh khác (Zalo OA) |

Dòng cũ (trước khi có cột `Channel`) tự mang giá trị mặc định `0` (email) — không cần migrate data.

⚠️ **BẮT BUỘC deploy filter `AND Channel=0` TRƯỚC khi proxy bắt đầu enqueue Telegram/Zalo vào bảng
này**, nếu không worker sẽ nhặt nhầm dòng kênh khác đem đi gửi SMTP (địa chỉ đích sai định dạng, gửi
lỗi hàng loạt).

## Hợp đồng worker (tóm tắt)

1. Poll: `SELECT TOP N * FROM dbo.OutboundMails WHERE Status=0 AND Channel=0 AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME()) ORDER BY CreatedUtc` — **giờ UTC** (so sánh bằng `DateTime.UtcNow`). **Lọc `Channel=0`** để chỉ nhặt dòng email — dòng kênh khác (Telegram/Zalo) không phải việc của worker SMTP này.
2. Render: load template theo `TemplateCode` → replace `{{key}}` từ `[Params]`. `Subject` lấy từ template hoặc cột `Subject`.
3. Người nhận:
   - `Kind='deal-cooling-alert'`: đọc `Data.dealId` → tenant DB `BookingTickets.NguoiPhuTrachs` → `Users.email` (1 deal nhiều NV → gửi nhiều / Cc).
   - `Kind='daily-brief'`: dùng thẳng `ToEmail` (người dùng tự khai) — KHÔNG tra CRM.
4. Gửi xong → `Status=1 (Sent)`, `ProcessedUtc=SYSUTCDATETIME()`. Lỗi → `Status=2 (Failed)`, `ErrorMessage`, `RetryCount++`.
5. Cancel khi deal hết nguội (tùy chọn, phase sau) → `Status=3 (Cancelled)` theo `SourceId`.

**Status (TINYINT):** `0=Pending 1=Sent 2=Failed 3=Cancelled 4=Skipped`.
