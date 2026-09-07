# Hộp thư chat — phân công và phân quyền xem

**Ngày:** 07/09/2026 · **Trạng thái:** thiết kế, chưa code
**Nguồn yêu cầu:** Sheet "Chat đa kênh" (Bug App), mục 9 · 10 · 11 · 12 · 13
**Liên quan:** [chat-inbox.md](../../features/chat-inbox.md) ·
[2026-08-20-omnichannel-chat-design.md](2026-08-20-omnichannel-chat-design.md)

---

## 1. Vấn đề

Hộp thư chat hiện cho **mọi người trong công ty xem mọi hội thoại**. `ChatInboxEndpoints` dài
2.527 dòng và **không có một dòng kiểm quyền nào** — ai đăng nhập được là đọc được hết, kể cả
hội thoại của đồng nghiệp.

Việc gán người thì đã có (`chat_conversations.assigned_username`, endpoint `/assign`), nhưng
thiếu hai thứ khiến nó không dùng được:

- **Không có danh sách người để chọn.** Endpoint chuyển việc nhận một chuỗi tên đăng nhập và
  **không kiểm tên đó có thật hay không** — gõ sai một ký tự là hội thoại gán vào hư không.
- **Không có cách chia việc tự động.** Đội đông thì hoặc tranh nhau một khách, hoặc bỏ quên khách.

## 2. Phạm vi

**Làm trong đợt này** — sheet mục 9 (nhãn nút), 10 (ô chọn người phụ trách), 11 (phân quyền xem),
12 + 13 (chế độ phân công).

**KHÔNG làm trong đợt này** — mục 1, 2 (AI soạn gợi ý, chấm cảm xúc), mục 3, 4 (tạo Cơ hội bán
hàng, đồng bộ ERP), mục 6, 7, 8 (tên Trang, lọc nhãn, cỡ biểu tượng). Mỗi cụm một đợt riêng.

---

## 3. Những gì ĐÃ CÓ — không dựng lại

Ba thứ tưởng phải làm mới, thật ra đã chạy:

**Phiên đã biết mã nhân viên.** `TkSession.CrmUserId` lấy từ claim `user_id` trong JWT của ERP,
qua [`JwtClaims`](../../../TourkitAiProxy.Infrastructure/TourKit/JwtClaims.cs). Cụm bản tin đã
dùng từ trước. **Không cần sửa API ERP, không cần deploy TourKit.Api.**

⚠️ Chú thích hiện tại của `JwtClaims` viết *"KHÔNG dùng hàm này để quyết định quyền truy cập"* —
lý do là nó không kiểm chữ ký. Thiết kế này **không phạm**: quyền đọc từ `session.CrmUserId` đã
chốt trên máy chủ lúc đăng nhập, không giải mã token do trình duyệt gửi lên. Khi sửa phải ghi rõ
sự phân biệt đó vào chú thích, không để người sau đọc thấy mâu thuẫn rồi tự nới ra.

**JWT còn mang claim `is_admin`** (`AuthService.cs:229`), `JwtClaims` mới đọc `user_id` — thêm một
hàm nữa là đủ, không phải dựng mã quyền mới.

**Một cửa chung cho 26 endpoint.** Cả 26 route `/conversations/{id…}` đều gọi
`GetConversationAsync` ở dòng đầu. Chặn ở đó là phủ hết, không phải vá 26 chỗ.

**Khoá nhận việc nguyên tử đã có** — `ClaimConversationAsync` (ChatRepository.cs:555) là một lệnh
cập nhật có điều kiện; người thứ hai nhận 409 kèm tên người đang giữ.

---

## 4. Quyết định đã chốt

| Câu hỏi | Chốt | Vì sao |
|---|---|---|
| Bao nhiêu chế độ | **Hai**: phân công thủ công · xoay vòng | Ảnh mẫu có bốn, sheet mô tả hai. Thêm chế độ sau không phải đập đi làm lại |
| Người phụ trách | **Một người** mỗi hội thoại | Giữ khoá nguyên tử chống hai người cùng trả lời một khách |
| Khoá định danh | **Mã nhân viên** (`user_id`), hiển thị tên đầy đủ | Đã có sẵn trong phiên |
| Ai xem được gì | **Admin xem tất cả · không admin chỉ xem hội thoại được phân công cho mình** | Quyết định của chủ dự án 07/09/2026 |

⚠️ **Hệ quả của luật xem, ghi lại để đợt sau không tưởng là lỗi:**

- Chế độ thủ công **không phải "nhân viên tự nhận"** mà là **admin giao việc** — nhân viên không
  nhìn thấy hội thoại chưa gán nên không tự bốc được. Nhãn trên giao diện phải gọi đúng là
  *"Phân công thủ công"*; để chữ "tự phân công" là người mua hiểu sai rồi thắc mắc sao đội ngồi im.
- Công tắc *"ai trả lời trước thì thành người phụ trách"* **chỉ có tác dụng với admin**, vì chỉ
  admin mở được hội thoại chưa gán.
- Nút *"Nhận chăm sóc"* **ẩn với nhân viên thường** — hội thoại họ mở được thì đã là của họ rồi,
  để nút đó bấm không ra gì là trông như lỗi.

---

## 4b. Luật một khoá: quyết định bằng mã, hiển thị bằng tên

> Chốt 07/09/2026, sau khi chủ dự án chỉ ra sự nhập nhằng — và sau khi cùng một gốc đẻ ra hai lỗi
> ở hai chỗ khác nhau.

**`assigned_user_id` là khoá DUY NHẤT để quyết định.** Quyền xem, khoá chống tranh việc, mọi bộ
lọc — chỉ đọc cột này.

**`assigned_username` chỉ để HIỂN THỊ và cho nhật ký cũ.** Không bao giờ được đọc để quyết định
bất cứ điều gì. Vẫn ghi khi biết, vì nó rẻ và giữ cho dòng nhật ký cũ còn nghĩa.

⚠️ **Vì sao thành luật.** Hai cột cùng mang nghĩa "ai là chủ" thì sinh ra BA trạng thái dòng —
*(tên có, mã trống)* từ dữ liệu cũ, *(tên có, mã có)* từ tự nhận việc, *(tên trống, mã có)* từ
chuyển việc và xoay vòng. Mỗi câu truy vấn chỉ đọc MỘT cột sẽ đúng với hai trạng thái và sai với
trạng thái thứ ba, **im lặng**. Hai lỗi đã xảy ra từ đúng gốc này:

- khoá chống tranh việc so theo tên → dòng *(tên trống, mã có)* làm mệnh đề luôn đúng → người thứ
  hai bấm nhận việc **thắng, không có 409**, người đang giữ mất việc mà không hay biết;
- bộ lọc "chỉ của tôi" so theo tên → hội thoại do xoay vòng gán có tên trống nên lọt vào bộ lọc
  của **mọi người**.

⚠️ **Phần còn lại của hộp thư vẫn khoá theo TÊN, và đó là cố ý.** Theo dõi, dấu đã đọc, nhật ký
thao tác đều dùng tên đăng nhập. Chúng là *dấu riêng của từng người* và *lịch sử*, không phải
quyết định về quyền sở hữu — nên không nằm dưới luật này. Đừng "chuẩn hoá" chúng sang mã: dấu đã
đọc chuyển khoá là mất sạch dấu, còn nhật ký chuyển khoá là mọi dòng cũ mất nghĩa.

⚠️ **Không cần sửa API danh sách nhân viên.** Nó đã trả mã + tên hiển thị. Chọn khoá là mã nghĩa
là bên ERP không phải đụng gì.

⚠️ **Dữ liệu cũ lấp dần khi người dùng đăng nhập**, không chạy script một lượt. Hệ chỉ biết cặp
tên ↔ mã của người ĐÃ đăng nhập, nên lấp theo từng người là cách duy nhất không phải tra ngược ra
ngoài. Ai chưa bao giờ đăng nhập thì hội thoại của họ thành chưa-ai-phụ-trách — hợp lý, vì họ
cũng không mở được hộp thư.

---

## 5. Dữ liệu

Ba thay đổi trong CSDL chat (PostgreSQL), **đều là thêm**, không sửa và không xoá cột cũ — theo
đúng lối `ALTER TABLE … ADD COLUMN IF NOT EXISTS` mà `ChatDb.SchemaSql` đang dùng.

### 5.1 `chat_conversations` — thêm một cột

```sql
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS assigned_user_id integer;
CREATE INDEX IF NOT EXISTS ix_conv_tenant_nguoi_phutrach
  ON chat_conversations (tenant_id, assigned_user_id, last_activity_at DESC);
```

`assigned_user_id` là **khoá thật** để so quyền. `assigned_username` giữ nguyên: nó là thứ hiển
thị trong nhật ký cũ, và bỏ đi thì mọi dòng `chat_audit` đã ghi mất nghĩa. Ghi mới điền cả hai.

### 5.2 `chat_assign_settings` — một dòng mỗi công ty

| Cột | Kiểu | Nghĩa |
|---|---|---|
| `tenant_id` | text PK | |
| `mode` | smallint | 1 = phân công thủ công · 2 = xoay vòng |
| `scope_own_only` | boolean | bật = không admin chỉ xem hội thoại của mình |
| `auto_assign_on_reply` | boolean | bật = ai trả lời trước thì thành người phụ trách |
| `member_ids` | integer[] | **đội trực chat** — mã những người được nhận hội thoại |
| `rotation_last_user_id` | integer | mã người **vừa nhận lượt**, không phải vị trí |
| `updated_utc` | timestamptz | |

⚠️ **Chưa có dòng = giữ nguyên hành vi hiện tại** (mọi người xem tất cả, không gán tự động). Đây
là đường lùi cho khách đang chạy: nâng cấp mà không ai bị mất hộp thư sáng hôm sau.

⚠️ **Con trỏ lưu MÃ NGƯỜI, không lưu vị trí trong danh sách.** Lưu số thứ tự thì thêm hoặc bớt
một người là cả vòng lệch — chị A nhận gấp đôi, anh B không nhận cái nào, và **không ai biết vì
sao**. Lưu mã người vừa nhận lượt thì lượt sau chỉ việc lấy người kế tiếp theo thứ tự mã.

### 5.3 Đội trực chat là một **danh sách số**, không phải một bảng

`member_ids integer[]` nằm ngay trong `chat_assign_settings`. Một danh sách cho hai việc: **vòng
quay** chia theo nó, và **ô chọn người phụ trách** đổ ra nó. Hai danh sách riêng thì sớm muộn
lệch nhau, mà lệch im lặng.

Vì sao **không** tách bảng: sau khi khoá định danh đổi sang mã người thì mỗi thành viên chỉ còn
đúng **một con số**. Tên hiển thị lấy từ danh sách nhân viên của ERP mà giao diện vốn đã nạp —
lưu lại là nhân đôi chỗ phải cập nhật khi ai đó đổi tên. Cờ "tạm nghỉ" cũng thừa: bỏ khỏi danh
sách và tắt cờ là **cùng một thao tác, cùng một kết quả**.

Đổi lại được một thứ có giá: "chia kiểu gì" và "chia cho ai" nằm cùng một dòng, nên đường nhận
tin chỉ đọc CSDL một lượt thay vì hai.

Vòng quay vẫn ổn định vì con trỏ lưu **mã người**: lấy mã nhỏ nhất lớn hơn con trỏ, hết thì quay
về mã nhỏ nhất. Thêm hoặc bớt người ở giữa không làm lệch vòng.

---

## 6. Chặn quyền xem

### 6.1 Chặn bằng chữ ký hàm, không bằng kỷ luật

```csharp
GetConversationAsync(tenant, id, NguoiXem nguoiXem, ct)
```

`NguoiXem` mang `CrmUserId` + `XemTatCa`. Không được xem → trả `null` → 26 endpoint sẵn có đã trả
404 sẵn, không sửa nhánh nào.

⚠️ Chọn **đổi chữ ký** thay vì thêm một hàm gác gọi riêng: ai viết endpoint mới mà quên thì **lỗi
biên dịch**, không phải lỗi bảo mật im lặng phát hiện sau sáu tháng.

### 6.2 Trả 404, không trả 403

403 nghĩa là *"có hội thoại này nhưng anh không được xem"* — tức xác nhận đúng cái đang giấu. Dò
tuần tự theo id là biết công ty có bao nhiêu khách và ai đang chăm khách nào.

### 6.3 Luật

```
xem được  ⇔  XemTatCa                      (admin, hoặc scope_own_only tắt)
          ∨  assigned_user_id = CrmUserId
```

Áp cùng luật ở `ListConversationsAsync` (mệnh đề `WHERE`), ở đếm chưa đọc, và ở luồng sự kiện.

### 6.4 Luồng sự kiện — chỗ dễ quên nhất

[`ChatEventBus`](../../../TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs) kẹp tenant **bên
trong bus**, và chú thích của nó nói rõ lý do: *"lọc ở endpoint thì một lần quên là rò rỉ chéo
tenant"*. Kẹp người xem đi đúng chỗ đó — người nghe đăng ký kèm `CrmUserId` + `XemTatCa`.

Không làm thì nhân viên bị giới hạn vẫn nhận chuông báo tin mới của hội thoại họ bấm vào ra 404:
vừa lộ đang có việc xảy ra, vừa trông như app hỏng.

---

## 7. Chế độ phân công

### 7.1 Chế độ 1 — Phân công thủ công

Máy không gán gì. Ai mở được hội thoại thì tự nhận, hoặc giao cho người khác qua `/assign` sẵn
có. Theo luật xem ở mục 6.3, hội thoại **chưa gán** chỉ admin mở được — nên trên thực tế đường
giao việc ở chế độ này là **admin giao xuống**, còn nhân viên chỉ nhận.

Thêm **kiểm tra mã người nhận phải nằm trong `member_ids`** — hiện endpoint nhận bất kỳ
chuỗi nào và gán xong im lặng.

### 7.2 Chế độ 2 — Xoay vòng

Gán khi hội thoại **chưa có người phụ trách**. Móc vào `GetOrCreateConversationAsync`
(ChatRepository.cs:244).

⚠️ Điều kiện là *"chưa ai phụ trách"*, **không phải** *"vừa tạo mới"*. `GetOrCreate…` chạy lại ở
mỗi tin khách gửi, và hội thoại đóng rồi khách nhắn lại vẫn phải có người. Điều kiện này bao cả
hai, và chạy bao nhiêu lần cũng ra một kết quả.

⚠️ **Hai tin cùng lúc là chỗ hỏng thật.** Hai luồng cùng đọc con trỏ, cùng thấy chưa ai phụ
trách, cùng gán → hai người khác nhau, cái sau đè cái trước, khách nhận hai lời chào. Nên việc
quay con trỏ và việc gán đi trong **MỘT câu lệnh SQL** mang điều kiện `assigned_user_id IS NULL`,
không phải đọc rồi ghi.

⚠️ **Đội trực rỗng thì không gán, và phải nói ra.** Bật xoay vòng mà quên tick người là mọi hội
thoại rơi về hàng chờ, trông y hệt chế độ thủ công — không ai đoán được nguyên nhân. Màn hình
cấu hình cảnh báo ngay tại chỗ.

### 7.3 Công tắc "ai trả lời trước thì thành người phụ trách"

Nằm ở đường gửi tin: chưa ai phụ trách mà có người gõ trả lời thì người đó nhận luôn — dùng lại
`ClaimConversationAsync` nguyên tử, nên hai người cùng gõ vẫn chỉ một người thành chủ.

---

## 8. Giao diện

| Chỗ | Đổi gì |
|---|---|
| Thanh tiêu đề hội thoại | Thêm **ô chọn người phụ trách** bên trái nút nhận việc (đúng vị trí ảnh mẫu). Danh sách = `member_ids`, tên hiển thị lấy từ danh sách nhân viên ERP |
| Nút nhận việc | "Nhận việc" → **"Nhận chăm sóc"** nền đỏ; đã nhận → **"Đã nhận chăm sóc"** nền xanh. Ẩn với nhân viên thường |
| Cột Phụ trách | Đang in thẳng tên đăng nhập (`admin`, `nv01`) → đổi sang **tên đầy đủ**, cả ở danh sách lẫn bảng bên phải |
| Màn hình cấu hình | Cùng chỗ Cấu hình trợ lý. Ba thứ: chọn chế độ · tick đội trực · công tắc trả-lời-trước |

Cờ tính năng `Features:ChatAssign`, mặc định **tắt** — theo quy ước mỗi tính năng mới một cờ
riêng. Tắt thì giao diện giữ nguyên như hôm nay.

---

## 9. Test

Bộ hiện có hơn 800 test logic thuần, chạy dưới 1 giây. Thêm:

- **Guard kiến trúc** — quét `ChatInboxEndpoints`, liệt kê mọi route `/conversations/{id…}` và bắt
  buộc chúng đi qua cửa chung. Thêm route mới mà quên → test đỏ, không đợi khách phát hiện.
- **Luật xem** — admin thấy tất cả; không admin chỉ thấy của mình; hội thoại chưa gán **không**
  hiện với không admin (đây là quyết định, không phải sơ suất — test khoá nó lại).
- **404 chứ không 403** khi xem hội thoại của người khác.
- **Vòng quay** — thứ tự ổn định; thêm/bớt người không làm lệch vòng; đội rỗng thì không gán;
  hai lượt đồng thời chỉ ra một người.
- **Không admin không gán được việc cho người ngoài đội trực.**

---

## 10. Việc phải làm kèm

- `CHANGELOG.md` — bắt buộc, viết cho người dùng cuối, không tên bảng/hàm.
- `docs/features/chat-inbox.md` — thêm mục phân công và phân quyền.
- Ngày giờ theo UTC kèm `Z` (xem `docs/datetime-convention.md`).

## 11. Rủi ro

**Bật nhầm là cả đội mất hộp thư.** Công ty đang dùng mà bật `scope_own_only` trong khi chưa gán
hội thoại nào thì mọi nhân viên mở ra thấy trống. Giảm nhẹ: mặc định tắt, và màn hình cấu hình
nói rõ hậu quả **trước** khi lưu, kèm số hội thoại hiện chưa có người phụ trách.

**Đổi chữ ký `GetConversationAsync` chạm 26 chỗ gọi.** Cơ học, không rủi ro logic — nhưng phải
chạy `codegraph impact GetConversationAsync` trước khi sửa và sửa hết trong một lần.
