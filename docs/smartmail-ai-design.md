# SmartMail AI — Thiết kế MVP

> Tính năng thứ 4 của tourkit-ai-proxy: Hộp thư + đồng bộ Gmail + phân loại AI + soạn trả lời theo ngữ điệu.
> Trạng thái: **đã brainstorm xong, chờ duyệt để lên plan.** Chưa code.

## Phạm vi đã chốt (brainstorm)

| Quyết định | Chốt | Lý do |
|---|---|---|
| Nguồn mail | **Chỉ Gmail, qua IMAP/SMTP (MailKit)** | Né thủ tục duyệt OAuth của Google; app-password tạo trong 1 phút; làm được ngay |
| Auth Gmail | **App Password** (cần bật 2-Step Verification) | Google đã bỏ "less secure apps"; đây là cách duy nhất cho IMAP |
| Hộp thư | **1 hộp thư chung công ty** (vd `booking@congty.com`) | Per-staff sẽ cần mỗi người 1 app-password → quá phiền |
| Đồng bộ | **Bấm Refresh kéo về (on-demand)**, 1 chiều | Mockup đã có nút Refresh → không cần background poller; ghi ngược trạng thái để sau |
| Trả lời | **Phase 1: chỉ soạn nháp** (người tự gửi); Phase 2: tự gửi SMTP | Né hoàn toàn SMTP + spam/SPF/DKIM ở MVP |
| Lưu trữ | **File-backed `data/mails.json`** | Đúng nếp `reviews.json` — lên DB sau |
| Phân loại AI | **Chỉ phân loại email MỚI**, cache theo fingerprint | Tiết kiệm token; Refresh lần sau không gọi lại AI |

## Kiến trúc (khớp folder-by-feature hiện có)

### Backend

```
Services/Mail/
  IMailSource.cs            ← interface nguồn mail (để sau cắm OAuth không phải đập lại)
  GmailImapClient.cs        ← MailKit: IMAP fetch N thư mới nhất (imap.gmail.com:993).
                              SMTP gửi (smtp.gmail.com:587) — phase 2.
  MailAccountStore.cs       ← đọc creds hộp thư (config/env hoặc data/mail-account.json gitignored)
  MailRepository.cs         ← file-backed data/mails.json, lock-guarded (mẫu ReviewRepository)
  MailClassifier.cs         ← prompt → ProviderRegistry.CompleteAsync → JSON {category, summary}.
                              Tolerant parse (LooseJson). Cache theo fingerprint (mẫu ReviewService)
  MailReplyService.cs       ← soạn nháp: tone + chỉ thị NV + email gốc → CompleteAsync (stream được)
Endpoints/MailEndpoints.cs
Models/MailModels.cs
data/mails.json             ← runtime state (gitignore)
data/mail-account.json      ← creds hộp thư, mã hóa Crypton (gitignore) — hoặc dùng appsettings
```

### API surface (versioned, RESTful — khớp nếp `/api/v1/*`)

| Method | Path | Ghi chú |
|---|---|---|
| POST | `/api/v1/mail/sync` | IMAP kéo N thư mới nhất → phân loại email mới → lưu → trả danh sách (nút Refresh) |
| GET | `/api/v1/mail` | List + filter: `folder` (all/mine/unread), `status`, `category`, `search` — kèm counts cho sidebar |
| GET | `/api/v1/mail/{id}` | 1 email + phân loại + nháp (nếu có) |
| POST | `/api/v1/mail/{id}/reply/draft` | body `{tone, instruction}` → AI soạn nháp (SSE stream giống `/completions/stream`) |
| PATCH | `/api/v1/mail/{id}/status` | đổi trạng thái (moi/dang_xu_ly/da_phan_hoi/da_dong) |
| POST | `/api/v1/mail/{id}/reply/send` | **Phase 2**: gửi qua SMTP |

### Data model (camelCase — khớp frontend JS)

```jsonc
// MailItem
{
  "id": "<gmail UID / messageId>",
  "from": { "name": "minh.tran", "email": "minh.tran@gmail.com" },
  "subject": "Đặt vé combo Phú Quốc",
  "body": "Cứu! Mình cần 2 combo...",
  "receivedAt": "2026-06-05T08:30:00Z",
  "isRead": false,
  "owner": null,                       // "của tôi" = gán nhân viên (phase sau)
  "category": "hoi_dat_tour",          // 6 loại bên dưới
  "status": "moi",                     // moi | dang_xu_ly | da_phan_hoi | da_dong
  "aiSummary": "Khách cần 2 combo Phú Quốc gấp...",
  "classificationFingerprint": "<sha256 32 hex của body>",
  "draft": { "tone": "lich_su", "instruction": "giảm 5%", "text": "...", "generatedAt": "..." }
}
```

- **6 loại (category):** `hoi_dat_tour`, `xin_bao_gia`, `khieu_nai`, `xac_nhan`, `spam`, `khac`
- **4 trạng thái (status):** `moi`, `dang_xu_ly`, `da_phan_hoi`, `da_dong`
- **4 ngữ điệu (tone):** `lich_su` (Lịch sự, trang trọng), `than_thien` (Thân thiện, cởi mở), `dam_phan` (Đàm phán thương lượng), `xin_loi` (Lời xin lỗi chuyên biệt)

### Frontend

```
wwwroot/pages/mail.jsx        ← window.MailPage. 3 cột:
                                 TRÁI = filter (Hộp thư/Trạng thái/Phân loại AI + counts)
                                 GIỮA = list email + search + nút Refresh (→ POST /mail/sync)
                                 PHẢI = chi tiết email + panel soạn AI (tone chips + ô chỉ thị NV
                                        + "Soạn câu trả lời cùng AI" → stream nháp)
```
- Thêm `<script src="pages/mail.jsx">` vào `index.html`
- `app.jsx`: thêm `<Route path="/mail">` + nav "Hộp thư"
- Tái dùng `window.tourkit.ai` cho phần soạn nháp (hoặc gọi thẳng `/mail/{id}/reply/draft`)

## Tiết kiệm token (quan trọng)
- Phân loại **chỉ email mới** (chưa có fingerprint khớp). Refresh lại không gọi AI.
- Lần sync đầu nhiều thư → phân loại **song song có giới hạn** (mẫu `BatchService`, cap ~5–10).
- Không cache kết quả rỗng (mẫu `ChatCache.HasContent`).

## Hoãn lại (YAGNI cho MVP)
- Tự gửi SMTP (phase 2) · Đồng bộ 2 chiều (ghi `\Seen` ngược) · Nguồn OAuth · Poller real-time ·
  Gộp luồng hội thoại (threading) · Đính kèm (attachments) · Gán email cho nhân viên ("của tôi")

## NuGet mới
- `MailKit` (IMAP + SMTP cho .NET)

## Việc anh cần chuẩn bị ngoài đời
1. Bật **2-Step Verification** cho hộp thư Gmail công ty.
2. Tạo **App Password** (16 ký tự) → đưa vào `appsettings.json` (`Mail:Gmail:Address` + `Mail:Gmail:AppPassword`) hoặc nhập qua UI sau.
3. Bật **IMAP** trong Gmail Settings → Forwarding and POP/IMAP.
