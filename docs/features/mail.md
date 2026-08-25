# Hộp thư AI (SmartMail)

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

---

## SmartMail AI feature ("Hộp thư AI")

Gmail inbox synced on demand, AI-classified, with AI-drafted replies. Flow lives in `MailEndpoints` → `Services/Mail/*`. Design doc: `docs/smartmail-ai-design.md`; implementation plan: `docs/superpowers/plans/2026-06-05-smartmail-ai.md`.

- **Source = Gmail IMAP via MailKit, NOT OAuth.** `GmailImapClient` (implements `IMailSource`) connects `imap.gmail.com:993` read-only with an **App Password** (requires Gmail 2-Step Verification + IMAP enabled). The interface keeps OAuth swappable later. Creds resolved by `MailAccountStore`: DB-backed `dbo.MailAccounts` per-tenant (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI per tenant. KHÔNG còn fallback config/env (đã drop từ commit multi-tenant fix 2026-06-09).
- **Sync is on-demand (Refresh button), not a background poller.** `POST /mail/sync` is **incremental theo UID** (`MailSyncStore` lưu `dbo.MailSyncState` per-tenant per-address `{uidValidity, lastUid}`): chỉ kéo email có UID > lần trước → KHÔNG sót dù >N email mới giữa 2 lần sync. Lần đầu/khi UidValidity đổi → kéo `max` (30) mới nhất. Cờ `\Seen` của Gmail map sang `IsRead` lúc kéo. Vẫn **classify chỉ email MỚI** (`repo.Has(id)` skip → tiết kiệm token). Email id = Message-Id (MimeKit chuẩn hóa/tự sinh), fallback `{address}:{uid}`.
- **Đọc/chưa đọc:** `POST /mail/{id}/read` đánh dấu đã đọc khi mở; `MailCounts.Unread` cho badge. Frontend in đậm + chấm cam dòng chưa đọc.
- **Thư CHUYỂN TIẾP DẠNG ĐÍNH KÈM** (sửa 14/08): `msg.HtmlBody`/`msg.TextBody` của MimeKit chỉ trả
  phần VỎ ngoài. Gmail bấm "Chuyển tiếp" thì chèn nội tuyến (không sao), nhưng Outlook + nhiều app
  doanh nghiệp đính kèm thư gốc dạng `message/rfc822` → vỏ rỗng → **mở lên trắng trơn**. `MailMapper`
  nay duyệt đệ quy `MessagePart` (chặn 5 lớp / 10 thư) và ghép nội dung bên trong kèm dòng phân cách.
  ⚠️ Chỉ áp dụng LÚC BÓC thư — thư cũ đã lưu Body/BodyHtml theo bản cũ, `reclassify` KHÔNG chữa được
  (nó chỉ chạy lại AI trên body đã lưu). Chữa bằng `POST /api/v1/mail/refresh-content` (dưới).
- **Đính kèm: BÁO TÊN, chưa tải được file.** `MailMapper` ghép dòng `📎 Tệp đính kèm: …` vào CHÍNH
  thân thư (cả text lẫn HTML) — cố ý KHÔNG thêm cột vào `dbo.Mails` (bảng cũ). Gom cả tệp của thư
  lồng bên trong, vì thư chuyển tiếp thường đính kèm ở lớp trong. **Bỏ qua phần `inline`** (logo chữ
  ký, ảnh `cid:`) — liệt kê cả logo thì gần như mọi email công ty đều hiện "image001.png", nhiễu tới
  mức lúc có tệp thật không ai để ý. Tên tệp do người ngoài đặt nên **phải escape** trước khi nhét
  vào HTML. Tải/mở file vẫn là Phase 2.
- **Phân loại: định nghĩa + luật gỡ hoà, KHÔNG chỉ tên nhóm** (sửa 14/08). Prompt cũ liệt kê trần
  `- spam: Spam` → thư máy-gửi (không phải khách, cũng không phải quảng cáo) kẹt giữa `spam`/`khac`,
  mỗi lần chọn một kiểu: soát 1.215 thư thật thấy `Thông báo có công việc mới được giao` rải **143
  `spam` / 52 `khac` / 41 `xac_nhan`**. Nay `MailTaxonomy.CategoryHints` + `MailTaxonomy.TieBreakRules`
  là 1 nguồn, nhúng vào prompt; `Temperature` = **0**. ⚠️ **Luật phải phân biệt theo MỤC ĐÍCH thư, KHÔNG
  theo người gửi** — bản đầu viết "máy gửi từ dịch vụ đang dùng → không bao giờ spam" thì **quảng cáo
  Grab cũng thoát khỏi spam**, tức là nhóm `spam` rỗng dần trong im lặng. Sửa lớp này thì phải đo **cả
  hai chiều** (thông báo rời spam VÀ quảng cáo ở lại spam), đo một chiều sẽ kết luận sai.
- **`POST /mail/refresh-content`** — kéo lại N thư mới nhất từ IMAP (`ignoreCursor: true` → bỏ mốc
  UID) và **ghi đè Body/BodyHtml** cho thư ĐÃ có. Cần vì phần bóc thư chỉ chạy lúc kéo về, nên mọi bản
  sửa `MailMapper` đều không tự áp dụng cho thư đang nằm trong hộp — mà đó đúng là thư người dùng đang
  nhìn. `MailSyncService.MergeForContentRefresh` (pure, có test) **giữ nguyên** `Category`/`AiSummary`/
  `Status`/`Draft`/`IsRead`/`AutoReplyError`: đè chúng đi là nhân viên mất nháp viết dở và thư đang xử
  lý bị đẩy về "mới" — tệ hơn cái đang định chữa. KHÔNG gọi AI → **0 lượt**. Chạy thật trên staging:
  30 thư, nhóm/trạng thái không đổi một dòng nào.
- **`POST /mail/{id}/reclassify`** — phân loại chỉ chạy MỘT LẦN lúc kéo thư về, nên sửa classifier xong
  thư cũ vẫn giữ nhãn sai vĩnh viễn. Endpoint này chạy lại cho 1 thư, **giữ nguyên** `Status`/`Draft`/
  `IsRead` (đẩy thư đang xử lý về "mới" là mất việc đang làm dở). CỐ Ý không có bản chạy hàng loạt —
  mỗi thư tốn 1 lượt AI.
- **Soạn thư MỚI:** `POST /mail/compose/draft` (SSE, AI viết từ `brief`) + `/mail/compose/send` (gửi tới người nhận bất kỳ) — `MailReplyService.ComposeNewStreamAsync` + `IMailSender.SendAsync`. Chữ ký công ty (`MailAccountStore.Signature()`, cấu hình ở UI per-tenant, lưu trong `dbo.MailAccounts`) được dệt vào prompt soạn.
- **Classification + reply reuse `ProviderRegistry`.** `MailClassifier.ClassifyAsync` (buffered, dual-path — xem "Native function-calling" section: Anthropic → `submit_mail_classification` tool với Haiku; else → JSON-prompt) → `{category, summary}`; 6 categories normalized to a known set (lạ → `khac`); lỗi cả 2 path → `("khac", "")` để mail vẫn lưu. `MailReplyService.DraftStreamAsync` streams a tone-aware draft (4 tones) + staff instruction via `provider.StreamAsync`, saves the draft + flips status → `dang_xu_ly`. Both client AI prefs (`provider`/`model`/`apiKey`) flow through like the other features.
- **Sending = SMTP Gmail (`IMailSender`/`GmailSmtpClient`).** `POST /mail/{id}/reply/send` gửi nội dung (đã sửa) tới người gửi gốc qua `smtp.gmail.com:587` STARTTLS bằng chính App Password — gửi AS the company Gmail, nên KHÔNG dính SPF/DKIM/spam như giả mạo domain. Gắn `In-Reply-To`/`References` để vào đúng luồng. Gửi xong → lưu nội dung + status `da_phan_hoi`. Frontend confirm trước khi gửi.
- **Storage = SQL Server `dbo.Mails`** per-tenant scoped (`MailRepository`, composite PK `(TenantId, Id)`, index `IX_Mails_Tenant_Received` cho list/sort). Cross-tenant access trả null/404. KHÔNG fallback file — DB lỗi → 503.
- **Taxonomy** (`MailTaxonomy`, single source): categories `hoi_dat_tour|xin_bao_gia|khieu_nai|xac_nhan|spam|khac`, statuses `moi|dang_xu_ly|da_phan_hoi|da_dong`, tones `lich_su|than_thien|dam_phan|xin_loi` — all with Vietnamese labels.
- **Frontend:** `wwwroot/pages/mail.jsx` (route `/mail`), 3-column (filters / list / detail+compose). A built-in **config form** (`GET`/`POST /mail/account`) lets staff paste Gmail address + App Password to test without editing JSON. Draft uses the same SSE `{delta}`/`{done}` reader as `assistant.jsx`. Statuses/categories color-coded via CSS.
- **Phase 2 (deferred):** 2-way sync (write `\Seen` back / mirror deletes), incremental UID fetch (hiện kéo 30 mới nhất/lần), OAuth source, assign-to-staff ("Của tôi"), attachments.
- **Tests:** `TourkitAiProxy.Tests` (xUnit, project nằm trong thư mục con → main csproj `<Compile Remove="TourkitAiProxy.Tests/**" />`). Covers pure logic only: `MailTaxonomy`, `MailMapper`, `MailClassifier.ParseClassification`, `MailRepository`. Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`. IMAP/frontend verified manually. (This is the repo's first test project — the rest of the codebase still has none.)

