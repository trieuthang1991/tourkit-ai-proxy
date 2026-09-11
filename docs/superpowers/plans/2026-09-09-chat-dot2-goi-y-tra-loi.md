# Hộp thư chat — Đợt 2: AI soạn gợi ý trả lời (mục 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nhân viên bấm một nút để nhận bản nháp trả lời do AI soạn, đổ vào ô soạn, sửa rồi tự gửi — **không bao giờ tự gửi**.

**Architecture:** Bot đã sinh câu trả lời bằng `GenerateReplyAsync` trong `ChatInboundService` (khung an toàn cấm bịa + lời dặn công ty + N tin gần nhất). Tách đúng bộ sinh đó ra lớp `ChatReplyComposer` để cả worker lẫn một endpoint mới cùng gọi. Endpoint `POST /conversations/{id}/goi-y` chỉ **trả chữ**, không đụng hàng đợi gửi. Nút trên ô soạn gọi endpoint rồi `setSoan(text)`.

**Tech Stack:** .NET 8 minimal API · `ProviderRegistry`/`AiModelRegistry`/`AiCallContext` (có sẵn) · React/Babel UMD · xUnit · Playwright.

**Spec:** [docs/superpowers/specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §4.

## Global Constraints

- Chữ hiển thị, log, chú thích: tiếng Việt. Ngày giờ UTC kèm `Z`.
- `CHANGELOG.md` bắt buộc, viết cho người dùng cuối.
- `codegraph impact GenerateReplyAsync` trước khi tách (dự kiến: 1 caller, dòng 312 cùng file). `codegraph impact ChatInboundService` — ctor đổi; không test nào `new ChatInboundService(...)` (đã soát), DI ở `WorkflowStackRegistration.cs:218`.
- Route mới **không có thân** → **không khai tham số thân** (bẫy Content-Type). Mọi thứ cần đã có ở route + phiên.
- **Chốt cứng của đợt này:** handler `goi-y` không được gọi `AppendMessageAsync`, `EnqueueOutboxAsync`, hay bất kỳ adapter `SendAsync` nào — có chốt canh (Task 3).
- Máy chủ khoá DLL: `taskkill //IM TourkitAiProxy.exe //F` trước build/test. Toàn bộ `dotnet test` không lọc ở cuối mỗi task.
- E2E: worker chat tắt; cấm `/send`; chạy `scratchpad/chay-e2e-phancong.ps1`.
- Chốt canh mới phải chứng minh ĐỎ rồi khôi phục.
- Commit trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; không commit 5 tệp đang dở.

**Giả định đã chốt (spec §10 chưa trả lời):**
- Bộ sinh **chưa đọc CRM** — giữ y nguyên khung hiện tại. Cho đọc CRM là đợt riêng sau khi mục 4 xong.
- Nút **luôn hiện** khi ô soạn mở (không gác theo trạng thái bot). Bot đang bật mà nhân viên gửi bản nháp thì luật im sẵn có (`muteMinutes`) tự chạy — không cần thêm điều kiện.
- Trả **một lần**, không stream: tin chat 2–4 câu, stream chỉ thêm một cơ chế.

---

## File Structure

| Tệp | Trách nhiệm |
|---|---|
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | `TachCauHoiCuoi` — luật thuần: lấy tin khách MỚI NHẤT làm câu hỏi, phần trước làm lịch sử |
| `TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs` (mới) | bộ sinh câu trả lời — `SinhAsync` (chuyển từ `GenerateReplyAsync`) + `GoiYAsync` |
| `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs` | gọi `ChatReplyComposer.SinhAsync` thay vì hàm riêng |
| `TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs` | đăng ký `ChatReplyComposer` |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `POST /conversations/{id:long}/goi-y` |
| `wwwroot/pages/chat-inbox.jsx` | nút **Gợi ý** trong `.ci-soan-nut` |
| `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs` | test `TachCauHoiCuoi` |
| `TourkitAiProxy.Tests/Chat/ChatSuggestGuardTests.cs` (mới) | chốt: goi-y không gửi |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `F — Gợi ý trả lời` |
| `CHANGELOG.md` | một mục |

---

### Task 1: Luật thuần — tin nào là "câu hỏi", tin nào là "lịch sử"

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs` (đặt ngay trước `BuildConversationPrompt`, dòng 182)
- Test: `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs`

**Interfaces:**
- Produces: `public static (string? CauHoi, List<ChatMessage> Truoc) TachCauHoiCuoi(IEnumerable<ChatMessage> tin)`.

- [ ] **Step 1: Viết test ĐỎ** — thêm vào cuối lớp `ChatRulesTests`:

```csharp
    private static ChatMessage Tin(short huong, string? body, short state = (short)ChatState.Sent, short kind = (short)ChatKind.Text)
        => new() { Direction = huong, Body = body, State = state, Kind = kind };

    [Fact]
    public void Tach_cau_hoi_cuoi__tin_khach_moi_nhat_la_cau_hoi__phan_truoc_la_lich_su()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
            Tin((short)ChatDirection.In,  "Tháng 10, 4 người"),
        };
        var (cauHoi, truoc) = ChatRules.TachCauHoiCuoi(ds);
        Assert.Equal("Tháng 10, 4 người", cauHoi);
        Assert.Equal(2, truoc.Count);
        Assert.Equal("Dạ anh đi tháng mấy ạ?", truoc[1].Body);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__minh_vua_tra_loi_xong_thi_KHONG_co_gi_de_goi_y()
    {
        // Tin mới nhất là của MÌNH → khách chưa nói gì thêm. Gợi ý lúc này là gợi ý trả lời cho
        // một câu đã được trả lời — sinh ra câu thứ hai chồng lên câu thứ nhất.
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
        };
        var (cauHoi, _) = ChatRules.TachCauHoiCuoi(ds);
        Assert.Null(cauHoi);
    }

    [Fact]
    public void Tach_cau_hoi_cuoi__bo_qua_tin_hong_va_tin_khong_co_chu()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Câu thật"),
            Tin((short)ChatDirection.In,  null, kind: (short)ChatKind.Image),     // ảnh, không chữ
            Tin((short)ChatDirection.In,  "gửi hỏng", state: (short)ChatState.Failed),
        };
        var (cauHoi, truoc) = ChatRules.TachCauHoiCuoi(ds);
        Assert.Equal("Câu thật", cauHoi);
        Assert.Empty(truoc);
    }
```

- [ ] **Step 2: Chạy — ĐỎ** (`TachCauHoiCuoi` chưa có → lỗi biên dịch).

- [ ] **Step 3: Viết hàm**

```csharp
    /// <summary>
    /// Từ một đoạn hội thoại (cũ → mới), tách ra CÂU HỎI để trả lời và LỊCH SỬ đứng trước nó.
    ///
    /// <para>Câu hỏi = tin CÓ CHỮ mới nhất, và nó phải là của KHÁCH. Tin mới nhất là của mình thì
    /// trả <c>null</c>: khách chưa nói gì thêm, gợi ý lúc này là sinh câu thứ hai chồng lên câu
    /// vừa gửi. Tin hỏng và tin không chữ (ảnh, sticker) bỏ qua ở cả hai phía — cùng luật với
    /// <see cref="BuildConversationPrompt"/>.</para>
    /// </summary>
    public static (string? CauHoi, List<ChatMessage> Truoc) TachCauHoiCuoi(IEnumerable<ChatMessage> tin)
    {
        var coChu = tin
            .Where(m => m.State != (short)ChatState.Failed && !string.IsNullOrWhiteSpace(m.Body))
            .ToList();
        if (coChu.Count == 0) return (null, new List<ChatMessage>());
        var cuoi = coChu[^1];
        if (cuoi.Direction != (short)ChatDirection.In) return (null, coChu);
        return (cuoi.Body!.Trim(), coChu.Take(coChu.Count - 1).ToList());
    }
```

- [ ] **Step 4: Chạy toàn bộ — XANH. Commit**

```bash
git add TourkitAiProxy.Domain/Chat/ChatRules.cs TourkitAiProxy.Tests/Chat/ChatRulesTests.cs
git commit -m "feat(chat): luật tách câu hỏi cuối của khách khỏi lịch sử — nền cho gợi ý trả lời

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Tách bộ sinh ra `ChatReplyComposer`; worker gọi lại nó

**Files:**
- Create: `TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs`
- Modify: `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs` — bỏ `GenerateReplyAsync` (dòng 583–640) và `DefaultSystemPrompt`; ctor nhận `ChatReplyComposer`; dòng 312 gọi `_soan.SinhAsync`
- Modify: `TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs:218`

**Interfaces:**
- Produces: `ChatReplyComposer.SinhAsync(string tenantId, long hoiThoaiId, string nhacLai, ChatBotSettings cfg, CancellationToken ct) → Task<string?>` và `GoiYAsync(string tenantId, long hoiThoaiId, ChatBotSettings cfg, CancellationToken ct) → Task<string?>`.

- [ ] **Step 1: `codegraph impact GenerateReplyAsync`** — ghi số caller (dự kiến 1).

- [ ] **Step 2: Tạo lớp mới** — chép NGUYÊN VĂN thân `GenerateReplyAsync` và hằng `DefaultSystemPrompt` từ ChatInboundService.cs:589–640 vào đây (đổi tên hàm thành `SinhAsync`, giữ toàn bộ chú thích), rồi thêm `GoiYAsync`:

```csharp
// Services/Chat/Inbox/ChatReplyComposer.cs
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Infrastructure.Chat.Inbox;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Bộ sinh câu trả lời cho khách — MỘT chỗ cho cả hai người dùng nó:
///   · worker <see cref="ChatInboundService"/>: bot trả lời tự động khi khách nhắn;
///   · endpoint <c>POST /conversations/{id}/goi-y</c>: nhân viên bấm xin bản nháp.
///
/// <para>Tách ra để hai đường cùng một khung an toàn (cấm bịa giá/lịch/số chỗ), cùng lời dặn
/// công ty, cùng model. Trước 09/09/2026 bộ sinh nằm riêng trong worker nên gợi ý cho người phải
/// dựng lối thứ hai — và hai lối thì sớm muộn lệch nhau.</para>
/// </summary>
public class ChatReplyComposer
{
    private readonly ChatRepository _repo;
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly AiCallContext _aiCtx;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatReplyComposer> _log;

    public ChatReplyComposer(ChatRepository repo, ProviderRegistry providers, AiModelRegistry models,
        AiCallContext aiCtx, IConfiguration cfg, ILogger<ChatReplyComposer> log)
    { _repo = repo; _providers = providers; _models = models; _aiCtx = aiCtx; _cfg = cfg; _log = log; }

    /// <summary>
    /// Bản nháp cho nhân viên: đọc đoạn hội thoại, lấy tin khách mới nhất làm câu hỏi, sinh câu
    /// trả lời. <c>null</c> khi khách chưa nói gì mới (tin cuối là của mình) hoặc AI hỏng.
    /// CHỈ TRẢ CHỮ — không ghi tin, không xếp hàng gửi. Xem ChatSuggestGuardTests.
    /// </summary>
    public async Task<string?> GoiYAsync(string tenantId, long hoiThoaiId, ChatBotSettings cfg, CancellationToken ct)
    {
        // Lấy dư rồi mới lọc — cùng lý do với worker: hàm dựng bỏ tin hỏng và tin không chữ.
        var lichSu = await _repo.ListMessagesAsync(tenantId, hoiThoaiId, cfg.HistoryTurns * 2 + 2, ct);
        var (cauHoi, truoc) = ChatRules.TachCauHoiCuoi(lichSu);
        if (cauHoi is null) return null;
        var nhacLai = ChatRules.BuildConversationPrompt(truoc, cauHoi, cfg.HistoryTurns);
        return await SinhAsync(tenantId, hoiThoaiId, nhacLai, cfg, ct);
    }

    /// <summary>Sinh câu trả lời. (Chép nguyên từ ChatInboundService.GenerateReplyAsync — giữ chú thích gốc.)</summary>
    public async Task<string?> SinhAsync(string tenantId, long hoiThoaiId, string cauHoi,
        ChatBotSettings cfgBot, CancellationToken ct)
    {
        // … THÂN HÀM chép nguyên văn từ ChatInboundService.GenerateReplyAsync (khung, _aiCtx.Push,
        // _models.Resolve(AiFeature.ChatInbox), provider.CompleteAsync với MaxTokens 700,
        // Temperature 0.5, ba nhánh catch) — KHÔNG sửa một chữ trong thân.
    }

    // … hằng DefaultSystemPrompt chép nguyên văn (kể cả chú thích "Cấm bịa số là dòng quan trọng nhất").
}
```

> Người thực hiện: mở ChatInboundService.cs:589–640, **cắt** (không chép) thân hàm và hằng sang đây; đổi `_log` kiểu `ILogger<ChatReplyComposer>`. Nếu thiếu `using` cho `IConfiguration`/`ILogger` thì thêm `using Microsoft.Extensions.Configuration; using Microsoft.Extensions.Logging;` — kiểm bằng `dotnet build`.

- [ ] **Step 3: Worker gọi lại** — trong `ChatInboundService`:
  - thêm trường `private readonly ChatReplyComposer _soan;` và tham số ctor `ChatReplyComposer soan` (gán `_soan = soan;`);
  - dòng 312: `var traLoi = await _soan.SinhAsync(tenantId, hoiThoai.Id, nhacLai, cfgBot, ct);`
  - xoá `GenerateReplyAsync` và `DefaultSystemPrompt` khỏi lớp; giữ lại `_providers`, `_models`, `_aiCtx`, `_cfg` **nếu** còn chỗ khác dùng — `grep -n "_providers\|_models\|_aiCtx\." ChatInboundService.cs`; không còn ai dùng thì bỏ cả trường lẫn tham số ctor để không giữ phụ thuộc chết.

- [ ] **Step 4: DI** — `WorkflowStackRegistration.cs:218` thêm ngay trước:

```csharp
        s.AddSingleton<Chat.Inbox.ChatReplyComposer>();
```

- [ ] **Step 5: Build, toàn bộ test XANH** (các chốt scan ChatInboundService không đụng tới hàm này — đã soát: `MarkStateWatermarkAsync`, `MarkSeenAsync`, `EnqueueOutboxAsync`).

- [ ] **Step 6: Kiểm hành vi bot không đổi** — bật máy chủ với worker **bật** trên máy cá nhân là gửi tin thật → **KHÔNG làm**. Thay vào đó đọc lại diff: `SinhAsync` là chép nguyên văn; lượt gọi ở 312 cùng tham số. Ghi vào commit message rằng hành vi worker giữ nguyên theo diff.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs
git commit -m "refactor(chat): tách bộ sinh câu trả lời ra ChatReplyComposer — worker và gợi ý dùng chung

Thân SinhAsync chép nguyên văn từ GenerateReplyAsync; lượt gọi ở worker giữ
nguyên tham số. Hành vi bot không đổi theo diff.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Endpoint `POST /conversations/{id}/goi-y` + chốt "không gửi"

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` — đặt ngay sau route `link-crm` (kết thúc ~dòng 1757), trước `/audit`
- Test: `TourkitAiProxy.Tests/Chat/ChatSuggestGuardTests.cs` (mới)

**Interfaces:**
- Consumes: `ChatReplyComposer.GoiYAsync`, `ChatBotSettingsRepository.GetAsync(tenant, ct)`.
- Produces: `POST /api/v1/chat/conversations/{id}/goi-y` → `200 {text}` · `422 {error}` khi không có gì để gợi ý hoặc AI im · `404` theo luật xem.

- [ ] **Step 1: Chốt canh ĐỎ trước**

```csharp
// TourkitAiProxy.Tests/Chat/ChatSuggestGuardTests.cs
using System;
using System.Linq;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Gợi ý trả lời CHỈ TRẢ CHỮ. Đây là chỗ duy nhất trong hệ mà một dòng gọi nhầm là AI nói thẳng
/// với khách hàng thật bằng lời chưa ai đọc — nên chốt cứng ngay ở mã, không dựa vào giao diện.
/// </summary>
public class ChatSuggestGuardTests
{
    private static string Than()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        src = string.Join("\n", src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var moc = "MapPost(\"/conversations/{id:long}/goi-y\"";
        var i = src.IndexOf(moc, StringComparison.Ordinal);
        Assert.True(i >= 0, "Chưa có route goi-y");
        var sau = src.Substring(i);
        var het = sau.IndexOf("\n        g.Map", 10, StringComparison.Ordinal);
        return het > 0 ? sau.Substring(0, het) : sau;
    }

    [Fact]
    public void Goi_y_khong_duoc_ghi_tin_hay_xep_hang_gui()
    {
        var than = Than();
        Assert.Contains("GoiYAsync", than);
        foreach (var cam in new[] { "AppendMessageAsync", "EnqueueOutboxAsync", "SendAsync(", "SendTextAsync(" })
            Assert.False(than.Contains(cam, StringComparison.Ordinal),
                $"Route goi-y đang gọi {cam} — gợi ý phải CHỈ trả chữ, nhân viên tự bấm gửi.");
    }

    [Fact]
    public void Goi_y_di_qua_luat_xem_nhu_moi_duong_hoi_thoai_khac()
    {
        var than = Than();
        Assert.Contains("ReadNguoiXemAsync", than);
        Assert.Contains("GetConversationAsync(a.TenantId, id, xem, ct)", than);
    }
}
```

- [ ] **Step 2: Chạy — ĐỎ** ("Chưa có route goi-y").

- [ ] **Step 3: Viết route**

```csharp
        // ── AI soạn gợi ý trả lời ────────────────────────────────────────────
        //
        // KHÔNG có thân: mọi thứ cần đã nằm ở đường dẫn và phiên. Khai tham số thân ở đây là dính
        // bẫy Content-Type ở tầng định tuyến (xem chú thích ở POST .../assign/me).
        //
        // CHỈ TRẢ CHỮ. Không ghi tin, không xếp hàng gửi — chốt bằng ChatSuggestGuardTests. Bản
        // nháp đổ vào ô soạn, nhân viên đọc, sửa, rồi tự bấm gửi; lúc đó luật "bot im sau khi
        // người thật trả lời" tự chạy như mọi tin tay khác.
        g.MapPost("/conversations/{id:long}/goi-y", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatAssignRepository assign,
            ChatBotSettingsRepository cauHinhBot, Services.Chat.Inbox.ChatReplyComposer soan,
            CancellationToken ct) =>
        {
            var p = await SessionAuth.ReadNguoiXemAsync(ctx, sessions, ct);
            if (p == null) return SessionAuth.Unauthorized();
            var (a, xem) = p.Value;
            if (!repo.Configured) return NotConfigured();
            if (await repo.GetConversationAsync(a.TenantId, id, xem, ct) is null) return Results.NotFound();

            // Dùng lời dặn + số tin nhớ của công ty, kể cả khi bot đang TẮT: tắt bot là "đừng tự
            // trả lời khách", không phải "đừng giúp nhân viên".
            var cfg = await cauHinhBot.GetAsync(a.TenantId, ct);
            var text = await soan.GoiYAsync(a.TenantId, id, cfg, ct);
            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { error = "Chưa có tin mới nào của khách để gợi ý, hoặc trợ lý đang bận — thử lại sau." },
                    statusCode: 422);
            return Results.Json(new { text }, Web);
        });
```

- [ ] **Step 4: Chạy chốt — XANH; toàn bộ test — XANH.**

- [ ] **Step 5: Chứng minh chốt đỏ** — tạm thêm dòng `await repo.AppendMessageAsync(` (sai kiểu cũng được, chỉ cần chuỗi) vào route, chạy chốt → ĐỎ; khôi phục, `md5sum` khớp.

- [ ] **Step 6: Gọi thật** — máy chủ worker tắt, phiên admin staging, hội thoại có tin khách mới nhất:

```
curl -s -X POST http://localhost:5080/api/v1/chat/conversations/<id>/goi-y -H "X-Session-Id: <sid>"
```
Expected: `{"text":"…"}` 2–4 câu tiếng Việt, xưng "em"; với hội thoại mà tin cuối là của mình → 422 kèm câu lỗi. **Kiểm CSDL**: `SELECT COUNT(*) FROM chat_messages WHERE conversation_id=<id>` không đổi trước/sau.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/ChatSuggestGuardTests.cs
git commit -m "feat(chat): POST /conversations/{id}/goi-y — AI soạn bản nháp, CHỈ trả chữ

Chốt canh: route không được ghi tin hay xếp hàng gửi (đã chứng minh đỏ).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Nút **Gợi ý** trên ô soạn

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — hàng `.ci-soan-nut` (~dòng 3405, cạnh nút `+ Nút` và nút mẫu)

**Interfaces:**
- Consumes: `POST …/goi-y` → `{text}` | 422 `{error}`.

- [ ] **Step 1: State** — cạnh `const [soan, setSoan] = useState('');` (dòng 2336):

```jsx
    const [dangGoiY, setDangGoiY] = useState(false);
```

- [ ] **Step 2: Hàm gọi** — đặt cạnh hàm `gui()` của ô soạn:

```jsx
    async function xinGoiY() {
      if (!chon || dangGoiY) return;
      setDangGoiY(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/goi-y', { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không lấy được gợi ý', 'error'); return; }
        // ĐỔ VÀO Ô SOẠN, không gửi. Có chữ đang gõ dở thì nối xuống dưới chứ không xoá.
        setSoan(cu => (cu.trim() ? cu.trimEnd() + '\n' : '') + (j.text || ''));
      } catch { pushToast('Không lấy được gợi ý', 'error'); }
      finally { setDangGoiY(false); }
    }
```

- [ ] **Step 3: Nút** — trong `<div className="ci-soan-nut">`, ngay sau nút `+ Nút`:

```jsx
                          {/* Gợi ý của trợ lý ĐỔ VÀO ô soạn, không gửi. Cùng bộ sinh với bot tự
                              trả lời, nên cùng luật cấm bịa giá/lịch/số chỗ. */}
                          <button className={'mau' + (dangGoiY ? ' dang-lam' : '')} disabled={dangGoiY}
                                  onClick={xinGoiY} title="Trợ lý soạn bản nháp, bạn sửa rồi gửi">
                            {dangGoiY ? 'Đang soạn…' : 'Gợi ý'}
                          </button>
```

- [ ] **Step 4: Dựng bundle, khởi động lại, kiểm tay** — mở hội thoại có tin khách mới nhất → bấm *Gợi ý* → chữ vào ô, **không có tin nào gửi đi** (khung tin không thêm dòng); bấm ở hội thoại mình vừa trả lời → toast câu lỗi 422.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx
git commit -m "feat(chat): nút Gợi ý trên ô soạn — bản nháp của trợ lý, nhân viên sửa rồi tự gửi

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: E2E + CHANGELOG

**Files:**
- Modify: `e2e/tests/07-chat-phan-cong-api.spec.js` — nhóm `F — Gợi ý trả lời`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: E2E** (không gọi AI thật trong bài đo luật; AI có thể chậm/hết lượt — chỉ đo **hình dạng** và **luật xem**):

```js
test.describe('F — Gợi ý trả lời', () => {
  test('F1 — goi-y trả JSON (200 hoặc 422), không bao giờ HTML, và KHÔNG ghi tin nào', async () => {
    const truoc = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    const soTinTruoc = (truoc.json.messages || []).length;

    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/goi-y`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.laHtml, 'goi-y trả HTML — request không tới handler').toBe(false);
    expect([200, 422], `mã ${r.ma}`).toContain(r.ma);
    if (r.ma === 200) expect(typeof r.json.text).toBe('string');

    const sau = await doc(await api.get(`${GOC}/conversations/${maHoiThoai}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect((sau.json.messages || []).length, 'goi-y đã GHI tin — phải chỉ trả chữ').toBe(soTinTruoc);
  });

  test('F2 — nhân viên không có quyền xem hội thoại thì goi-y là 404, không phân biệt với id lạ', async () => {
    // Hội thoại thử đang thuộc người khác/chưa giao (xem C3) → nhân viên thường không thấy.
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/goi-y`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    expect(r.ma).toBe(404);
  });
});
```
> F2 dựa trên trạng thái hội thoại thử giống bài C3; nếu C3 có bước gán trước, chép cùng bước.

- [ ] **Step 2: CHANGELOG** — dưới `### ✨ Tính năng mới` của mục 09/09/2026:

```markdown
- **Trợ lý soạn giúp bản nháp trả lời.** Trên ô soạn có nút *Gợi ý*: trợ lý đọc đoạn hội thoại
  và viết sẵn câu trả lời cho tin mới nhất của khách, đổ vào ô soạn để bạn sửa rồi tự bấm gửi.
  Trợ lý **không bao giờ tự gửi** — và vẫn giữ đúng luật cũ: không bịa giá, lịch khởi hành hay
  số chỗ. Bạn vừa trả lời xong mà khách chưa nói gì thêm thì nút sẽ báo là chưa có gì để gợi ý.
```

- [ ] **Step 3: Chạy toàn bộ** — `dotnet test` xanh; e2e nhóm 07 → `19 passed` (17 + 2).

- [ ] **Step 4: Commit**

```bash
git add e2e/tests/07-chat-phan-cong-api.spec.js CHANGELOG.md
git commit -m "test(e2e)+docs: gợi ý trả lời — chỉ trả chữ, đi theo luật xem

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** §4 "cùng bộ sinh" → Task 2; "một nút, bấm mới sinh" → Task 4; "đổ vào ô soạn, không gửi — chốt cứng" → Task 3 (+ chốt canh, + F1 đo số tin không đổi); "cần tách hàm khỏi worker" → Task 2; hai câu hỏi §4 → ghi giả định ở đầu.
- **Placeholder:** Task 2 bước 2 cố ý ghi "chép nguyên văn" thay vì chép lại 50 dòng — đó là cắt-dán, không phải "làm tương tự"; người thực hiện có số dòng chính xác.
- **Nhất quán tên:** `SinhAsync`/`GoiYAsync`/`TachCauHoiCuoi` dùng thống nhất ở Task 1–3; route `goi-y` thống nhất ở Task 3–5.
