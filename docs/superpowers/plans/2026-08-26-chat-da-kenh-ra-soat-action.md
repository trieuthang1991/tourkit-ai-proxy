# Rà soát ĐỦ hành động của hộp thư chat đa kênh

**Ngày:** 26/08/2026 · **Nguồn đối chiếu:** `D:\MiGroup\AI\chat-bot-xio\ChatbotX` (có CodeGraph —
`cd` vào đó rồi `codegraph explore "..."`, đừng grep mò) + tài liệu Meta chính thức.

## Vì sao có tài liệu này

Ngày 26/08/2026 tự suy ra danh sách quyền OAuth cho Messenger, cố ý bỏ `business_management` "cho
nhẹ khâu duyệt". Facebook cấp quyền **thành công**, màn hình đồng ý **không báo lỗi gì**, nhưng
`/me/accounts` trả **rỗng** — mất gần một buổi lần mò. ChatbotX xin đúng quyền đó ngay từ đầu.

Bài học: **cụm chat đa kênh dày nghiệp vụ ngầm, và hỏng thì hỏng im lặng.** Không phải "gọi sai API
báo lỗi 400", mà là "mọi thứ trông như chạy, chỉ có một thứ không bao giờ tới". Nên phải liệt kê
đủ hành động rồi soát từng cái, thay vì làm tới đâu phát hiện tới đó.

---

## A. Sự kiện NHẬN VÀO (webhook)

Cột "ChatbotX" = họ có xử lý không. Cột "Mình" = hiện trạng `MessengerChatAdapter.Parse`.

| Sự kiện Meta | Là gì | ChatbotX | Mình | Ưu tiên |
|---|---|---|---|---|
| `messages` | Tin khách gửi (chữ, ảnh, tệp, vị trí) | ✅ | ✅ | — |
| `message_echoes` | Tin nhân viên gửi từ ứng dụng Meta | ✅ (bỏ echo của chính mình theo `metadata`) | ✅ | — |
| `message_deliveries` | Đã tới máy khách (một tích) | ❌ | ✅ | — |
| `message_reads` | Khách đã đọc (hai tích) | ✅ | ✅ | — |
| `message_reactions` | **Khách thả cảm xúc** | ❌ | ❌ | **P1 — đã báo lỗi thật** |
| `messaging_postbacks` | Khách bấm nút | ✅ | ❌ (`Parse` bỏ qua) | P2 |
| `messaging_optins` | Khách đồng ý nhận tin | ❌ | ❌ | P3 |
| `messaging_referrals` | Khách vào từ liên kết/QR/quảng cáo | ❌ | ❌ | P2 |
| `feed` (bình luận) | Bình luận trên bài đăng Trang | ✅ (thêm/sửa/xoá) | ❌ | P3 |
| `inbox_labels` | Nhãn hộp thư đổi bên Meta | ✅ (đồng bộ về) | ❌ | P3 |
| `standby` / `messaging_handovers` | Bàn giao giữa nhiều app | ❌ | ❌ | P4 |

⚠️ **Đăng ký ≠ xử lý.** Đang đăng ký 7 trường bên Meta; `message_reactions` **không nằm trong đó**,
nên dù viết mã bóc cũng không bao giờ có gói tin nào tới. Phải sửa **cả hai chỗ**.

## B. Hành động GỬI RA

| Hành động | ChatbotX | Mình | Ưu tiên |
|---|---|---|---|
| Gửi chữ | `sendPageMessage` | ✅ | — |
| Gửi ảnh/tệp qua URL | ✅ | ✅ | — |
| **Báo "đang gõ"** (`sender_action`) | ❌ | ❌ | **P1 — rẻ, ăn ngay vào cảm giác mượt** |
| Đánh dấu đã xem (`mark_seen`) | ❌ | ❌ | P2 |
| Tải tệp lên trước rồi gửi lại nhiều lần | `uploadAttachment` | ❌ | P3 |
| Nhãn khách của Meta | `assignLabelToUser`… | ❌ (mình có nhãn RIÊNG) | P4 — cân nhắc kỹ |
| Menu cố định | `setUserPersistentMenu` | ❌ | P4 |
| Mẫu tin duyệt sẵn (ngoài cửa sổ 24h) | `message-templates.ts` | ❌ | P3 |
| Hồ sơ khách | `getUserProfile` | ✅ (26/08) | — |

## C. Việc cụ thể

### C1 — Reaction (P1, đang có lỗi thật)

Người dùng thả cảm xúc, hộp thư không hiện gì.

**Ba chỗ phải sửa, thiếu một là im lặng:**

1. **Đăng ký thêm trường** `message_reactions` bên Meta (một lượt gọi `subscribed_apps`).
2. **Bóc gói tin.** Dạng Meta gửi:
   ```json
   {"sender":{"id":"<PSID>"},"recipient":{"id":"<PAGE>"},"timestamp":…,
    "reaction":{"mid":"<mid tin bị thả>","action":"react","emoji":"❤️","reaction":"love"}}
   ```
   `action` có hai giá trị: `react` và **`unreact`** (bỏ thả). Bỏ sót `unreact` là cảm xúc đã gỡ vẫn
   hiện mãi.
3. **Lưu + hiện.** Reaction gắn vào **một tin cụ thể** (`mid`), không phải một tin mới trong hội
   thoại. Ghi thành tin mới là dòng thời gian loạn: "❤️" hiện như một câu khách nói.

⚠️ **Không có nguồn chép.** ChatbotX không xử lý reaction ở kênh nào (WhatsApp của họ ghi thẳng
`// case "reaction": do nothing`). Phải theo tài liệu Meta và tự kiểm bằng tay.

⚠️ **Zalo và Telegram cũng có cảm xúc** nhưng dạng khác hẳn. Làm phần lưu trữ theo hướng chung
(`tin nào` + `ai` + `biểu tượng`), đừng gắn cứng vào dạng của Meta.

### C2 — "Đang gõ" (P1)

Bot mất vài giây mới trả lời, trong lúc đó khách nhìn màn hình trống và tưởng không ai đọc. Meta có
`sender_action=typing_on`; gửi ngay lúc nhận tin là khách thấy ba chấm.

Rẻ (một lượt gọi, không lưu gì) và ăn thẳng vào đúng cảm giác "không mượt" — cùng họ với bản sửa
đánh thức worker hôm nay.

### C3 — Postback + referral (P2)

Chưa dùng nút bấm nên chưa gấp. Nhưng `messaging_referrals` cho biết **khách đến từ đâu** (quảng
cáo nào, mã QR nào) — dữ liệu bán hàng thật, mà không bóc lúc nhận là **mất vĩnh viễn**.

### C4 — Rà lại phần đã có

- Echo của **chính mình**: ChatbotX gắn `metadata` lúc gửi rồi bỏ echo mang dấu đó. Mình dựa vào
  ràng buộc trùng `external_msg_id` — **đã kiểm là có ràng buộc**, nhưng chưa kiểm bằng tay đầu cuối.
- URL ảnh đại diện của Meta **hết hạn**. Hiện lấy một lần rồi thôi → vài tuần nữa ảnh vỡ. Cần một
  cột ghi mốc lần hỏi cuối.

## D. Quy tắc làm việc từ nay

1. **Đọc ChatbotX TRƯỚC khi viết**, dùng CodeGraph: `cd D:\MiGroup\AI\chat-bot-xio\ChatbotX` rồi
   `codegraph explore "<câu hỏi>"`. Nhanh hơn grep và không sót đường gọi động.
2. **ChatbotX không có ≠ không cần.** Họ bỏ reaction, `message_deliveries`, `optins`. Không có thì
   quay về tài liệu Meta, và ghi rõ trong mã là đã tra ở đâu.
3. **Chép CÁCH LÀM, không chép mã** — khác ngôn ngữ, khác kiến trúc.
4. **Mỗi lần thêm một sự kiện nhận vào: sửa ĐỦ HAI chỗ** — đăng ký bên Meta *và* bóc trong `Parse`.
   Thiếu một là hỏng im lặng, không lỗi, không log.
5. Chỗ nào cố ý làm khác ChatbotX thì ghi lý do vào [`docs/features/chat-inbox.md`](../../features/chat-inbox.md).

---

## E. Soát lại hiện trạng — 28/08/2026

Soát từng dòng bảng A và B trên mã thật, không tin danh sách cũ. Hai chỗ hỏng IM LẶNG mới lộ ra
đúng bằng cách này, dù quy tắc "sửa ĐỦ HAI chỗ" ở mục D đã được viết ra từ 26/08.

### Đã xong

| Việc | Ghi chú |
|---|---|
| `message_reactions` | Bóc trong `MetaMessagingParser` (27/08), gồm cả `unreact` |
| `messaging_postbacks` | Bóc kèm chữ trên nút, không phải payload kỹ thuật |
| `messaging_referrals` | Ba cột `referral_*`, ghi MỘT LẦN rồi thôi |
| `feed` / `comments` | Bình luận dưới bài, tách khỏi tin riêng bằng `surface` + `source_thread_id` |
| Báo "đang gõ" · `mark_seen` | `SendTypingAsync` · `MarkSeenAsync` (gọi từ đường đánh dấu đã đọc) |
| Mẫu tin ngoài cửa sổ 24h | `POST /conversations/{id}/send-template` |
| Ảnh đại diện Meta hết hạn | Soi về kho riêng lúc lấy hồ sơ + vòng vét nền (28/08) |

### Vừa sửa hôm nay — cả hai đều là hỏng im lặng

1. **`message_reactions` thiếu trong `PageEvents` của Trang Facebook.** Bộ bóc đọc nhánh này từ
   27/08 và Instagram đăng ký đủ, riêng Trang thì quên → khách thả tim trên Messenger là hộp thư
   không hiện gì. Nay đã thêm, và có `MetaWebhookFieldTests` so chéo hai danh sách để lần sau
   không lọt nữa (đã thử bỏ trường ra: test đỏ 3 chỗ).
   ⚠️ **Trang đã nối từ trước vẫn thiếu** — Meta chỉ nhận danh sách lúc đăng ký. Phải bấm nối lại
   Trang đó một lượt.

2. **`messaging_optins` đã đăng ký nhưng KHÔNG bóc.** Ca ngược lại của (1), và trớ trêu là chú
   thích ngay trong `MetaMessagingParser` đã liệt kê `m.optin.ref` là một trong ba nguồn "khách đến
   từ đâu" — chỉ có mã là không đọc. Gói optin không mang `source`/`ad_id`, nên nguồn tự đặt nhãn
   `OPTIN`. Gói không có `sender.id` (hộp tích trên web, khách chưa từng nhắn) thì bỏ qua.

### Còn lại, theo thứ tự đáng làm

| Việc | Ưu tiên | Vì sao chưa làm |
|---|---|---|
| Đánh dấu **chưa đọc** | Nên làm | ChatbotX có (`unread-conversation`), mình chỉ có đánh dấu ĐÃ đọc. Người trực mở nhầm là mất dấu, không trả lại được. Cần: sửa mốc đọc của riêng người đó về trước tin cuối. |
| **Theo dõi** hội thoại | Nên làm | ChatbotX có (`follow`/`unfollow`). Mình chỉ có giao việc — tức phải NHẬN mới theo dõi được. Quản lý muốn ngó một ca khó mà không giành việc của nhân viên thì chưa có đường. |
| Nhãn khách của Meta | P4 | Mình đã có nhãn RIÊNG (`chat_contact_tags`) đủ dùng. Đồng bộ hai chiều với Meta là thêm một nguồn sự thật thứ hai — cân nhắc kỹ trước khi làm. |
| Tải tệp lên tái sử dụng | P3 | Chỉ đáng khi gửi CÙNG một tệp cho nhiều khách (ảnh bảng giá). Chưa có nhu cầu đó. |
| Menu cố định / lời chào | P4 | `chat_bot_settings.greeting` đã lưu nhưng **chưa nối vào đường gửi** — làm thì làm trọn gói cùng menu. |
| Bàn giao `standby` | P4 | Chỉ cần khi một Trang cắm NHIỀU ứng dụng cùng lúc. Chưa công ty nào như vậy. |

### Lưu trữ (archive) — cố ý KHÁC ChatbotX, đừng "sửa"

ChatbotX tách `archived` thành một trục riêng, độc lập với trạng thái. Mình gộp: `status = 2`
(đã đóng) là tự đặt `archived_at`. Một trục thì người trực chỉ phải hiểu một khái niệm; hai trục
thì luôn có câu hỏi "đóng rồi mà chưa lưu trữ nghĩa là gì". Giữ nguyên.
