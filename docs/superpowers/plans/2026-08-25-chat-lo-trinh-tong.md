# Hộp thư chat — Lộ trình tổng, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa hộp thư chat từ "chạy được" lên "vận hành được thật": tin không mất, trạng thái nói đúng sự thật, nhiều nhân viên làm cùng lúc không giẫm chân, và AI biết nó đang nói với ai.

**Architecture:** Bốn đợt dọc, mỗi đợt tự chạy và tự kiểm được. Đợt 3 đóng nốt nền móng (vòng đời tin). Đợt 4 bỏ hỏi-lại-4-giây, chuyển sang đẩy sự kiện + phân trang con trỏ. Đợt 5 cho nhiều người làm chung một hộp thư. Đợt 6 nối hồ sơ khách với CRM. Mọi thứ đều cộng thêm (additive) — không đợt nào phá dữ liệu của đợt trước.

**Tech Stack:** ASP.NET Core 8 · PostgreSQL (Npgsql + Dapper, `MatchNamesWithUnderscores`) · SQL Server (`dbo.TenantChannelSettings`, phiên) · Redis (tuỳ chọn) · xUnit · React qua Babel/esbuild, **không bundler**.

**Spec:** [docs/superpowers/specs/2026-08-24-omnichannel-chat-parity-design.md](../specs/2026-08-24-omnichannel-chat-parity-design.md)

---

## Bảng đối chiếu: spec nói gì, code đang thế nào

Kiểm bằng cách đọc code thật ngày 25/08, không suy đoán. Đây là căn cứ chia đợt.

| Khoảng trống (spec §2.2) | Kiểm trong code | Đợt |
|---|---|---|
| Inbound durability — `Task.Run` | **ĐÃ XONG** (`ChatInboundWorker` + `chat_inbound_events`) | ✅ đợt 2 |
| Multi-account identity | **ĐÃ XONG** (`ux_conv_scope_acc` có `account_id`) | ✅ đợt 2 |
| Quick reply chưa có repo/API/UI | **ĐÃ XONG** (`ChatQuickReplyRepository` + 3 route + popup `/`) | ✅ đợt 2 |
| Lifecycle — outbound không lưu provider message ID | `SendResult.ExternalMsgId` có, cả 3 adapter trả về, `ChatOutboxWorker` **không đọc ở đâu cả** | **Đợt 3** |
| Lifecycle — Zalo seen marker chưa vào DB | `ZaloChatAdapter:159` bóc ra → `ChatInboundService:65` `return` thẳng | **Đợt 3** |
| Lifecycle — Messenger bỏ delivery/read | `MessengerChatAdapter:126` `continue`, chú thích "chưa dùng ở đợt này" | **Đợt 3** |
| Realtime — polling 4 giây | `useEffect` + `setInterval(nhip, 4000)` trong `chat-inbox.jsx` | **Đợt 4** |
| Pagination cố định | `ListConversationsAsync(limit=60, clamp 200)`, `ListMessagesAsync(limit=100, clamp 300)`, không con trỏ | **Đợt 4** |
| Collaboration thiếu team/transfer/per-user unread/audit | `chat_conversations` đã có `assigned_username`, `agent_last_read_at`, `archived_at`; **chưa có** bảng team, chưa có audit | **Đợt 5** |
| Contact/CRM thiếu tags/notes/custom fields | `chat_contacts` có `crm_customer_id` nhưng **chưa ai ghi**; không có bảng tag/note | **Đợt 6** |
| Composer thiếu emoji/reply-to/multi-file | Đã có đính kèm 1 tệp + mẫu trả lời | Đợt 7 — xem §"Chưa lên bước được" |
| AI thiếu history/CRM context/policy | `Chat:SystemPrompt` chung, không lịch sử | Đợt 8 — xem §"Chưa lên bước được" |
| Webchat chỉ có enum | `ChatChannel.Web = 2`, không adapter | Đợt 9 — xem §"Chưa lên bước được" |

---

## Global Constraints

Chép từ CLAUDE.md và spec — **mọi task đều phải theo**, không nhắc lại ở từng task:

- **Chữ cho người dùng, chú thích, log, commit: TIẾNG VIỆT.**
- **DateTime = UTC, luôn kèm `Z`.** `DateTime.UtcNow` / SQL `now()`, KHÔNG `DateTime.Now`/`GETDATE()`. Chuỗi `ToString("o")` đọc từ SQL Server phải `DateTime.SpecifyKind(x, DateTimeKind.Utc)` trước — Dapper đọc `DATETIME2` ra `Kind=Unspecified`, thiếu `Z` là giao diện lệch +7h.
- **CodeGraph trước khi sửa symbol:** `codegraph impact <Symbol>`. Blast-radius rộng → **dừng, báo người dùng**.
- **Test-first:** viết test đỏ → chạy cho thấy đỏ → code → chạy cho thấy xanh → commit.
- **CSDL chat là PostgreSQL** (`ConnectionStrings:Chat`), KHÔNG phải SQL Server. Schema ở `ChatDb.SchemaSql`, idempotent.
- **`ALTER TABLE ... ADD COLUMN IF NOT EXISTS` phải đứng TRƯỚC mọi `CREATE INDEX` dùng cột đó** — `CREATE TABLE IF NOT EXISTS` là lệnh rỗng trên CSDL đã có, viết sau là hỏng đúng ở máy chưa nâng cấp.
- **`ON CONFLICT` phải khớp CHÍNH XÁC biểu thức chỉ mục duy nhất** — lệch là lỗi lúc CHẠY.
- **Không có CI chạy PostgreSQL.** Test chỉ phủ được logic thuần + guard mã nguồn. Việc cần DB → kiểm tay trên staging, ghi lại kết quả thật.
- **Chỉ dùng `staging.tourkit.vn` để thử.** `erp.tourkit.vn` là dữ liệu thật: đọc được, **cấm ghi**.
- **Nhánh tách từ `dev`**, không commit thẳng `main`.
- **Dừng app trước khi `dotnet build`** — không thì `MSB3027` (file bị khoá).
- **Sửa `.jsx` xong phải `.\build-frontend.ps1`** — `wwwroot/dist/` đã tồn tại nên app chạy chế độ bundle; không dựng lại thì sửa **không có tác dụng** và dev không bao giờ lộ ra.
- **Thêm route mới vào hộp thư chat → thêm tiền tố vào `ChatInboxEndpoints.DuongRieng`.** `ChatFeatureFlagCoverageTests` canh; quên là khi tắt cờ `Features:Chat` route đó rơi vào `MapFallback` và trả `index.html` kèm **200**.
- **CHANGELOG.md bắt buộc** trước khi phát hành, viết cho NGƯỜI DÙNG CUỐI: không mã commit, không tên file/hàm/bảng, không thuật ngữ kỹ thuật.

---

## Quyết định kiến trúc lệch khỏi spec — đọc trước khi làm đợt 4

Spec §3.4 ghi "SignalR". **Plan này dùng SSE (Server-Sent Events) thay thế.** Đây là lệch có chủ đích, lý do:

1. **Dự án đã có sẵn hạ tầng SSE ở CẢ HAI đầu** — backend: `Endpoints/AiEndpoints.cs:204`, `ChatEndpoints.cs:167`, `DealEndpoints.cs:209` đều set `text/event-stream`; frontend: `window.tourkitUtil.readSSE` ([wwwroot/lib/util.js:13](../../../wwwroot/lib/util.js)). Không phải học thêm gì.
2. **Frontend KHÔNG có bundler.** Thêm SignalR nghĩa là thêm một thẻ `<script>` CDN vào `index.html` **và** một `import` vào `bundle-entry.js` — mà hai danh sách đó đã lệch nhau một lần rồi (12 file, xem CLAUDE.md). SSE dùng `EventSource` có sẵn trong trình duyệt, không thêm phụ thuộc nào.
3. **Nhu cầu thật là MỘT CHIỀU** server → client ("có tin mới"). Nhân viên gõ phím thì gửi bằng POST như hiện tại. SignalR hai chiều là thừa.
4. **SSE tự kết nối lại** kèm `Last-Event-ID` — đúng cái spec gọi là "reconnect cursor", không phải viết tay.

⚠️ **Cái giá phải biết:** HTTP/1.1 giới hạn **6 kết nối/origin/trình duyệt**; một luồng SSE giữ mất một suất. Mở nhiều tab TRAV-AI là hết suất, các request thường bị treo. Giảm nhẹ: **chỉ mở SSE khi tab đang hiện** (`document.hidden` → đóng), và nói rõ trong tài liệu. Nếu prod chạy HTTP/2 thì hết vấn đề (HTTP/2 ghép kênh, không giới hạn 6).

⚠️ **Nhiều instance sau load-balancer:** SSE giữ kết nối tới ĐÚNG MỘT instance. Tin tới instance khác thì tab đang mở không nhận được. Đợt 4 giải quyết bằng **Redis pub/sub** (`RedisProvider` đã có sẵn trong DI). Không có Redis → tự lùi về chế độ hỏi-lại như cũ, **nói rõ trong log lúc khởi động**, không im lặng.

---

# ĐỢT 3 — Vòng đời tin nhắn

**Mục tiêu:** tin gửi đi nói được nó đang ở đâu: đã gửi → khách đã nhận → khách đã xem.

**Vì sao đây là việc gấp:** giao diện **đã vẽ sẵn** dấu tích hai mức (`DauGui` trong [chat-inbox.jsx](../../../wwwroot/pages/chat-inbox.jsx):110) và enum `ChatState` đã có đủ `DaGui=1, DaNhan=2, DaXem=3`. Nhưng **không dòng code nào từng đặt state lên 2 hay 3 cho tin mình gửi**. Nhân viên nhìn mãi một tích, không phân biệt được "khách chưa đọc" với "hệ thống không biết". Giao diện hứa một thứ mà dữ liệu không bao giờ giao — kiểu hỏng tệ nhất vì trông như đang chạy đúng.

⚠️ **Telegram KHÔNG có báo đã nhận/đã xem cho bot.** Bot API không cung cấp. Tin Telegram dừng ở "đã gửi" **vĩnh viễn và đó là đúng**. Đừng "sửa" bằng cách tự nhảy state khi gửi xong — như thế là nói dối nhân viên rằng khách đã nhận trong khi mình không biết.

## Task 3.1: Luật không lùi trạng thái (hàm thuần)

**Files:**
- Modify: `Services/Chat/Inbox/ChatRules.cs` (thêm cuối class)
- Test: `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs` (**tạo mới**)

**Interfaces:**
- Consumes: `ChatState` (`Services/Chat/Inbox/ChatModels.cs:29`) — `Cho=0, DaGui=1, DaNhan=2, DaXem=3, Hong=4`
- Produces: `public static bool KhongLui(ChatState dangCo, ChatState moi)`

> Nền tảng KHÔNG bảo đảm thứ tự webhook: `delivery` (đã nhận) hoàn toàn có thể tới **sau** `read` (đã xem) — hai webhook hai đường mạng, hoặc bị gửi lại. Ghi đè mù thì tin đang "đã xem" tụt về "đã nhận", nhân viên thấy dấu tích **chạy ngược**, tưởng khách bỏ đọc. Và `Hong` (4) tuy số lớn nhất nhưng KHÔNG phải mức cao nhất: gửi được rồi mà báo hỏng là vô nghĩa.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Vòng đời tin gửi đi. Nền tảng KHÔNG bảo đảm thứ tự webhook, nên mọi cập nhật trạng thái
/// phải đi qua một luật duy nhất — không thì dấu tích chạy ngược trước mắt nhân viên.
/// </summary>
public class ChatLifecycleTests
{
    [Theory]
    [InlineData(ChatState.Cho, ChatState.DaGui, true)]
    [InlineData(ChatState.DaGui, ChatState.DaNhan, true)]
    [InlineData(ChatState.DaNhan, ChatState.DaXem, true)]
    [InlineData(ChatState.DaGui, ChatState.DaXem, true)]    // nhảy cóc: chỉ nhận được "đã xem"
    public void Tien_len_thi_duoc(ChatState dangCo, ChatState moi, bool mong)
        => Assert.Equal(mong, ChatRules.KhongLui(dangCo, moi));

    [Theory]
    [InlineData(ChatState.DaXem, ChatState.DaNhan)]   // delivery tới SAU read — chuyện thường
    [InlineData(ChatState.DaXem, ChatState.DaGui)]
    [InlineData(ChatState.DaNhan, ChatState.DaGui)]
    public void Lui_lai_thi_bo_qua(ChatState dangCo, ChatState moi)
        => Assert.False(ChatRules.KhongLui(dangCo, moi));

    [Fact]
    public void Cung_mot_muc_thi_bo_qua()
        => Assert.False(ChatRules.KhongLui(ChatState.DaXem, ChatState.DaXem));

    [Fact]
    public void Tin_da_gui_duoc_thi_khong_the_thanh_hong()
    {
        // Hỏng (4) số lớn nhất nhưng KHÔNG phải mức cao nhất.
        Assert.False(ChatRules.KhongLui(ChatState.DaGui, ChatState.Hong));
        Assert.False(ChatRules.KhongLui(ChatState.DaXem, ChatState.Hong));
    }

    [Fact]
    public void Tin_dang_cho_thi_hong_duoc()
        => Assert.True(ChatRules.KhongLui(ChatState.Cho, ChatState.Hong));
}
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

Mong đợi: **FAIL** — `error CS0117: 'ChatRules' does not contain a definition for 'KhongLui'`.

- [ ] **Bước 3: Viết luật**

`Services/Chat/Inbox/ChatRules.cs`, thêm vào cuối class:

```csharp
    /// <summary>
    /// Có được cập nhật trạng thái tin từ <paramref name="dangCo"/> sang <paramref name="moi"/> không.
    ///
    /// <para><b>Chỉ tiến, không lùi.</b> Nền tảng không bảo đảm thứ tự webhook: "đã nhận" hoàn toàn
    /// có thể tới sau "đã xem". Ghi đè mù thì dấu tích chạy ngược trước mắt nhân viên.</para>
    ///
    /// <para><b>Hỏng KHÔNG phải mức cao nhất</b> dù số lớn nhất: gửi được rồi mà báo hỏng là vô
    /// nghĩa. Chỉ tin còn đang chờ mới hỏng được.</para>
    /// </summary>
    public static bool KhongLui(ChatState dangCo, ChatState moi)
    {
        if (moi == ChatState.Hong) return dangCo == ChatState.Cho;
        if (dangCo == ChatState.Hong) return false;   // đã hỏng thì không tự sống lại
        return (short)moi > (short)dangCo;
    }
```

- [ ] **Bước 4: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

Mong đợi: **Passed — 10 test** (4+3 từ hai Theory, 3 Fact).

- [ ] **Bước 5: Commit**

```bash
git add Services/Chat/Inbox/ChatRules.cs TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs
git commit -m "feat(chat): luật không lùi trạng thái tin nhắn

Nền tảng không bảo đảm thứ tự webhook — 'đã nhận' hoàn toàn có thể tới sau
'đã xem'. Ghi đè mù thì dấu tích chạy ngược trước mắt nhân viên.

Hỏng tuy số lớn nhất nhưng KHÔNG phải mức cao nhất: gửi được rồi thì báo hỏng
là vô nghĩa, chỉ tin còn đang chờ mới hỏng được.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 3.2: Lưu mã tin của nền tảng

**Files:**
- Modify: `Services/Chat/Inbox/ChatRepository.cs` (sau `SetMessageStateAsync`, ~dòng 297)
- Modify: `Services/Chat/Inbox/ChatOutboxWorker.cs` (nhánh `if (kq.Ok)` trong `MotDongAsync`, ~dòng 110)
- Test: `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs`

**Interfaces:**
- Consumes: `SendResult(bool Ok, bool ThuLai, string? ExternalMsgId, string? Error)` — **cả ba adapter đã trả mã**: Zalo qua `TraVeSauKhiGuiAsync`, Telegram `o["result"]["message_id"]`, Messenger trong `GuiAsync`
- Produces: `Task SetExternalMsgIdAsync(string tenant, long messageId, string? maNenTang, CancellationToken ct = default)`

> **Đây là gốc của cả đợt.** Nền tảng báo "đã nhận/đã xem" bằng **mã tin của nó**. Không lưu mã thì không có gì đối chiếu, Task 3.3 và 3.4 đều vô nghĩa. Cột `external_msg_id` đã có sẵn trong `chat_messages` (đang dùng cho tin ĐẾN để chống trùng); đợt này dùng nốt cho tin ĐI — **không cần đổi schema**.

⚠️ **Lệnh RIÊNG, KHÔNG gộp vào `SetMessageStateAsync`.** Trạng thái đổi nhiều lần trong đời một tin (gửi → nhận → xem), mã nền tảng chỉ ghi đúng một lần. Gộp thì lần cập nhật nào quên truyền mã sẽ **xoá mất mã** bằng `null`.

- [ ] **Bước 1: Viết test đỏ**

Thêm vào `ChatLifecycleTests.cs`:

```csharp
    [Fact]
    public void Worker_gui_xong_phai_luu_ma_tin_cua_nen_tang()
    {
        // Không có CI chạy PostgreSQL nên canh ở mức mã nguồn. Mã tin nền tảng là thứ DUY NHẤT
        // đối chiếu được khi nền tảng báo lại — vứt đi là cả vòng đời tin vô nghĩa.
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatOutboxWorker.cs");
        Assert.Contains("SetExternalMsgIdAsync", src);
        Assert.Contains("kq.ExternalMsgId", src);
    }

    [Fact]
    public void Ghi_ma_tin_la_lenh_rieng_khong_gop_vao_doi_trang_thai()
    {
        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatRepository.cs");
        Assert.Contains("public async Task SetExternalMsgIdAsync", repo);
        Assert.DoesNotContain(
            "SetMessageStateAsync(string tenant, long messageId, ChatState tt, string? loi, string? maNenTang",
            repo);
    }
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

Mong đợi: **FAIL 2**.

- [ ] **Bước 3: Thêm hàm vào repository**

`Services/Chat/Inbox/ChatRepository.cs`, NGAY SAU `SetMessageStateAsync`:

```csharp
    /// <summary>
    /// Ghi mã tin của nền tảng cho tin MÌNH GỬI, sau khi gửi thành công.
    ///
    /// <para>Đây là thứ duy nhất đối chiếu được khi nền tảng báo lại "đã nhận"/"đã xem".
    /// Không lưu thì mọi báo lại đều không biết là của tin nào.</para>
    ///
    /// <para><b>Lệnh RIÊNG, cố ý không gộp vào <see cref="SetMessageStateAsync"/></b>: trạng thái
    /// đổi nhiều lần trong đời một tin, còn mã nền tảng chỉ ghi đúng một lần. Gộp lại thì lần cập
    /// nhật trạng thái nào quên truyền mã sẽ xoá mất mã bằng <c>null</c>.</para>
    /// </summary>
    public async Task SetExternalMsgIdAsync(string tenant, long messageId, string? maNenTang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(maNenTang)) return;   // kênh không trả mã — không có gì để ghi
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_messages SET external_msg_id = @ma
             WHERE id = @id AND tenant_id = @tenant AND external_msg_id IS NULL
            """, new { id = messageId, tenant, ma = maNenTang });
    }
```

> `external_msg_id IS NULL` để không đè lên mã đã ghi — gửi lại sau lỗi tạm thời có thể chạy hàm này hai lần.

- [ ] **Bước 4: Gọi từ worker**

`Services/Chat/Inbox/ChatOutboxWorker.cs`, trong `MotDongAsync`, thay:

```csharp
        if (kq.Ok)
        {
            await repo.FinishOutboxAsync(r.Id, true, false, null, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.DaGui, null, ct);
            return;
        }
```

bằng:

```csharp
        if (kq.Ok)
        {
            await repo.FinishOutboxAsync(r.Id, true, false, null, ct);
            await repo.SetMessageStateAsync(r.TenantId, r.MessageId, ChatState.DaGui, null, ct);
            // Mã tin của nền tảng — thứ duy nhất đối chiếu được khi nó báo lại "đã nhận"/"đã xem".
            // Telegram không bao giờ báo lại (Bot API không có), nhưng vẫn lưu: rẻ, và khi cần truy
            // vết một tin cụ thể trên nền tảng thì đúng cái mã này là thứ dán vào công cụ của họ.
            await repo.SetExternalMsgIdAsync(r.TenantId, r.MessageId, kq.ExternalMsgId, ct);
            return;
        }
```

- [ ] **Bước 5: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

Mong đợi: **Passed — 12 test**.

- [ ] **Bước 6: Build**

```bash
# dừng app trước nếu đang chạy, không thì MSB3027
dotnet build TourkitAiProxy.csproj
```

- [ ] **Bước 7: Commit**

```bash
git add Services/Chat/Inbox/ChatRepository.cs Services/Chat/Inbox/ChatOutboxWorker.cs TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs
git commit -m "feat(chat): lưu mã tin của nền tảng sau khi gửi

Hợp đồng SendResult đã mang ExternalMsgId và cả ba adapter đều trả về, nhưng
worker vứt đi — nên nền tảng báo 'đã nhận/đã xem' thì không có gì đối chiếu.

Ghi mã là lệnh RIÊNG, cố ý không gộp vào đổi trạng thái: gộp thì lần cập nhật
nào quên truyền mã sẽ xoá mất mã bằng null.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 3.3: `SeenMarker` → mốc trạng thái có thời điểm

**Files:**
- Modify: `Services/Chat/Inbox/ChatModels.cs:39-49`
- Modify: `Services/Chat/Channels/ZaloChatAdapter.cs:159`
- Modify: `Services/Chat/Inbox/ChatInboundService.cs:64-65`
- Modify: `Services/Chat/Inbox/ChatRepository.cs`
- Test: `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs`

**Interfaces:**
- Consumes: `ChatRules.KhongLui` (Task 3.1), `ChatState`
- Produces:
  - `public record StateWatermark(ChatState TrangThai, DateTime DenLuc)` trong `ChatModels.cs`
  - `InboundChatEvent` thêm `StateWatermark? Moc = null`, **bỏ** `string? SeenMarker`
  - `Task<int> MarkStateWatermarkAsync(string tenant, long conversationId, ChatState moi, DateTime denLuc, CancellationToken ct = default)` — trả số dòng đã đổi

> **Vì sao đổi kiểu chứ không thêm cờ thứ hai.** `SeenMarker` là `string?` mang đúng giá trị `"seen"`: chỉ nói được "đã xem", không nói được "đã nhận", và **không mang thời điểm**. Mà cả Messenger lẫn Zalo đều báo theo **mốc nước**: "mọi tin gửi TRƯỚC thời điểm này đã đọc". Thiếu mốc thì hoặc đánh dấu cả hội thoại (sai — tin gửi sau đó cũng bị coi là đã xem), hoặc không đánh dấu gì.

- [ ] **Bước 1: CodeGraph trước khi sửa** (bắt buộc theo CLAUDE.md)

```bash
codegraph impact SeenMarker
```

Mong đợi blast-radius nhỏ, đúng 3 chỗ: `ChatModels.cs:49` (khai báo) · `ZaloChatAdapter.cs:159` (sinh ra) · `ChatInboundService.cs:65` (đọc). **Rộng hơn thế thì dừng, báo người dùng.**

- [ ] **Bước 2: Viết test đỏ**

Thêm vào `ChatLifecycleTests.cs`:

```csharp
    [Fact]
    public void Zalo_bao_da_xem_thi_sinh_moc_co_thoi_diem()
    {
        var adapter = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/ZaloChatAdapter.cs");
        // Mốc phải mang THỜI ĐIỂM: nền tảng báo kiểu "mọi tin trước lúc này đã đọc". Không có mốc
        // thì hoặc đánh dấu cả hội thoại (sai), hoặc không đánh dấu gì.
        Assert.Contains("Watermark: new(ChatState.DaXem", adapter);
        Assert.DoesNotContain("SeenMarker", adapter);
    }

    [Fact]
    public void Moc_khong_con_bi_vut_di()
    {
        // Trước đây: `if (e.SeenMarker is not null) return;` — bóc ra rồi bỏ.
        var svc = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatInboundService.cs");
        Assert.Contains("MarkStateWatermarkAsync", svc);
        Assert.DoesNotContain("SeenMarker", svc);
    }

    [Fact]
    public void Danh_dau_moc_chi_dung_cho_tin_MINH_gui()
    {
        // "Khách đã xem" nói về tin CỦA MÌNH. Quên kẹp direction thì tin của chính khách cũng bị
        // đánh dấu, vô nghĩa và làm hỏng bộ đếm chưa đọc.
        var repo = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatRepository.cs");
        var i = repo.IndexOf("MarkStateWatermarkAsync", StringComparison.Ordinal);
        Assert.True(i > 0, "chưa có MarkStateWatermarkAsync");
        var than = repo.Substring(i, Math.Min(900, repo.Length - i));
        Assert.Contains("direction = 1", than);
        Assert.Contains("created_utc <=", than);
    }
```

- [ ] **Bước 3: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

Mong đợi: **FAIL 3**.

- [ ] **Bước 4: Đổi kiểu trong `ChatModels.cs`**

Thay dòng cuối record `InboundChatEvent` (`string? SeenMarker = null);`) bằng:

```csharp
    StateWatermark? Moc = null);

/// <summary>
/// Nền tảng báo lại trạng thái tin MÌNH đã gửi, theo kiểu <b>mốc nước</b>: mọi tin gửi trước
/// <paramref name="DenLuc"/> đều đã đạt <paramref name="TrangThai"/>.
///
/// <para>Thay cho <c>SeenMarker</c> cũ (chuỗi <c>"seen"</c>): chuỗi đó chỉ nói được "đã xem",
/// không nói được "đã nhận", và không mang thời điểm — mà thiếu thời điểm thì hoặc đánh dấu cả
/// hội thoại (sai: tin gửi sau đó cũng bị coi là đã xem), hoặc không đánh dấu gì.</para>
/// </summary>
public record StateWatermark(ChatState TrangThai, DateTime DenLuc);
```

Sửa luôn chú thích `<param name="IsEcho">` phía trên nếu nó nhắc tới `SeenMarker`.

- [ ] **Bước 5: Sửa Zalo adapter**

`Services/Chat/Channels/ZaloChatAdapter.cs` dòng 159, thay:

```csharp
                ra.Add(new(ChatChannel.Zalo, uid0!, null, ChatKind.Chu, null, null, luc, SeenMarker: "seen"));
```

bằng:

```csharp
                ra.Add(new(ChatChannel.Zalo, uid0!, null, ChatKind.Chu, null, null, luc,
                    Watermark: new(ChatState.DaXem, luc)));
```

- [ ] **Bước 6: Thêm `MarkStateWatermarkAsync`**

`Services/Chat/Inbox/ChatRepository.cs`, NGAY SAU `SetExternalMsgIdAsync`:

```csharp
    /// <summary>
    /// Nền tảng báo mọi tin gửi trước <paramref name="denLuc"/> đã đạt <paramref name="moi"/>.
    /// Trả về số dòng thật sự đổi.
    /// </summary>
    /// <remarks>
    /// <para><b>Chỉ tin MÌNH GỬI</b> (<c>direction = 1</c>): "khách đã xem" nói về tin của mình.
    /// Quên kẹp thì tin của chính khách cũng bị đánh dấu — vô nghĩa, và làm hỏng bộ đếm chưa đọc.</para>
    /// <para><b>Chỉ tiến, không lùi</b> (<c>state &lt; @moi</c>): nền tảng không bảo đảm thứ tự.
    /// Luật đầy đủ ở <see cref="ChatRules.KhongLui"/>; ở đây chặn ngay trong SQL vì cập nhật hàng
    /// loạt không đọc từng dòng ra được.</para>
    /// <para><b>Bỏ qua tin hỏng</b> (<c>state &lt;&gt; 4</c>): tin gửi hỏng thì không thể được xem.</para>
    /// </remarks>
    public async Task<int> MarkStateWatermarkAsync(string tenant, long conversationId, ChatState moi,
        DateTime denLuc, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_messages
               SET state = @moi
             WHERE tenant_id = @tenant AND conversation_id = @conv
               AND direction = 1
               AND created_utc <= @denLuc
               AND state < @moi AND state <> 4
            """, new { tenant, conv = conversationId, moi = (short)moi, denLuc });
    }
```

- [ ] **Bước 7: Xử lý mốc trong `ChatInboundService`**

Thay:

```csharp
        // "Khách đã xem" — không phải tin nhắn, chỉ ghi mốc.
        if (e.SeenMarker is not null) return;
```

bằng:

```csharp
        // Nền tảng báo trạng thái tin MÌNH đã gửi — không phải tin mới, xử lý xong là về.
        // Trước đây chỗ này bóc ra rồi BỎ, nên tin gửi đi dừng mãi ở "đã gửi" dù giao diện đã vẽ
        // sẵn dấu tích hai mức.
        if (e.Watermark is { } moc)
        {
            var soDong = await _repo.MarkStateWatermarkAsync(tenantId, hoiThoai.Id, moc.State, moc.UpToUtc, ct);
            _log.LogDebug("[chat] mốc {TT} tới {Luc:o} — đổi {N} tin, hội thoại {H}",
                moc.State, moc.UpToUtc, soDong, hoiThoai.Id);
            return;
        }
```

- [ ] **Bước 8: Chạy TOÀN BỘ test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```

Đổi kiểu có thể làm đỏ test cũ. Nếu `ChatAttachmentTests` hay test khác dùng `SeenMarker` thì sửa sang kiểu mới.

- [ ] **Bước 9: Build + commit**

```bash
dotnet build TourkitAiProxy.csproj
git add Services/Chat/Inbox/ChatModels.cs Services/Chat/Channels/ZaloChatAdapter.cs Services/Chat/Inbox/ChatRepository.cs Services/Chat/Inbox/ChatInboundService.cs TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs
git commit -m "feat(chat): mốc 'khách đã xem' của Zalo cập nhật thật vào tin

Trước đây adapter bóc ra rồi ChatInboundService bỏ thẳng, nên tin gửi đi dừng
mãi ở 'đã gửi' dù giao diện đã vẽ sẵn dấu tích hai mức.

SeenMarker (chuỗi 'seen') đổi thành StateWatermark có thời điểm: nền tảng báo theo
kiểu mốc nước. Thiếu thời điểm thì hoặc đánh dấu cả hội thoại, hoặc không đánh
dấu gì.

Chỉ đụng tin MÌNH gửi; quên kẹp thì tin của khách cũng bị đánh dấu, làm hỏng bộ
đếm chưa đọc.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 3.4: Messenger bóc `delivery` và `read`

**Files:**
- Modify: `Services/Chat/Channels/MessengerChatAdapter.cs:122-126`
- Test: `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs`

**Interfaces:**
- Consumes: `StateWatermark` (Task 3.3)
- Produces: `Parse` trả thêm sự kiện có `Moc`

> **Hình dạng thật của webhook Meta.** Trong `entry[].messaging[]`, ngoài `message` còn có:
> - `{"delivery": {"mids": [...], "watermark": 1700000000000}}` — mọi tin gửi trước `watermark` đã tới máy khách
> - `{"read": {"watermark": 1700000000000}}` — mọi tin gửi trước `watermark` đã được đọc
>
> Cả hai là **mili giây** kể từ epoch, giống `m["timestamp"]`. Dùng `watermark` chứ **không** dùng `mids`: gói `read` không có `mids`, đi chung một đường thì ít code hơn và hai loại không lệch hành vi.
>
> ⚠️ **Người gửi ở hai gói này là KHÁCH** (`sender.id` = khách, `recipient.id` = Trang) — ngược với tin echo. Lấy nhầm là đánh dấu vào hội thoại của chính Trang mình, tức là không hội thoại nào cả.

- [ ] **Bước 1: Viết test đỏ**

```csharp
    [Fact]
    public void Messenger_boc_duoc_delivery_va_read()
    {
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/MessengerChatAdapter.cs");
        Assert.Contains("\"delivery\"", src);
        Assert.Contains("\"read\"", src);
        Assert.Contains("watermark", src);
        Assert.DoesNotContain("delivery/read/postback — chưa dùng ở đợt này", src);
    }
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

- [ ] **Bước 3: Bóc hai gói mới**

Thay:

```csharp
                if (m is null) continue;
                var msg = m["message"];
                if (msg is null) continue;   // delivery/read/postback — chưa dùng ở đợt này
```

bằng:

```csharp
                if (m is null) continue;

                // Meta báo trạng thái tin MÌNH đã gửi bằng hai gói riêng, không nằm trong "message":
                //   delivery: {"mids":[…], "watermark": <ms>}  — đã tới máy khách
                //   read:     {"watermark": <ms>}              — khách đã đọc
                // Dùng watermark chứ không dùng mids: "read" không có mids, đi chung một đường thì
                // ít code hơn và hai loại không lệch hành vi.
                //
                // ⚠️ Người gửi ở hai gói này là KHÁCH (ngược với tin echo). Lấy nhầm recipient là
                // đánh dấu vào hội thoại của chính Trang mình — tức là không hội thoại nào cả.
                var tt = m["delivery"] is not null ? ChatState.DaNhan
                       : m["read"] is not null ? ChatState.DaXem
                       : (ChatState?)null;
                if (tt is { } trangThai)
                {
                    var uidM = m["sender"]?["id"]?.ToString();
                    var wm = m[trangThai == ChatState.DaNhan ? "delivery" : "read"]?["watermark"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidM) && long.TryParse(wm, out var wms))
                    {
                        var mocLuc = DateTimeOffset.FromUnixTimeMilliseconds(wms).UtcDateTime;
                        ra.Add(new(ChatChannel.Messenger, uidM!, null, ChatKind.Chu, null, null,
                            mocLuc, Watermark: new(trangThai, mocLuc)));
                    }
                    continue;
                }

                var msg = m["message"];
                if (msg is null) continue;   // postback, opt-in… — chưa dùng
```

- [ ] **Bước 4: Chạy TOÀN BỘ test + build**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
dotnet build TourkitAiProxy.csproj
```

- [ ] **Bước 5: Commit**

```bash
git add Services/Chat/Channels/MessengerChatAdapter.cs TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs
git commit -m "feat(chat): Messenger báo đã nhận / đã xem

Trước đây mọi gói không phải 'message' đều bị bỏ qua, nên tin gửi qua Messenger
dừng mãi ở 'đã gửi'.

Dùng watermark chứ không dùng mids: gói 'read' không có mids, đi chung một đường
thì ít code hơn và hai loại không lệch hành vi.

Người gửi ở hai gói này là KHÁCH, ngược với tin echo — lấy nhầm là đánh dấu vào
hội thoại của chính Trang mình.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 3.5: Telegram — nói thật thay vì hứa hão

**Files:**
- Modify: `Services/Chat/Channels/TelegramChatAdapter.cs` (chú thích class)
- Modify: `wwwroot/pages/chat-inbox.jsx` (`DauGui` dòng 110, `BongBong` dòng 171 và 190, chỗ render dòng 779)
- Test: `TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs`

**Interfaces:**
- Consumes: `ChatChannel` — `Zalo=0, Messenger=1, Web=2, Telegram=3`
- Produces: —

> Sau Task 3.3 và 3.4, tin Zalo/Messenger leo lên hai tích còn tin Telegram **mãi** một tích — không phải khách chưa nhận, mà vì **Bot API không cho biết**. Nhân viên nhìn hai hội thoại cạnh nhau sẽ kết luận sai "khách Telegram không đọc tin": hiểu nhầm **do mình tạo ra**, tệ hơn là không hiện gì.
>
> Chữa **không phải** bằng cách tự nhảy state lên 2 khi gửi xong — như thế là nói dối. Chữa bằng **nói thật**.

- [ ] **Bước 1: Viết test đỏ**

```csharp
    [Fact]
    public void Telegram_khong_duoc_tu_nhay_trang_thai()
    {
        // Bot API không có báo đã nhận/đã xem. Tự đặt DaNhan khi gửi xong là NÓI DỐI nhân viên.
        var src = ChatSchemaGuardTests.DocFile("Services/Chat/Channels/TelegramChatAdapter.cs");
        Assert.DoesNotContain("ChatState.DaNhan", src);
        Assert.DoesNotContain("ChatState.DaXem", src);
        // Phải có chú thích giải thích, không thì người sau tưởng là thiếu sót rồi "sửa".
        Assert.Contains("không báo", src);
    }

    [Fact]
    public void Giao_dien_noi_ro_kenh_nao_khong_bao_lai()
    {
        var jsx = ChatSchemaGuardTests.DocFile("wwwroot/pages/chat-inbox.jsx");
        Assert.Contains("kênh này không báo", jsx);
    }
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatLifecycle"
```

- [ ] **Bước 3: Chú thích trong Telegram adapter**

Thêm vào khối `<summary>` của class:

```csharp
/// <para><b>Telegram KHÔNG báo đã nhận / đã xem.</b> Bot API không cung cấp — khác hẳn Zalo
/// (<c>user_seen_message</c>) và Messenger (<c>delivery</c>/<c>read</c>). Nên tin gửi qua kênh này
/// dừng ở "đã gửi" vĩnh viễn, và <b>đó là đúng</b>. Đừng "sửa" bằng cách tự đặt trạng thái cao hơn
/// khi gửi thành công: như thế là nói dối nhân viên rằng khách đã nhận trong khi mình không biết.
/// Giao diện nói rõ ở tooltip dấu tích (xem <c>DauGui</c> trong chat-inbox.jsx).</para>
```

- [ ] **Bước 4: Giao diện nói rõ**

⚠️ **Kênh phải lấy từ HỘI THOẠI, không phải từ tin.** Bảng `chat_messages` có cột `channel` nhưng lớp `ChatMessage` ([ChatModels.cs:94-107](../../../Services/Chat/Inbox/ChatModels.cs)) **không map cột đó**, nên API không trả về — viết `tin.channel` sẽ ra `undefined` và mọi tin bị coi là Zalo (kênh 0).

`wwwroot/pages/chat-inbox.jsx`, thay `DauGui`:

```jsx
  // Telegram không bao giờ báo lại đã nhận/đã xem (Bot API không có). Không nói rõ thì nhân viên
  // nhìn hai hội thoại cạnh nhau sẽ kết luận sai "khách Telegram không đọc tin" — hiểu nhầm do
  // MÌNH tạo ra, tệ hơn là không hiện gì.
  function DauGui({ state, kenh }) {
    if (state === 0) return <span className="ci-tich cho">đang gửi…</span>;
    if (state === 4) return null;   // lỗi có dòng riêng, màu đỏ, không nhét vào đây
    const khongBao = kenh === 3;    // Telegram
    const nhan = khongBao
      ? 'Đã gửi — kênh này không báo lại việc khách đã nhận hay đã xem'
      : state >= 3 ? 'Khách đã xem' : state === 2 ? 'Đã tới máy khách' : 'Đã gửi';
    return (
      <span className={'ci-tich' + (state >= 3 && !khongBao ? ' xem' : '')} title={nhan} aria-label={nhan}>
        <window.Icon name="check" size={11} stroke={2.6} />
        {state >= 2 && !khongBao && <window.Icon name="check" size={11} stroke={2.6} />}
      </span>
    );
  }
```

`BongBong` nhận thêm tham số (dòng 171):

```jsx
  function BongBong({ tin, kenh }) {
```

Dòng 190 trong `BongBong`:

```jsx
            {cuaMinh && <DauGui state={tin.state} kenh={kenh} />}
```

Chỗ render (dòng 779) — `v` là hội thoại đang mở, `v.channel` đã có sẵn, cùng thứ mà `<ThanhCuaSo kenh={v.channel} />` phía trên đang dùng:

```jsx
                      <BongBong tin={m} kenh={v.channel} />
```

- [ ] **Bước 5: Chạy test + dựng bundle**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
.\build-frontend.ps1
```

Bắt buộc dựng bundle: `wwwroot/dist/` đã tồn tại nên app chạy chế độ bundle, không dựng lại thì sửa `.jsx` **không có tác dụng**.

- [ ] **Bước 6: Commit**

```bash
git add Services/Chat/Channels/TelegramChatAdapter.cs wwwroot/pages/chat-inbox.jsx TourkitAiProxy.Tests/Chat/ChatLifecycleTests.cs
git commit -m "feat(chat): nói rõ Telegram không báo đã nhận/đã xem

Sau khi Zalo và Messenger leo lên hai tích, tin Telegram mãi một tích — không
phải khách chưa nhận mà vì Bot API không cho biết. Nhân viên nhìn hai hội thoại
cạnh nhau sẽ kết luận sai: hiểu nhầm do mình tạo ra, tệ hơn là không hiện gì.

Chữa bằng cách NÓI THẬT (tooltip riêng), không phải tự nhảy trạng thái khi gửi
xong — cái đó là nói dối. Kênh lấy từ hội thoại vì ChatMessage không map cột
channel, viết tin.channel sẽ ra undefined.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 3.6: Kiểm tay trên staging + tài liệu

**Files:** `CLAUDE.md`, `CHANGELOG.md`

> Không có CI chạy PostgreSQL, nên đây là lần **duy nhất** vòng đời tin được kiểm bằng dữ liệu thật. Năm task trên chỉ có test guard mã nguồn — chúng chặn được việc xoá mất code, **không** chứng minh được SQL chạy đúng.

- [ ] **Bước 1: Lấy phiên staging** (không dùng script SQL trực tiếp — hay bị chặn; dùng đường admin của chính app)

```bash
dotnet run --project TourkitAiProxy.csproj
# POST /api/v1/admin/auth/login  {username,password} lấy từ Admin:Users trong appsettings.json
#      (KHÔNG in mật khẩu ra màn hình)
# GET  /api/v1/admin/ui/tk-sessions  header X-Admin-Session: <token>
# CHỌN ĐÚNG dòng tenantId = "staging.tourkit.vn"
```

⚠️ Bảng phiên có cả `erp.tourkit.vn` — **dữ liệu thật, cấm ghi**. Chọn nhầm dòng là ghi vào công ty thật.

- [ ] **Bước 2: Kiểm mã tin nền tảng được lưu**

Gửi một tin qua `/chat-inbox` rồi đọc lại:

```bash
curl -s "http://localhost:5080/api/v1/chat/conversations/<ID>" -H "X-Session-Id: <SID>"
```

Mong đợi: tin vừa gửi có `externalMsgId` khác `null`. Nếu `null` → Task 3.2 chưa chạy đúng, xem log `[chat/outbox]`.

- [ ] **Bước 3: Kiểm luật không lùi bằng webhook giả** (nếu nối được Trang Facebook thật)

Bơm `read` trước, `delivery` sau → trạng thái phải **giữ nguyên** ở 3, không tụt về 2.

**Nếu chưa nối được Trang nào: ĐỪNG BỊA KẾT QUẢ.** Ghi rõ "chưa kiểm được trên nền tảng thật vì chưa nối Trang Facebook" vào commit và báo người dùng. Luật đã có test thuần ở Task 3.1 — đó là mức bảo đảm thật sự đang có, nói đúng như thế.

- [ ] **Bước 4: Dọn sạch dữ liệu thử.** Ghi lại đã xoá những gì.

- [ ] **Bước 5: CLAUDE.md** — thêm vào mục "Hộp thư chat đa kênh", sau đoạn "Đường đi":

```markdown
**Vòng đời tin gửi đi:** `chờ → đã gửi → đã nhận → đã xem`, cập nhật qua
[`ChatRepository.MarkStateWatermarkAsync`](Services/Chat/Inbox/ChatRepository.cs) và **chỉ tiến, không lùi**
([`ChatRules.KhongLui`](Services/Chat/Inbox/ChatRules.cs), có test) — nền tảng không bảo đảm thứ tự
webhook, "đã nhận" hoàn toàn có thể tới sau "đã xem", ghi đè mù thì dấu tích chạy ngược trước mắt
nhân viên. Mã tin của nền tảng lưu vào `chat_messages.external_msg_id` ngay khi gửi được — thứ duy
nhất đối chiếu được khi nền tảng báo lại.

⚠️ **Ba kênh báo lại khác nhau, đừng áp một luật:** Zalo `user_seen_message` (chỉ "đã xem") ·
Messenger `delivery` + `read` (đủ hai mức, theo **mốc nước**: mọi tin trước thời điểm đó) ·
**Telegram KHÔNG báo gì cả** — Bot API không có, nên tin Telegram dừng ở "đã gửi" vĩnh viễn và
**đó là đúng**. Đừng "sửa" bằng cách tự nhảy trạng thái khi gửi xong: như thế là nói dối nhân viên.
Giao diện nói rõ ở tooltip dấu tích.
```

- [ ] **Bước 6: CHANGELOG.md** — mục mới trên cùng:

```markdown
## Phiên bản dd/MM/2026 — Biết khách đã nhận và đã đọc tin chưa

### ✨ Tính năng mới
- **Dấu tích cho biết tin đã tới đâu.** Tin bạn gửi cho khách nay hiện rõ: một dấu là đã gửi đi,
  hai dấu là đã tới máy khách, hai dấu đậm là khách đã mở đọc. Trước đây mọi tin đều dừng ở một
  dấu nên không biết khách đã thấy chưa.
- Áp dụng cho **Zalo** (báo khi khách đã đọc) và **Facebook Messenger** (báo cả hai mức).
  **Telegram** không cung cấp thông tin này nên tin gửi qua Telegram chỉ hiện "đã gửi" — di chuột
  lên dấu tích sẽ thấy giải thích, đây không phải lỗi.
```

- [ ] **Bước 7: Chạy toàn bộ + đồng bộ + commit**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
dotnet build TourkitAiProxy.csproj
codegraph sync
git add CLAUDE.md CHANGELOG.md
git commit -m "docs: ghi lại đợt 3 chat — vòng đời tin nhắn

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

Rồi dùng skill `superpowers:finishing-a-development-branch`.

---

# ĐỢT 4 — Đẩy sự kiện + phân trang con trỏ

**Mục tiêu:** bỏ hỏi-lại-4-giây; tin mới tới trong ~2 giây; hộp thư nghìn hội thoại vẫn mở được.

**Hiện trạng cần bỏ:** `chat-inbox.jsx` có `setInterval(nhip, 4000)` gọi `taiDsach()` **và** `taiChiTiet(chon)`. Mười nhân viên mở hộp thư = 300 request/phút cho thứ hầu hết thời gian không đổi. Và tin mới vẫn trễ tới 4 giây.

**Đọc §"Quyết định kiến trúc lệch khỏi spec" ở đầu tài liệu trước khi làm task này** — dùng SSE, không SignalR.

## Task 4.1: Phân trang con trỏ cho danh sách hội thoại

**Files:**
- Modify: `Services/Chat/Inbox/ChatRepository.cs:88-111` (`ListConversationsAsync`)
- Modify: `Endpoints/ChatInboxEndpoints.cs` (route `GET /conversations`)
- Modify: `wwwroot/pages/chat-inbox.jsx` (`taiDsach`)
- Test: `TourkitAiProxy.Tests/Chat/ChatPagingTests.cs` (**tạo mới**)

**Interfaces:**
- Produces:
  - `public record ConvCursor(DateTime LastActivityAt, long Id)` trong `ChatModels.cs`
  - `static string ChatCursor.Ma(ConvCursor c)` / `static ConvCursor? ChatCursor.Giai(string? s)` — hàm thuần, **đây là chỗ có test thật**
  - `ListConversationsAsync(..., ConvCursor? sau = null, ...)`
  - `GET /api/v1/chat/conversations?cursor=<mã>` → `{ items, counts, channelCounts, nextCursor }`

> **Vì sao con trỏ chứ không `OFFSET`.** Hộp thư sắp theo `last_activity_at DESC` — thứ **đổi liên tục** khi khách nhắn. Với `OFFSET 60`, chỉ cần một hội thoại nhảy lên đầu giữa hai lần tải là trang sau **lặp lại** một dòng và **bỏ sót** một dòng khác. Người dùng không thấy lỗi, chỉ thấy "hình như thiếu ai đó" — không bao giờ báo lại được.
>
> **Con trỏ phải gồm CẢ `id`**, không chỉ thời gian: hai hội thoại hoàn toàn có thể có cùng `last_activity_at` tới từng micro giây (hai webhook xử lý song song). Chỉ so thời gian thì hoặc lặp hoặc mất dòng.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatPagingTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class ChatPagingTests
{
    [Fact]
    public void Ma_roi_giai_ra_dung_nguyen_ban()
    {
        var c = new ConvCursor(new DateTime(2026, 8, 25, 10, 30, 15, 123, DateTimeKind.Utc), 4567);
        var lai = ChatCursor.Giai(ChatCursor.Ma(c));
        Assert.NotNull(lai);
        Assert.Equal(c.LastActivityAt, lai!.LastActivityAt);
        Assert.Equal(c.Id, lai.Id);
    }

    [Fact]
    public void Moc_thoi_gian_giu_dung_UTC()
    {
        // Mất Kind=Utc là lệch 7 tiếng — trang sau bắt đầu sai chỗ, người dùng thấy thiếu hội thoại.
        var c = new ConvCursor(new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc), 1);
        var lai = ChatCursor.Giai(ChatCursor.Ma(c))!;
        Assert.Equal(DateTimeKind.Utc, lai.LastActivityAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("khong-phai-ma-hop-le")]
    [InlineData("!!!@@@")]
    public void Ma_hong_thi_tra_null_chu_khong_nem(string? tho)
    {
        // Con trỏ nằm trên URL — người dùng sửa tay, hoặc mã cũ từ bản trước. Ném là cả trang trắng.
        Assert.Null(ChatCursor.Giai(tho));
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatPaging"
```

Mong đợi: **FAIL** — không có `ConvCursor` / `ChatCursor`.

- [ ] **Bước 3: Viết `ConvCursor` + `ChatCursor`**

Thêm vào cuối `Services/Chat/Inbox/ChatModels.cs`:

```csharp
/// <summary>Vị trí đọc tiếp trong danh sách hội thoại (sắp theo <c>last_activity_at DESC, id DESC</c>).</summary>
/// <param name="Id">BẮT BUỘC có, không chỉ mốc thời gian: hai hội thoại hoàn toàn có thể cùng
/// <c>last_activity_at</c> tới từng micro giây (hai webhook xử lý song song). Chỉ so thời gian thì
/// hoặc lặp một dòng, hoặc mất một dòng — và người dùng không bao giờ báo lại được lỗi kiểu đó.</param>
public record ConvCursor(DateTime LastActivityAt, long Id);

/// <summary>
/// Mã hoá con trỏ thành chuỗi đi trên URL. Hàm thuần — đây là chỗ có test thật.
/// </summary>
public static class ChatCursor
{
    public static string Ma(ConvCursor c)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
               $"{c.LastActivityAt.Ticks}|{c.Id}"))
           .TrimEnd('=').Replace('+', '-').Replace('/', '_');   // base64url — đi trên URL không phải escape

    /// <summary>Mã hỏng → <c>null</c>, KHÔNG ném: con trỏ nằm trên URL nên người dùng sửa tay được,
    /// và mã cũ từ bản trước vẫn có thể còn trong lịch sử trình duyệt. Ném là cả trang trắng.</summary>
    public static ConvCursor? Giai(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var b = s.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var phan = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b)).Split('|');
            if (phan.Length != 2) return null;
            if (!long.TryParse(phan[0], out var ticks) || !long.TryParse(phan[1], out var id)) return null;
            return new(new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch { return null; }
    }
}
```

- [ ] **Bước 4: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatPaging"
```

Mong đợi: **Passed — 7 test**.

- [ ] **Bước 5: Dùng con trỏ trong truy vấn**

`Services/Chat/Inbox/ChatRepository.cs`, `ListConversationsAsync`: thêm tham số `ConvCursor? sau = null`, thêm vào `WHERE`:

```sql
              AND (@sauLuc IS NULL OR (c.last_activity_at, c.id) < (@sauLuc, @sauId))
```

và đổi `ORDER BY` thành `ORDER BY c.last_activity_at DESC, c.id DESC`.

> `(a, b) < (x, y)` là **so sánh bộ** của PostgreSQL — đúng ngữ nghĩa "đứng sau trong thứ tự đã sắp", và dùng được chỉ mục ghép. Viết tay `a < x OR (a = x AND b < y)` cũng đúng nhưng dài và dễ sai dấu.

Thêm chỉ mục vào `ChatDb.SchemaSql` (**sau** mọi `ALTER TABLE`, xem Global Constraints):

```sql
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_hoatdong
      ON chat_conversations (tenant_id, last_activity_at DESC, id DESC);
```

- [ ] **Bước 6: Endpoint trả `nextCursor`**

`Endpoints/ChatInboxEndpoints.cs`, route `GET /conversations`: nhận `string? cursor`, gọi `ChatCursor.Giai(cursor)`, và trả thêm:

```csharp
nextCursor = ds.Count < limit ? null
           : ChatCursor.Ma(new(ds[^1].LastActivityAt, ds[^1].Id)),
```

> `ds.Count < limit` → hết dữ liệu → trả `null` để giao diện biết dừng. Luôn trả mã thì giao diện cuộn mãi không hết.

- [ ] **Bước 7: Giao diện cuộn vô hạn**

`wwwroot/pages/chat-inbox.jsx`: `taiDsach()` nhận `cursor`, nối vào `dsach` thay vì thay thế khi có cursor; thêm nút/`IntersectionObserver` ở cuối danh sách. Giữ nguyên hành vi khi đổi bộ lọc: **đổi lọc là reset con trỏ** (không thì trộn kết quả hai bộ lọc).

- [ ] **Bước 8: Test + bundle + commit**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
.\build-frontend.ps1
git add Services/Chat/Inbox/ChatModels.cs Services/Chat/Inbox/ChatRepository.cs Services/Chat/Inbox/ChatDb.cs Endpoints/ChatInboxEndpoints.cs wwwroot/pages/chat-inbox.jsx TourkitAiProxy.Tests/Chat/ChatPagingTests.cs
git commit -m "feat(chat): phân trang con trỏ cho danh sách hội thoại

OFFSET sai với danh sách sắp theo hoạt động mới nhất — thứ đổi liên tục khi
khách nhắn. Chỉ cần một hội thoại nhảy lên đầu giữa hai lần tải là trang sau
lặp một dòng và bỏ sót một dòng khác; người dùng không thấy lỗi, chỉ thấy
'hình như thiếu ai đó'.

Con trỏ gồm CẢ id, không chỉ thời gian: hai hội thoại có thể cùng
last_activity_at tới từng micro giây khi hai webhook xử lý song song.

Mã hỏng trả null chứ không ném — con trỏ nằm trên URL, người dùng sửa tay được.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 4.2: Kênh đẩy sự kiện (SSE)

**Files:**
- Create: `Services/Chat/Inbox/ChatEventBus.cs`
- Modify: `Endpoints/ChatInboxEndpoints.cs` (thêm `GET /events`, thêm vào `DuongRieng`)
- Modify: `Services/Chat/Inbox/ChatInboundService.cs`, `ChatOutboxWorker.cs` (bắn sự kiện)
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs` (đăng ký DI)
- Test: `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs` (**tạo mới**)

**Interfaces:**
- Produces:
  - `public record ChatEvent(string TenantId, long ConversationId, string Loai, long? MessageId)` — `Loai` ∈ `"tin-moi"` · `"doi-trang-thai"` · `"doi-hoi-thoai"`
  - `ChatEventBus.Bao(ChatEvent e)` — bắn, không chờ
  - `IAsyncEnumerable<ChatEvent> ChatEventBus.NgheAsync(string tenantId, CancellationToken ct)`
  - `GET /api/v1/chat/events` (SSE, cần `X-Session-Id`)

> **Kẹp theo tenant ngay trong bus**, không phải lọc ở endpoint. Lọc ở endpoint thì một lần quên là hộp thư công ty này nhận sự kiện của công ty khác — rò rỉ chéo tenant, thứ nặng nhất trong danh sách rủi ro của spec.

⚠️ **Nhiều instance:** bus trong bộ nhớ chỉ thấy sự kiện của chính instance mình. Có `Redis:ConnectionString` → dùng pub/sub (`RedisProvider` đã có trong DI). Không có Redis → **giữ nguyên hỏi-lại-4-giây làm đường lùi** và **ghi log lúc khởi động nói rõ**, đừng im lặng chạy chế độ kém hơn.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class ChatEventBusTests
{
    [Fact]
    public async Task Nghe_dung_tenant_cua_minh()
    {
        var bus = new ChatEventBus();
        using var huy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nhan = new List<ChatEvent>();

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.NgheAsync("cong-ty-A", huy.Token))
            {
                nhan.Add(e);
                if (nhan.Count == 1) break;
            }
        });

        await Task.Delay(100, huy.Token);
        bus.Bao(new("cong-ty-B", 1, "tin-moi", 10));   // KHÔNG được thấy
        bus.Bao(new("cong-ty-A", 2, "tin-moi", 20));
        await doc;

        Assert.Single(nhan);
        Assert.Equal("cong-ty-A", nhan[0].TenantId);
        Assert.Equal(2, nhan[0].ConversationId);
    }

    [Fact]
    public void Bao_khi_khong_ai_nghe_thi_khong_nem()
    {
        // Webhook chạy nền, không ai mở hộp thư là chuyện bình thường — ném ở đây là chết luồng xử lý
        // tin của khách chỉ vì không có ai đang nhìn màn hình.
        var bus = new ChatEventBus();
        bus.Bao(new("cong-ty-A", 1, "tin-moi", 1));
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatEventBus"
```

- [ ] **Bước 3: Viết `ChatEventBus`**

Tạo `Services/Chat/Inbox/ChatEventBus.cs`:

```csharp
// Services/Chat/Inbox/ChatEventBus.cs
using System.Threading.Channels;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <param name="Loai">"tin-moi" · "doi-trang-thai" · "doi-hoi-thoai".</param>
public record ChatEvent(string TenantId, long ConversationId, string Loai, long? MessageId);

/// <summary>
/// Đẩy sự kiện tới các tab đang mở hộp thư, thay cho hỏi-lại-4-giây.
///
/// <para><b>Kẹp theo tenant NGAY TRONG BUS</b>, không lọc ở endpoint: lọc ở ngoài thì một lần quên
/// là hộp thư công ty này nhận sự kiện của công ty khác.</para>
///
/// <para><b>Bắn là bỏ (fire-and-forget), có giới hạn.</b> Mỗi người nghe một hàng đợi 100 sự kiện,
/// đầy thì BỎ sự kiện mới chứ không chặn. Chặn nghĩa là một tab treo làm nghẽn cả luồng xử lý tin
/// của khách — đắt hơn nhiều so với việc một tab lỡ mất vài sự kiện rồi tự tải lại.</para>
/// </summary>
public class ChatEventBus
{
    private readonly List<(string Tenant, Channel<ChatEvent> Kenh)> _nghe = new();
    private readonly object _khoa = new();

    public void Bao(ChatEvent e)
    {
        lock (_khoa)
            foreach (var (tenant, kenh) in _nghe)
                if (tenant == e.TenantId)
                    kenh.Writer.TryWrite(e);   // TryWrite: đầy thì bỏ, KHÔNG chặn
    }

    public async IAsyncEnumerable<ChatEvent> NgheAsync(string tenantId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var kenh = Channel.CreateBounded<ChatEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,   // mất sự kiện cũ còn hơn nghẽn
        });
        lock (_khoa) _nghe.Add((tenantId, kenh));
        try
        {
            await foreach (var e in kenh.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            lock (_khoa) _nghe.RemoveAll(x => x.Kenh == kenh);
        }
    }
}
```

- [ ] **Bước 4: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatEventBus"
```

- [ ] **Bước 5: Endpoint SSE**

`Endpoints/ChatInboxEndpoints.cs` — thêm `"/api/v1/chat/events"` vào `DuongRieng` (bắt buộc, xem Global Constraints), rồi thêm vào `MapInbox`:

```csharp
        // Đẩy sự kiện thay cho hỏi-lại-4-giây. Dùng SSE chứ không SignalR — xem phần "Quyết định
        // kiến trúc" trong plan: dự án đã có sẵn SSE ở cả hai đầu, và frontend không có bundler.
        g.MapGet("/events", async (HttpContext ctx, TkSessionStore sessions, ChatEventBus bus,
            CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";   // nginx: đừng gom lại rồi mới đẩy

            await foreach (var e in bus.NgheAsync(a.TenantId, ct))
                await ctx.Response.WriteAsync(
                    $"data: {System.Text.Json.JsonSerializer.Serialize(e, Web)}\n\n", ct);
            return Results.Empty;
        });
```

- [ ] **Bước 6: Bắn sự kiện ở ba chỗ**

- `ChatInboundService.MotSuKienAsync` — sau `TouchConversationAsync` (tin khách): `_bus.Bao(new(tenantId, hoiThoai.Id, "tin-moi", id.Value));`
- `ChatInboundService` — sau `MarkStateWatermarkAsync` (Task 3.3): `"doi-trang-thai"`
- `ChatOutboxWorker.MotDongAsync` — sau `SetMessageStateAsync`: `"doi-trang-thai"`

- [ ] **Bước 7: DI**

`Services/Bootstrap/WorkflowStackRegistration.cs`, cạnh `ChatQuickReplyRepository`:

```csharp
        s.AddSingleton<Chat.Inbox.ChatEventBus>();
```

- [ ] **Bước 8: Giao diện — nghe SSE, bỏ nhịp 4 giây**

`wwwroot/pages/chat-inbox.jsx`, thay `useEffect` có `setInterval(nhip, 4000)`:

```jsx
    // Nghe sự kiện đẩy thay cho hỏi lại 4 giây một lần. EventSource tự kết nối lại khi rớt mạng.
    //
    // ⚠️ ĐÓNG khi tab ẩn: HTTP/1.1 chỉ cho 6 kết nối mỗi origin, một luồng SSE giữ mất một suất.
    // Mở nhiều tab TRAV-AI mà không đóng là các request thường bị treo — lỗi rất khó lần.
    useEffect(() => {
      if (!chon && !dsach.length) return;
      let es = null;
      const moKet = () => {
        if (document.hidden || es) return;
        es = new EventSource('/api/v1/chat/events?sessionId='
          + encodeURIComponent(window.tourkitAuth.getSessionId() || ''));
        es.onmessage = (ev) => {
          let e; try { e = JSON.parse(ev.data); } catch { return; }
          taiDsach();
          if (e.conversationId === chon) taiChiTiet(chon);
        };
        es.onerror = () => { /* EventSource tự thử lại — không đóng tay ở đây */ };
      };
      const dong = () => { if (es) { es.close(); es = null; } };
      const doiTab = () => (document.hidden ? dong() : moKet());
      moKet();
      document.addEventListener('visibilitychange', doiTab);
      return () => { document.removeEventListener('visibilitychange', doiTab); dong(); };
    }, [chon, taiDsach, taiChiTiet, dsach.length]);
```

⚠️ `EventSource` **không gửi được header tuỳ ý** — nên phiên phải đi qua query `?sessionId=`. Đã kiểm: [`SessionAuth.Read`](../../../Endpoints/SessionAuth.cs) đọc `X-Session-Id` **rồi mới** tới `Query["sessionId"]`, nên endpoint SSE chạy được **không cần sửa gì** ở lớp xác thực.

⚠️ **Không dùng `authedFetch` cho SSE.** `authedFetch` tự đăng xuất toàn cục khi gặp bất kỳ 401 nào; một luồng SSE đứt lúc phiên hết hạn sẽ **đá người dùng ra khỏi app** giữa lúc họ đang gõ dở cho khách. `EventSource` gọi thẳng và tự thử lại là đúng.

- [ ] **Bước 9: Test + bundle + kiểm tay**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
.\build-frontend.ps1
```

Kiểm tay: mở hai tab `/chat-inbox`, gửi tin ở tab 1 → tab 2 phải cập nhật **trong ~2 giây mà không có request định kỳ nào** trong tab Network.

- [ ] **Bước 10: Commit**

```bash
git add Services/Chat/Inbox/ChatEventBus.cs Services/Chat/Inbox/ChatInboundService.cs Services/Chat/Inbox/ChatOutboxWorker.cs Endpoints/ChatInboxEndpoints.cs Services/Bootstrap/WorkflowStackRegistration.cs wwwroot/pages/chat-inbox.jsx TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs
git commit -m "feat(chat): đẩy sự kiện qua SSE, bỏ hỏi lại 4 giây

Mười nhân viên mở hộp thư = 300 request/phút cho thứ hầu hết thời gian không
đổi, mà tin mới vẫn trễ tới 4 giây.

Dùng SSE chứ không SignalR: dự án đã có sẵn SSE ở cả hai đầu, frontend không có
bundler (thêm SignalR là thêm thẻ script CDN + import bundle-entry, hai danh
sách đó đã lệch nhau một lần rồi), và nhu cầu thật là một chiều.

Kẹp tenant ngay trong bus, không lọc ở endpoint — lọc ở ngoài thì một lần quên
là rò rỉ chéo tenant.

Đóng luồng khi tab ẩn: HTTP/1.1 chỉ cho 6 kết nối mỗi origin.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Task 4.3: Redis pub/sub cho nhiều instance

**Files:**
- Modify: `Services/Chat/Inbox/ChatEventBus.cs`
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs`
- Modify: `CLAUDE.md`
- Test: `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs`

> SSE giữ kết nối tới **đúng một** instance. Tin tới instance khác thì tab đang mở **không nhận được** — và triệu chứng là "thỉnh thoảng tin mới không hiện", loại lỗi chỉ xuất hiện khi đông người dùng, tức đúng lúc tệ nhất.

- [ ] **Bước 1: Test đỏ** — `ChatEventBus` nhận `RedisProvider?`, không có Redis thì chạy y như cũ:

```csharp
    [Fact]
    public void Khong_co_Redis_van_chay_nhu_cu()
    {
        // Redis là tuỳ chọn. Thiếu nó thì bus vẫn phải hoạt động trong một instance — không được
        // ném lúc khởi động, vì máy dev và VPS nhỏ thường không cắm Redis.
        var bus = new ChatEventBus(null);
        bus.Bao(new("cong-ty-A", 1, "tin-moi", 1));
    }
```

- [ ] **Bước 2** Chạy, xác nhận đỏ (`ChatEventBus` chưa nhận tham số).

- [ ] **Bước 3: Thêm nhánh Redis**

`Bao()` publish lên kênh `tkai:chat:events`; lúc khởi tạo subscribe kênh đó và đổ vào các listener nội bộ. **Tự bỏ qua sự kiện do chính instance mình publish** (kèm một id instance vào payload) — không thì mỗi sự kiện xử lý hai lần.

- [ ] **Bước 4: Log lúc khởi động nói rõ chế độ**

```csharp
_log.LogInformation("[chat/events] chế độ {C}", redis is null
    ? "MỘT INSTANCE (không có Redis — nhiều instance sau load-balancer sẽ mất sự kiện, giao diện tự lùi về hỏi lại định kỳ)"
    : "nhiều instance qua Redis pub/sub");
```

- [ ] **Bước 5: Giao diện có đường lùi.** `GET /api/v1/features` trả thêm `chatRealtime: bool`; giao diện thấy `false` thì bật lại `setInterval` 4 giây. **Không im lặng chạy chế độ kém hơn.**

- [ ] **Bước 6: Test + commit** (thông điệp nêu rõ vì sao phải bỏ qua sự kiện của chính mình).

---

# ĐỢT 5 — Cộng tác nhiều nhân viên

**Mục tiêu:** hai nhân viên mở cùng hộp thư không giẫm chân; ai làm gì có dấu vết.

**Đã có sẵn:** `chat_conversations.assigned_username`, `agent_last_read_at`, `archived_at`, endpoint claim/status/bot-pause.
**Còn thiếu:** nhận việc **nguyên tử** (hai người bấm cùng lúc), chuyển việc, chưa-đọc **theo từng người**, nhật ký thao tác.

## Task 5.1: Nhận việc nguyên tử

**Files:** `Services/Chat/Inbox/ChatRepository.cs` · `Endpoints/ChatInboxEndpoints.cs` · test guard

> Hiện tại nhận việc là `UPDATE ... SET assigned_username = @u` — **không kiểm ai đang giữ**. Hai nhân viên bấm cách nhau 100ms thì người sau **im lặng cướp việc** của người trước; cả hai đều thấy "của tôi" và cùng trả lời một khách. Khách nhận hai câu trả lời khác nhau từ một công ty.

- [ ] **Bước 1: Test đỏ** — guard: `NhanViecAsync` phải có `assigned_username IS NULL` trong `WHERE`.
- [ ] **Bước 2:** Chạy, xác nhận đỏ.
- [ ] **Bước 3:** `UPDATE ... WHERE id=@id AND tenant_id=@t AND assigned_username IS NULL` → trả số dòng; 0 dòng = **người khác nhận trước**, endpoint trả **409** kèm tên người đang giữ, không phải 200 im lặng.
- [ ] **Bước 4:** Giao diện hiện "Đang do <tên> xử lý" thay vì đổi nút, và tải lại danh sách.
- [ ] **Bước 5:** Test + bundle + commit.

## Task 5.2: Chưa đọc theo từng người

**Files:** `Services/Chat/Inbox/ChatDb.cs` (bảng mới) · `ChatRepository.cs` · endpoint · giao diện

> `agent_last_read_at` là **một cột cho cả công ty**: A mở hội thoại thì B cũng mất dấu chưa đọc. Với hộp thư một người thì không lộ; hai người trở lên là sai ngay.

- [ ] **Bước 1: Test đỏ** — guard: có bảng `chat_conversation_reads` với khoá `(tenant_id, conversation_id, username)`.
- [ ] **Bước 2:** Chạy, xác nhận đỏ.
- [ ] **Bước 3:** Thêm bảng vào `SchemaSql` (nhớ thứ tự ALTER/INDEX):

```sql
    CREATE TABLE IF NOT EXISTS chat_conversation_reads (
      tenant_id       text        NOT NULL,
      conversation_id bigint      NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
      username        text        NOT NULL,
      last_read_at    timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, conversation_id, username)
    );
```

- [ ] **Bước 4:** `MarkReadAsync` ghi theo `username` (`ON CONFLICT (tenant_id, conversation_id, username) DO UPDATE`); bộ đếm chưa đọc `LEFT JOIN` bảng này.
- [ ] **Bước 5:** **Giữ `agent_last_read_at`**, không xoá — dữ liệu cũ vẫn dùng làm mốc ban đầu cho người chưa có dòng nào. Xoá là mọi hội thoại cũ bật lại thành "chưa đọc" cho tất cả mọi người ngay sau khi deploy.
- [ ] **Bước 6:** Test + commit.

## Task 5.3: Nhật ký thao tác

**Files:** `ChatDb.cs` (bảng `chat_audit`) · `ChatRepository.cs` · các endpoint ghi · giao diện (tab trong panel hồ sơ)

> Spec §1.3: "Mọi thao tác nhạy cảm đều được phân quyền và audit". Nhận/nhả việc, đổi trạng thái, tạm dừng bot, gỡ kết nối kênh, gửi tin — hiện **không lưu dấu vết nào**. Khi khách khiếu nại "ai nói câu này với tôi" thì không tra được.

- [ ] **Bước 1: Test đỏ** — guard: bảng `chat_audit` tồn tại và endpoint nhận việc có gọi ghi nhật ký.
- [ ] **Bước 2:** Chạy, xác nhận đỏ.
- [ ] **Bước 3:** Bảng:

```sql
    CREATE TABLE IF NOT EXISTS chat_audit (
      id              bigserial PRIMARY KEY,
      tenant_id       text        NOT NULL,
      conversation_id bigint,
      username        text        NOT NULL,
      hanh_dong       text        NOT NULL,   -- nhan-viec | nha-viec | doi-trang-thai | tam-dung-bot | go-ket-noi
      chi_tiet        jsonb,
      created_utc     timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS ix_audit_conv ON chat_audit (tenant_id, conversation_id, created_utc DESC);
```

- [ ] **Bước 4:** Ghi ở các endpoint. **KHÔNG ghi nội dung tin** vào `chi_tiet` — tin đã nằm ở `chat_messages`, chép lại là nhân đôi dữ liệu khách và nhân đôi chỗ phải xoá khi khách yêu cầu xoá dữ liệu.
- [ ] **Bước 5:** `GET /api/v1/chat/conversations/{id}/audit` (thêm vào `DuongRieng`), giao diện hiện trong panel hồ sơ.
- [ ] **Bước 6:** Test + bundle + commit.

---

# ĐỢT 6 — Hồ sơ khách và CRM

**Mục tiêu:** biết đang nói chuyện với ai; ghi chú và nhãn không mất khi đổi ca.

**Đã có:** `chat_contacts` với `crm_customer_id` — nhưng **chưa dòng code nào ghi giá trị vào đó**.

## Task 6.1: Nối khách chat với khách CRM

**Files:** `Services/Chat/Inbox/ChatRepository.cs` · `Endpoints/ChatInboxEndpoints.cs` · `Services/TourKit/TourKitApiClient.cs` (chỉ đọc) · giao diện panel hồ sơ

> **Nối TAY trước, đoán tự động sau.** Ghép tự động theo tên là sai thường xuyên (trùng tên là chuyện bình thường ở khách du lịch); ghép theo số điện thoại thì Zalo/Messenger **không cho biết số** trừ khi khách tự nhắn. Nối tay đúng 100% và làm được ngay; tự động để sau khi đã có dữ liệu thật xem tỉ lệ trùng thế nào.

- [ ] **Bước 1: Test đỏ** — guard: có `NoiCrmAsync` và endpoint `POST /conversations/{id}/link-crm`.
- [ ] **Bước 2:** Chạy, xác nhận đỏ.
- [ ] **Bước 3:** `NoiCrmAsync(tenant, channel, externalId, crmCustomerId)` — `UPDATE chat_contacts SET crm_customer_id = @id`.
- [ ] **Bước 4:** Endpoint tìm khách (`GET /conversations/{id}/crm-search?q=`) gọi `/api/ai/customers` qua `TourKitApiClient` bằng phiên **của chính nhân viên** — không dùng tài khoản dịch vụ, để CRM tự chặn theo quyền của họ.
- [ ] **Bước 5:** Panel hồ sơ: ô tìm + nút "Nối", nối xong hiện tên + link sang CRM.
- [ ] **Bước 6:** Thêm đường vào `DuongRieng`. Test + bundle + commit.

## Task 6.2: Nhãn và ghi chú

**Files:** `ChatDb.cs` · `ChatRepository.cs` · endpoint · giao diện

- [ ] **Bước 1: Test đỏ** — guard hai bảng + hàm chuẩn hoá nhãn (hàm thuần, có test thật như `ChuanHoaTrigger` của mẫu trả lời nhanh).
- [ ] **Bước 2:** Chạy, xác nhận đỏ.
- [ ] **Bước 3:** Bảng:

```sql
    CREATE TABLE IF NOT EXISTS chat_contact_tags (
      tenant_id   text NOT NULL,
      channel     smallint NOT NULL,
      external_id text NOT NULL,
      tag         text NOT NULL,
      created_utc timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, channel, external_id, tag)
    );
    CREATE TABLE IF NOT EXISTS chat_contact_notes (
      id          bigserial PRIMARY KEY,
      tenant_id   text NOT NULL,
      channel     smallint NOT NULL,
      external_id text NOT NULL,
      username    text NOT NULL,
      noi_dung    text NOT NULL,
      created_utc timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS ix_note_contact
      ON chat_contact_notes (tenant_id, channel, external_id, created_utc DESC);
```

- [ ] **Bước 4:** Chuẩn hoá nhãn **dùng lại** `ChatQuickReplyRepository.ChuanHoaTrigger` (bỏ dấu, hạ chữ thường, gạch nối) — cùng vấn đề, cùng lời giải; viết lại lần hai là hai chỗ lệch nhau. Tách hàm đó ra chỗ dùng chung nếu cần.
- [ ] **Bước 5:** Endpoint CRUD + giao diện panel hồ sơ. Thêm vào `DuongRieng`.
- [ ] **Bước 6:** Test + bundle + commit.

---

# Đợt 7-10 — chưa lên bước chi tiết được, và đây là lý do

Bốn đợt trên viết được bước-bước vì mọi thứ chúng đụng tới **đã tồn tại trong code và tôi đọc được**. Bốn đợt dưới thì không, và viết bước giả ra sẽ tệ hơn là không viết — người thực thi tin vào mã mẫu sai còn mất thời gian hơn tự tìm hiểu.

| Đợt | Nội dung | Chặn ở đâu — cụ thể |
|---|---|---|
| **7. Composer nâng cao** | emoji · trả lời-vào-tin · nhiều tệp một lượt · nút theo năng lực kênh | Cần **bảng năng lực từng kênh** (kênh nào cho trả lời-vào-tin? Zalo có, Telegram có, Messenger có nhưng giới hạn khác nhau) — phải tra tài liệu chính thức của cả ba nền tảng tại thời điểm làm, spec §7 dặn rõ "không coi hành vi trong repo tham chiếu là nguồn quy định hiện hành". Chưa tra thì mọi mã mẫu đều là đoán. |
| **8. AI có trí nhớ + chính sách** | lịch sử hội thoại · ngữ cảnh CRM · chính sách theo hộp thư · chuyển cho người · vết chạy | Phụ thuộc **đợt 6** (không có `crm_customer_id` thì "ngữ cảnh CRM" không có gì để lấy). Và phải chốt trước: bao nhiêu lượt lịch sử đưa vào prompt, tóm tắt hay cắt bớt — quyết định này đổi hẳn chi phí AI mỗi tin, cần đo trên dữ liệu thật chứ không chọn bừa. |
| **9. Webchat** | widget nhúng site khách · phiên · cho phép theo tên miền | Dự án **đã có** `Services/Widget/` (token per-tenant, `WidgetChatService`, `WidgetChatCrmService`) cho trợ lý số liệu. Phải quyết trước: Webchat của hộp thư **dùng lại** hạ tầng đó hay là kênh thứ tư độc lập. Hai đường đi khác hẳn nhau và chọn sai là viết lại từ đầu. Cần đọc `Services/Widget/` kỹ rồi mới lên bước được. |
| **10. WhatsApp · Instagram · TikTok · bình luận** | thêm kênh | Mỗi kênh một hợp đồng API riêng, và **cả ba đều cần tài khoản doanh nghiệp đã duyệt** để thử. Chưa có tài khoản thì viết adapter chỉ là đoán hình dạng webhook — đúng loại lỗi mà spec §7 cảnh báo. |

**Việc cần làm trước khi lên plan cho đợt 7-10** (làm được ngay, không phụ thuộc gì):

- [ ] Tra tài liệu chính thức Zalo/Messenger/Telegram về trả lời-vào-tin và giới hạn tệp → dựng **bảng năng lực** trong `IChatChannelAdapter`
- [ ] Đo trên staging: một hội thoại thật dài bao nhiêu tin, để chốt chiến lược lịch sử cho AI
- [ ] Đọc `Services/Widget/` và quyết dùng lại hay tách
- [ ] Xác nhận công ty có tài khoản doanh nghiệp WhatsApp/Instagram chưa

---

## Thứ tự và điều kiện

```
Đợt 3 (vòng đời)  ──┬──> Đợt 4 (đẩy sự kiện + phân trang)
                    │
                    └──> Đợt 5 (cộng tác)  ──> Đợt 6 (CRM) ──> Đợt 8 (AI)
                                                    │
                                       Đợt 7, 9, 10 ┘ (sau khi gỡ được các chặn ở trên)
```

- **Đợt 3 phải xong trước đợt 4**: đợt 4 bắn sự kiện `"doi-trang-thai"`, mà trạng thái chỉ có nghĩa sau đợt 3.
- **Đợt 5 và 4 độc lập** — làm song song được nếu có hai người.
- **Đợt 6 phải xong trước đợt 8.**

**Sau MỖI task**: chạy `dotnet test` **toàn bộ**, không chỉ filter. Đổi kiểu ở đợt 3 và thêm bảng ở đợt 5-6 đều có thể làm đỏ test cũ.

**Chốt mỗi đợt** bằng skill `superpowers:finishing-a-development-branch` — gộp về `dev`, không commit thẳng `main`.
