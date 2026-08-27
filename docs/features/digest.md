# Bản tin AI và Bảng tin

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

---

## Bản tin AI ("Đợt 1" — bản tin sáng + Bảng tin)

Bản tin chủ động gửi mỗi sáng, thay vì bắt người dùng tự vào hỏi. Spec + plan đầy đủ:
[specs/2026-08-11-dot1-digest-insight-design.md](../superpowers/specs/2026-08-11-dot1-digest-insight-design.md) ·
[plans/2026-08-11-dot1-digest-insight.md](../superpowers/plans/2026-08-11-dot1-digest-insight.md).

⚠️ **CẢ CỤM NÀY NẰM SAU CỜ `Features:Digest` — mặc định TẮT** (chưa ra mắt; thiếu key = tắt, cố ý sai
theo hướng an toàn). Một cờ cho cả 3 tác vụ `sale-brief` · `ceo-brief` · `payment-watchdog` + Bảng tin,
vì với người dùng chúng là MỘT tính năng: cả 3 đều ghi vào Bảng tin và Bảng tin là chỗ đọc lại.
Bật: `appsettings.json` → `"Features": { "Digest": true }` **ở CẢ web lẫn worker** (worker mới là nơi
thật sự chạy tác vụ nền — web tắt mà worker bật thì bản tin vẫn gửi cho khách dù giao diện đã ẩn sạch),
rồi restart. Tắt thì: 3 workflow không đăng ký DI ([`WorkflowStackRegistration`](../../TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs))
→ biến mất khỏi scheduler + `GET /api/v1/workflows` → thẻ tự mất khỏi trang; `/api/v1/insights|digest/*`
trả 404 tường minh; chuông + tab Bảng tin + khối Zalo OA + mục admin "Bản tin" bị ẩn qua
[`GET /api/v1/features`](../../TourkitAiProxy.Endpoints/SystemEndpoints.cs) → [`window.tourkitFeatures`](../../wwwroot/core/features.js).
**Không xoá dữ liệu** — `dbo.DigestSubscriptions`/`dbo.UserWorkflows` giữ nguyên, bật lại là còn đủ.
Cờ này KHÁC phân quyền: tắt là tắt cho tất cả, kể cả admin.

> **Bẫy đã dính 1 lần:** không map endpoint ≠ 404. `app.MapFallback` (SPA deep-link) nuốt mọi đường dẫn
> không khớp kể cả `/api/**` và trả `index.html` **status 200** → client gọi API nhận HTML thay vì lỗi.
> Vì thế nhánh `else` trong [Program.cs](../../Program.cs) phải map tay 2 tiền tố về 404 JSON.

**2 loại bản tin** (`BriefTypes`): `sale-brief` — việc cần làm của từng nhân viên bán hàng (cơ hội cần
gọi, lịch hẹn, việc, báo giá, tour còn thiếu tiền) — **số do máy chủ lấy, AI sắp xếp lại cho gọn** (tốn 1 lượt/người/ngày, tắt bằng tuỳ chọn `useAi=false`; AI lỗi → rơi về bản rule); `ceo-brief` — doanh thu/chi
phí/lợi nhuận so cùng kỳ, **AI chỉ viết lời còn số do máy chủ tính**, AI lỗi → in bảng số
([`CeoBriefBuilder.RenderFallback`](../../TourkitAiProxy.Domain/Digest/CeoBriefBuilder.cs)).

**Cách chạy — CHUẨN BỊ TRƯỚC, GỬI QUA HÀNG ĐỢI** (đổi 13/08, xem
[plan](../superpowers/plans/2026-08-13-digest-queue-pipeline.md)). Cả 2 là `PerTenant` (1 bản ghi
scheduler, bật 1 lần) nhưng workflow **tự đổi phiên theo từng người nhận**; workflow KHÔNG gửi gì cả:

1. **PREPARE** — từ mốc `giờ người chọn − Digest:LeadMinutes` (mặc định 10') trở đi, workflow dựng nội
   dung ([`DigestDue.ShouldPrepare`](../../TourkitAiProxy.Domain/Digest/DigestDue.cs) — so theo **phút**, mở tới hết ngày VN).
2. **GHI Bảng tin** — `dbo.AgentInsights`. Đây là kênh "trong app" **luôn bật** (kho lưu để xem/nghe lại).
3. **ENQUEUE** — mỗi kênh ngoài đang bật = 1 dòng `dbo.OutboundMails` với `ScheduledUtc`
   ([`DigestEnqueuePlanner`](../../TourkitAiProxy.Domain/Digest/DigestEnqueuePlanner.cs) + [`DigestDue.SendMomentUtc`](../../TourkitAiProxy.Domain/Digest/DigestDue.cs)).
   Dòng mang theo **đủ thứ cần để gửi**: email → `Params`; telegram/zalo → `Data` chứa nơi nhận +
   `title` + `body`.
4. **GỬI** — **KHÔNG phải việc của proxy.** Cả 3 kênh do **`TourKit.PushWorker` bên toutkit-app** rút
   (`PushNotification.Worker/OutboundQueueWorker.cs`, nhịp 30s).

⚠️ **MỘT hàng đợi, MỘT nơi tiêu thụ** (sửa 14/08). Trước đó proxy có bộ rút riêng cho telegram/zalo →
hai tiến trình cùng poll một bảng, và cái nhanh hơn nuốt mất dòng của cái kia: worker mail (30s) vớ dòng
telegram, không thấy email nên đánh dấu `Status=4` "thiếu người nhận"; bộ rút proxy chỉ tìm `Status=0`
nên **không bao giờ thấy nữa** → bản tin Telegram biến mất im lặng. Nay proxy chỉ XẾP hàng đợi.
Đừng thêm bộ rút thứ hai ở đây.

Chống dựng trùng trong ngày = `InsightRepository.ExistsTodayAsync` (không còn `LastSentLocalDate`).
Vì "đã dựng" đọc từ Bảng tin và mốc gửi nằm trên dòng hàng đợi, **máy chủ sập đúng khung giờ không còn
làm mất bản tin của ngày** — bật lại là dựng/gửi bù. Điều kiện: người đó đã từng đăng nhập
(`dbo.TkSessions` giữ mật khẩu mã hoá + tự re-login, 30 ngày).

**Ba trạng thái kết thúc** (worker quyết, xem `IOutboundChannelSender`): gửi được → `Status=1`; hỏng mà
thử lại vô ích (thiếu nơi nhận, công ty chưa khai OA, Zalo hết cửa sổ 48h) → `Status=4` + lý do; hỏng
tạm thời (mạng, nhà cung cấp 5xx) → tăng `RetryCount`, hết lượt (`OutboundMail:MaxRetries`, mặc định 3)
mới thành `Status=2`.

**`Telegram:BotToken` — BẮT BUỘC ở worker, gần như không cần ở proxy.** Worker cần để gửi bản tin thật.
Proxy chỉ còn dùng cho MỘT tiện ích: `POST /digest/telegram/detect` (quét `getUpdates` để tìm chat id
giúp người dùng). Thiếu ở proxy thì endpoint đó trả 503 kèm gợi ý tự dán chat id — không ảnh hưởng gì
tới bản tin.

⚠️ **Thứ tự deploy:** `TourKit.PushWorker` (bản có adapter kênh) phải lên **TRƯỚC**, proxy bật
`Features:Digest` **SAU** — worker cũ không biết cột `Channel`, vớ dòng telegram/zalo rồi đánh dấu
"thiếu email người nhận" là mất tin.

⚠️ **Fetch bằng phiên CỦA NGƯỜI NHẬN, KHÔNG dùng service account** — đây là quyết định có chủ đích:
service account có quyền xem toàn công ty, lọc sai 1 dòng là nhân viên A đọc được cơ hội của nhân viên B.
Dùng token của chính họ thì **CRM tự chặn** → lọc sai chỉ thiếu, không lộ. Vì thế proxy cũng **KHÔNG tự
gác quyền** khi đăng ký: `DashboardService.ResolveSpUserIdAsync` (TourKit.Api) chỉ cho "xem tất cả" khi
tài khoản có `BC_NV_XEM`, còn lại SP tự lọc về số của riêng họ; và proxy không truyền `userId` —
`AiController.GetClaims()` bóc từ JWT.

⚠️ **Hệ quả của việc dùng phiên người nhận: không có phiên = không có bản tin.** Trước 27/08/2026
chỗ này **hỏng im lặng** — workflow bỏ qua người đó rồi ghi log *"chưa đăng nhập lần nào"*, câu đó
**sai** với cả hai nguyên nhân thật, và người mất bản tin không bao giờ biết mình đang mất.

Nay tách hai bệnh, mỗi bệnh một câu hướng dẫn khác nhau
([`BriefReadiness`](../../TourkitAiProxy.Domain/Digest/BriefReadiness.cs), hàm thuần, có test):

| Mã lưu ở `NotReadyReason` | Nghĩa | Người dùng cần làm |
|---|---|---|
| `thieu-phien` | Không còn dòng phiên nào | Đăng nhập một lần |
| `dang-nhap-lai-hong` | Có phiên nhưng xin lại chìa khoá hỏng | Kiểm tra tài khoản bên CRM trước |

⚠️ **KHÔNG được viết "chưa đăng nhập lần nào".** Dòng phiên đã bị dọn nên không phân biệt được
"chưa từng" với "hết hạn"; khẳng định sai với người đã dùng nhiều tháng là họ mất tin vào cả tính năng.

⚠️ **Báo MỘT lần rồi TẮT đăng ký** (`MarkNotReadyAsync` ghi lý do và `Enabled=0` trong **cùng một
lệnh**). Tắt để lượt sau khỏi kiểm lại và không có lá thư thứ hai. Người dùng đăng nhập, thấy dải
cảnh báo trên thẻ "Bản tin của tôi", tự bật lại — `UpsertAsync` xoá ba cột trạng thái khi `Enabled=1`.

⚠️ **Thứ tự: báo TRƯỚC, tắt SAU.** Tắt trước mà xếp thư hỏng thì họ mất bản tin và không hề được
báo — đúng cái lỗi đang sửa, chỉ khác là do mình gây ra. Báo hỏng thì **giữ nguyên đăng ký**.

⚠️ **Lời nhắc chỉ đi qua THƯ và trong app.** Telegram bỏ vì lời nhắc hành chính không nên chen vào
kênh chat; **Zalo bỏ vì ZNS chỉ chở được mẫu đã duyệt** — gửi tự do là chắc chắn bị từ chối, mà mỗi
lần hỏng lại đẻ một dòng hàng đợi làm người đọc nhật ký tưởng kênh Zalo đang lỗi. Ai **chưa khai
email** thì không nhắc ra ngoài được: con số đó phải hiện trong tóm tắt lượt chạy, đừng nuốt.

⚠️ **Dòng ghi vào Bảng tin mang `Kind` RIÊNG** (`brief-login-required`), không mang loại bản tin —
ghi cùng loại thì lượt sau hệ thống tưởng "hôm nay gửi rồi" và bản tin thật không bao giờ tới.

**Chữ hiển thị sinh ở MÁY CHỦ** (`NotReadyLabel` / `NotReadyAction` — thuộc tính tính toán nên tự
vào JSON). Giao diện chỉ việc in ra: chép bảng ánh xạ mã→chữ sang JavaScript là hai bản, và thêm
một mã mới thì màn hình lặng lẽ hiện mã kỹ thuật kiểu `thieu-phien` cho người dùng đọc.

⚠️ **Mốc dọn phiên 30 ngày KHÔNG phải nguyên nhân.** `GetValidJwtAsync` cập nhật `LastUsed` mỗi lượt
chạy, nên người nhận bản tin hằng ngày **không bao giờ bị dọn** kể cả khi không mở app — chuỗi tự
nuôi nó. Bị dọn là *hệ quả* của việc gì đó đã cắt đứt chuỗi suốt 30 ngày trước đó.

**Nơi nhận — 1 kho lưu + 3 kênh gửi.** "Trong app" (`dbo.AgentInsights`) **KHÔNG phải kênh gửi** mà là
**kho lưu luôn bật**: bản tin ghi vào đó lúc dựng, trước khi nghĩ tới chuyện gửi đi đâu — nên mọi kênh
ngoài hỏng hết thì vẫn còn chỗ xem/nghe lại. Server ép `ChannelInApp=true` khi lưu đăng ký; UI khoá ô
tick. 3 kênh gửi thật ([`Services/Digest/Channels/`](Services/Digest/Channels/)): email (`TemplateCode=daily-brief`,
worker toutkit-app gửi) · Telegram (bot DÙNG CHUNG `Telegram:BotToken` — miễn phí nên hệ thống cấp) ·
Zalo (**ZNS — nhắn theo SỐ ĐIỆN THOẠI**, qua **OA RIÊNG của từng công ty**, khai ở mục "Theo tổ
chức" — xem 3 điều dễ hiểu sai bên dưới). Một kênh hỏng KHÔNG làm chết kênh còn lại.

**Nơi nhận khai MỘT LẦN cho mọi thông báo** (17/08). `dbo.DigestSubscriptions` vốn đã là *một dòng
mỗi người* (PK `TenantId+Username`) và giữ sẵn email/chat id/số Zalo — nên nó **là** hồ sơ nơi nhận
dùng chung, chỉ từng bị đặt tên và đặt vị trí như thể của riêng bản tin sáng. Nay tách rõ: khối
**"Nơi nhận của tôi"** ([`MyChannelsBlock`](../../wwwroot/pages/digest.jsx)) đứng đầu mục "Theo người dùng",
lưu qua endpoint RIÊNG `PUT /api/v1/digest/my-channels` — **không đụng** `BriefType`/`Enabled`/
`SendHourLocal`. Gộp vào endpoint đăng ký thì mỗi lần đổi email lại phải gửi kèm loại bản tin + giờ
nhận, client quên một trường là **âm thầm tắt đăng ký của chính người đó**. Cảnh báo cấp công ty
(vd `payment-watchdog`) đọc cùng hồ sơ này qua `ListWithChannelsAsync` — **không** lọc theo `Enabled`
(cờ đó nói về bản tin sáng; một người có thể không nhận bản tin nhưng vẫn muốn nhận cảnh báo).
KHÔNG thêm bảng mới cho việc này.

⚠️ **Zalo: 3 điều dễ hiểu sai** (đổi 14/08 — trước đó code dùng API `message/cs` theo Zalo user id):
1. **Nơi nhận là SỐ ĐIỆN THOẠI**, không phải Zalo user id. Người dùng chỉ nhập số của mình; server
   chuẩn hoá về `0xxxxxxxxx` ngay lúc lưu ([`DigestPhone`](../../TourkitAiProxy.Domain/Digest/DigestPhone.cs)), worker đổi
   sang `84…` lúc gọi API. Cột DB tên `ZaloPhone` (đổi từ `ZaloUserId`
   ngày 14/08 bằng `sp_rename` trong `SchemaSql` — tên cột nói sai nội dung là bẫy cho người sau).
2. **ZNS KHÔNG gửi được chữ tự do** — chỉ điền tham số vào mẫu đã được Zalo duyệt. Nên tin Zalo là
   **lời nhắc ngắn**; bản tin đầy đủ đọc ở Bảng tin (kênh trong app luôn bật nên chắc chắn có).
3. **OA RIÊNG từng công ty** (quay lại per-tenant 17/08 — bản 14/08 từng gỡ để dùng OA chung).
   Lý do đảo quyết định: đi gặp khách hàng thì **không công ty nào chịu dùng OA chung** — tin ZNS
   hiện **tên OA người gửi**, nên gửi bằng OA của bên cung cấp dịch vụ nghĩa là khách của họ nhận
   tin mang tên một công ty khác. Lập luận cũ ("bắt mỗi công ty tự đăng ký mẫu thì họ bỏ dở") tính
   đúng chi phí khai báo nhưng bỏ qua chuyện thương hiệu, mà đó mới là thứ quyết định.
   - 3 endpoint `/api/v1/digest/zalo-config` (GET/PUT/DELETE) **khôi phục**, gác `CH_HT_XEM`;
     lưu ở `dbo.TenantChannelSettings` (`Channel='zalo'`) qua
     [`TenantChannelSettingsStore`](../../TourkitAiProxy.Infrastructure/Digest/TenantChannelSettingsStore.cs). Giao diện nằm
     **cùng thẻ với tài khoản dịch vụ** trong mục "Theo tổ chức" — cả hai đều là thông tin đăng
     nhập cấp công ty, khai một lần.
   - **MỘT bộ ô, MỘT đường code — `mode` đã gỡ khỏi giao diện (18/08).** Dù OA do công ty tự đăng ký
     hay do bên cung cấp dịch vụ đưa sẵn, thứ phải dán vào vẫn đúng CÙNG bộ `oaId` + `appId` +
     `secretKey` + `refreshTokenSeed` — khác nhau duy nhất ở chỗ giá trị lấy từ đâu, mà đó không phải
     việc hệ thống cần biết. Trước đây có ô chọn "Dùng OA nào"; nó **không đổi hành vi gì** (không
     dòng nào ở proxy lẫn worker rẽ nhánh theo nó) nên chỉ tổ bắt người khai dừng lại phân vân chọn
     sai thì sao. Trường `Mode` giữ lại ở DTO/DB cho client cũ, mặc định `own`, **không ai đọc** —
     đừng dựng lại logic dựa trên nó. Chuyện "OA của bên cung cấp" nay nói bằng một câu trong khối
     hướng dẫn "Lấy bốn thông tin này ở đâu?".
     ⚠️ `refreshTokenSeed` **bắt buộc**: App ID + Secret không lấy được token — Zalo đổi refresh
     token lấy access token, mà refresh token đầu chỉ có sau bước cấp quyền OA. Thiếu nó thì công ty
     khai xong tưởng chạy được, worker không bao giờ lấy nổi token.
     **KHÔNG có đường rơi ngầm**: chưa khai đủ → kênh Zalo không gửi và nói thẳng, tuyệt đối không
     lặng lẽ gửi bằng danh nghĩa đơn vị khác.
   - **Mã mẫu ZNS khai theo TỪNG CHỨC NĂNG** (`sale-brief` · `ceo-brief` · `payment-alert`): Zalo
     duyệt mẫu theo nội dung nên bản tin sáng và nhắc thu tiền là hai mẫu khác nhau. Danh sách 1
     nguồn ở `DigestEndpoints.ZaloTemplateFeatures` — thêm chức năng gửi Zalo mới = thêm 1 dòng,
     giao diện tự mọc ô nhập. Mã mẫu được **đính kèm ngay trên dòng hàng đợi** (`Data.templateId`)
     chứ không bắt worker tự tra: worker đọc bảng của proxy càng ít càng tốt, và lúc gửi mới tra
     thì mẫu có thể đã đổi so với lúc dựng nội dung.
   - ⚠️ **Lưu là HỢP NHẤT, không ghi đè cả cục.** `ConfigJson` có hai chủ: phần khai tay (giao diện)
     và `refreshToken`/`accessToken` do **worker xoay vòng** ghi lại. Ghi đè trọn gói từ giao diện
     sẽ xoá token worker vừa làm mới → kênh Zalo chết ngay sau lần lưu cấu hình kế tiếp mà không
     lỗi nào hiện lên. Bí mật gửi lên rỗng = **giữ nguyên** bản đang lưu (giao diện không đọc lại
     được bí mật nên không thể gửi lại).

⚠️ **Proxy KHÔNG có lớp gửi nào** (gỡ 14/08: `IDigestChannel`, `DigestDispatcher`, 3 lớp kênh,
`TelegramFormat`). Kể cả nút **"Gửi thử"** cũng chỉ **xếp hàng đợi** bằng CHÍNH `DigestEnqueuePlanner`
mà workflow dùng mỗi sáng. Trước đó gửi thử có đường riêng, nghĩa là "Gửi thử OK" **không chứng minh
được** bản tin thật gửi được — hai đường khác nhau. Nay chung một đường: thử thành công là bằng chứng
thật, và khoá OA/bot không phải nhân đôi sang proxy. Đổi lại kết quả không tức thì (tới nhịp rút kế,
~1 phút) — endpoint nói rõ điều đó trong `summary`.

⚠️ **"Gửi thử" CỐ Ý không ghi vào `dbo.AgentInsights`.** Hai lý do, cái đầu là lỗi thật đã suýt lọt:
(1) mốc chống trùng của bản tin thật (`InsightRepository.ExistsTodayAsync`) đếm dòng trong bảng đó —
bản thử ghi vào thì ai bấm "Gửi thử" buổi trưa sẽ **mất bản tin thật sáng mai**, vì workflow tưởng hôm
nay chuẩn bị rồi; (2) Bảng tin là nơi xem/nghe **lại bản tin thật**, nhét bản thử vào làm bẩn lịch sử.
Gửi thử là để thử **kênh ngoài** — kênh trong app luôn bật, không cần thử. Không bật kênh ngoài nào thì
endpoint trả `ok:false` nói thẳng là không có gì để thử.

**Một enum kênh duy nhất** — [`OutboundChannel`](../../TourkitAiProxy.Domain/Digest/OutboundChannel.cs): `0=Email`,
`1=Telegram`, `2=Zalo`, lưu thẳng cột `dbo.OutboundMails.Channel` (TINYINT). Default 0 nên dòng cũ trong
DB tự đúng nghĩa. Worker toutkit-app **mirror đúng bảng số này**
([docs/mail-templates/README.md](../mail-templates/README.md)) — thêm kênh mới = thêm 1 member ở CẢ 2
repo + 1 lớp `IOutboundChannelSender` bên worker (KHÔNG đụng vòng lặp, KHÔNG đụng kênh cũ).
`ChannelMask`/`DigestChannel`/`InAppChannel` **đã gỡ hẳn**
(13/08): cờ bit "đã gửi kênh nào hôm nay" hết lý do tồn tại khi mỗi kênh đã là một dòng có `Status` riêng.
Cột `SentMask`/`SentAttempts` còn trong DB nhưng **code không ghi nữa**.

**Giao diện — GỘP trong trang Tự động hoá, KHÔNG có trang riêng** (chốt 12/08: đăng ký bản tin chính là
cấu hình của 2 tác vụ đó). `/workflows` có 2 tab: "Tác vụ" (thẻ bản tin chứa khối **"Bản tin của tôi"** —
[`digest.jsx`](../../wwwroot/pages/digest.jsx)) và "Bảng tin" ([`insights.jsx`](../../wwwroot/pages/insights.jsx)).
Zalo OA nằm cạnh tài khoản dịch vụ trong nhóm "Theo tổ chức". `/insights` + `/digest` là 2 đường cũ trỏ
về đúng tab (chuông ở thanh trên dùng `/insights`). Item bản tin trong Bảng tin có nút **Nghe** (đọc qua
`/api/v1/speech/tts`, giọng server đồng nhất) — lời đọc do `BriefNarration.ToSpeakable` làm sạch từ
markdown; và mỗi người chỉ nhận **1 loại** bản tin theo vai trò (bật loại này tự tắt loại kia).

**Phân vai quyền:** "Bản tin của tôi" (nơi nhận của chính mình) → KHÔNG cần quyền, giống hộp thư cá nhân.
Lịch chạy + tài khoản dịch vụ + Zalo OA → cần `CH_HT_XEM`. Vì trang này nay có phần cá nhân nên mục menu
nằm ở khối **"Tích hợp"** (kiểm code 18/08) (không phải "Tích hợp") và route KHÔNG gate cứng.

**Theo dõi (admin):** `/admin-trav-ai` → **Bản tin**. Cần trang này vì **cả 3 kiểu hỏng của tính năng
đều IM LẶNG** — người dùng chỉ thấy sáng ra không có gì, không lỗi nào hiện lên: (1) đã đăng ký nhưng
công ty chưa bật lịch chạy, (2) bật kênh mà bỏ trống nơi nhận, (3) kênh gửi hỏng. Cột "Hôm nay" đọc từ
**hàng đợi** (đã gửi / hỏng / còn chờ tới giờ) thay cờ bit cũ; "Gửi lần cuối" = `MAX(ProcessedUtc)` của
dòng đã gửi. Cột "Vấn đề" tính ở server ([`AdminDigestRepository.DetectProblem`](../../TourkitAiProxy.Infrastructure/Admin/AdminDigestRepository.cs))
theo thứ tự nguyên nhân GỐC trước. Bộ đếm luôn là tổng THẬT kể cả khi đang lọc "chỉ lỗi" — lọc ở SQL thì
"3/12 có vấn đề" biến thành "3/3", đọc xong tưởng cả hệ thống hỏng.

**Cấu hình cần có:** `Telegram:BotToken` (rỗng = kênh Telegram tự tắt) · `Models:Digest` (thiếu → kế thừa
`Models:Primary`) · template mail `daily-brief` trong `/admin-trav-ai` → Mail Templates (thiếu thì worker
vẫn render từ `Params`) · `Digest:LeadMinutes|InsightKeepDays` (thiếu → 10/30).

⚠️ **Nhịp quét KHÔNG nằm trong config.** `Digest:CheckIntervalMinutes` từng có trong appsettings nhưng
**không dòng code nào đọc** — đã gỡ 14/08. Nhịp thật là `dbo.UserWorkflows.IntervalMinutes` của chính 2
tác vụ bản tin (ô "Kiểm tra ai đến giờ, mỗi" trên trang Tự động hoá; mặc định 15' do
`WorkflowEndpoints.DefaultInterval`). Quan hệ với `LeadMinutes`: workflow dựng nội dung từ mốc
`giờ chọn − Lead` và **hẹn `ScheduledUtc` đúng giờ người chọn**, nên trễ tối đa = `max(0, Interval − Lead)`
— đặt Interval ≤ Lead thì luôn đúng giờ. Sàn cứng là tick 60s của `WorkflowSchedulerService`.

**E2E:** [`scripts/e2e/features-digest.ps1`](../../scripts/e2e/features-digest.ps1) (tự sao lưu + khôi phục đăng
ký thật) · sơ đồ luồng: `node scripts/e2e/features-flow-diagram.check.js`.

