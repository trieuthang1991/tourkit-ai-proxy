# Bổ sung hành động hộp thư chat — Đợt 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm bốn hành động hộp thư mà ChatbotX có còn mình chưa — đánh dấu chưa đọc, theo dõi hội thoại, chặn khách, xoá/sửa tin — tất cả đều chỉ đụng dữ liệu của mình, không gọi nền tảng nào.

**Architecture:** Bốn việc này gom chung MỘT đợt vì cùng một hình dạng: một cột (hoặc một bảng nhỏ) trong CSDL chat → một luật thuần trong `ChatRules` để test được → một endpoint → một nút trên giao diện → một dòng nhật ký thao tác. Không việc nào gọi API nhà cung cấp, nên không có ca "hỏng im lặng vì quên đăng ký bên Meta" như các đợt trước.

**Tech Stack:** ASP.NET Core 8 Minimal API · Dapper + PostgreSQL · React qua UMD/Babel (`wwwroot/`) · xUnit (test logic thuần, không chạm CSDL)

**Spec:** [2026-08-28-so-sanh-action-chatbotx.md](2026-08-28-so-sanh-action-chatbotx.md) — mục J (bảng đối chiếu từng action) và mục I (ranh giới cục bộ ↔ nền tảng)

## Global Constraints

- **CHỈ làm hành động ChatbotX CÓ.** Loại khỏi phạm vi: thu hồi tin phía khách (`deleteMessage` của Telegram), báo xấu lên nền tảng, chuyển tiếp tin xuyên hội thoại, xoá hội thoại — ChatbotX không có cái nào.
- **Chữ hiển thị, log, chú thích: tiếng Việt.** Tên định danh theo file đang sửa (route/thành viên/JSON tiếng Anh, biến cục bộ tiếng Việt).
- **Ngày giờ UTC, luôn kèm `Z`.** Lưu bằng `now()` phía SQL hoặc `DateTime.UtcNow`.
- **Mọi route mới phải có mặt trong `ChatInboxEndpoints.OwnedPaths`** — thiếu là rơi vào `MapFallback`, trả `index.html` kèm 200 thay vì 404. `ChatFeatureFlagCoverageTests` canh.
- **Mọi thao tác đổi trạng thái phải ghi `chat_audit`** — dùng `AppendAuditAsync`. `chi_tiet` **KHÔNG chứa nội dung tin**.
- **CHANGELOG.md bắt buộc** trước khi phát hành, viết cho người dùng cuối, không tên hàm/bảng.
- **Test là logic thuần**, không chạm CSDL. Chạy toàn bộ: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
- **Cột mới thêm bằng `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` với mặc định HẰNG** — mặc định biến đổi (`now()`) khiến Postgres viết lại cả bảng và khoá bảng tin nhắn hàng phút.

---

## File Structure

| File | Trách nhiệm trong đợt này |
|---|---|
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | Bốn luật thuần: mốc chưa đọc · bot có được trả lời khi khách bị chặn · tin nào sửa được |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` | 2 cột mới trên `chat_contacts`/`chat_messages` + 1 bảng `chat_conversation_follows` |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs` | Truy vấn cho cả bốn việc + lọc danh sách |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | 7 route mới + `OwnedPaths` |
| `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs` | Bot câm khi khách bị chặn |
| `TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs` | Chặn đường gửi tới khách bị chặn |
| `wwwroot/pages/chat-inbox.jsx` · `wwwroot/styles.css` | Nút + trạng thái + chữ cảnh báo phạm vi |
| `TourkitAiProxy.Tests/Chat/InboxActionTests.cs` (mới) | Test cho bốn luật thuần |
| `TourkitAiProxy.Tests/Chat/InboxActionRouteTests.cs` (mới) | Canh route nằm trong `OwnedPaths` + canh câu SQL |

---

## Task 1: Đánh dấu chưa đọc

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Modify: `wwwroot/pages/chat-inbox.jsx`
- Test: `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`

**Interfaces:**
- Produces: `ChatRules.MocChuaDoc(DateTime tinCuoiCuaKhachUtc) -> DateTime` · `ChatRepository.MarkUnreadAsync(string tenant, long id, string username, CancellationToken ct) -> Task<bool>` · route `POST /api/v1/chat/conversations/{id}/unread`

**Bối cảnh cho người làm:** đã đọc được lưu **theo từng người** ở bảng `chat_conversation_reads(tenant_id, conversation_id, username, last_read_at)`. Danh sách tính "chưa đọc" bằng `contact_replied_at > COALESCE(r.last_read_at, v.agent_last_read_at)`. Vì thế **XOÁ dòng đọc là sai**: nó lùi về cột chung `agent_last_read_at` vốn có thể vẫn mới, và hội thoại vẫn hiện là đã đọc. Phải **ĐẶT** mốc về trước tin cuối của khách.

> **Hiệu chỉnh bắt buộc khi thực thi:** Mọi đường repository/endpoint của action này phải luôn kiểm đồng thời `tenant` và `conversationId` trước khi đổi trạng thái, rồi ghi `chat_audit` sau khi thành công. Giao diện phải xử lý cả HTTP lỗi, JSON lỗi và `ok=false`; không được giả định `r.json()` luôn thành công hoặc báo thành công khi thao tác thất bại.

- [ ] **Step 1: Viết test thất bại cho luật mốc chưa đọc**

Tạo `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`:

```csharp
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class InboxActionTests
{
    [Fact]
    public void Moc_chua_doc_phai_lui_ve_TRUOC_tin_cuoi_cua_khach()
    {
        // Danh sách tính chưa đọc bằng phép so LỚN HƠN THỰC SỰ:
        //   contact_replied_at > last_read_at
        // Nên đặt mốc BẰNG đúng thời điểm tin cuối là hội thoại vẫn hiện "đã đọc" — bấm nút mà
        // không có gì xảy ra, và không ai đoán ra tại sao.
        var tinCuoi = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var moc = ChatRules.MocChuaDoc(tinCuoi);
        Assert.True(moc < tinCuoi);
    }

    [Fact]
    public void Moc_chua_doc_khong_lui_qua_xa()
    {
        // Lùi cả phút thì những tin của khách gửi TRƯỚC đó vài giây cũng thành chưa đọc theo —
        // đúng thì đúng nhưng người dùng chỉ định đánh dấu một hội thoại, không phải cả cụm.
        var tinCuoi = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var moc = ChatRules.MocChuaDoc(tinCuoi);
        Assert.True(tinCuoi - moc <= System.TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Moc_chua_doc_giu_nguyen_Kind_Utc()
    {
        // Kind=Unspecified lọt xuống Dapper là lệch +7h khi đọc lại — xem docs/datetime-convention.md.
        var moc = ChatRules.MocChuaDoc(new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DateTimeKind.Utc, moc.Kind);
    }
}
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: FAIL — `ChatRules` không có `MocChuaDoc`

- [ ] **Step 3: Thêm luật vào `ChatRules.cs`**

Đặt ngay dưới `BotMayReply`:

```csharp
    /// <summary>
    /// Mốc "đã đọc" cần đặt lại thành gì để hội thoại hiện CHƯA ĐỌC.
    ///
    /// <para>Danh sách hội thoại tính chưa đọc bằng phép so lớn hơn THỰC SỰ
    /// (<c>contact_replied_at &gt; last_read_at</c>), nên đặt mốc bằng đúng thời điểm tin cuối là
    /// không đủ — bấm nút mà không có gì xảy ra. Lùi một phần nghìn giây là vừa: đủ để vượt phép
    /// so, mà không kéo theo những tin khách gửi trước đó thành chưa đọc oan.</para>
    /// </summary>
    public static DateTime MocChuaDoc(DateTime tinCuoiCuaKhachUtc)
        => DateTime.SpecifyKind(tinCuoiCuaKhachUtc.AddMilliseconds(-1), DateTimeKind.Utc);
```

- [ ] **Step 4: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: PASS (3 test)

- [ ] **Step 5: Thêm truy vấn vào `ChatRepository.cs`**

Đặt ngay dưới `MarkReadAsync`:

```csharp
    /// <summary>
    /// Đánh dấu hội thoại CHƯA đọc, cho riêng người đang thao tác.
    ///
    /// <para><b>ĐẶT mốc chứ không XOÁ dòng.</b> Xoá thì phép tính chưa đọc lùi về cột chung
    /// <c>agent_last_read_at</c> — vốn có thể vẫn mới vì người khác vừa mở — và hội thoại vẫn
    /// hiện là đã đọc. Người dùng bấm nút, không thấy gì đổi, và không có lỗi nào để lần ra.</para>
    ///
    /// <para>Trả <c>false</c> khi hội thoại chưa có tin nào của khách: lúc đó không có gì để đánh
    /// dấu chưa đọc, và tự nghĩ ra một mốc là nói dối dữ liệu.</para>
    /// </summary>
    public async Task<bool> MarkUnreadAsync(string tenant, long id, string username,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var soDong = await c.ExecuteAsync("""
            INSERT INTO chat_conversation_reads (tenant_id, conversation_id, username, last_read_at)
            SELECT @tenant, @id, @username, m.created_utc - interval '1 millisecond'
              FROM chat_messages m
             WHERE m.conversation_id = @id AND m.tenant_id = @tenant AND m.direction = 0
             ORDER BY m.created_utc DESC
             LIMIT 1
            ON CONFLICT (tenant_id, conversation_id, username)
            DO UPDATE SET last_read_at = EXCLUDED.last_read_at
            """, new { tenant, id, username });
        return soDong > 0;
    }
```

- [ ] **Step 6: Thêm route vào `ChatInboxEndpoints.cs`**

Đặt ngay dưới route `POST /conversations/{id}/read`:

```csharp
        // Đánh dấu CHƯA đọc — trả lại hội thoại cho chính mình hoặc cho người khác nhặt.
        //
        // ⚠️ KHÔNG gọi MarkSeenAsync như đường /read: không nền tảng nào cho "bỏ đã xem". Báo
        // sang kênh một lần nữa còn tệ hơn — khách nhận thêm một tín hiệu đã xem cho tin cũ.
        g.MapPost("/conversations/{id:long}/unread", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            var duoc = await repo.MarkUnreadAsync(a.TenantId, id, a.Username, ct);
            if (duoc)
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "danh-dau-chua-doc", null, ct);

            // ok=false nghĩa là hội thoại chưa có tin nào của khách — giao diện nói rõ thay vì im.
            return Results.Json(new { ok = duoc }, Web);
        });
```

- [ ] **Step 7: Thêm đường vào `OwnedPaths`**

Route nằm dưới tiền tố `/api/v1/chat/conversations` đã có sẵn trong `OwnedPaths` → **không phải sửa gì**. Chạy test để xác nhận:

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "ChatFeatureFlagCoverageTests"`
Expected: PASS

- [ ] **Step 8: Thêm nút vào giao diện**

Trong `wwwroot/pages/chat-inbox.jsx`, thêm vào cụm nút thao tác của hội thoại (cạnh nút giao việc):

```jsx
{/* Đánh dấu chưa đọc — trả hội thoại về hàng chờ của chính mình. */}
<button className="ci-nut-nho" title="Đánh dấu chưa đọc"
  onClick={async () => {
    const r = await authedFetch(`/api/v1/chat/conversations/${hoiThoai.id}/unread`, { method: 'POST' });
    const j = await r.json();
    if (!j.ok) { bao('Hội thoại chưa có tin nào của khách nên không đánh dấu được'); return; }
    taiLaiDanhSach();
  }}>Chưa đọc</button>
```

- [ ] **Step 9: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS, không có test nào đỏ

- [ ] **Step 10: Commit**

```bash
git add TourkitAiProxy.Domain/Chat/ChatRules.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/InboxActionTests.cs wwwroot/pages/chat-inbox.jsx
git commit -m "feat(hộp thư chat): đánh dấu chưa đọc theo từng người"
```

---

## Task 2: Theo dõi hội thoại

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Modify: `wwwroot/pages/chat-inbox.jsx`
- Test: `TourkitAiProxy.Tests/Chat/InboxActionRouteTests.cs`

**Interfaces:**
- Consumes: không có gì từ Task 1
- Produces: `ChatRepository.SetFollowAsync(string tenant, long id, string username, bool theoDoi, CancellationToken ct) -> Task` · route `POST|DELETE /api/v1/chat/conversations/{id}/follow` · tham số lọc `followed=true` cho `GET /conversations`

**Bối cảnh:** giao việc (`assigned_username`) là **sở hữu** — một người một hội thoại. Theo dõi là **quan tâm** — nhiều người, không giành việc của ai. Hai khái niệm khác nhau, đừng gộp.

> **Hiệu chỉnh bắt buộc khi thực thi:** Thêm `bool Followed` vào `ChatConversation`; SQL danh sách phải chọn cờ này và `Shape` phải trả nó cho UI. `GET /conversations?followed=true` phải parse đúng `followed=true` và truyền `chiTheoDoi` vào repository. `DELETE /follow` phải xác nhận hội thoại thuộc `tenant` đang gọi trước khi xoá/theo audit. UI phải có bộ lọc **Tôi theo dõi**, nút theo dõi/bỏ theo dõi và xử lý lỗi HTTP/JSON thay vì tải lại giả định thành công.

- [ ] **Step 1: Viết test thất bại canh bảng mới có trong schema**

Thêm vào `TourkitAiProxy.Tests/Chat/InboxActionRouteTests.cs` (tạo mới):

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class InboxActionRouteTests
{
    [Fact]
    public void Bang_theo_doi_phai_khoa_theo_TUNG_NGUOI()
    {
        // Theo dõi là chuyện của từng người, không phải của cả công ty. Thiếu username trong khoá
        // chính thì A bỏ theo dõi là B mất theo dõi theo — hỏng im lặng, giống hệt lỗi cột
        // agent_last_read_at dùng chung trước đây.
        var sql = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        var m = Regex.Match(sql,
            @"CREATE TABLE IF NOT EXISTS chat_conversation_follows[\s\S]*?PRIMARY KEY \(([^)]*)\)");
        Assert.True(m.Success, "Không thấy bảng chat_conversation_follows");

        var cot = m.Groups[1].Value;
        Assert.Contains("tenant_id", cot);
        Assert.Contains("conversation_id", cot);
        Assert.Contains("username", cot);
    }
}
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionRouteTests"`
Expected: FAIL — "Không thấy bảng chat_conversation_follows"

- [ ] **Step 3: Thêm bảng vào `ChatDb.cs`**

Đặt ngay dưới bảng `chat_conversation_reads`:

```sql
    -- THEO DÕI một hội thoại. Khác hẳn giao việc: giao việc là SỞ HỮU (một người một hội thoại,
    -- ai nhận thì người khác thôi), theo dõi là QUAN TÂM (nhiều người cùng theo dõi được, và
    -- không giành việc của ai). Quản lý muốn ngó một ca khó mà không cướp việc của nhân viên thì
    -- đây là đường duy nhất.
    --
    -- Khoá gồm username vì đây là chuyện của TỪNG NGƯỜI — thiếu nó thì A bỏ theo dõi là B mất
    -- theo dõi theo, đúng kiểu hỏng im lặng của cột agent_last_read_at dùng chung ngày trước.
    CREATE TABLE IF NOT EXISTS chat_conversation_follows (
      tenant_id       text        NOT NULL,
      conversation_id bigint      NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
      username        text        NOT NULL,
      created_utc     timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, conversation_id, username)
    );
    -- Lọc "hội thoại tôi theo dõi" đi bằng chỉ mục này; không có nó thì mỗi lần mở bộ lọc là quét
    -- cả bảng, mà bảng này chỉ phình theo thời gian.
    CREATE INDEX IF NOT EXISTS ix_follow_nguoi
      ON chat_conversation_follows (tenant_id, username);
```

- [ ] **Step 4: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionRouteTests"`
Expected: PASS

- [ ] **Step 5: Thêm truy vấn vào `ChatRepository.cs`**

Đặt ngay dưới `MarkUnreadAsync`:

```csharp
    /// <summary>Bật/tắt theo dõi một hội thoại cho riêng một người.</summary>
    public async Task SetFollowAsync(string tenant, long id, string username, bool theoDoi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (theoDoi)
            await c.ExecuteAsync("""
                INSERT INTO chat_conversation_follows (tenant_id, conversation_id, username)
                VALUES (@tenant, @id, @username)
                ON CONFLICT (tenant_id, conversation_id, username) DO NOTHING
                """, new { tenant, id, username });
        else
            await c.ExecuteAsync("""
                DELETE FROM chat_conversation_follows
                 WHERE tenant_id = @tenant AND conversation_id = @id AND username = @username
                """, new { tenant, id, username });
    }
```

- [ ] **Step 6: Cho danh sách lọc được theo "tôi theo dõi"**

Trong `ListConversationsAsync`, thêm tham số `bool chiTheoDoi = false` và nối vào câu SQL — đặt ngay sau mệnh đề `@giaoCho`:

```sql
              AND (NOT @chiTheoDoi OR EXISTS (
                    SELECT 1 FROM chat_conversation_follows f
                     WHERE f.tenant_id = v.tenant_id AND f.conversation_id = v.id
                       AND f.username = @nguoiDung))
```

Và trả kèm cờ cho từng dòng — thêm vào danh sách `SELECT`:

```sql
                   EXISTS (SELECT 1 FROM chat_conversation_follows f2
                            WHERE f2.tenant_id = v.tenant_id AND f2.conversation_id = v.id
                              AND f2.username = @nguoiDung) AS followed
```

- [ ] **Step 7: Thêm hai route vào `ChatInboxEndpoints.cs`**

```csharp
        // Theo dõi / bỏ theo dõi. KHÔNG đụng assigned_username — theo dõi không phải nhận việc.
        g.MapPost("/conversations/{id:long}/follow", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetFollowAsync(a.TenantId, id, a.Username, true, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "theo-doi", null, ct);
            return Results.Json(new { ok = true, followed = true }, Web);
        });

        g.MapDelete("/conversations/{id:long}/follow", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            await repo.SetFollowAsync(a.TenantId, id, a.Username, false, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "bo-theo-doi", null, ct);
            return Results.Json(new { ok = true, followed = false }, Web);
        });
```

- [ ] **Step 8: Thêm nút + bộ lọc vào giao diện**

Trong `wwwroot/pages/chat-inbox.jsx`, cạnh nút "Chưa đọc" của Task 1:

```jsx
{/* Theo dõi — quan tâm mà không nhận việc. Khác hẳn nút giao việc bên cạnh. */}
<button className={'ci-nut-nho' + (hoiThoai.followed ? ' dang-bat' : '')}
  title={hoiThoai.followed ? 'Bỏ theo dõi' : 'Theo dõi hội thoại này'}
  onClick={async () => {
    await authedFetch(`/api/v1/chat/conversations/${hoiThoai.id}/follow`,
      { method: hoiThoai.followed ? 'DELETE' : 'POST' });
    taiLaiDanhSach();
  }}>{hoiThoai.followed ? '★ Đang theo dõi' : '☆ Theo dõi'}</button>
```

Và thêm một mục vào dải bộ lọc sẵn có (cạnh "Chưa đọc" / "Của tôi"): `Tôi theo dõi` → gọi `GET /conversations?followed=true`.

- [ ] **Step 9: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS

- [ ] **Step 10: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/InboxActionRouteTests.cs wwwroot/pages/chat-inbox.jsx
git commit -m "feat(hộp thư chat): theo dõi hội thoại mà không cần nhận việc"
```

---

## Task 3: Chặn khách

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Modify: `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs`
- Modify: `TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Modify: `wwwroot/pages/chat-inbox.jsx`
- Test: `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`

**Interfaces:**
- Consumes: không có gì từ Task 1–2
- Produces: `ChatRules.BotMayReply(ChatConversation, DateTime, bool khachBiChan)` (thêm tham số thứ ba, mặc định `false`) · `ChatRepository.SetContactBlockedAsync(...)` · `ChatRepository.ContactBlockedAsync(...) -> Task<bool>` · route `POST|DELETE /api/v1/chat/conversations/{id}/block`

**Bối cảnh — ĐỌC TRƯỚC KHI VIẾT:** không nền tảng nào cho phía doanh nghiệp chặn một khách qua API. Việc này **hoàn toàn nội bộ**: hộp thư ẩn khách đó, bot câm, đường gửi từ chối. **Tuyệt đối không đặt tên nút là "Báo xấu"** — người dùng sẽ tưởng đã báo lên Facebook. Tin của khách **vẫn được ghi lại** làm bằng chứng; chặn không phải là xoá.

> **Hiệu chỉnh bắt buộc khi thực thi:** Bổ sung trạng thái/cờ `blocked` vào model, truy vấn danh sách và `Shape`. Mặc định `GET /conversations` loại khách bị chặn; `blocked=true` chỉ trả khách bị chặn. UI có bộ lọc **Đã chặn** và thao tác **Bỏ chặn**. Inbound vẫn phải lưu tin trước khi dừng bot. Nếu outbox gặp khách bị chặn, worker phải kết thúc hàng đợi, đặt `chat_messages.state = Failed` với lý do chặn và publish state event trong cùng luồng xử lý; tuyệt đối không để row pending. Các thao tác đổi trạng thái vẫn audit, không ghi nội dung tin.

- [ ] **Step 1: Viết test thất bại cho luật bot câm**

Thêm vào `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`:

```csharp
    [Fact]
    public void Khach_bi_chan_thi_bot_KHONG_duoc_tra_loi()
    {
        var hoiThoai = new ChatConversation { Status = 0, BotResumeAt = null };
        var bayGio = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(ChatRules.BotMayReply(hoiThoai, bayGio, khachBiChan: false));
        Assert.False(ChatRules.BotMayReply(hoiThoai, bayGio, khachBiChan: true));
    }

    [Fact]
    public void Chan_khach_khong_lam_hong_cac_luat_cu()
    {
        // Tham số mới phải có mặc định, nếu không mọi chỗ gọi cũ đứt — và quan trọng hơn: hành vi
        // khi không truyền gì phải GIỐNG HỆT trước.
        var daDong = new ChatConversation { Status = (short)ChatStatus.Closed };
        Assert.False(ChatRules.BotMayReply(daDong, DateTime.UtcNow));
    }
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: FAIL — `BotMayReply` không nhận 3 tham số

- [ ] **Step 3: Sửa luật trong `ChatRules.cs`**

```csharp
    /// <param name="khachBiChan">
    /// Khách đã bị công ty chặn. Chặn là chuyện NỘI BỘ — không nền tảng nào cho doanh nghiệp chặn
    /// một người qua API — nên tin của họ vẫn tới và vẫn được ghi làm bằng chứng; chỉ có bot là
    /// phải câm. Bot trả lời một người đã bị chặn là mâu thuẫn ngay trước mắt khách.
    /// </param>
    public static bool BotMayReply(ChatConversation hoiThoai, DateTime nowUtc, bool khachBiChan = false)
    {
        if (khachBiChan) return false;
        if (hoiThoai.Status == (short)ChatStatus.Closed) return false;
        if (hoiThoai.BotResumeAt is { } moc && moc > nowUtc) return false;
        return true;
    }
```

- [ ] **Step 4: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: PASS

- [ ] **Step 5: Thêm hai cột vào `ChatDb.cs`**

Đặt ngay dưới cột `avatar_state` của `chat_contacts`:

```sql
    -- CHẶN khách. Hoàn toàn NỘI BỘ: không nền tảng nào cho phía doanh nghiệp chặn một người qua
    -- API. Chặn ở đây nghĩa là hộp thư ẩn họ đi, bot câm, và đường gửi từ chối — chứ KHÔNG phải
    -- báo lên Facebook/Zalo. Đặt tên nút trên giao diện cho đúng chuyện đó.
    --
    -- Tin của khách bị chặn VẪN ĐƯỢC GHI: chặn không phải xoá, và khi cần đối chất thì đó là bằng
    -- chứng duy nhất còn lại.
    ALTER TABLE chat_contacts ADD COLUMN IF NOT EXISTS blocked_utc timestamptz;
    ALTER TABLE chat_contacts ADD COLUMN IF NOT EXISTS blocked_by  text;
```

- [ ] **Step 6: Thêm truy vấn vào `ChatRepository.cs`**

```csharp
    /// <summary>Chặn / bỏ chặn một khách. Xem ghi chú ở <c>ChatDb</c> về phạm vi nội bộ.</summary>
    public async Task SetContactBlockedAsync(string tenant, ChatChannel kenh, string externalId,
        bool chan, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_contacts
               SET blocked_utc = CASE WHEN @chan THEN now() ELSE NULL END,
                   blocked_by  = CASE WHEN @chan THEN @username ELSE NULL END,
                   updated_utc = now()
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
            """, new { tenant, kenh = (short)kenh, id = externalId, chan, username });
    }

    /// <summary>Khách này có đang bị chặn không.</summary>
    public async Task<bool> ContactBlockedAsync(string tenant, ChatChannel kenh, string externalId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>("""
            SELECT COALESCE(blocked_utc IS NOT NULL, false) FROM chat_contacts
             WHERE tenant_id = @tenant AND channel = @kenh AND external_id = @id
            """, new { tenant, kenh = (short)kenh, id = externalId });
    }
```

- [ ] **Step 7: Cho bot câm trong `ChatInboundService.cs`**

Trong `OneEventAsync`, tìm dòng `if (moi is null || !ChatRules.BotMayReply(moi, DateTime.UtcNow)) return;` và đổi thành:

```csharp
        // Khách bị chặn thì bot câm. Đọc ở ĐÂY chứ không ở đầu hàm: tin của khách vẫn phải được
        // ghi lại làm bằng chứng — chặn không phải xoá.
        var biChan = await _repo.ContactBlockedAsync(tenantId, e.Channel, e.ExternalUserId, ct);
        var moi = await _repo.GetConversationAsync(tenantId, hoiThoai.Id, ct);
        if (moi is null || !ChatRules.BotMayReply(moi, DateTime.UtcNow, biChan)) return;
```

- [ ] **Step 8: Thêm hai route vào `ChatInboxEndpoints.cs`**

```csharp
        // Chặn / bỏ chặn khách. CHỈ trong hộp thư của mình — xem ghi chú ở ChatDb.
        g.MapPost("/conversations/{id:long}/block", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is not { } v) return Results.NotFound();

            await repo.SetContactBlockedAsync(a.TenantId, (ChatChannel)v.Channel,
                v.ContactExternalId, true, a.Username, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "chan-khach", null, ct);
            return Results.Json(new { ok = true, blocked = true }, Web);
        });

        g.MapDelete("/conversations/{id:long}/block", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is not { } v) return Results.NotFound();

            await repo.SetContactBlockedAsync(a.TenantId, (ChatChannel)v.Channel,
                v.ContactExternalId, false, a.Username, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "bo-chan-khach", null, ct);
            return Results.Json(new { ok = true, blocked = false }, Web);
        });
```

- [ ] **Step 9: Chặn đường GỬI trong `ChatOutboxWorker.cs`**

Trong `OneRowAsync`, trước khi gọi adapter gửi, thêm:

```csharp
        // Chặn ở đường gửi nữa, không chỉ ở bot: người trực vẫn có thể mở hội thoại cũ ra gõ.
        // Hoàn tất hàng đợi, đánh dấu tin Failed có lý do và phát state event; không để pending.
        if (await repo.ContactBlockedAsync(r.TenantId, kenh, r.ContactExternalId, ct))
        {
            await repo.FinishOutboxAsync(r.Id, false, false, "Khách đang bị chặn trong hộp thư", ct);
            await repo.MarkMessageFailedAsync(r.TenantId, r.ConversationId, r.MessageId,
                "Khách đang bị chặn trong hộp thư", ct);
            bus.Publish(new(r.TenantId, r.ConversationId, "doi-trang-thai", r.MessageId));
            return;
        }
```

- [ ] **Step 10: Thêm nút vào giao diện — CHỮ PHẢI ĐÚNG PHẠM VI**

```jsx
{/* ⚠️ "Chặn trong hộp thư", KHÔNG phải "Báo xấu": không nền tảng nào cho báo xấu qua API,
    gọi tên sai là người dùng tưởng đã báo lên Facebook. */}
<button className="ci-nut-nho nguy-hiem"
  title="Ẩn khách này khỏi hộp thư và ngừng trả lời. Không báo gì cho nền tảng."
  onClick={async () => {
    if (!confirm('Chặn khách này trong hộp thư?\n\nHộp thư sẽ ẩn họ và trợ lý ngừng trả lời. '
      + 'Việc này KHÔNG báo cho Facebook/Zalo và khách vẫn nhắn tới được.')) return;
    await authedFetch(`/api/v1/chat/conversations/${hoiThoai.id}/block`, { method: 'POST' });
    taiLaiDanhSach();
  }}>Chặn trong hộp thư</button>
```

- [ ] **Step 11: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS

- [ ] **Step 12: Commit**

```bash
git add TourkitAiProxy.Domain/Chat/ChatRules.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/InboxActionTests.cs wwwroot/pages/chat-inbox.jsx
git commit -m "feat(hộp thư chat): chặn khách trong hộp thư"
```

---

## Task 4: Xoá tin và sửa tin — chỉ trong hộp thư mình

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs`
- Modify: `TourkitAiProxy.Domain/Chat/ChatModels.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Modify: `wwwroot/pages/chat-inbox.jsx`
- Test: `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`

**Interfaces:**
- Consumes: không có gì từ Task 1–3
- Produces: `ChatRules.CoTheSuaTin(short state) -> bool` · `ChatRepository.SoftDeleteMessageAsync(...)` · `ChatRepository.EditPendingMessageAsync(...)` · route `DELETE|PATCH /api/v1/chat/conversations/{id}/messages/{msgId}`

**Bối cảnh — hai chỗ cố ý khác ChatbotX, ghi lý do vào `docs/features/chat-inbox.md` theo luật D5:**

1. **Xoá là XOÁ MỀM, và chỉ trong hộp thư mình.** Đã kiểm mã ChatbotX: `delete-message.action.ts` gọi `repository.deleteById` — thao tác CSDL thuần, không chạm nền tảng. Meta không cấp API thu hồi cho doanh nghiệp. Nên chữ trên giao diện **phải nói rõ khách vẫn thấy**, nếu không nhân viên tưởng đã thu hồi được câu lỡ tay và không đi xin lỗi khách.
2. **Sửa pending chỉ cho tin CHƯA GỬI ĐI và chưa bị worker claim.** Tin hỏng dùng thao tác riêng **Sửa và gửi lại** để requeue/reset trạng thái. ChatbotX cho sửa mọi tin, nhưng sửa một tin đã gửi thì hộp thư nói dối về thứ khách thật sự nhận được — tệ hơn xoá.

> **Hiệu chỉnh bắt buộc khi thực thi:** Thêm `ChatMessage.DeletedUtc` và `GetMessageAsync(string tenant, long conversationId, long messageId, CancellationToken ct)`. Toàn bộ SQL và route xoá/sửa phải ràng buộc đủ `tenant + conversationId + messageId`. Xoá cục bộ phải từ chối tin `Pending`; UI không bao giờ hiện nút xoá cho pending. Chỉ cho sửa pending khi outbox còn `status = 0` và chưa bị claim. Tin failed dùng luồng riêng, nhãn **Sửa và gửi lại**, sửa xong phải requeue/reset trạng thái; không gọi đó là sửa pending. Audit chỉ ghi metadata/ID, tuyệt đối không ghi nội dung tin.

- [ ] **Step 1: Viết test thất bại cho luật sửa tin**

Thêm vào `TourkitAiProxy.Tests/Chat/InboxActionTests.cs`:

```csharp
    [Theory]
    [InlineData(ChatState.Pending, true)]   // chờ gửi — sửa được, đây là ca hữu ích nhất
    [InlineData(ChatState.Failed,  false)]  // gửi hỏng dùng luồng Sửa và gửi lại riêng
    [InlineData(ChatState.Sent,      false)]
    [InlineData(ChatState.Delivered, false)]
    [InlineData(ChatState.Seen,      false)]
    public void Chi_sua_duoc_tin_CHUA_ra_khoi_may(ChatState tt, bool mong)
    {
        // Tin đã đi rồi thì khách đã thấy bản gốc VĨNH VIỄN — không nền tảng nào cho sửa lại phía
        // họ. Sửa bản của mình lúc đó là làm hộp thư nói dối về thứ khách thật sự nhận được, và
        // đó là kiểu sai không ai phát hiện ra cho tới lúc đối chất với khách.
        Assert.Equal(mong, ChatRules.CoTheSuaTin((short)tt));
    }
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: FAIL — `ChatRules` không có `CoTheSuaTin`

- [ ] **Step 3: Thêm luật vào `ChatRules.cs`**

```csharp
    /// <summary>
    /// Tin này còn sửa được không.
    ///
    /// <para><b>Chỉ tin CHƯA ra khỏi máy.</b> Tin đã gửi thì khách đã thấy bản gốc vĩnh viễn —
    /// Meta không cấp API sửa cho doanh nghiệp — nên sửa bản của mình là làm hộp thư nói dối về
    /// thứ khách thật sự nhận được. Đây là chỗ CỐ Ý làm khác ChatbotX (bên đó cho sửa mọi tin).</para>
    /// </summary>
    public static bool CoTheSuaTin(short state)
        => state == (short)ChatState.Pending;
```

- [ ] **Step 4: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "InboxActionTests"`
Expected: PASS (5 ca)

- [ ] **Step 5: Thêm truy vấn vào `ChatRepository.cs`**

```csharp
    /// <summary>
    /// Xoá MỀM một tin khỏi hộp thư của mình. Cột <c>deleted_utc</c> đã có sẵn (trước nay chỉ
    /// dùng cho bình luận khách tự xoá) — dùng lại, đừng thêm cột thứ hai cùng nghĩa.
    ///
    /// <para>⚠️ Chỉ xoá ở PHÍA MÌNH. Không nền tảng nào cho doanh nghiệp thu hồi tin đã gửi.</para>
    /// </summary>
    public async Task<bool> SoftDeleteMessageAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages SET deleted_utc = now()
             WHERE id = @id AND tenant_id = @tenant AND conversation_id = @conversationId
               AND state <> 0 AND deleted_utc IS NULL
            """, new { id = messageId, tenant, conversationId }) > 0;
    }

    /// <summary>
    /// Sửa nội dung một tin CHƯA gửi đi. Điều kiện trạng thái kiểm ngay trong câu lệnh chứ không
    /// chỉ ở tầng trên: worker gửi có thể vừa nhặt tin đó lên giữa chừng.
    /// </summary>
    public async Task<bool> EditPendingMessageAsync(string tenant, long conversationId, long messageId, string body,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages m SET body = @body
             WHERE m.id = @id AND m.tenant_id = @tenant AND m.conversation_id = @conversationId
               AND m.state = 0 AND m.deleted_utc IS NULL
               AND EXISTS (SELECT 1 FROM chat_outbox o WHERE o.message_id = m.id AND o.status = 0)
            """, new { id = messageId, tenant, conversationId, body }) > 0;
    }
```

- [ ] **Step 6: Thêm hai route vào `ChatInboxEndpoints.cs`**

```csharp
        // Xoá tin — CHỈ trong hộp thư mình. Giao diện phải nói rõ chuyện đó.
        g.MapDelete("/conversations/{id:long}/messages/{msgId:long}", async (long id, long msgId,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, ChatEventBus bus,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            if (!await repo.SoftDeleteMessageAsync(a.TenantId, id, msgId, ct)) return Results.NotFound();
            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "xoa-tin",
                new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
            return Results.Json(new { ok = true }, Web);
        });

        // Sửa tin — chỉ tin CHƯA gửi đi. Xem ChatRules.CoTheSuaTin.
        g.MapPatch("/conversations/{id:long}/messages/{msgId:long}", async (long id, long msgId,
            EditMsgReq body, HttpContext ctx, TkSessionStore sessions, ChatRepository repo,
            ChatEventBus bus, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.Json(new { error = "Nội dung không được để trống" }, Web, statusCode: 400);
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            if (!await repo.EditPendingMessageAsync(a.TenantId, id, msgId, body.Body.Trim(), ct))
                return Results.Json(new { error = "Tin đã gửi đi rồi nên không sửa được nữa" },
                    Web, statusCode: 409);

            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "sua-tin",
                new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
            return Results.Json(new { ok = true }, Web);
        });
```

Và khai bản ghi yêu cầu cạnh các `*Req` sẵn có trong cùng file:

```csharp
    private record EditMsgReq(string Body);
```

- [ ] **Step 7: Giao diện — chữ phải nói đúng phạm vi**

```jsx
{/* ⚠️ Câu xác nhận PHẢI nói khách vẫn thấy. Không nói thì nhân viên tưởng đã thu hồi được
    câu lỡ tay và không đi xin lỗi khách — đó là hậu quả thật, không phải chuyện chữ nghĩa. */}
<button className="ci-nut-nho" onClick={async () => {
  if (!confirm('Xoá tin này khỏi hộp thư?\n\nChỉ xoá ở phía bạn — KHÁCH VẪN THẤY tin này. '
    + 'Các nền tảng không cho phép thu hồi tin đã gửi.')) return;
  await authedFetch(`/api/v1/chat/conversations/${hoiThoai.id}/messages/${tin.id}`,
    { method: 'DELETE' });
  taiLaiTin();
}}>Xoá</button>
```

Nút **Sửa** chỉ hiện cho `tin.state === 0` khi hàng đợi còn pending/chưa claim. Tin `state === 4` hiện nút riêng **Sửa và gửi lại**; nút này requeue và reset trạng thái. Các trạng thái khác **ẩn hẳn nút**, đừng hiện rồi báo lỗi. Nút **Xoá** không bao giờ hiện cho `tin.state === 0`.

- [ ] **Step 8: Tin đã xoá thì hiện "đã bị xoá", không biến mất**

Trong phần vẽ dòng tin của `chat-inbox.jsx`, nếu `tin.deletedUtc` thì vẽ chữ nhạt *"Tin đã bị xoá khỏi hộp thư"* thay cho nội dung — giống hệt cách đang làm với bình luận khách tự xoá. Biến mất hẳn thì người trực tưởng mình nhớ nhầm.

- [ ] **Step 9: Ghi lý do làm khác ChatbotX vào tài liệu**

Thêm vào `docs/features/chat-inbox.md`, mục "Chỗ cố ý khác ChatbotX": hai gạch đầu dòng đã nêu ở phần **Bối cảnh** của task này.

- [ ] **Step 10: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS

- [ ] **Step 11: Cập nhật CHANGELOG + commit**

Thêm vào mục ngày phát hành trong `CHANGELOG.md`, dưới `### ✨ Tính năng mới`:

```markdown
- **Đánh dấu chưa đọc, theo dõi, chặn khách, xoá tin.** Bốn nút mới trên mỗi hội thoại: trả lại
  dấu chưa đọc khi bạn lỡ mở nhầm; theo dõi một ca khó mà không phải nhận việc (nhiều người cùng
  theo dõi được); chặn khách quấy rối để hộp thư ẩn họ và trợ lý ngừng trả lời; xoá một tin khỏi
  hộp thư.

  *Lưu ý:* chặn và xoá **chỉ có tác dụng trong hộp thư của bạn** — Facebook, Zalo và các nơi khác
  không cho phép chặn hay thu hồi từ phía doanh nghiệp, nên khách vẫn nhắn tới được và vẫn thấy
  tin cũ. Màn hình nói rõ điều này trước khi bạn bấm.
```

```bash
git add -A
git commit -m "feat(hộp thư chat): xoá tin và sửa tin chưa gửi"
```

---

## Đợt sau — mỗi đợt một kế hoạch riêng

Bốn việc trên gom được vào một đợt vì cùng hình dạng và không chạm nền tảng. Những nhóm dưới đây **không** như vậy, nên mỗi nhóm cần một kế hoạch riêng, viết khi tới lượt:

| Đợt | Phạm vi | Vì sao tách |
|---|---|---|
| 2 | **Trả lời / ẩn / xoá bình luận** (`sendComment`, `hideComment`, `deleteComment`) | Chạm Graph API, và phải sửa `ChatOutboxWorker` để phân biệt `surface`. Đây là **tính năng đang dở dang** — bình luận vào được hộp thư mà trả lời thì đi ra đường tin nhắn riêng. Ưu tiên cao nhất sau đợt 1. |
| 3 | **Danh bạ**: tạo khách tay · xoá khách · xuất/nhập · trường thông tin có cấu trúc · quản lý nhãn toàn công ty | Bảng mới, màn hình mới, và xoá khách vướng nghĩa vụ pháp lý — cần bàn trước khi làm. |
| 4 | **Cấu hình kênh**: menu cố định · nút "Đăng ký lại nhận tin" · persona · quản lý mẫu tin | Chạm Meta Profile API. Nút đăng ký lại giải trực tiếp việc Trang nối trước 28/08 thiếu `message_reactions`. |
| 5 | **Còn lại**: băng chuyền · GIF · tải tệp dùng lại · `inbox_labels` · `messaging_policy_enforcement` · nút bấm TikTok | Rời rạc, mỗi cái một kênh, không phụ thuộc nhau. |

**Ngoài phạm vi vĩnh viễn** (ChatbotX không có, theo ràng buộc của đợt này): báo xấu lên nền tảng · chuyển tiếp tin xuyên hội thoại · xoá hội thoại.

**Thu hồi tin đã tách thành kế hoạch riêng** — [2026-08-28-thu-hoi-tin.md](2026-08-28-thu-hoi-tin.md). Ban đầu xếp ngoài phạm vi vì ChatbotX không có, nhưng người dùng nêu đúng một ca thật: *gõ sai gửi nhầm mà không rút lại được thì chết*. Ca đó giải được — chỉ là không bằng cách "thu hồi", xem kế hoạch đó.
