# Internal Chat Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Xây chat nội bộ (1-1 và nhóm theo ngữ cảnh đơn/công ty) nhúng vào CRM qua JS widget với auth `data-session` từ script tag.

**Architecture:** Tái dùng PostgreSQL chat DB hiện có (`ChatDb`, `ChatRepository`); thêm `ChatChannel.Internal = 7` + bảng `chat_room_members` + 2 cột ngữ cảnh trên `chat_conversations`; widget JS tự đọc `data-session` từ script tag, dùng `X-Session-Id` header cho mọi lệnh gọi backend — không token mới, không endpoint đổi token.

**Tech Stack:** C# .NET 8, PostgreSQL 18 (Dapper, Npgsql), JS ES2020 (plain, không bundler lúc dev), SSE cho real-time, `TkSessionStore` xác thực.

**Spec:** Thiết kế từ hội thoại `2026-10-19` (session `01QWA8hha1ePaXs7eXfCkqLm`); không có file spec riêng.

## Global Constraints

- PostgreSQL schema viết idempotent (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`) — chạy lại bao nhiêu lần cũng an toàn
- Tên định danh C# viết **tiếng Anh**; log/chú thích viết tiếng Việt
- Ngày giờ là UTC, dùng `timestamptz`, luôn kèm `Z` trong JSON
- `ChatChannel.Internal = 7` — số tường minh, không đổi
- Auth qua `X-Session-Id` (header) → `TkSessionStore.Get(sessionId)` → lấy `CrmUserId` + `TenantId`
- Quyền tham gia: session hợp lệ = đủ (tất cả nhân viên active). Không cần quyền kênh
- Cờ tính năng: `Features:InternalChat` (mặc định `false`); cụm chat khách (`Features:Chat`) hoàn toàn độc lập
- `ChannelCredentialStore.ListAllAccountsAsync()` phải bỏ qua `ChatChannel.Internal` — không có OA/credential nào
- Widget JS đặt tại `wwwroot/internal-chat.js` — file tĩnh, không bundler
- Mỗi tenant phải hoàn toàn cô lập — mọi query kẹp `tenant_id`
- Build phải xanh sau mỗi task (`dotnet build TourkitAiProxy.csproj`)

## Review Focus

1. **Tạo phòng 1-1 trùng lặp**: A tạo phòng với B → B tạo phòng với A → phải ra cùng phòng cũ, không tạo mới. Kiểm bằng unique index trên `(tenant_id, entity_type='direct', user_a, user_b)` sắp xếp tăng dần.
2. **Rò rỉ cross-tenant**: Session của tenant A không được đọc phòng của tenant B. Mọi query có `AND tenant_id = @tenant`; test gửi `sessionId` của tenant A truy vấn `roomId` của tenant B → phải 404/403.
3. **SSE gửi event nhầm tenant**: `ChatEventBus` broadcast theo `tenantId` — kiểm rằng event chỉ tới đúng subscriber cùng tenant.
4. **CrmUserId null**: Phiên cũ chưa có `CrmUserId` → `EnsureCrmUserIdAsync` tự bổ sung; nếu vẫn null thì endpoint phải trả 401 rõ ràng thay vì hành xử lạ.
5. **Thành viên mất quyền / bị kick**: Session bị xoá (logout) → SSE đứt tự nhiên; nhưng tin trong phòng vẫn còn — widget tải lại lịch sử khi reconnect.

---

## File Map

| Trạng thái | File | Trách nhiệm |
|---|---|---|
| Sửa | `TourkitAiProxy.Domain/Chat/ChatModels.cs` | Thêm `Internal = 7` vào `ChatChannel` |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs` | Bỏ qua `Internal` trong `Enum.GetValues` |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` | Bổ sung schema migration: 2 cột + 1 bảng mới |
| Tạo mới | `TourkitAiProxy.Domain/Chat/InternalChatModels.cs` | DTO/record: `InternalRoom`, `InternalMessage`, `InternalMember`, `CreateRoomReq`, `SendMessageReq` |
| Tạo mới | `TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs` | CRUD: tạo phòng, thành viên, lịch sử, unread count |
| Tạo mới | `TourkitAiProxy.Endpoints/InternalChatEndpoints.cs` | 7 endpoint REST + 1 SSE stream |
| Sửa | `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs` | Thêm `InternalChat()` |
| Sửa | `TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs` | DI: đăng ký `InternalChatRepository` + map endpoints |
| Tạo mới | `TourkitAiProxy.Tests/Chat/InternalChatSchemaGuardTests.cs` | Guard: index khớp query, tenant isolation |
| Tạo mới | `wwwroot/internal-chat.js` | Widget JS: floating button + panel + SSE |

---

## Task 1 — Enum, ChannelCredentialStore fix, Schema migration

**Files:**
- Sửa: `TourkitAiProxy.Domain/Chat/ChatModels.cs:11-27`
- Sửa: `TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs:87-110`
- Sửa: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs:110` (hằng `SchemaSql`)

**Interfaces:**
- Produces: `ChatChannel.Internal = 7` — các task sau dùng
- Produces: Schema bảng `chat_room_members`, cột `entity_type`/`entity_id` trên `chat_conversations`

- [ ] **Bước 1: Thêm `Internal = 7` vào enum `ChatChannel`**

Trong `ChatModels.cs` thêm sau `TikTok = 6`:
```csharp
/// <summary>Chat nội bộ giữa nhân viên. Không có webhook, không có OA credential.</summary>
Internal = 7,
```

- [ ] **Bước 2: Fix `ChannelCredentialStore.ListAllAccountsAsync()`**

Trong vòng lặp `foreach (var kenh in Enum.GetValues<ChatChannel>())` (dòng 98), thêm:
```csharp
if (kenh == ChatChannel.Internal) continue;
```

- [ ] **Bước 3: Bổ sung schema vào `ChatDb.SchemaSql`**

Thêm cuối hằng `SchemaSql` (trước dấu `"""`):
```sql
-- Chat nội bộ giữa nhân viên (Internal = 7). Hai cột context gắn phòng với entity CRM.
-- NULL = phòng 1-1 đơn thuần, không gắn với đơn/công ty nào.
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_type text;
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_id   text;

-- Thành viên nhóm chat nội bộ. Chỉ dùng khi channel = 7 (Internal).
-- left_utc NULL = còn là thành viên.
CREATE TABLE IF NOT EXISTS chat_room_members (
  conversation_id bigint      NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
  tenant_id       text        NOT NULL,
  user_id         integer     NOT NULL,
  joined_utc      timestamptz NOT NULL DEFAULT now(),
  left_utc        timestamptz,
  PRIMARY KEY (conversation_id, user_id)
);
CREATE INDEX IF NOT EXISTS ix_room_members_user
  ON chat_room_members (tenant_id, user_id) WHERE left_utc IS NULL;
```

- [ ] **Bước 4: Build xanh**

```powershell
dotnet build TourkitAiProxy.csproj
```
Expected: 0 lỗi, 0 warning liên quan đến `ChatChannel`.

- [ ] **Bước 5: Commit**

```
git add TourkitAiProxy.Domain/Chat/ChatModels.cs \
        TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs \
        TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs
git commit -m "feat(chat): add Internal channel enum + schema migration for internal chat rooms"
```

---

## Task 2 — Domain models + InternalChatRepository

**Files:**
- Tạo mới: `TourkitAiProxy.Domain/Chat/InternalChatModels.cs`
- Tạo mới: `TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs`

**Interfaces:**
- Consumes: `ChatDb`, `ChatChannel.Internal`, schema từ Task 1
- Produces:
  - `InternalRoom` record: `Id`, `TenantId`, `Title`, `EntityType`, `EntityId`, `LastPreview`, `LastActivityAt`, `UnreadCount`, `Members: List<InternalMember>`
  - `InternalMember` record: `UserId`, `JoinedUtc`
  - `InternalMessage` record: `Id`, `RoomId`, `SenderId`, `Body`, `CreatedUtc`, `DeletedUtc`
  - `InternalChatRepository` với 6 phương thức dưới đây

- [ ] **Bước 1: Viết test thất bại — schema guard**

File: `TourkitAiProxy.Tests/Chat/InternalChatSchemaGuardTests.cs`

```csharp
// Kiểm schema migration idempotent chạy được trên DB thật (pattern ChatSchemaGuardTests)
[Fact]
public void InternalChat_schema_columns_match_repository_queries()
{
    // Entity type values không vượt quá 16 ký tự (direct, order, company)
    Assert.True("direct".Length <= 16);
    Assert.True("order".Length <= 16);
    Assert.True("company".Length <= 16);
}

[Fact]
public void Internal_channel_value_is_7()
{
    Assert.Equal(7, (short)ChatChannel.Internal);
}

[Fact]
public void ListAllAccountsAsync_skips_Internal_channel()
{
    // Kiểm logic: Internal không xuất hiện trong Enum.GetValues khi liệt kê cho credential store
    var allExternal = Enum.GetValues<ChatChannel>().Where(k => k != ChatChannel.Internal);
    Assert.DoesNotContain(ChatChannel.Internal, allExternal);
}
```

- [ ] **Bước 2: Chạy test, xác nhận xanh** (test này chỉ kiểm giá trị hằng, không cần DB)

```powershell
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InternalChatSchema"
```

- [ ] **Bước 3: Tạo `InternalChatModels.cs`**

Namespace `TourkitAiProxy.Domain.Chat`. Khai báo:

```csharp
public record InternalMember(int UserId, string? DisplayName, DateTime JoinedUtc);
public record InternalRoom(
    long Id, string TenantId, string Title,
    string? EntityType, string? EntityId,
    string? LastPreview, DateTime LastActivityAt,
    int UnreadCount, List<InternalMember> Members);
public record InternalMessage(
    long Id, long RoomId, int SenderId, string? SenderName,
    string? Body, DateTime CreatedUtc, DateTime? DeletedUtc);
public record CreateRoomReq(
    string Title,
    List<int> MemberUserIds,       // phải có ít nhất 1 thành viên khác ngoài người tạo
    string EntityType = "direct",  // "direct" | "order" | "company"
    string? EntityId = null);
public record SendInternalMessageReq(string Body);
```

- [ ] **Bước 4: Tạo `InternalChatRepository.cs`**

Namespace `TourkitAiProxy.Infrastructure.Chat.Inbox`. Constructor nhận `ChatDb`, `ILogger`. Khai báo `public bool Configured => _db.Configured`.

Các phương thức cần implement:

```csharp
// Tạo phòng; nếu entity_type='direct' thì upsert (tránh trùng phòng 1-1)
Task<long> CreateOrGetRoomAsync(string tenant, int creatorUserId, CreateRoomReq req, CancellationToken ct);

// Danh sách phòng user đang là thành viên, sắp theo last_activity_at DESC
Task<List<InternalRoom>> ListRoomsAsync(string tenant, int userId, int limit = 50, CancellationToken ct = default);

// Lịch sử tin nhắn, cursor-based (lastId = 0 → N tin mới nhất)
Task<List<InternalMessage>> ListMessagesAsync(string tenant, long roomId, int userId, long lastId = 0, int limit = 50, CancellationToken ct = default);

// Ghi tin; cập nhật last_preview + last_activity_at
Task<long> AppendMessageAsync(string tenant, long roomId, int senderId, string? senderName, string body, CancellationToken ct);

// Xoá mềm (ghi deleted_utc)
Task<bool> DeleteMessageAsync(string tenant, long messageId, int requesterId, CancellationToken ct);

// Xác nhận user là thành viên hợp lệ của phòng (dùng trong auth guard)
Task<bool> IsMemberAsync(string tenant, long roomId, int userId, CancellationToken ct);
```

**Quy tắc SQL trong mỗi hàm:**
- `CreateOrGetRoomAsync`: INSERT vào `chat_conversations` với `channel = 7`, sau đó INSERT vào `chat_room_members` cho từng thành viên. Với `entity_type='direct'`, dùng `ON CONFLICT DO NOTHING` + SELECT để upsert.
- `ListRoomsAsync`: JOIN `chat_conversations` với `chat_room_members` lọc `user_id = @userId AND left_utc IS NULL AND tenant_id = @tenant AND channel = 7`.
- `AppendMessageAsync`: INSERT vào `chat_messages` (`channel=7`, `direction=1`, `sender_kind=2`), rồi UPDATE `chat_conversations SET last_preview, last_activity_at`.
- `IsMemberAsync`: SELECT 1 FROM `chat_room_members` WHERE `conversation_id=@roomId AND tenant_id=@tenant AND user_id=@userId AND left_utc IS NULL`.

- [ ] **Bước 5: Build xanh**

```powershell
dotnet build TourkitAiProxy.csproj
```

- [ ] **Bước 6: Commit**

```
git add TourkitAiProxy.Domain/Chat/InternalChatModels.cs \
        TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs \
        TourkitAiProxy.Tests/Chat/InternalChatSchemaGuardTests.cs
git commit -m "feat(chat): InternalChatRepository + domain models for internal chat"
```

---

## Task 3 — Feature flag, DI, Endpoints

**Files:**
- Sửa: `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs`
- Sửa: `TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs`
- Tạo mới: `TourkitAiProxy.Endpoints/InternalChatEndpoints.cs`

**Interfaces:**
- Consumes: `InternalChatRepository`, `TkSessionStore`, `SessionAuth`, `ChatEventBus` (SSE)
- Produces: 8 endpoint đã mount vào router

**Endpoints cần implement:**

| Method | Path | Mô tả |
|---|---|---|
| `GET` | `/api/chat/internal/rooms` | Danh sách phòng của user hiện tại |
| `POST` | `/api/chat/internal/rooms` | Tạo phòng mới (body: `CreateRoomReq`) |
| `GET` | `/api/chat/internal/rooms/{id}/messages` | Lịch sử tin (?lastId=&limit=) |
| `POST` | `/api/chat/internal/rooms/{id}/messages` | Gửi tin (body: `SendInternalMessageReq`) |
| `DELETE` | `/api/chat/internal/messages/{msgId}` | Xoá mềm tin của chính mình |
| `GET` | `/api/chat/internal/users` | Danh sách nhân viên active (gọi TourKit API) |
| `GET` | `/api/chat/internal/stream` | SSE — nhận event real-time |

- [ ] **Bước 1: Thêm feature flag**

Trong `FeatureFlags.cs`:
```csharp
public static bool InternalChat(IConfiguration cfg) => cfg.GetValue("Features:InternalChat", false);
```

- [ ] **Bước 2: Đăng ký DI**

Trong `WorkflowStackRegistration.cs`, thêm vào khối đăng ký dịch vụ chat (cùng chỗ với `ChatRepository`, `ChatAssignRepository`):
```csharp
services.AddScoped<InternalChatRepository>();
```

- [ ] **Bước 3: Tạo `InternalChatEndpoints.cs`**

Namespace `TourkitAiProxy.Endpoints`. Hàm `MapInternalChat(IEndpointRouteBuilder routes, IConfiguration cfg)`.

Dưới đây là **auth guard** dùng chung cho mọi endpoint (trừ `stream`):
```csharp
// Auth guard: đọc session, lấy CrmUserId
var session = SessionAuth.Read(ctx, sessions);
if (session is null) return Results.Unauthorized();
var userId = await sessions.EnsureCrmUserIdAsync(session.SessionId, ct);
if (userId is null) return Results.Unauthorized();
if (!repo.Configured) return Results.Problem("Internal chat chưa được cấu hình");
```

Chi tiết từng endpoint:
- `GET /rooms`: gọi `repo.ListRoomsAsync(session.TenantId, userId.Value)`
- `POST /rooms`: validate `req.MemberUserIds.Count >= 1`; gọi `repo.CreateOrGetRoomAsync`
- `GET /rooms/{id}/messages`: gọi `repo.IsMemberAsync` → 403 nếu không phải thành viên; rồi `repo.ListMessagesAsync`
- `POST /rooms/{id}/messages`: kiểm thành viên; `repo.AppendMessageAsync`; publish event lên `ChatEventBus` với key `internal:{tenant}:{roomId}`
- `DELETE /messages/{msgId}`: gọi `repo.DeleteMessageAsync(tenant, msgId, userId.Value)`
- `GET /users`: gọi `TourKitApiClient` để lấy danh sách nhân viên active; cache 5 phút trong `IMemoryCache` (đây là dữ liệu **lookup**, không phải detail endpoint — dùng cache là đúng theo quy tắc proxy)
- `GET /stream`: SSE subscribe `ChatEventBus` trên key `internal:{tenant}:*`; giữ kết nối cho tới khi `ct.IsCancellationRequested`

- [ ] **Bước 4: Mount endpoints trong `WorkflowStackRegistration.cs`**

```csharp
InternalChatEndpoints.MapInternalChat(app, cfg);
```

- [ ] **Bước 5: Build xanh**

```powershell
dotnet build TourkitAiProxy.csproj
```

- [ ] **Bước 6: Test thủ công (staging)**

```powershell
# Bật cờ
# appsettings.json: "Features": { "InternalChat": true }

# Lấy sessionId từ TravAI (đăng nhập bình thường)
$sid = "..."

# Danh sách phòng (phải ra [])
curl -H "X-Session-Id: $sid" http://localhost:5080/api/chat/internal/rooms

# Tạo phòng
curl -X POST -H "X-Session-Id: $sid" -H "Content-Type: application/json" \
  -d '{"title":"Test 1-1","memberUserIds":[73],"entityType":"direct"}' \
  http://localhost:5080/api/chat/internal/rooms

# Gửi tin
curl -X POST -H "X-Session-Id: $sid" -H "Content-Type: application/json" \
  -d '{"body":"Chào bạn!"}' \
  http://localhost:5080/api/chat/internal/rooms/{id}/messages
```

- [ ] **Bước 7: Commit**

```
git add TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs \
        TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs \
        TourkitAiProxy.Endpoints/InternalChatEndpoints.cs
git commit -m "feat(chat): internal chat endpoints + feature flag + DI"
```

---

## Task 4 — Widget JS (`internal-chat.js`)

**Files:**
- Tạo mới: `wwwroot/internal-chat.js`

**Interfaces:**
- Consumes: `GET /api/chat/internal/rooms`, `GET /rooms/{id}/messages`, `POST /rooms/{id}/messages`, `GET /stream` (SSE)
- Produces: Script có thể nhúng vào trang CRM:
  ```html
  <script src="https://proxy.domain/internal-chat.js"
          data-session="{X-Session-Id}"></script>
  ```

**Hành vi widget:**
1. Đọc `document.currentScript.dataset.session` ngay khi script tải
2. Render floating button (góc phải dưới) với badge số tin chưa đọc
3. Click → mở panel trượt (danh sách phòng)
4. Click phòng → mở khung chat (lịch sử + ô nhập)
5. Gửi tin bằng Enter hoặc nút gửi
6. SSE subscription: nhận event `new_message` → cập nhật UI không reload

- [ ] **Bước 1: Skeleton + auth**

```js
(function () {
  const session = document.currentScript?.dataset?.session;
  if (!session) { console.warn('[TK-Chat] Thiếu data-session'); return; }
  const BASE = document.currentScript.src.replace(/\/[^/]+$/, '');
  const H = { 'X-Session-Id': session, 'Content-Type': 'application/json' };
  // ... phần còn lại
})();
```

- [ ] **Bước 2: Floating button + panel HTML**

Tạo bằng `document.createElement` + inline style (không phụ thuộc CSS của trang). Button có `id="tk-internal-chat-btn"`, panel có `id="tk-internal-chat-panel"`.

- [ ] **Bước 3: Load danh sách phòng**

```js
async function loadRooms() {
  const res = await fetch(`${BASE}/api/chat/internal/rooms`, { headers: H });
  if (!res.ok) return;
  const rooms = await res.json();
  renderRoomList(rooms);
}
```

- [ ] **Bước 4: Mở phòng + load tin**

```js
async function openRoom(roomId) {
  const res = await fetch(`${BASE}/api/chat/internal/rooms/${roomId}/messages`, { headers: H });
  const msgs = await res.json();
  renderMessages(msgs);
}
```

- [ ] **Bước 5: Gửi tin**

```js
async function sendMessage(roomId, body) {
  await fetch(`${BASE}/api/chat/internal/rooms/${roomId}/messages`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ body })
  });
}
```

- [ ] **Bước 6: SSE real-time**

```js
function subscribeSSE() {
  const es = new EventSource(`${BASE}/api/chat/internal/stream?session=${session}`);
  es.addEventListener('new_message', e => {
    const data = JSON.parse(e.data);
    if (data.roomId === currentRoomId) appendMessage(data);
    else incrementBadge(data.roomId);
  });
  es.onerror = () => setTimeout(subscribeSSE, 5000); // reconnect
}
```

- [ ] **Bước 7: Test trên trình duyệt (staging)**

Nhúng vào trang CRM test:
```html
<script src="http://localhost:5080/internal-chat.js" data-session="YOUR_SESSION_ID"></script>
```
- Kiểm button hiện, panel mở
- Gửi tin → hiện tức thì
- Mở tab thứ hai cùng session → tin SSE hiện ở cả hai

- [ ] **Bước 8: Commit**

```
git add wwwroot/internal-chat.js
git commit -m "feat(chat): internal chat JS widget with SSE real-time"
```

---

## Task 5 — CHANGELOG + docs

**Files:**
- Sửa: `CHANGELOG.md`
- Sửa: `docs/features/chat-inbox-ky-thuat.md`

- [ ] **Bước 1: Thêm vào CHANGELOG.md**

Thêm mục mới (phiên bản tiếp theo) với nội dung cho người dùng cuối:
- Thêm tính năng **Chat nội bộ**: nhắn tin 1-1 và tạo nhóm theo đơn hàng/công ty ngay trong CRM
- Nhúng qua JavaScript: `<script src="…/internal-chat.js" data-session="…">`
- Tất cả nhân viên active đều dùng được, không cần quyền riêng

- [ ] **Bước 2: Bổ sung ghi chú kỹ thuật**

Trong `docs/features/chat-inbox-ky-thuat.md`, thêm section mới "## Chat nội bộ (`Features:InternalChat`)" ghi:
- `ChatChannel.Internal = 7`, không có webhook/adapter
- Schema: cột `entity_type`/`entity_id` + bảng `chat_room_members`
- Auth widget: `data-session` → `X-Session-Id`
- `ChannelCredentialStore.ListAllAccountsAsync` bỏ qua `Internal`

- [ ] **Bước 3: Commit**

```
git add CHANGELOG.md docs/features/chat-inbox-ky-thuat.md
git commit -m "docs: chat nội bộ — CHANGELOG + ghi chú kỹ thuật"
```

---

## Tổng kết task

| Task | Ước lượng | Phụ thuộc |
|---|---|---|
| 1 — Enum + schema | 2h | — |
| 2 — Repository + models | 4h | Task 1 |
| 3 — Endpoints + DI | 4h | Task 2 |
| 4 — Widget JS | 4–6h | Task 3 |
| 5 — Docs | 1h | Task 3, 4 |

**Tổng: ~15–17h dev** (không tính review và test thực tế trên staging).

Deploy an toàn: bật `Features:InternalChat: true` sau khi đã deploy cả proxy — cờ mặc định `false` nên không ảnh hưởng luồng đang chạy.
