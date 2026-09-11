# Hộp thư chat — phân tích bảy cụm đã hoãn, đối chiếu với mã thật

**Ngày:** 09/09/2026 (bản 2, đọc lại từng đường mã) · **Trạng thái:** phân tích, chưa chốt, chưa code
**Nguồn:** Sheet "Chat đa kênh" (Bug App) — các mục bị gạt khỏi đợt phân công/phân quyền
**Spec đợt trước:** [2026-09-07-chat-phan-cong-phan-quyen-design.md](2026-09-07-chat-phan-cong-phan-quyen-design.md)

Bản 1 viết theo quét nhanh và sai ở ba chỗ đáng kể; bản này sửa lại và ghi rõ chỗ sửa (§9).
Mỗi mục trả lời bốn câu: *đã có gì*, *còn thiếu gì*, *vướng ở đâu*, *nên làm theo hình nào*.

---

## 0. Đọc nhanh

| Cụm | Đã có sẵn trong mã | Còn thiếu | Repo khác | Cỡ |
|---|---|---|---|---|
| 7 · Lọc nhãn | nhãn trên khách + danh mục + khuôn lọc SQL | một mệnh đề `EXISTS` + một chip | không | nhỏ |
| 6 · Tên Trang | **tên đã lưu** (`label` trong cấu hình kênh) | đường cho nhân viên tra tên + chỗ hiện | không | nhỏ |
| 8 · Cỡ biểu tượng | ảnh 38px, huy hiệu kênh 11.5px | chưa rõ bảng gốc muốn gì | không | rất nhỏ |
| 1 · AI soạn gợi ý | **bot đã sinh câu trả lời** (`GenerateReplyAsync`) | đưa đúng bộ sinh đó ra cho người | không | nhỏ-vừa |
| 4 · Đồng bộ ERP | nối tay đã chạy; **số ĐT đã lưu** (Zalo, WhatsApp); CRM tìm theo số ĐT; CRM có API ghi CSKH | luật gợi ý nối + luật đẩy nhật ký | có (đọc + ghi CSKH) | vừa |
| 2 · Chấm cảm xúc | — (và hộp thư **không có** khái niệm ưu tiên để đặt nó vào) | toàn bộ | không | vừa |
| 3 · Tạo Cơ hội bán hàng | **CRM đã có `POST /bookingtickets`**; proxy đã có lối ghi sang CRM | kiểm quyền ở proxy, mã nguồn "chat", phụ thuộc mục 4 | có (ghi) | vừa |

Thứ tự đề nghị: **7 + 6 + 8 một đợt** → **1** → **4** → **3** → **2**.
Đổi so với bản 1: mục 2 xuống cuối (không có chỗ để đặt kết quả), mục 3 lên trước mục 2 (rẻ hơn tưởng).

---

## 1. Mục 7 — Lọc theo nhãn

**Đã có.**
- Nhãn gắn trên KHÁCH: `chat_contact_tags (tenant_id, channel, external_id, tag)`. Theo khách chứ
  không theo hội thoại là cố ý — khách quay lại sau ba tháng vẫn còn nhãn.
- Danh mục `chat_tag_catalog` (08/09) → chip lọc có nguồn đóng để đổ ra.
- Câu liệt kê `ListConversationsAsync` (ChatRepository.cs:366) đã theo khuôn
  `AND (@x IS NULL OR …)` cho từng bộ lọc, và đã `LEFT JOIN chat_contacts` — tức khoá
  `(channel, contact_external_id)` để nối sang bảng nhãn đã nằm sẵn trong câu.
- Giao diện: thanh lọc giữ `kenhLoc · nhom · loc · tim`, dựng query ở chat-inbox.jsx:2384; thanh
  nhãn `ThanhNhan` (dòng 758) đã có sẵn cách vẽ chip nhãn để dùng lại.

**Còn thiếu.** Tham số `tag` ở `GET /conversations` (dòng 883, hiện nhận `status · search ·
channel · unread · followed · mine`), một mệnh đề `EXISTS` trong SQL, một chip trên thanh lọc.

**Cần chốt trước khi code.** Chọn nhiều nhãn là **VÀ** hay **HOẶC**. Về SQL: HOẶC là
`t.tag = ANY(@tags)`; VÀ là `COUNT(DISTINCT t.tag) = @n`. Đổi về sau là đổi thói quen người dùng.

**Cỡ:** nhỏ. Không đụng schema, không đụng repo khác, con trỏ phân trang giữ nguyên.

---

## 2. Mục 6 — Tên Trang

**Đã có — nhiều hơn bản 1 nghĩ.**
- `chat_conversations.account_id` là một phần khoá hội thoại; `ChatConversation.AccountId`
  (ChatModels.cs:233) đã có nên payload danh sách đã mang mã tài khoản.
- **Tên tài khoản đã được lưu** trong `dbo.TenantChannelSettings.ConfigJson` dưới khoá `label`:
  Messenger ghi `label = tên Trang` (MessengerChatAdapter.cs:507), Zalo ghi tên OA
  (ZaloChatAdapter.cs:693). Không cần bảng mới, không cần hỏi lại nhà cung cấp.

**Còn thiếu — và có một cái bẫy.** Đường duy nhất trả `label` là `GET /channels`, mà đường đó
**gác bằng `CanConfigSystemAsync`** — nhân viên thường gọi là 403. Nên không thể "tra tên ở
giao diện" bằng đường có sẵn. Hai lối:
- (a) trả thêm `accountLabel` ngay trong từng dòng của `GET /conversations` (một `JOIN` hoặc tra
  đệm theo `(channel, account_id)`), giao diện không phải gọi thêm gì;
- (b) một đường nhẹ `GET /chat/accounts` chỉ trả `(channel, accountId, label)`, mở cho mọi người
  có quyền chat.
Tôi nghiêng về (a): tên Trang là thuộc tính của dòng, hiện cùng lúc với dòng.

**Chỗ hiện.** Dòng hội thoại (`.ci-muc-avt` có huy hiệu kênh `.ci-hh`) và đầu khung chat. Chỉ
hiện khi công ty có **từ hai** tài khoản cùng kênh — một Trang thì tên Trang là nhiễu.

**Cỡ:** nhỏ.

---

## 3. Mục 8 — Cỡ biểu tượng

**Đo được.** Ảnh đại diện trong danh sách `.ci-muc-avt` 38×38; huy hiệu kênh `.ci-hh` 11.5px
gắn góc dưới-trái (styles.css:9068, 9115). Lịch sử có hai lần chỉnh vùng này: "huy hiệu kênh
không còn đè lên dòng xem trước" (c41941a) và "sáu kênh không còn làm vỡ dải chọn kênh" (e1af2c3).

**Chưa rõ.** Bảng gốc không nằm trong repo; tracker mobile không có dòng nào về biểu tượng của
chat. **Cần bạn chỉ đúng chỗ**: huy hiệu kênh, ảnh đại diện, biểu tượng thao tác, hay dải chọn kênh.

**Cỡ:** rất nhỏ, thuần CSS, gộp cùng đợt với 7 và 6.

---

## 4. Mục 1 — AI soạn gợi ý trả lời

**Đã có — đây là chỗ bản 1 sai nhiều nhất.** Hộp thư **đã có bot tự trả lời khách**
(tab *Trợ lý*: bật/tắt, lời dặn riêng của công ty, câu chào, im bao nhiêu phút sau khi nhân viên
trả lời, nhớ bao nhiêu tin). Đường sinh câu trả lời `GenerateReplyAsync`
(ChatInboundService.cs:589) đã có:
- khung an toàn (`DefaultSystemPrompt`, đè được bằng `Chat:SystemPrompt`) với luật **cấm bịa**
  giá, lịch, số chỗ; lời dặn của công ty *nối thêm*, không thay thế;
- ngữ cảnh: `HistoryTurns` tin gần nhất qua `ChatRules.BuildConversationPrompt`;
- chọn model theo `AiFeature.ChatInbox`, đếm hạn mức theo công ty, AI hỏng thì **im** chứ không
  gửi rác.
- Bot im khi: khách bị chặn, hội thoại đã đóng, hoặc `BotResumeAt` chưa tới (nhân viên vừa trả
  lời → bot nhường trong `muteMinutes`). Xem `ChatRules.BotMayReply` (ChatRules.cs:289).

**Ý nghĩa cho mục 1.** "AI soạn gợi ý" **không phải tính năng mới** — nó là *cùng bộ sinh đó*,
nhưng đưa kết quả vào ô soạn của nhân viên thay vì gửi thẳng. Và thời điểm cần nó trùng khít
với lúc bot đang im: nhân viên vừa nhảy vào trả lời tay. Nên hình đúng là:
- một nút **Gợi ý** cạnh ô soạn (`.ci-soan`, chat-inbox.jsx:3274), chỉ hiện khi bot đang im
  hoặc đang tắt;
- bấm mới sinh (không tự sinh mỗi tin — tốn tiền cho hội thoại không ai trả lời, và tạo thói
  quen gửi không đọc);
- kết quả **đổ vào ô soạn, không gửi**. Đây là chốt chặn duy nhất cần thêm, và nó là chốt cứng.
- Hộp thư mail đã có khuôn stream nháp (`POST /mail/{id}/reply/draft`, SSE) để dùng lại cách
  hiện chữ chảy; nhưng với tin chat 2–4 câu thì trả một lần cũng đủ, không cần stream.

**Còn thiếu.** Một đường `POST /conversations/{id}/goi-y` gọi lại `GenerateReplyAsync` (cần tách
hàm đó ra khỏi worker để endpoint gọi được), một nút, một ô nhật ký "gợi ý được dùng/bỏ" nếu
muốn đo chất lượng sau này.

**Hai câu để bạn chốt.**
1. Có cho bộ sinh đọc **lịch sử mua của khách CRM** khi hội thoại đã nối (mục 4) không? Hiện bot
   *cố ý chưa* đọc CRM ("Đợt 1 bot chưa tra được CRM"). Cho đọc là bước nhảy chất lượng lớn
   nhất — và cũng là lúc nối nhầm khách trở thành lỗi lộ dữ liệu.
2. Nhân viên có được **sửa lời dặn riêng cho một hội thoại** không, hay chỉ dùng lời dặn chung?

**Cỡ:** nhỏ-vừa. Phần khó là đo chất lượng bằng tay trên dữ liệu thật, không phải phần code.

---

## 5. Mục 4 — Đồng bộ ERP

**Đã có.**
- Nối tay khách chat ↔ khách CRM: `POST /conversations/{id}/link-crm`
  (ChatInboxEndpoints.cs:~1740), có gỡ nối; tìm khách qua `khach.ListAsync(Search)`.
- `chat_contacts` có cột `phone`, `email`, `crm_customer_id`. **Số điện thoại đã được ghi** khi
  kênh cho: Zalo (khi khách chia sẻ số, ZaloChatAdapter.cs:421–442); WhatsApp thì `external_id`
  chính là số điện thoại. Telegram và Messenger **không bao giờ** cho số.
- Phía CRM, ô tìm `filter=` khớp `full_name, email, phone, customer_code, address`
  (CustomerService.cs:435) → tìm theo số là dùng được ngay, không sửa CRM.
- CRM có `POST /customers` (`CreateCustomerRequest`: FullName, PhoneNumber, Email, …) và
  `POST /customercare` (`CreateCustomerCareRequest`: CustomerId, CareTitle, CareDetail, …).

**"Đồng bộ" thật ra là ba việc khác nhau, nên tách:**
1. **Gợi ý nối** — có số ĐT thì tìm CRM theo số, hiện "có thể là khách X, xác nhận?". *Không tự
   nối.* Lý do ghi ngay trong mã: nối nhầm là bot đọc lịch sử mua của người khác rồi nói với
   khách này.
2. **Tạo khách mới từ chat** — khi CRM chưa có, một nút tạo bằng `POST /customers` với tên +
   số + email từ `chat_contacts`. Đây là tiền đề của mục 3.
3. **Đẩy nhật ký chăm sóc** — mỗi lượt nhân viên trả lời / kết thúc hội thoại ghi một dòng CSKH
   vào CRM qua `POST /customercare`. Cần chốt: ghi theo *hội thoại* (một dòng khi đóng) hay theo
   *lượt* (nhiều dòng)? Theo lượt là ồn; theo hội thoại là mất chi tiết.

**Cỡ:** vừa. Việc 1 và 2 nhỏ; việc 3 phụ thuộc câu hỏi trên.

---

## 6. Mục 3 — Tạo Cơ hội bán hàng từ hội thoại

**Sửa lại so với bản 1: KHÔNG cần API mới bên CRM.** `POST /api/bookingtickets` đã có
(BookingTicketController.cs:71) với `CreateBookingTicketRequest`: `TenKH, SoDienThoaiKH,
EmailKH, TenPhieu, NoiDungPhieu, NguoiPhuTrachs, NguonPhieu, IdKhachHang, CustomerSourceId,
MarketId, TepDinhKems, …`. Proxy đã có lối ghi sang CRM (`api.PostAsync`, dùng ở tour/NCC).

Nhắc lại bẫy tên: theo `toutkit-app/docs/module-mapping.md`, **Cơ hội bán hàng = BookingTicket**.

**Ba ràng buộc thật, đọc từ mã:**
1. **Bắt buộc `IdKhachHang > 0` và `TenKH`** — tức hội thoại phải đã nối với khách CRM (mục 4,
   việc 1 hoặc 2) *trước*. Đây là phụ thuộc cứng, không phải thứ tự cho đẹp.
2. **CRM không kiểm quyền ở `CreateAsync`** — chỉ `CH_XEM/CH_XEM_ALL` khi xem và `CH_SUA` khi
   sửa được kiểm (BookingTicketService.cs:262, 822). Web cũ chắc chắn kiểm quyền thêm phiếu
   ở tầng màn hình. Vậy proxy **phải tự kiểm** quyền thêm phiếu từ phiên (đã có
   `TkSessionStore.HasPermission`) trước khi gọi, nếu không thì bất kỳ ai vào được hộp thư
   chat đều tạo được Cơ hội. Cần xác nhận mã quyền đúng (`CH_THEM`?) với bên CRM.
3. **`NguonPhieu` là mã số** (web cũ dùng `3` cho đại lý, TourBo.cs:1113), chưa có mã cho
   "từ chat". Muốn báo cáo lọc được "cơ hội đến từ chat" thì cần **một mã mới thống nhất với
   CRM** — đây là chỗ duy nhất cần bên CRM gật đầu, và không cần deploy gì.

**Dữ kiện đáng nối.** Tracker mobile có dòng "*Màn tạo Cơ hội thiếu file đính kèm — không đính
kèm được thông tin chat khách*" (Defer). Tức ý ban đầu của người dùng là **kéo nội dung chat
sang Cơ hội**. Cách rẻ và đúng: `NoiDungPhieu` = tóm tắt hội thoại + đường dẫn về hội thoại;
đính kèm tệp (`TepDinhKems`, R2) để đợt sau như tracker đã tách.

**Cỡ:** vừa — nhỏ hơn bản 1 (không có phần API CRM), nhưng thêm phần kiểm quyền.

---

## 7. Mục 2 — Chấm cảm xúc

**Chưa có gì**, và bản tham chiếu ChatbotX cũng **không có** (đã soát: không có sentiment,
không có suggest).

**Bẫy tên.** Mã đã có `camXuc` trong hộp thư — đó là **biểu tượng cảm xúc khách thả trên tin**
(reactions của kênh), không phải chấm cảm xúc. Đặt tên khác hẳn.

**Vướng lớn hơn bản 1 nêu.** Hộp thư **không có khái niệm ưu tiên/khẩn**: không cột, không
sắp xếp nào ngoài `last_activity_at`, không cờ nào trên dòng. Chấm cảm xúc xong thì con số đó
*không có chỗ để rơi vào*. Muốn làm thì phải dựng thêm: một cột trên hội thoại, một cách sắp xếp
hoặc một cờ, một chỗ hiện — và một lượt gọi AI **cho mỗi cụm tin đến** (tốn lượt cho cả hội
thoại không ai xem).

**Câu hỏi chặn vẫn nguyên:** chấm để làm gì? Chưa có hành động thì chưa nên làm. Xếp cuối.

---

## 8. Chỗ trống trong spec cũ

Mục 5 không nằm trong danh sách làm lẫn danh sách hoãn của spec 07/09. Tracker mobile không
phải bảng gốc nên không tra được. Cần mở bảng "Chat đa kênh" xem mục 5 là gì.

---

## 9. Bản 1 sai ở đâu

| Bản 1 nói | Thực tế | Hệ quả |
|---|---|---|
| Mục 3 "cần API mới bên CRM, phải deploy" | `POST /bookingtickets` đã có | rẻ hơn; thay bằng việc kiểm quyền ở proxy |
| Mục 1 "còn thiếu luật khi nào gợi ý" | bot đã sinh câu trả lời, đã có luật im | là *đưa ra cho người*, không phải dựng mới |
| Mục 6 "cần chỗ lưu tên Trang" | tên đã lưu ở `label` | chỉ còn đường tra cho nhân viên thường |
| Mục 4 "chỉ có nối tay" | đã có số ĐT, CRM tìm được theo số, CRM có API CSKH | tách ba việc, hai việc nhỏ |
| Mục 2 xếp giữa | không có chỗ đặt kết quả | xuống cuối |

---

## 10. Việc cần bạn quyết

1. Mục 7: nhiều nhãn → **VÀ** hay **HOẶC**?
2. Mục 8: chỗ nào sai cỡ?
3. Mục 1: cho bộ sinh **đọc CRM** khi hội thoại đã nối không? Nhân viên sửa được lời dặn riêng không?
4. Mục 4: đẩy CSKH theo **hội thoại** hay theo **lượt**?
5. Mục 3: mã quyền thêm phiếu, và có cấp **mã `NguonPhieu` mới** cho "từ chat" không?
6. Mục 5 trong bảng gốc là gì?
7. Đồng ý thứ tự **7+6+8 → 1 → 4 → 3 → 2** chứ?
