# Biểu so sánh hành động: TourKit ↔ ChatbotX — TẤT CẢ các kênh

**Ngày:** 28/08/2026 · **Nguồn:** đọc mã thật hai bên, không đọc tài liệu.
`D:\MiGroup\AI\chat-bot-xio\ChatbotX` (thư mục `integrations/*`, `packages/business`,
`apps/builder/src/features/conversations/actions`) ↔ `TourkitAiProxy.Services/Chat/Channels`.

Khác [bản rà soát 26/08](2026-08-26-chat-da-kenh-ra-soat-action.md) ở chỗ: bản đó chỉ soát **Meta**
(webhook + gửi ra). Bản này soát **cả sáu kênh** và thêm hai nhóm mà bản cũ bỏ sót hẳn — thao tác
quản trị kênh và thao tác trên hội thoại.

Ký hiệu: ✅ có · ❌ chưa có · ➖ nền tảng không hỗ trợ (không phải thiếu sót) · ⚠️ có nhưng lệch

---

## A. Kênh

| Kênh | ChatbotX | TourKit |
|---|---|---|
| Messenger · Instagram · WhatsApp · Telegram · Zalo · TikTok | ✅ | ✅ |
| Webchat (widget nhúng web) | ✅ (một kênh trong hộp thư) | ⚠️ có **Widget Chat** nhưng là tính năng RIÊNG, không đổ vào hộp thư |

Sáu kênh chính ngang nhau. Webchat lệch về kiến trúc chứ không phải thiếu.

---

## B. Loại tin GỬI RA

| Loại | ChatbotX | TourKit | Ghi chú |
|---|---|---|---|
| Chữ | 6 kênh | ✅ 6 kênh | |
| Ảnh / tệp / video / âm thanh | 6 kênh | ✅ `SendMediaAsync` 6 kênh | |
| Nút bấm | MS·IG·TG·Zalo·TikTok | ✅ MS·IG·TG·Zalo·**WA** · ❌ TikTok | Mình có thêm WhatsApp; thiếu TikTok — xem §F |
| Trả lời nhanh (`quick_replies`) | MS·IG·TG | ✅ (`MetaButtonBuilder` tự chọn giữa nút và `quick_replies`) | |
| Mẫu duyệt sẵn (ngoài cửa sổ) | MS·WA | ✅ MS·WA·**Zalo ZNS** | Mình phủ rộng hơn |
| Nhãn "nhân viên thật" (HUMAN_AGENT, 7 ngày) | MS | ✅ MS·IG | |
| **Băng chuyền (carousel)** | MS·IG·TG·WA | ❌ | |
| **Ảnh động (GIF) riêng** | MS·IG | ❌ (đi chung đường media) | |
| **Thẻ (card) · Flow · danh sách chọn** | WA | ❌ | Rất đặc thù WhatsApp |
| Báo "đang gõ" | TG (`sendChatAction`) | ✅ 6 kênh | |
| Đánh dấu đã xem | ❌ | ✅ | |

---

## C. Sự kiện NHẬN VÀO

### Meta (Trang Facebook)

| Trường | ChatbotX | TourKit |
|---|---|---|
| `messages` · `message_echoes` · `messaging_postbacks` · `messaging_optins` · `messaging_referrals` · `feed` | ✅ | ✅ |
| `message_reads` | ✅ | ✅ |
| `message_deliveries` (một tích) | ❌ | ✅ |
| `message_reactions` (thả tim) | ❌ | ✅ (sửa 28/08) |
| **`inbox_labels`** (nhãn đổi bên Meta) | ✅ | ❌ |
| **`standby`** (bàn giao nhiều ứng dụng) | ✅ | ❌ |
| **`messaging_feedback`** (khách chấm sao) | ✅ | ❌ |
| **`messaging_policy_enforcement`** (Meta phạt Trang) | ✅ | ❌ |
| **`messaging_customer_information`** | ✅ | ❌ |
| `live_videos` | ✅ | ➖ không thuộc hộp thư |

### Các kênh khác

| Kênh | ChatbotX | TourKit |
|---|---|---|
| Instagram | `messages` `messaging_postbacks` `messaging_optins` `messaging_seen` `messaging_referral` `comments` | ✅ đủ **+ `message_reactions`** |
| WhatsApp | `messages` `history` `smb_app_state_sync` `smb_message_echoes` | ✅ đủ **+ `message_echoes`** |
| Telegram | (không khai `allowed_updates` → nhận mặc định, **thiếu `message_reaction`**) | ✅ khai đủ 5 loại kể cả `message_reaction` |
| Zalo | 8 sự kiện `user_send_*` + `user_seen_message` | ✅ đủ **+ `oa_send_*`** (tiếng vọng OA) |

Ba kênh này mình **phủ rộng hơn** ChatbotX.

---

## D. Thao tác QUẢN TRỊ KÊNH — nhóm lệch nhiều nhất

| Việc | ChatbotX | TourKit |
|---|---|---|
| Nối kênh bằng OAuth một nút | 6 kênh | ✅ 6 kênh |
| Gỡ kênh | ✅ | ✅ |
| Lấy hồ sơ khách | ✅ | ✅ |
| Nhập hội thoại cũ | MS (`listConversations`/`listMessages`) | ✅ MS·IG·WA |
| **Trả lời BÌNH LUẬN công khai** | MS·IG `sendComment` | ❌ **nhận được mà không trả lời được** |
| **Ẩn / xoá / sửa / thích bình luận** | MS `hideComment` `deleteComment` `editComment` `likeComment` | ❌ |
| **Đọc chi tiết bài viết** (`getPostDetails`) | MS·IG | ❌ |
| **Nhãn khách của Meta** | tạo/xoá/gán/gỡ/liệt kê | ❌ (có nhãn riêng, xem §F) |
| **Nhãn khách của Zalo** | `listOaTags` `tagFollower` `removeTag`… | ❌ (có nhãn riêng) |
| **Menu cố định** | MS·IG đọc/ghi/xoá | ❌ |
| **Hồ sơ bot** (lời chào, nút Bắt đầu, câu mở đầu) | `updateMessengerProfile` | ⚠️ có lưu `greeting` nhưng **chưa nối vào đâu** |
| **Persona** (nhiều danh nghĩa trả lời) | MS tạo/sửa/xoá | ❌ |
| **Tải tệp lên trước, dùng lại nhiều lần** | MS·IG·Zalo·TikTok | ❌ |
| **Quản lý mẫu tin** (tạo/liệt kê/nhân bản) | MS·WA | ⚠️ gửi được mẫu đã duyệt, nhưng **không tạo/sửa mẫu từ app** |
| Quản lý số điện thoại WABA, Flow, hạn mức | WA (rất sâu) | ❌ |
| **Đếm hạn mức Trang** (`concurrencyForUsage`, BUC header) | MS | ❌ |

---

## E. Thao tác TRÊN HỘI THOẠI

| Việc | ChatbotX | TourKit |
|---|---|---|
| Giao việc | ✅ | ✅ nhận · nhả · chuyển |
| Bật / tắt bot | ✅ | ✅ **có hạn giờ** (hơn: không ai quên bật lại) |
| Đánh dấu đã đọc | ✅ | ✅ **theo từng người** (hơn) |
| Trạng thái · lưu trữ | ✅ 2 trục | ⚠️ 1 trục — cố ý, xem §G |
| **Đánh dấu CHƯA đọc** | ✅ | ❌ |
| **Theo dõi hội thoại** | ✅ | ❌ |
| Chế độ chat trực tiếp | ✅ | ➖ đã có trong "tắt bot có hạn giờ" |
| Nhãn khách | ✅ (đồng bộ Meta/Zalo) | ✅ (nội bộ) |
| Ghi chú nội bộ | ? | ✅ |
| Nhật ký thao tác | ? | ✅ |
| Nối khách sang CRM | ➖ | ✅ (không có bên kia) |

---

## F. Việc còn thiếu, xếp theo mức đáng làm

| # | Việc | Vì sao đáng / chưa đáng |
|---|---|---|
| 1 | **Trả lời bình luận công khai** | **Tính năng đang dở dang.** Bình luận đã vào hộp thư từ 28/08 nhưng đường gửi (`ChatOutboxWorker`) không hề biết tới `surface` — người trực gõ trả lời một bình luận thì tin đi ra đường TIN NHẮN RIÊNG. Hoặc hỏng, hoặc trả lời riêng cho người chỉ muốn hỏi công khai. Phải làm trước khi mở bình luận cho người dùng. |
| 2 | **Đánh dấu chưa đọc** | Mở nhầm là mất dấu, không trả lại được. Rẻ: sửa một mốc thời gian. |
| 3 | **Theo dõi hội thoại** | Quản lý muốn ngó ca khó mà không giành việc của nhân viên. Rẻ: một bảng như `chat_conversation_reads`. |
| 4 | **Ẩn bình luận** | Bình luận xấu dưới bài quảng cáo là chuyện hằng ngày; hiện phải mở Facebook ra ẩn tay. Đi cùng #1. |
| 5 | **Menu cố định + lời chào + nút Bắt đầu** | `greeting` đã lưu mà chưa dùng — đang là lời hứa suông trong màn hình cài đặt. |
| 6 | `messaging_policy_enforcement` | Meta phạt Trang thì hộp thư im lặng ngừng gửi được; có sự kiện này mới biết lý do. Rẻ (một nhánh bóc). |
| 7 | Băng chuyền (carousel) | Hợp để chào 3–5 tour kèm ảnh. Chưa gấp vì nút bấm đã đủ dùng. |
| 8 | Nút bấm cho TikTok | ChatbotX có `send-button` cho TikTok, mình ghi trong CHANGELOG là "TikTok không có nút". **Một trong hai bên sai — cần kiểm lại tài liệu TikTok.** |
| 9 | Tải tệp lên dùng lại | Chỉ đáng khi gửi cùng một tệp cho nhiều khách (ảnh bảng giá). |
| 10 | Đồng bộ nhãn Meta/Zalo | Thêm nguồn sự thật thứ hai. Cân nhắc kỹ. |
| 11 | Persona · WABA · Flow · `standby` · `messaging_feedback` | Nghiệp vụ của nền tảng lớn, chưa công ty nào cần. |

---

## G. Chỗ CỐ Ý khác — đừng "sửa"

1. **Lưu trữ gộp vào trạng thái.** ChatbotX tách `archived` thành trục riêng. Mình gộp: `status = 2`
   (đã đóng) tự đặt `archived_at`. Một trục thì người trực chỉ phải hiểu một khái niệm.
2. **Nhãn khách là của mình, không đồng bộ về nền tảng.** Đồng bộ hai chiều là hai nguồn sự thật, và
   xung đột thì không có bên nào đúng hiển nhiên.
3. **Tắt bot có HẠN GIỜ** thay vì cờ bật/tắt. Cờ thì sẽ có hội thoại tắt bot vĩnh viễn chỉ vì hôm đó
   có người lỡ nhắn một câu.
4. **Widget Chat tách khỏi hộp thư.** Widget trả lời bằng kiến thức nền + CRM, khác đường hẳn.

## H. Chỗ mình HƠN ChatbotX — giữ, đừng bỏ khi "chuẩn hoá theo bên kia"

`message_deliveries` (một tích) · `message_reactions` cho cả Messenger lẫn Telegram · tiếng vọng
`oa_send_*` của Zalo · đánh dấu đã đọc theo từng người · tắt bot có hạn giờ · mẫu ZNS của Zalo ·
nút bấm cho WhatsApp · ghi chú nội bộ · nhật ký thao tác · nối khách sang CRM.

---

## I. Nhóm "xoá / gỡ / báo xấu / chuyển tiếp"

⚠️ **ChatbotX KHÔNG có cái nào trong nhóm này** — không `deleteMessage`, không thu hồi, không chặn,
không báo xấu, không chuyển tiếp, ở cả sáu kênh. Nên đây là chỗ áp luật D2 của bản rà soát 26/08:
*"ChatbotX không có ≠ không cần"* — phải quay về tài liệu nền tảng, và ghi rõ đã tra ở đâu.

Điểm mấu chốt của cả nhóm: **mỗi việc đều nằm vắt qua ranh giới giữa dữ liệu của mình và dữ liệu
của nền tảng.** Một nút hứa hẹn tác dụng phía khách mà thật ra chỉ đổi CSDL của mình là một lời nói
dối ở tầng giao diện — và là loại nói dối có hậu quả thật: nhân viên tưởng đã thu hồi được câu lỡ
tay, nên không đi xin lỗi khách.

| Việc | Nền tảng cho phép? | ChatbotX | TourKit | Kết luận |
|---|---|---|---|---|
| **Xoá hội thoại** | — (chuyện nội bộ) | ❌ chỉ lưu trữ | ✅ lưu trữ qua `status = 2` | **ĐÃ CHỐT 27/08: không làm.** Lịch sử chat là dữ liệu nghiệp vụ (bối cảnh CRM, tra khiếu nại, bàn giao). Bỏ lưu trữ thì đổi trạng thái khác 2 là xong — đã có. |
| **Gỡ tin — Telegram** | ✅ `deleteMessage` (48 giờ), thu hồi THẬT | ❌ | ❌ | Đáng làm, và là kênh DUY NHẤT gỡ được thật. |
| **Gỡ tin — Messenger · Instagram · WhatsApp** | ❌ Meta không có API thu hồi cho doanh nghiệp | ❌ | ❌ | Chỉ xoá được trong hộp thư mình. Nút phải ghi đúng chữ đó. |
| **Gỡ tin — Zalo · TikTok** | **chưa tra** | ❌ | ❌ | Phải đọc tài liệu trước khi hứa gì. |
| **Sửa tin đã gửi** | Telegram ✅ `editMessageText`; Meta ❌ | ❌ | ❌ | Cùng cảnh với gỡ tin. |
| **Chặn khách / báo xấu** | Hầu như không kênh nào có API cho phía doanh nghiệp | ❌ | ❌ (chỉ PHÁT HIỆN được khách chặn MÌNH — xem `ChannelFailures`) | Làm được ở mức **nội bộ**: đánh dấu khách bị chặn → hộp thư ẩn, bot câm, không gửi ra. Đừng gọi là "báo xấu" nếu không thật sự báo cho nền tảng. |
| **Chuyển tiếp cho đồng nghiệp** | — (nội bộ) | ✅ giao việc | ✅ **đã có** (nhận · nhả · chuyển việc) | Không thiếu. |
| **Chuyển tiếp một TIN sang hội thoại khác** | Telegram ✅ `forwardMessage`/`copyMessage`; còn lại ❌ | ❌ | ❌ | Kênh khác vẫn làm được bằng cách chép nội dung rồi gửi như tin mới — nhưng lúc đó nó là tin của mình, không phải "chuyển tiếp". |
| **Xoá dữ liệu khách** (khách yêu cầu) | — (nghĩa vụ pháp lý) | ❌ | ❌ | Đáng làm riêng, không thuộc nhóm tiện ích. Lưu ý `chat_contact_notes` và `chat_audit` cố ý KHÔNG chép nội dung tin, nên chỗ phải xoá ít hơn. |

### Nếu làm, làm theo thứ tự này

1. **Chặn khách (nội bộ).** Rẻ, không phụ thuộc nền tảng, và là thứ người trực cần thật khi gặp
   khách quấy rối. Một cột trên `chat_contacts` + lọc ở danh sách + chặn ở đường gửi.
2. **Gỡ tin.** Làm cả hai nghĩa trong MỘT nút, nhưng chữ trên màn hình đổi theo kênh: Telegram thì
   "Thu hồi cả hai bên", kênh Meta thì "Chỉ xoá trong hộp thư — khách vẫn thấy". Dùng lại cột
   `deleted_utc` sẵn có (hiện chỉ dùng cho bình luận khách tự xoá).
3. **Sửa tin đã gửi.** Chỉ bật ở Telegram. Kênh khác ẩn hẳn nút, đừng hiện rồi báo lỗi.
4. **Xoá dữ liệu khách.** Khi có yêu cầu thật hoặc trước khi ký hợp đồng có điều khoản dữ liệu.

**Không làm:** xoá hội thoại (đã chốt), "báo xấu" lên nền tảng (không có API — hứa là nói dối),
chuyển tiếp tin xuyên hội thoại ở kênh không hỗ trợ.

---

## J. BẢNG ĐẦY ĐỦ — đối chiếu từng action một

Lập bằng cách liệt kê **toàn bộ** thư mục `apps/builder/src/features/*/actions/` của ChatbotX (đó là
bản kiểm kê đúng nghĩa "người dùng bấm được gì"), rồi soát từng dòng sang mã của mình.

⚠️ Bản §E ở trên **thiếu hai nhóm lớn**: `contacts/actions` (15 action) và `messages/actions`
(5 action). Lần đó chỉ đọc `conversations/actions` nên tưởng chỉ hụt hai việc.

### J1. Hội thoại — `conversations/actions` (11 tệp)

| Action bên họ | Mình | Ghi chú |
|---|---|---|
| `assign-conversation` | ✅ | nhận · nhả · chuyển |
| `enable-bot` / `disable-bot` | ✅ | mình có **hạn giờ**, hơn |
| `read-conversation` | ✅ | mình **theo từng người**, hơn |
| `archive-conversation` | ✅ | qua `status = 2` |
| `unarchive-conversation` | ✅ | đổi trạng thái khác 2 là `archived_at` tự về NULL — **hôm qua ghi nhầm là thiếu** |
| `enable-live-chat` / `disable-live-chat` | ➖ | trùng với "tắt bot có hạn giờ" |
| **`unread-conversation`** | ❌ | |
| **`follow` / `unfollow-conversation`** | ❌ | |

### J2. Khách — `contacts/actions` (15 tệp) ← nhóm bỏ sót

| Action bên họ | Mình | Ghi chú |
|---|---|---|
| `add-contact-tag` / `remove-contact-tag` | ✅ | `chat_contact_tags` |
| **`block-contact` / `unblock-contact`** | ❌ | chỉ làm nội bộ được — xem §I |
| **`create-contact`** (thêm khách tay) | ❌ | hiện khách chỉ sinh ra khi họ nhắn tới |
| **`delete-contact`** | ❌ | nghĩa vụ pháp lý khi khách yêu cầu |
| **`export-contacts` / `import-contacts`** | ❌ | |
| **`add`/`update`/`delete-contact-custom-field`** | ❌ | mình có ghi chú tự do, **không có trường có cấu trúc** |
| `update-contact-tag` | ⚠️ | mình sửa nhãn từng khách, không sửa được nhãn toàn công ty |
| `add`/`remove-contact-sequence` | ➖ | chiến dịch marketing, không thuộc hộp thư |

### J3. Tin nhắn — `messages/actions` (5 tệp) ← nhóm bỏ sót

| Action bên họ | Mình | Ghi chú |
|---|---|---|
| `create-message` | ✅ | |
| `create-webchat-message` | ➖ | widget của mình là tính năng riêng |
| **`delete-message`** | ❌ | **đã kiểm mã họ: chỉ xoá trong CSDL của họ, KHÔNG gọi nền tảng** |
| **`edit-message`** | ❌ | cũng chỉ sửa CSDL |
| `change-message-attributes` | ❓ | chưa tra kỹ |

> Phát hiện đáng giá: **ChatbotX cũng không thu hồi được tin phía khách** — `deleteById` /
> `updateMessageText` là thao tác CSDL thuần. Nên bản ghi 27/08 nói "chỉ xoá cục bộ được" là ĐÚNG,
> và bên họ cũng vậy. Khác biệt duy nhất mình có thể làm tốt hơn: **nói thật chuyện đó trên giao diện**.

### J4. Cấu hình dùng chung

| Action bên họ | Mình |
|---|---|
| `saved-replies` tạo/sửa/xoá/liệt kê | ✅ mẫu trả lời nhanh |
| **`tags` tạo/sửa/xoá toàn công ty** | ❌ nhãn chỉ sinh ra khi gắn vào một khách |
| **`user-persistent-menus` tạo/sửa/xoá** | ❌ |
| `automated-response` tạo/sửa/xoá/bật | ⚠️ mình có trợ lý AI + lời dặn, khác cách làm |

### J5. Kết nối kênh — `integration-*/actions`

| Action bên họ | Mình |
|---|---|
| `connect` · `disconnect` (6 kênh) | ✅ 6 kênh |
| `select-page` / `select-account` | ✅ |
| `refresh-token` (TikTok) | ✅ tự gia hạn |
| `refresh-permissions` (MS·IG·Zalo) | ❌ — **nhưng đã kiểm: bên họ chỉ đổi access token, KHÔNG đăng ký lại webhook** |
| `toggle-tag-sync` (MS·Zalo) | ➖ mình cố ý không đồng bộ nhãn |
| `webhook-url` (WA) | ✅ |
| `message-templates` CRUD (MS·WA) | ⚠️ gửi được, không tạo/sửa từ app |

⚠️ `refresh-permissions` **không** giải được bài toán "Trang nối trước 28/08 thiếu
`message_reactions`" — bên họ cũng chỉ làm mới token. Muốn đăng ký lại trường webhook thì phải gọi
lại `subscribed_apps`. **Nên làm một nút "Đăng ký lại nhận tin" riêng**, rẻ hơn bắt người dùng gỡ
rồi nối lại cả Trang.

---

## K. Đối chiếu với danh sách miệng hôm 27/08

Nhật ký 27/08 ghi mình thiếu: *đánh dấu chưa đọc · theo dõi hội thoại · xoá/sửa tin · chặn khách ·
xoá khách · xuất/nhập danh bạ · bỏ lưu trữ*.

Soát lại hôm nay: **đúng 6/7**. Riêng **"bỏ lưu trữ" là ghi nhầm — mình đã có**. Và đợt quét hôm nay
tìm thêm 5 thứ nữa mà hôm qua sót: **tạo khách tay · trường thông tin có cấu trúc · quản lý nhãn
toàn công ty · menu cố định · nút đăng ký lại nhận tin**.
