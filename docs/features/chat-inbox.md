# Hộp thư chat đa kênh

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

---

## Hộp thư chat đa kênh (`Features:Chat`)

Khách nhắn Zalo OA → tin vào hộp thư trong app → bot trả lời → nhân viên tiếp quản. Spec + kế hoạch:
[specs/2026-08-20-omnichannel-chat-design.md](../superpowers/specs/2026-08-20-omnichannel-chat-design.md) ·
[plans/2026-08-20-omnichannel-chat-dot1.md](../superpowers/plans/2026-08-20-omnichannel-chat-dot1.md).
Nghiệp vụ tham khảo [ChatbotX](https://github.com/ChatbotXIO/ChatbotX) (MIT) — **đọc để lấy nghiệp vụ,
KHÔNG chạy như phụ thuộc**.

⚠️ **CSDL RIÊNG, PostgreSQL — không phải SQL Server.** `ConnectionStrings:Chat` trỏ tới PostgreSQL 18
(Google Cloud SQL, instance dùng chung với dự án `farmer`). Lý do tách: `pgvector` để sau này tìm hội
thoại theo ngữ nghĩa — SQL Server 2022 không có kiểu vector. Cái giá: **không `JOIN` được** với
khách/tour bên SQL Server và **không có giao dịch chung** — ghi tin nhắn trước, cập nhật CRM sau và cho
thử lại. Schema ở [`ChatDb.SchemaSql`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs); thiếu chuỗi kết nối thì cụm chat
tự tắt, KHÔNG làm sập app.

**Ba kênh, MỘT đường dẫn webhook:** `POST /api/v1/chat/webhook/{kenh}/{tenantId}` với `kenh` ∈
`zalo` · `messenger` · `telegram` — **công khai** (kênh gọi tới, không có phiên), gác bằng chữ ký.
Viết riêng từng kênh thì phần chung (đọc thân thô, kiểm chữ ký, trả 200 ngay, xử lý nền) bị chép ba
lần và sớm muộn lệch nhau. Messenger cần thêm `GET` cùng đường dẫn cho bước Meta xác minh
(`hub.challenge`) — thiếu là không đăng ký được webhook dù phần nhận tin đã đúng.

**Zalo dùng MỘT ứng dụng của TourKit cho mọi khách hàng** (`Chat:Zalo` trong cấu hình). Khách chỉ
bấm **"Kết nối Zalo OA"**, đăng nhập Zalo, chọn OA của mình, bấm đồng ý. Không App ID, không khoá
bí mật, không callback, không webhook.

⚠️ **Vì sao phải đổi:** mô hình cũ bắt MỖI công ty tự tạo một ứng dụng trên `developers.zalo.me` —
tám bước kỹ thuật trước khi nhắn được tin đầu tiên. Không công ty du lịch nào làm nổi, và không
sửa được bằng cách viết hướng dẫn hay hơn. (ChatbotX cũng đặt khoá ứng dụng ở cấp nền tảng:
`apps/builder/src/features/platform-credentials/zalo`.)

⚠️ **Định tuyến webhook đổi hẳn cách làm việc.** Ứng dụng dùng chung thì `app_id` **giống hệt nhau**
ở mọi khách, nên nó không còn phân biệt được ai với ai, và URL webhook cũng không mang tên công ty
nữa (`/api/v1/chat/webhook/zalo`, khai một lần trong ứng dụng của TourKit). Khoá định tuyến duy
nhất còn lại là **id OA** — mà id đó chính là `accountId` của tài khoản, nên tra ngược ra công ty
chỉ mất một phép so trên cột `Channel`.

⚠️ **Zalo không đặt id OA vào một chỗ cố định** — đây là cái bẫy: sự kiện gắn nhãn có `oa_id` riêng;
tin khách gửi và "đã xem" thì OA là **người nhận**; tiếng vọng OA gửi thì OA là **người gửi**. Lấy
nhầm đầu là tra ra id của khách, không khớp công ty nào, tin rơi vào hư không mà chỉ còn một dòng
log. Có test cho cả bốn dạng.

⚠️ **Tra được công ty KHÔNG có nghĩa là tin thật.** Id OA không phải bí mật, ai biết đường dẫn cũng
đoán được — nên sau khi tra vẫn phải kiểm chữ ký bằng khoá của chính tài khoản đó.

⚠️ **Đường cũ mang tên công ty vẫn sống** (`/webhook/zalo/{tenantId}`) cho các OA đã khai theo ứng
dụng riêng. Bỏ đi là webhook đang chạy của họ chết ngay lúc deploy. Khoá lùi theo TỪNG Ô: tài khoản
nào có khoá riêng thì dùng khoá riêng, thiếu ô nào mới lấy từ cấu hình nền tảng.
⚠️ **Zalo cấp HAI khoá bí mật khác nhau, đừng dùng lẫn.** `secretKey` = **App Secret Key** (Ứng
dụng → Cài đặt) dùng ở header khi đổi token, tức đường **GỬI**. `oaSecretKey` = **OA Secret Key**
(Sản phẩm → Official Account → Cài đặt chung) dùng kiểm chữ ký webhook, tức đường **NHẬN**. Một ô
cho cả hai thì luôn có một chiều hỏng, mà thông báo lỗi không nói ra điều đó — chỉ thấy "tin khách
không vào hộp thư". Ô `oaSecretKey` để trống thì lùi về `secretKey`, nên cấu hình cũ không gãy.

**Refresh Token lấy bằng nút "Cấp quyền OA", không phải chép tay.** Zalo không cho copy Refresh
Token từ giao diện của họ; nó chỉ ra sau một vòng OAuth: mở `/v4/oa/permission` → quản trị viên OA
bấm đồng ý → Zalo đá về callback kèm `code` sống rất ngắn → đổi `code` lấy token. Làm tay thì phải
chép `code` trên thanh địa chỉ rồi gọi `curl`; ở đây là một nút.

⚠️ **Đường callback CÔNG KHAI nên không có phiên.** Zalo đá trình duyệt về bằng chuyển hướng
thường — không mang `X-Session-Id`. Ghép lại công ty/tài khoản bằng `state` **do máy chủ sinh**
(32 byte ngẫu nhiên, dùng một lần, sống 10 phút). Để client tự khai tenant trên URL callback thì ai
biết đường dẫn cũng nhét được refresh token của OA mình vào công ty khác, rồi đọc và trả lời tin
của khách công ty đó — rò rỉ chéo tenant, thứ nặng nhất trong danh sách rủi ro của spec.

⚠️ **`redirect_uri` phải khớp Y HỆT chuỗi khai ở ô Official Account Callback URL bên Zalo** — lệch
một dấu gạch chéo là Zalo từ chối và câu lỗi của họ không nói lệch ở đâu. Vì thế chuỗi này sinh MỘT
lần rồi giữ luôn trong `state`, lượt đổi mã dùng lại đúng chuỗi đó chứ không dựng lại.

**Messenger cũng dùng MỘT ứng dụng Facebook của TourKit** (`Chat:Messenger` trong cấu hình), cùng
lối với Zalo và **dễ hơn Zalo một bậc**. Khách bấm **"Kết nối Facebook"**, đăng nhập, chọn Trang —
hết. Đường dẫn: `POST /channels/1/connect-url` → `dialog/oauth` → `GET /api/v1/chat/oauth/messenger/callback`
→ trang chọn Trang → `POST /api/v1/chat/oauth/messenger/chon`.

⚠️ **Facebook KHÔNG có cửa gói như Zalo.** Meta không thu tiền, và ứng dụng ở chế độ **Development**
đã nhận/gửi tin thật với Trang mà người kết nối là quản trị viên. App Review + xác minh doanh nghiệp
(vẫn miễn phí) chỉ cần khi mở cho Trang của khách hàng khác. Zalo thì Open API nằm sau gói trả tiền —
đó là lý do nửa nhận của hộp thư kiểm được trên Facebook trước.

⚠️ **`subscribed_apps` là thứ biến cả bước nối thành một nút.** Sau khi chọn Trang, hệ thống tự gọi
`POST /{pageId}/subscribed_apps` để bật nhận tin — khách **không vào màn hình quản trị Meta lần**
**nào**. Zalo không có cái tương đương. Danh sách `subscribed_fields` phải khớp những gì `Parse` bóc:
thiếu `message_echoes` là mất tin nhân viên trả lời từ ứng dụng Meta, thiếu `message_deliveries`/
`message_reads` là tin gửi đi không bao giờ leo lên hai tích. Cả hai hỏng **âm thầm**; có test canh.

⚠️ **Đổi user token sang bản DÀI hạn TRƯỚC khi gọi `/me/accounts`.** Page token lấy ra từ user token
ngắn hạn cũng chỉ sống vài giờ; lấy ra từ user token dài hạn thì **không hết hạn**. Làm ngược thứ tự
thì vài giờ sau cả hộp thư ngừng gửi được, mà Meta chỉ nói "session expired" — không ai đoán ra
nguyên nhân nằm ở thứ tự hai lệnh gọi. Có test canh thứ tự.

⚠️ **Bước chọn Trang là bắt buộc, không bỏ được.** Zalo hỏi `getoa` ra đúng một OA nên nối xong ngay
trong callback; Meta trả `/me/accounts` — một người có thể quản trị chục Trang, kể cả Trang chẳng
liên quan. Nối bừa Trang đầu danh sách là sai, nối hết còn tệ hơn. Trang picker là **HTML dựng tay**
([`TrangChonTrang`](../../TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs)) vì nó chạy trong cửa sổ Meta vừa đá về:
không có phiên nên không nạp được ứng dụng React chính. Danh sách Trang + token giữ ở máy chủ
([`MessengerPageChoices`](../../TourkitAiProxy.Services/Chat/Channels/MessengerPageChoices.cs)), trình duyệt chỉ cầm một mã tra
cứu vô nghĩa — và **chỉ nối được Trang nằm trong danh sách của mã đó**, không thì ai cầm mã cũng nối
được Trang bất kỳ bằng cách đoán một id.

⚠️ **Quyền xin ở mức tối thiểu:** `public_profile` · `pages_show_list` · `pages_messaging` ·
`pages_read_engagement` · `pages_manage_metadata` (cái cuối để gọi `subscribed_apps`). ChatbotX xin 12
quyền vì họ còn quản bài đăng và quảng cáo; mỗi quyền thừa là một mục phải giải trình khi Meta duyệt
và một dòng đáng ngờ trên màn hình khách bấm đồng ý. Có test chặn việc âm thầm xin thêm.

⚠️ **Lúc dev, `Chat:PublicBaseUrl` là bắt buộc.** Mặc định URL webhook và callback lấy theo địa chỉ
của chính yêu cầu đang tới — trên máy chủ thật thì đúng, còn ở máy dev là `localhost`, mà Zalo/Meta
**không gọi vào `localhost`** được. Chạy đường hầm rồi dán URL `https` vào khoá đó.

⚠️ **Zalo xoay vòng refresh token.** Mỗi lượt đổi trả về một refresh token MỚI, phải lưu cái mới và
bỏ cái cũ — dùng lại cái cũ ở lần sau là bị từ chối. Cả hai lượt (đổi `code` lần đầu và làm mới về
sau) đi chung một hàm nên không có chỗ nào quên lưu.
**Telegram nối bằng MỘT nút, giống Zalo/Messenger** (đổi 27/08). Dán bot token → máy chủ gọi
`getMe` xác thực → **tự sinh** chuỗi bí mật webhook → gọi `setWebhook`. Gỡ kết nối gọi
`deleteWebhook`. Vào từ chính `POST|PUT|DELETE /channels/3/accounts[/{id}]`, không thêm đường mới
([`NoiBotAsync`](../../TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs)).

⚠️ **Xác thực token TRƯỚC khi đăng ký webhook, không được ngược.** Đăng ký trước rồi mới biết
token sai là đã trỏ một địa chỉ công khai vào một bot không tồn tại, và bản ghi rác nằm lại trong
danh sách kênh. Có test canh thứ tự.

⚠️ **`allowed_updates` phải khai ĐỦ — đây là bẫy "sửa hai chỗ" của Telegram.** Telegram CHỈ gửi
những loại nằm trong danh sách khai lúc `setWebhook`, và **danh sách mặc định của họ đã bỏ sẵn**
**`message_reaction`**. Viết mã bóc cảm xúc mà quên khai là không bao giờ có gói tin nào tới:
không lỗi, không log, chỉ là một thứ không xảy ra. Đang khai năm loại: `message` ·
`edited_message` · `callback_query` · `message_reaction` · `my_chat_member`. Có test canh đủ năm.

⚠️ **Chuỗi bí mật webhook do MÁY CHỦ sinh, KHÔNG còn là ô nhập.** Để lại ô đó thì người khai vẫn
tưởng phải làm tay, mà giá trị họ gõ sẽ đè lên chuỗi máy chủ vừa sinh → webhook chết im lặng.
Dùng base64url (`A-Z a-z 0-9 _ -`), không phải base64 thường: một dấu `+` hay `/` lọt vào là
Telegram từ chối mà không nói vướng ở đâu.

⚠️ **Telegram gói MỖI loại đính kèm vào MỘT trường tên khác nhau** — không có trường chung nào cho
biết "tin này có tệp". Thiếu một nhánh trong `Parse` là loại đó rơi xuống `ChatKind.Chu` với nội
dung `null`: một **dòng trắng** trong hộp thư vẫn đẩy hội thoại lên đầu và vẫn tính chưa đọc.
Đã dính thật với `video` và `audio` (đối chiếu ChatbotX 27/08 mới lộ). Nay bắt `photo` · `video` ·
`video_note` · `audio` · `document` · `voice` · `location` · `sticker`; loại lạ thì **bỏ qua** kèm
log WARNING liệt kê các trường trong gói.

⚠️ **Bấm nút phải gọi `answerCallbackQuery`, không thì nút QUAY VÒNG mãi** trên máy khách dù mình
đã xử lý xong. Chỉ biết được điều này khi đọc ChatbotX. Gọi **ngay đầu** `MotSuKienAsync` qua
`IChatChannelAdapter.XacNhanBamNutAsync` (mặc định rỗng — Zalo/Messenger không có khái niệm này),
trước cả bước gọi AI vốn mất vài giây. Lượt bấm ghi lại bằng **chữ trên nút** (dò `reply_markup`
theo `callback_data`), lùi về mã nút khi tin cũ không kèm bàn phím. **Thời điểm là BÂY GIỜ**, không
phải `date` của tin mang nút — tin đó có thể gửi từ hôm qua.

⚠️ **Cảm xúc Telegram báo TRẠNG THÁI MỚI, không báo "thêm/bớt"** — gỡ cảm xúc là gói có
`new_reaction` **rỗng**, khác hẳn Meta nói thẳng `action="unreact"`. Áp luật của Meta sang là cảm
xúc đã gỡ vẫn hiện mãi.

⚠️ **`/start <tham số>` là cách DUY NHẤT Telegram nói khách đến từ đâu**, và nó tới đúng một lần,
đội lốt một câu tin thường. Không tách ra thì hộp thư có một câu `"/start fb_ads_hue"` vô nghĩa còn
dữ liệu bán hàng mất vĩnh viễn. Tách xong phải bỏ **cả** phần chữ **và** mã tin, không thì lõi coi
đây là tin thật và ghi một dòng trắng. `/start` trơn (bấm nút Bắt đầu trong Telegram) **không** có
nguồn — ghi bừa nguồn rỗng là làm bẩn báo cáo.

⚠️ **Ảnh đại diện Telegram phải đi qua máy chủ: đường tải thật của họ CHỨA BOT TOKEN**
(`/file/bot<token>/…`). ChatbotX lưu thẳng chuỗi đó làm avatar, tức phát bot token cho mọi trình
duyệt mở hộp thư — **không chép chỗ này**. Ở đây lưu đường tương đối `/api/v1/chat/avatars/`
`{accountId}/{fileId}`, và lúc trả ra API mới gắn thêm `?sessionId=` (thẻ `<img>` không gửi được
tiêu đề xác thực). Lấy cỡ **nhỏ nhất** — ngược với ảnh khách gửi (lấy cỡ lớn nhất để soi được chữ).

⚠️ **`file_id` gắn với TỪNG bot.** Proxy tệp trước 27/08 dùng `Telegram:BotToken` — bot **dùng**
**chung của bản tin sáng**, không phải bot công ty vừa nối — nên mọi tệp khách gửi đều hiện "chưa
tải được" mà không lỗi nào lần ra. Nay tra token theo `account_id` của hội thoại
(`GetConversationByMessageAsync`), chỉ lùi về cấu hình chung khi tài khoản chưa có khoá riêng.

**Telegram vẫn KHÔNG có** báo đã nhận/đã xem (dừng ở "đã gửi" vĩnh viễn — đó là đúng), và **chưa**
**làm**: gửi nút inline ra (chưa có luồng bot dùng nút), menu cố định, xử lý `my_chat_member` khi
khách chặn bot (đã khai nhận gói, chưa xử lý).
⚠️ **Mỗi kênh một kiểu xác thực, đừng chép qua lại:** Zalo = `SHA256(appId+thânThô+timestamp+secret)`;
Messenger = **HMAC**-SHA256(appSecret, thânThô) trong `X-Hub-Signature-256`; Telegram **không ký gì
cả** — chỉ so chuỗi bí mật trong `X-Telegram-Bot-Api-Secret-Token`, nên **thiếu chuỗi đó là ai biết
địa chỉ webhook cũng bơm tin giả vào hộp thư**.

⚠️ **Cửa sổ gửi khác nhau THẬT:** Zalo 48h · Messenger 24h · Telegram và web **không giới hạn**. Áp
một luật chung là hoặc tự khoá tay mình (Telegram), hoặc để tin biến mất (Messenger).

**NHIỀU tài khoản mỗi kênh** (đổi 24/08). Một công ty du lịch có nhiều Trang Facebook cho các chi
nhánh, nhiều OA Zalo, nhiều bot Telegram cho từng đội sale — ép về một tài khoản/kênh là sai với thực
tế vận hành. Khoá lưu ở [`ChannelCredentialStore`](../../TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs),
vẫn dùng lại bảng `dbo.TenantChannelSettings` nhưng cột `Channel` nay mang dạng `"{tiềnTố}:{accountId}"`
(mã 8 ký tự do **máy chủ sinh**, không nhận từ client — nó nằm trên URL webhook công khai). Mọi giá trị
mã hoá Crypton. CRUD qua `GET /api/v1/chat/channels` + `POST|PUT|DELETE .../channels/{kênh}/accounts[/{id}]`
(cần `CH_HT_XEM`); giao diện **tự vẽ form** theo danh sách ô máy chủ trả về, dạng **popup** (khai kênh là
việc một lần lúc cài đặt, chèn giữa trang thì mỗi lần mở là danh sách hội thoại tụt xuống).

⚠️ **Zalo của chat ĐỘC LẬP với Zalo của bản tin sáng.** Trước 24/08 chat dùng chung bản ghi `zalo` của
`TenantChannelSettingsStore`; nay chat có kho riêng (tiền tố `chat-zalo`) và **tự xoay vòng access token
của chính nó** trong [`ZaloChatAdapter`](../../TourkitAiProxy.Services/Chat/Channels/ZaloChatAdapter.cs). Hai kho tuyệt đối
không đọc/ghi chéo — hai nơi cùng xoay MỘT refresh token thì Zalo vô hiệu hoá cái cũ và bên chậm chân
mất token vĩnh viễn. Zalo trả refresh token MỚI mỗi lần làm mới, phải lưu đè cái cũ.

⚠️ **Đường webhook khác nhau theo kênh, không phải tuỳ tiện:**
`POST /api/v1/chat/webhook/{kênh}/{tenantId}[/{accountId}]`.
**Telegram BẮT BUỘC có `{accountId}`** — thân tin không chứa bất kỳ thông tin nào cho biết bot nào, định
danh duy nhất nằm ở chính URL đã khai lúc `setWebhook`. **Zalo/Messenger dùng CHUNG một URL** cho mọi tài
khoản (hai nền tảng đăng ký webhook theo App chứ không theo Trang/OA), adapter tự soát ra tài khoản.

⚠️ **Với ứng dụng cấp nền tảng, thứ tự là TÌM TRANG/OA TRƯỚC rồi mới kiểm chữ ký** — không phải ngược
lại. Messenger từng thử lần lượt mọi `appSecret` đã khai rồi mới khớp `entry[].id`; cách đó chỉ đúng khi
mỗi công ty một App riêng. Nay mọi khách chung một App Secret nên "khớp một cái bất kỳ" không chứng minh
được gì. Cả hai kênh nay: đọc id Trang (`entry[].id`) / id OA trong thân tin → tra ra công ty và tài
khoản → kiểm chữ ký bằng khoá **của chính tài khoản đó**. Tài khoản khai tay theo đường cũ vẫn khớp được
vì tìm cả `accountId` lẫn ô `pageId`.

⚠️ **Đường DÙNG CHUNG luôn trả 200, kể cả khi từ chối** (`/webhook/zalo`, `/webhook/messenger`). Zalo nói
thẳng "chỉ được thiết lập khi trả về 200 OK" và gọi thử bằng gói rỗng; Meta thì **tự động ngừng gửi** cho
ứng dụng nào trả lỗi liên tục — mà ứng dụng là dùng chung, nên trả 401 cho tin rác là tắt kênh của **mọi**
khách hàng cùng lúc. Từ chối vẫn là **không ghi gì** vào hộp thư. Cái giá: hỏng thì hỏng **im lặng**, nên
mọi lượt từ chối ghi log mức WARNING kèm id Trang/OA — đó là chỗ duy nhất nhìn ra "tin có tới mà không vào
hộp thư".

Hội thoại nhớ `account_id` **ghi một lần lúc tạo**, những lần sau không ghi đè kể cả khi tới từ tài khoản
khác — đổi ngầm giữa chừng làm nhân viên trả lời sai danh nghĩa mà không hay.

**Gỡ kết nối KHÔNG xoá hội thoại cũ** — lịch sử chat với khách là dữ liệu nghiệp vụ; gỡ chỉ nghĩa là thôi
nhận/gửi qua tài khoản đó.

**Gửi ảnh/tệp — kho lưu chọn được: `Storage:Provider` = `r2` | `s3` | `local`** (mặc định `local`).
Một giao diện [`IChatFileStorage`](../../TourkitAiProxy.Services/Storage/IChatFileStorage.cs), ba cách lưu; R2 và S3 dùng
CHUNG một lớp vì cùng giao thức S3, chỉ khác cách dựng client. **`local` không cần tài khoản cloud nào**
nên chạy được ngay trên máy dev/VPS tự quản (phục vụ qua `/chat-files`), NHƯNG không hợp khi nhiều
instance sau load-balancer — mỗi máy một đĩa, ảnh tải lên máy A sẽ 404 khi máy B phục vụ.

⚠️ **Chọn `r2`/`s3` mà thiếu khoá thì TẮT hẳn kèm lý do, KHÔNG tự lùi về `local`** — lùi ngầm nghĩa là
ảnh tưởng nằm trên cloud hoá ra nằm trên đĩa máy chủ, đầy đĩa hoặc mất máy là mất ảnh mà không ai biết.

⚠️ **`local`: thư mục neo vào THƯ MỤC APP, tuyệt đối không dùng `Directory.GetCurrentDirectory()`.**
Nơi GHI và nơi PHỤC VỤ `/chat-files` phải ra cùng một chỗ, nên cùng gọi
[`LocalChatFileStorage.ThuMucGoc`](../../TourkitAiProxy.Services/Storage/LocalChatFileStorage.cs) — hai bên tự dựng riêng
thì lệch lúc nào không biết, mà triệu chứng chỉ là **ảnh 404**, không lỗi nào hiện lên. Thư mục làm
việc của tiến trình KHÔNG phải thư mục app: chạy `dotnet run` ở gốc repo thì tình cờ trùng nên không
lộ, dưới IIS nó thường là `C:\Windows\System32` → ảnh ghi ra ngoài app rồi mất khi deploy lại, hoặc
ghi hỏng vì không có quyền. Đường dẫn ảnh **lưu trong CSDL là vĩnh viễn** nên một lần lệch là ảnh đó
404 mãi. Đã dính thật 25/08 (ảnh gửi trong hội thoại thử không còn đọc được).

⚠️ **Bucket R2/S3 phải cho ĐỌC CÔNG KHAI.** Cả ba kênh gửi media bằng cách đưa URL để nền tảng TỰ TẢI
về, không nhận nhị phân qua API chat. Presigned URL có hạn cũng không hợp vì khách xem lại tin cũ bất cứ
lúc nào. Nên **đừng để tệp nhạy cảm đi đường này**.

**Đính kèm khách gửi** chuẩn hoá ở MÁY CHỦ ([`ChatAttachment`](../../TourkitAiProxy.Domain/Chat/ChatAttachment.cs),
hàm thuần, có test): mỗi kênh gói tệp một kiểu, để giao diện tự bóc thì cùng đoạn phân tích phải viết
lại bằng JavaScript và không test được. Ảnh Telegram lấy **cỡ lớn nhất** (Telegram xếp nhỏ trước — lấy
nhầm cỡ nhỏ thì soi ảnh hoá đơn/hộ chiếu khách gửi không đọc nổi chữ). Telegram chỉ cho `file_id` chứ
không cho URL, nên đi qua `GET /api/v1/chat/messages/{id}/file` để **giấu bot token** khỏi trình duyệt.

**Đường đi:** webhook **chỉ GHI thân thô** vào `chat_inbound_events` rồi trả 200 →
[`ChatInboundWorker`](../../TourkitAiProxy.Services/Chat/Inbox/ChatInboundWorker.cs) (nhịp 2s) rút ra →
[`ChatInboundService`](../../TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs) → bot trả lời → xếp
`chat_outbox` → [`ChatOutboxWorker`](../../TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs) gửi qua
[`ZaloChatAdapter`](../../TourkitAiProxy.Services/Chat/Channels/ZaloChatAdapter.cs).

⚠️ **Đã trả 200 thì kênh KHÔNG gửi lại — nên việc còn dở tuyệt đối không được nằm trong bộ nhớ.**
Bản đầu dùng `Task.Run` rời: IIS recycle / deploy / crash đúng lúc đó là **mất hẳn tin của khách,
không dấu vết**. Hàng đợi vào lưu **thân THÔ** chứ không lưu bản đã bóc — sửa adapter xong là chạy
lại được dòng cũ, còn lưu bản đã bóc thì lỗi bóc tin nằm lại vĩnh viễn. Webhook vẫn bóc MỘT lần
nhưng chỉ để lấy id sự kiện làm khoá chống trùng: chống trùng phải xảy ra lúc **GHI**, không thì
kênh gửi lại đồng thời hai lần sẽ tạo hai dòng và bot trả lời hai lần.

**Danh sách hội thoại phân trang bằng CON TRỎ**, không phải số trang. Con trỏ là cặp
`(lần hoạt động cuối, id)` mã hoá base64url, so bằng bộ đôi ngay trong SQL
([`ChatCursor`](../../TourkitAiProxy.Domain/Chat/ChatModels.cs), có test). Dùng `OFFSET` thì mỗi tin mới đẩy cả
danh sách xuống một dòng — người đang cuộn thấy **lặp lại** hội thoại vừa đọc và **sót** hội thoại
chưa đọc, đúng lúc hộp thư bận nhất. Giao diện **trộn theo id** chứ không thay thế, nên trang đã
cuộn ở dưới không bị cuốn về đầu khi có tin mới.

**Tin mới ĐẨY tới bằng SSE, không hỏi lại định kỳ** (`GET /api/v1/chat/events`,
[`ChatEventBus`](../../TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs)). Mười nhân viên mở hộp thư theo
kiểu hỏi-lại-4-giây là 300 lượt mỗi phút cho thứ hầu hết thời gian không đổi — mà tin mới **vẫn**
trễ tới 4 giây. Chọn SSE chứ không SignalR vì dự án đã có sẵn SSE ở cả hai đầu và **frontend không
có bundler**: thêm SignalR là thêm một thẻ script CDN VÀ một dòng vào `bundle-entry.js`, hai danh
sách đó đã lệch nhau một lần rồi. Nhu cầu cũng chỉ một chiều — nhân viên gõ thì vẫn `POST`.

⚠️ **Bus kẹp tenant NGAY TRONG BUS, không lọc ở endpoint.** Lọc ở ngoài thì một lần quên là hộp thư
công ty này nhận sự kiện của công ty khác. Sự kiện **cố ý không mang nội dung tin** — chỉ nói "hội
thoại này vừa đổi", tab tự gọi API để lấy dữ liệu, nhờ vậy luật xem-được-gì vẫn nằm nguyên ở
endpoint thay vì phải nhân bản sang kênh đẩy.

⚠️ **HTTP/1.1 chỉ cho 6 kết nối mỗi origin** và một luồng SSE giữ mất một suất, nên giao diện
**đóng luồng khi tab ẩn** — không thì mở vài tab TRAV-AI là các request thường bị treo, lỗi rất khó
lần. Chạy HTTP/2 thì hết vấn đề.

**Nhiều instance đi qua Redis pub/sub** (kênh `tkai:chat:events`). SSE giữ kết nối tới **đúng một**
instance, nên không có pub/sub thì tin tới instance khác làm tab đang mở không thấy — triệu chứng là
"thỉnh thoảng tin mới không hiện", loại lỗi chỉ ra mặt khi đông người dùng, tức đúng lúc tệ nhất.
Mỗi gói tin kèm **mã instance phát** và instance **bỏ qua gói của chính mình**: Redis trả gói về cho
cả người phát, không lọc thì mỗi sự kiện tới người nghe hai lần và giao diện tải lại gấp đôi. Gói tin
hỏng (phiên bản cũ còn trong Redis, hoặc ai đó publish nhầm kênh) thì **bỏ qua chứ không ném** — ném
là chết luồng đăng ký và từ đó instance câm hẳn mà không ai biết.

⚠️ **Không có `Redis:ConnectionString` thì bus chỉ thấy sự kiện của CHÍNH instance mình** — vẫn chạy
tốt khi triển khai một bản, nhưng **nói ra chứ không im lặng**: log lúc khởi động ghi rõ chế độ, và
`GET /api/v1/features` trả `chatRealtime: false` để giao diện giữ **đường lùi hỏi lại 20 giây chạy
liên tục**. Có Redis thì `chatRealtime: true` và đường lùi chỉ bật khi luồng đứt — lúc đẩy chạy tốt
thì tab Network sạch, không có request định kỳ nào.

⚠️ Bản gốc của kế hoạch ghi đường lùi là **4 giây** (đúng bằng nhịp cũ). Ở đây để **20 giây**: khi
chỉ chạy một instance — trường hợp thường gặp — luồng đẩy đã phủ đủ, quay lại nhịp 4 giây là xoá
sạch cái lợi vừa làm được. 20 giây vẫn nhẹ hơn 5 lần so với trước mà không để hộp thư câm.
**Nhận việc là NGUYÊN TỬ.** Điều kiện `assigned_username IS NULL` nằm trong chính câu `UPDATE`,
không phải đọc-rồi-ghi trong C#: giữa lần đọc và lần ghi có một khe, hai nhân viên bấm cách nhau
100ms là cả hai cùng lọt, cùng thấy "của tôi" và cùng trả lời một khách. 0 dòng đổi được → **409**
kèm tên người đang giữ, không phải 200 im lặng. Tên người nhận lấy từ **phiên ở máy chủ**, không
lấy từ thân yêu cầu — để client tự khai tên là ai cũng gán việc cho người khác được.

⚠️ **Nhả việc và chuyển việc CỐ Ý không đi đường nguyên tử** — cả hai đều là thao tác đè lên người
đang giữ. Chỉ "nhận việc cho chính mình" mới phải tranh nhau.

**Chưa đọc tính theo TỪNG NGƯỜI** (`chat_conversation_reads`, khoá `(tenant_id, conversation_id,
username)`). Trước đây chỉ có `chat_conversations.agent_last_read_at` — **một cột cho cả công ty**,
nên A mở hội thoại là B cũng mất dấu chưa đọc. Hộp thư một người thì không lộ ra; hai người trở
lên là sai ngay, mà sai **im lặng**: không có lỗi nào hiện, chỉ có tin của khách trôi qua mắt người
thứ hai.

⚠️ **Cột cũ VẪN GIỮ và vẫn được đọc**, làm mốc ban đầu cho người chưa có dòng nào trong bảng mới —
nhưng **không còn được ghi**. Xoá cột là mọi hội thoại cũ bật lại thành "chưa đọc" cho tất cả mọi
người ngay sau khi deploy; còn tiếp tục ghi vào nó thì quay lại đúng cái lỗi vừa sửa, chỉ khác là
nay có thêm một bảng trông như đã sửa.
**Nhật ký thao tác** (`chat_audit`, xem ở cuối panel hồ sơ): nhận việc · nhả việc · chuyển việc ·
đổi trạng thái · chỉnh trợ lý · gỡ kết nối kênh. Khi khách khiếu nại "ai nói câu này với tôi",
hoặc một hội thoại bị đóng nhầm, đây là chỗ duy nhất tra được.

⚠️ **`chi_tiet` KHÔNG chứa nội dung tin** — tin đã nằm ở `chat_messages`. Chép lại là nhân đôi dữ
liệu khách **và** nhân đôi chỗ phải xoá khi khách yêu cầu xoá dữ liệu; sót một chỗ là vẫn còn lưu
trái ý khách. Có test canh việc này.

⚠️ **Ghi nhật ký không bao giờ ném.** Nhân viên bấm đóng hội thoại mà nhận lỗi chỉ vì bảng nhật ký
có vấn đề là đổi một sự cố ghi chép thành một sự cố vận hành.
**Nối khách chat với khách CRM — NỐI TAY, không đoán tự động.** Cột `chat_contacts.crm_customer_id`
có từ đợt 1 nhưng **chưa dòng code nào ghi giá trị vào đó**, nên panel hồ sơ luôn hiện "chưa nối".
Nay panel có ô tìm + nút Nối/Đổi/Gỡ (`GET …/crm-search?q=`, `POST …/link-crm`).

⚠️ **Vì sao không ghép tự động:** ghép theo tên sai thường xuyên — trùng tên là chuyện bình thường
ở khách du lịch; ghép theo số điện thoại thì Zalo/Messenger **không cho biết số** trừ khi khách tự
nhắn. Nối tay đúng 100% và làm được ngay; tự động để sau khi có dữ liệu thật xem tỉ lệ trùng.

⚠️ **Tìm khách dùng phiên CỦA CHÍNH NHÂN VIÊN**, không phải tài khoản dịch vụ — để CRM tự chặn theo
quyền của họ. Dùng tài khoản dịch vụ là nhân viên chỉ được xem khách của mình vẫn tra ra cả kho
khách của công ty. Có test canh.

⚠️ **Gỡ nối phải làm được.** Nối nhầm là bot đọc lịch sử mua của người khác rồi nói với khách này;
không có đường lùi thì chỉ còn cách sửa tay CSDL. Cả nối lẫn gỡ đều vào nhật ký thao tác.
**Nhãn và ghi chú gắn theo KHÁCH, không theo hội thoại** (`chat_contact_tags`,
`chat_contact_notes`). Khách nhắn lại sau ba tháng vẫn còn nhãn cũ; gắn theo hội thoại thì mỗi lần
mở hội thoại mới là mất hết — đúng lúc cần nhất. Ghi chú là **nội bộ**: khách không bao giờ thấy,
giao diện nói thẳng câu đó, vì không nói thì không ai dám ghi thật.

⚠️ **Nhãn dùng CHUNG hàm chuẩn hoá với lệnh gọi mẫu trả lời nhanh** —
[`ChatRules.ChuanHoaSlug`](../../TourkitAiProxy.Domain/Chat/ChatRules.cs), hàm thuần, có test. Đây là đúng cùng một
vấn đề: người Việt gõ nhanh sẽ không bật bộ gõ để ra dấu. Viết lại lần hai là hai chỗ lệch nhau —
`khach-vip` bên này và `khach vip` bên kia — rồi lọc theo nhãn ra rỗng mà không ai hiểu tại sao.

⚠️ **Chuẩn hoá cả lúc XOÁ nhãn.** Nhãn nằm trên đường dẫn nên trình duyệt có thể gửi bản còn dấu,
mà trong CSDL chỉ có bản đã chuẩn hoá — không chuẩn hoá là xoá trượt, nút bấm không có tác dụng gì.
**Mẫu trả lời nhanh** (`chat_quick_replies` +
[`ChatQuickReplyRepository`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatQuickReplyRepository.cs)): gõ `/` ở **ĐẦU** ô
soạn ra danh sách. Lệnh gọi **bỏ dấu** khi lưu — nhân viên đang gõ nhanh cho khách sẽ gõ `/gia` chứ
không dừng bật bộ gõ để ra `/giá`. Chỉ gợi ý khi `/` đứng đầu, giữa câu nó là dấu gạch bình thường
(vd "sáng/chiều"). Theo TỪNG CÔNG TY, không theo từng nhân viên. ⚠️ Chỉ mục là **biểu thức**
`lower(trigger)`, nên `ON CONFLICT` phải ghi đúng biểu thức đó — lệch là lỗi lúc CHẠY, có test đối
chiếu hai chỗ.

**Vòng đời tin gửi đi:** `chờ → đã gửi → đã nhận → đã xem`, cập nhật qua
[`ChatRepository.MarkStateWatermarkAsync`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs) và **chỉ tiến,
không lùi** ([`ChatRules.KhongLui`](../../TourkitAiProxy.Domain/Chat/ChatRules.cs), có test) — nền tảng không bảo
đảm thứ tự webhook, "đã nhận" hoàn toàn có thể tới sau "đã xem", ghi đè mù thì dấu tích chạy ngược
trước mắt nhân viên. Mã tin của nền tảng lưu vào `chat_messages.external_msg_id` ngay khi gửi được
([`SetExternalMsgIdAsync`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs)) — thứ **duy nhất** đối chiếu được
khi nền tảng báo lại.

⚠️ **Ba kênh báo lại khác nhau, đừng áp một luật:** Zalo `user_seen_message` (chỉ "đã xem") ·
Messenger `delivery` + `read` (đủ hai mức, theo **mốc nước** — mọi tin trước thời điểm đó; dùng
`watermark` chứ không `mids` vì gói `read` không có `mids`) · **Telegram KHÔNG báo gì cả** — Bot API
không có, nên tin Telegram dừng ở "đã gửi" vĩnh viễn và **đó là đúng**. Đừng "sửa" bằng cách tự nhảy
trạng thái khi gửi xong: như thế là nói dối nhân viên rằng khách đã nhận trong khi mình không biết.
Giao diện nói rõ ở tooltip dấu tích.

⚠️ **Mốc nước quét theo `created_utc` nên phải loại tin CÒN TRONG HÀNG ĐỢI** (`state > 0`). Nhân viên
bấm gửi lúc 10:00:00 (tin vào hàng đợi), worker gửi lúc 10:00:03 vì nhịp 5 giây; khách đọc một tin CŨ
lúc 10:00:01 → mốc 10:00:01 quét trúng luôn tin vừa tạo còn chưa rời khỏi hệ thống. Để lọt thì nhân
viên thấy "khách đã xem" một tin khách chưa hề nhận, rồi worker gửi xong lại đặt về "đã gửi" — dấu
tích chạy ngược. Chặn ở **cả** luật thuần lẫn SQL, vì cập nhật hàng loạt không đọc từng dòng ra hỏi
luật được.

**Tên định danh trong cụm chat KHÔNG đồng nhất, và đó là chuyện đã rồi:**
[`ChatRules`](../../TourkitAiProxy.Domain/Chat/ChatRules.cs) đặt tên tiếng Việt (`TinhCuaSo`, `GhepCum`, `TomTat`,
`KhongLui`), còn [`ChatRepository`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs) và
[`ChatModels`](../../TourkitAiProxy.Domain/Chat/ChatModels.cs) đặt tiếng Anh. **Theo file mình đang sửa**, đừng
theo cụm — thêm một tên tiếng Việt vào `ChatRepository` là tạo ngoại lệ giữa 26 tên tiếng Anh (đã
xảy ra một lần, phải đổi lại). Quy ước ở mục Conventions chỉ nói tiếng Việt cho **chữ hiển thị, log,
chú thích** — không nói gì về tên định danh.

**Sáu luật sai-là-hỏng**, tách thuần ở [`ChatRules`](../../TourkitAiProxy.Domain/Chat/ChatRules.cs), có test:
1. **Cửa sổ gửi** — Zalo 48h / Messenger 24h kể từ tin cuối CỦA KHÁCH. **Chưa có tin nào của khách =
   ĐÓNG**, không phải mở. Hết cửa sổ thì khoá ô soạn kèm lý do, đừng để gọi API rồi mới biết.
2. **Bot câm khi người thật vào** — `bot_resume_at` là **MỐC THỜI GIAN, không phải cờ**: câm có thời
   hạn rồi tự nói lại. Làm thành cờ thì sẽ có hội thoại tắt bot vĩnh viễn mà không ai nhớ bật lại.
3. **Chống trùng ở TẦNG CSDL** (chỉ mục duy nhất trên `external_msg_id`), không chỉ kiểm trong code —
   webhook gửi lại đồng thời hai lần thì kiểm-rồi-ghi vẫn lọt.
4. **Gộp tin liên tiếp** — chờ khách im 4 giây rồi xử lý cả cụm; trả lời từng dòng vừa ngớ ngẩn vừa
   tốn gấp mấy lần lượt AI.
5. **Webhook trả 200 NGAY** rồi xử lý nền — Zalo gửi lại khi không thấy 200, mà xử lý có gọi AI.
   "Nền" ở đây là **hàng đợi trong CSDL**, không phải `Task.Run` (xem cảnh báo ở mục Đường đi).
   ⚠️ Luật thứ bảy nằm ở mục "Vòng đời tin gửi đi" bên trên: **trạng thái chỉ tiến, không lùi**.
6. **Tiếng vọng `oa_send_*`** — nhân viên trả lời từ chính app Zalo OA thì mình chỉ biết qua đây. Bỏ
   nhóm này thì hộp thư thiếu nửa cuộc trò chuyện VÀ bot nói đè lên người thật.

⚠️ **Zalo chat dùng `message/cs`, KHÁC bản tin sáng (ZNS).** Bản tin là mình chủ động đẩy đi nên cửa
sổ tư vấn luôn đóng → phải dùng mẫu ZNS. Chat là trả lời khách vừa nhắn nên cửa sổ vừa mở → `message/cs`
đúng chỗ. **Đừng "sửa" cái này thành ZNS.**

⚠️ **Access token Zalo: proxy chỉ ĐỌC, worker toutkit-app xoay vòng.** Hai nơi cùng xoay một refresh
token thì Zalo vô hiệu hoá cái cũ và bên chậm chân mất token vĩnh viễn.

**Đợt 1 CHƯA nối CRM** — bot trả lời bằng kiến thức chung, `crm_customer_id` để trống. Lời dặn mặc
định **cấm bịa giá/lịch khởi hành**; đổi qua `Chat:SystemPrompt`.

