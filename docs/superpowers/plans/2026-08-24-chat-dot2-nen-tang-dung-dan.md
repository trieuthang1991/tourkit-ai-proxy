# Hộp thư chat Đợt 2 — Nền tảng đúng đắn + hoàn thiện giao diện

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sửa hai lỗi đúng đắn có thể làm mất/lạc tin của khách, bổ sung mẫu trả lời nhanh, và dọn giao diện hộp thư chat cho dùng được thật.

**Architecture:** Giữ nguyên kiến trúc hiện tại (PostgreSQL riêng cho chat, adapter theo kênh, hàng đợi `FOR UPDATE SKIP LOCKED`). Không dựng lại schema, không thêm realtime. Bảng sự kiện vào (`chat_inbound_events`) dùng **đúng khuôn** đã có của `chat_outbox` — nhân bản một khuôn đã chạy thật rẻ hơn và ít rủi ro hơn phát minh khuôn mới.

**Tech Stack:** ASP.NET Core 8 Minimal API · Npgsql + Dapper · PostgreSQL · React qua Babel (không build step) · xUnit

**Spec:** [docs/superpowers/specs/2026-08-24-omnichannel-chat-parity-design.md](../specs/2026-08-24-omnichannel-chat-parity-design.md)

## Phạm vi

Plan này lấy **4 hạng mục** từ spec, chọn theo tiêu chí "sai thì mất dữ liệu khách" và "người dùng đã yêu cầu":

| # | Hạng mục | Spec | Vì sao đợt này |
|---|---|---|---|
| 1 | Khoá hội thoại có `account_id` | §2.2, §4.5 | Lỗi thật đang nằm trong code, sửa một dòng |
| 2 | Webhook bền: bỏ `Task.Run` | §2.2, §3.1 | Tiến trình chết sau khi trả 200 là **mất hẳn** tin khách |
| 3 | Mẫu trả lời nhanh | §2.2, §5.4 | Bảng đã có sẵn, thiếu repo/API/UI |
| 4 | Dọn giao diện | §5.4, §5.5 | Người dùng đã chỉ đích danh 4 lỗi |

**HOÃN sang plan sau** (ghi rõ để không ai tưởng bị sót): realtime/SignalR (§3.4 — repo **chưa có SignalR**, phải quyết dùng chung với `toutkit-app` hay dựng mới), tách `chat_contact_identities` (§4.2), phân công theo team (§5.2), AI policy hierarchy (§6), kênh mới (§7.3), dashboard vận hành (§8.1), integration test bằng PostgreSQL (§9.3 — **repo chưa có CI chạy test**, chỉ có workflow deploy).

## Global Constraints

Chép nguyên văn từ CLAUDE.md và spec — mọi task đều phải theo:

- **Tiếng Việt** cho mọi chuỗi hiển thị, log, comment.
- **DateTime = UTC, luôn kèm `Z`.** Lưu bằng `DateTime.UtcNow` hoặc SQL `now()`. Không `DateTime.Now`.
- **Trước khi sửa bất kỳ symbol nào: chạy `codegraph impact <Symbol>`.** Blast-radius rộng → báo người dùng trước.
- **Test-first.** Viết test đỏ trước, rồi mới code.
- **Không đụng `tourkit/`** (dự án tham khảo, chỉ đọc).
- **Chỉ test trên `staging.tourkit.vn`.** `erp.tourkit.vn` là dữ liệu thật, cấm ghi.
- Bảng mới thì tự tạo được; **thêm cột vào bảng cũ phải xác nhận với người dùng**.
- Bo góc thống nhất trong trang chat: thẻ 10px · ô điều khiển 8px · chip nhỏ 6px · viên thuốc 999px · bong bóng 12px.
- Màu: chỉ dùng token (`--primary`, `--border`, `--text*`, `--ci-cam-700`, `--ci-cam-800`). **Không thêm màu mới.**
- `appsettings.json` gitignore, chứa khoá thật — không echo, không commit.
- Chạy test: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
- Dựng bundle sau khi sửa `.jsx`: `.\build-frontend.ps1`
- **Dừng app trước khi `dotnet build`** — app đang chạy khoá `TourkitAiProxy.exe`, build sẽ fail với MSB3027.

---

## Cấu trúc file

| File | Trách nhiệm | Task |
|---|---|---|
| `Services/Chat/Inbox/ChatDb.cs` | Schema (đã có `SchemaSql`) — thêm chỉ mục mới + bảng `chat_inbound_events` | 1, 2 |
| `Services/Chat/Inbox/ChatRepository.cs` | Dapper CRUD — sửa `ON CONFLICT`, thêm CRUD sự kiện vào | 1, 2 |
| `Services/Chat/Inbox/ChatInboundWorker.cs` | **MỚI** — rút `chat_inbound_events` và xử lý | 2 |
| `Endpoints/ChatInboxEndpoints.cs` | Webhook chỉ GHI rồi trả 200; thêm API mẫu trả lời | 2, 3 |
| `Services/Chat/Inbox/ChatQuickReplyRepository.cs` | **MỚI** — CRUD mẫu trả lời nhanh | 3 |
| `wwwroot/pages/chat-inbox.jsx` | Giao diện: tab, chèn mẫu, placeholder | 3, 4 |
| `wwwroot/styles.css` | Hệ nút thống nhất, bỏ viền lồng viền, tab | 4 |
| `TourkitAiProxy.Tests/Chat/*.cs` | Test | 1, 2, 3, 4 |

---

## Task 1: Khoá hội thoại phải có `account_id`

**Files:**
- Modify: `Services/Chat/Inbox/ChatDb.cs` (khối `SchemaSql`, chỉ mục `ux_conv_scope`)
- Modify: `Services/Chat/Inbox/ChatRepository.cs:51-58` (`GetOrCreateConversationAsync`)
- Test: `TourkitAiProxy.Tests/Chat/ChatSchemaGuardTests.cs` (tạo mới)

**Interfaces:**
- Consumes: —
- Produces: chỉ mục `ux_conv_scope_acc`; `GetOrCreateConversationAsync` giữ nguyên chữ ký
  `Task<ChatConversation> GetOrCreateConversationAsync(string tenant, ChatChannel kenh, string externalId, string accountId, CancellationToken ct = default)`

> **Bối cảnh cho người thực thi.** Khoá hiện tại là `(tenant_id, channel, contact_external_id)` — thiếu tài khoản.
> Không phải kênh nào cũng lộ ra như nhau nên rất dễ tưởng an toàn:
> - **Messenger** cấp PSID theo TỪNG Trang → hai Trang cho hai id khác nhau → tình cờ không sao.
> - **Zalo** cấp user id theo TỪNG OA → cũng không sao.
> - **Telegram** thì `chat.id` của chat riêng **chính là id người dùng, giống hệt ở mọi bot**. Một khách nhắn bot A rồi nhắn bot B sẽ rơi vào **cùng một hội thoại**, và bot B trả lời tin của bot A.
>
> ⚠️ Một phần thay đổi này **đã nằm sẵn trong working tree chưa commit** (khối `SchemaSql`). Chạy `git diff Services/Chat/Inbox/ChatDb.cs` để xem trước, đừng viết đè.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatSchemaGuardTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh schema ở mức MÃ NGUỒN. Không có CI chạy PostgreSQL nên đây là lớp bảo vệ duy nhất
/// chống việc ai đó lỡ tay bỏ account_id khỏi khoá hội thoại.
/// </summary>
public class ChatSchemaGuardTests
{
    [Fact]
    public void Khoa_hoi_thoai_phai_co_account_id()
    {
        var sql = DocFile("Services/Chat/Inbox/ChatDb.cs");

        var m = Regex.Match(sql,
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_conversations \(([^)]*)\)");
        Assert.True(m.Success, "Không thấy chỉ mục duy nhất nào trên chat_conversations");

        var cot = m.Groups[1].Value;
        Assert.Contains("account_id", cot);
        Assert.Contains("tenant_id", cot);
        Assert.Contains("contact_external_id", cot);
    }

    [Fact]
    public void ON_CONFLICT_phai_khop_voi_chi_muc()
    {
        // Postgres đòi cột trong ON CONFLICT phải khớp một chỉ mục duy nhất. Lệch là lỗi lúc
        // CHẠY chứ không phải lúc biên dịch — nghĩa là chỉ lộ ra khi khách nhắn tin thật.
        var repo = DocFile("Services/Chat/Inbox/ChatRepository.cs");
        Assert.Contains("ON CONFLICT (tenant_id, channel, account_id, contact_external_id)", repo);
    }

    internal static string DocFile(string duongDanTuongDoi)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj")))
            d = d.Parent;
        Assert.NotNull(d);
        var f = Path.Combine(d!.FullName, duongDanTuongDoi.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(f), $"Không thấy {f}");
        return File.ReadAllText(f);
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận nó ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatSchemaGuard"
```

Mong đợi: `ON_CONFLICT_phai_khop_voi_chi_muc` FAIL. Test còn lại có thể đã xanh nếu thay đổi schema chưa commit đang có trong cây.

- [ ] **Bước 3: Sửa schema**

Trong `Services/Chat/Inbox/ChatDb.cs`, thay khối chỉ mục cũ bằng:

```sql
    -- Khoá hội thoại PHẢI có account_id. Thiếu nó thì hai tài khoản cùng kênh của cùng công ty
    -- gộp nhầm hội thoại của cùng một khách, và câu trả lời đi ra SAI tài khoản.
    -- Telegram là kênh lộ rõ nhất: chat.id của chat riêng CHÍNH LÀ id người dùng, giống hệt ở
    -- mọi bot. Messenger/Zalo cấp id theo từng Trang/OA nên tình cờ không sao.
    --
    -- Thứ tự CỐ Ý: tạo chỉ mục mới TRƯỚC rồi mới bỏ cái cũ. Nếu dữ liệu đang có trùng thì lệnh
    -- tạo hỏng, cả khối SQL dừng, và chỉ mục CŨ vẫn còn nguyên — vẫn chống trùng. Bỏ trước tạo
    -- sau thì lúc hỏng sẽ không còn chỉ mục duy nhất nào cả.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_conv_scope_acc
      ON chat_conversations (tenant_id, channel, account_id, contact_external_id);
    DROP INDEX IF EXISTS ux_conv_scope;
```

- [ ] **Bước 4: Sửa `ON CONFLICT` cho khớp**

`Services/Chat/Inbox/ChatRepository.cs`, trong `GetOrCreateConversationAsync`:

```csharp
        return await c.QuerySingleAsync<ChatConversation>("""
            INSERT INTO chat_conversations (tenant_id, channel, contact_external_id, account_id)
            VALUES (@tenant, @kenh, @id, @accountId)
            ON CONFLICT (tenant_id, channel, account_id, contact_external_id)
              DO UPDATE SET tenant_id = EXCLUDED.tenant_id
            RETURNING *
            """, new { tenant, kenh = (short)kenh, id = externalId, accountId });
```

- [ ] **Bước 5: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatSchemaGuard"
```

- [ ] **Bước 6: Chạy app để schema tự áp dụng, xác nhận không lỗi**

```bash
dotnet build TourkitAiProxy.csproj
dotnet run --project TourkitAiProxy.csproj --no-build
```

Trong log phải thấy `ChatDb schema OK`. Nếu thấy `ChatDb InitAsync thất bại` kèm lỗi trùng khoá → CSDL đang có hội thoại trùng, **dừng lại và báo người dùng**, đừng tự xoá dữ liệu.

- [ ] **Bước 7: Commit**

```bash
git add Services/Chat/Inbox/ChatDb.cs Services/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Tests/Chat/ChatSchemaGuardTests.cs
git commit -m "fix(chat): khoá hội thoại phải có account_id

Telegram dùng chat.id = id người dùng, GIỐNG HỆT ở mọi bot. Thiếu account_id
trong khoá thì một khách nhắn hai bot của cùng công ty sẽ rơi vào cùng một hội
thoại, và bot này trả lời tin của bot kia. Messenger/Zalo cấp id theo từng
Trang/OA nên tình cờ không lộ.

Tạo chỉ mục mới TRƯỚC rồi mới bỏ cũ: dữ liệu trùng thì lệnh hỏng và chỉ mục cũ
còn nguyên, thay vì mất hẳn lớp chống trùng.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Webhook bền — bỏ `Task.Run`

**Files:**
- Modify: `Services/Chat/Inbox/ChatDb.cs` (thêm bảng `chat_inbound_events`)
- Modify: `Services/Chat/Inbox/ChatRepository.cs` (thêm 3 method)
- Create: `Services/Chat/Inbox/ChatInboundWorker.cs`
- Modify: `Endpoints/ChatInboxEndpoints.cs` (hàm `XuLy` trong `MapWebhook`)
- Modify: `Program.cs:202-203` (đăng ký worker)
- Test: `TourkitAiProxy.Tests/Chat/ChatInboundEventTests.cs` (tạo mới)

**Interfaces:**
- Consumes: `ChatInboundService.HandleAsync(string tenantId, string accountId, IReadOnlyList<InboundChatEvent> sk, CancellationToken ct)` — đã có, giữ nguyên.
- Produces:
  - `Task<long?> ChatRepository.EnqueueInboundAsync(string tenant, ChatChannel kenh, string accountId, string? providerEventId, string rawBody, CancellationToken ct = default)` — trả `null` khi trùng.
  - `record ChatRepository.InboundRow(long Id, string TenantId, short Channel, string AccountId, string RawBody, int RetryCount)`
  - `Task<List<InboundRow>> ChatRepository.ClaimInboundAsync(int soLuong, CancellationToken ct = default)`
  - `Task ChatRepository.FinishInboundAsync(long id, bool thanhCong, bool thuLai, string? loi, CancellationToken ct = default)`

> **Bối cảnh cho người thực thi.** Webhook hiện làm thế này (`Endpoints/ChatInboxEndpoints.cs`, hàm `XuLy`):
> ```csharp
> _ = Task.Run(async () => { await svc.HandleAsync(tenantId, taiKhoan, sk, CancellationToken.None); }, ...);
> return Results.Ok();
> ```
> Đã trả `200` cho kênh nghĩa là kênh coi như **đã giao xong** và sẽ không gửi lại. Nhưng việc xử lý mới chỉ nằm trong bộ nhớ. IIS recycle, deploy, hay app crash trong vài giây đó → **tin của khách biến mất, không dấu vết**.
>
> Cách sửa: webhook chỉ **ghi thân thô xuống CSDL** rồi mới trả 200. Một worker riêng rút ra xử lý. Ghi rồi thì crash bao nhiêu lần cũng làm lại được.
>
> Bảng mới dùng **đúng khuôn** `chat_outbox` đã có (`status` 0=chờ 1=xong 2=hỏng 3=đang xử lý, `FOR UPDATE SKIP LOCKED`) — nhân bản khuôn đã chạy thật rẻ và an toàn hơn phát minh khuôn mới.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatInboundEventTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh ở mức mã nguồn (không có CI chạy PostgreSQL). Ba điều phải giữ:
/// bảng sự kiện vào tồn tại, có chống trùng ở tầng CSDL, và webhook KHÔNG còn fire-and-forget.
/// </summary>
public class ChatInboundEventTests
{
    [Fact]
    public void Co_bang_su_kien_vao()
    {
        var sql = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatDb.cs");
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_inbound_events", sql);
    }

    [Fact]
    public void Chong_trung_o_TANG_CSDL_chu_khong_chi_trong_code()
    {
        // Webhook gửi lại đồng thời hai lần thì kiểm-rồi-ghi trong code vẫn lọt. Phải là chỉ mục
        // duy nhất để chính CSDL từ chối bản thứ hai.
        var sql = ChatSchemaGuardTests.DocFile("Services/Chat/Inbox/ChatDb.cs");
        var m = Regex.Match(sql,
            @"CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON chat_inbound_events \(([^)]*)\)");
        Assert.True(m.Success, "chat_inbound_events thiếu chỉ mục duy nhất chống trùng");
        Assert.Contains("provider_event_id", m.Groups[1].Value);
    }

    [Fact]
    public void Webhook_khong_con_fire_and_forget()
    {
        // Đã trả 200 nghĩa là kênh sẽ KHÔNG gửi lại. Xử lý còn nằm trong bộ nhớ lúc đó thì
        // IIS recycle / deploy / crash làm mất hẳn tin của khách, không dấu vết.
        var src = ChatSchemaGuardTests.DocFile("Endpoints/ChatInboxEndpoints.cs");
        Assert.DoesNotContain("Task.Run", src);
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận cả 3 test ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatInboundEvent"
```

Mong đợi: 3 FAIL.

- [ ] **Bước 3: Thêm bảng vào `SchemaSql`**

Chèn ngay **trước** `CREATE TABLE IF NOT EXISTS chat_outbox`:

```sql
    -- Sự kiện webhook ĐÃ NHẬN, chưa xử lý. Webhook chỉ ghi vào đây rồi trả 200; xử lý là việc
    -- của ChatInboundWorker. Trước đây webhook trả 200 rồi mới `Task.Run` xử lý — đã trả 200
    -- nghĩa là kênh coi như giao xong và không gửi lại, nên app chết trong vài giây đó là mất
    -- hẳn tin của khách, không dấu vết.
    CREATE TABLE IF NOT EXISTS chat_inbound_events (
      id                bigserial PRIMARY KEY,
      tenant_id         text     NOT NULL,
      channel           smallint NOT NULL,
      account_id        text     NOT NULL,
      provider_event_id text,             -- id sự kiện phía kênh, dùng chống trùng
      raw_body          text     NOT NULL,
      status            smallint NOT NULL DEFAULT 0,  -- 0=chờ 1=xong 2=hỏng 3=đang xử lý
      retry_count       integer  NOT NULL DEFAULT 0,
      error_message     text,
      created_utc       timestamptz NOT NULL DEFAULT now(),
      processed_utc     timestamptz
    );
    -- Chống trùng ở TẦNG CSDL. Kiểm-rồi-ghi trong code vẫn lọt khi kênh gửi lại đồng thời.
    -- Partial index: sự kiện không có id thì không chống trùng được, cứ nhận.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_inbound_event
      ON chat_inbound_events (tenant_id, channel, provider_event_id)
      WHERE provider_event_id IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_inbound_cho
      ON chat_inbound_events (created_utc) WHERE status = 0;
```

- [ ] **Bước 4: Thêm 3 method vào `ChatRepository`**

Chèn vào cuối `ChatRepository`, ngay trước `PruneAsync`:

```csharp
    // ── Hàng đợi sự kiện VÀO ─────────────────────────────────────────────────

    /// <summary>Ghi sự kiện webhook xuống CSDL. Trả <c>null</c> khi trùng (kênh gửi lại).</summary>
    public async Task<long?> EnqueueInboundAsync(string tenant, ChatChannel kenh, string accountId,
        string? providerEventId, string rawBody, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long?>("""
            INSERT INTO chat_inbound_events (tenant_id, channel, account_id, provider_event_id, raw_body)
            VALUES (@tenant, @kenh, @accountId, @ext, @raw)
            ON CONFLICT (tenant_id, channel, provider_event_id) WHERE provider_event_id IS NOT NULL
              DO NOTHING
            RETURNING id
            """, new { tenant, kenh = (short)kenh, accountId, ext = providerEventId, raw = rawBody });
    }

    public record InboundRow(long Id, string TenantId, short Channel, string AccountId,
        string RawBody, int RetryCount);

    public async Task<List<InboundRow>> ClaimInboundAsync(int soLuong, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<InboundRow>("""
            UPDATE chat_inbound_events SET status = 3
             WHERE id IN (
               SELECT id FROM chat_inbound_events WHERE status = 0
                ORDER BY created_utc LIMIT @n FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, channel, account_id, raw_body, retry_count
            """, new { n = Math.Clamp(soLuong, 1, 50) })).ToList();
    }

    /// <param name="thuLai">true = trả về hàng đợi để thử lần sau (lỗi tạm thời).</param>
    public async Task FinishInboundAsync(long id, bool thanhCong, bool thuLai, string? loi,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            UPDATE chat_inbound_events
               SET status = CASE WHEN @ok THEN 1 WHEN @thuLai THEN 0 ELSE 2 END,
                   retry_count = retry_count + CASE WHEN @thuLai THEN 1 ELSE 0 END,
                   error_message = @loi,
                   processed_utc = CASE WHEN @thuLai THEN NULL ELSE now() END
             WHERE id = @id
            """, new { id, ok = thanhCong, thuLai, loi });
    }
```

- [ ] **Bước 5: Tạo worker**

Tạo `Services/Chat/Inbox/ChatInboundWorker.cs`:

```csharp
// Services/Chat/Inbox/ChatInboundWorker.cs
using TourkitAiProxy.Services.Chat.Channels;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Rút sự kiện webhook đã ghi rồi xử lý (chống trùng → ghi tin → gộp cụm → bot trả lời).
///
/// <para><b>Vì sao tách khỏi webhook.</b> Trước đây webhook trả 200 rồi <c>Task.Run</c> xử lý.
/// Đã trả 200 nghĩa là kênh coi như giao xong và sẽ không gửi lại — app chết trong vài giây đó
/// là mất hẳn tin của khách. Nay webhook chỉ ghi xuống CSDL, ghi rồi thì crash bao nhiêu lần
/// cũng làm lại được.</para>
///
/// <para><b>Nhịp 2 giây</b>, nhanh hơn hàng đợi gửi (5 giây): khách đang chờ trước màn hình.</para>
/// </summary>
public class ChatInboundWorker : BackgroundService
{
    private static readonly TimeSpan Nhip = TimeSpan.FromSeconds(2);
    private const int SoLuotThuLai = 3;

    private readonly IServiceProvider _sp;
    private readonly ILogger<ChatInboundWorker> _log;

    public ChatInboundWorker(IServiceProvider sp, ILogger<ChatInboundWorker> log)
    { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[chat/inbound] bắt đầu, nhịp {N}s", Nhip.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try { await MotNhipAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Không để vòng lặp chết vì một nhịp hỏng — chết là hộp thư ngừng nhận trong im lặng.
                _log.LogError(ex, "[chat/inbound] nhịp hỏng");
            }
            try { await Task.Delay(Nhip, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task MotNhipAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatRepository>();
        if (!repo.Configured) return;

        var svc = scope.ServiceProvider.GetRequiredService<ChatInboundService>();
        foreach (var r in await repo.ClaimInboundAsync(10, ct))
        {
            try
            {
                var adapter = svc.Adapter((ChatChannel)r.Channel);
                if (adapter is null)
                {
                    await repo.FinishInboundAsync(r.Id, false, false, $"Chưa hỗ trợ kênh {r.Channel}", ct);
                    continue;
                }
                var sk = adapter.Parse(r.RawBody);
                if (sk.Count > 0) await svc.HandleAsync(r.TenantId, r.AccountId, sk, ct);
                await repo.FinishInboundAsync(r.Id, true, false, null, ct);
            }
            catch (Exception ex)
            {
                var conLuot = r.RetryCount + 1 < SoLuotThuLai;
                await repo.FinishInboundAsync(r.Id, false, conLuot, ex.Message, ct);
                _log.LogError(ex, "[chat/inbound] xử lý sự kiện {Id} hỏng ({Thu})",
                    r.Id, conLuot ? "sẽ thử lại" : "dừng");
            }
        }
    }
}
```

- [ ] **Bước 6: Webhook chỉ ghi rồi trả 200**

Trong `Endpoints/ChatInboxEndpoints.cs`, thay phần cuối hàm `XuLy` (từ `var sk = adapter.Parse(raw);` tới `return Results.Ok();`) bằng:

```csharp
            // Chỉ GHI thân thô rồi trả 200. XỬ LÝ là việc của ChatInboundWorker.
            var repo = ctx.RequestServices.GetRequiredService<ChatRepository>();
            if (!repo.Configured) return ChuaCauHinh();

            // Vẫn parse MỘT lần ở đây, nhưng chỉ để lấy id sự kiện làm khoá chống trùng — chống
            // trùng phải xảy ra lúc GHI, không thì kênh gửi lại sẽ tạo hai dòng và bot trả lời
            // hai lần. Parse là thao tác rẻ (đọc JSON) và KHÔNG gọi mạng hay AI, khác hẳn phần
            // xử lý. Worker sẽ parse lại lần nữa — chấp nhận, vì đổi lại webhook không phụ thuộc
            // vào bất cứ thứ gì có thể chậm hay hỏng.
            //
            // Parse hỏng thì idSuKien = null: vẫn GHI, không mất tin. Partial index bỏ qua dòng
            // NULL nên mất chống trùng cho riêng dòng đó — thà nhận trùng còn hơn mất hẳn.
            var idSuKien = adapter.Parse(raw).FirstOrDefault()?.ExternalMsgId;
            var da = await repo.EnqueueInboundAsync(tenantId, loaiKenh, taiKhoan, idSuKien, raw, ct);
            if (da is null)
                log.LogDebug("[chat/webhook] bỏ sự kiện trùng ext={Ext} tenant={T}", idSuKien, tenantId);

            return Results.Ok();
```

- [ ] **Bước 7: Đăng ký worker**

`Program.cs`, ngay dưới dòng đăng ký `ChatOutboxWorker` (khoảng dòng 203):

```csharp
if (TourkitAiProxy.Services.Bootstrap.FeatureFlags.Chat(builder.Configuration))
{
    builder.Services.AddHostedService<TourkitAiProxy.Services.Chat.Inbox.ChatOutboxWorker>();
    // Rút sự kiện webhook đã ghi. Phải chạy ở WEB vì webhook vào web, và tin chat phải đi ngay.
    builder.Services.AddHostedService<TourkitAiProxy.Services.Chat.Inbox.ChatInboundWorker>();
}
```

- [ ] **Bước 8: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```

Mong đợi: toàn bộ PASS (766+).

- [ ] **Bước 9: Thử thật đầu-cuối**

```bash
dotnet build TourkitAiProxy.csproj
dotnet run --project TourkitAiProxy.csproj --no-build
```

Sau khi app lên, bắn webhook Telegram giả **hai lần cùng một `message_id`** (cần một tài khoản Telegram đã khai với `webhookSecret`). Mong đợi: cả hai lần HTTP 200, nhưng hộp thư chỉ có **một** tin — dòng thứ hai bị chỉ mục duy nhất chặn. Log có `bỏ sự kiện trùng`.

- [ ] **Bước 10: Commit**

```bash
git add Services/Chat/Inbox/ChatDb.cs Services/Chat/Inbox/ChatRepository.cs \
        Services/Chat/Inbox/ChatInboundWorker.cs Endpoints/ChatInboxEndpoints.cs Program.cs \
        TourkitAiProxy.Tests/Chat/ChatInboundEventTests.cs
git commit -m "fix(chat): webhook ghi xuống CSDL trước khi trả 200

Trước đây trả 200 rồi Task.Run xử lý. Đã trả 200 nghĩa là kênh coi như giao
xong và không gửi lại — IIS recycle / deploy / crash trong vài giây đó là mất
hẳn tin của khách, không dấu vết.

chat_inbound_events dùng đúng khuôn chat_outbox đã chạy thật (FOR UPDATE SKIP
LOCKED, 3 kết cục). Chống trùng bằng chỉ mục duy nhất ở tầng CSDL — kiểm-rồi-ghi
trong code vẫn lọt khi kênh gửi lại đồng thời.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Mẫu trả lời nhanh

**Files:**
- Create: `Services/Chat/Inbox/ChatQuickReplyRepository.cs`
- Modify: `Endpoints/ChatInboxEndpoints.cs` (thêm 4 route vào `MapInbox`, thêm `/api/v1/chat/quick-replies` vào `DuongRieng`)
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs` (đăng ký DI, cạnh `ChatRepository`)
- Modify: `wwwroot/pages/chat-inbox.jsx` (gõ `/` trong ô soạn → hiện danh sách)
- Modify: `wwwroot/styles.css`
- Test: `TourkitAiProxy.Tests/Chat/ChatQuickReplyTests.cs`

**Interfaces:**
- Consumes: bảng `chat_quick_replies` đã có trong `SchemaSql` (cột: `id, tenant_id, trigger, body, created_utc, updated_utc`; unique `(tenant_id, lower(trigger))`).
- Produces:
  - `record QuickReply(long Id, string Trigger, string Body)`
  - `Task<List<QuickReply>> ListAsync(string tenant, CancellationToken ct = default)`
  - `Task<long> UpsertAsync(string tenant, string trigger, string body, CancellationToken ct = default)`
  - `Task<bool> DeleteAsync(string tenant, long id, CancellationToken ct = default)`
  - `static string ChuanHoaTrigger(string thô)` — **hàm thuần**, đây là chỗ có test thật.

- [ ] **Bước 1: Viết test đỏ cho hàm thuần**

Tạo `TourkitAiProxy.Tests/Chat/ChatQuickReplyTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

public class ChatQuickReplyTests
{
    [Theory]
    [InlineData("giá", "gia")]          // bỏ dấu: gõ "/gia" phải ra mẫu "/giá"
    [InlineData("/gia", "gia")]         // người dùng gõ luôn dấu gạch
    [InlineData("  Báo Giá  ", "bao-gia")]
    [InlineData("hẹn lịch", "hen-lich")]
    [InlineData("giá!!!", "gia")]
    public void Chuan_hoa_trigger(string tho, string mong)
        => Assert.Equal(mong, ChatQuickReplyRepository.ChuanHoaTrigger(tho));

    [Fact]
    public void Trigger_rong_thi_nem()
    {
        // Mẫu không có lệnh gọi thì gõ "/" mãi cũng không ra — thà chặn lúc lưu.
        Assert.Throws<ArgumentException>(() => ChatQuickReplyRepository.ChuanHoaTrigger("///"));
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatQuickReply"
```

Mong đợi: FAIL vì `ChatQuickReplyRepository` chưa tồn tại.

- [ ] **Bước 3: Tạo repository**

Tạo `Services/Chat/Inbox/ChatQuickReplyRepository.cs`:

```csharp
// Services/Chat/Inbox/ChatQuickReplyRepository.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <param name="Trigger">Chuỗi gõ sau dấu "/" — đã chuẩn hoá, không dấu, không khoảng trắng.</param>
public record QuickReply(long Id, string Trigger, string Body);

/// <summary>
/// Mẫu trả lời nhanh, theo TỪNG CÔNG TY (không theo từng nhân viên): cả đội trực chat dùng chung
/// một bộ câu, sửa một mẫu là cả đội thấy ngay, không phải dạy lại từng người.
/// </summary>
public class ChatQuickReplyRepository
{
    private readonly ChatDb _db;
    public ChatQuickReplyRepository(ChatDb db) { _db = db; }

    public bool Configured => _db.Configured;

    /// <summary>
    /// Bỏ dấu, hạ chữ thường, thay khoảng trắng bằng gạch nối.
    ///
    /// <para><b>Bỏ dấu là bắt buộc.</b> Nhân viên đang gõ nhanh cho khách sẽ gõ <c>/gia</c> chứ
    /// không dừng lại bật bộ gõ để ra <c>/giá</c>. Giữ nguyên dấu thì mẫu gần như không ai dùng.</para>
    /// </summary>
    public static string ChuanHoaTrigger(string tho)
    {
        var s = (tho ?? "").Trim().TrimStart('/').ToLowerInvariant();
        s = s.Replace('đ', 'd');
        s = new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"[\s-]+", "-").Trim('-');
        if (s.Length == 0)
            throw new ArgumentException("Lệnh gọi mẫu không được rỗng", nameof(tho));
        return s;
    }

    public async Task<List<QuickReply>> ListAsync(string tenant, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return (await c.QueryAsync<QuickReply>(
            "SELECT id, trigger, body FROM chat_quick_replies WHERE tenant_id = @tenant ORDER BY trigger",
            new { tenant })).ToList();
    }

    public async Task<long> UpsertAsync(string tenant, string trigger, string body,
        CancellationToken ct = default)
    {
        var tg = ChuanHoaTrigger(trigger);
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO chat_quick_replies (tenant_id, trigger, body)
            VALUES (@tenant, @tg, @body)
            ON CONFLICT (tenant_id, lower(trigger))
              DO UPDATE SET body = EXCLUDED.body, updated_utc = now()
            RETURNING id
            """, new { tenant, tg, body });
    }

    public async Task<bool> DeleteAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM chat_quick_replies WHERE tenant_id = @tenant AND id = @id",
            new { tenant, id }) > 0;
    }
}
```

- [ ] **Bước 4: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatQuickReply"
```

- [ ] **Bước 5: Đăng ký DI**

`Services/Bootstrap/WorkflowStackRegistration.cs`, ngay dưới `s.AddSingleton<Chat.Inbox.ChatRepository>();`:

```csharp
        s.AddSingleton<Chat.Inbox.ChatQuickReplyRepository>();
```

- [ ] **Bước 6: Thêm API**

`Endpoints/ChatInboxEndpoints.cs` — thêm `"/api/v1/chat/quick-replies"` vào mảng `DuongRieng`, rồi thêm vào cuối `MapInbox`:

```csharp
        // ── Mẫu trả lời nhanh ───────────────────────────────────────────────
        // ĐỌC thì mọi nhân viên trực chat đều cần; SỬA/XOÁ thì cần quyền cấu hình hệ thống —
        // đây là bộ câu dùng chung cả đội, một người sửa là cả đội đổi theo.
        g.MapGet("/quick-replies", async (HttpContext ctx, TkSessionStore sessions,
            ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!repo.Configured) return ChuaCauHinh();
            return Results.Json(new { items = await repo.ListAsync(a.TenantId, ct) }, Web);
        });

        g.MapPut("/quick-replies", async (QuickReplyReq body, HttpContext ctx,
            TkSessionStore sessions, ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return ChuaCauHinh();
            if (string.IsNullOrWhiteSpace(body.Body))
                return Results.BadRequest(new { error = "Chưa nhập nội dung mẫu" });
            try
            {
                var id = await repo.UpsertAsync(a.TenantId, body.Trigger, body.Body.Trim(), ct);
                return Results.Json(new { ok = true, id }, Web);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        g.MapDelete("/quick-replies/{id:long}", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatQuickReplyRepository repo, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (!repo.Configured) return ChuaCauHinh();
            return Results.Json(new { ok = true, removed = await repo.DeleteAsync(a.TenantId, id, ct) }, Web);
        });
```

Và thêm record cạnh `SendReq`:

```csharp
public record QuickReplyReq(string Trigger, string Body);
```

- [ ] **Bước 7: Giao diện — gõ `/` hiện danh sách**

`wwwroot/pages/chat-inbox.jsx`. Trong `ChatInboxPage`, thêm state cạnh `dinhKem`:

```jsx
    const [mauTraLoi, setMauTraLoi] = useState([]);
    const [goiY, setGoiY] = useState(null);   // null = đang không gõ lệnh
```

Tải một lần, cạnh các `useEffect` khác:

```jsx
    // Tải một lần, KHÔNG theo nhịp hỏi lại 4 giây: bộ mẫu hiếm khi đổi, kéo lại liên tục là
    // tốn truy vấn cho thứ gần như đứng yên.
    useEffect(() => {
      authedFetch('/api/v1/chat/quick-replies')
        .then(r => r.ok ? r.json() : { items: [] })
        .then(j => setMauTraLoi(j.items || []))
        .catch(() => {});
    }, []);
```

Thay `onChange` của textarea trong `.ci-soan-o`:

```jsx
                        <textarea value={soan}
                                  onChange={e => {
                                    const v = e.target.value;
                                    setSoan(v);
                                    // Chỉ gợi ý khi "/" đứng ĐẦU ô soạn — giữa câu thì "/" là
                                    // dấu gạch bình thường (vd "sáng/chiều"), bật popup là phiền.
                                    const m = /^\/([a-z0-9-]*)$/i.exec(v);
                                    setGoiY(m ? m[1].toLowerCase() : null);
                                  }}
                                  placeholder={dinhKem ? 'Thêm chú thích (không bắt buộc)…' : 'Nhập trả lời cho khách… (gõ / để chèn mẫu)'}
                                  onKeyDown={e => {
                                    if (e.key === 'Escape') { setGoiY(null); return; }
                                    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); gui(); }
                                  }} />
```

Thêm popup ngay trên `.ci-soan-o`, bên trong nhánh `!khoaSoan`:

```jsx
                      {goiY !== null && mauTraLoi.filter(m => m.trigger.startsWith(goiY)).length > 0 && (
                        <div className="ci-mau">
                          <div className="ci-mau-dau">Mẫu trả lời</div>
                          {mauTraLoi.filter(m => m.trigger.startsWith(goiY)).slice(0, 6).map(m => (
                            <button key={m.id} className="ci-mau-muc"
                                    onClick={() => { setSoan(m.body); setGoiY(null); }}>
                              <b>/{m.trigger}</b>
                              <span>{m.body}</span>
                            </button>
                          ))}
                        </div>
                      )}
```

- [ ] **Bước 8: CSS**

`wwwroot/styles.css`, thêm ngay trước `.ci-soan-o`:

```css
/* Danh sách mẫu trả lời khi gõ "/". Nổi TRÊN ô soạn chứ không đẩy ô soạn xuống — đẩy xuống thì
   con trỏ nhảy khỏi chỗ mắt đang nhìn. */
.ci-mau { border: 1px solid var(--border); border-radius: 10px; background: var(--surface);
          box-shadow: var(--shadow-md); margin-bottom: 8px; overflow: hidden; max-height: 220px;
          overflow-y: auto; }
.ci-mau-dau { padding: 6px 11px; font-size: 11px; font-weight: 700; letter-spacing: .04em;
              text-transform: uppercase; color: var(--text-3); background: var(--bg);
              border-bottom: 1px solid var(--border); }
.ci-mau-muc { display: flex; align-items: baseline; gap: 9px; width: 100%; padding: 8px 11px;
              border: 0; border-bottom: 1px solid var(--border); background: transparent;
              cursor: pointer; text-align: left; font-family: inherit; }
.ci-mau-muc:last-child { border-bottom: 0; }
.ci-mau-muc:hover { background: var(--primary-soft); }
.ci-mau-muc b { flex: none; font-family: ui-monospace, monospace; font-size: 12px;
                color: var(--ci-cam-800); }
.ci-mau-muc span { flex: 1; min-width: 0; font-size: 12.5px; color: var(--text-2);
                   overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
```

- [ ] **Bước 9: Dựng bundle và thử tay**

```bash
.\build-frontend.ps1
dotnet build TourkitAiProxy.csproj
dotnet run --project TourkitAiProxy.csproj --no-build
```

Tạo một mẫu rồi thử:

```bash
curl -X PUT http://localhost:5080/api/v1/chat/quick-replies \
  -H "X-Session-Id: <SESSION>" -H "Content-Type: application/json" \
  -d '{"trigger":"giá","body":"Dạ em gửi anh/chị bảng giá tour ạ."}'
```

Mở `/chat-inbox`, chọn một hội thoại, gõ `/gi` vào ô soạn → phải thấy `/gia` hiện lên; bấm vào → nội dung mẫu điền vào ô soạn.

- [ ] **Bước 10: Commit**

```bash
git add Services/Chat/Inbox/ChatQuickReplyRepository.cs Endpoints/ChatInboxEndpoints.cs \
        Services/Bootstrap/WorkflowStackRegistration.cs wwwroot/pages/chat-inbox.jsx \
        wwwroot/styles.css TourkitAiProxy.Tests/Chat/ChatQuickReplyTests.cs
git commit -m "feat(chat): mẫu trả lời nhanh gõ bằng /lệnh

Bảng chat_quick_replies đã có từ trước mà chưa ai dùng — nay có repo, API và
popup trong ô soạn.

Lệnh gọi BỎ DẤU khi lưu: nhân viên đang gõ nhanh cho khách sẽ gõ /gia chứ
không dừng bật bộ gõ để ra /giá. Giữ nguyên dấu thì mẫu gần như không ai dùng.

Chỉ gợi ý khi / đứng ĐẦU ô soạn — giữa câu thì / là dấu gạch bình thường.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Dọn giao diện

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` (component `KhaiKenh` → tab; các nút → một hệ)
- Modify: `wwwroot/styles.css`
- Test: `TourkitAiProxy.Tests/Chat/ChatUiGuardTests.cs`

**Interfaces:**
- Consumes: `GET /api/v1/chat/channels` trả `{ items: [{ channel, name, fields, webhookUrl, accounts: [{accountId, label, configured, webhookUrl, values}] }] }` — đã có từ Task trước, không đổi.
- Produces: —

> **Bối cảnh cho người thực thi.** Người dùng đã chỉ đích danh 4 lỗi, kèm ảnh chụp:
> 1. **Viền lồng viền** trong ô soạn — `.ci-wrap :is(button,input,textarea):focus-visible` đặt `outline` cho MỌI ô, kể cả textarea vốn đã nằm trong khung `.ci-soan-o` có viền riêng. Hai quy tắc cùng độ ưu tiên (0,2,1), quy tắc `focus-visible` viết sau nên thắng.
> 2. **Nút mỗi cái một kiểu** — `.ci-nut-nhom button` (viền xám), `.btn-primary` (cam đặc), `.ci-nut-xoa` (viền đỏ), nút "Thêm" nhỏ hơn hẳn. Bốn kích cỡ, ba kiểu.
> 3. **Thiếu placeholder** ở các ô nhập trong form khai kênh.
> 4. **Khai kênh đổ hết ra một màn hình** — ba thẻ cạnh nhau, mỗi thẻ lồng thêm khối tài khoản, khối tài khoản lồng ô nhập: **ba lớp viền**.

- [ ] **Bước 1: Viết test đỏ**

Tạo `TourkitAiProxy.Tests/Chat/ChatUiGuardTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh vài lỗi giao diện đã bị người dùng chỉ ra, ở mức mã nguồn. Không thay được việc nhìn
/// bằng mắt, nhưng chặn được việc lặp lại đúng lỗi cũ.
/// </summary>
public class ChatUiGuardTests
{
    [Fact]
    public void O_soan_khong_bi_vien_long_vien()
    {
        // .ci-soan-o đã có viền riêng và đổi màu khi focus. Nếu textarea bên trong lại nhận
        // outline nữa thì thành hai viền lồng nhau.
        var css = ChatSchemaGuardTests.DocFile("wwwroot/styles.css");
        Assert.Contains(".ci-soan-o textarea:focus-visible { outline: none; }", css);
    }

    [Fact]
    public void Moi_o_nhap_khai_kenh_deu_co_placeholder()
    {
        // Ô trống không gợi ý gì thì người khai phải đoán định dạng — nhất là các ô token dài.
        //
        // Đếm token "Hint:" thì HỎNG: gán theo vị trí (đối số thứ 4) cũng là khai placeholder mà
        // không có chữ "Hint:" nào. Phải soi TỪNG dòng khai báo và đếm đối số.
        var src = ChatSchemaGuardTests.DocFile("Endpoints/ChatInboxEndpoints.cs");

        var thieu = new List<string>();
        foreach (Match m in Regex.Matches(src, @"new ONhap\((?<args>[^;]*?)\),\s*$",
                                          RegexOptions.Multiline))
        {
            var args = m.Groups["args"].Value;
            if (args.Contains("\"note\"")) continue;          // ghi chú, không phải ô nhập

            // Tách đối số ở dấu phẩy NGOÀI chuỗi — nhãn tiếng Việt có dấu phẩy bên trong.
            var soDoiSo = 1; var trongChuoi = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == '"' && (i == 0 || args[i - 1] != '\')) trongChuoi = !trongChuoi;
                else if (args[i] == ',' && !trongChuoi) soDoiSo++;
            }
            if (soDoiSo < 4) thieu.Add(args.Trim());
        }

        Assert.True(thieu.Count == 0,
            "Các ô sau chưa có placeholder (thiếu đối số thứ 4):
  " + string.Join("
  ", thieu));
    }
}
```

- [ ] **Bước 2: Chạy để xác nhận ĐỎ**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~ChatUiGuard"
```

Mong đợi: 2 FAIL.

- [ ] **Bước 3: Sửa viền lồng viền**

`wwwroot/styles.css`, thêm NGAY SAU quy tắc `.ci-wrap :is(button, input, textarea):focus-visible`:

```css
/* Ô soạn: khung ngoài .ci-soan-o đã đổi màu viền khi focus, nên textarea bên trong KHÔNG được
   vẽ thêm outline — hai viền lồng nhau trông như lỗi. Phải đặt SAU quy tắc focus-visible ở trên
   vì hai bên cùng độ ưu tiên (0,2,1), cái viết sau thắng. */
.ci-soan-o textarea:focus-visible { outline: none; }
```

- [ ] **Bước 4: Một hệ nút duy nhất**

`wwwroot/styles.css`, thêm ngay trước `.ci-nut-nhom`:

```css
/* MỘT hệ nút cho cả trang chat. Trước đó có bốn cỡ và ba kiểu lẫn lộn: nút hành động ở đầu
   khung chat, nút Lưu cam đặc, nút Gỡ viền đỏ, nút Thêm nhỏ hơn hẳn. Cùng chiều cao, cùng bo
   góc, khác nhau chỉ ở MỨC ĐỘ nhấn. */
.ci-nut { display: inline-flex; align-items: center; justify-content: center; gap: 6px;
          height: 34px; padding: 0 14px; border-radius: 8px; border: 1px solid var(--border);
          background: var(--surface); color: var(--text-2); cursor: pointer;
          font-family: inherit; font-size: 13px; font-weight: 500; white-space: nowrap; }
.ci-nut:hover:not(:disabled) { background: var(--bg); border-color: var(--border-strong); }
.ci-nut:active:not(:disabled) { transform: translateY(1px); }
.ci-nut:disabled { opacity: .55; cursor: not-allowed; }
/* Hành động chính — mỗi khối chỉ được có MỘT. */
.ci-nut.chinh { background: var(--primary); border-color: var(--primary); color: #fff; font-weight: 600; }
.ci-nut.chinh:hover:not(:disabled) { background: var(--primary-dark); border-color: var(--primary-dark); }
/* Hành động phá huỷ — viền đỏ, KHÔNG nền đỏ đặc: nền đặc hút mắt ngang nút chính. */
.ci-nut.nguyhiem { border-color: #FECACA; color: #991B1B; }
.ci-nut.nguyhiem:hover:not(:disabled) { background: #FEF2F2; }
```

Rồi đổi các nút cũ sang hệ này. `.ci-nut-nhom button` chỉ còn lo khoảng cách:

```css
.ci-nut-nhom { display: flex; gap: 6px; flex: none; align-items: center; }
```

Xoá hẳn khối `.ci-nut-nhom button { … }` và `.ci-nut-xoa { … }` cũ.

- [ ] **Bước 5: Đổi JSX sang hệ nút mới**

`wwwroot/pages/chat-inbox.jsx` — trong `.ci-nut-nhom` ở đầu khung chat:

```jsx
                    <button className="ci-nut" onClick={nhanViec}>{v.assignedUsername ? 'Bỏ nhận' : 'Nhận việc'}</button>
                    <button className="ci-nut" onClick={batTatBot}>{v.botPaused ? 'Cho bot nói lại' : 'Tạm dừng bot'}</button>
                    {v.status !== 2
                      ? <button className="ci-nut" onClick={() => doiTrangThai(2)}>Đóng</button>
                      : <button className="ci-nut" onClick={() => doiTrangThai(1)}>Mở lại</button>}
```

Trong `KhaiKenh`, đổi `className="btn-primary"` → `className="ci-nut chinh"` và `className="ci-nut-xoa"` → `className="ci-nut nguyhiem"`.

- [ ] **Bước 6: Thêm placeholder cho ô khai kênh**

`Endpoints/ChatInboxEndpoints.cs` — thêm tham số `Hint` vào record `ONhap`:

```csharp
    /// <param name="Type">"text" (điền sẵn lại được) · "secret" (KHÔNG bao giờ trả ra client) ·
    /// "note" (chỉ là dòng hướng dẫn, không phải ô nhập).</param>
    /// <param name="Hint">Chữ mờ trong ô — cho VÍ DỤ về định dạng, không lặp lại nhãn. Ô token
    /// dài mà không có ví dụ thì người khai không biết mình dán đúng thứ chưa.</param>
    private record ONhap(string Key, string Label, string Type = "text", string Hint = "");
```

Rồi điền `Hint` cho từng ô:

```csharp
        (ChatChannel.Zalo, "Zalo OA", new[]
        {
            new ONhap("label",        "Tên gợi nhớ", Hint: "OA Hà Nội"),
            new ONhap("appId",        "App ID", Hint: "1234567890123456789"),
            new ONhap("secretKey",    "Secret Key", "secret", "Lấy ở Zalo Developers → ứng dụng của bạn"),
            new ONhap("refreshToken", "Refresh Token", "secret", "Lấy sau bước cấp quyền OA"),
            new ONhap("note",
                "Đây là OA RIÊNG của chat, độc lập với OA khai cho bản tin sáng ở Tự động hoá.", "note"),
        }, false),
        (ChatChannel.Messenger, "Facebook Messenger", new[]
        {
            new ONhap("label",           "Tên gợi nhớ", Hint: "Trang chi nhánh Q1"),
            new ONhap("pageId",          "ID Trang", Hint: "102938475610293"),
            new ONhap("pageAccessToken", "Page Access Token", "secret", "EAAG… (lấy ở Meta for Developers)"),
            new ONhap("appSecret",       "App Secret", "secret", "Dùng để kiểm chữ ký webhook"),
            new ONhap("verifyToken",     "Verify Token", "secret", "Bạn tự đặt, dán y hệt vào Meta"),
        }, false),
        (ChatChannel.Telegram, "Telegram", new[]
        {
            new ONhap("label",         "Tên gợi nhớ", Hint: "Bot đội sale lẻ"),
            new ONhap("botToken",      "Bot token", "secret", "123456:ABC-DEF… (lấy từ @BotFather)"),
            new ONhap("webhookSecret", "Chuỗi bí mật webhook", "secret", "Bạn tự đặt, khai khi gọi setWebhook"),
        }, true),
```

Thêm `Hint` vào JSON trả về (trong handler `GET /channels`, phần `fields = oNhap` giữ nguyên vì record tự serialize đủ trường).

Frontend, trong `ONhap` của `chat-inbox.jsx`:

```jsx
          <input type={biMat ? 'password' : 'text'}
                 placeholder={biMat && daKhai ? 'để trống = giữ nguyên' : (truong.hint || '')}
                 value={giaTri}
                 onChange={e => dat(kenh, accId, truong.key, e.target.value)} />
```

- [ ] **Bước 7: Đổi khai kênh sang TAB**

Trong `KhaiKenh`, thêm state:

```jsx
    const [tab, setTab] = useState(0);   // số của kênh đang xem
```

Thay toàn bộ `<div className="ci-khai-luoi">…</div>` bằng:

```jsx
      <>
        {/* Tab thay vì đổ cả ba kênh ra một màn hình. Mỗi lần người dùng chỉ khai MỘT kênh, mà
            bày cả ba thì vừa phải cuộn vừa thêm một lớp viền bao quanh từng kênh. */}
        <div className="ci-tab">
          {ds.map(k => (
            <button key={k.channel} className={'ci-tab-nut' + (tab === k.channel ? ' on' : '')}
                    onClick={() => setTab(k.channel)}>
              <HuyHieuKenh kenh={k.channel} />
              {k.name}
              {k.accounts.length > 0 && <b>{k.accounts.length}</b>}
            </button>
          ))}
        </div>
        {ds.filter(k => k.channel === tab).map(k => (
          <div key={k.channel} className="ci-tab-noi">
            {k.webhookUrl && (
              <label className="ci-url">
                Địa chỉ nhận tin (dán vào trang quản trị của kênh)
                <input readOnly value={k.webhookUrl} onFocus={e => e.target.select()} />
              </label>
            )}
            {k.accounts.length === 0 && (
              <div className="ci-trong">Chưa nối tài khoản nào cho kênh này.</div>
            )}
            {k.accounts.map(t => (
              <details key={t.accountId} className="ci-tk">
                <summary>
                  <b>{t.label || 'Chưa đặt tên'}</b>
                  {t.configured
                    ? <span className="ci-xong">đã khai</span>
                    : <span className="ci-chua">thiếu khoá</span>}
                </summary>
                {!k.webhookUrl && (
                  <label className="ci-url">
                    Địa chỉ nhận tin của tài khoản này
                    <input readOnly value={t.webhookUrl} onFocus={e => e.target.select()} />
                  </label>
                )}
                {k.fields.map(f => (
                  <ONhap key={f.key} kenh={k.channel} accId={t.accountId} truong={f} daKhai
                         sanCo={t.values} />
                ))}
                <div className="ci-tk-nut">
                  <button className="ci-nut chinh" disabled={dangLuu === k.channel + ':' + t.accountId}
                          onClick={() => luu(k.channel, t.accountId)}>
                    {dangLuu === k.channel + ':' + t.accountId ? 'Đang lưu…' : 'Lưu'}
                  </button>
                  <button className="ci-nut nguyhiem" onClick={() => xoa(k.channel, t.accountId, t.label)}>
                    Gỡ kết nối
                  </button>
                </div>
              </details>
            ))}
            <details className="ci-tk ci-tk-moi">
              <summary><b>+ Thêm tài khoản</b></summary>
              {k.fields.map(f => (
                <ONhap key={f.key} kenh={k.channel} accId={null} truong={f} daKhai={false} />
              ))}
              <button className="ci-nut chinh" disabled={dangLuu === k.channel + ':moi'}
                      onClick={() => luu(k.channel, null)}>
                {dangLuu === k.channel + ':moi' ? 'Đang thêm…' : 'Thêm tài khoản'}
              </button>
            </details>
          </div>
        ))}
      </>
```

- [ ] **Bước 8: CSS cho tab, và bỏ bớt một lớp viền**

`wwwroot/styles.css` — thay khối `.ci-khai-luoi` và `.ci-kenh-the` bằng:

```css
/* Tab kênh. Bỏ hẳn .ci-kenh-the (thẻ bao quanh từng kênh) — với tab thì mỗi lúc chỉ có một
   kênh trên màn hình, thẻ bao quanh chỉ còn là một lớp viền thừa. */
.ci-tab { display: flex; gap: 4px; border-bottom: 1px solid var(--border); margin-bottom: 14px; }
.ci-tab-nut { display: inline-flex; align-items: center; gap: 7px; padding: 9px 13px;
              border: 0; border-bottom: 2px solid transparent; background: transparent;
              cursor: pointer; font-family: inherit; font-size: 13.5px; color: var(--text-3);
              margin-bottom: -1px; }
.ci-tab-nut:hover { color: var(--text-2); }
.ci-tab-nut.on { color: var(--text); font-weight: 600; border-bottom-color: var(--primary); }
.ci-tab-nut.on .ci-hh { border-color: var(--primary); color: var(--ci-cam-800); }
.ci-tab-nut b { background: var(--bg); border-radius: 999px; padding: 0 6px; font-size: 11px;
                line-height: 17px; color: var(--text-3); }
.ci-tab-nut.on b { background: var(--primary-soft); color: var(--ci-cam-800); }
.ci-tab-noi { display: grid; gap: 2px; }
```

Xoá các khối cũ: `.ci-khai-luoi`, `.ci-kenh-the`, `.ci-kenh-ten`, `.ci-so-tk`.

- [ ] **Bước 9: Chạy test — phải XANH**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```

- [ ] **Bước 10: Nhìn bằng mắt**

```bash
.\build-frontend.ps1
dotnet build TourkitAiProxy.csproj
dotnet run --project TourkitAiProxy.csproj --no-build
```

Mở `/chat-inbox` → "Kết nối kênh". Kiểm:
- Ba tab, mỗi lúc chỉ một kênh hiện — **không còn ba thẻ cạnh nhau**.
- Bấm vào ô soạn: **một** viền cam, không phải hai.
- Mọi nút cùng chiều cao 34px; mỗi khối chỉ một nút cam.
- Ô "Bot token" có chữ mờ ví dụ.

- [ ] **Bước 11: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx wwwroot/styles.css Endpoints/ChatInboxEndpoints.cs \
        TourkitAiProxy.Tests/Chat/ChatUiGuardTests.cs
git commit -m "refactor(chat): tab cho khai kênh, một hệ nút, hết viền lồng viền

Bốn lỗi người dùng chỉ đích danh:
- Ô soạn có hai viền: .ci-soan-o đã đổi màu viền khi focus mà textarea bên
  trong còn nhận outline từ quy tắc focus-visible chung.
- Nút bốn cỡ ba kiểu → một lớp .ci-nut, khác nhau chỉ ở mức độ nhấn.
- Ô nhập token dài mà không có ví dụ định dạng → thêm Hint.
- Khai kênh đổ cả ba kênh ra một màn hình, ba lớp viền lồng nhau → tab, bỏ
  hẳn thẻ bao quanh từng kênh.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Kiểm lại sau khi xong cả 4 task

- [ ] `dotnet test` — toàn bộ PASS
- [ ] Tắt cờ `Features__Chat=false` → 5 đường chat (kể cả `/quick-replies` mới) đều 404, Trợ lý số liệu vẫn sống
- [ ] Cập nhật `CLAUDE.md`: bảng schema chat có thêm `chat_inbound_events`; mục "Đường đi" đổi thành `webhook → ghi CSDL → ChatInboundWorker → bot → chat_outbox → ChatOutboxWorker`
- [ ] Cập nhật `CHANGELOG.md` theo giọng người dùng cuối
- [ ] `codegraph sync`

## Việc còn treo (không thuộc plan này)

- **SQL Server chập chờn** — 14 lần lỗi `Error Number:258` trong một phiên chạy; `/api/v1/quota` và `/api/v1/insights/unread-count` từng mất 15 giây rồi trả 500. Khoá kết nối kênh nằm ở SQL Server, nên lúc nó timeout thì webhook **từ chối tin thật của khách** mà log trông giống "chữ ký sai". Cần chẩn đoán riêng.
- **Chưa từng nối Zalo OA hay Facebook Page thật.** Nhiều giả định trong spec (cửa sổ gửi, callback đã xem, lấy hồ sơ khách) vẫn là phỏng đoán. Nên làm trước khi xây tiếp hạ tầng.
