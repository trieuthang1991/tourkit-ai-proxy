# Chat đa kênh — Đợt 1: Zalo OA hai chiều + hộp thư chung — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Khách nhắn Zalo OA → tin vào hộp thư chung trong TourKit → AI trả lời bằng dữ liệu CRM →
nhân viên tiếp quản/giao việc được. Đủ 6 luật nghiệp vụ ở §6 của spec. Xong đợt này là **dùng được thật**.

**Spec:** [2026-08-20-omnichannel-chat-design.md](../specs/2026-08-20-omnichannel-chat-design.md) — ĐỌC TRƯỚC KHI LÀM.

**Tech Stack:** ASP.NET Core 8 · **Dapper + Npgsql trên PostgreSQL 18** (CSDL chat RIÊNG, xem mục
"Hạ tầng dữ liệu") · xUnit (`TourkitAiProxy.Tests`) · React no-build (`wwwroot/pages/*.jsx` +
`bundle-entry.js`).

⚠️ **Hai cơ sở dữ liệu, hai loại khác nhau.** Phần còn lại của hệ chạy SQL Server
(`ConnectionStrings:PushDb`); chat chạy PostgreSQL (`ConnectionStrings:Chat`). Đây là **quyết định
đã chốt**, không phải tình cờ — lý do và hệ quả ở mục "Hạ tầng dữ liệu".

## Global Constraints

- Comment/log/chuỗi hiển thị **tiếng Việt**. DateTime **UTC** (`SYSUTCDATETIME()` / `DateTime.UtcNow`);
  giờ VN qua `TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")`.
- Cả cụm nằm sau cờ **`Features:Chat`**, mặc định TẮT — thêm 1 method vào
  [`FeatureFlags`](../../../Services/Bootstrap/FeatureFlags.cs), 1 field vào `GET /api/v1/features`,
  khai key ở **CẢ** `appsettings.example.json` lẫn bản worker. Tắt thì endpoint trả **404 tường minh**
  (nhớ map tay, `MapFallback` sẽ nuốt và trả `index.html` kèm 200).
- Bảng chat nằm **trong PostgreSQL**, KHÔNG thêm vào `TourkitAiDb.SchemaSql` (file đó là T-SQL,
  chạy trên SQL Server). Tuyệt đối không nhét chat vào `dbo.OutboundMails`.
- **Không `JOIN` được giữa chat và CRM** — hai máy chủ khác nhau. Mọi liên kết nối ở tầng ứng dụng.
- **Không có giao dịch chung.** Ghi tin nhắn (PostgreSQL) và cập nhật khách hàng (SQL Server) là hai
  việc tách rời: cái này xong cái kia hỏng là chuyện CÓ THẬT, phải xử lý chứ không giả định nguyên tử.
  Nguyên tắc: **ghi tin nhắn trước** (không mất dữ liệu khách), cập nhật CRM sau và cho phép thử lại.
- Tên định danh PostgreSQL viết **snake_case chữ thường** (`chat_conversations`) — không dùng
  PascalCase như SQL Server, vì PostgreSQL hạ chữ thường mọi định danh không đặt trong nháy kép, và
  đặt nháy kép thì về sau câu lệnh nào cũng phải nháy theo.
- Tenant lấy từ `ITenantContext`/`SessionAuth` — **KHÔNG nhận `tenantId` từ client**.
- DI đăng ký trong [`WorkflowStackRegistration.AddWorkflowStack`](../../../Services/Bootstrap/WorkflowStackRegistration.cs).
- AI gọi từ nền (không có HttpContext) PHẢI bọc `AiCallContext.Push("chat-reply", tenantId)` —
  thiếu là bypass hạn mức tenant và log `feature=unknown`.
- Thêm trang mới: khai ở **CẢ** `index.html` **VÀ** `bundle-entry.js` (thiếu một bên → prod trắng trang).

---

## Hạ tầng dữ liệu

Đo thật ngày 20/08/2026, không ước chừng.

### CSDL chat — PostgreSQL riêng

| | |
|---|---|
| Máy chủ | **Google Cloud SQL**, instance `farmer-db`, khu vực Singapore |
| Phiên bản | **PostgreSQL 18.4** (Debian) |
| CSDL | `tourkit-chat` · 7,8 MB · **0 bảng** (còn trắng) |
| Tài khoản | **`tourkit_chat`** — riêng cho chat, KHÔNG thuộc nhóm nào |
| Chuỗi kết nối | `ConnectionStrings:Chat`, **đã mã hoá `ENC:`** (Crypton, như `PushDb`) |
| Độ trễ từ máy phát triển | **78 ms** bắt tay TCP |
| Đã kiểm | tạo bảng · ghi · đọc · đánh chỉ mục · xoá — **qua hết** |
| `pgvector` | ✅ **đã bật** |

**Chốt: dùng CHUNG instance `farmer-db`** (quyết định 20/08). Có sẵn một instance riêng tên
`tourkit-chat` nhân bản từ `farmer-db` nhưng **không dùng** — xem mục rủi ro bên dưới.

⚠️ **`gcloud sql users create` tự cấp nhóm `cloudsqlsuperuser`** cho tài khoản mới — quyền gần như
quản trị, quá rộng cho tài khoản ứng dụng, và để nguyên thì việc tách tài khoản gần như vô nghĩa.
Đã gỡ. Bẫy khi gỡ: **quyền tạo bảng của nó đến từ chính nhóm đó**, nên phải cấp quyền tường minh
TRƯỚC rồi mới gỡ nhóm, không thì gỡ xong là mất luôn quyền. Tạo tài khoản mới sau này nhớ làm đúng
thứ tự này.

### Vì sao tách khỏi SQL Server — và mất gì

**Được:** `pgvector` chạy được → **tìm hội thoại theo ngữ nghĩa làm được**. Trên SQL Server 2022 thì
không: kiểu dữ liệu vector tới bản 2025 mới có. Đây là lý do chính đáng để tách.

**Mất — ba thứ, đều phải xử ở tầng ứng dụng:**

1. **Không `JOIN` được** giữa `chat_conversations` và khách hàng/tour trong SQL Server. Muốn hiện tên
   khách cạnh hội thoại thì phải đọc hai nơi rồi ghép trong bộ nhớ. Danh sách 50 hội thoại mà hỏi CRM
   50 lần là hỏng — **phải gom một lượt theo lô**.
2. **Không có giao dịch chung.** Xem mục Global Constraints.
3. **Nuôi hai loại CSDL.** Sao lưu, theo dõi, cấp quyền, nâng cấp — nhân đôi. Chi phí thật, chấp nhận
   đổi lấy `pgvector` và việc chat không làm nặng CSDL nghiệp vụ.

### Ước dung lượng

Công ty tour cỡ vừa: 50 hội thoại/ngày × 15 tin = **~275.000 tin/năm**, mỗi tin 1–2 KB (chữ ngắn,
khác email 29 KB vì có HTML) → **~400 MB/năm**. Với Cloud SQL thì không đáng kể; điều đáng để ý là
**chi phí lưu trữ và sao lưu tính theo dung lượng**, nên phải chốt thời hạn giữ lịch sử (xem câu hỏi
cuối tài liệu).

### Ba việc PostgreSQL không tự làm — chốt cách xử

| Việc | Đợt 1 chốt thế nào |
|---|---|
| Đẩy tin lên màn hình thời gian thực | **Hỏi lại mỗi 4 giây** khi đang mở hộp thư, dừng khi tab ẩn. SignalR để đợt sau |
| Hàng đợi gửi | **Bảng + rút định kỳ**, đúng cách `OutboundMails` đang chạy tốt. `LISTEN/NOTIFY` của PostgreSQL để dành khi cần nhanh hơn |
| Tìm theo ngữ nghĩa | Hạ tầng đã sẵn sàng, nhưng **đợt 1 chưa làm** — chưa có gì để tìm khi kho hội thoại còn trống |

### Bốn rủi ro phải xử TRƯỚC khi code

**① Chuỗi kết nối đang để DẠNG RÕ.** `ConnectionStrings:Chat` chưa mã hoá, trong khi `PushDb` dùng
`ENC:`. File đang gitignore nên chưa lộ ra repo, nhưng **phải mã hoá cho đồng nhất** trước khi lên máy
chủ thật.

**② Chung instance với dự án `farmer` — đã giảm nhẹ, chưa hết.** Đã tạo tài khoản riêng
`tourkit_chat` nên không còn chung tài khoản với dự án kia. Nhưng vẫn còn hai điểm:

- `farmer_app` **vẫn còn quyền tạo bảng trên CSDL chat**. Tài khoản mới không gỡ được vì quyền đó do
  chủ CSDL cấp — cần chạy bằng `postgres`, **trong CSDL `tourkit-chat`**:
  `REVOKE CREATE, USAGE ON SCHEMA public FROM farmer_app;`
- `tourkit_chat` **vẫn kết nối được sang CSDL `farmer`** — mặc định của PostgreSQL cho mọi role.
  Chặn hẳn phải thu hồi quyền kết nối của `PUBLIC` trên `farmer`, nhưng làm vậy **có thể gãy chính
  `farmer_app`** nếu nó đang dựa vào mặc định đó. Chấp nhận rủi ro này khi chọn dùng chung instance.

⚠️ Trong lúc dựng đã có **một lệnh cấp quyền chạy nhầm sang CSDL `farmer`**, vô tình cho `farmer_app`
quyền tạo bảng ở đó. **Cố ý không tự thu hồi**: không phân biệt được nó vốn đã có hay do lệnh nhầm
thêm vào, gỡ mà nó vốn cần thì gãy dự án kia. Người biết rõ dự án `farmer` phải tự quyết.

**③ Còn một instance thừa đang tính tiền.** `tourkit-chat` (8 vCPU, **250 GB SSD**) tạo lúc
20/08 rồi dừng. Máy dừng thì không tính giờ CPU nhưng **đĩa vẫn tính tiền** — 250 GB SSD ở Singapore
khoảng **$50/tháng cho thứ không dùng**. Đã chốt dùng chung `farmer-db` nên instance này nên xoá,
nhưng **xoá là không lấy lại được** — người quản trị tự quyết.

**④ IP máy chủ ứng dụng phải nằm trong danh sách cho phép.** Cloud SQL chặn mặc định. Máy phát triển
dùng IP động nên khai cứng chỉ hợp để thử; chạy thật nên đi qua **Cloud SQL Proxy** hoặc mạng riêng.

⚠️ **Bẫy đã dính, ghi lại cho người sau:** Cloud SQL Studio **chạy câu lệnh trên CSDL mà tab đang nối,
không phải nhánh mở trong cây Explorer bên trái**. Hai thứ đó độc lập, nên lệnh báo
*"Statement executed successfully"* hoàn toàn thật mà lại thành công ở nhầm CSDL. Luôn chạy kèm
`SELECT current_database();` để tự nó nói đang đứng ở đâu.

---

### Task 1: Kết nối PostgreSQL + bảng dữ liệu + cờ tính năng

**Files:**
- Create: `Services/Chat/Db/ChatDb.cs` (mở kết nối Npgsql + `SchemaSql` dựng bảng, idempotent)
- Modify: `TourkitAiProxy.csproj` (thêm `Npgsql` — dự án hiện **chưa tham chiếu**)
- Modify: `Services/Bootstrap/FeatureFlags.cs`, `Endpoints/SystemEndpoints.cs`, `wwwroot/core/features.js`
- Modify: `docs/database-schema.md`, `appsettings.example.json`

- [ ] **Step 0:** Thêm gói `Npgsql`; `ChatDb` đọc `ConnectionStrings:Chat`, hỗ trợ cả chuỗi `ENC:`
      (giải bằng `Crypton` như `PushDb` đang làm) lẫn chuỗi rõ. Không có khoá → **cụm chat tự tắt**,
      log cảnh báo, KHÔNG làm sập cả ứng dụng.
- [ ] **Step 1:** Dựng 4 bảng bằng `CREATE TABLE IF NOT EXISTS` (PostgreSQL, snake_case):

```sql
-- Danh tính khách theo TỪNG KÊNH. KHÔNG phải danh bạ thứ hai: cột crm_customer_id trỏ về khách
-- trong CRM (SQL Server). Chưa nhận ra là ai thì để NULL — tuyệt đối không đoán mò rồi gộp nhầm.
-- Không có khoá ngoại tới CRM được: khác máy chủ. Đây là cái giá của việc tách CSDL.
CREATE TABLE IF NOT EXISTS chat_contacts (
  tenant_id       text        NOT NULL,
  channel         smallint    NOT NULL,
  external_id     text        NOT NULL,
  display_name    text, avatar_url text, phone text, email text,
  crm_customer_id integer,
  created_utc     timestamptz NOT NULL DEFAULT now(),
  updated_utc     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, channel, external_id)
);

-- Một luồng chat. bot_resume_at là MỐC THỜI GIAN, không phải cờ (xem spec §5).
CREATE TABLE IF NOT EXISTS chat_conversations (
  id                  bigserial PRIMARY KEY,
  tenant_id           text     NOT NULL,
  channel             smallint NOT NULL,
  contact_external_id text     NOT NULL,
  status              smallint NOT NULL DEFAULT 0,   -- 0=mới 1=đang xử lý 2=đã đóng
  assigned_username   text,
  bot_resume_at       timestamptz,                   -- trong khoảng này bot CÂM
  contact_replied_at  timestamptz,                   -- mốc tính cửa sổ gửi
  agent_replied_at    timestamptz,
  contact_last_read_at timestamptz, agent_last_read_at timestamptz,
  last_activity_at    timestamptz NOT NULL DEFAULT now(),
  last_preview        text,
  archived_at         timestamptz,
  created_utc         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_conv_tenant_status ON chat_conversations (tenant_id, status, last_activity_at DESC);
CREATE INDEX IF NOT EXISTS ix_conv_tenant_assignee ON chat_conversations (tenant_id, assigned_username, last_activity_at DESC);

-- Tin nhắn. external_msg_id để CHỐNG TRÙNG khi webhook gửi lại.
CREATE TABLE IF NOT EXISTS chat_messages (
  id              bigserial PRIMARY KEY,
  tenant_id       text     NOT NULL,
  conversation_id bigint   NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
  channel         smallint NOT NULL,
  direction       smallint NOT NULL,   -- 0=khách gửi 1=mình gửi
  sender_kind     smallint NOT NULL,   -- 0=khách 1=AI 2=nhân viên
  sender_username text,
  kind            smallint NOT NULL,   -- 0=text 1=ảnh 2=file 3=audio 4=sticker 5=vị trí
  body            text,
  attachment      jsonb,               -- jsonb chứ không phải text: PostgreSQL truy vấn được bên trong
  external_msg_id text,
  state           smallint NOT NULL DEFAULT 0,  -- 0=chờ 1=đã gửi 2=đã nhận 3=đã xem 4=hỏng
  error_message   text,
  created_utc     timestamptz NOT NULL DEFAULT now(),
  processed_utc   timestamptz
);
-- Chỉ mục DUY NHẤT có điều kiện — chốt chống trùng, đặt ở TẦNG CSDL chứ không chỉ trong code:
-- webhook gửi lại đồng thời hai lần thì kiểm tra trong code vẫn lọt, chỉ mục thì không.
CREATE UNIQUE INDEX IF NOT EXISTS ux_msg_external ON chat_messages (tenant_id, channel, external_msg_id)
  WHERE external_msg_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_msg_conv ON chat_messages (conversation_id, created_utc);

-- Hàng đợi gửi RIÊNG cho chat. KHÔNG dùng dbo.OutboundMails (khác máy chủ, và khác vòng đời).
CREATE TABLE IF NOT EXISTS chat_outbox (
  id              bigserial PRIMARY KEY,
  tenant_id       text     NOT NULL,
  conversation_id bigint   NOT NULL,
  message_id      bigint   NOT NULL,
  status          smallint NOT NULL DEFAULT 0,
  retry_count     integer  NOT NULL DEFAULT 0,
  error_message   text,
  created_utc     timestamptz NOT NULL DEFAULT now(),
  processed_utc   timestamptz
);
-- Chỉ mục CÓ ĐIỀU KIỆN: worker chỉ hỏi dòng đang chờ. Không có nó thì mỗi 5 giây lại quét cả bảng,
-- và bảng này chỉ có phình chứ không co lại.
CREATE INDEX IF NOT EXISTS ix_outbox_cho ON chat_outbox (created_utc) WHERE status = 0;
```

⚠️ **`timestamptz` chứ không phải `timestamp`.** PostgreSQL có cả hai; loại không có múi giờ sẽ khiến
giờ VN và giờ UTC lẫn vào nhau đúng kiểu lỗi đã ghi trong `docs/datetime-convention.md`. Đặt
`timestamptz` là máy chủ tự quy về UTC.

- [ ] **Step 2:** `FeatureFlags.Chat(cfg)` + field `chat` trong `GET /api/v1/features`.
- [ ] **Step 3:** Cập nhật `docs/database-schema.md` — thêm **mục riêng cho CSDL chat**, ghi rõ đây là
      PostgreSQL khác máy chủ, đừng để người đọc tưởng nằm chung với 26 bảng SQL Server.

**Verify:** chạy app → log dựng bảng chat OK, kiểm bằng `\dt` trong `psql` thấy đủ 4 bảng;
`GET /api/v1/features` có `chat:false` khi chưa khai key; **xoá khoá `ConnectionStrings:Chat` thì app
vẫn chạy bình thường**, chỉ cụm chat tắt.

---

### Task 2: Enum kênh + kho dữ liệu (Dapper trên Npgsql)

**Files:** Create `Services/Chat/Channels/ChatChannel.cs`, `Services/Chat/ChatRepository.cs`

- [ ] **Step 1:** `enum ChatChannel : byte { Zalo = 0, Messenger = 1, Webchat = 2 }` — số tường minh,
      lưu thẳng cột. Thêm kênh = thêm 1 member + 1 adapter, **không đụng phần lõi**.
- [ ] **Step 1b:** Dapper chạy nguyên với Npgsql, nhưng **tham số dùng `@ten`** và tên cột trả về là
      snake_case — đặt `MatchNamesWithUnderscores = true` cho `DefaultTypeMap`, nếu không mọi thuộc
      tính map ra `null` mà **không báo lỗi gì cả**.
- [ ] **Step 2:** `ChatRepository` — mọi hàm kẹp `tenant_id`:
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
3. ~~Tài khoản CSDL chat dùng cái nào?~~ **XONG 20/08:** đã tạo `tourkit_chat` riêng, gỡ nhóm
   `cloudsqlsuperuser`, chuỗi kết nối đã mã hoá `ENC:`. Còn nợ một lệnh `REVOKE` phải chạy bằng
   `postgres` (xem rủi ro ②).
4. **SQL Server rớt kết nối lai rai — 4 lần trong ngày 20/08.** Chat KHÔNG dùng máy đó nữa nên không
   bị ảnh hưởng trực tiếp, NHƯNG mọi câu trả lời của AI đều đọc CRM từ đó. Đọc CRM hỏng thì chat vẫn
   nhận được tin mà **trả lời sai hoặc không trả lời được** — vẫn phải xử.
5. **Tính lượt AI thế nào?** Một hội thoại 20 lượt qua lại tốn gấp 20 lần một lần chấm khách.
   Hạn mức tenant hiện tại chưa tính tới kiểu tiêu này — cần ngưỡng riêng cho chat, nếu không một
   khách nhắn nhiều là hết lượt của cả công ty.
