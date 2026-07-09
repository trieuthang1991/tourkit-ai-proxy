# Hướng dẫn Tự động hóa (TourKit AI)

Tài liệu cho **người dùng & quản trị**: cách bật và dùng các tác vụ AI chạy tự động theo lịch
(review khách hàng, review cơ hội bán hàng, đồng bộ hộp thư), cảnh báo deal nguội qua email,
và cách vận hành worker gửi mail.

> Mục lục
> 1. [Tổng quan](#1-tổng-quan)
> 2. [Tài khoản tự động (bắt buộc)](#2-tài-khoản-tự-động-bắt-buộc-cho-workflow-toàn-công-ty)
> 3. [Các workflow có sẵn](#3-các-workflow-có-sẵn)
> 4. [Bật/tắt – đặt lịch – chạy ngay – lịch sử](#4-bậttắt--đặt-lịch--chạy-ngay--xem-lịch-sử)
> 5. [Cảnh báo deal nguội qua email](#5-cảnh-báo-deal-nguội-qua-email)
> 6. [Trang quản trị: Hàng đợi mail & Template mail](#6-trang-quản-trị-hàng-đợi-mail--template-mail)
> 7. [Worker gửi mail (deploy)](#7-worker-gửi-mail-deploy)
> 8. [Xử lý sự cố thường gặp](#8-xử-lý-sự-cố-thường-gặp)

---

## 1. Tổng quan

**Tự động hóa** là các tác vụ AI tự chạy nền theo chu kỳ (vài phút/giờ một lần), không cần ai bấm nút.

- Vào trang **"Tự động hóa"** (menu nhóm *Tích hợp*, đường dẫn `/workflows`).
- Mỗi loại tự động hóa là một **thẻ (card)** riêng: có công tắc Bật/Tắt, chọn chu kỳ, nút *Chạy ngay*, và xem lịch sử chạy.
- Cấu hình lưu theo **từng công ty (tenant)** hoặc **từng nhân viên (user)** tùy loại.

Có 2 phạm vi:
- **Theo công ty (PerTenant)** — chạy 1 lần cho cả công ty, cần **Tài khoản tự động** (xem mục 2). VD: review deal, review khách hàng.
- **Theo nhân viên (PerUser)** — gắn với hộp thư/tài khoản của từng người. VD: đồng bộ Gmail.

---

## 2. Tài khoản tự động (bắt buộc cho workflow toàn công ty)

Workflow chạy nền **không có ai đăng nhập sẵn**, nên cần một **tài khoản dịch vụ** để hệ thống tự đăng nhập TourKit thay bạn.

**Cách cấu hình** (trong thẻ workflow ví dụ *Tự động review deal*):
1. Nhập **Tài khoản / Mật khẩu** TourKit của một user (nên là tài khoản chuyên dụng cho automation).
2. Bấm **Lưu**. Hệ thống sẽ **đăng nhập thử + đếm số deal nhìn thấy** trước khi lưu:
   - Thành công → lưu (mật khẩu được **mã hóa**, không hiển thị lại).
   - Sai mật khẩu → báo lỗi, không lưu.

**Lưu ý quyền hạn:** tài khoản này nên có quyền **xem toàn bộ** (vd `CH_XEM_ALL` cho deal) — vì phạm vi nó thấy quyết định phạm vi quét. Nếu chỉ thấy deal của riêng nó thì automation cũng chỉ xử lý bấy nhiêu.

**Xóa tài khoản tự động** → workflow toàn-công-ty sẽ ngừng tự đăng nhập (tạm dừng).

---

## 3. Các workflow có sẵn

### 3.1. `mail-auto-sync` — Tự đồng bộ hộp thư (theo nhân viên)
- Mỗi N phút: kéo Gmail mới về, **AI phân loại** 6 nhóm, (tùy chọn) **tự soạn & gửi trả lời**.
- Cần cấu hình hộp thư Gmail (địa chỉ + App Password) ở trang **Hộp thư AI**.
- Tùy chọn: bật/tắt auto-reply, chọn nhóm thư được auto-reply, giọng văn trả lời.

### 3.2. `deal-auto-review` — Tự review cơ hội bán hàng + cảnh báo deal nguội (toàn công ty)
Mỗi chu kỳ làm 3 việc:
- **Chấm deal mới**: deal chưa được AI chấm, trong cửa sổ `createdWithinDays`, đúng `statuses` → AI cho điểm (xác suất chốt + gợi ý hành động).
- **Review lại deal đã chấm**: khi nội dung deal thay đổi (đổi người phụ trách, có comment mới…) → chấm lại. Có giới hạn `maxAutoReviews` để không review vô tận. Deal **đã chốt / đã hủy** → tự **bỏ qua** (không review lại, không nhắc).
- **Cảnh báo deal nguội**: deal đang mở mà **không có hoạt động ≥ `coolingDays` ngày** → gửi email nhắc nhân viên phụ trách (xem mục 5).

**Tùy chọn (Options):**
| Khóa | Ý nghĩa |
|---|---|
| `statuses[]` | Chỉ xử lý các trạng thái này (rỗng = mọi trạng thái mở) |
| `createdWithinDays` | Chỉ xét deal tạo trong N ngày gần đây |
| `autoReview` | Bật/tắt việc chấm điểm tự động |
| `reviewMax` | Số deal tối đa chấm mỗi lần chạy |
| `maxAutoReviews` | Số lần tối đa tự chấm lại 1 deal |
| `coolingDays` | Số ngày không hoạt động thì coi là "nguội" |
| `minWinRateToNotify` | Chỉ cảnh báo deal có xác suất chốt ≥ ngưỡng này |
| `maxNotifications` | Số lần cảnh báo tối đa cho 1 deal |
| `notifyMinGapHours` | Khoảng cách tối thiểu giữa 2 lần cảnh báo cùng 1 deal |

> **Ghi chú:** throttle cảnh báo tính **theo deal** (không theo người) — cố ý, để khi đổi người phụ trách không bị spam lại. Deal **chưa giao nhân viên** thì bỏ qua cảnh báo.

### 3.3. `customer-auto-review` — Tự chấm hạng khách hàng (toàn công ty)
- **Lượt 1**: khách hàng chưa được review (tạo trong `createdWithinDays`) → AI chấm hạng A–D + gợi ý hành động.
- **Lượt 2**: khách đã review nhưng quá `reReviewDays` ngày **và** hồ sơ có thay đổi → review lại (so "vân tay" dữ liệu, không đổi thì bỏ qua để khỏi tốn AI).
- Giới hạn ~200 khách/lượt để không quá lâu; hết thì chu kỳ sau quét tiếp.

**Tùy chọn:** `createdWithinDays`, `reReviewDays`, `reviewMax`.

---

## 4. Bật/tắt – đặt lịch – chạy ngay – xem lịch sử

Trong mỗi thẻ workflow ở trang **Tự động hóa**:
- **Công tắc Bật/Tắt** — bật thì hệ thống tự chạy theo chu kỳ.
- **Chu kỳ (interval)** — chọn số phút giữa 2 lần chạy.
- **Chạy ngay** — chạy thử 1 lần ngay lập tức, trả kết quả tóm tắt.
- **Lịch sử chạy** — xem các lần chạy gần đây: thời điểm, trạng thái (ok/lỗi), tóm tắt (đã chấm bao nhiêu, bỏ qua bao nhiêu…), lỗi nếu có.

**Tự tạm dừng:** nếu một workflow **lỗi 5 lần liên tiếp**, hệ thống tự **tạm dừng** nó (tránh chạy hỏng liên tục). Bạn xử lý nguyên nhân rồi bấm **Bật lại** để tiếp tục.

---

## 5. Cảnh báo deal nguội qua email

Đây là phần email tự động của `deal-auto-review`. Luồng:

```
deal-auto-review phát hiện deal nguội
   → ĐƯA VÀO HÀNG ĐỢI mail (dbo.OutboundMails), trạng thái "Chờ gửi"
   → WORKER gửi mail đọc hàng đợi → dựng nội dung từ TEMPLATE → gửi qua nhà cung cấp (SendGrid/SES/Mailgun)
   → cập nhật trạng thái: Đã gửi / Lỗi / Bỏ qua
```

- **Người nhận** = (các) nhân viên phụ trách deal. Nhiều người → người đầu là **To**, còn lại **Cc**.
- **Nội dung** không soạn cứng — lấy từ **Template mail** (sửa được trong admin, xem mục 6), điền tham số như tên khách, giá trị deal, số ngày nguội, gợi ý hành động…

---

## 6. Trang quản trị: Hàng đợi mail & Template mail

Vào hệ quản trị `/admin-trav-ai/` (đăng nhập admin).

### 6.1. 📤 Hàng đợi mail
Theo dõi **mọi email tự động** của tất cả công ty:
- Lọc theo **trạng thái** (Chờ gửi / Đã gửi / Lỗi / Đã hủy / Bỏ qua), theo **tenant**, theo **loại**.
- Mỗi dòng hiện: người nhận, tiêu đề (render sẵn để xem trước), trạng thái, số lần thử, lỗi (nếu có), thời gian.
- **Bấm vào 1 dòng** → mở chi tiết + **xem trước email** đúng như khách sẽ nhận.

Ý nghĩa trạng thái:
| Trạng thái | Nghĩa |
|---|---|
| **Chờ gửi** | Đang xếp hàng, worker chưa xử lý |
| **Đã gửi** | Gửi thành công |
| **Lỗi** | Gửi thất bại sau số lần thử (xem cột lỗi) |
| **Bỏ qua** | Không có email người nhận → không gửi |
| **Đã hủy** | Bị hủy (vd deal hết nguội trước khi gửi) |

### 6.2. 📝 Template mail
Quản lý **nội dung email** dùng chung (không cần lập trình viên):
- **Sửa** tiêu đề + nội dung bằng **trình soạn trực quan (WYSIWYG)**; nút **`<> Code`** để chỉnh HTML thô khi cần.
- **Xem trước** email theo dữ liệu mẫu ngay bên cạnh.
- **Bật/Tắt** template (tắt → worker dùng nội dung mặc định trong code).
- Tạo template mới / Xóa.

**Cú pháp tham số trong template:**
- `{{tenKhoa}}` — chèn giá trị (vd `{{customerName}}`, `{{coolingDays}}`).
- `{{#if tenKhoa}}…{{/if}}` — chỉ hiện đoạn bên trong nếu tham số có giá trị.

Template có sẵn: **`deal-cooling-alert`** (cảnh báo deal nguội). Các tham số nó nhận: `customerName`, `phone`, `title`,
`totalPriceFormatted`, `statusName`, `sourceName`, `assigneeNames`, `coolingDays`, `lastInteractionAt`,
`winRate`, `level`, `nextAction`, `dealCode`.

---

## 7. Worker gửi mail (deploy)

Email tự động **không gửi bởi web** — một **worker nền** (Windows Service) đọc hàng đợi rồi gửi.

### 7.1. Chọn nhà cung cấp (`OutboundMail:Provider`)
| Provider | Giá trị `Provider` | Cách gửi |
|---|---|---|
| SendGrid | `sendgrid` | HTTP API (443) |
| Amazon SES | `ses` | HTTP API (443) |
| Mailgun / SMTP relay | `smtp` *(hoặc `mailgun`)* | SMTP STARTTLS (587) |
| Mailgun HTTP API | `mailgun-http` | HTTP API (443) — **né được mạng chặn SMTP** |

### 7.2. Cấu hình `appsettings.json` của worker (ví dụ Mailgun HTTP API)
```jsonc
"OutboundMail": {
  "Enabled": true,
  "Provider": "mailgun-http",
  "IntervalSeconds": 30,
  "BatchSize": 50,
  "MaxRetries": 3,
  "FromEmail": "no-reply@mistudio.asia",   // phải thuộc domain đã verify ở nhà cung cấp
  "FromName": "TourKit",
  "Mailgun": {
    "Domain": "mistudio.asia",
    "BaseUrl": "https://api.mailgun.net",   // EU: https://api.eu.mailgun.net
    "ApiKey": "ENC:..."                      // API key THẬT (không phải mật khẩu SMTP); nên mã hóa ENC:
  }
}
```
Ví dụ dùng **Mailgun SMTP** thay vì HTTP:
```jsonc
"OutboundMail": {
  "Provider": "smtp",
  "FromEmail": "no-reply@mistudio.asia",
  "Smtp": { "Host": "smtp.mailgun.org", "Port": 587, "Username": "no-reply@mistudio.asia", "Password": "ENC:...", "UseStartTls": true }
}
```
> Giá trị bí mật (ApiKey/Password) nên ở dạng **`ENC:`** — worker tự giải mã (cùng cơ chế mã hóa với hệ thống). `ConnectionStrings:PushDb` phải trỏ đúng DB chứa hàng đợi.

### 7.3. Publish & cài đặt
```powershell
# 1) Publish
dotnet publish PushNotification.Worker\PushNotification.Worker.csproj -c Release -o dist\worker

# 2) Sửa dist\worker\appsettings.json (PushDb + OutboundMail như trên)

# 3) Cài Windows Service (PowerShell Administrator, trong thư mục dist\worker)
.\install-worker.ps1 install -ServiceName "TourKit.OutboundMailWorker" -Instance mail `
  -DisplayName "TourKit - Outbound Mail" -Description "Gui mail tu dong"

# Quản lý:
.\install-worker.ps1 status  -ServiceName "TourKit.OutboundMailWorker"
.\install-worker.ps1 stop    -ServiceName "TourKit.OutboundMailWorker"
.\install-worker.ps1 uninstall -ServiceName "TourKit.OutboundMailWorker"
```
- `-Instance mail` → chỉ chạy worker gửi mail (cần khai báo `Instances:mail` trong appsettings).
- Test nhanh không cài service: chạy `\.PushNotification.Worker.exe --instance mail` trong cửa sổ lệnh.

### 7.4. Mã hóa giá trị bí mật (ENC:)
ApiKey/Password nên lưu mã hóa. Liên hệ đội kỹ thuật để mã hóa chuỗi `ENC:` (dùng cùng thuật toán với cấu hình hệ thống); **không** dán key thật ở dạng plaintext lên server dùng chung.

---

## 8. Xử lý sự cố thường gặp

| Hiện tượng | Nguyên nhân & cách xử lý |
|---|---|
| Mail trạng thái **Bỏ qua** | Dòng đó **không có email người nhận** (chưa resolve được email NV phụ trách). Kiểm tra deal đã giao NV và NV có email chưa. |
| Lỗi **"Maximum credits exceeded"** (SendGrid) | Tài khoản SendGrid **hết credit** → nâng gói hoặc đổi nhà cung cấp (Mailgun). |
| Lỗi **"Failure sending mail / net_io_connectionclosed"** (SMTP) | Máy chạy worker **bị chặn gửi SMTP ra ngoài (cổng 587)**. → Chạy worker trên **server** cho phép SMTP, hoặc đổi sang `Provider="mailgun-http"` (cổng 443). |
| Lỗi **401** khi gọi Mailgun HTTP API | Đang dùng **mật khẩu SMTP** thay vì **API key** thật. Lấy API key ở Mailgun → *Settings → API keys*. Kiểm tra đúng **vùng** (US/EU `BaseUrl`). |
| Mail không bị **gửi lại** sau khi sửa lỗi | Dòng đã **hết lượt thử** (Status = Lỗi). Đưa lại về *Chờ gửi* (reset `Status=0, RetryCount=0`) hoặc tạo cảnh báo mới. |
| Workflow **tự tạm dừng** | Lỗi 5 lần liên tiếp → xem lịch sử chạy tìm nguyên nhân, sửa rồi **Bật lại**. |
| `From` bị từ chối / vào spam | `FromEmail` phải thuộc **domain đã verify** (SPF/DKIM) ở nhà cung cấp. |

---

*Cập nhật: 2026-06-29. Liên quan: [docs/database-schema.md](database-schema.md) (bảng `dbo.OutboundMails`, `dbo.MailTemplates`, `dbo.UserWorkflows`, `dbo.TenantServiceAccounts`).*
