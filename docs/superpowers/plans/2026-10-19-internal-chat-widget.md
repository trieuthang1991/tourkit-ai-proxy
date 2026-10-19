# Internal Chat Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Xây chat nội bộ (1-1, phòng theo đơn/công ty, nhóm tự do) nhúng vào CRM qua JS widget với auth `data-session` từ script tag.

**Architecture:** Tái dùng PostgreSQL chat DB hiện có (`ChatDb`). Phòng nội bộ là một dòng `chat_conversations` với `channel = 7`; tin là dòng `chat_messages`; thành viên ở bảng mới `chat_room_members`; đã-đọc tái dùng `chat_conversation_reads`. **Vì nằm chung bảng với hội thoại khách nên mọi câu đọc/đếm/chia việc/dọn của hộp thư khách phải kẹp `channel <> 7`** — đây là rủi ro chính của thiết kế và có guard test riêng. Real-time đi qua `ChatEventBus` sẵn có, mở rộng bằng `Recipients` để bus tự tách hai thế giới (khách / nội bộ). Widget đọc `data-session`, gọi backend bằng header `X-Session-Id`; SSE đi `?sessionId=` vì `EventSource` không gửi header được — `SessionAuth.Read` đã hỗ trợ sẵn.

**Tech Stack:** C# .NET 8, PostgreSQL 17 (Dapper, Npgsql), JS ES2020 thuần (không bundler), SSE, `TkSessionStore`.

**Spec:** Thiết kế từ hội thoại `2026-10-19` (session `01QWA8hha1ePaXs7eXfCkqLm`) + lượt soát ngày 19/10 (13 điểm, ghi ở ledger `.superpowers/sdd/2026-10-19-internal-chat-widget/progress.md`). Không có file spec riêng.

## Global Constraints

- Schema idempotent (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`); cột mới đều **nullable, không default** → ALTER chỉ ghi metadata, không viết lại bảng.
- Tên định danh C# tiếng Anh; log/chú thích tiếng Việt. Tên test theo lối repo (`Khoa_hoi_thoai_phai_co_account_id`).
- UTC, `timestamptz`, JSON kèm `Z`.
- `ChatChannel.Internal = 7` — cố định.
- Auth: `SessionAuth.Read(ctx, sessions)` → `sessions.EnsureCrmUserIdAsync(sid, ct)`; null → 401. Không cần quyền kênh; mọi phiên hợp lệ đều dùng được.
- Cờ `Features:InternalChat` (mặc định `false`), độc lập với `Features:Chat`; cần `ConnectionStrings:Chat` (`repo.Configured`).
- Đường dẫn API: `/api/v1/chat/internal/*` (cùng gốc với hộp thư khách, KHÔNG phải `/api/chat/`).
- `ChannelCredentialStore.ListAllAccountsAsync()` bỏ qua `Internal`.
- Mọi câu SQL của `InternalChatRepository` kẹp `tenant_id = @tenant`; kiểm thành viên **gộp trong chính câu đọc/ghi** (`EXISTS (… chat_room_members …)`), không tách round-trip riêng.
- Không N+1: thêm thành viên bằng một câu `unnest(@ids)`; đọc phòng + thành viên tối đa 2 round-trip.
- Widget: `wwwroot/internal-chat.js`, file tĩnh.
- Build xanh sau mỗi task; `dotnet test` toàn bộ (không lọc) xanh trước mỗi commit.

## Quy ước khoá phòng (quyết định ở lượt soát)

Phòng nội bộ ghi vào `chat_conversations` với `channel = 7`, `account_id = ''`, `surface = 0`, `source_thread_id = ''`, và **`contact_external_id` là khoá phòng**:

| Loại | `entity_type` | `contact_external_id` | Chống trùng |
|---|---|---|---|
| 1-1 | `direct` | `direct:{minUserId}:{maxUserId}` | A mở với B hay B mở với A → cùng dòng |
| theo đơn | `order` | `order:{entityId}` | **một phòng duy nhất per đơn** |
| theo công ty | `company` | `company:{entityId}` | một phòng per công ty |
| nhóm tự do | `group` | `group:{guid:N}` | không chống trùng |

Chống trùng đi bằng chỉ mục sẵn có `ux_conv_scope_surface` — không tạo chỉ mục mới. Ghi `''` vào `contact_external_id` là **mỗi công ty chỉ tạo được đúng một phòng** (vỡ unique từ dòng thứ hai).

## Review Focus

1. **Phòng nội bộ lọt vào luồng khách**: vòng quay chia việc (`HoiThoaiChuaCoNguoiAsync`) giao phòng nội bộ cho nhân viên trực; hộp thư khách chọn "tất cả kênh" hiện phòng nội bộ; chip đếm lệch; dọn theo mốc xoá phòng nội bộ. Guard: `InternalChatGuardTests.Hoi_thoai_khach_phai_loai_phong_noi_bo`.
2. **Rò rỉ cross-tenant**: mọi câu có `tenant_id = @tenant`; guard regex trên source repo. Thủ công: session tenant A gọi `roomId` của tenant B → 403/404.
3. **Bus giao nhầm**: sự kiện nội bộ không tới tab hộp thư khách (kể cả admin `XemTatCa`) và ngược lại; qua Redis (`Pack`/`FromRemote`) `Recipients` không bị rơi. Unit test thật trong `ChatEventBusTests`.
4. **CrmUserId null**: `EnsureCrmUserIdAsync` thất bại → 401 rõ ràng ở mọi endpoint nội bộ.
5. **Badge không về 0**: phải có `POST /rooms/{id}/read`; widget gọi khi mở phòng và khi nhận tin trong phòng đang mở. Đếm chưa đọc tính từ `COALESCE(last_read_at, joined_utc)` và **trần 100** để phòng bỏ quên không làm chậm danh sách.

---

## File Map

| Trạng thái | File | Trách nhiệm |
|---|---|---|
| Sửa | `TourkitAiProxy.Domain/Chat/ChatModels.cs` | `ChatChannel.Internal = 7`; `ChatEvent.Recipients` |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs` | Bỏ qua `Internal` trong `ListAllAccountsAsync` |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` | Schema: 3 cột `chat_conversations`, 1 cột `chat_messages`, bảng `chat_room_members` |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs` | `channel <> 7` ở `ListConversationsAsync`, `CountAsync`, `GetConversationAsync`, câu đọc theo messageId (:416), hàm dọn theo mốc (:1699) |
| Sửa | `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs` | `channel <> 7` ở `HoiThoaiChuaCoNguoiAsync` |
| Tạo | `TourkitAiProxy.Domain/Chat/InternalChatModels.cs` | Records + `InternalRoomKey` (hàm thuần dựng khoá phòng) |
| Tạo | `TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs` | 8 phương thức SQL |
| Sửa | `TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs` | `SubscribeInternalAsync` + tách giao theo `Recipients` |
| Sửa | `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs` | `InternalChat()` |
| Sửa | `TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs` | DI + map endpoints sau cờ |
| Tạo | `TourkitAiProxy.Endpoints/ChatStaffDirectory.cs` | Tách khối đọc nhân viên ERP (đệm Redis 2h) khỏi `/assign-settings` để dùng chung |
| Sửa | `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `/assign-settings` gọi `ChatStaffDirectory` (chỉ tách, không đổi hành vi) |
| Tạo | `TourkitAiProxy.Endpoints/InternalChatEndpoints.cs` | 8 endpoint |
| Tạo | `TourkitAiProxy.Tests/Chat/InternalChatGuardTests.cs` | Guard Task 1 |
| Tạo | `TourkitAiProxy.Tests/Chat/InternalChatRepositoryGuardTests.cs` | Guard Task 2 + unit test `InternalRoomKey` |
| Sửa | `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs` | 4 test `Recipients` |
| Tạo | `wwwroot/internal-chat.js` | Widget |
| Sửa | `appsettings.example.json`, `Configuration/CorsSetup.cs` (chú thích), `CHANGELOG.md`, `docs/features/chat-inbox-ky-thuat.md`, `docs/api-surface.md` | Task 5 |

---

## Task 1 — Enum, schema, kẹp `channel <> 7` cho luồng khách

**Files:**
- Sửa: `ChatModels.cs` (enum), `ChannelCredentialStore.cs:98`, `ChatDb.cs` (cuối `SchemaSql`), `ChatRepository.cs` (5 câu), `ChatAssignRepository.cs:289`
- Test: `TourkitAiProxy.Tests/Chat/InternalChatGuardTests.cs`

**Interfaces:**
- Produces: `ChatChannel.Internal = 7`; cột `chat_conversations.entity_type/entity_id/title`, `chat_messages.sender_user_id`, bảng `chat_room_members` + `ix_room_members_user`.

- [ ] **Bước 1: Viết guard test (RED)** — `InternalChatGuardTests.cs`, dùng `DocFile` như `ChatSchemaGuardTests`:

```csharp
[Fact] public void Internal_channel_value_is_7()
    => Assert.Equal((short)7, (short)ChatChannel.Internal);

[Theory]
[InlineData("ChatRepository.cs", "ListConversationsAsync")]
[InlineData("ChatRepository.cs", "CountAsync")]
[InlineData("ChatRepository.cs", "GetConversationAsync")]
[InlineData("ChatAssignRepository.cs", "HoiThoaiChuaCoNguoiAsync")]
public void Hoi_thoai_khach_phai_loai_phong_noi_bo(string file, string method)
{
    // Thân hàm = từ chữ ký "Async(" của hàm tới chữ "public " kế tiếp.
    var than = ThanHam(DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/" + file), method);
    Assert.Contains("channel <> 7", than);
}

[Fact] public void Don_theo_moc_khong_xoa_phong_noi_bo()
{
    var src = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");
    var m = Regex.Match(src, @"DELETE FROM chat_conversations WHERE ([^""]+)");
    Assert.True(m.Success);
    Assert.Contains("channel <> 7", m.Groups[1].Value);
}

[Fact] public void Schema_noi_bo_du_cot_va_chi_muc()
{
    var sql = DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
    Assert.Contains("ADD COLUMN IF NOT EXISTS entity_type text", sql);
    Assert.Contains("ADD COLUMN IF NOT EXISTS entity_id", sql);
    Assert.Contains("ADD COLUMN IF NOT EXISTS title", sql);
    Assert.Contains("ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS sender_user_id integer", sql);
    Assert.Contains("CREATE TABLE IF NOT EXISTS chat_room_members", sql);
    Assert.Matches(@"ix_room_members_user\s+ON chat_room_members \(tenant_id, user_id\) WHERE left_utc IS NULL", sql);
}

[Fact] public void ListAllAccountsAsync_bo_qua_Internal()
{
    var than = ThanHam(DocFile("TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs"),
                       "ListAllAccountsAsync");
    Assert.Contains("if (kenh == ChatChannel.Internal) continue;", than);
}
```

- [ ] **Bước 2: Chạy, xác nhận RED** — `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter InternalChatGuardTests`
  Expected: `Internal_channel_value_is_7` không biên dịch được (chưa có enum) → sửa tạm bằng cách thêm enum trước (Bước 3a), chạy lại: 6 test còn lại FAIL vì thiếu chữ.

- [ ] **Bước 3a: Enum** — sau `TikTok = 6,` thêm `/// <summary>Chat nội bộ giữa nhân viên. Không có webhook, không có OA credential.</summary> Internal = 7,`

- [ ] **Bước 3b: `ChannelCredentialStore.ListAllAccountsAsync`** — dòng đầu thân `foreach`: `if (kenh == ChatChannel.Internal) continue;`

- [ ] **Bước 3c: Schema** — thêm cuối `SchemaSql`:

```sql
-- Chat nội bộ giữa nhân viên (channel = 7). Phòng là một dòng chat_conversations;
-- contact_external_id là KHOÁ PHÒNG (direct:a:b / order:id / company:id / group:guid) và chống
-- trùng bằng chính ux_conv_scope_surface. Ba cột dưới nullable, không default → chỉ ghi metadata.
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_type text;
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS entity_id   text;
ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS title       text;
-- Mã người gửi. sender_username là chữ; cả cụm chat đã chuẩn hoá sang mã người (đặc tả 4b).
ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS sender_user_id integer;

-- Thành viên phòng nội bộ. left_utc NULL = còn là thành viên (chưa có API rời phòng — cột để sẵn).
CREATE TABLE IF NOT EXISTS chat_room_members (
  conversation_id bigint      NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
  tenant_id       text        NOT NULL,
  user_id         integer     NOT NULL,
  joined_utc      timestamptz NOT NULL DEFAULT now(),
  left_utc        timestamptz,
  PRIMARY KEY (conversation_id, user_id)
);
-- "Phòng của tôi": ListRoomsAsync đi bằng chỉ mục này; vị từ phải khớp câu WHERE của nó.
CREATE INDEX IF NOT EXISTS ix_room_members_user
  ON chat_room_members (tenant_id, user_id) WHERE left_utc IS NULL;
```

- [ ] **Bước 3d: Kẹp `channel <> 7`** — thêm `AND v.channel <> 7` (hoặc `AND channel <> 7` nếu không alias) vào WHERE của: `ListConversationsAsync` (ChatRepository.cs:489), `CountAsync` (:782), `GetConversationAsync` (:387, câu `FROM chat_conversations v LEFT JOIN chat_contacts`), câu đọc theo messageId (:416), hàm dọn theo mốc (:1699 `DELETE FROM chat_conversations WHERE last_activity_at < …`), `HoiThoaiChuaCoNguoiAsync` (ChatAssignRepository.cs:292). Kèm một dòng chú thích tiếng Việt tại mỗi chỗ: "phòng chat nội bộ (7) nằm chung bảng — luồng khách không được thấy".

- [ ] **Bước 4: GREEN** — chạy lại filter: 7/7 pass. Rồi **toàn bộ**: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → xanh (chú ý `ChatSchemaGuardTests` vẫn xanh vì không đụng chỉ mục).

- [ ] **Bước 5: Build** — `dotnet build TourkitAiProxy.csproj` → 0 lỗi.

- [ ] **Bước 6: Chạy schema trên DB thật (staging)** — theo skill `server-migrate`: backup `-Tag pre-internal-chat-schema`, rồi chạy app local trỏ `ConnectionStrings:Chat` staging HOẶC chạy khối SQL trên qua `psql` trong `BEGIN; … ROLLBACK;` để xác nhận không lỗi cú pháp; chạy lần 2 phải no-op.

- [ ] **Bước 7: Commit**
```
git add TourkitAiProxy.Domain/Chat/ChatModels.cs TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs \
        TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs \
        TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs TourkitAiProxy.Tests/Chat/InternalChatGuardTests.cs
git commit -m "feat(chat): kênh Internal=7, schema phòng nội bộ, luồng khách kẹp channel <> 7"
```

---

## Task 2 — Domain models + `InternalChatRepository`

**Files:**
- Tạo: `TourkitAiProxy.Domain/Chat/InternalChatModels.cs`, `TourkitAiProxy.Infrastructure/Chat/Inbox/InternalChatRepository.cs`
- Test: `TourkitAiProxy.Tests/Chat/InternalChatRepositoryGuardTests.cs`

**Interfaces:**
- Consumes: `ChatDb`, schema Task 1.
- Produces:

```csharp
// Domain/Chat/InternalChatModels.cs — namespace TourkitAiProxy.Domain.Chat
public static class InternalRoomKey
{
    public const short Channel = 7;
    public static string Direct(int a, int b);                 // "direct:{min}:{max}"; a == b → ArgumentException
    public static string ForEntity(string entityType, string entityId); // "order:123" | "company:9"; type khác → ArgumentException
    public static string Group() => "group:" + Guid.NewGuid().ToString("N");
    public static bool IsValidType(string? t) => t is "direct" or "order" or "company" or "group";
}
public record InternalMember(int UserId, DateTime JoinedUtc);
public record InternalRoom(long Id, string? Title, string EntityType, string? EntityId, string RoomKey,
    string? LastPreview, DateTime LastActivityAt, int UnreadCount, IReadOnlyList<InternalMember> Members);
public record InternalMessage(long Id, long RoomId, int? SenderUserId, string? SenderUsername,
    string? Body, DateTime CreatedUtc, DateTime? DeletedUtc);
public record CreateRoomRequest(string? Title, List<int> MemberUserIds, string EntityType = "direct", string? EntityId = null);
public record SendInternalMessageRequest(string Body);
/// Kết quả ghi tin: Id null = người gửi không phải thành viên. Members để bus biết giao cho ai.
public record AppendResult(long? Id, DateTime CreatedUtc, int[] Members);
```

```csharp
// Infrastructure/Chat/Inbox/InternalChatRepository.cs — ctor(ChatDb db, ILogger<InternalChatRepository> log)
public bool Configured => _db.Configured;
Task<long> CreateOrGetRoomAsync(string tenant, int creatorUserId, string roomKey, string entityType,
    string? entityId, string? title, IReadOnlyCollection<int> memberIds, CancellationToken ct);
Task<IReadOnlyList<InternalRoom>> ListRoomsAsync(string tenant, int userId, int limit, CancellationToken ct);
Task<IReadOnlyList<InternalMessage>?> ListMessagesAsync(string tenant, long roomId, int userId,
    DateTime? beforeUtc, long? beforeId, int limit, CancellationToken ct);      // null = không phải thành viên
Task<IReadOnlyList<InternalMessage>?> ListMessagesAfterAsync(string tenant, long roomId, int userId,
    long afterId, int limit, CancellationToken ct);
Task<AppendResult> AppendMessageAsync(string tenant, long roomId, int senderUserId, string senderUsername,
    string body, CancellationToken ct);
Task<bool> MarkReadAsync(string tenant, long roomId, int userId, CancellationToken ct);
Task<bool> DeleteOwnMessageAsync(string tenant, long messageId, int userId, CancellationToken ct);
```

- [ ] **Bước 1: Unit test thật cho `InternalRoomKey` (RED)** — trong `InternalChatRepositoryGuardTests.cs`:
  - `Khoa_1_1_khong_phu_thuoc_thu_tu`: `Direct(5,3) == Direct(3,5) == "direct:3:5"`.
  - `Khoa_1_1_tu_chat_voi_minh_thi_nem`: `Direct(4,4)` → `ArgumentException`.
  - `Khoa_entity_dung_dinh_dang`: `ForEntity("order","123") == "order:123"`; `ForEntity("lead","1")` → `ArgumentException`.
  - `Khoa_nhom_moi_lan_khac_nhau`: `Group() != Group()`, bắt đầu bằng `"group:"`.

- [ ] **Bước 2: RED** — `dotnet test … --filter InternalChatRepositoryGuardTests` → không biên dịch (chưa có type). Tạo `InternalChatModels.cs` với records + `InternalRoomKey` → chạy lại: 4/4 PASS.

- [ ] **Bước 3: Guard test cho SQL (RED)** — thêm vào cùng file, đọc source `InternalChatRepository.cs`:
  - `Moi_cau_SQL_deu_kep_tenant`: mọi khối `"""…"""` chứa `chat_` phải chứa `tenant_id = @tenant` **hoặc** `mb.tenant_id = @tenant`.
  - `Tao_phong_ON_CONFLICT_khop_chi_muc`: cột trong `ON CONFLICT (…)` của câu `INSERT INTO chat_conversations` == cột `ux_conv_scope_surface` trong `ChatDb.cs` (chép cách so của `ChatSchemaGuardTests.ON_CONFLICT_phai_khop_voi_chi_muc`).
  - `Them_thanh_vien_mot_cau_unnest`: thân `CreateOrGetRoomAsync` chứa `unnest(@` và chứa `ON CONFLICT (conversation_id, user_id) DO UPDATE SET left_utc = NULL`.
  - `Lich_su_di_dung_chi_muc_ix_msg_conv`: thân `ListMessagesAsync` chứa `ORDER BY m.created_utc DESC, m.id DESC`.
  - `Kiem_thanh_vien_gop_trong_cau`: thân `AppendMessageAsync`, `ListMessagesAsync`, `ListMessagesAfterAsync`, `MarkReadAsync` đều chứa `FROM chat_room_members` (không có hàm `IsMemberAsync` riêng: `Assert.DoesNotContain("IsMemberAsync", src)`).
  - `Dem_chua_doc_co_tran`: thân `ListRoomsAsync` chứa `LIMIT 100`.
  Chạy → FAIL (file chưa có).

- [ ] **Bước 4: Viết `InternalChatRepository`** theo các quyết định SQL sau (mỗi hàm ≤ 1 transaction, số round-trip ghi cạnh tên):

  - `CreateOrGetRoomAsync` — **1 transaction, 2 câu**:
    ```sql
    INSERT INTO chat_conversations (tenant_id, channel, account_id, contact_external_id, surface, source_thread_id,
                                    entity_type, entity_id, title, status, last_activity_at)
    VALUES (@tenant, 7, '', @roomKey, 0, '', @entityType, @entityId, @title, 1, now())
    ON CONFLICT (tenant_id, channel, account_id, contact_external_id, surface, source_thread_id)
    DO UPDATE SET title = COALESCE(chat_conversations.title, EXCLUDED.title)
    RETURNING id;
    -- memberIds đã gồm creatorUserId, đã DISTINCT ở tầng gọi
    INSERT INTO chat_room_members (conversation_id, tenant_id, user_id)
    SELECT @id, @tenant, unnest(@ids)
    ON CONFLICT (conversation_id, user_id) DO UPDATE SET left_utc = NULL;
    ```
  - `ListRoomsAsync` — **2 round-trip** (phòng, rồi thành viên theo `conversation_id = ANY(@ids)`):
    ```sql
    SELECT v.id, v.title, v.entity_type, v.entity_id, v.contact_external_id AS room_key,
           v.last_preview, v.last_activity_at,
           (SELECT count(*)::int FROM (
              SELECT 1 FROM chat_messages m
               WHERE m.conversation_id = v.id
                 AND m.created_utc > COALESCE(r.last_read_at, mb.joined_utc)
                 AND m.sender_user_id IS DISTINCT FROM @userId
                 AND m.deleted_utc IS NULL
               LIMIT 100) x) AS unread_count          -- trần 100: phòng bỏ quên không kéo chậm danh sách
      FROM chat_room_members mb
      JOIN chat_conversations v ON v.id = mb.conversation_id AND v.tenant_id = @tenant AND v.channel = 7
      LEFT JOIN chat_conversation_reads r
        ON r.tenant_id = @tenant AND r.conversation_id = v.id AND r.user_id = @userId
     WHERE mb.tenant_id = @tenant AND mb.user_id = @userId AND mb.left_utc IS NULL
     ORDER BY v.last_activity_at DESC, v.id DESC
     LIMIT @limit
    ```
    (`mb` đi `ix_room_members_user`; `v` theo PK; đếm chưa đọc đi `ix_msg_conv (conversation_id, created_utc)`.)
  - `ListMessagesAsync` — 1 câu, thành viên gộp:
    ```sql
    SELECT m.id, m.conversation_id AS room_id, m.sender_user_id, m.sender_username, m.body, m.created_utc, m.deleted_utc
      FROM chat_messages m
     WHERE m.conversation_id = @roomId AND m.tenant_id = @tenant
       AND EXISTS (SELECT 1 FROM chat_room_members mb WHERE mb.conversation_id = @roomId
                    AND mb.tenant_id = @tenant AND mb.user_id = @userId AND mb.left_utc IS NULL)
       AND (@beforeUtc::timestamptz IS NULL OR (m.created_utc, m.id) < (@beforeUtc::timestamptz, @beforeId::bigint))
     ORDER BY m.created_utc DESC, m.id DESC
     LIMIT @limit
    ```
    Không phải thành viên → câu trả 0 dòng, nhưng phòng rỗng cũng 0 dòng: hàm chạy **thêm** `SELECT EXISTS(member)` chỉ khi kết quả rỗng (hiếm) để phân biệt null/rỗng.
  - `ListMessagesAfterAsync` — như trên với `m.id > @afterId ORDER BY m.id LIMIT @limit`.
  - `AppendMessageAsync` — **1 câu CTE**, trả `Id null` nếu không phải thành viên:
    ```sql
    WITH tv AS (SELECT array_agg(user_id) AS members FROM chat_room_members
                 WHERE conversation_id = @roomId AND tenant_id = @tenant AND left_utc IS NULL),
         m AS (INSERT INTO chat_messages (tenant_id, conversation_id, channel, direction, sender_kind,
                                          sender_username, sender_user_id, kind, body, state)
               SELECT @tenant, @roomId, 7, 1, 2, @senderUsername, @senderUserId, 0, @body, 1
                WHERE EXISTS (SELECT 1 FROM tv WHERE @senderUserId = ANY(members))
               RETURNING id, created_utc),
         u AS (UPDATE chat_conversations c SET last_preview = left(@body, 200), last_activity_at = m.created_utc
                 FROM m WHERE c.id = @roomId AND c.tenant_id = @tenant)
    SELECT m.id, m.created_utc, tv.members FROM tv LEFT JOIN m ON true
    ```
    (`direction = 1`, `attachment NULL` → vòng soi tệp bỏ qua; không ghi `chat_outbox`.)
  - `MarkReadAsync` — `INSERT INTO chat_conversation_reads … SELECT @tenant, @roomId, @userId, now() WHERE EXISTS(member) ON CONFLICT (tenant_id, conversation_id, user_id) DO UPDATE SET last_read_at = now()` → `rows > 0`.
  - `DeleteOwnMessageAsync` — `UPDATE chat_messages SET deleted_utc = now() WHERE id = @id AND tenant_id = @tenant AND channel = 7 AND sender_user_id = @userId AND deleted_utc IS NULL` → `rows > 0`.

- [ ] **Bước 5: GREEN** — filter → tất cả pass; toàn bộ suite xanh; `dotnet build TourkitAiProxy.csproj` xanh.

- [ ] **Bước 6: Đo trên DB thật (staging, `BEGIN … ROLLBACK`)** — `EXPLAIN (ANALYZE, BUFFERS)` cho `ListRoomsAsync` và `ListMessagesAsync` với tham số đúng kiểu: `ix_room_members_user` Index Scan, `ix_msg_conv` Index Scan, không Seq Scan trên `chat_messages`. Ghi số buffers vào ledger.

- [ ] **Bước 7: Commit** — `feat(chat): InternalChatRepository + models cho chat nội bộ`

---

## Task 3 — Bus, cờ, DI, `ChatStaffDirectory`, endpoints

**Files:**
- Sửa: `ChatModels.cs` (`ChatEvent`), `ChatEventBus.cs`, `ChatEventBusTests.cs`, `FeatureFlags.cs`, `WorkflowStackRegistration.cs`, `ChatInboxEndpoints.cs` (`/assign-settings`)
- Tạo: `TourkitAiProxy.Endpoints/ChatStaffDirectory.cs`, `TourkitAiProxy.Endpoints/InternalChatEndpoints.cs`

**Interfaces:**
- Consumes: Task 2 repo; `SessionAuth.Read`, `TkSessionStore.EnsureCrmUserIdAsync/GetValidJwtAsync`, `TourKitApiClient.GetAsync(jwt, "/api/ai/reference", ct)`, `RedisStore`.
- Produces: `ChatEvent.Recipients`; `ChatEventBus.SubscribeInternalAsync(tenant, userId, ct)`; `ChatStaffDirectory.ListAsync(...)`; 8 endpoint dưới `/api/v1/chat/internal`.

| Method | Path | Ghi chú |
|---|---|---|
| GET | `/rooms?limit=50` | `ListRoomsAsync` |
| POST | `/rooms` | body `CreateRoomRequest`; validate: `IsValidType`, `direct` đúng 1 người khác, `order/company` cần `EntityId`, mọi `MemberUserIds` phải có trong `ChatStaffDirectory` của tenant; dựng khoá bằng `InternalRoomKey`; thêm chính mình vào `memberIds` |
| GET | `/rooms/{id}/messages?beforeUtc&beforeId&afterId&limit=50` | `afterId` → `ListMessagesAfterAsync`, còn lại `ListMessagesAsync`; null → 403 |
| POST | `/rooms/{id}/messages` | body `{ body }` (trim, 1..4000 ký tự); `AppendMessageAsync`; `Id null` → 403; `bus.Publish(new ChatEvent(tenant, roomId, "noi-bo", id) { Recipients = members })` |
| POST | `/rooms/{id}/read` | `MarkReadAsync`; false → 403 |
| DELETE | `/messages/{msgId}` | `DeleteOwnMessageAsync`; false → 404 |
| GET | `/users` | `ChatStaffDirectory.ListAsync` → `[{id, name}]` |
| GET | `/events?sessionId=` | SSE, chép khung của `/api/v1/chat/events` (nhịp 25s, một luồng ghi), nguồn `bus.SubscribeInternalAsync` |

Guard chung mọi endpoint (trừ SSE dùng cùng logic): `SessionAuth.Read` → null 401; `EnsureCrmUserIdAsync` → null 401 kèm thông điệp "Phiên chưa có mã người dùng — đăng nhập lại"; `!repo.Configured` → 503 `Results.Problem("Chat nội bộ chưa được cấu hình (ConnectionStrings:Chat)")`.

- [ ] **Bước 1: Test bus (RED)** — thêm vào `ChatEventBusTests.cs` (dùng `ChoDangKyAsync` sẵn có):
  - `Su_kien_noi_bo_chi_toi_thanh_vien`: 2 người nghe `SubscribeInternalAsync("A", 7)` và `("A", 8)`; publish `Recipients = [7]` → chỉ 7 nhận.
  - `Su_kien_noi_bo_khong_toi_hop_thu_khach`: nghe `SubscribeAsync("A", NguoiXem.HeThong)` (admin xem tất cả); publish nội bộ `Recipients = [7]` → không nhận; publish khách thường → nhận.
  - `Su_kien_khach_khong_toi_nguoi_nghe_noi_bo`: nghe `SubscribeInternalAsync("A", 7)`; publish `ChatEvent("A", 1, "tin-moi", 1){ AssignedUserId = 7 }` → không nhận.
  - `Pack_giu_Recipients_qua_Redis`: `bus2.FromRemote(ChatEventBus.Pack("khac", e))` với `Recipients = [7]` → người nghe nội bộ 7 của `bus2` nhận và `Recipients` còn nguyên.
  Chạy → không biên dịch (chưa có `Recipients`/`SubscribeInternalAsync`).

- [ ] **Bước 2: Sửa bus (GREEN)**:
  - `ChatEvent`: `public int[]? Recipients { get; init; }` + docstring: khác null = sự kiện nội bộ.
  - `ChatEventBus._nghe` thêm cờ `bool NoiBo`. `PublishLocal`: `e.Recipients is not null` → giao cho người nghe `NoiBo && xem.CrmUserId is int u && e.Recipients.Contains(u)`; ngược lại → `!NoiBo && DuocThay(xem, e)` (giữ nguyên).
  - `SubscribeInternalAsync(string tenantId, int userId, CancellationToken ct)` = đăng ký với `new NguoiXem(userId, false)`, `NoiBo = true`; dùng chung thân với `SubscribeAsync` (tách private helper).
  Chạy test bus → pass; toàn bộ `ChatEventBusTests` cũ vẫn xanh.

- [ ] **Bước 3: `FeatureFlags.InternalChat`** — `public static bool InternalChat(IConfiguration cfg) => cfg.GetValue("Features:InternalChat", false);` kèm docstring theo lối file (độc lập `Chat`, cần `ConnectionStrings:Chat`).

- [ ] **Bước 4: `ChatStaffDirectory`** — tách nguyên khối ChatInboxEndpoints.cs:556–657 (đệm `chat:nhanvien:{tenant}` 2h, gọi `/api/ai/reference` → `lookups.sellers`, lọc `TryGetInt32`/`name` chuỗi, 3 dòng cảnh báo giữ nguyên chữ) thành
  `static Task<StaffList> ListAsync(TourKitApiClient api, RedisStore redis, TkSessionStore sessions, string tenant, string sessionId, ILogger log, CancellationToken ct)` với `record StaffItem(int Id, string Name)`, `record StaffList(List<StaffItem> Staffs, int ErpRows, bool Failed)`. `/assign-settings` gọi hàm này và giữ **y nguyên** JSON trả ra (`staffs`, các trường đếm/cờ đang có). Đây là tách thuần, không đổi hành vi — kiểm ở Bước 8.

- [ ] **Bước 5: `InternalChatEndpoints.MapInternalChat(IEndpointRouteBuilder routes)`** theo bảng trên. SSE: chép khung `/events` hiện có (headers, `DisableBuffering`, `PeriodicTimer` 25s, `WhenAny`), chỉ đổi nguồn sang `SubscribeInternalAsync`. Dữ liệu SSE: `data: {json ChatEvent}` — widget chỉ nhận "phòng X có tin Y", tự gọi `?afterId=`.

- [ ] **Bước 6: DI + map** trong `WorkflowStackRegistration.cs`: `services.AddScoped<InternalChatRepository>()` cạnh `ChatAssignRepository`; map endpoints sau `if (FeatureFlags.InternalChat(cfg))` cùng chỗ `MapInbox` được gate bởi `FeatureFlags.Chat`. Thêm `"InternalChat": false` vào `appsettings.example.json` mục `Features`.

- [ ] **Bước 7: Build + toàn bộ test xanh.**

- [ ] **Bước 8: Test đầu cuối (staging, `Features:InternalChat=true`)** — lấy `$sid` từ dòng **staging** của bảng phiên (không lấy erp):
  ```powershell
  $h = @{ 'X-Session-Id' = $sid; 'Content-Type' = 'application/json' }
  irm http://localhost:5080/api/v1/chat/internal/rooms -Headers $h                       # []
  irm http://localhost:5080/api/v1/chat/internal/users -Headers $h | select -First 3
  $r = irm -Method POST http://localhost:5080/api/v1/chat/internal/rooms -Headers $h -Body '{"memberUserIds":[<id khác>],"entityType":"direct"}'
  irm -Method POST http://localhost:5080/api/v1/chat/internal/rooms -Headers $h -Body '{"memberUserIds":[<id khác>],"entityType":"direct"}'   # cùng id với $r
  irm -Method POST "http://localhost:5080/api/v1/chat/internal/rooms/$($r.id)/messages" -Headers $h -Body '{"body":"Chào"}'
  irm "http://localhost:5080/api/v1/chat/internal/rooms/$($r.id)/messages" -Headers $h
  irm -Method POST "http://localhost:5080/api/v1/chat/internal/rooms/$($r.id)/read" -Headers $h
  curl -N "http://localhost:5080/api/v1/chat/internal/events?sessionId=$sid"              # nhận "noi-bo" khi gửi tin từ cửa sổ khác
  ```
  Hồi quy: `irm /api/v1/chat/assign-settings -Headers $h` trước/sau Bước 4 phải cho cùng JSON; hộp thư khách `/api/v1/chat/conversations` **không** hiện phòng vừa tạo; `/api/v1/chat/events` của tab khách **không** nhận sự kiện khi gửi tin nội bộ. Phân quyền: `$sid` của người không phải thành viên gọi `/rooms/{id}/messages` → 403.

- [ ] **Bước 9: Commit** — `feat(chat): endpoint chat nội bộ, bus tách sự kiện theo người nhận, cờ Features:InternalChat`

---

## Task 4 — Widget `wwwroot/internal-chat.js`

**Interfaces:**
- Consumes: 8 endpoint Task 3.
- Produces: `<script src="https://<proxy>/internal-chat.js" data-session="{X-Session-Id}"></script>`

- [ ] **Bước 1: Khung + auth** — đọc `document.currentScript.dataset.session` **ngay lúc tải** (currentScript null sau đó); `BASE` từ `currentScript.src`; thiếu session → `console.warn`, không vẽ gì. Tạo `H = {'X-Session-Id': session, 'Content-Type': 'application/json'}`.

- [ ] **Bước 2: UI** — `document.createElement` + inline style, `id="tk-internal-chat-btn"` (nút tròn góc phải dưới + badge tổng `unreadCount`), `id="tk-internal-chat-panel"` (danh sách phòng ↔ khung tin). Tên người: nạp `/users` một lần, map `id → name`; phòng `direct` hiện tên người kia, phòng khác hiện `title` hoặc `entityType #entityId`.

- [ ] **Bước 3: Dữ liệu** — `loadRooms()`; `openRoom(id)` → `GET messages?limit=50` (đảo ngược để hiện cũ→mới) rồi `POST read`; cuộn lên đầu → `beforeUtc/beforeId` của tin cũ nhất; `send()` → POST, append trả về; Enter gửi, Shift+Enter xuống dòng.

- [ ] **Bước 4: SSE** — `new EventSource(BASE + '/api/v1/chat/internal/events?sessionId=' + encodeURIComponent(session))`; `onmessage`: parse; nếu `conversationId === currentRoomId` → `GET messages?afterId=<lastId>` rồi append + `POST read`; ngược lại → `loadRooms()` (gộp bằng `setTimeout` 1s). Để EventSource tự nối lại; khi `onopen` sau lỗi → `loadRooms()` + nếu đang mở phòng thì `afterId` (bù tin rơi lúc đứt).

- [ ] **Bước 5: Thử trên trình duyệt (staging)** — nhúng vào một trang HTML trống cùng origin proxy trước (không CORS), rồi từ origin khác (ví dụ `http://localhost:3000`) để xác nhận CORS/preflight: nút hiện; tạo phòng 1-1; gửi tin; mở tab 2 cùng session → tin hiện ở cả hai không reload; badge về 0 sau khi mở phòng; tắt server 10s rồi bật → widget tự nối lại và bù tin.

- [ ] **Bước 6: Commit** — `feat(chat): widget chat nội bộ (nút nổi, phòng, tin, SSE)`

---

## Task 5 — CHANGELOG, docs, cấu hình triển khai

- [ ] **Bước 1: `CHANGELOG.md`** — mục phiên bản kế tiếp, cho người dùng cuối: "Chat nội bộ: nhắn 1-1 hoặc mở phòng theo đơn hàng/công ty ngay trong CRM; mọi nhân viên đều dùng được; mỗi đơn/công ty có đúng một phòng chung; nhúng bằng một dòng script." Không tên file/hàm/bảng.

- [ ] **Bước 2: `docs/features/chat-inbox-ky-thuat.md`** — section "## Chat nội bộ (`Features:InternalChat`)": kênh 7 không webhook; quy ước khoá phòng (bảng ở đầu plan); **luật `channel <> 7` cho mọi câu mới chạm `chat_conversations` ở luồng khách** + tên guard test; bus tách theo `Recipients`; SSE `?sessionId=`; đếm chưa đọc trần 100; chưa có API rời phòng/thêm người; retention không xoá phòng nội bộ.

- [ ] **Bước 3: `docs/api-surface.md`** — 8 dòng endpoint.

- [ ] **Bước 4: CORS prod** — `Configuration/CorsSetup.cs` đang `SetIsOriginAllowed(_ => true)` (dev). Thêm chú thích: khi siết prod phải giữ origin CRM nhúng widget và `X-Session-Id` trong `AllowAnyHeader`. Ghi vào docs/operations.md mục triển khai: bật `Features:InternalChat` **sau** khi deploy + backup (`server-migrate`).

- [ ] **Bước 5: Commit** — `docs: chat nội bộ — CHANGELOG, ghi chú kỹ thuật, API, CORS`

---

## Tổng kết

| Task | Ước lượng | Phụ thuộc |
|---|---|---|
| 1 — Enum + schema + kẹp luồng khách | 3h | — |
| 2 — Models + repository | 5h | 1 |
| 3 — Bus + endpoints + tách staff directory | 6h | 2 |
| 4 — Widget | 4–5h | 3 |
| 5 — Docs | 1h | 3, 4 |

**Tổng ~19–20h.** Deploy: backup → deploy (schema tự chạy ở `InitAsync`, idempotent) → bật `Features:InternalChat`.
