# Trợ lý hành động (/assistant + /travai) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho JARVIS/trợ lý (`/assistant` + `/travai`) thực hiện hành động — check/trả lời mail, review KH, chấm deal, giao việc/tạo lịch hẹn — với cổng xác nhận an toàn; hành động CRM chỉ enqueue bảng tạm để worker app-side đồng bộ.

**Architecture:** Mở rộng `ChatAgentService` planner để nhận `{action, params}` song song `{tool, params}` đọc. Backend re-resolve tên→id, phát `action-proposal`/`action-clarify`/`action-result` qua SSE. Endpoint `/action/execute` định tuyến theo `ActionKind`: Mail → service mail có sẵn; Internal → ReviewService/DealScoringService; CrmQueue → enqueue `dbo.CrmActionQueue` (payload khớp DTO thật TourKit.Api). Frontend render thẻ xác nhận + kết quả, dùng chung 2 trang.

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper + SQL Server, xUnit (`TourkitAiProxy.Tests`), no-build React (Babel dev / esbuild prod), SSE.

**Spec nguồn:** `docs/superpowers/specs/2026-07-14-assistant-action-tools-design.md`

---

## Cấu trúc file

**Tạo mới:**
- `Services/Crm/CrmActionQueueRepository.cs` — Dapper enqueue + list (clone `MailQueueRepository`).
- `Services/Chat/ActionTools.cs` — catalog action (song song `ChatTools`).
- `Services/Chat/ActionResolver.cs` — resolve tên→id (staff/customer/deal/workflow) + phát hiện mơ hồ.
- `Services/Chat/ActionExecutor.cs` — định tuyến execute theo `ActionKind`.
- `Endpoints/AssistantActionEndpoints.cs` — `POST /api/v1/assistant/action/execute`.
- `docs/crm-action-contract/README.md` — contract payload cho worker app-side.
- `wwwroot/components/action-confirm-card.jsx` — thẻ xác nhận + clarify list (dùng chung).
- Test: `TourkitAiProxy.Tests/CrmActionQueueTests.cs`, `ActionToolsTests.cs`, `ActionResolverTests.cs`, `ActionExecutorTests.cs`.

**Sửa:**
- `Services/Db/TourkitAiDb.cs` — thêm bảng `dbo.CrmActionQueue` vào `SchemaSql`.
- `Services/Chat/ChatModels.cs` — thêm DTO `ActionProposal`, `ActionResult`, `ActionExecuteRequest`.
- `Services/Chat/JsonPlannerAgent.cs` + `NativeToolUseAgent.cs` — nhúng `ActionTools` catalog vào prompt; nhận `{action,params}`.
- `Services/Chat/ChatAgentService.cs` — sau planner: nếu có `action` → nhánh action (resolve + proposal/execute) thay vì dispatch tool đọc.
- `Endpoints/WorkflowEndpoints.cs` — thêm `GET /api/v1/workflows/crm-queue`.
- `Program.cs` — DI `CrmActionQueueRepository`, `ActionResolver`, `ActionExecutor`; `MapAssistantActionEndpoints`.
- `wwwroot/pages/assistant.jsx` + `jarvis.jsx` — xử lý event action mới.
- `wwwroot/pages/workflows.jsx` — card "Hàng đợi CRM".
- `wwwroot/bundle-entry.js` — `import "./components/action-confirm-card.jsx";`
- `wwwroot/index.html` — `<script type="text/babel" src="components/action-confirm-card.jsx">`.
- `docs/database-schema.md`, `CLAUDE.md` — cập nhật.

---

## PHASE 1 — Bảng tạm CRM (schema + repo + queue viewer)

### Task 1: Schema `dbo.CrmActionQueue`

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs` (const `SchemaSql`, cạnh block `dbo.OutboundMails`)

- [ ] **Step 1: Thêm DDL vào `SchemaSql`**

Thêm ngay sau block `IF OBJECT_ID('dbo.OutboundMails' ...) END;`:

```sql
-- Hàng đợi HÀNH ĐỘNG CRM từ trợ lý (giao việc / tạo lịch hẹn). Proxy CHỈ enqueue;
-- worker app-side (toutkit-app) đọc Pending → POST /api/tasks | /api/customer-care → cập nhật Status.
-- PayloadJson khớp 1:1 CreateOrUpdateTaskingRequest / CreateCustomerCareRequest.
IF OBJECT_ID('dbo.CrmActionQueue', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmActionQueue (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        TenantId      NVARCHAR(64)   NOT NULL,
        Username      NVARCHAR(128)  NOT NULL,
        Kind          NVARCHAR(40)   NOT NULL,   -- 'assign-task' | 'create-appointment'
        PayloadJson   NVARCHAR(MAX)  NOT NULL,
        Status        TINYINT        NOT NULL CONSTRAINT DF_CrmActionQueue_Status  DEFAULT 0, -- 0=pending 1=processing 2=done 3=failed
        ResultJson    NVARCHAR(MAX)  NULL,
        RetryCount    INT            NOT NULL CONSTRAINT DF_CrmActionQueue_Retry   DEFAULT 0,
        ErrorMessage  NVARCHAR(1000) NULL,
        CreatedUtc    DATETIME2      NOT NULL CONSTRAINT DF_CrmActionQueue_Created DEFAULT SYSUTCDATETIME(),
        ProcessedUtc  DATETIME2      NULL,
        CONSTRAINT PK_CrmActionQueue PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_CrmActionQueue_Poll   ON dbo.CrmActionQueue(Status, CreatedUtc);
    CREATE INDEX IX_CrmActionQueue_Tenant ON dbo.CrmActionQueue(TenantId, Status, CreatedUtc);
END;
```

- [ ] **Step 2: Cập nhật log message** trong `EnsureSchemaAsync` — thêm `CrmActionQueue` vào chuỗi `_log.LogInformation("TourkitAiDb schema OK (...)")`.

- [ ] **Step 3: Build để chắc DDL không lỗi cú pháp C#**

Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add Services/Db/TourkitAiDb.cs
git commit -m "feat(crm): schema dbo.CrmActionQueue (hàng đợi hành động CRM)"
```

---

### Task 2: `CrmActionQueueRepository` + read-model (TDD)

**Files:**
- Create: `Services/Crm/CrmActionQueueRepository.cs`
- Test: `TourkitAiProxy.Tests/CrmActionQueueTests.cs`

- [ ] **Step 1: Viết record + status const + repo skeleton**

`Services/Crm/CrmActionQueueRepository.cs`:

```csharp
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Crm;

/// <summary>
/// Hàng đợi hành động CRM (dbo.CrmActionQueue). Proxy CHỈ enqueue + đọc cho monitor.
/// Worker app-side (toutkit-app) drain Pending → POST TourKit.Api → cập nhật Status.
/// Thuần Dapper, KHÔNG cache. Lỗi DB → throw.
/// </summary>
public class CrmActionQueueRepository
{
    private readonly TourkitAiDb _db;
    public CrmActionQueueRepository(TourkitAiDb db) => _db = db;

    /// Enqueue 1 hành động pending (Status=0). Trả Id mới.
    public async Task<long> EnqueueAsync(CrmActionInput a, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.CrmActionQueue (TenantId, Username, Kind, PayloadJson, Status, CreatedUtc)
VALUES (@TenantId, @Username, @Kind, @PayloadJson, 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { a.TenantId, a.Username, a.Kind, a.PayloadJson });
    }

    /// Đọc cho trang theo dõi (lọc Kind/Status, mới nhất trước).
    public async Task<List<CrmActionRow>> ListForMonitorAsync(
        string tenantId, string? kind, int? status, int take, CancellationToken ct = default)
    {
        if (take < 1) take = 1; if (take > 500) take = 500;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<CrmActionRow>(@"
SELECT TOP (@take)
    Id, TenantId, Username, Kind, PayloadJson, [Status], ResultJson,
    RetryCount, ErrorMessage, CreatedUtc, ProcessedUtc
FROM dbo.CrmActionQueue
WHERE TenantId = @tenantId
  AND (@kind IS NULL OR Kind = @kind)
  AND (@status IS NULL OR [Status] = @status)
ORDER BY Id DESC;",
            new { tenantId, kind, status, take });
        return rows.AsList();
    }
}

/// Input enqueue (Id/Status/CreatedUtc do DB sinh).
public record CrmActionInput(string TenantId, string Username, string Kind, string PayloadJson);

/// Read-model 1 dòng (monitor).
public record CrmActionRow(
    long Id, string TenantId, string Username, string Kind, string PayloadJson,
    byte Status, string? ResultJson, int RetryCount, string? ErrorMessage,
    DateTime CreatedUtc, DateTime? ProcessedUtc);

public static class CrmActionStatus
{
    public const byte Pending = 0, Processing = 1, Done = 2, Failed = 3;
}

public static class CrmActionKind
{
    public const string AssignTask = "assign-task";
    public const string CreateAppointment = "create-appointment";
}
```

- [ ] **Step 2: Viết test thuần (không cần DB) — kiểm hằng số + record shape**

`TourkitAiProxy.Tests/CrmActionQueueTests.cs`:

```csharp
using TourkitAiProxy.Services.Crm;
using Xunit;

public class CrmActionQueueTests
{
    [Fact]
    public void Kind_constants_match_worker_contract()
    {
        Assert.Equal("assign-task", CrmActionKind.AssignTask);
        Assert.Equal("create-appointment", CrmActionKind.CreateAppointment);
    }

    [Fact]
    public void Status_constants_are_stable()
    {
        Assert.Equal(0, CrmActionStatus.Pending);
        Assert.Equal(2, CrmActionStatus.Done);
        Assert.Equal(3, CrmActionStatus.Failed);
    }

    [Fact]
    public void Input_record_carries_required_fields()
    {
        var i = new CrmActionInput("t1", "user@x", CrmActionKind.AssignTask, "{\"name\":\"x\"}");
        Assert.Equal("t1", i.TenantId);
        Assert.Equal("assign-task", i.Kind);
    }
}
```

- [ ] **Step 3: Run test — verify pass**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter CrmActionQueueTests`
Expected: PASS (3 tests)

- [ ] **Step 4: Commit**

```bash
git add Services/Crm/CrmActionQueueRepository.cs TourkitAiProxy.Tests/CrmActionQueueTests.cs
git commit -m "feat(crm): CrmActionQueueRepository enqueue + monitor list"
```

---

### Task 3: DI + endpoint `GET /api/v1/workflows/crm-queue`

**Files:**
- Modify: `Program.cs` (đăng ký DI)
- Modify: `Endpoints/WorkflowEndpoints.cs` (thêm route, cạnh `/workflows/outbound-mails`)

- [ ] **Step 1: Đăng ký repo trong `Program.cs`**

Cạnh chỗ đăng ký `MailQueueRepository`:

```csharp
builder.Services.AddScoped<TourkitAiProxy.Services.Crm.CrmActionQueueRepository>();
```

- [ ] **Step 2: Thêm route đọc hàng đợi** trong `WorkflowEndpoints.cs`, copy pattern handler `/workflows/outbound-mails` (require `X-Session-Id` → resolve tenant qua `ITenantContext`):

```csharp
// GET /api/v1/workflows/crm-queue?kind=&status=&limit=50
g.MapGet("/crm-queue", async (
    HttpContext ctx, string? kind, int? status, int? limit,
    ITenantContext tenantCtx, CrmActionQueueRepository repo) =>
{
    var tenantId = await tenantCtx.ResolveAsync(ctx);          // pattern hiện có
    if (string.IsNullOrEmpty(tenantId)) return Results.Unauthorized();
    var items = await repo.ListForMonitorAsync(tenantId, kind, status, limit ?? 50, ctx.RequestAborted);
    return Results.Ok(new { items });
});
```

> Kiểm tra tên chính xác của `ITenantContext.ResolveAsync` trong handler `/outbound-mails` sẵn có và dùng đúng y hệt.

- [ ] **Step 3: Build + smoke test**

Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q`
Expected: `0 Error(s)`

Chạy app (`dotnet run`) → `curl -H "X-Session-Id: <valid>" http://localhost:5080/api/v1/workflows/crm-queue`
Expected: `{"items":[]}` (200) khi chưa có row; `401` khi thiếu session.

- [ ] **Step 4: Commit**

```bash
git add Program.cs Endpoints/WorkflowEndpoints.cs
git commit -m "feat(crm): GET /workflows/crm-queue (theo dõi hàng đợi CRM)"
```

---

## PHASE 2 — Action catalog + planner nhận `{action, params}`

### Task 4: `ActionTools` catalog (TDD)

**Files:**
- Create: `Services/Chat/ActionTools.cs`
- Test: `TourkitAiProxy.Tests/ActionToolsTests.cs`

- [ ] **Step 1: Viết catalog**

`Services/Chat/ActionTools.cs`:

```csharp
using System.Text;

namespace TourkitAiProxy.Services.Chat;

public enum ActionKind { Mail, Internal, CrmQueue }

/// 1 "action" = 1 hành động GHI/nghiệp vụ trợ lý có thể đề xuất. Song song ChatTools (read).
public record ActionTool(
    string Name, string Description, string[] Params,
    ActionKind Kind, bool NeedsConfirm, string Title);

/// Catalog action — NGUỒN DUY NHẤT cho prompt planner + dispatch executor.
public static class ActionTools
{
    public static readonly IReadOnlyList<ActionTool> All = new List<ActionTool>
    {
        new("check_mail",
            "Kiểm tra & tóm tắt mail MỚI (sync IMAP + liệt kê chưa đọc). Dùng khi user nói 'kiểm tra mail mới', 'có mail nào mới không'.",
            new[] { "limit" }, ActionKind.Mail, false, "Kiểm tra hộp thư"),

        new("send_mail_reply",
            "Soạn & GỬI trả lời cho 1 email của khách. Dùng khi 'trả lời khách X', 'phản hồi mail khiếu nại'. " +
            "params: mailId (hoặc mô tả để backend resolve), tone (lich_su|than_thien|dam_phan|xin_loi), instruction.",
            new[] { "mailId", "mailQuery", "tone", "instruction" }, ActionKind.Mail, true, "Trả lời email"),

        new("compose_mail",
            "Soạn & GỬI 1 email MỚI tới người nhận bất kỳ. params: to, subject, brief, tone.",
            new[] { "to", "subject", "brief", "tone" }, ActionKind.Mail, true, "Soạn email mới"),

        new("review_customer",
            "Đánh giá/xếp hạng 1 khách hàng (A–D + gợi ý). Dùng khi 'đánh giá khách X', 'review khách này'. " +
            "params: customerId (hoặc customerName để resolve), forceFresh.",
            new[] { "customerId", "customerName", "forceFresh" }, ActionKind.Internal, false, "Đánh giá khách hàng"),

        new("score_deal",
            "Chấm điểm 1 cơ hội bán hàng/deal. Dùng khi 'chấm deal X', 'đánh giá cơ hội của khách B'. " +
            "params: dealId (hoặc dealQuery để resolve).",
            new[] { "dealId", "dealQuery" }, ActionKind.Internal, false, "Chấm điểm deal"),

        new("assign_task",
            "GIAO VIỆC cho nhân viên. Dùng khi 'giao việc … cho …', 'tạo task cho nhân viên Y'. " +
            "params: workflowName, name, content, staffNames (CSV tên), prioritized(cao|tb|thap), dueDate, reminderMinutes.",
            new[] { "workflowName", "name", "content", "staffNames", "prioritized", "startDate", "dueDate", "reminderMinutes", "customerName", "bookingTicketId" },
            ActionKind.CrmQueue, true, "Giao việc"),

        new("create_appointment",
            "TẠO LỊCH HẸN CSKH cho khách. Dùng khi 'đặt lịch hẹn với khách X', 'hẹn tư vấn'. " +
            "params: customerName, careTitle, careDetail, startTime, endTime, reminderMinutes.",
            new[] { "customerName", "customerId", "careTitle", "careDetail", "startTime", "endTime", "reminderMinutes", "bookingTicketId" },
            ActionKind.CrmQueue, true, "Tạo lịch hẹn"),
    };

    public static ActionTool? Find(string? name)
        => string.IsNullOrEmpty(name) ? null
           : All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// Catalog gọn nhúng vào prompt planner.
    public static string CatalogForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var a in All)
        {
            var ps = a.Params.Length == 0 ? "(không tham số)" : string.Join(", ", a.Params);
            sb.Append("- ").Append(a.Name).Append(": ").Append(a.Description)
              .Append(" | params: ").Append(ps).Append('\n');
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Viết test**

`TourkitAiProxy.Tests/ActionToolsTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionToolsTests
{
    [Fact]
    public void Find_is_case_insensitive()
        => Assert.NotNull(ActionTools.Find("REVIEW_CUSTOMER"));

    [Fact]
    public void Mail_and_crm_actions_need_confirm_but_review_does_not()
    {
        Assert.True(ActionTools.Find("send_mail_reply")!.NeedsConfirm);
        Assert.True(ActionTools.Find("assign_task")!.NeedsConfirm);
        Assert.False(ActionTools.Find("review_customer")!.NeedsConfirm);
        Assert.False(ActionTools.Find("check_mail")!.NeedsConfirm);
    }

    [Fact]
    public void Catalog_lists_every_action()
    {
        var cat = ActionTools.CatalogForPrompt();
        foreach (var a in ActionTools.All) Assert.Contains(a.Name, cat);
    }

    [Fact]
    public void Kinds_are_correctly_assigned()
    {
        Assert.Equal(ActionKind.CrmQueue, ActionTools.Find("assign_task")!.Kind);
        Assert.Equal(ActionKind.Internal, ActionTools.Find("score_deal")!.Kind);
        Assert.Equal(ActionKind.Mail, ActionTools.Find("compose_mail")!.Kind);
    }
}
```

- [ ] **Step 3: Run test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ActionToolsTests`
Expected: PASS (4 tests)

- [ ] **Step 4: Commit**

```bash
git add Services/Chat/ActionTools.cs TourkitAiProxy.Tests/ActionToolsTests.cs
git commit -m "feat(assistant): ActionTools catalog (7 hành động, confirm flags)"
```

---

### Task 5: DTO action trong `ChatModels.cs`

**Files:**
- Modify: `Services/Chat/ChatModels.cs`

- [ ] **Step 1: Thêm DTO** (cuối file, namespace hiện có):

```csharp
/// Field sửa được hiển thị trên thẻ xác nhận.
public record ActionField(string Key, string Label, string? Value, string Type = "text"); // type: text|textarea|datetime|select

/// Đề xuất hành động cần user xác nhận (kind=action-proposal).
public record ActionProposal(
    string ActionId, string Action, string Title, string Summary,
    Dictionary<string, object?> Params, List<ActionField> Fields,
    bool NeedsConfirm, string? Estimate = null);

/// Yêu cầu chọn khi resolve mơ hồ (kind=action-clarify).
public record ActionClarify(string ActionId, string Action, string Question, List<ActionChoice> Choices);
public record ActionChoice(string Id, string Label, string? Hint = null);

/// Kết quả sau execute (kind=action-result). Data = ChatData giàu (thẻ review) hoặc null.
public record ActionResult(string Action, string Message, ChatData? Data = null, string? Warning = null);

/// Body POST /action/execute.
public record ActionExecuteRequest(
    string ActionId, string Action, Dictionary<string, object?> Params,
    string? Provider = null, string? Model = null);
```

> Nếu `ChatData` nằm namespace khác → thêm `using` phù hợp.

- [ ] **Step 2: Build**

Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q`
Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Services/Chat/ChatModels.cs
git commit -m "feat(assistant): DTO ActionProposal/Clarify/Result/ExecuteRequest"
```

---

### Task 6: Planner nhúng ActionTools + nhận `{action, params}`

**Files:**
- Modify: `Services/Chat/JsonPlannerAgent.cs` (prompt `PLANNER_SYSTEM` + parse)
- Modify: `Services/Chat/NativeToolUseAgent.cs` (tương tự cho path Anthropic)

- [ ] **Step 1: Nhúng action catalog vào prompt planner** — trong `JsonPlannerAgent`, chỗ build planner prompt (đang nhúng `ChatTools.CatalogForPrompt()`), thêm khối:

```csharp
sb.AppendLine("== HÀNH ĐỘNG (khi user YÊU CẦU LÀM việc gì đó, không phải hỏi số liệu) ==");
sb.AppendLine(ActionTools.CatalogForPrompt());
sb.AppendLine("Quy tắc: câu hỏi SỐ LIỆU → trả {\"tool\":...}. Yêu cầu HÀNH ĐỘNG (giao việc, trả lời mail, đánh giá khách, chấm deal, kiểm tra mail) → trả {\"action\":\"<name>\",\"params\":{...}}.");
sb.AppendLine("Điền params từ câu nói + NGỮ CẢNH lượt trước (vd 'khách này' → customerName đã nhắc). KHÔNG tự bịa id.");
```

- [ ] **Step 2: Parse `action` từ JSON planner** — nơi hiện parse `{tool,params}` (qua `LooseJson`), thêm nhánh: nếu JSON có key `action` → trả một kết quả mang `Action` + `Params` thay vì `Tool`.

Định nghĩa struct trả về planner (nếu chưa có field action) — thêm vào record kết quả planner hiện có, ví dụ:

```csharp
// trong record PlannerDecision (hoặc tương đương): thêm
public string? Action { get; init; }
```

Và tại parse:

```csharp
if (root.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.String)
{
    var prms = root.TryGetProperty("params", out var p) ? p : default;
    return new PlannerDecision { Action = actEl.GetString(), ParamsRaw = prms.Clone() };
}
```

> Dùng đúng tên record/field planner hiện có. Nếu planner trả cả `tool` lẫn `action` (hiếm) → ưu tiên `action`.

- [ ] **Step 3: Lặp lại cho `NativeToolUseAgent`** — thêm ActionTools vào `SystemPromptBase`/tool list tương tự (path Anthropic có thể để phase sau nếu default là JSON; tối thiểu KHÔNG vỡ build).

- [ ] **Step 4: Build**

Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q`
Expected: `0 Error(s)`

- [ ] **Step 5: Verify tay bằng chat/stream** (dev key config sẵn) — gọi:

```bash
curl -s -X POST http://localhost:5080/api/v1/chat -H "Content-Type: application/json" \
  -H "X-Session-Id: <valid>" \
  -d '{"messages":[{"role":"user","content":"giao việc gọi lại khách A cho nhân viên Minh"}]}' | jq .
```

Expected: log/response cho thấy planner chọn `action=assign_task` (chưa cần execute — Phase 4 mới nối). Nếu planner vẫn ra `tool` → tinh chỉnh câu prompt Step 1.

- [ ] **Step 6: Commit**

```bash
git add Services/Chat/JsonPlannerAgent.cs Services/Chat/NativeToolUseAgent.cs
git commit -m "feat(assistant): planner nhận {action,params} + nhúng ActionTools catalog"
```

---

## PHASE 3 — Resolver tên → id

### Task 7: `ActionResolver` (TDD phần thuần)

**Files:**
- Create: `Services/Chat/ActionResolver.cs`
- Test: `TourkitAiProxy.Tests/ActionResolverTests.cs`

- [ ] **Step 1: Tách hàm normalize thuần (test được) + skeleton resolver**

`Services/Chat/ActionResolver.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Chat;

/// Resolve tên người/khách/deal/workflow → id qua các tool đọc /api/ai/*.
/// Trả (id, matches) — matches>1 → mơ hồ (clarify), matches=0 → không thấy.
public class ActionResolver
{
    // ... ctor nhận TourKitApiClient + ITenantContext/session (điền ở Step 3)

    /// Chuẩn hóa tên để so khớp: lowercase, bỏ dấu, đ→d, gộp khoảng trắng.
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant().Replace('đ', 'd');
        var formD = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var noMark = sb.ToString().Normalize(NormalizationForm.FormC);
        return string.Join(' ', noMark.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// True nếu `query` là tập con token của `candidate` (khớp lỏng tên).
    public static bool TokenSubsetMatch(string query, string candidate)
    {
        var q = Norm(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var c = new HashSet<string>(Norm(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return q.Length > 0 && q.All(c.Contains);
    }
}
```

- [ ] **Step 2: Test hàm thuần**

`TourkitAiProxy.Tests/ActionResolverTests.cs`:

```csharp
using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionResolverTests
{
    [Theory]
    [InlineData("Nguyễn Văn A", "nguyen van a")]
    [InlineData("  Đặng   Minh ", "dang minh")]
    public void Norm_strips_diacritics_and_spaces(string input, string expected)
        => Assert.Equal(expected, ActionResolver.Norm(input));

    [Fact]
    public void TokenSubset_matches_partial_name()
    {
        Assert.True(ActionResolver.TokenSubsetMatch("Minh", "Đặng Văn Minh"));
        Assert.True(ActionResolver.TokenSubsetMatch("Nguyễn A", "Nguyễn Văn A"));
        Assert.False(ActionResolver.TokenSubsetMatch("Hoa", "Đặng Văn Minh"));
    }
}
```

- [ ] **Step 3: Thêm resolver gọi CRM** — implement `ResolveCustomerAsync`, `ResolveStaffAsync`, `ResolveDealAsync`, `ResolveWorkflowAsync` dùng `TourKitApiClient.GetAsync(jwt, "/api/ai/customers?filter={name}"...)` (v.v.), lọc bằng `TokenSubsetMatch`, trả `record ResolveOutcome(int? Id, string? Label, List<ActionChoice> Ambiguous)`. Ký tên (không đoán khi >1).

```csharp
public record ResolveOutcome(int? Id, string? Label, List<ActionChoice>? Ambiguous = null);
```

- [ ] **Step 4: Run test + build**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ActionResolverTests`
Expected: PASS (3 tests)
Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q` → `0 Error(s)`

- [ ] **Step 5: Đăng ký DI** trong `Program.cs`: `builder.Services.AddScoped<ActionResolver>();`

- [ ] **Step 6: Commit**

```bash
git add Services/Chat/ActionResolver.cs TourkitAiProxy.Tests/ActionResolverTests.cs Program.cs
git commit -m "feat(assistant): ActionResolver tên→id (customer/staff/deal/workflow)"
```

---

## PHASE 4 — Executor + endpoint execute

### Task 8: `ActionExecutor` — định tuyến theo Kind

**Files:**
- Create: `Services/Chat/ActionExecutor.cs`
- Test: `TourkitAiProxy.Tests/ActionExecutorTests.cs` (chỉ test builder payload thuần)

- [ ] **Step 1: Skeleton executor + builder payload CRM (thuần, test được)**

`Services/Chat/ActionExecutor.cs`:

```csharp
using System.Text.Json;
using TourkitAiProxy.Services.Crm;

namespace TourkitAiProxy.Services.Chat;

/// Thực thi 1 hành động đã xác nhận. Định tuyến theo ActionKind.
public class ActionExecutor
{
    // ctor: CrmActionQueueRepository, ReviewService, DealScoringService, MailReplyService,
    //       IMailSender, MailRepository, ActionResolver, TourKitApiClient ... (điền dần)

    /// Dựng PayloadJson khớp CreateOrUpdateTaskingRequest cho assign-task.
    public static string BuildAssignTaskPayload(
        int workflowId, string name, string? content, string staffsInChargeCsv,
        int prioritized, DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        int? bookingTicketId)
        => JsonSerializer.Serialize(new
        {
            id = 0, workflowId, name, content,
            staffsInCharge = staffsInChargeCsv,
            prioritized, status = 1,
            startDate = startUtc, endDate = endUtc,
            appointmentReminder = reminderMinutes,
            bookingTicketId
        });

    /// Dựng PayloadJson khớp CreateCustomerCareRequest cho create-appointment.
    public static string BuildAppointmentPayload(
        int customerId, string careTitle, string? careDetail,
        DateTime? startUtc, DateTime? endUtc, int reminderMinutes,
        string? customerName, string? customerPhone, int? bookingTicketId)
        => JsonSerializer.Serialize(new
        {
            customerId, careTitle, careDetail,
            careStartTime = startUtc, careEndTime = endUtc,
            status = 1, appointmentReminder = reminderMinutes,
            bookingTicketId, customerName, customerPhone
        });

    /// Map "cao|tb|thap" → Prioritized (0..3).
    public static int MapPriority(string? p) => (p ?? "").Trim().ToLowerInvariant() switch
    {
        "cao" or "high" => 1,
        "tb" or "trung binh" or "trung bình" or "medium" => 2,
        "thap" or "thấp" or "low" => 3,
        _ => 0
    };
}
```

- [ ] **Step 2: Test builder + MapPriority**

`TourkitAiProxy.Tests/ActionExecutorTests.cs`:

```csharp
using System.Text.Json;
using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionExecutorTests
{
    [Theory]
    [InlineData("cao", 1)] [InlineData("TB", 2)] [InlineData("thấp", 3)] [InlineData("", 0)]
    public void MapPriority_maps_vietnamese(string input, int expected)
        => Assert.Equal(expected, ActionExecutor.MapPriority(input));

    [Fact]
    public void AssignTask_payload_has_required_fields()
    {
        var json = ActionExecutor.BuildAssignTaskPayload(
            12, "Gọi lại khách A", "ND", "15,18", 1,
            new DateTime(2026,7,15,0,0,0,DateTimeKind.Utc),
            new DateTime(2026,7,16,0,0,0,DateTimeKind.Utc), 30, 456);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        Assert.Equal(12, r.GetProperty("workflowId").GetInt32());
        Assert.Equal("15,18", r.GetProperty("staffsInCharge").GetString());
        Assert.Equal(1, r.GetProperty("status").GetInt32());
        Assert.Equal(456, r.GetProperty("bookingTicketId").GetInt32());
    }

    [Fact]
    public void Appointment_payload_has_care_times()
    {
        var json = ActionExecutor.BuildAppointmentPayload(
            123, "Hẹn tư vấn", "chi tiết",
            new DateTime(2026,7,16,2,0,0,DateTimeKind.Utc),
            new DateTime(2026,7,16,3,0,0,DateTimeKind.Utc), 30, "A", "09", null);
        using var d = JsonDocument.Parse(json);
        Assert.Equal(123, d.RootElement.GetProperty("customerId").GetInt32());
        Assert.True(d.RootElement.TryGetProperty("careStartTime", out _));
    }
}
```

- [ ] **Step 3: Implement `ExecuteAsync(ActionExecuteRequest, tenantId, jwt, username, ct)`** trả `ActionResult`:
  - `Kind==CrmQueue`: re-resolve id (staff/customer/workflow) → build payload → `CrmActionQueueRepository.EnqueueAsync` → `ActionResult("assign_task", "✅ Đã đưa vào hàng đợi — hệ thống sẽ tạo trong CRM.", null)`.
  - `Kind==Internal review_customer`: nạp Customer (qua `CustomerReviewClient` như `CustomerAutoReviewWorkflow`) → `ReviewService.ReviewAsync(...)` → `ActionResult` kèm `ChatData` thẻ review.
  - `Kind==Internal score_deal`: resolve dealId → fetch chi tiết → dựng profile → `DealScoringService.ScoreAsync(profile,...)` → `ActionResult` + data.
  - `Kind==Mail send_mail_reply`: resolve mailId → `MailReplyService` draft (nếu chưa có) → `IMailSender.SendAsync` → flip status → `ActionResult("send_mail_reply","✅ Đã gửi.")`.
  - Bọc MỌI AI call bằng `AiCallContext.Push("assistant-action", tenantId, sessionId)`.
  - Idempotent: nếu đã xử lý `ActionId` (lưu tạm in-mem `ConcurrentDictionary<string,ActionResult>` TTL ngắn) → trả kết quả cũ.

- [ ] **Step 4: Run test + build**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ActionExecutorTests`
Expected: PASS (4 tests)
Run: `dotnet build ...` → `0 Error(s)`

- [ ] **Step 5: DI** `Program.cs`: `builder.Services.AddScoped<ActionExecutor>();`

- [ ] **Step 6: Commit**

```bash
git add Services/Chat/ActionExecutor.cs TourkitAiProxy.Tests/ActionExecutorTests.cs Program.cs
git commit -m "feat(assistant): ActionExecutor định tuyến Mail/Internal/CrmQueue + payload CRM"
```

---

### Task 9: Endpoint `POST /api/v1/assistant/action/execute`

**Files:**
- Create: `Endpoints/AssistantActionEndpoints.cs`
- Modify: `Program.cs` (`app.MapAssistantActionEndpoints();`)

- [ ] **Step 1: Viết endpoint** (require `X-Session-Id`, resolve tenant + jwt qua session store — copy pattern `MailEndpoints`):

```csharp
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Endpoints;

public static class AssistantActionEndpoints
{
    public static void MapAssistantActionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/assistant");

        // Thực thi hành động đã xác nhận. SSE nếu cần stream (mail draft); ở đây buffered.
        g.MapPost("/action/execute", async (
            HttpContext ctx, ActionExecuteRequest req,
            ITenantContext tenantCtx, TkSessionStore sessions, ActionExecutor exec) =>
        {
            var sessionId = ctx.Request.Headers["X-Session-Id"].ToString();
            var sess = await sessions.GetAsync(sessionId);            // pattern hiện có
            if (sess is null) return Results.Unauthorized();

            var result = await exec.ExecuteAsync(
                req, sess.TenantId, sess.Jwt, sess.Username, ctx.RequestAborted);

            var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            return Results.Json(result, json);   // camelCase (khớp client)
        });
    }
}
```

> Dùng đúng API `TkSessionStore` (tên method Get/GetAsync + field Jwt/TenantId/Username) như `ChatEndpoints`/`MailEndpoints`.

- [ ] **Step 2: Wire `Program.cs`**: `app.MapAssistantActionEndpoints();` (cạnh `MapChatEndpoints`).

- [ ] **Step 3: Build + smoke**

Run: `dotnet build ...` → `0 Error(s)`
Chạy app, POST thử `assign_task` (params đã có id) → kỳ vọng row mới trong `dbo.CrmActionQueue` + response `{"action":"assign_task","message":"✅ Đã đưa vào hàng đợi…"}`.
Kiểm: `GET /api/v1/workflows/crm-queue` thấy item Pending.

- [ ] **Step 4: Commit**

```bash
git add Endpoints/AssistantActionEndpoints.cs Program.cs
git commit -m "feat(assistant): POST /assistant/action/execute (định tuyến + tenant scope)"
```

---

### Task 10: Nối nhánh action vào `ChatAgentService` (proposal/clarify/run-through)

**Files:**
- Modify: `Services/Chat/ChatAgentService.cs` (`AskStreamAsync` + `AskAsync`)

- [ ] **Step 1: Sau khi planner trả decision** — trước dispatch tool đọc, thêm nhánh:

```csharp
if (!string.IsNullOrEmpty(decision.Action))
{
    var tool = ActionTools.Find(decision.Action);
    if (tool is not null)
    {
        // 1) resolve tên→id qua ActionResolver; nếu mơ hồ → emit action-clarify + return
        // 2) nếu tool.NeedsConfirm (hoặc batch review nhiều) → emit action-proposal (KHÔNG execute) + return
        // 3) nếu run-through (review/deal đơn, check_mail) → gọi ActionExecutor.ExecuteAsync + emit action-result
        return; // kết thúc lượt, KHÔNG rơi xuống dispatch tool đọc
    }
}
```

Với SSE (`AskStreamAsync`) emit event camelCase:
```
data: {"kind":"action-proposal", "proposal":{...}}
data: {"kind":"action-clarify", "clarify":{...}}
data: {"kind":"action-result", "result":{...}, "data":{...}}
```

- [ ] **Step 2: `AskAsync` (buffered)** trả cùng cấu trúc trong reply object (cho client không stream).

- [ ] **Step 3: Build + verify tay 2 kịch bản**

Run: `dotnet build ...` → `0 Error(s)`

KB-A (run-through): `POST /chat/stream` "đánh giá khách <tên có thật>" → nhận `action-result` kèm data thẻ review; DB `dbo.Reviews` có row.
KB-E (confirm): "giao việc gọi lại khách A cho Minh, hạn ngày mai" → nhận `action-proposal` (CHƯA có row queue). Gọi `/action/execute` với proposal → row Pending xuất hiện.

- [ ] **Step 4: Commit**

```bash
git add Services/Chat/ChatAgentService.cs
git commit -m "feat(assistant): nhánh action trong ChatAgentService (proposal/clarify/run-through)"
```

---

## PHASE 5 — Frontend (thẻ xác nhận + kết quả, 2 trang)

### Task 11: Component `ActionConfirmCard` + clarify + result

**Files:**
- Create: `wwwroot/components/action-confirm-card.jsx`
- Modify: `wwwroot/index.html` (script babel), `wwwroot/bundle-entry.js` (import)

- [ ] **Step 1: Viết component** (`window.ActionConfirmCard`, `window.ActionClarifyList`) — thẻ tóm tắt + field sửa (`ActionField.type`) + nút "Xác nhận"/"Hủy"; clarify = list nút chọn. Gọi `onConfirm(editedParams)` / `onCancel()` / `onChoose(id)`.

```jsx
function ActionConfirmCard({ proposal, onConfirm, onCancel }) {
  const [vals, setVals] = React.useState(() =>
    Object.fromEntries(proposal.fields.map(f => [f.key, f.value ?? ""])));
  return (
    <div className="jv-action-card">
      <div className="jv-action-title">🔔 {proposal.title}</div>
      <div className="jv-action-summary">{proposal.summary}</div>
      {proposal.fields.map(f => (
        <label key={f.key} className="jv-action-field">
          <span>{f.label}</span>
          {f.type === "textarea"
            ? <textarea value={vals[f.key]} onChange={e => setVals({ ...vals, [f.key]: e.target.value })} />
            : <input type={f.type === "datetime" ? "datetime-local" : "text"}
                     value={vals[f.key]} onChange={e => setVals({ ...vals, [f.key]: e.target.value })} />}
        </label>
      ))}
      {proposal.estimate && <div className="jv-action-estimate">{proposal.estimate}</div>}
      <div className="jv-action-actions">
        <button className="jv-btn-cancel" onClick={onCancel}>Hủy</button>
        <button className="jv-btn-confirm" onClick={() => onConfirm(vals)}>Xác nhận</button>
      </div>
    </div>
  );
}
window.ActionConfirmCard = ActionConfirmCard;

function ActionClarifyList({ clarify, onChoose }) {
  return (
    <div className="jv-action-card">
      <div className="jv-action-summary">{clarify.question}</div>
      {clarify.choices.map(c => (
        <button key={c.id} className="jv-clarify-choice" onClick={() => onChoose(c.id)}>
          {c.label}{c.hint ? <small> · {c.hint}</small> : null}
        </button>
      ))}
    </div>
  );
}
window.ActionClarifyList = ActionClarifyList;
```

- [ ] **Step 2: Đăng ký load** — `index.html` thêm `<script type="text/babel" src="components/action-confirm-card.jsx"></script>` (trước pages); `bundle-entry.js` thêm `import "./components/action-confirm-card.jsx";` (BẮT BUỘC — thiếu là prod trắng trang).

- [ ] **Step 3: CSS** — thêm `.jv-action-*` vào styles của trang (styles.css hoặc inline theo trang). Không cần đẹp hoàn hảo, đủ rõ.

- [ ] **Step 4: Verify** — mở `/assistant` dev mode, `window.ActionConfirmCard` tồn tại (console không lỗi). Prod: `.\build-frontend.ps1` build không lỗi.

- [ ] **Step 5: Commit**

```bash
git add wwwroot/components/action-confirm-card.jsx wwwroot/index.html wwwroot/bundle-entry.js wwwroot/styles.css
git commit -m "feat(ui): ActionConfirmCard + ActionClarifyList (dùng chung assistant/travai)"
```

---

### Task 12: Nối SSE event action vào `assistant.jsx` + `jarvis.jsx`

**Files:**
- Modify: `wwwroot/pages/assistant.jsx`, `wwwroot/pages/jarvis.jsx`

- [ ] **Step 1: Trong reader SSE** (`window.tourkitUtil.readSSE`) — thêm nhánh theo `msg.kind`:
  - `action-proposal` → lưu `pendingProposal` state → render `<window.ActionConfirmCard proposal={..} onConfirm={confirmAction} onCancel={..}/>`.
  - `action-clarify` → render `<window.ActionClarifyList .../>`; chọn → gửi lại câu với id đã chọn.
  - `action-result` → append message trợ lý + nếu `result.data` → render panel/thẻ review (dùng renderer data sẵn có + `customer-review-card.jsx`).

- [ ] **Step 2: `confirmAction(editedVals)`** — `POST /api/v1/assistant/action/execute` với `{actionId, action, params: editedVals}` (kèm `X-Session-Id`) → nhận `action-result` → render.

- [ ] **Step 3: /travai (D1 — voice)** — review/deal đọc kết quả bằng TTS như bình thường; với `action-proposal` (gửi mail/giao việc): JARVIS đọc "Tôi đã soạn xong, hãy kiểm tra và bấm Xác nhận" + hiện thẻ; KHÔNG auto-execute, KHÔNG bắt từ khóa giọng để confirm.

- [ ] **Step 4: Verify tay E2E** (mic + click) — chạy 5 kịch bản KB-A…KB-E ở spec. Ghi lại kết quả.

- [ ] **Step 5: Build prod bundle** — `.\build-frontend.ps1`; kiểm không lỗi, cả 2 trang không trắng.

- [ ] **Step 6: Commit**

```bash
git add wwwroot/pages/assistant.jsx wwwroot/pages/jarvis.jsx
git commit -m "feat(ui): xử lý action-proposal/clarify/result trên assistant + travai"
```

---

### Task 13: Card "Hàng đợi CRM" trong `/workflows`

**Files:**
- Modify: `wwwroot/pages/workflows.jsx`

- [ ] **Step 1: Thêm card** gọi `GET /api/v1/workflows/crm-queue?limit=50` (kèm `X-Session-Id`) → bảng: Kind · tóm tắt payload (name/careTitle) · Status (Pending⏳/Done✅/Failed❌) · CreatedUtc (`window.tourkitUtil.fmtAgo`). Filter status đơn giản.

- [ ] **Step 2: Verify** — sau khi execute 1 `assign_task`, mở `/workflows` thấy item Pending.

- [ ] **Step 3: Build + commit**

```bash
.\build-frontend.ps1
git add wwwroot/pages/workflows.jsx
git commit -m "feat(ui): card Hàng đợi CRM trong /workflows"
```

---

## PHASE 6 — Docs + tổng kiểm

### Task 14: Contract doc + cập nhật CLAUDE.md + schema doc

**Files:**
- Create: `docs/crm-action-contract/README.md`
- Modify: `docs/database-schema.md`, `CLAUDE.md`

- [ ] **Step 1: Viết contract** `docs/crm-action-contract/README.md` — mô tả `dbo.CrmActionQueue`, 2 Kind, PayloadJson shape (copy từ spec mục 4), endpoint đích (`POST /api/tasks`, `POST /api/customer-care`), luồng worker cập nhật Status. Đây là bàn giao cho worker app-side.

- [ ] **Step 2: `docs/database-schema.md`** — thêm `dbo.CrmActionQueue` vào inventory (giống các bảng khác).

- [ ] **Step 3: `CLAUDE.md`** — thêm mục ngắn về "Trợ lý hành động" (action tools + confirm-first + CrmActionQueue) trong section Chat-Analytics; bổ sung endpoint mới vào bảng API surface.

- [ ] **Step 4: Commit**

```bash
git add docs/crm-action-contract/README.md docs/database-schema.md CLAUDE.md
git commit -m "docs(assistant): contract CRM queue + cập nhật schema + CLAUDE.md"
```

---

### Task 15: Tổng kiểm + regression

- [ ] **Step 1: Full build + test**

Run: `dotnet build TourkitAiProxy.csproj -c Debug --nologo -v q` → `0 Error(s)`
Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → all PASS

- [ ] **Step 2: Regression đọc số liệu** — hỏi 1 câu số liệu thuần ("doanh thu tháng này") trên `/assistant` → vẫn ra bảng+chart như cũ (nhánh action KHÔNG chặn luồng đọc).

- [ ] **Step 3: Prod bundle** — `.\build-frontend.ps1` → cả 4 trang (`/assistant`, `/travai`, `/workflows`, `/mail`) load không trắng.

- [ ] **Step 4: GitNexus re-index** (index đang stale): `node .gitnexus/run.cjs analyze` (hoặc `gitnexus analyze`).

- [ ] **Step 5: Commit cuối (nếu có chỉnh)** + push khi user đồng ý.

```bash
git commit -am "chore(assistant): tổng kiểm action tools v1"
```

---

## Ghi chú thực thi

- **DateTime UTC + `Z`** mọi chỗ (dueDate/careTime parse `AssumeUniversal|AdjustToUniversal`).
- **SSE camelCase** bắt buộc (`JsonSerializerDefaults.Web`) — sai là client vỡ.
- **Tenant scope** mọi endpoint action (X-Session-Id → tenant); cross-tenant → 401/null.
- **Quota + log**: bọc AI call bằng `AiCallContext.Push("assistant-action", tenantId, sessionId)`.
- **Không phá luồng đọc**: nhánh action chỉ chạy khi planner trả `action`; còn lại rơi về dispatch tool đọc như cũ.
- **bundle-entry.js ↔ index.html** phải khớp danh sách file (thêm component mới ở CẢ HAI).
