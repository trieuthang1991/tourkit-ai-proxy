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
