# Thu hồi tin nhắn Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Người trực gõ sai hoặc gửi nhầm hội thoại thì rút lại được — thật sự rút lại, không phải chỉ giấu đi trong hộp thư của mình.

**Architecture:** Thu hồi SAU khi tin đã rời máy chủ là bất khả thi trên Meta (Messenger · Instagram · WhatsApp) — họ không cấp API thu hồi cho phía doanh nghiệp; thu hồi là tính năng của ứng dụng người dùng, không phải của nền tảng. Nên kế hoạch này **không đi tìm đường thu hồi**, mà **tạo ra một khoảng thời gian trước khi tin rời máy chủ**: tin của người thật nằm chờ vài giây trong hàng đợi, trong lúc đó nút "Thu hồi" xoá nó khỏi hàng đợi — tin **chưa bao giờ được gửi**, nên rút lại là tuyệt đối, ở mọi kênh. Đây đúng là cách Gmail làm ("Hoàn tác gửi"), và nó **chắc chắn hơn** thu hồi thật vì không phụ thuộc nền tảng nào. Riêng Telegram có cho bot xoá tin đã gửi trong 48 giờ, nên bổ sung thêm đường thu hồi thật cho mỗi kênh đó.

**Tech Stack:** ASP.NET Core 8 Minimal API · Dapper + PostgreSQL · React qua UMD/Babel · xUnit (test logic thuần)

**Spec:** [2026-08-28-so-sanh-action-chatbotx.md](2026-08-28-so-sanh-action-chatbotx.md) mục I — ranh giới giữa dữ liệu của mình và của nền tảng

## Global Constraints

- **Không hứa thứ không làm được.** Nút chỉ được gọi là "Thu hồi" khi tin thật sự chưa rời máy chủ (Task 1–2) hoặc kênh thật sự cho xoá (Task 3). Hết cửa sổ đó thì nút phải đổi thành "Xoá khỏi hộp thư" kèm chữ *khách vẫn thấy* — xem Task 4 của [kế hoạch đợt 1](2026-08-28-bo-sung-action-hop-thu-dot-1.md).
- **Chỉ hoãn tin của NGƯỜI THẬT** (`ChatSender.Agent`). Tin của trợ lý không hoãn: nó đã chờ 4 giây gộp tin rồi, hoãn thêm nữa là khách ngồi nhìn màn hình trống lâu hơn nữa. Tin hệ thống cũng không hoãn.
- **Hoãn bao lâu là do công ty đặt**, mặc định 5 giây, đặt 0 là tắt hẳn tính năng. Đội trực đông và cẩn thận thì để 0; đội mỏng hay gõ vội thì để 10.
- Chữ hiển thị/log/chú thích tiếng Việt · ngày giờ UTC · route mới phải nằm trong `OwnedPaths` · mọi thao tác ghi `chat_audit` · CHANGELOG bắt buộc.
- Test là logic thuần: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`

---

## Vì sao KHÔNG làm "thu hồi thật" trên Meta — đọc trước khi ai đó thử lại

Đã tra: Messenger, Instagram và WhatsApp Cloud API **không có endpoint thu hồi/gỡ tin cho Trang hoặc số doanh nghiệp**. ChatbotX cũng không có — và điều đáng nói là bên đó **có** nút `delete-message`, nhưng đọc mã (`apps/builder/src/features/messages/actions/delete-message.action.ts`) thì nó gọi `repository.deleteById`, tức chỉ xoá trong CSDL của họ. Khách vẫn thấy nguyên tin.

Nghĩa là một nút "Thu hồi" đặt ở đó **là lời nói dối trên giao diện**, và là loại nói dối có hậu quả thật: nhân viên tưởng đã rút lại được câu lỡ tay nên không gọi xin lỗi khách. Kế hoạch này cố ý đi đường khác để nút "Thu hồi" nói đúng sự thật.

---

## File Structure

| File | Trách nhiệm |
|---|---|
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | Luật thuần: ai được hoãn · còn trong cửa sổ thu hồi không |
| `TourkitAiProxy.Domain/Chat/ChatModels.cs` | Model tin nhắn: `SendAfterUtc` và `ProcessedUtc` |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` | Cột `send_after` trên `chat_outbox` |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs` | Xếp hàng có hẹn giờ · nhận việc theo giờ · huỷ khỏi hàng đợi |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | Route thu hồi + truyền độ trễ lúc gửi |
| `TourkitAiProxy.Services/Chat/Channels/IChatChannelAdapter.cs` | Giao diện tuỳ chọn `IMessageRecaller` |
| `TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs` | Thu hồi THẬT (`deleteMessage`) |
| `wwwroot/pages/chat-inbox.jsx` · `styles.css` | Dải "Đang gửi… Thu hồi" + đếm ngược |
| `TourkitAiProxy.Tests/Chat/RecallTests.cs` (mới) | Test bốn luật thuần |

---

## Task 1: Cửa sổ hoãn gửi

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs`
- Modify: `TourkitAiProxy.Domain/Chat/ChatModels.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Test: `TourkitAiProxy.Tests/Chat/RecallTests.cs`

**Interfaces:**
- Produces: `ChatRules.HoanGuiGiay(ChatSender nguoiGui, int caiDatGiay) -> int` · `ChatRepository.EnqueueOutboxAsync(..., int hoanGiay = 0, ...)` · `ChatRepository.CancelOutboxAsync(string tenant, long conversationId, long messageId, CancellationToken ct) -> Task<bool>`

**Bối cảnh:** `ClaimOutboxAsync` hiện lấy mọi dòng `status = 0` không xét thời gian, và endpoint `/send` gọi `Signal(ChatLane.Out)` ngay sau khi xếp hàng nên worker tỉnh dậy lập tức. Vì thế hôm nay **không có một giây nào** để rút lại.

> **Hiệu chỉnh bắt buộc khi thực thi:** Thêm `ChatMessage.SendAfterUtc`; `ListMessages` phải trả giá trị `send_after` có thẩm quyền này cho UI. `CancelOutboxAsync` nhận cả `conversationId` và phải atomically chỉ huỷ/update đúng `tenant + conversationId + messageId`. UI ở Task 2 phải đếm ngược theo `sendAfterUtc`, không suy diễn từ `createdUtc + cấu hình`.

- [ ] **Step 1: Viết test thất bại cho luật hoãn**

Tạo `TourkitAiProxy.Tests/Chat/RecallTests.cs`:

```csharp
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class RecallTests
{
    [Fact]
    public void Chi_hoan_tin_cua_NGUOI_THAT()
    {
        // Trợ lý đã chờ 4 giây gộp tin trước khi soạn; hoãn thêm nữa là khách ngồi nhìn màn hình
        // trống lâu hơn. Mà trợ lý cũng không phải thứ gõ nhầm — guardrail đã lọc trước rồi.
        Assert.Equal(5, ChatRules.HoanGuiGiay(ChatSender.Agent, 5));
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.Ai, 5));
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.System, 5));
    }

    [Fact]
    public void Dat_0_la_tat_han_tinh_nang()
    {
        Assert.Equal(0, ChatRules.HoanGuiGiay(ChatSender.Agent, 0));
    }

    [Theory]
    [InlineData(-5, 0)]      // số âm là cấu hình sai, đừng biến thành lỗi lúc chạy
    [InlineData(999, 60)]    // trần: giữ khách chờ một phút đã là quá nhiều
    public void Kep_gia_tri_cau_hinh_hong(int caiDat, int mong)
    {
        Assert.Equal(mong, ChatRules.HoanGuiGiay(ChatSender.Agent, caiDat));
    }
}
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "RecallTests"`
Expected: FAIL — `ChatRules` không có `HoanGuiGiay`

- [ ] **Step 3: Thêm luật vào `ChatRules.cs`**

```csharp
    /// <summary>Trần cho cửa sổ hoãn gửi. Giữ khách chờ hơn một phút là quá nhiều dù ai đặt.</summary>
    public const int HoanGuiToiDaGiay = 60;

    /// <summary>
    /// Tin này nằm chờ bao nhiêu giây trước khi thật sự đi.
    ///
    /// <para><b>Đây là toàn bộ cơ chế "thu hồi".</b> Meta không cho doanh nghiệp thu hồi tin đã
    /// gửi, nên cách duy nhất để nút Thu hồi nói thật là đừng gửi vội — giữ tin lại vài giây, và
    /// trong quãng đó thì rút lại là tuyệt đối vì tin chưa hề rời máy chủ.</para>
    ///
    /// <para>Chỉ hoãn tin của NGƯỜI THẬT: trợ lý đã chờ 4 giây gộp tin rồi, và nó cũng không phải
    /// thứ gõ nhầm. Tin hệ thống thì không ai cần rút lại.</para>
    /// </summary>
    public static int HoanGuiGiay(ChatSender nguoiGui, int caiDatGiay)
        => nguoiGui != ChatSender.Agent ? 0 : Math.Clamp(caiDatGiay, 0, HoanGuiToiDaGiay);
```

- [ ] **Step 4: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "RecallTests"`
Expected: PASS (4 ca)

- [ ] **Step 5: Thêm cột vào `ChatDb.cs`**

Đặt ngay dưới bảng `chat_outbox`:

```sql
    -- SỚM NHẤT được phép gửi. Đây là cửa sổ "thu hồi": tin của người thật nằm chờ vài giây, và
    -- trong quãng đó nút Thu hồi xoá thẳng dòng này — tin CHƯA BAO GIỜ rời máy chủ.
    --
    -- Vì sao phải làm thế thay vì gọi API thu hồi: Meta không có API đó cho phía doanh nghiệp.
    -- Xem docs/superpowers/plans/2026-08-28-thu-hoi-tin.md.
    --
    -- Mặc định NULL = gửi ngay, nên mọi dòng đang nằm sẵn trong hàng đợi lúc nâng cấp vẫn đi bình
    -- thường. Cột không mặc định nên ALTER chỉ ghi metadata, không viết lại bảng.
    ALTER TABLE chat_outbox ADD COLUMN IF NOT EXISTS send_after timestamptz;

    -- Chỉ mục CÓ ĐIỀU KIỆN của hàng đợi phải mang thêm cột mới, nếu không worker vẫn quét đúng
    -- những dòng chưa tới giờ rồi bỏ đi — vô hại nhưng lãng phí, và lớn dần theo hàng đợi.
    DROP INDEX IF EXISTS ix_outbox_cho;
    CREATE INDEX IF NOT EXISTS ix_outbox_cho
      ON chat_outbox (send_after NULLS FIRST, created_utc) WHERE status = 0;
```

- [ ] **Step 6: Sửa `ClaimOutboxAsync` để tôn trọng giờ hẹn**

Trong `ChatRepository.cs`, sửa câu con:

```sql
               SELECT id FROM chat_outbox
                WHERE status = 0 AND (send_after IS NULL OR send_after <= now())
                ORDER BY send_after NULLS FIRST, created_utc
                LIMIT @n FOR UPDATE SKIP LOCKED
```

- [ ] **Step 7: Cho `EnqueueOutboxAsync` nhận độ trễ**

```csharp
    /// <param name="hoanGiay">
    /// Giữ tin lại bấy nhiêu giây trước khi gửi — cửa sổ để người trực rút lại. 0 = gửi ngay.
    /// Tính bằng <see cref="ChatRules.HoanGuiGiay"/>, đừng tự nhân chia ở chỗ gọi.
    /// </param>
    public async Task EnqueueOutboxAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default, int hoanGiay = 0)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_outbox (tenant_id, conversation_id, message_id, send_after)
            VALUES (@tenant, @conv, @msg,
                    CASE WHEN @hoan > 0 THEN now() + (@hoan || ' seconds')::interval END)
            """, new { tenant, conv = conversationId, msg = messageId, hoan = hoanGiay });
    }
```

- [ ] **Step 8: Thêm đường HUỶ khỏi hàng đợi**

```csharp
    /// <summary>
    /// Rút một tin khỏi hàng đợi gửi — thu hồi THẬT, vì tin chưa hề rời máy chủ.
    ///
    /// <para>Điều kiện <c>status = 0</c> và <c>send_after &gt; now()</c> kiểm ngay trong câu lệnh
    /// chứ không ở tầng trên: worker có thể vừa nhặt đúng tin đó lên giữa chừng. Trả <c>false</c>
    /// nghĩa là muộn rồi — chỗ gọi phải nói thật với người dùng, đừng báo thành công.</para>
    /// </summary>
    public async Task<bool> CancelOutboxAsync(string tenant, long conversationId, long messageId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            WITH bo AS (
              DELETE FROM chat_outbox
               WHERE tenant_id = @tenant AND conversation_id = @conversationId AND message_id = @msg
                 AND status = 0 AND send_after IS NOT NULL AND send_after > now()
              RETURNING message_id
            )
            UPDATE chat_messages SET deleted_utc = now(), state = 4
             WHERE id = (SELECT message_id FROM bo) AND tenant_id = @tenant AND conversation_id = @conversationId
            """, new { tenant, conversationId, msg = messageId }) > 0;
    }
```

- [ ] **Step 9: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS

- [ ] **Step 10: Commit**

```bash
git add TourkitAiProxy.Domain/Chat/ChatRules.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Tests/Chat/RecallTests.cs
git commit -m "feat(hộp thư chat): hàng đợi gửi biết hẹn giờ, mở đường cho thu hồi"
```

---

## Task 2: Nút thu hồi

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs`
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs`
- Modify: `wwwroot/pages/chat-inbox.jsx`, `wwwroot/styles.css`
- Modify: UI cài đặt chatbot theo tenant
- Test: `TourkitAiProxy.Tests/Chat/RecallTests.cs`

**Interfaces:**
- Consumes: `ChatRules.HoanGuiGiay` · `ChatRepository.EnqueueOutboxAsync(hoanGiay:)` · `ChatRepository.CancelOutboxAsync`
- Produces: route `POST /api/v1/chat/conversations/{id}/messages/{msgId}/recall` · trường `undoSendSeconds` trong `GET /api/v1/chat/bot-settings`

> **Hiệu chỉnh bắt buộc khi thực thi:** `UndoSendSeconds` là cấu hình **theo tenant** trong `ChatBotSettings`, có cột trong `chat_bot_settings`, repository, `GET`/`PUT /bot-settings` và UI cài đặt. Mặc định 5, kẹp trong 0..60; không được dùng cấu hình ứng dụng. Route recall phải xác nhận message thuộc đúng conversation trước khi huỷ/audit. UI đếm ngược từ `SendAfterUtc` do server trả về.

- [ ] **Step 1: Khai cấu hình**

Thêm `undo_send_seconds integer NOT NULL DEFAULT 5` vào `chat_bot_settings`; repository đọc/ghi `ChatBotSettings.UndoSendSeconds` đã kẹp trong 0..60, và UI cài đặt gửi/nhận nó qua `GET`/`PUT /bot-settings` theo tenant:

```csharp
var undoSendSeconds = Math.Clamp(settings.UndoSendSeconds, 0, 60);
```

- [ ] **Step 2: Truyền độ trễ vào đường gửi**

Trong `ChatInboxEndpoints.cs`, ở route `POST /conversations/{id}/send`, sửa lượt xếp hàng:

```csharp
            // Người thật gõ thì giữ lại vài giây cho kịp bấm Thu hồi — xem ChatRules.HoanGuiGiay.
            var settings = await repo.GetBotSettingsAsync(a.TenantId, ct);
            var hoan = ChatRules.HoanGuiGiay(ChatSender.Agent, settings.UndoSendSeconds);
            await repo.EnqueueOutboxAsync(a.TenantId, id, msgId.Value, ct, hoan);
            tin.Signal(Services.Chat.Inbox.ChatLane.Out);
```

> Vẫn gọi `Signal`: worker tỉnh dậy, thấy chưa tới giờ, ngủ lại — vô hại. Nhịp 5 giây sẵn có của worker là thứ nhặt tin lên khi hết hẹn, nên tin thật sự đi trong khoảng `hoãn`…`hoãn + 5` giây.

- [ ] **Step 3: Thêm route thu hồi**

```csharp
        // THU HỒI — chỉ chạy được khi tin còn nằm trong hàng đợi và chưa tới giờ gửi. Hết cửa sổ
        // đó thì KHÔNG có đường nào khác: Meta không cho doanh nghiệp thu hồi tin đã gửi. Trả 409
        // để giao diện nói thật thay vì báo thành công rồi để nhân viên tưởng đã rút lại được.
        g.MapPost("/conversations/{id:long}/messages/{msgId:long}/recall", async (long id, long msgId,
            HttpContext ctx, TkSessionStore sessions, ChatRepository repo, ChatEventBus bus,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, ct) is null) return Results.NotFound();

            if (!await repo.CancelOutboxAsync(a.TenantId, id, msgId, ct))
                return Results.Json(new { error = "Tin đã gửi đi mất rồi — không thu hồi được nữa" },
                    Web, statusCode: 409);

            await repo.AppendAuditAsync(a.TenantId, id, a.Username, "thu-hoi-tin",
                new JsonObject { ["tin"] = msgId }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
            return Results.Json(new { ok = true }, Web);
        });
```

- [ ] **Step 4: Trả cấu hình cho giao diện**

`GET /bot-settings` trả `undoSendSeconds` từ `ChatBotSettings`; `PUT /bot-settings` nhận, kẹp 0..60 và lưu theo tenant. UI cài đặt hiển thị/lưu đúng trường này. Dải thu hồi vẫn dùng `SendAfterUtc` của từng tin để đếm ngược, không suy ra từ cấu hình.

- [ ] **Step 5: Dải "Đang gửi… Thu hồi" trên giao diện**

Trong `wwwroot/pages/chat-inbox.jsx`, với tin có `state === 0` (chờ) và do người thật gửi:

```jsx
{/* Đếm ngược thật, không phải trang trí: hết giây là tin đã đi và không rút lại được nữa. */}
{tin.state === 0 && conLai > 0 && (
  <div className="ci-thu-hoi">
    <span>Đang gửi sau {conLai}s</span>
    <button onClick={async () => {
      const r = await authedFetch(
        `/api/v1/chat/conversations/${hoiThoai.id}/messages/${tin.id}/recall`, { method: 'POST' });
      if (!r.ok) { bao('Tin đã gửi đi mất rồi — không thu hồi được nữa'); }
      taiLaiTin();
    }}>Thu hồi</button>
  </div>
)}
```

`conLai` tính từ `tin.sendAfterUtc - now`, giảm mỗi giây bằng `setInterval`; hết giờ thì dải tự biến mất. `sendAfterUtc` là mốc có thẩm quyền do server trả về.

- [ ] **Step 6: Thêm CSS**

Trong `wwwroot/styles.css`, dải nhỏ màu nhạt nằm dưới bong bóng tin, nút chữ gạch chân — cùng lối với các `.ci-*` sẵn có.

- [ ] **Step 7: Chạy TOÀN BỘ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(hộp thư chat): thu hồi tin trong vài giây trước khi gửi"
```

---

## Task 3: Thu hồi THẬT trên Telegram

**Files:**
- Modify: `TourkitAiProxy.Services/Chat/Channels/IChatChannelAdapter.cs`
- Modify: `TourkitAiProxy.Services/Chat/Channels/TelegramChatAdapter.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`
- Test: `TourkitAiProxy.Tests/Chat/RecallTests.cs`

**Interfaces:**
- Consumes: route `/recall` từ Task 2
- Produces: `interface IMessageRecaller { TimeSpan RecallWindow { get; } Task<bool> RecallAsync(string tenantId, string accountId, string externalUserId, string externalMsgId, CancellationToken ct); }`

**Bối cảnh:** Telegram Bot API cho bot **xoá tin của chính nó trong 48 giờ** (`deleteMessage`) — xoá thật, khách không còn thấy. Đây là kênh **duy nhất** trong sáu kênh làm được. Vì thế nó là một **giao diện tuỳ chọn**, đúng lối `IButtonSender` / `IApprovedTemplateSender` sẵn có: kênh nào làm được thì cài đặt, kênh nào không thì giao diện ẩn hẳn nút — đừng hiện rồi báo lỗi.

> **Hiệu chỉnh bắt buộc khi thực thi:** Thêm `ChatMessage.ProcessedUtc`; cửa sổ 48 giờ tính từ mốc này, không phải `CreatedUtc`. External ID outbound Telegram phải lưu đúng `message_id` dạng số, không ghép `chatId:messageId`. Cài đường HTTP/JSON `deleteMessage` riêng trả về boolean; không gọi `CallJsonAsync` nếu helper đó không tồn tại. Route phải xác nhận message thuộc `tenant + conversationId`; chỉ `TelegramChatAdapter` được cài `IMessageRecaller`.

- [ ] **Step 1: Viết test thất bại canh đúng những kênh cài đặt**

```csharp
    [Fact]
    public void Chi_Telegram_thu_hoi_that_duoc()
    {
        // Nếu mai có người cho Messenger cài IMessageRecaller thì test này đỏ, và người đó phải
        // dừng lại đọc: Meta KHÔNG có API thu hồi cho doanh nghiệp, cài vào là hứa suông.
        Assert.True(typeof(IMessageRecaller).IsAssignableFrom(typeof(TelegramChatAdapter)));

        foreach (var t in new[] { typeof(MessengerChatAdapter), typeof(InstagramChatAdapter),
                                  typeof(WhatsAppChatAdapter), typeof(ZaloChatAdapter),
                                  typeof(TikTokChatAdapter) })
            Assert.False(typeof(IMessageRecaller).IsAssignableFrom(t),
                $"{t.Name} không có API thu hồi cho phía doanh nghiệp — cài IMessageRecaller là hứa suông");
    }
```

- [ ] **Step 2: Chạy test cho chắc là ĐỎ**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "RecallTests"`
Expected: FAIL — không có `IMessageRecaller`

- [ ] **Step 3: Khai giao diện tuỳ chọn**

Thêm vào cuối `IChatChannelAdapter.cs`:

```csharp
/// <summary>
/// Kênh cho phép <b>thu hồi THẬT</b> một tin đã gửi — khách không còn thấy nữa.
///
/// <para>⚠️ <b>Chỉ Telegram.</b> Messenger, Instagram và WhatsApp Cloud API KHÔNG cấp API thu hồi
/// cho phía doanh nghiệp: thu hồi là tính năng của ứng dụng người dùng, không phải của nền tảng.
/// Cài giao diện này cho một kênh không làm được là hứa suông với người trực — và họ sẽ không đi
/// xin lỗi khách vì tưởng đã rút lại được.</para>
///
/// <para>Kênh không cài thì hộp thư dựa vào cửa sổ hoãn gửi (xem
/// <c>ChatRules.HoanGuiGiay</c>) — rút lại trước khi tin rời máy chủ.</para>
/// </summary>
public interface IMessageRecaller
{
    /// <summary>Thu hồi được trong bao lâu kể từ lúc gửi. Telegram: 48 giờ.</summary>
    TimeSpan RecallWindow { get; }

    /// <summary>Trả <c>false</c> khi nền tảng từ chối — chỗ gọi phải nói thật, đừng nuốt.</summary>
    Task<bool> RecallAsync(string tenantId, string accountId, string externalUserId,
        string externalMsgId, CancellationToken ct);
}
```

- [ ] **Step 4: Cài đặt cho Telegram**

Thêm `IMessageRecaller` vào danh sách giao diện của `TelegramChatAdapter`, rồi:

```csharp
    /// <inheritdoc />
    public TimeSpan RecallWindow => TimeSpan.FromHours(48);

    /// <inheritdoc />
    public async Task<bool> RecallAsync(string tenantId, string accountId, string externalUserId,
        string externalMsgId, CancellationToken ct)
    {
        var token = await TokenAsync(tenantId, accountId, ct);
        if (token is null) return false;

        // ExternalMsgId outbound là đúng message_id dạng số, không ghép chatId vào đó.
        if (!long.TryParse(externalMsgId, out var messageId)) return false;
        return await DeleteMessageAsync(token, externalUserId, messageId, ct);
    }
```

- [ ] **Step 5: Chạy test cho chắc là XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "RecallTests"`
Expected: PASS

- [ ] **Step 6: Nối vào route `/recall`**

Sửa route ở Task 2: nếu `CancelOutboxAsync` trả `false` (tin đã đi rồi) thì **thử đường thu hồi thật** trước khi trả 409:

```csharp
            if (!await repo.CancelOutboxAsync(a.TenantId, id, msgId, ct))
            {
                // Tin đã rời máy chủ. Kênh nào thu hồi thật được thì thử — chỉ Telegram.
                var v = await repo.GetConversationAsync(a.TenantId, id, ct);
                var tinDaGui = await repo.GetMessageAsync(a.TenantId, id, msgId, ct);
                if (v is not null && tinDaGui?.ExternalMsgId is { Length: > 0 } maNgoai
                    && svc.Adapter((ChatChannel)v.Channel) is IMessageRecaller boThuHoi
                    && tinDaGui.ProcessedUtc is { } processedUtc
                    && DateTime.UtcNow - processedUtc < boThuHoi.RecallWindow
                    && await boThuHoi.RecallAsync(a.TenantId, v.AccountId, v.ContactExternalId,
                                                  maNgoai, ct))
                {
                    await repo.SoftDeleteMessageAsync(a.TenantId, id, msgId, ct);
                    await repo.AppendAuditAsync(a.TenantId, id, a.Username, "thu-hoi-tin",
                        new JsonObject { ["tin"] = msgId, ["kenh"] = true }.ToJsonString(), ct);
                    bus.Publish(new(a.TenantId, id, "doi-trang-thai", msgId));
                    return Results.Json(new { ok = true, recalledOnChannel = true }, Web);
                }

                return Results.Json(new { error = "Tin đã gửi đi mất rồi — không thu hồi được nữa" },
                    Web, statusCode: 409);
            }
```

> Thêm bắt buộc `ChatRepository.GetMessageAsync(string tenant, long conversationId, long messageId, CancellationToken ct)`; trả `ChatMessage` gồm `ExternalMsgId` và `ProcessedUtc`.

- [ ] **Step 7: Giao diện — nút Thu hồi ở Telegram sống lâu hơn**

Với hội thoại Telegram, nút **Thu hồi** vẫn hiện sau khi hết đếm ngược, trong 48 giờ, kèm chú thích *"Xoá cả phía khách"*. Kênh khác thì hết đếm ngược là nút đổi thành **Xoá khỏi hộp thư** kèm chữ *khách vẫn thấy*.

- [ ] **Step 8: Chạy TOÀN BỘ test + CHANGELOG + commit**

```markdown
- **Gửi nhầm thì rút lại được.** Sau khi bấm gửi, tin nằm chờ vài giây và có nút **Thu hồi** —
  bấm là tin không bao giờ đến tay khách. Quản trị đặt được số giây này (mặc định 5, đặt 0 để tắt).
  Riêng Telegram còn thu hồi được cả tin đã gửi, trong vòng 48 giờ.

  *Lưu ý:* hết vài giây đó thì Facebook, Instagram, WhatsApp, Zalo và TikTok **không cho thu hồi** —
  đó là quy định của họ, không phải giới hạn của phần mềm. Lúc đó bạn chỉ xoá được tin khỏi hộp thư
  của mình, còn khách vẫn thấy; màn hình sẽ nói rõ như vậy.
```

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
git add -A && git commit -m "feat(hộp thư chat): thu hồi thật trên Telegram trong 48 giờ"
```

---

## Self-review

- **Phủ hết spec?** Mục I của bản so sánh nêu ba việc: gỡ tin Telegram (Task 3) · gỡ tin Meta chỉ cục bộ (đã nằm ở Task 4 kế hoạch đợt 1) · nói đúng phạm vi trên giao diện (Task 2 Step 5, Task 3 Step 7). Đủ.
- **Không có chỗ trống:** mọi bước có mã thật hoặc câu SQL thật.
- **Tên gọi nhất quán:** `HoanGuiGiay` · `CancelOutboxAsync` · `IMessageRecaller.RecallAsync` dùng y hệt ở mọi task.
- **Tính nhất quán đã chốt:** `ChatRepository.GetMessageAsync` là bắt buộc với chữ ký có `tenant`, `conversationId` và `messageId`; không được để nhánh triển khai tuỳ tình trạng sẵn có.
