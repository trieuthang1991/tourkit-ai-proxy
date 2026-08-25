# Mail templates cho hàng đợi `dbo.OutboundMails`

Proxy (workflow `deal-auto-review`, `sale-brief`, `ceo-brief`) **không** soạn HTML — chỉ enqueue 1 dòng vào `dbo.OutboundMails` với
`TemplateCode` + `[Params]` (JSON). **`TourKit.PushWorker` bên toutkit-app** đọc dòng `Status=0`, load template HTML theo
`TemplateCode`, replace tham số, resolve người nhận, gửi SMTP, cập nhật `Status`.

Template lưu ở `dbo.MailTemplates` (admin sửa được ở `/admin-trav-ai`), thiếu thì worker rơi về mẫu trong code. Thư mục này chứa **mẫu khởi đầu**.

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

⚠️ **`bodyHtml` ĐÃ escape ở proxy** ([`SaleBriefBuilder.ToHtml`](../../TourkitAiProxy.Domain/Digest/SaleBriefBuilder.cs)) —
worker chèn NGUYÊN, **không escape lần nữa**, nếu không người đọc thấy `&lt;b&gt;` thay vì chữ in đậm.

Người nhận: đọc thẳng cột `ToEmail` (chính người dùng tự khai ở "Bản tin của tôi") — **không** phải resolve
từ CRM như `deal-cooling-alert`. `Username` để `null` nghĩa là gửi bằng hộp thư của công ty.
`SourceId` dạng `{briefType}:{username}:{yyyy-MM-dd}` → mỗi người mỗi ngày đúng 1 dòng, dễ đối soát khi có
người báo "hôm nay không nhận được mail".

Thiếu template này worker **vẫn gửi được** (tự render từ `Params`) — mẫu chỉ để mail đẹp + có chữ ký.

## Hợp đồng CHUNG cho thư thông báo — `title` + `bodyHtml`

**Đây là bộ tham số mặc định cho MỌI loại thư mới.** Dòng nào mang đúng bộ này thì worker dựng được
thành thư hoàn chỉnh (tiêu đề · ngày · thân bài · chân thư) **mà không cần mẫu riêng và không cần
khai gì trong `dbo.MailTemplates`**.

| Key | Kiểu | Mô tả |
|---|---|---|
| `title` | string | Tiêu đề thư, cũng là dòng chủ đề nếu cột `Subject` để trống |
| `bodyHtml` | string | Thân thư đã render sẵn HTML — worker chèn NGUYÊN, không escape lần nữa |
| `date` | string? | Ngày hiển thị dưới tiêu đề. Bỏ trống thì không hiện dòng ngày |

Đang dùng bộ này: `daily-brief` · `payment-alert` · `auto-care` · `anomaly-alert`.

⚠️ **Trước 18/08 chỉ `deal-cooling-alert` có mẫu riêng, mọi mã khác rơi xuống bảng tham số thô** —
người nhận mở thư ra thấy đúng ba dòng `title / bodyHtml / date` kèm một đống HTML, tưởng hệ thống
hỏng. Mà gửi vẫn báo THÀNH CÔNG nên không lỗi nào nổi lên. Đã xảy ra thật với thư nhắc chăm khách.

⚠️ **Escape `bodyHtml` phải TỐI THIỂU — chỉ `& < > "`** ([`MailHtml.Esc`](../../TourkitAiProxy.Domain/Digest/MailHtml.cs)).
KHÔNG dùng `WebUtility.HtmlEncode`: nó mã hoá cả chữ có dấu, "Những khách này" thành
`Những kh&#225;ch n&#224;y`. Đã dính hai lần (chữ dựng sẵn cho máy tìm kiếm 17/08, thư nhắc chăm
khách 18/08) nên hàm escape để hẳn cạnh chỗ dựng thư, có test khoá lại.

**Thêm loại thư mới thì KHÔNG phải sửa worker** — chỉ cần đặt đúng `title` + `bodyHtml`. Đó là điểm
của việc chuẩn hoá: mã mới không kéo theo mẫu mới. Muốn thư đẹp riêng thì mới khai mẫu trong
`dbo.MailTemplates` (mẫu DB luôn được ưu tiên hơn bản dựng sẵn trong code).

## Kênh gửi (`Channel`, TINYINT)

Từ 2026-08 hàng đợi này thành đa kênh (email/Telegram/Zalo) cho bản tin sáng, không chỉ email. Cột
`Channel` cho worker biết dòng đó gửi bằng đường nào — enum
[`OutboundChannel`](../../TourkitAiProxy.Domain/Digest/OutboundChannel.cs), **worker toutkit-app phải MIRROR đúng
bảng số** này:

| Số | Tên | Nơi nhận + nội dung nằm ở | Adapter bên worker |
|---|---|---|---|
| 0 | `Email` (default) | `ToEmail` + `Params` (mẫu HTML) | `EmailChannelSender` |
| 1 | `Telegram` | `Data`: `{chatId, title, body}` | `TelegramChannelSender` |
| 2 | `Zalo` (ZNS) | `Data`: `{phone, title, body, templateId}` — SỐ ĐIỆN THOẠI | `ZaloZnsSender` |

Dòng cũ (trước khi có cột `Channel`) tự mang giá trị mặc định `0` (email) — không cần migrate data.

### Zalo: OA riêng từng công ty + mẫu theo chức năng (17/08)

Bản trước gửi ZNS bằng **OA chung** khai ở config worker. Đổi lại thành **OA riêng từng công ty**:
tin ZNS hiện tên OA người gửi, nên gửi bằng OA của bên cung cấp dịch vụ thì khách của công ty A nhận
tin mang tên công ty B. Đi gặp khách hàng 17/08: không bên nào chấp nhận.

Worker cần đọc thêm:

- **Tài khoản OA**: `dbo.TenantChannelSettings` với `TenantId = <tenant của dòng hàng đợi>` và
  `Channel = 'zalo'`. `ConfigJson`:
  ```json
  { "mode": "own|provided",
    "oaId": "...", "appId": "...",
    "secretKeyEnc": "<Crypton>",
    "refreshTokenSeedEnc": "<Crypton>",     // hạt giống: quản trị dán 1 lần
    "templates": { "sale-brief": "...", "ceo-brief": "...", "payment-alert": "..." },

    "accessTokenEnc": "<Crypton>",          // ─┐ CỦA WORKER
    "refreshTokenEnc": "<Crypton>",         //  │ xoay vòng mỗi lần làm mới
    "refreshedUtc": "2026-08-17T02:00:00Z" } // ─┘
  ```
  **`mode` KHÔNG đổi cách gửi** — nó chỉ nói OA đó của ai, để giao diện hướng dẫn đúng và để biết
  công ty đang gửi dưới tên nào. Cả hai chế độ khai cùng một bộ `oaId/appId/secretKey/refreshTokenSeed`:
  - `own` — công ty tự đăng ký OA, tự lấy 4 giá trị đó.
  - `provided` — công ty **không phải đi đăng ký OA**; bên cung cấp dịch vụ đưa sẵn 4 giá trị của OA
    hệ thống để họ dán vào đúng những ô đó (kèm mã mẫu đã đăng ký dưới OA ấy).

  Thiếu cấu hình → `Status=4` (không thử lại) kèm lý do "chưa khai OA Zalo", **KHÔNG** mượn OA khác
  gửi thay.

  ⚠️ **`refreshTokenSeedEnc` là bắt buộc với `mode=own`.** App ID + Secret KHÔNG đủ để lấy token:
  Zalo cấp access token bằng cách đổi refresh token, mà refresh token đầu tiên chỉ có sau bước cấp
  quyền OA trên trang Zalo. Worker chỉ đọc hạt giống khi `refreshTokenEnc` còn trống; từ lần làm
  mới đầu tiên trở đi DB là nguồn đúng.

- **Mã mẫu**: đọc `Data.templateId` **trên chính dòng hàng đợi** — proxy đã tra sẵn theo đúng loại
  bản tin lúc dựng nội dung. Trống (dòng cũ do bản proxy trước xếp vào) → tra lại theo
  `Kind`/`Data.briefType`; vẫn không có → `Status=4` + lý do, **đừng lấy mẫu của chức năng khác**.

⚠️ **Ba khoá token là của WORKER, phần còn lại là của proxy — CẢ HAI BÊN ĐỀU GHI HỢP NHẤT.** Đọc
`ConfigJson` hiện có → sửa đúng khoá của mình → ghi lại. Bên nào ghi đè cả cục là xoá mất phần của
bên kia, và hỏng hóc chỉ lộ ra ở lần gửi sau đó chứ không báo gì lúc lưu. Bên worker:
[`TenantZaloConfigStore`](../../../toutkit-app/PushNotification.Worker/Channels/TenantZaloConfigStore.cs);
bên proxy: [`TenantChannelSettingsStore`](../../TourkitAiProxy.Infrastructure/Digest/TenantChannelSettingsStore.cs).

⚠️ **`ZaloTokenStore` cũ (OA dùng chung, `TenantId='(system)'`, `Channel='zalo-zns'`) vẫn còn trong
worker nhưng KHÔNG lớp nào gọi tới** — giữ lại cho chế độ `provided` khi bên cung cấp mở đường đó.
Đừng nhầm hai lớp: `zalo` là cấu hình per-tenant, `zalo-zns` là token của OA hệ thống.

⚠️ **MỘT hàng đợi, MỘT nơi tiêu thụ:** `TourKit.PushWorker` rút **cả 3 kênh** và KHÔNG lọc `Channel`;
proxy chỉ XẾP vào, không rút. Đừng thêm bộ rút thứ hai ở bất kỳ đâu — hai tiến trình cùng poll thì cái
nhanh hơn nuốt mất dòng của cái kia (đã dính đúng lỗi này 14/08: worker mail vớ dòng telegram → ghi
`Status=4` "thiếu email người nhận" → bộ rút proxy chỉ tìm `Status=0` nên không bao giờ thấy nữa).

⚠️ **BẮT BUỘC deploy worker (bản có adapter kênh) TRƯỚC khi proxy bật `Features:Digest`** — worker cũ
không biết cột `Channel`, sẽ vớ dòng telegram/zalo rồi đánh dấu "thiếu email người nhận" là mất tin.

Thêm kênh mới = 1 member enum ở **cả hai repo** + 1 lớp `IOutboundChannelSender` bên worker + 1 dòng DI.
KHÔNG đụng vòng lặp, KHÔNG đụng lớp của kênh cũ.

## Hợp đồng worker (tóm tắt)

1. Poll: `SELECT TOP N * FROM dbo.OutboundMails WHERE Status=0 AND RetryCount < @max AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME()) ORDER BY Id` — **giờ UTC** (so sánh bằng `DateTime.UtcNow`). **KHÔNG lọc `Channel`**: worker là nơi tiêu thụ duy nhất, lấy hết rồi giao cho adapter của từng kênh. Lọc ở đây sẽ để lại dòng không ai rút, nằm mãi `Status=0` mà chẳng có lỗi nào nổi lên.
2. Render: load template theo `TemplateCode` → replace `{{key}}` từ `[Params]`. `Subject` lấy từ template hoặc cột `Subject`.
3. Người nhận:
   - `Kind='deal-cooling-alert'`: đọc `Data.dealId` → tenant DB `BookingTickets.NguoiPhuTrachs` → `Users.email` (1 deal nhiều NV → gửi nhiều / Cc).
   - `Kind='daily-brief'`: dùng thẳng `ToEmail` (người dùng tự khai) — KHÔNG tra CRM.
4. Gửi xong → `Status=1 (Sent)`, `ProcessedUtc=SYSUTCDATETIME()`. Lỗi → `Status=2 (Failed)`, `ErrorMessage`, `RetryCount++`.
5. Cancel khi deal hết nguội (tùy chọn, phase sau) → `Status=3 (Cancelled)` theo `SourceId`.

**Status (TINYINT):** `0=Pending 1=Sent 2=Failed 3=Cancelled 4=Skipped`.
