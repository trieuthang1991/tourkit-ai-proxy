# Chat đa kênh — Đợt 1: Zalo OA hai chiều + hộp thư chung — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Khách nhắn Zalo OA → tin vào hộp thư chung trong TourKit → AI trả lời bằng dữ liệu CRM →
nhân viên tiếp quản/giao việc được. Đủ 6 luật nghiệp vụ ở §6 của spec. Xong đợt này là **dùng được thật**.

**Spec:** [2026-08-20-omnichannel-chat-design.md](../specs/2026-08-20-omnichannel-chat-design.md) — ĐỌC TRƯỚC KHI LÀM.

**Tech Stack:** ASP.NET Core 8, Dapper + SQL Server (`TourkitAiDb.SchemaSql` idempotent), xUnit
(`TourkitAiProxy.Tests`), React no-build (`wwwroot/pages/*.jsx` + `bundle-entry.js`).

## Global Constraints

- Comment/log/chuỗi hiển thị **tiếng Việt**. DateTime **UTC** (`SYSUTCDATETIME()` / `DateTime.UtcNow`);
  giờ VN qua `TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")`.
- Cả cụm nằm sau cờ **`Features:Chat`**, mặc định TẮT — thêm 1 method vào
  [`FeatureFlags`](../../../Services/Bootstrap/FeatureFlags.cs), 1 field vào `GET /api/v1/features`,
  khai key ở **CẢ** `appsettings.example.json` lẫn bản worker. Tắt thì endpoint trả **404 tường minh**
  (nhớ map tay, `MapFallback` sẽ nuốt và trả `index.html` kèm 200).
- Bảng MỚI, **không sửa bảng cũ**. Tuyệt đối không nhét chat vào `dbo.OutboundMails`.
- Tenant lấy từ `ITenantContext`/`SessionAuth` — **KHÔNG nhận `tenantId` từ client**.
- DI đăng ký trong [`WorkflowStackRegistration.AddWorkflowStack`](../../../Services/Bootstrap/WorkflowStackRegistration.cs).
- AI gọi từ nền (không có HttpContext) PHẢI bọc `AiCallContext.Push("chat-reply", tenantId)` —
  thiếu là bypass hạn mức tenant và log `feature=unknown`.
- Thêm trang mới: khai ở **CẢ** `index.html` **VÀ** `bundle-entry.js` (thiếu một bên → prod trắng trang).

---

## Hạ tầng dữ liệu — SQL Server có đáp ứng không

Đo thật trên máy đang chạy (20/08/2026), không ước chừng.

**Hiện trạng:** SQL Server **2022 Developer Edition**, Windows Server 2022. Bảng lớn nhất
`PushLogs` 370.188 dòng / 185 MB; `Mails` 4.059 dòng / 116 MB (mỗi thư ~29 KB vì chứa HTML);
`Reviews` 9.381 dòng / 25 MB. Cả cơ sở dữ liệu chừng **350 MB**.

**Chat sẽ nặng cỡ nào:** ước cho công ty tour cỡ vừa — 50 hội thoại/ngày × 15 tin =
**750 tin/ngày ≈ 275.000 tin/năm**. Tin chat là chữ ngắn (không như email), chừng 1–2 KB/tin →
**~400 MB/năm**. `PushLogs` đã 370k dòng và chạy bình thường; kiểu truy vấn của chat (ghi dòng nhỏ,
đọc theo một hội thoại đã đánh chỉ mục) đúng là loại SQL Server làm tốt nhất.

→ **Lưu trữ và ghi/đọc: không phải lo.**

### Ba chỗ SQL KHÔNG làm được — và chốt cách xử lý

| Việc | ChatbotX làm sao | Đợt 1 chốt thế nào |
|---|---|---|
| Đẩy tin lên màn hình thời gian thực | Redis + websocket | **Hỏi lại mỗi 4 giây** khi đang mở hộp thư. Đủ dùng, ít việc. SignalR để đợt sau nếu thấy chậm |
| Hàng đợi gửi | BullMQ trên Redis | **Bảng + rút định kỳ**, đúng cách `OutboundMails` đang chạy tốt mấy tháng nay. Redis đã có sẵn trong cấu hình nếu cần đổi |
| Tìm theo ngữ nghĩa | `pgvector` | **Không làm.** Trợ lý trả lời bằng gọi công cụ đọc CRM, không tìm trong kho văn bản |

⚠️ **Giới hạn thật, ghi lại để sau khỏi ngạc nhiên:** SQL Server 2022 **không có kiểu dữ liệu
vector** — thứ đó tới SQL Server 2025 mới có. Nên câu kiểu *"tìm hội thoại nào khách từng hỏi về
Nhật Bản"* sẽ **không làm được** bằng hạ tầng hiện tại. Muốn có thì phải nâng SQL lên 2025, hoặc
dựng kho vector riêng. Đừng hứa tính năng đó ở đợt 1.

### Hai rủi ro hạ tầng phải xử TRƯỚC khi code

**① Máy chủ đang rớt kết nối lai rai.** Riêng ngày 20/08 gặp **ít nhất 4 lần**
`wait operation timed out`: lúc chạy 5 tác vụ nền song song, lúc truy vấn thường, và một lần làm
giao diện TRAVAI trả 500 cho người dùng. Chat hỏi cơ sở dữ liệu **liên tục** chứ không thưa như bản
tin — chuyện đang khó chịu sẽ thành hỏng thấy rõ. **Tìm nguyên nhân trước.**

**② Developer Edition không được phép dùng cho sản xuất.** Bản này miễn phí và đầy đủ tính năng như
Enterprise, nhưng giấy phép Microsoft chỉ cho **phát triển và kiểm thử**. Nếu máy đo được ở trên là
máy chạy thật thì đó là vấn đề giấy phép, không phải kỹ thuật. Nếu chỉ là staging thì phải biết máy
thật chạy bản nào: **Express giới hạn 10 GB mỗi cơ sở dữ liệu**, với đà chat ~400 MB/năm cộng dữ
liệu sẵn có thì vài năm là chạm trần — và lúc chạm thì **ghi vào hỏng, không phải chạy chậm**.

---

### Task 1: Bảng dữ liệu + cờ tính năng

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs` (thêm block cuối `SchemaSql`)
- Modify: `Services/Bootstrap/FeatureFlags.cs`, `Endpoints/SystemEndpoints.cs`, `wwwroot/core/features.js`
- Modify: `docs/database-schema.md`, `appsettings.example.json`

- [ ] **Step 1:** Thêm 4 bảng vào `SchemaSql` (idempotent `IF OBJECT_ID(...) IS NULL`):

```sql
-- Danh tính khách theo TỪNG KÊNH. KHÔNG phải danh bạ thứ hai: cột CrmCustomerId trỏ về
-- khách trong CRM. Chưa nhận ra là ai thì để NULL — tuyệt đối không đoán mò rồi gộp nhầm.
dbo.ChatContacts (TenantId, Channel TINYINT, ExternalId NVARCHAR(128), DisplayName, AvatarUrl,
                  Phone, Email, CrmCustomerId INT NULL, CreatedUtc, UpdatedUtc)
  PK (TenantId, Channel, ExternalId)

-- Một luồng chat. BotResumeAt là MỐC THỜI GIAN, không phải cờ (xem spec §5).
dbo.ChatConversations (Id BIGINT IDENTITY, TenantId, Channel TINYINT, ContactExternalId,
                       Status TINYINT,              -- 0=mới 1=đang xử lý 2=đã đóng
                       AssignedUsername NVARCHAR(120) NULL,
                       BotResumeAt DATETIME2 NULL,  -- trong khoảng này bot CÂM
                       ContactRepliedAt DATETIME2 NULL,   -- mốc tính cửa sổ gửi
                       AgentRepliedAt   DATETIME2 NULL,
                       ContactLastReadAt DATETIME2 NULL, AgentLastReadAt DATETIME2 NULL,
                       LastActivityAt DATETIME2, LastPreview NVARCHAR(300),
                       ArchivedAt DATETIME2 NULL, CreatedUtc)
  INDEX (TenantId, Status, LastActivityAt DESC)
  INDEX (TenantId, AssignedUsername, LastActivityAt DESC)

-- Tin nhắn. ExternalMsgId để CHỐNG TRÙNG khi webhook gửi lại.
dbo.ChatMessages (Id BIGINT IDENTITY, TenantId, ConversationId BIGINT,
                  Direction TINYINT,          -- 0=khách gửi 1=mình gửi
                  SenderKind TINYINT,         -- 0=khách 1=AI 2=nhân viên
                  SenderUsername NVARCHAR(120) NULL,
                  Kind TINYINT,               -- 0=text 1=ảnh 2=file 3=audio 4=sticker 5=vị trí
                  Text NVARCHAR(MAX) NULL, AttachmentJson NVARCHAR(MAX) NULL,
                  ExternalMsgId NVARCHAR(128) NULL,
                  State TINYINT,              -- 0=chờ 1=đã gửi 2=đã nhận 3=đã xem 4=hỏng
                  ErrorMessage NVARCHAR(500) NULL, CreatedUtc, ProcessedUtc DATETIME2 NULL)
  UNIQUE INDEX (TenantId, Channel, ExternalMsgId) WHERE ExternalMsgId IS NOT NULL
  INDEX (ConversationId, CreatedUtc)

-- Hàng đợi gửi RIÊNG cho chat. KHÔNG dùng dbo.OutboundMails (vòng đời khác hẳn — xem spec §5).
dbo.ChatOutbox (Id BIGINT IDENTITY, TenantId, ConversationId, MessageId,
                Status TINYINT, RetryCount INT, ErrorMessage, CreatedUtc, ProcessedUtc)
```

- [ ] **Step 2:** `FeatureFlags.Chat(cfg)` + field `chat` trong `GET /api/v1/features`.
- [ ] **Step 3:** Cập nhật `docs/database-schema.md` (lệ repo: thêm bảng là phải update file đó).

**Verify:** chạy app → log "schema OK"; `GET /api/v1/features` có `chat:false` khi chưa khai key.

---

### Task 2: Enum kênh + kho dữ liệu (Dapper)

**Files:** Create `Services/Chat/Channels/ChatChannel.cs`, `Services/Chat/ChatRepository.cs`

- [ ] **Step 1:** `enum ChatChannel : byte { Zalo = 0, Messenger = 1, Webchat = 2 }` — số tường minh,
      lưu thẳng cột. Thêm kênh = thêm 1 member + 1 adapter, **không đụng phần lõi**.
- [ ] **Step 2:** `ChatRepository` — mọi hàm kẹp `TenantId`:
      `UpsertContactAsync` · `GetOrCreateConversationAsync` · `AppendMessageAsync` (trả `null` nếu
      `ExternalMsgId` đã có → **chống trùng**) · `ListConversationsAsync(status, assignee, search)` ·
      `ListMessagesAsync` · `AssignAsync` · `SetStatusAsync` · `PauseBotAsync(minutes)` ·
      `MarkReadAsync`.

**Verify:** test `AppendMessageAsync` gọi 2 lần cùng `ExternalMsgId` → lần 2 trả `null`, bảng chỉ 1 dòng.

---

### Task 3: Luật thuần — cửa sổ gửi, bot câm, gộp tin (CÓ TEST)

**Files:** Create `Services/Chat/ChatRules.cs` + `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs`

Tách thuần vì **sai ở đây là hỏng thật**, và chỉ tách mới test hết ca biên.

- [ ] **Step 1:** `SendWindow(channel, contactRepliedAt, nowUtc)` → `(bool Open, TimeSpan Left, string Reason)`.
      Zalo **48h**, Messenger **24h**, Webchat không giới hạn. Chưa có `ContactRepliedAt` (mình chủ động
      mở lời) → **đóng**.
- [ ] **Step 2:** `BotShouldReply(conv, nowUtc)` → false khi `BotResumeAt > now`, khi `Status = đã đóng`,
      hoặc khi đã có người được giao và họ vừa trả lời.
- [ ] **Step 3:** `ShouldFlush(messages, nowUtc, quietMs = 4000)` — gộp tin nhắn liên tiếp. Khách gõ
      3 dòng liền thì chờ im lặng ~4 giây rồi xử lý CẢ CỤM, không trả lời từng dòng.

**Verify — test bắt buộc có:**
- Zalo 47h59' → mở; 48h01' → đóng kèm lý do đọc được.
- `ContactRepliedAt = null` → **đóng** (không phải mở).
- `BotResumeAt` tương lai → bot câm; quá khứ → nói lại.
- 3 tin trong 2 giây → chờ; im 4 giây → gộp thành 1 lượt.

---

### Task 4: Adapter Zalo — webhook nhận tin

**Files:** Create `Services/Chat/Channels/IChatChannelAdapter.cs`, `ZaloChatAdapter.cs`,
`Endpoints/ChatWebhookEndpoints.cs`

- [ ] **Step 1:** `IChatChannelAdapter`: `Channel`, `VerifySignature(raw, headers, cfg)`,
      `ParseInbound(raw)` → `IReadOnlyList<InboundChatEvent>`, `SendAsync(...)`.
- [ ] **Step 2:** **Xác thực chữ ký Zalo** — `SHA256(appId + rawBody + timestamp + oaSecretKey)`,
      header dạng `xxx=<hash>`, so sánh **timing-safe**.

  ⚠️ **Dùng THÂN REQUEST THÔ**, đừng parse rồi serialize lại. ChatbotX làm `JSON.stringify(payload)`
  — chỉ đúng khi thứ tự khoá và khoảng trắng trùng khít bản gốc; .NET serialize lại gần như chắc
  chắn ra chuỗi khác → chữ ký không bao giờ khớp. Đọc raw bằng `EnableBuffering()`.

- [ ] **Step 3:** Bóc **đủ** loại sự kiện (lấy từ `integrations/zalo/src/schema/webhook.ts`):
  - Khách gửi: `user_send_text` · `user_send_image` · `user_send_file` · `user_send_audio` ·
    `user_send_sticker` · `user_send_location` · `user_seen_message`
  - **OA gửi (tiếng vọng)**: `oa_send_text` · `oa_send_image` · `oa_send_file` · `oa_send_video` ·
    `oa_send_sticker` · `oa_send_link` · `oa_send_list` · `oa_send_carousel`

  ⚠️ **Đừng bỏ nhóm `oa_send_*`.** Nhân viên trả lời từ **app Zalo OA** (không qua TourKit) thì mình
  chỉ biết qua tiếng vọng này. Bỏ qua thì: (a) hộp thư thiếu mất nửa cuộc trò chuyện, (b) bot **nói
  đè lên người thật** vì không biết có ai đang trả lời. Nhận tiếng vọng phải ghi tin **và** đặt
  `BotResumeAt`.

- [ ] **Step 4:** Endpoint `POST /api/v1/chat/webhook/zalo` — **CÔNG KHAI** (Zalo gọi, không có phiên),
      nhưng bắt buộc qua chữ ký. Nhận xong **trả 200 NGAY** rồi xử lý nền: Zalo **gửi lại** khi không
      thấy 200, mà xử lý chậm sẽ thành lặp tin.

**Verify:** test `VerifySignature` với payload mẫu (đúng/sai/thiếu header); test `ParseInbound` bóc
đủ 15 loại sự kiện trên.

---

### Task 5: Đường xử lý tin đến + AI trả lời

**Files:** Create `Services/Chat/ChatInboundService.cs`

- [ ] **Step 1:** Luồng: chống trùng (`ExternalMsgId`) → upsert liên hệ → tìm/ tạo hội thoại →
      ghi tin → cập nhật `ContactRepliedAt` + `LastActivityAt` → chờ gộp (`ShouldFlush`) →
      `BotShouldReply`? → gọi AI → xếp `ChatOutbox`.
- [ ] **Step 2:** **Nối vào trợ lý sẵn có, KHÔNG viết bộ não mới.** Dùng lại
      [`ChatAgentService`](../../../Services/Chat/ChatAgentService.cs) để câu trả lời bám dữ liệu CRM thật.
      Bọc `AiCallContext.Push("chat-reply", tenantId)`.
- [ ] **Step 3:** Nối khách chat với khách CRM: có số điện thoại/email → tìm trong CRM → gán
      `CrmCustomerId`. **Không khớp thì để trống** — gộp nhầm hai khách còn tệ hơn không gộp.
- [ ] **Step 4:** AI hỏng → **không im lặng**: ghi tin hệ thống vào hội thoại ("chưa trả lời tự động
      được") + đánh dấu hội thoại cần người xem. Khách không nhận câu rác, nhân viên biết mà vào.

**Verify:** bắn payload webhook giả 2 lần cùng id → 1 tin, 1 câu trả lời.

---

### Task 6: Gửi đi + worker rút hàng đợi

**Files:** Create `Services/Chat/ChatOutboxWorker.cs`; Modify `ZaloChatAdapter`

- [ ] **Step 1:** `SendAsync` gọi `v3.0/oa/message/cs` (base `https://openapi.zalo.me`).
      Token OA lấy từ [`TenantChannelSettingsStore`](../../../Services/Digest/TenantChannelSettingsStore.cs) —
      **tái dùng cấu hình OA đã có**, không bắt khai lại.

  ⚠️ Lưu cấu hình phải **HỢP NHẤT**, đừng ghi đè cả cục: `ConfigJson` có hai chủ — phần khai tay và
  `refreshToken`/`accessToken` do worker xoay vòng. Đây là bẫy đã ghi trong CLAUDE.md.

- [ ] **Step 2:** `ChatOutboxWorker` (`BackgroundService`, nhịp ~5s): rút `Status=0` → kiểm cửa sổ gửi
      → gửi → cập nhật trạng thái tin.
- [ ] **Step 3:** **Ba kết cục tách bạch** (theo lệ của `OutboundMails`): gửi được → `1`;
      hỏng mà thử lại vô ích (hết cửa sổ, OA chưa khai, khách chặn) → `4` + lý do đọc được;
      hỏng tạm thời (mạng, 5xx) → tăng `RetryCount`, hết lượt mới `2`.
- [ ] **Step 4:** Token hết hạn (`-1001`) → làm mới rồi thử lại **một lần**.

**Verify:** gửi khi hết cửa sổ → `Status=4` + lý do nói rõ, **không** gọi API Zalo.

---

### Task 7: API cho giao diện

**Files:** Create `Endpoints/ChatEndpoints.cs` (⚠️ tên này đã dùng cho Chat-Analytics —
đặt `ChatInboxEndpoints.cs`)

Tất cả yêu cầu `X-Session-Id`; tenant từ phiên.

| Method | Path | Việc |
|---|---|---|
| GET | `/api/v1/chat/conversations` | Lọc `status`/`assignee`/`search` + số đếm |
| GET | `/api/v1/chat/conversations/{id}` | Chi tiết + tin nhắn |
| POST | `/api/v1/chat/conversations/{id}/send` | Nhân viên gửi → đặt `BotResumeAt` |
| POST | `/api/v1/chat/conversations/{id}/assign` | Giao/gỡ việc |
| PATCH | `/api/v1/chat/conversations/{id}/status` | Đổi trạng thái |
| POST | `/api/v1/chat/conversations/{id}/read` | Đánh dấu đã đọc |
| POST | `/api/v1/chat/conversations/{id}/bot` | Bật/tạm dừng bot |

- [ ] **Phân quyền:** nhân viên thấy hội thoại **của mình** + chưa ai nhận; xem hết cần quyền —
      cùng luật đã áp cho Bảng tin. Kẹp ở **SQL**, không lọc ở client.

**Verify:** tài khoản không quyền chỉ thấy phần của mình; id của tenant khác → 404.

---

### Task 8: Màn hình hộp thư chung

**Files:** Create `wwwroot/pages/chat-inbox.jsx`; Modify `index.html`, `bundle-entry.js`, `app.jsx`, `styles.css`

- [ ] **Step 1:** 3 cột theo đúng mẫu [`mail.jsx`](../../../wwwroot/pages/mail.jsx) đã có: bộ lọc /
      danh sách hội thoại / khung chat + ô soạn.
- [ ] **Step 2:** Khung chat: bong bóng phân biệt **khách · AI · nhân viên** (3 màu, không phải 2 —
      người đọc cần biết câu nào do máy trả lời).
- [ ] **Step 3:** **Hết cửa sổ gửi → khoá ô soạn** kèm câu nói rõ còn bao lâu / vì sao, và gợi ý
      đường thay thế. Đừng để bấm gửi rồi mới báo hỏng.
- [ ] **Step 4:** Nút giao việc, đóng việc, tạm dừng bot. Hiện rõ **ai đang phụ trách**.
- [ ] **Step 4b:** Cập nhật bằng **hỏi lại mỗi 4 giây** khi trang đang mở (dừng hỏi khi tab ẩn —
      `document.hidden`, không thì mở 10 tab là nhân 10 lần tải). KHÔNG dùng SignalR ở đợt này;
      xem mục "Hạ tầng dữ liệu" để biết vì sao.
- [ ] **Step 5:** Khai trang ở `index.html` **VÀ** `bundle-entry.js` **VÀ** `app.jsx` (route + menu)
      **VÀ** `SeoSetup.Routes` (thiếu → mở link trực tiếp ra 404).

**Verify:** dựng lại bundle, mở trang thật, gửi thử một tin.

---

### Task 9: Chốt lại

- [ ] `dotnet test` — **toàn bộ** phải xanh.
- [ ] Cập nhật `CLAUDE.md` (mục tính năng + bảng cờ `Features:*`) và `docs/database-schema.md`.
- [ ] Thêm mục **CHANGELOG.md** viết cho người dùng cuối.
- [ ] Viết hướng dẫn `docs/features/hop-thu-chat.md` bằng agent `tourkit-doc-writer` + chụp ảnh.

---

## Năm câu phải chốt TRƯỚC khi bắt đầu

1. **Quyền Zalo OA cho chat.** Nhận tin cần quyền khác với gửi ZNS. Phải kiểm bộ quyền OA thật
   trước khi code Task 4 — không thì làm xong mới biết OA không nhận được webhook.
2. **Giữ lịch sử chat bao lâu?** Bảng tin giữ 30 ngày. Chat nhiều hơn hẳn và có thể là **chứng cứ
   giao dịch** — chốt trước khi bảng phình.
3. **Máy chạy thật dùng bản SQL nào?** Máy đo được là **Developer Edition** — không được phép dùng
   cho sản xuất. Nếu máy thật là **Express** thì trần 10 GB/CSDL là mốc phải tính ngay từ đầu
   (xem mục "Hạ tầng dữ liệu").
4. **Vì sao SQL rớt kết nối lai rai?** 4 lần trong một ngày. Chat hỏi CSDL liên tục nên phải xử
   trước, không thì lỗi sẽ hiện ra ngay trước mặt khách.
5. **Tính lượt AI thế nào?** Một hội thoại 20 lượt qua lại tốn gấp 20 lần một lần chấm khách.
   Hạn mức tenant hiện tại chưa tính tới kiểu tiêu này — cần ngưỡng riêng cho chat, nếu không một
   khách nhắn nhiều là hết lượt của cả công ty.
