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
**WhatsApp và TikTok — kênh thứ năm và sáu** (thêm 27/08).

⚠️ **WhatsApp cùng nhà Meta nhưng KHÔNG dùng chung được gì với Messenger/Instagram.** Hai kênh kia
đi hợp đồng nhắn tin `entry[] × messaging[]`; WhatsApp đi hợp đồng Business Management
`entry[] × changes[] × value`. Chỉ **chữ ký** là giống (HMAC App Secret trong `X-Hub-Signature-256`).
Đừng nhét vào `MetaMessagingParser` — gộp hai hợp đồng khác nhau vào một hàm là chỗ đẻ ra lỗi im lặng.

⚠️ **Khoá định tuyến WhatsApp là `value.metadata.phone_number_id`, KHÔNG phải `entry[].id`** (chỗ đó
là id WABA). Lấy nhầm là tra ra rỗng và tin rơi vào hư không.

⚠️ **Tên khách WhatsApp nằm ở `contacts[]`, TÁCH khỏi `messages[]`** — ghép lại bằng số điện thoại.
Không ghép thì hộp thư hiện một dãy số dù gói tin có sẵn tên.

⚠️ **WhatsApp báo trạng thái theo `id` TỪNG TIN** (`statuses[]`: `sent`/`delivered`/`read`), không
theo mốc nước như Messenger — đi chung đường với Instagram qua `StateWatermark.ExternalMsgId`.
`failed` **không** map sang trạng thái nào: luật `KhongLui` vốn chặn tin gửi được thành hỏng, ghi bừa
ở đây là dấu tích chạy ngược. Chỉ ghi log để còn tra.

⚠️ **Tệp khách gửi qua WhatsApp đòi KHOÁ XÁC THỰC ở CẢ HAI lượt.** Gói tin chỉ cho mã tệp; hỏi ra
đường tải rồi vẫn phải gắn `Bearer` mới tải được — gọi trần vào đường đó là 401. Khác Telegram (khoá
giấu trong đường dẫn) và khác hẳn Zalo/Messenger/Instagram (URL công khai). Nên **không có cách nào**
đưa thẳng cho trình duyệt; bắt buộc qua `GET /api/v1/chat/messages/{id}/file`, nay đường đó rẽ nhánh
theo kênh của hội thoại.

⚠️ **Ngoài cửa sổ 24h WhatsApp chỉ nhận mẫu đã duyệt**, không gửi chữ tự do. Cửa sổ tính như
Messenger nên `ChatRules.TinhCuaSo` không phải sửa.

⚠️ **TikTok: nội dung tin là CHUỖI JSON lồng trong JSON.** Trường `content` của gói webhook là một
**chuỗi**, phải phân tích lần thứ hai mới ra tin. Đọc thẳng như đối tượng là luôn ra rỗng — không lỗi,
không log, hộp thư chỉ đơn giản không bao giờ có tin.

⚠️ **Chữ ký TikTok CÓ HẠN 5 GIÂY.** Header `TikTok-Signature: t=<giây>,s=<hex>`, ký trên chuỗi
`"{t}.{thân thô}"`. Máy chủ lệch giờ là **mọi** gói bị từ chối sạch — nên nhật ký tách riêng "quá hạn"
khỏi "chữ ký sai", không thì người tìm lỗi đi soi khoá bí mật suốt buổi trong khi lỗi nằm ở đồng hồ.

⚠️ **TikTok gửi theo mã HỘI THOẠI, không theo mã người** (`recipient_type=CONVERSATION`). Nên ở kênh
này `ExternalUserId` mang **mã hội thoại** — mọi kênh khác mang mã khách. Lấy nhầm là gửi ra lỗi mà
nhìn dữ liệu vẫn thấy "có id đàng hoàng". Và tiếng vọng `im_send_msg` thì tên khách nằm ở **người**
**nhận**, lấy `from_user` là hội thoại mang tên chính công ty mình.

⚠️ **TikTok trả HTTP 200 KỂ CẢ KHI HỎNG** — lỗi nằm ở trường `code` trong thân. Chỉ nhìn mã HTTP là
báo "đã gửi" cho những tin không bao giờ tới. Và **ảnh phải tải lên trước** (`media/upload` ra
`media_id`), TikTok không tự tải từ URL như bốn kênh kia; header xác thực là `Access-Token`, **không**
phải `Authorization: Bearer`.

**Hạn trả lời của TikTok không có trong tài liệu công khai**, nên `TinhCuaSo` để **mở** cho kênh này —
khoá ô soạn theo một con số tự đoán là tự khoá tay nhân viên vì một luật có thể không tồn tại. TikTok
từ chối thì câu lỗi của họ hiện lên. Tra ra hạn thật thì chuyển xuống nhánh có hạn.

⚠️ **Cả hai chưa kiểm bằng tài khoản thật** (27/08): WhatsApp cần WABA đã xác minh doanh nghiệp + số
điện thoại riêng; TikTok cần ứng dụng TikTok for Business đã duyệt quyền nhắn tin. Phần bóc tin, chữ ký
và cửa sổ gửi có test; đường gửi và bước nối vẫn là theo tài liệu.

**Instagram Direct — kênh thứ tư** (thêm 27/08). Đi **cùng hợp đồng nhắn tin của Meta** với
Messenger, nên phần bóc tin dùng CHUNG một lớp
([`MetaMessagingParser`](../../TourkitAiProxy.Services/Chat/Channels/MetaMessagingParser.cs)): cùng hình dạng
`entry[] × messaging[]`, cùng `mid`, cùng `is_echo`, cùng cách gói đính kèm, cùng kiểu ký. Chép ra
hai bản là hai bản lệch nhau — mà lệch ở đây thì hỏng im lặng: một kênh nhận được cảm xúc, kênh
kia không, không lỗi nào hiện ra. Cùng lý do R2 và S3 dùng chung một lớp lưu trữ.

**Nối qua TRANG FACEBOOK đã kết nối, KHÔNG qua đăng nhập Instagram riêng.** Khách bấm "Kết nối
Facebook" như cũ; nối Trang xong hệ thống tự hỏi Trang đó có tài khoản Instagram liên kết không
(`GET /{pageId}?fields=instagram_business_account`) và nối luôn — **không thêm nút nào**. Cùng ứng
dụng Meta, cùng App Secret, cùng Page Access Token.

⚠️ **Vì sao KHÔNG chép cách ChatbotX làm.** Họ dùng *Instagram Login* (`api.instagram.com`, app
Instagram riêng, scope `instagram_business_*`). Đường đó không cần Trang, nhưng token **hết hạn sau
60 ngày** và phải tự làm mới — thêm một thứ hỏng âm thầm vào lúc không ai để ý. Page Access Token
thì không hết hạn, và công ty du lịch nào cũng đã có Trang. Cái giá: tài khoản phải là Instagram
Professional đã liên kết Trang, và phải bật "Cho phép truy cập tin nhắn" trong cài đặt Instagram.

⚠️ **Ba chỗ KHÁC Messenger thật, đừng áp một luật:**
1. Trường `object` là `"instagram"`, không phải `"page"`.
2. Đường gửi là `graph.instagram.com` và token đi ở header `Authorization: Bearer` — Instagram
   **không** nhận `?access_token=` trên URL như Graph của Facebook. Chép nguyên đường gửi của
   Messenger sang là mọi tin gửi đi đều bị từ chối.
3. **KHÔNG có `message_deliveries`.** Meta chỉ cấp `messaging_seen` cho Instagram, nên tin nhảy
   thẳng "đã gửi" → "đã xem", không bao giờ có "đã nhận" — **và đó là đúng**.

⚠️ **`messaging_seen` của Instagram báo bằng `mid`, KHÔNG bằng `watermark`.** Messenger gửi
`{"read":{"watermark":<ms>}}`; Instagram gửi `{"read":{"mid":"<tin cuối đã đọc>"}}`. Đọc theo lối
Messenger thì giá trị ra `null`, sự kiện **rơi im lặng**, dấu tích đứng mãi ở "đã gửi". Mốc thời
gian phải tra ngược từ chính tin đó (`ChatRepository.ThoiDiemTinAsync`) — lấy tạm giờ nhận gói cho
nhanh là đánh dấu THỪA lên tin khách chưa hề mở, tức nói dối nhân viên. Không tra ra tin thì **bỏ
qua**, không đoán.

⚠️ **Đường webhook RIÊNG dù chung ứng dụng:** `/api/v1/chat/webhook/instagram`. Meta khai địa chỉ
webhook riêng cho từng *đối tượng* (`page` · `instagram`), nên gộp vào đường của Messenger là
Instagram không có chỗ gửi tới. Cũng **luôn trả 200** kể cả khi từ chối, và vì lý do nặng hơn: ứng
dụng dùng chung nên trả lỗi liên tục là Meta tắt kênh của **mọi** khách hàng cùng lúc.

⚠️ **Trường webhook của đối tượng `instagram` bật ở CẤP ỨNG DỤNG**, không phải lệnh gọi cho từng
tài khoản (khác Trang Facebook và khác Telegram). Danh sách ghi ở `InstagramChatAdapter.SuKienTaiKhoan`
để lúc khai ứng dụng không ai phải đoán — thiếu một trường thì mã bóc vẫn đúng, chỉ là gói tin không
bao giờ tới.

⚠️ **Chưa kiểm bằng tài khoản thật** (27/08). Phần bóc tin, cửa sổ gửi và luật "đã xem" có test;
bước nối và đường gửi phải thử trên một tài khoản Instagram Professional thật rồi mới coi là xong.

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

⚠️ **Cửa sổ gửi khác nhau THẬT:** Zalo 48h · Messenger/Instagram/WhatsApp 24h · Telegram, TikTok
và web **không giới hạn**. Áp một luật chung là hoặc tự khoá tay mình (Telegram), hoặc để tin biến
mất (Messenger).

⚠️ **Messenger và Instagram còn một cửa thứ hai: 7 NGÀY cho NGƯỜI THẬT** (nhãn `HUMAN_AGENT`, đổi
28/08/2026). Ngoài 24h, tin do **nhân viên** gõ vẫn gửi được tới 7 ngày kể từ tin của khách; tin do
**trợ lý** sinh ra thì không — đính nhãn đó cho tin của bot là vi phạm chính sách Meta và có thể bị
khoá quyền nhắn tin của cả Trang. Vì thế `ComputeSendWindow` nhận thêm `ChatSender`, và **mặc định
là bot** (chặt hơn): chỗ gọi nào quên truyền thì mất quyền chứ không được thêm quyền. WhatsApp
**không** có cửa này — ngoài 24h phải dùng mẫu đã duyệt. Đường gửi có nhãn tách riêng ở
[`ILateHumanReplySender`](../../TourkitAiProxy.Services/Chat/Channels/IChatChannelAdapter.cs), không
nhét vào chữ ký chung: bốn kênh còn lại không có khái niệm này.

### Cấu hình trợ lý (theo TỪNG công ty)

[`chat_bot_settings`](../../TourkitAiProxy.Infrastructure/Chat/Inbox/ChatBotSettingsRepository.cs)
trong **CSDL chat (PostgreSQL)** — không phải `dbo.TenantChannelSettings` bên SQL Server, vì bảng
đó dùng chung với cụm bản tin và worker của `toutkit-app`. Cụm chat tách hẳn, cấu hình cũng vậy.

Trước 28/08/2026 lời dặn cho bot nằm ở `Chat:SystemPrompt` trong `appsettings.json` — **một
prompt cho MỌI công ty**. Cấu hình một sản phẩm nhiều khách hàng bằng file cấu hình máy chủ chỉ
đúng khi có đúng một khách hàng.

⚠️ **Lời dặn của công ty NỐI THÊM, tuyệt đối không thay thế khung.** Khung chứa luật chống bịa
(giá tour, lịch khởi hành, số chỗ còn, khuyến mãi) — bot này **không đọc dữ liệu thật của công
ty**, nên bỏ khung đi là nó bịa giá và hứa giữ chỗ với khách thật. Và khung đặt **SAU** phần
công ty viết: phần cuối là phần model bám chặt nhất, nên luật cấm phải nằm cuối để một câu vô ý
("cứ báo giá luôn cho khách") không đè được lên. Có test cho cả hai điều này.

⚠️ **Bot phải đọc lại đoạn hội thoại.** Trước đây `GenerateReplyAsync` chỉ nhận cụm tin vừa tới:

```
Khách: "Tour Nhật bao nhiêu tiền ạ?"
Bot:   "Dạ em kiểm tra rồi báo lại anh ngay…"
Khách: "Thế còn tháng 10?"
Bot:   ← chỉ thấy "Thế còn tháng 10?", không biết đang nói về tour nào
```

`ChatRules.BuildConversationPrompt` dựng bản ghi hội thoại (tầng AI chỉ nhận MỘT chuỗi prompt,
không có mảng lượt). Nó **bỏ tin `Failed` và `Pending`** — khách chưa hề đọc chúng, đưa vào là
bot tưởng mình đã nói rồi. Nhân viên và trợ lý ghi **chung nhãn "Mình"**: với khách thì cả hai
đều là công ty.

⚠️ Đọc rất thường xuyên (mỗi tin khách nhắn là một lượt) nên kho nhớ tạm 60 giây trong bộ nhớ,
và **dọn ngay khi Lưu** — chờ 60 giây mới thấy hiệu lực thì người vừa bấm tưởng nút Lưu hỏng.

### Nút bấm dưới tin

**Chỉ HAI kiểu, cố ý.** Dự án tham chiếu gắn vào nút một `payload` trỏ tới bước trong luồng của
nó — bên mình **không có trình dựng luồng**, bot là trợ lý AI đọc CRM. Nên:

- **Mở liên kết** (`url` có giá trị) — bấm là mở trang.
- **Trả lời nhanh** (`url` rỗng) — bấm là khách **nói đúng câu trên nút**. Nền tảng gửi chữ đó
  về như một tin của khách, rồi trợ lý xử như mọi câu khác.

Vế thứ hai khép kín mà không cần cơ chế nào thêm: bộ bóc tin **vốn đã** ghi lượt bấm bằng CHỮ
TRÊN NÚT chứ không phải mã kỹ thuật (xem `MetaMessagingParser`). Không có trạng thái nào phải
giữ giữa hai lượt.

| Kênh | Số nút tối đa | Ghi chú |
|---|---|---|
| Messenger · Instagram | **13** trả lời nhanh · **3** nếu có nút liên kết | hai cơ chế khác nhau, xem dưới |
| Telegram | 8 | `inline_keyboard`, gộp cả hai kiểu vào một cơ chế |
| Zalo | 5 | `oa.open.url` / `oa.query.show` — tên trường khác hẳn Meta |
| WhatsApp | 3, và **0 nếu có nút liên kết** | nút liên kết chỉ sống trong mẫu đã duyệt |
| TikTok | 0 | không có nút |

⚠️ **Vượt giới hạn là nền tảng từ chối CẢ TIN**, không phải cắt bớt nút — khách không nhận được
gì. Vì thế `ChatRules.FitButtons` cắt trước khi gọi API, ở **cả hai chỗ**: endpoint `/send` (để
báo lại cho nhân viên trong cùng lượt bấm) và worker gửi (chốt chặn cuối). Cắt thì **phải nói
ra** — im lặng cắt thì nhân viên soạn năm nút, khách thấy ba, không ai biết vì sao.

⚠️ **Meta có HAI cơ chế nút khác hẳn nhau** — chọn nhầm thì tin vẫn đi nhưng hỏng khó thấy:
`quick_replies` (tối đa 13, không chứa được liên kết, biến mất sau khi bấm) và khung nút
`button` template (tối đa 3, chứa được liên kết, nằm lại trong dòng tin mãi mãi). Nhét liên kết
vào `quick_replies` là Meta bỏ luôn phần liên kết. Phần thuần ở
[`MetaButtonBuilder`](../../TourkitAiProxy.Services/Chat/Channels/MetaButtonBuilder.cs), có test.

⚠️ **Telegram cắt `callback_data` theo BYTE, không theo ký tự** — chặn ở 64 byte, mà tiếng Việt
có dấu tốn 2–3 byte một ký tự nên nhãn 25 chữ cái đã vượt, và Telegram từ chối cả tin.

Nút lưu ở cột `chat_messages.buttons` (jsonb) để vẽ lại khi đọc hội thoại cũ, và ở
`chat_quick_replies.buttons` để mẫu trả lời nhanh mang theo nút. Đọc lại **luôn qua**
`ChatRules.ReadButtons` — nó lọc `http(s)`, vì nút do người dùng tự đặt là dữ liệu không tin được.

### Tin mẫu đã duyệt — nhắn khi cửa sổ đã đóng

Hết 24h (Meta) hoặc 48h (Zalo) là hộp thư **câm hẳn**. Mẫu đã được nền tảng duyệt là đường
**duy nhất** còn lại. Giao diện đặt nút ngay trong hộp báo "hết hạn" ở ô soạn — đúng chỗ và
đúng lúc người dùng gặp bức tường.

| Kênh | Có mẫu? | Gửi theo | Tham số |
|---|---|---|---|
| WhatsApp | ✅ | id số điện thoại | đánh SỐ, tách theo khối |
| Messenger | ✅ | PSID, `messaging_type: UTILITY` | như WhatsApp |
| Zalo (ZNS) | ✅ | **SỐ ĐIỆN THOẠI của khách** | TÊN tự đặt |
| Instagram | ❌ | Meta không cấp mẫu cho kênh này | |
| Telegram · TikTok | ❌ | không có cửa sổ nên không cần | |

⚠️ **Ba cơ chế khác hẳn nhau, đừng gộp.** Meta ghép tham số **theo VỊ TRÍ trong mảng, không
đọc tên khoá** — sắp sai thứ tự là khách nhận tin với các ô hoán chỗ cho nhau, mà lượt gọi vẫn
trả về thành công. Và Meta đánh `{{1}}`, `{{2}}`… **đếm lại từ đầu trong TỪNG khối** (tiêu đề
có `{{1}}` riêng, thân tin có `{{1}}` riêng), nên khoá ô mang cả tên khối: `body:1`, `header:1`.
Phần thuần nằm ở [`MetaTemplateParser`](../../TourkitAiProxy.Services/Chat/Channels/MetaTemplateParser.cs),
dùng chung cho WhatsApp + Messenger, có test.

⚠️ **ZNS gửi theo SỐ ĐIỆN THOẠI, không theo id người dùng Zalo.** Một hội thoại Zalo đang mở
vẫn có thể không gửi ZNS được: mình biết khách là ai trên OA nhưng không biết số của họ. Vì thế
có `WhyBlocked` — kiểm TRƯỚC khi bày danh sách mẫu, để nhân viên không chọn mẫu, điền năm ô rồi
mới bị báo thiếu số.

⚠️ **ZNS của hộp thư chat tách hẳn khỏi ZNS của bản tin sáng.** Bản tin xếp vào
`dbo.OutboundMails` (SQL Server) rồi worker của `toutkit-app` mới rút ra gửi. Chat gọi thẳng
`business.openapi.zalo.me` bằng token OA của **chính kênh chat**, vì ba lý do: cần mã tin trả về
NGAY để gắn vào hội thoại; nhân viên bấm gửi và chờ kết quả trên màn hình; và hai kho dữ liệu
tách hẳn (chat ở PostgreSQL). **Đừng gộp lại.**

⚠️ Đường gửi mẫu **không đi qua hàng đợi gửi** như tin thường: hàng đợi kiểm cửa sổ gửi trước
khi gọi API, mà cả điểm của tin mẫu là gửi được KHI cửa sổ đã đóng — qua hàng đợi thì mọi tin
mẫu đều bị chính chốt chặn đó loại bỏ.

### Khôi phục hội thoại cũ

Câu hỏi hay gặp: *nối kênh xong, các đoạn chat có từ trước có lấy lại được không?* Câu trả lời
**khác nhau theo từng kênh**, và bốn trong sáu kênh là **không**:

| Kênh | Lấy lại được? | Đường nào |
|---|---|---|
| Messenger | ✅ | `GET /{pageId}/conversations` → `GET /{convId}/messages`, phân trang bằng cursor |
| Instagram | ✅ | y hệt Messenger, nhưng qua `graph.instagram.com` và token đi ở header |
| WhatsApp | ⚠️ chỉ khi chuyển từ ứng dụng WhatsApp Business | Meta **tự đẩy** qua trường webhook `history`, sau khi mình gọi `POST /{phoneNumberId}/smb_app_data` |
| Telegram | ❌ | Bot API chỉ cho bot thấy tin gửi tới nó **sau khi bot được tạo**. Không có API đọc quá khứ. |
| Zalo | ❌ | Open API không có đầu đọc hội thoại |
| TikTok | ❌ | có đầu đọc nhưng đòi tư cách **Messaging Partner**, phải xin duyệt riêng |

**Messenger / Instagram** — nằm sau cờ riêng `Features:ChatHistoryImport` và **người dùng tự bấm**
từng tài khoản (`POST .../accounts/{id}/import-history`, tra tiến độ bằng `GET` cùng đường). Không
tự chạy lúc nối: một Trang bán hàng lâu năm có hàng chục nghìn tin, và gọi Graph quá nhiều là
Facebook chặn tạm cả ứng dụng — lúc đó **tin trực tiếp cũng ngừng về**, tức lấy lịch sử làm hỏng
chính việc đang chạy. Chặn ở 200 hội thoại × 200 tin mỗi lượt; hết chặn thì bấm lại, phần đã ghi
được chống trùng bỏ qua.

[`MetaHistoryImporter`](../../TourkitAiProxy.Services/Chat/Channels/MetaHistoryImporter.cs) **không
ghi thẳng vào bảng tin** — nó xếp từng trang vào hàng đợi `chat_inbound_events` dưới vỏ
`{"tourkit_lich_su": …}`, rồi `MetaMessagingParser` nhận ra vỏ đó và bóc. Nhờ vậy được lại nguyên
bộ: chống trùng ở tầng CSDL, chạy lại được khi mất điện, và **dùng chung đúng một đường ghi tin với
webhook** thay vì một đường thứ hai âm thầm lệch dần.

⚠️ **Tin cũ phải mang cờ `IsHistory`**, và lõi xử lý nó ở một nhánh RIÊNG. Ba việc lõi làm với tin
thường đều sai với tin cũ: sinh câu trả lời (một năm lịch sử = hàng trăm câu trợ lý gửi cho khách
**hôm nay**, về chuyện đã xong từ lâu — không rút lại được), cho bot câm 30 phút tính từ giờ, và chờ
4 giây gộp tin nhân với vài nghìn tin. Thời điểm lấy từ `SentUtc` của chính gói tin, không phải giờ
nhập — bỏ qua là cả năm hội thoại dồn vào một phút. Sau khi ghi, `RecomputeActivityAsync` tính lại
mốc hoạt động **từ chính các tin**: dùng `TouchConversationAsync` thì hội thoại chết ba năm nhảy lên
đầu hộp thư như vừa có người nhắn.

⚠️ **Chiều tin không đọc từ "id của mình".** Ở Graph, `from.id != <id khách>` là tin của mình; ở
gói `history` của WhatsApp, `thread.id` **chính là** số khách nên `from != thread.id` là tin của
mình. Cả hai cách đều không cần biết id của chính công ty — mà id đó thì Instagram trả về khác hẳn
chuỗi `me` mình gửi lên, và gói lịch sử WhatsApp không phải lúc nào cũng có.

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

### Soi tệp khách gửi về kho riêng

⚠️ **URL của nền tảng có HẠN — không soi về kho mình thì hộp thư tự rỗng dần.** Meta ký hạn thẳng vào
URL (tham số `oe=`): đo trên hộp thư staging 27/08/2026, ảnh khách gửi hôm đó hết hạn **01/09/2026** —
sống đúng 5 ngày. Telegram còn ngặt hơn (chỉ `file_id`, đổi ra đường tải sống ~1 giờ), WhatsApp đòi khoá
khi tải. Quá hạn là **mất hẳn**, không có API nào lấy lại.

[`ChatMediaMirror`](../../TourkitAiProxy.Services/Chat/Inbox/ChatMediaMirror.cs) tải → nén (ImageSharp,
cạnh dài 1600, JPEG q82; **bỏ qua ảnh động và tệp dưới 300KB**) → băm → ghi kho. **Nén TRƯỚC khi băm** —
băm trước rồi nén thì hai lần nhận cùng một ảnh ra cùng khoá nhưng nội dung đã khác, chống lặp mất tác
dụng đúng ở ca nó cần nhất. Hai khoá cho hai loại: nhãn dán `sticker/{kênh}/{sticker_id}` **dùng chung
mọi công ty** (mã nền tảng cố định — cái like luôn là `369239263222822`, hỏi kho TRƯỚC khi tải nên lượt
sau không chạm mạng); ảnh khách `chat/{công ty}/{sha256}` **khoá theo tenant**, hai công ty không bao
giờ dùng chung một đối tượng.

Tin MỚI soi ngay lúc nhận. Ảnh CŨ do
[`ChatMediaBackfillWorker`](../../TourkitAiProxy.Services/Chat/Inbox/ChatMediaBackfillWorker.cs) vét nền,
**tự động, không có nút bấm nào** — việc cứu dữ liệu có hạn chót thì người trực không có cách nào biết mà
bấm. Ảnh đại diện khách đi cùng đường: soi lúc lấy hồ sơ, và có luồng vét riêng.

**Chống phình theo dữ liệu — ba luật, đọc trước khi sửa:**

| Luật | Vì sao |
|---|---|
| **Một cột cờ, không ba cột** — `chat_messages.media_state`, `chat_contacts.avatar_state`. Số **không âm** = đã thử bấy nhiêu lần, còn trong hàng chờ; **−1** = xong; **−2** = thôi. | Gộp cả "thử mấy lần / xong chưa / có bỏ không" vào một cột. Nhờ số âm mà vị từ chỉ mục chỉ cần `>= 0` — đổi số lần thử trong mã không phải dựng lại chỉ mục. |
| **Chỉ mục CÓ ĐIỀU KIỆN** `ix_msg_media_cho (media_state, id) WHERE media_state >= 0 AND direction = 0 AND attachment IS NOT NULL` | Chỉ mục chỉ chứa tin CÒN phải soi → chi phí tỉ lệ **phần việc còn lại**, không tỉ lệ cỡ bảng. Tin chữ không có đính kèm nên không bao giờ lọt vào. Vét sạch rồi thì vòng quét chỉ là một lượt hỏi rỗng. ⚠️ Câu `WHERE`/`ORDER BY` của `ClaimMediaAsync` phải KHỚP vị từ này — lệch một chữ là Postgres quay ra quét cả bảng, vẫn đúng nhưng chậm dần và **không lỗi nào hiện ra**. `ChatSchemaGuardTests` canh. |
| **NHẬN việc, không liệt kê** — `ClaimMediaAsync` / `ClaimAvatarsAsync` dùng `FOR UPDATE SKIP LOCKED` + tăng cờ ngay trong cùng câu lệnh. | Tin soi hỏng **cố ý không bị ghi đè** (giữ `file_id` Telegram trong gói gốc), nên nếu chỉ liệt kê thì mỗi vòng lại tải lại đúng những ảnh đã chết — mà phần đã chết chỉ tăng theo thời gian, tới lúc nó chiếm trọn mọi vòng và ảnh còn cứu được không bao giờ tới lượt. |

**Không có cột mốc thời gian thử lại.** Giãn cách giữa hai lượt = nhịp vòng quét: mẻ đầu của vòng
không chặn tầng và trả về `BackfillResult.Tier` (số lần đã thử thấp nhất nó gặp), từ mẻ sau con số đó
thành **trần truyền xuống tận câu `WHERE`**. Mỗi tin chỉ được thử đúng một lần mỗi vòng, nên sự cố mạng
5 phút không đốt sạch số lần thử của cả hộp thư.

⚠️ **Trần phải chặn ở TRUY VẤN, không phải ở vòng lặp worker.** Bản đầu để worker tự nhận ra "mẻ vừa
rồi đã sang tầng trên" rồi thoát — lúc nhận ra thì mẻ đó đã tải xong, tức tin tầng trên vẫn ăn thêm một
lượt oan. Bắt được trên dữ liệu thật 28/08/2026: một khách duy nhất trong hàng chờ mà cờ nhảy thẳng
`0 → 2` trong một vòng. Còn đúng một chỗ chưa khít và cố ý bỏ qua: mẻ ĐẦU có thể vắt qua ranh giới hai
tầng khi việc còn lại ít hơn một mẻ (tối đa 24 dòng ăn thêm một lượt) — chặn nốt thì phải thêm một
truy vấn "tầng thấp nhất là mấy" cho MỌI vòng quét.

Nhà cung cấp trả 4xx (trừ 408/429) = **hết cứu** → bỏ ngay, không đợi đủ 5 lượt.

**Ảnh hưởng tới web đang chạy** được chặn bằng: một việc một lúc (nhiều nhất 1 lõi CPU) · **nghỉ bằng
đúng thời gian vừa làm** (2–30s → chiếm không quá nửa thời gian) · bắt đầu sau khởi động 1 phút · nhịp
tự đổi (còn cứu được ảnh → 15 phút; hết việc → 6 tiếng).

`POST /api/v1/chat/media/backfill` là đường **gọi tay lúc gấp** cho một công ty — không có nút nào trên
giao diện gọi nó, và cố ý vậy.

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

**Tên định danh trong cụm chat là TIẾNG ANH — toàn bộ, không ngoại lệ** (chuẩn hoá 27/08/2026).
Kiểu, hàm, thuộc tính, hằng, thành viên enum: tiếng Anh. **Chữ hiển thị, log và chú thích vẫn
tiếng Việt** như quy ước chung ở [conventions.md](../conventions.md) — luật đó nói về *nội dung*
cho người đọc, không nói về tên định danh.

⚠️ **Vì sao đổi.** Trước đó cụm này pha trộn: `ChatRules` đặt tiếng Việt (`TinhCuaSo`, `GhepCum`,
`KhongLui`), còn `ChatRepository`/`ChatModels` đặt tiếng Anh, và luật là "theo file mình đang sửa".
Luật đó **không sống nổi**: chỉ trong một buổi đã có hai lần đặt nhầm — một tên tiếng Việt lọt vào
`ChatRepository` giữa 26 tên tiếng Anh, đúng cái bẫy tài liệu đã cảnh báo. Một quy ước mà người
đọc nó xong vẫn vi phạm thì quy ước ấy sai, không phải người đọc sai.

⚠️ **Vài chỗ đổi tên KHÔNG phải chỉ đổi chữ, đọc kỹ nếu bạn nhớ tên cũ:**

| Cũ | Mới | Ghi chú |
|---|---|---|
| `ChatState.Cho/DaGui/DaNhan/DaXem/Hong` | `Pending/Sent/Delivered/Seen/Failed` | số trong CSDL **không đổi** |
| `ChatKind.Chu/Anh/Tep/AmThanh/ViTri` | `Text/Image/File/Audio/Location` | như trên |
| `ChatDirection.Vao/Ra` · `ChatSender.Khach/NhanVien/HeThong` | `In/Out` · `Customer/Agent/System` | như trên |
| `KhongLui` | `CanAdvanceState` | tên cũ nói điều **cấm**, tên mới nói điều **cho phép** — nghĩa giữ nguyên, đọc ngược lại |
| `TinhCuaSo` | `ComputeSendWindow` | |
| `XacMinhDungChungAsync` | `ResolveSharedWebhookAsync` | |
| `HoSoKhach` | `ContactProfile` | trường `Ten`/`Anh` → `Name`/`AvatarUrl` |
| `ChatLan` | `ChatLane` | |

⚠️ **Thành viên enum đổi tên nhưng GIÁ TRỊ SỐ giữ nguyên** — `chat_messages.state`,
`chat_conversations.status`, `direction`, `kind`, `channel` đều lưu số, nên dữ liệu cũ không cần
chuyển đổi gì. Đừng "sửa" bằng cách đánh số lại cho đẹp: làm thế là hỏng toàn bộ lịch sử chat.

**Biến cục bộ vẫn còn tên tiếng Việt** (`than`, `chu`, `luc`, `kenh`…) và **cố ý để nguyên**: chúng
chỉ sống trong một hàm, không phải bề mặt API, còn đổi hàng loạt thì mỗi phép thay lại có nguy cơ
sửa trúng chữ trong câu chú thích tiếng Việt. Viết mới thì đặt tiếng Anh.

**Sáu luật sai-là-hỏng**, tách thuần ở [`ChatRules`](../../TourkitAiProxy.Domain/Chat/ChatRules.cs), có test:
1. **Cửa sổ gửi** — Zalo 48h / Meta 24h kể từ tin cuối CỦA KHÁCH, và **thêm 7 ngày cho nhân viên**
   ở Messenger + Instagram. **Chưa có tin nào của khách = ĐÓNG**, không phải mở. Hết cửa sổ thì
   khoá ô soạn kèm lý do, đừng để gọi API rồi mới biết.
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

