# Chat đa kênh — phương án đưa vào TourKit AI

**Ngày:** 20/08/2026 · **Trạng thái:** thiết kế, chưa code
**Tham khảo:** [ChatbotX](https://github.com/ChatbotXIO/ChatbotX) (MIT, 639★, 795 commit) — đọc để
lấy nghiệp vụ, **KHÔNG** chạy như phụ thuộc lúc vận hành.

---

## 1. Vấn đề

Hôm nay khách nhắn tới công ty du lịch bằng Zalo, Facebook, web, email — mỗi kênh một chỗ, không
ai thấy được bức tranh chung. Hệ hiện tại có bốn mặt chạm nhưng rời rạc và phần lớn **một chiều**:

| Kênh | Đang có | Thiếu |
|---|---|---|
| Web widget | AI trả lời theo dữ liệu CRM | Không chuyển được cho người thật |
| Email (Hộp thư AI) | Nhận + gửi + AI phân loại + soạn nháp | — |
| Zalo | **Chỉ gửi đi**, mẫu ZNS duyệt sẵn | Không nhận được tin khách nhắn |
| Telegram | **Chỉ gửi đi** (bản tin) | Không hai chiều |
| Facebook · Instagram · WhatsApp | Không có | Toàn bộ |

Thiếu cốt lõi không phải "ít kênh" mà là **không có một hộp thư chung** để thấy hết khách đang
nhắn từ đâu, ai đang trả lời, và tin nào chưa ai đụng tới.

---

## 2. Vì sao TỰ VIẾT chứ không dựng ChatbotX

Đã cân ba hướng: (A) dựng nguyên ChatbotX chạy song song · (B) tự viết adapter vào proxy hiện tại ·
(C) lai — ChatbotX làm đường ống, TourKit làm não. **Chọn B.**

**Lý do chính — phần khó không nằm ở đường truyền.** Nhận webhook và gọi API gửi tin, mỗi kênh
chừng vài trăm dòng; chính repo ChatbotX cho thấy thế. Phần khó là **hiểu khách hỏi gì và trả lời
bằng số liệu thật của công ty** — thứ TourKit đã có (trợ lý số liệu, chấm hạng khách, giá tour,
visa, hàng đợi hành động CRM) và ChatbotX không có.

**Lý do vận hành.** Dựng ChatbotX là thêm **Node 24 + PostgreSQL + Redis + BullMQ** cạnh hệ
.NET + SQL Server đang chạy: hai chỗ đăng nhập, hai mô hình tenant, hai chỗ deploy, hai chỗ hỏng.

**Lý do giấy phép.** Chỉ `apps/builder/src/enterprise` thuộc bản thương mại, nhưng bên trong đó có
đúng thứ công ty du lịch cần nhất:

```
inbox-teams · inbox-team-members   ← chia hội thoại cho từng nhân viên
platform-branding                  ← gắn thương hiệu riêng
audit-logs · billing · manage · platform-email-templates
```

Tức là lấy phần dễ (đường truyền) miễn phí, còn phần đắt (chia việc, thương hiệu) vẫn phải mua.

⚠️ **Nhắc cho người đọc sau:** hướng A vẫn đáng chọn nếu cần **nhiều kênh cùng lúc trong vài tuần**
và chấp nhận nuôi hai hệ. Quyết định ở đây gắn với bối cảnh hiện tại (một hệ .NET, đội nhỏ), không
phải chân lý.

---

## 3. Soát nghiệp vụ ChatbotX — 96 action, đánh dấu cái nào cần

Đây là phần quan trọng nhất của tài liệu: **liệt kê đủ để không sót**, rồi mới cắt.
Nguồn: `packages/flow-config/src/steps` (96 file) + `src/nodes` (13 loại) + `src/event`.

### 3.1 Gửi nội dung — 21 action

| Action | Đợt | Ghi chú |
|---|---|---|
| `send-text` | **1** | Nền tảng của mọi thứ |
| `send-image` · `send-file` | **1** | Khách hay gửi/nhận ảnh phòng, hộ chiếu, bảng giá |
| `typing` | **1** | Báo "đang soạn" — rẻ, tăng cảm giác có người thật |
| `send-quick-reply` · `button` | **1** | Rút ngắn hội thoại; Zalo và Messenger đều hỗ trợ |
| `send-card` · `send-carousel` | 2 | Khoe tour dạng thẻ — hợp du lịch, nhưng để sau |
| `send-video` · `send-audio` · `send-gif` | 2 | |
| `email` | — | Đã có (Hộp thư AI) |
| `open-website` · `landing-page` | 2 | |
| `send-messenger-message-template` | 2 | |
| `send-wa-message-template` · `whatsapp-flow` · `whatsapp-option-list` · `whatsapp-carousel-rules` · `wa-template-flow-token` · `wa-template-utils` | — | WhatsApp chưa nằm trong phạm vi |
| `tiktok-text-rules` | — | |

### 3.2 Hội thoại & hộp thư — 14 action

| Action | Đợt | Ghi chú |
|---|---|---|
| `assign-conversation` · `unassign-conversation` | **1** | Giao cho nhân viên. Đây là thứ ChatbotX tính tiền — mình tự viết |
| `auto-assign-conversation` | 2 | Chia vòng tròn / theo tải |
| `disable-bot` · `enable-bot` | **1** | Người thật vào thì bot phải câm. Xem luật ở §6 |
| `archive-conversation` · `unarchive-conversation` | **1** | Đóng việc |
| `follow-conversation` · `unfollow-conversation` | 2 | Theo dõi hội thoại không phải của mình |
| `disable-messenger-composer` · `enable-messenger-composer` | 2 | Khoá ô nhập của khách khi đang chạy luồng |
| `set-messenger-persona` | 2 | Gửi dưới danh nghĩa nhân viên cụ thể |
| `set-messenger-user-persistent-menu` | 2 | |
| `update-messenger-contact-data` | 2 | |

### 3.3 Danh bạ / CRM — 12 action

| Action | Đợt | Ghi chú |
|---|---|---|
| `add-contact-tag` · `remove-contact-tag` | **1** | Phân loại khách ngay trong hội thoại |
| `set-custom-field` · `clear-custom-field` | **1** | Ghi ngày đi, số khách, tuyến quan tâm |
| `add-contact-notes` · `add-notes` | **1** | Nhật ký chăm sóc |
| `get-user-data` | **1** | Lấy hồ sơ khách từ CRM để cá nhân hoá |
| `block-contact` · `delete-contact` | 2 | Chặn spam |
| `mark-email-verified` · `opt-in-email` · `opt-out-email` | 2 | Liên quan luật quảng cáo |

⚠️ **Chỗ dễ sai:** ChatbotX giữ danh bạ RIÊNG của nó. Mình thì đã có khách hàng trong CRM TourKit.
**Không tạo danh bạ thứ hai** — bảng liên hệ chat chỉ giữ *danh tính theo kênh* rồi trỏ về khách CRM
(xem §5). Làm ngược lại sẽ có hai nguồn sự thật về cùng một khách, và đến lúc lệch thì không biết tin cái nào.

### 3.4 Điều hướng luồng — 14 action

`condition` · `split-traffic` · `wait` · `start-flow` · `start-another-node` · `start-external-flow` ·
`start-external-node` · `step-action` · `questionnaires` · `coupon` · `generate-code` ·
`count-characters` · `format-date` · `get-data-from-json`

**Đợt 1 chỉ cần `condition` + `wait`.** Cả cụm còn lại là bộ máy của **trình vẽ luồng kéo-thả** —
xem §4 để biết vì sao cố ý chưa làm.

### 3.5 AI — 9 action

| Action | Đợt | Ghi chú |
|---|---|---|
| `ai-generate-text` · `ai-generate-text-agent` | **1** | Nối thẳng vào trợ lý sẵn có, KHÔNG viết mới |
| `ai-extract-data` | **1** | Bóc ngày đi / số khách / tuyến từ câu khách nhắn |
| `ai-speech-to-text` | 2 | Khách Zalo hay gửi tin thoại. Mình đã có STT |
| `ai-text-to-speech` | 2 | Đã có |
| `ai-analyze-image` | 2 | Đọc ảnh hộ chiếu → hồ sơ visa (nối phần Thẩm định Visa) |
| `ai-generate-image` · `ai-edit-image` | — | Không phục vụ nghiệp vụ tour |
| `ai-delete-message-history` | **1** | Xoá trí nhớ hội thoại khi khách đổi chủ đề |

### 3.6 Chiến dịch — 5 action

`subscribe-sequence` · `unsubscribe-sequence` · `subscribe-broadcast` · `unsubscribe-broadcast` ·
`follow-up` → **Đợt 3.** Đây là mảng marketing nhỏ giọt, khác hẳn chăm khách 1-1 và **đụng luật
quảng cáo**; làm ẩu là bị Zalo/Meta khoá OA.

### 3.7 Tích hợp ngoài — 21 action

`external-request` (**đợt 2** — móc mở cho mọi thứ khác) · `execute-javascript` (**không làm**: chạy
mã người dùng nhập cần hộp cát riêng) · `trigger-n8n` · `make` · 6 action Google Sheets ·
`appointment-scheduling` (**đợt 2** — mình đã có `create_appointment` qua hàng đợi CRM) ·
8 dịch vụ email marketing (Mailchimp/Klaviyo/SendGrid/Drip/GetResponse/MailerLite/Moosend/ActiveCampaign) ·
`facebook-custom-audience` · `send-meta-capi-event` · `choose-channel` — **đều không làm**, không
phục vụ nghiệp vụ tour.

### 3.8 Sự kiện vòng đời tin nhắn — bắt buộc có đủ

Từ `flow-config/src/event/schema.ts`: **received · sent · delivered · seen · failed · clicked ·
ref-link**.

⚠️ Đừng bỏ `delivered`/`seen`/`failed`. Thiếu chúng thì nhân viên **không phân biệt được**
"khách chưa đọc" với "gửi hỏng" — mà hai cái đó dẫn tới hai hành động trái ngược. `ref-link` là
link có gắn mã nguồn (biết khách đến từ quảng cáo nào) — đợt 2.

---

## 4. Cố ý KHÔNG làm trình vẽ luồng kéo-thả

ChatbotX có 13 loại node và cả bộ máy chạy luồng. Bỏ hẳn ở đợt đầu, vì:

- Nó giải bài toán **"người không biết code tự dựng kịch bản chatbot"**. Bài toán của mình khác:
  **AI đã đọc được CRM rồi**, nên phần lớn câu hỏi ("tour Đà Nẵng còn chỗ không", "giá bao nhiêu",
  "đặt cọc thế nào") trả lời thẳng bằng dữ liệu, không cần kịch bản.
- Trình vẽ luồng là hạng mục lớn nhất trong cả repo. Làm nó trước = trì hoãn thứ mang lại giá trị
  ngay: **thấy được tin khách nhắn và trả lời được**.
- Nếu sau này cần, phần `condition`/`wait`/`start-flow` đã chừa chỗ ở §3.4.

Thay vào đó đợt 1 dùng **luật đơn giản, khai bằng cấu hình**: khớp từ khoá → hành động (theo
`automated-response/src/keyword-match.ts`), còn lại giao AI.

---

## 5. Mô hình dữ liệu

Bảng mới trong SQL Server (`ConnectionStrings:PushDb`), theo lệ đặt tên sẵn có. ChatbotX dùng 138
bảng — phần lớn cho trình vẽ luồng, chiến dịch và tích hợp bên thứ ba mà mình không làm.

| Bảng | Giữ gì |
|---|---|
| `dbo.ChatChannels` | Khai báo kênh theo tenant: loại kênh, khoá/token, trạng thái. Zalo đã có `dbo.TenantChannelSettings` — **tái dùng**, không tạo trùng |
| `dbo.ChatContacts` | Danh tính theo kênh (Zalo user id, PSID Facebook…) + **trỏ về khách CRM**. PK `(TenantId, Channel, ExternalId)` |
| `dbo.ChatConversations` | Một luồng chat. Giữ: người được giao, trạng thái (mới/đang xử lý/đã đóng), `BotResumeAt`, mốc đọc của **cả hai phía**, `ContactRepliedAt` (tính cửa sổ gửi), `LastActivityAt` |
| `dbo.ChatMessages` | Tin nhắn: chiều (vào/ra), nội dung, đính kèm, trạng thái vòng đời (§3.8), id gốc phía kênh để chống trùng |
| `dbo.ChatOutbox` | Hàng đợi gửi RIÊNG cho chat |

⚠️ **Không nhét chat vào `dbo.OutboundMails`.** Bảng đó là **thông báo hệ thống** — có `TemplateCode`,
`ScheduledUtc`, hợp đồng `title`+`bodyHtml`, và worker bên `toutkit-app` đang rút. Chat khác hẳn:
gửi ngay, nội dung tự do, có **cửa sổ thời gian theo kênh**, và hỏng thì phải báo lại vào đúng hội
thoại. Trộn hai thứ là làm hỏng cả hai — hàng đợi thông báo đang chạy tốt, đừng đụng vào.

⚠️ **`BotResumeAt` không phải cờ bật/tắt.** ChatbotX để nó là **mốc thời gian** (`conversation.ts`),
và đó là thiết kế đúng: nhân viên nhảy vào trả lời thì bot câm **có thời hạn**, hết hạn tự nói lại.
Nếu làm cờ bật/tắt thì sẽ có hội thoại bị tắt bot vĩnh viễn vì hôm đó có người lỡ nhắn một câu, và
không ai nhớ để bật lại.

---

## 6. Luật nghiệp vụ bắt buộc

Những luật này **sai là hỏng thật**, không phải chuyện đẹp xấu.

**① Cửa sổ gửi theo kênh.** Zalo OA `message/cs` chỉ gửi được trong **48 giờ** kể từ tin cuối của
khách; Messenger là **24 giờ**. Hết cửa sổ mà cứ gửi thì API trả lỗi và **tin biến mất trong im
lặng**. Phải: tính cửa sổ từ `ContactRepliedAt`, hết hạn thì **chặn ở giao diện** kèm nói rõ lý do,
và gợi ý đường thay thế (ZNS theo mẫu — thứ mình đã có).

> Đây chính là lý do dự án đã bỏ `message/cs` cho bản tin sáng hồi 14/08. Nhưng **hai việc khác
> nhau**: bản tin là mình chủ động đẩy đi (cửa sổ luôn đóng), còn chat là trả lời khách vừa nhắn
> (cửa sổ vừa mở). Dùng `message/cs` cho chat là đúng chỗ.

**② Người thật vào thì bot câm.** Nhân viên gửi một tin → đặt `BotResumeAt = now + N phút`
(mặc định 30). Trong khoảng đó tin khách nhắn **vẫn lưu, vẫn hiện**, chỉ không sinh trả lời tự động.

**③ Chống trả lời trùng.** Webhook của mọi kênh đều **gửi lại khi không nhận được 200**. Phải khoá
theo id tin gốc phía kênh — nhận lần hai thì bỏ qua, không sinh thêm câu trả lời. Thiếu chốt này,
khách sẽ nhận hai ba câu giống hệt nhau.

**④ Gộp tin nhắn liên tiếp.** Khách hay gõ ba dòng liền ("cho hỏi tour Đà Nẵng" / "đi 4 ngày" /
"2 người lớn 1 trẻ"). Trả lời từng dòng là ba câu rời rạc, đọc rất ngớ ngẩn. ChatbotX gom bằng
`smart-delay.ts` — **chờ vài giây im lặng rồi mới xử lý cả cụm**. Phải làm.

**⑤ Một khách, một hồ sơ.** Cùng người nhắn từ Zalo và Facebook phải gộp về một khách CRM khi
nhận ra (qua số điện thoại/email họ để lại). Chưa nhận ra thì để riêng — **tuyệt đối không đoán
mò rồi gộp nhầm** hai khách thành một.

**⑥ Phân quyền.** Nhân viên chỉ thấy hội thoại của mình; xem hết cần quyền tương ứng — cùng luật
đã áp cho Bảng tin và thư nhắc thu tiền. **Không để hộp thư chat thành cửa sau đọc dữ liệu khách
của người khác.**

---

## 7. Lộ trình

**Đợt 1 — Zalo OA hai chiều + hộp thư chung.** Nhận webhook Zalo, lưu hội thoại, hiện hộp thư,
AI trả lời bằng dữ liệu CRM, nhân viên tiếp quản được, giao việc cho nhau được. Kèm đủ 6 luật ở §6.
*Xong đợt này là đã dùng được thật.*

**Đợt 2 — Facebook Messenger.** Cùng bộ khung, thêm một adapter. Đây là phép thử kiến trúc: nếu
thêm kênh thứ hai mà phải sửa phần lõi thì phần trừu tượng hoá ở đợt 1 đã sai.

**Đợt 3 — làm dày.** Thẻ/băng chuyền tour, chia việc tự động, tin thoại, đọc ảnh hộ chiếu,
`external-request`.

**Đợt 4 (nếu cần) — chiến dịch nhỏ giọt.** Chỉ mở khi đã chắc về luật quảng cáo của từng nền tảng.

Mỗi đợt sau một cờ `Features:*` riêng, mặc định tắt — theo đúng lệ đang áp cho mọi tính năng mới.

---

## 8. Chỗ dễ sai, ghi sẵn cho người làm

1. **Đừng tạo danh bạ thứ hai.** Khách đã có trong CRM (§3.3).
2. **Đừng nhét chat vào hàng đợi thông báo.** Hai vòng đời khác nhau (§5).
3. **Đừng làm `BotResumeAt` thành cờ.** Phải là mốc thời gian (§5).
4. **Đừng bỏ sự kiện `delivered`/`seen`/`failed`.** Không có chúng thì nhân viên đoán mò (§3.8).
5. **Đừng gọi API gửi khi hết cửa sổ.** Chặn ở giao diện và nói rõ, đừng để lỗi im lặng (§6①).
6. **Đừng bắt đầu bằng trình vẽ luồng.** Hạng mục lớn nhất, giá trị đến sau cùng (§4).
7. **Đừng chạy `execute-javascript`.** Chạy mã người dùng nhập cần hộp cát riêng (§3.7).

---

## 9. Câu chưa có lời giải

- **Zalo OA của ai?** Chat cần OA riêng từng công ty (giống ZNS đã chốt 17/08) — tin hiện tên OA
  người gửi, dùng OA chung là khách thấy tên công ty khác. Nhưng khai OA cho *chat* cần quyền
  khác với ZNS; phải kiểm lại bộ quyền Zalo trước khi code.
- **Giữ lịch sử chat bao lâu?** Bảng tin đang giữ 30 ngày. Chat nhiều hơn hẳn về lượng, và có thể
  là **chứng cứ giao dịch** — cần chốt trước khi bảng phình.
- **Tính lượt AI thế nào?** Mỗi tin khách nhắn là một lượt. Một hội thoại 20 lượt qua lại tốn gấp
  20 lần một lần chấm khách. Hạn mức tenant hiện tại chưa tính tới kiểu tiêu này.
