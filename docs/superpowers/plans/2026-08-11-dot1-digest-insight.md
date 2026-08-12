# Đợt 1 — Insight Feed + Digest Engine + Subscriptions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Phát hành bản tin chủ động `sale-brief` (rule thuần) + `ceo-brief` (1 AI call/tenant/ngày) + cảnh báo `payment-watchdog` (rule thuần), gửi đa kênh (in-app / email / Telegram / Zalo OA) theo đăng ký per-user có gate quyền.

**Architecture:** 3 workflow `IScheduledWorkflow` PerTenant cắm vào `WorkflowSchedulerService` sẵn có (KHÔNG sửa scheduler — brief chạy interval 60', tự chọn subscription "đến giờ"); nội dung build bằng pure builders (test được); phát qua `DigestDispatcher` → 4 `IDigestChannel`. Insight feed là bảng + endpoint + trang mới, 100% additive.

> **[SỬA 12/08/2026] Bản tin fetch bằng phiên CỦA NGƯỜI NHẬN, KHÔNG dùng tài khoản tự động.**
> Quy tắc: *luồng theo người dùng thì chạy bằng tài khoản người dùng; luồng theo tổ chức mới dùng
> tài khoản hệ thống* (4 workflow cũ đã đúng: `mail-auto-sync` PerUser dùng hộp thư của chính người
> đó; deal/customer/tour-price PerTenant dùng tài khoản tự động).
> Bản đầu cho bản tin fetch bằng tài khoản tự động (`CH_XEM_ALL` — thấy deal mọi người) rồi tự lọc →
> **lọc sai là rò rỉ chéo giữa các sale, CRM không chặn giúp**. Nay mỗi người fetch bằng token của
> chính mình, CRM tự áp quyền.
> Kéo theo: **digest KHÔNG cần `TenantServiceAccounts`**; `ceo-brief` không phải re-check
> `CH_XEM_ALL` mỗi lần gửi (giữ gate lúc đăng ký); điều kiện mới là người nhận đã từng đăng nhập
> (`TkSessions` tự re-login, giữ 30 ngày) — chưa có phiên thì bỏ qua + ghi lý do.
> Scheduler vẫn 1 bản ghi PerTenant (bật 1 lần), workflow tự đổi phiên theo từng người — tránh bắt
> mỗi user cấu hình ở hai nơi. Chi tiết + đánh đổi: xem spec §4.3.

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper + SQL Server (`TourkitAiDb`), xUnit (`TourkitAiProxy.Tests`), frontend React no-build (`wwwroot/pages/*.jsx`).

**Spec:** [docs/superpowers/specs/2026-08-11-dot1-digest-insight-design.md](../specs/2026-08-11-dot1-digest-insight-design.md)

## Global Constraints

- Comment/log/string user-facing = **tiếng Việt**. DateTime lưu DB = **UTC** (`SYSUTCDATETIME()` / `DateTime.UtcNow`); giờ VN qua `TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")`.
- Schema mới vào `Services/Db/TourkitAiDb.cs` `SchemaSql` — **idempotent** (`IF OBJECT_ID(...) IS NULL`, cột mới qua `IF NOT EXISTS sys.columns`). Sau đó cập nhật `docs/database-schema.md`.
- AI call nền PHẢI bọc `AiCallContext.Push(AiFeatures.Digest, tenantId)` (STRICT — bypass = sai quota + log `unknown`).
- DI workflow đăng ký trong `Services/Bootstrap/WorkflowStackRegistration.cs` (KHÔNG phải `Program.cs`) → web + worker cùng pickup. Endpoint chỉ map ở web `Program.cs`.
- Frontend thêm trang = sửa đủ 3 chỗ: `index.html` + `bundle-entry.js` + `app.jsx` (thiếu `bundle-entry.js` → prod trắng trang React #130).
- Secrets: Zalo token Crypton-enc trong DB; KHÔNG log token.
- **NGUYÊN TẮC CHỌN NƠI CẤU HÌNH (chốt 12/08/2026): chia theo CHI PHÍ, không chia đều.**
  - **Tốn tiền / có hạn mức / có rủi ro bị nhà cung cấp khoá → TENANT TỰ CẤU HÌNH.** Đẩy chi phí về
    đúng công ty hưởng lợi. Dự án đã theo lối này ở `MailAccountStore` và `TenantServiceAccountStore`.
  - **Rẻ hoặc miễn phí → DỊCH VỤ MÌNH LO, người dùng KHÔNG phải khai gì.** Bắt khai thêm một bước
    chỉ để tiết kiệm thứ gần như không tốn là đánh đổi sai: mất người dùng nhiều hơn được.

  Áp vào 4 kênh của Đợt 1:

  | Kênh | Chi phí thật | Cấu hình ở đâu |
  |---|---|---|
  | Trong app | 0 | Không phải khai gì |
  | **Telegram** | **0 — bot miễn phí, không hạn mức** | **Server-level, 1 bot dùng chung** (`Telegram:BotToken`) |
  | **Email** | Rất rẻ, và **hộp thư tenant đã cấu hình sẵn cho SmartMail** | Dùng lại hộp thư đó — **không khai thêm** |
  | **Zalo OA / ZNS** | **Tốn tiền thật**: gói 1–6 triệu/năm, hạn mức tính **theo từng OA** ([bảng giá](https://zalo.solutions/oa/pricing): gói mua chỉ dùng cho 1 OA, không chuyển nhượng) | **Per-tenant**, `dbo.TenantChannelSettings`, Crypton-enc |

  **Ghi nhận sai sót:** bản sửa đầu trong ngày đã đẩy CẢ Telegram sang per-tenant — áp nguyên tắc
  quá tay. Telegram miễn phí nên bắt mỗi công ty tự tạo bot chỉ thêm ma sát. Riêng với Telegram,
  bot dùng chung còn **đơn giản hơn về kỹ thuật**: endpoint `POST /digest/telegram/detect` dò
  `chatId` bằng `getUpdates` chỉ chạy được khi biết trước MỘT token — nhiều bot thì phải dò từng cái.
  Rủi ro bot chung bị khoá do một tenant spam là có, nhưng bản tin gửi vài người/ngày, còn xa giới
  hạn Telegram (~30 tin/giây).

  Hai tầng cấu hình còn lại giữ nguyên:
  - **Tầng công ty (quản trị khai 1 lần)** — CHỈ OA Zalo/ZNS.
  - **Tầng cá nhân (trong `DigestSubscriptions`)** — nơi NHẬN: email, Zalo user id, Telegram chat id;
    bật/tắt từng kênh độc lập.

  Ghi chú kỹ thuật: `ZaloUserId` **không nhập tay được** — phải để người dùng follow OA rồi bắt
  `user_id` từ sự kiện `follow` qua webhook; token OA hết hạn nên cần vòng refresh.
- Test: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` — pure logic only (không test DB thật).
- Interface sẵn có (VERBATIM — không đổi):
  - `IScheduledWorkflow { string Type; string Label; string Description; WorkflowScope Scope; Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct); }` · `WorkflowScope.PerTenant` · `WorkflowRunResult(bool Ok, string? Summary, string? Error)`
  - `TenantServiceAccountStore.Get(tenantId)` → `ServiceAccount(TenantId, Username, Password, Enabled)?`
  - `TkSessionStore.GetOrCreateServiceSessionAsync(tenantId, username, password, ct)` → `string sessionId`; `GetValidJwtAsync(sessionId, ct)` → `string jwt`; `HasPermission(sessionId, code)`; `EnsurePermissionsAsync(sessionId, ct)`
  - `TourKitApiClient.GetAsync(string jwt, string pathAndQuery, CancellationToken ct)` → `JsonElement` (đã unwrap `data`)
  - `MailQueueRepository.EnqueueAsync(OutboundMailInput m, ct)` → `long`; `OutboundMailInput(TenantId, Kind, SourceId?, Username?, TemplateCode?, ToEmail?, ToName?, ToUserId?, Cc?, Subject?, Params?, Data?, ScheduledUtc?)`
  - `MailRepository.Counts(tenantId)` → `MailCounts(Total, Unread, ByStatus, ByCategory)`
  - `TkPermissionCodes` (const strings) · `AiFeatures` (const strings) · `Crypton.Encrypt/Decrypt`

---

### Task 1: Schema + models + JWT userId helper

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs` (thêm block vào cuối `SchemaSql`, trước dấu `";` đóng const)
- Create: `Services/Digest/DigestModels.cs`
- Create: `Services/TourKit/JwtClaims.cs`
- Test: `TourkitAiProxy.Tests/Digest/JwtClaimsTests.cs`, `TourkitAiProxy.Tests/Digest/DigestModelTests.cs`

**Interfaces (Produces):**
- `TourkitAiProxy.Services.Digest`: `record DigestSubscription(string TenantId, string Username, string BriefType, bool Enabled, int SendHourLocal, bool ChannelInApp, bool ChannelEmail, string? Email, bool ChannelTelegram, string? TelegramChatId, bool ChannelZalo, string? ZaloUserId, DateTime? LastSentUtc, DateTime? LastSentLocalDate)` + `static class BriefTypes { public const string Sale = "sale-brief"; public const string Ceo = "ceo-brief"; }` + `DigestSubscription.ClampHour(int h)` → int (0–23, ngoài khoảng → 7)
- `record DigestMessage(string Title, string BodyMarkdown, string BodyHtml, string Kind, int Severity = 0)`
- `record AgentInsight(long Id, string TenantId, string Username, string Kind, int Severity, string Title, string Body, string? DataJson, string? AlertKey, bool IsRead, DateTime CreatedUtc)`
- `JwtClaims.TryGetUserId(string jwt)` → `int?` (decode payload base64url, đọc claim `user_id` dạng số HOẶC chuỗi số)

- [ ] **Step 1: Viết test fail**

```csharp
// TourkitAiProxy.Tests/Digest/JwtClaimsTests.cs
using TourkitAiProxy.Services.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class JwtClaimsTests
{
    private static string MakeJwt(string payloadJson)
    {
        static string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64Url("{\"alg\":\"HS256\"}")}.{B64Url(payloadJson)}.sig";
    }

    [Fact] public void Doc_user_id_dang_so()
        => Assert.Equal(123, JwtClaims.TryGetUserId(MakeJwt("{\"user_id\":123,\"tenant_id\":\"t\"}")));
    [Fact] public void Doc_user_id_dang_chuoi_so()
        => Assert.Equal(45, JwtClaims.TryGetUserId(MakeJwt("{\"user_id\":\"45\"}")));
    [Fact] public void Thieu_claim_tra_null()
        => Assert.Null(JwtClaims.TryGetUserId(MakeJwt("{\"tenant_id\":\"t\"}")));
    [Theory]
    [InlineData("")] [InlineData("khong.phai.jwt-hop-le")] [InlineData("1phan")]
    public void Jwt_rac_tra_null(string jwt) => Assert.Null(JwtClaims.TryGetUserId(jwt));
}
```

```csharp
// TourkitAiProxy.Tests/Digest/DigestModelTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestModelTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(23, 23)] [InlineData(7, 7)]
    [InlineData(-1, 7)] [InlineData(24, 7)] [InlineData(99, 7)]
    public void ClampHour_gioi_han_0_23_ngoai_khoang_ve_7(int input, int expected)
        => Assert.Equal(expected, DigestSubscription.ClampHour(input));
}
```

- [ ] **Step 2: Chạy để thấy fail** — `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JwtClaims|FullyQualifiedName~DigestModel"` → FAIL (compile: chưa có type).

- [ ] **Step 3: Implement models + JwtClaims**

```csharp
// Services/Digest/DigestModels.cs
namespace TourkitAiProxy.Services.Digest;

/// Loại bản tin (khớp WorkflowType của workflow tương ứng).
public static class BriefTypes
{
    public const string Sale = "sale-brief";
    public const string Ceo  = "ceo-brief";
    public static bool IsValid(string? t) => t == Sale || t == Ceo;
}

/// Đăng ký nhận bản tin per-(tenant, user, loại). LastSentLocalDate (ngày VN) chống gửi trùng trong ngày.
public record DigestSubscription(
    string TenantId, string Username, string BriefType,
    bool Enabled, int SendHourLocal,
    bool ChannelInApp, bool ChannelEmail, string? Email,
    bool ChannelTelegram, string? TelegramChatId,
    bool ChannelZalo, string? ZaloUserId,
    DateTime? LastSentUtc, DateTime? LastSentLocalDate)
{
    /// Giờ gửi hợp lệ 0–23; giá trị rác → 7h sáng (default an toàn).
    public static int ClampHour(int h) => h is >= 0 and <= 23 ? h : 7;
}

/// Thông điệp bản tin đã render — mọi kênh dùng chung 1 nguồn.
public record DigestMessage(string Title, string BodyMarkdown, string BodyHtml, string Kind, int Severity = 0);

/// 1 dòng Insight Feed (dbo.AgentInsights). Username='' = tenant-wide.
public record AgentInsight(
    long Id, string TenantId, string Username, string Kind, int Severity,
    string Title, string Body, string? DataJson, string? AlertKey,
    bool IsRead, DateTime CreatedUtc);
```

```csharp
// Services/TourKit/JwtClaims.cs
using System.Text.Json;

namespace TourkitAiProxy.Services.TourKit;

/// Đọc claim từ JWT TourKit KHÔNG verify chữ ký (chỉ dùng nội bộ sau khi login thành công).
public static class JwtClaims
{
    /// Lấy claim user_id (số hoặc chuỗi số). Trả null nếu JWT rác/thiếu claim.
    public static int? TryGetUserId(string jwt)
    {
        try
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("user_id", out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null
            };
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Thêm schema vào `TourkitAiDb.SchemaSql`** (append block mới NGAY TRƯỚC chuỗi đóng const — giữ nguyên các block cũ):

```sql
--
IF OBJECT_ID('dbo.AgentInsights', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AgentInsights (
        Id          BIGINT IDENTITY PRIMARY KEY,
        TenantId    NVARCHAR(128)  NOT NULL,
        Username    NVARCHAR(256)  NOT NULL CONSTRAINT DF_AgentInsights_User DEFAULT '',
        Kind        NVARCHAR(64)   NOT NULL,
        Severity    TINYINT        NOT NULL CONSTRAINT DF_AgentInsights_Sev DEFAULT 0,
        Title       NVARCHAR(512)  NULL,
        Body        NVARCHAR(MAX)  NULL,
        DataJson    NVARCHAR(MAX)  NULL,
        AlertKey    NVARCHAR(128)  NULL,
        IsRead      BIT            NOT NULL CONSTRAINT DF_AgentInsights_Read DEFAULT 0,
        CreatedUtc  DATETIME2      NOT NULL
    );
    CREATE INDEX IX_AgentInsights_Tenant_User_Created ON dbo.AgentInsights(TenantId, Username, CreatedUtc DESC);
    CREATE INDEX IX_AgentInsights_AlertKey ON dbo.AgentInsights(TenantId, AlertKey) WHERE AlertKey IS NOT NULL;
END;
--
IF OBJECT_ID('dbo.DigestSubscriptions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DigestSubscriptions (
        TenantId          NVARCHAR(128) NOT NULL,
        Username          NVARCHAR(256) NOT NULL,
        BriefType         NVARCHAR(32)  NOT NULL,
        Enabled           BIT           NOT NULL CONSTRAINT DF_DigestSubs_Enabled DEFAULT 1,
        SendHourLocal     TINYINT       NOT NULL CONSTRAINT DF_DigestSubs_Hour DEFAULT 7,
        ChannelInApp      BIT           NOT NULL CONSTRAINT DF_DigestSubs_InApp DEFAULT 1,
        ChannelEmail      BIT           NOT NULL CONSTRAINT DF_DigestSubs_Email DEFAULT 0,
        Email             NVARCHAR(256) NULL,
        ChannelTelegram   BIT           NOT NULL CONSTRAINT DF_DigestSubs_Tele DEFAULT 0,
        TelegramChatId    NVARCHAR(64)  NULL,
        ChannelZalo       BIT           NOT NULL CONSTRAINT DF_DigestSubs_Zalo DEFAULT 0,
        ZaloUserId        NVARCHAR(64)  NULL,
        LastSentUtc       DATETIME2     NULL,
        LastSentLocalDate DATE          NULL,
        CreatedUtc        DATETIME2     NOT NULL,
        UpdatedUtc        DATETIME2     NOT NULL,
        CONSTRAINT PK_DigestSubscriptions PRIMARY KEY (TenantId, Username, BriefType)
    );
END;
--
IF OBJECT_ID('dbo.TenantChannelSettings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantChannelSettings (
        TenantId   NVARCHAR(128) NOT NULL,
        Channel    NVARCHAR(32)  NOT NULL,
        ConfigJson NVARCHAR(MAX) NOT NULL,
        UpdatedUtc DATETIME2     NOT NULL,
        CONSTRAINT PK_TenantChannelSettings PRIMARY KEY (TenantId, Channel)
    );
END;
--
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TkSessions') AND name = 'CrmUserId')
    ALTER TABLE dbo.TkSessions ADD CrmUserId INT NULL;
```

Đồng thời cập nhật câu `_log.LogInformation("TourkitAiDb schema OK (…)")` thêm `/AgentInsights/DigestSubscriptions/TenantChannelSettings`.

- [ ] **Step 5: Chạy test pass** — `dotnet test ... --filter "FullyQualifiedName~JwtClaims|FullyQualifiedName~DigestModel"` → PASS. `dotnet build TourkitAiProxy.csproj` → 0 error.

- [ ] **Step 6: Commit** — `git add Services/Db/TourkitAiDb.cs Services/Digest/DigestModels.cs Services/TourKit/JwtClaims.cs TourkitAiProxy.Tests/Digest/ && git commit -m "feat(digest): schema AgentInsights/DigestSubscriptions/TenantChannelSettings + models + JwtClaims"`

---

### Task 2: TkSession.CrmUserId (decode lúc login/relogin)

**Files:**
- Modify: `Services/TourKit/TkSession.cs` (model — tìm class `TkSession`, thêm property `public int? CrmUserId { get; set; }`)
- Modify: `Services/TourKit/TkSessionStore.cs` — trong `CreateAsync` (cả nhánh reuse lẫn tạo mới, sau khi có `login.Token`) và `ReloginAsync`: `s.CrmUserId = JwtClaims.TryGetUserId(login.Token) ?? s.CrmUserId;`
- Modify: `Services/TourKit/TkSessionRepository.cs` — thêm cột `CrmUserId` vào SELECT/INSERT/UPDATE (MERGE) hiện có (cùng pattern các cột khác)

**Interfaces:**
- Consumes: `JwtClaims.TryGetUserId` (Task 1)
- Produces: `TkSession.CrmUserId` (`int?`) — Task 8 dùng để filter per-recipient

- [ ] **Step 1:** Sửa 3 file như trên (đọc file trước khi sửa — cấu trúc MERGE trong repo phải thêm cột ở CẢ 3 vị trí: SELECT list, UPDATE SET, INSERT columns/VALUES).
- [ ] **Step 2:** `dotnet build TourkitAiProxy.csproj` → 0 error; `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → toàn bộ pass (không vỡ test cũ).
- [ ] **Step 3: Commit** — `git commit -am "feat(digest): TkSession.CrmUserId decode từ JWT lúc login/relogin"`

---

### Task 3: InsightRepository + DigestSubscriptionRepository + DigestDue (pure)

**Files:**
- Create: `Services/Digest/InsightRepository.cs`
- Create: `Services/Digest/DigestSubscriptionRepository.cs`
- Create: `Services/Digest/DigestDue.cs`
- Test: `TourkitAiProxy.Tests/Digest/DigestDueTests.cs`

**Interfaces (Produces):**
- `InsightRepository`: `Task<long?> InsertAsync(AgentInsight i, ct)` (AlertKey đã tồn tại trong 24h → trả null, KHÔNG insert), `Task<List<AgentInsight>> ListAsync(string tenant, string username, string? kind, bool unreadOnly, int offset, int limit, ct)` (lấy row của user + row Username=''), `Task<int> UnreadCountAsync(tenant, username, ct)`, `Task MarkReadAsync(tenant, username, long id, ct)`, `Task MarkAllReadAsync(tenant, username, ct)`, `Task<int> PruneAsync(int keepDays, ct)`
- `DigestSubscriptionRepository`: `Task<List<DigestSubscription>> ListForUserAsync(tenant, username, ct)`, `Task<List<DigestSubscription>> ListEnabledAsync(tenant, briefType, ct)`, `Task UpsertAsync(DigestSubscription sub, ct)`, `Task MarkSentAsync(tenant, username, briefType, DateTime utcNow, DateTime localDate, ct)`
- `DigestDue`: `static DateTime NowVn(DateTime utcNow)`, `static bool IsDue(DigestSubscription sub, DateTime utcNow)`

- [ ] **Step 1: Test fail — DigestDue (múi giờ VN + chống trùng ngày)**

```csharp
// TourkitAiProxy.Tests/Digest/DigestDueTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestDueTests
{
    private static DigestSubscription Sub(int hour, DateTime? lastLocalDate = null, bool enabled = true)
        => new("t", "u", BriefTypes.Sale, enabled, hour, true, false, null, false, null, false, null, null, lastLocalDate);

    [Fact] public void Dung_gio_va_chua_gui_hom_nay_thi_due()
    {
        // 00:05 UTC = 07:05 VN
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(7), utc));
    }
    [Fact] public void Sai_gio_thi_khong_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);   // 07h VN
        Assert.False(DigestDue.IsDue(Sub(8), utc));
    }
    [Fact] public void Da_gui_hom_nay_thi_khong_due()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(7, lastLocalDate: new DateTime(2026, 8, 11)), utc));
    }
    [Fact] public void Gui_hom_qua_thi_hom_nay_due_lai()
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(7, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }
    [Fact] public void Disabled_khong_due() 
    {
        var utc = new DateTime(2026, 8, 11, 0, 5, 0, DateTimeKind.Utc);
        Assert.False(DigestDue.IsDue(Sub(7, enabled: false), utc));
    }
    [Fact] public void Nua_dem_VN_doi_ngay_dung()
    {
        // 17:30 UTC ngày 10 = 00:30 VN ngày 11 → sub 0h chưa gửi ngày 11 → due
        var utc = new DateTime(2026, 8, 10, 17, 30, 0, DateTimeKind.Utc);
        Assert.True(DigestDue.IsDue(Sub(0, lastLocalDate: new DateTime(2026, 8, 10)), utc));
    }
}
```

- [ ] **Step 2: Chạy fail** — filter `DigestDue` → FAIL (chưa có `DigestDue`).

- [ ] **Step 3: Implement `DigestDue`**

```csharp
// Services/Digest/DigestDue.cs
namespace TourkitAiProxy.Services.Digest;

/// Chọn subscription "đến giờ gửi": đúng giờ VN + hôm nay (VN) chưa gửi.
/// Workflow brief chạy interval 60' → mỗi giờ VN chỉ khớp 1 lần; LastSentLocalDate chống double-send.
public static class DigestDue
{
    private static readonly TimeZoneInfo VnTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

    public static DateTime NowVn(DateTime utcNow)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), VnTz);

    public static bool IsDue(DigestSubscription sub, DateTime utcNow)
    {
        if (!sub.Enabled) return false;
        var vn = NowVn(utcNow);
        if (vn.Hour != DigestSubscription.ClampHour(sub.SendHourLocal)) return false;
        return sub.LastSentLocalDate?.Date != vn.Date;
    }
}
```

- [ ] **Step 4: Chạy pass** — 6/6 PASS.

- [ ] **Step 5: Implement 2 repository (Dapper — pattern `TkSessionRepository`/`MailQueueRepository`)**

```csharp
// Services/Digest/InsightRepository.cs
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Digest;

/// dbo.AgentInsights — feed thông báo/bản tin. Username='' = tenant-wide.
public class InsightRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<InsightRepository> _log;
    public InsightRepository(TourkitAiDb db, ILogger<InsightRepository> log) { _db = db; _log = log; }

    /// Insert 1 insight. AlertKey đã có trong 24h (cùng tenant) → dedup, trả null.
    public async Task<long?> InsertAsync(AgentInsight i, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        if (!string.IsNullOrEmpty(i.AlertKey))
        {
            var dup = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @TenantId AND AlertKey = @AlertKey
  AND CreatedUtc > DATEADD(HOUR, -24, SYSUTCDATETIME())",
                new { i.TenantId, i.AlertKey });
            if (dup > 0) return null;
        }
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.AgentInsights (TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc)
VALUES (@TenantId, @Username, @Kind, @Severity, @Title, @Body, @DataJson, @AlertKey, 0, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { i.TenantId, i.Username, i.Kind, i.Severity, i.Title, i.Body, i.DataJson, i.AlertKey });
    }

    /// Feed của user: row của chính user + row tenant-wide (Username='').
    public async Task<List<AgentInsight>> ListAsync(string tenant, string username, string? kind,
        bool unreadOnly, int offset, int limit, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<AgentInsight>(@"
SELECT Id, TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc
FROM dbo.AgentInsights
WHERE TenantId = @tenant AND (Username = @username OR Username = '')
  AND (@kind IS NULL OR Kind = @kind)
  AND (@unreadOnly = 0 OR IsRead = 0)
ORDER BY CreatedUtc DESC
OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY",
            new { tenant, username, kind, unreadOnly = unreadOnly ? 1 : 0, offset, limit = Math.Clamp(limit, 1, 100) });
        return rows.ToList();
    }

    public async Task<int> UnreadCountAsync(string tenant, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @tenant AND (Username = @username OR Username = '') AND IsRead = 0",
            new { tenant, username });
    }

    public async Task MarkReadAsync(string tenant, string username, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.AgentInsights SET IsRead = 1
WHERE Id = @id AND TenantId = @tenant AND (Username = @username OR Username = '')",
            new { id, tenant, username });
    }

    public async Task MarkAllReadAsync(string tenant, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.AgentInsights SET IsRead = 1
WHERE TenantId = @tenant AND (Username = @username OR Username = '') AND IsRead = 0",
            new { tenant, username });
    }

    /// Xóa insight cũ hơn keepDays. Gọi cuối mỗi workflow run.
    public async Task<int> PruneAsync(int keepDays, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(
            "DELETE FROM dbo.AgentInsights WHERE CreatedUtc < DATEADD(DAY, -@keepDays, SYSUTCDATETIME())",
            new { keepDays });
    }
}
```

```csharp
// Services/Digest/DigestSubscriptionRepository.cs
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Digest;

/// dbo.DigestSubscriptions — sổ người nhận bản tin (F5).
public class DigestSubscriptionRepository
{
    private readonly TourkitAiDb _db;
    public DigestSubscriptionRepository(TourkitAiDb db) { _db = db; }

    private const string Cols = @"TenantId, Username, BriefType, Enabled, SendHourLocal,
ChannelInApp, ChannelEmail, Email, ChannelTelegram, TelegramChatId, ChannelZalo, ZaloUserId,
LastSentUtc, LastSentLocalDate";

    public async Task<List<DigestSubscription>> ListForUserAsync(string tenant, string username, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<DigestSubscription>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND Username = @username",
            new { tenant, username });
        return rows.ToList();
    }

    public async Task<List<DigestSubscription>> ListEnabledAsync(string tenant, string briefType, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<DigestSubscription>(
            $"SELECT {Cols} FROM dbo.DigestSubscriptions WHERE TenantId = @tenant AND BriefType = @briefType AND Enabled = 1",
            new { tenant, briefType });
        return rows.ToList();
    }

    public async Task UpsertAsync(DigestSubscription s, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.DigestSubscriptions AS T
USING (SELECT @TenantId AS TenantId, @Username AS Username, @BriefType AS BriefType) AS S
    ON T.TenantId = S.TenantId AND T.Username = S.Username AND T.BriefType = S.BriefType
WHEN MATCHED THEN UPDATE SET
    Enabled = @Enabled, SendHourLocal = @SendHourLocal,
    ChannelInApp = @ChannelInApp, ChannelEmail = @ChannelEmail, Email = @Email,
    ChannelTelegram = @ChannelTelegram, TelegramChatId = @TelegramChatId,
    ChannelZalo = @ChannelZalo, ZaloUserId = @ZaloUserId, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (TenantId, Username, BriefType, Enabled, SendHourLocal, ChannelInApp, ChannelEmail, Email,
     ChannelTelegram, TelegramChatId, ChannelZalo, ZaloUserId, CreatedUtc, UpdatedUtc)
VALUES
    (@TenantId, @Username, @BriefType, @Enabled, @SendHourLocal, @ChannelInApp, @ChannelEmail, @Email,
     @ChannelTelegram, @TelegramChatId, @ChannelZalo, @ZaloUserId, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new
            {
                s.TenantId, s.Username, s.BriefType, s.Enabled,
                SendHourLocal = DigestSubscription.ClampHour(s.SendHourLocal),
                s.ChannelInApp, s.ChannelEmail, s.Email,
                s.ChannelTelegram, s.TelegramChatId, s.ChannelZalo, s.ZaloUserId
            });
    }

    public async Task MarkSentAsync(string tenant, string username, string briefType,
        DateTime utcNow, DateTime localDate, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.DigestSubscriptions SET LastSentUtc = @utcNow, LastSentLocalDate = @localDate, UpdatedUtc = SYSUTCDATETIME()
WHERE TenantId = @tenant AND Username = @username AND BriefType = @briefType",
            new { tenant, username, briefType, utcNow, localDate = localDate.Date });
    }
}
```

- [ ] **Step 6:** Build + toàn bộ test pass. **Commit** — `git commit -am "feat(digest): InsightRepository + DigestSubscriptionRepository + DigestDue (6 test múi giờ VN)"`

---

### Task 4: Kênh gửi — IDigestChannel × 4 + DigestDispatcher

**Files:**
- Create: `Services/Digest/Channels/IDigestChannel.cs`
- Create: `Services/Digest/Channels/InAppChannel.cs`
- Create: `Services/Digest/Channels/EmailChannel.cs`
- Create: `Services/Digest/Channels/TelegramChannel.cs`
- Create: `Services/Digest/Channels/ZaloOaChannel.cs`
- Create: `Services/Digest/Channels/TelegramFormat.cs`
- Create: `Services/Digest/TenantChannelSettingsStore.cs`
- Create: `Services/Digest/DigestDispatcher.cs`
- Test: `TourkitAiProxy.Tests/Digest/TelegramFormatTests.cs`, `TourkitAiProxy.Tests/Digest/DigestDispatcherTests.cs`

**Interfaces (Produces):**
- `interface IDigestChannel { string Id { get; } bool IsConfigured(DigestSubscription sub); Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct); }`
- `DigestDispatcher.SendAsync(DigestSubscription sub, DigestMessage m, ct)` → `Task<string>` (summary per-channel, vd `"inapp:ok email:ok telegram:FAIL"`)
- `TelegramFormat.ToTelegramHtml(string title, string bodyMarkdown)` → string (escape `& < >`, `**x**`→`<b>x</b>`, cắt 4096)
- `TenantChannelSettingsStore.GetZaloConfig(tenantId)` → `(string OaId, string AccessToken)?` (ConfigJson: `{"oaId":"...","accessTokenEnc":"<Crypton>"}`); `SaveZaloConfigAsync(tenantId, oaId, accessToken, ct)`

- [ ] **Step 1: Test fail — TelegramFormat + Dispatcher fail-isolation**

```csharp
// TourkitAiProxy.Tests/Digest/TelegramFormatTests.cs
using TourkitAiProxy.Services.Digest.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class TelegramFormatTests
{
    [Fact] public void Escape_html_dac_biet()
    {
        var s = TelegramFormat.ToTelegramHtml("A & B", "x < y > z");
        Assert.Contains("A &amp; B", s);
        Assert.Contains("x &lt; y &gt; z", s);
    }
    [Fact] public void Bold_markdown_thanh_the_b()
        => Assert.Contains("<b>Deal</b>", TelegramFormat.ToTelegramHtml("T", "**Deal** can goi"));
    [Fact] public void Cat_4096_ky_tu()
    {
        var s = TelegramFormat.ToTelegramHtml("T", new string('x', 9000));
        Assert.True(s.Length <= 4096);
    }
}
```

```csharp
// TourkitAiProxy.Tests/Digest/DigestDispatcherTests.cs
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Digest.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestDispatcherTests
{
    private sealed class FakeChannel : IDigestChannel
    {
        public string Id { get; init; } = "fake";
        public bool Configured = true; public bool Result = true; public bool Throws = false;
        public int Calls;
        public bool IsConfigured(DigestSubscription sub) => Configured;
        public Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
        { Calls++; if (Throws) throw new InvalidOperationException("boom"); return Task.FromResult(Result); }
    }
    private static DigestSubscription Sub() => new("t", "u", BriefTypes.Sale, true, 7,
        true, false, null, true, "123", false, null, null, null);
    private static DigestMessage Msg() => new("Tiêu đề", "body", "<p>body</p>", BriefTypes.Sale);

    [Fact] public async Task Kenh_loi_khong_chan_kenh_khac()
    {
        var a = new FakeChannel { Id = "a", Throws = true };
        var b = new FakeChannel { Id = "b" };
        var d = new DigestDispatcher(new IDigestChannel[] { a, b },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DigestDispatcher>.Instance);
        var summary = await d.SendAsync(Sub(), Msg(), CancellationToken.None);
        Assert.Equal(1, b.Calls);
        Assert.Contains("a:FAIL", summary);
        Assert.Contains("b:ok", summary);
    }
    [Fact] public async Task Kenh_chua_cau_hinh_bi_skip()
    {
        var a = new FakeChannel { Id = "a", Configured = false };
        var d = new DigestDispatcher(new IDigestChannel[] { a },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DigestDispatcher>.Instance);
        var summary = await d.SendAsync(Sub(), Msg(), CancellationToken.None);
        Assert.Equal(0, a.Calls);
        Assert.Contains("a:skip", summary);
    }
}
```

- [ ] **Step 2: Chạy fail** → FAIL (chưa có type).

- [ ] **Step 3: Implement contract + format + dispatcher**

```csharp
// Services/Digest/Channels/IDigestChannel.cs
namespace TourkitAiProxy.Services.Digest.Channels;

/// Kênh phát bản tin. SendAsync trả false khi fail (log Warning bên trong) — KHÔNG throw ra ngoài.
public interface IDigestChannel
{
    string Id { get; }                                   // "inapp" | "email" | "telegram" | "zalo"
    bool IsConfigured(DigestSubscription sub);           // sub có bật + đủ thông tin kênh này?
    Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct);
}
```

```csharp
// Services/Digest/Channels/TelegramFormat.cs
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Digest.Channels;

/// Đổi markdown tối giản của bản tin sang Telegram HTML (parse_mode=HTML), cắt 4096 ký tự.
public static class TelegramFormat
{
    public static string ToTelegramHtml(string title, string bodyMarkdown)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var body = Esc(bodyMarkdown ?? "");
        body = Regex.Replace(body, @"\*\*(.+?)\*\*", "<b>$1</b>");
        var text = $"<b>{Esc(title ?? "")}</b>\n\n{body}";
        return text.Length <= 4096 ? text : text[..4093] + "…";
    }
}
```

```csharp
// Services/Digest/DigestDispatcher.cs
using System.Text;
using TourkitAiProxy.Services.Digest.Channels;

namespace TourkitAiProxy.Services.Digest;

/// Fan-out 1 bản tin qua mọi kênh sub bật. Kênh fail/throw KHÔNG chặn kênh khác.
public class DigestDispatcher
{
    private readonly IReadOnlyList<IDigestChannel> _channels;
    private readonly ILogger<DigestDispatcher> _log;
    public DigestDispatcher(IEnumerable<IDigestChannel> channels, ILogger<DigestDispatcher> log)
    { _channels = channels.ToList(); _log = log; }

    public async Task<string> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var ch in _channels)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (!ch.IsConfigured(sub)) { sb.Append($"{ch.Id}:skip"); continue; }
            try
            {
                var ok = await ch.SendAsync(sub, m, ct);
                sb.Append($"{ch.Id}:{(ok ? "ok" : "FAIL")}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Digest] kênh {Ch} lỗi (tenant={T} user={U})", ch.Id, sub.TenantId, sub.Username);
                sb.Append($"{ch.Id}:FAIL");
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Chạy pass** (TelegramFormat 3 + Dispatcher 2).

- [ ] **Step 5: Implement 4 kênh + settings store** (không unit-test được — HTTP/DB; verify chạy thật ở Task 10)

```csharp
// Services/Digest/Channels/InAppChannel.cs
namespace TourkitAiProxy.Services.Digest.Channels;

public class InAppChannel : IDigestChannel
{
    private readonly InsightRepository _insights;
    public InAppChannel(InsightRepository insights) { _insights = insights; }
    public string Id => "inapp";
    public bool IsConfigured(DigestSubscription sub) => sub.ChannelInApp;
    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        await _insights.InsertAsync(new AgentInsight(0, sub.TenantId, sub.Username, m.Kind, m.Severity,
            m.Title, m.BodyMarkdown, null, null, false, DateTime.UtcNow), ct);
        return true;
    }
}
```

```csharp
// Services/Digest/Channels/EmailChannel.cs
using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest.Channels;

/// Enqueue dbo.OutboundMails — OutboundMailWorker (toutkit-app, ĐÃ tồn tại) render + gửi.
/// Template 'daily-brief' tạo tay 1 lần ở /admin-trav-ai/mail-templates (xem Task 12);
/// thiếu template → worker fallback render từ Params (hành vi sẵn có).
public class EmailChannel : IDigestChannel
{
    private readonly MailQueueRepository _queue;
    private readonly ILogger<EmailChannel> _log;
    public EmailChannel(MailQueueRepository queue, ILogger<EmailChannel> log) { _queue = queue; _log = log; }
    public string Id => "email";
    public bool IsConfigured(DigestSubscription sub) => sub.ChannelEmail && !string.IsNullOrWhiteSpace(sub.Email);
    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        var prms = JsonSerializer.Serialize(new
        {
            title = m.Title,
            bodyHtml = m.BodyHtml,
            briefType = m.Kind,
            date = DigestDue.NowVn(DateTime.UtcNow).ToString("dd/MM/yyyy")
        });
        await _queue.EnqueueAsync(new OutboundMailInput(
            sub.TenantId, Kind: "daily-brief", Username: sub.Username,
            TemplateCode: "daily-brief", ToEmail: sub.Email,
            Subject: m.Title, Params: prms), ct);
        return true;
    }
}
```

```csharp
// Services/Digest/Channels/TelegramChannel.cs
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Digest.Channels;

/// Gửi qua bot chung của hệ (Telegram:BotToken). Token rỗng → kênh coi như chưa cấu hình.
public class TelegramChannel : IDigestChannel
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TelegramChannel> _log;
    public TelegramChannel(IHttpClientFactory http, IConfiguration cfg, ILogger<TelegramChannel> log)
    { _http = http; _cfg = cfg; _log = log; }
    public string Id => "telegram";
    private string? Token => _cfg["Telegram:BotToken"];
    public bool IsConfigured(DigestSubscription sub)
        => sub.ChannelTelegram && !string.IsNullOrWhiteSpace(sub.TelegramChatId) && !string.IsNullOrWhiteSpace(Token);
    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            chat_id = sub.TelegramChatId,
            text = TelegramFormat.ToTelegramHtml(m.Title, m.BodyMarkdown),
            parse_mode = "HTML"
        });
        var client = _http.CreateClient("telegram");
        var resp = await client.PostAsync($"https://api.telegram.org/bot{Token}/sendMessage",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            _log.LogWarning("[Digest] Telegram gửi fail {Status} (user={U})", (int)resp.StatusCode, sub.Username);
        return resp.IsSuccessStatusCode;
    }
}
```

```csharp
// Services/Digest/TenantChannelSettingsStore.cs
using Dapper;
using System.Text.Json;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Digest;

/// dbo.TenantChannelSettings — config kênh per-tenant. Đợt 1: chỉ 'zalo-oa'.
/// Access token Crypton-enc trong ConfigJson: {"oaId":"...","accessTokenEnc":"..."}.
public class TenantChannelSettingsStore
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantChannelSettingsStore> _log;
    public TenantChannelSettingsStore(TourkitAiDb db, ILogger<TenantChannelSettingsStore> log) { _db = db; _log = log; }

    public (string OaId, string AccessToken)? GetZaloConfig(string tenantId)
    {
        try
        {
            using var c = _db.Open();
            var json = c.QueryFirstOrDefault<string?>(
                "SELECT ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId = @t AND Channel = 'zalo-oa'",
                new { t = tenantId });
            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var oaId = doc.RootElement.GetProperty("oaId").GetString() ?? "";
            var tokenEnc = doc.RootElement.GetProperty("accessTokenEnc").GetString() ?? "";
            var token = Crypton.Decrypt(tokenEnc);
            return string.IsNullOrEmpty(token) ? null : (oaId, token);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Digest] đọc Zalo config lỗi tenant {T}", tenantId); return null; }
    }

    public async Task SaveZaloConfigAsync(string tenantId, string oaId, string accessToken, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { oaId, accessTokenEnc = Crypton.Encrypt(accessToken) });
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.TenantChannelSettings AS T
USING (SELECT @t AS TenantId) AS S ON T.TenantId = S.TenantId AND T.Channel = 'zalo-oa'
WHEN MATCHED THEN UPDATE SET ConfigJson = @json, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Channel, ConfigJson, UpdatedUtc)
VALUES (@t, 'zalo-oa', @json, SYSUTCDATETIME());",
            new { t = tenantId, json });
    }
}
```

```csharp
// Services/Digest/Channels/ZaloOaChannel.cs
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Digest.Channels;

/// Zalo OA "tin tư vấn" (best-effort): CHỈ gửi được trong cửa sổ 48h sau khi user nhắn OA.
/// Ngoài cửa sổ → API trả lỗi → log Warning, KHÔNG fail run (đã ghi rõ trên UI).
public class ZaloOaChannel : IDigestChannel
{
    private readonly IHttpClientFactory _http;
    private readonly TenantChannelSettingsStore _settings;
    private readonly ILogger<ZaloOaChannel> _log;
    public ZaloOaChannel(IHttpClientFactory http, TenantChannelSettingsStore settings, ILogger<ZaloOaChannel> log)
    { _http = http; _settings = settings; _log = log; }
    public string Id => "zalo";
    public bool IsConfigured(DigestSubscription sub)
        => sub.ChannelZalo && !string.IsNullOrWhiteSpace(sub.ZaloUserId) && _settings.GetZaloConfig(sub.TenantId) != null;
    public async Task<bool> SendAsync(DigestSubscription sub, DigestMessage m, CancellationToken ct)
    {
        var cfg = _settings.GetZaloConfig(sub.TenantId);
        if (cfg == null) return false;
        var body = JsonSerializer.Serialize(new
        {
            recipient = new { user_id = sub.ZaloUserId },
            message = new { text = $"{m.Title}\n\n{m.BodyMarkdown}" }
        });
        var client = _http.CreateClient("zalo");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://openapi.zalo.me/v3.0/oa/message/cs")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("access_token", cfg.Value.AccessToken);
        var resp = await client.SendAsync(req, ct);
        var ok = resp.IsSuccessStatusCode;
        if (!ok) _log.LogWarning("[Digest] Zalo gửi fail {Status} (tenant={T}) — có thể ngoài cửa sổ 48h", (int)resp.StatusCode, sub.TenantId);
        return ok;
    }
}
```

- [ ] **Step 6:** Build + test pass toàn bộ. **Commit** — `git commit -am "feat(digest): 4 kênh gửi + DigestDispatcher fail-isolation + TelegramFormat"`

---

### Task 5: PaymentWatchdogRule (O2 — pure)

**Files:**
- Create: `Services/Digest/PaymentWatchdogRule.cs`
- Test: `TourkitAiProxy.Tests/Digest/PaymentWatchdogRuleTests.cs`

**Interfaces (Produces):**
- `record TourPaymentRow(int TourId, string Title, string? CustomerName, string? SellerName, DateTime DepartureDate, decimal Revenue, decimal ActualRevenue)`
- `record PaymentAlert(int TourId, string Title, string? CustomerName, string? SellerName, decimal Outstanding, DateTime DepartureDate, int DaysLeft, int Severity, string AlertKey)`
- `PaymentWatchdogRule.Evaluate(IEnumerable<TourPaymentRow> rows, DateTime todayLocal, int windowDays = 7)` → `List<PaymentAlert>`

- [ ] **Step 1: Test fail**

```csharp
// TourkitAiProxy.Tests/Digest/PaymentWatchdogRuleTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class PaymentWatchdogRuleTests
{
    private static readonly DateTime Today = new(2026, 8, 11);
    private static TourPaymentRow Row(int id, int daysToDeparture, decimal revenue, decimal actual)
        => new(id, $"Tour {id}", "Khách A", "Sale B", Today.AddDays(daysToDeparture), revenue, actual);

    [Fact] public void No_du_tien_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 5, 100m, 100m) }, Today));
    [Fact] public void Con_no_trong_cua_so_7_ngay_thi_canh_bao()
    {
        var a = Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 5, 100m, 40m) }, Today));
        Assert.Equal(60m, a.Outstanding);
        Assert.Equal("payment:1", a.AlertKey);
        Assert.Equal(1, a.Severity);           // D-5 → warning
        Assert.Equal(5, a.DaysLeft);
    }
    [Fact] public void D3_tro_xuong_la_critical()
        => Assert.Equal(2, PaymentWatchdogRule.Evaluate(new[] { Row(1, 3, 100m, 0m) }, Today).Single().Severity);
    [Fact] public void Ngoai_cua_so_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, 8, 100m, 0m) }, Today));
    [Fact] public void Da_khoi_hanh_hom_qua_khong_canh_bao()
        => Assert.Empty(PaymentWatchdogRule.Evaluate(new[] { Row(1, -1, 100m, 0m) }, Today));
    [Fact] public void Khoi_hanh_hom_nay_van_canh_bao_critical()
    {
        var a = Assert.Single(PaymentWatchdogRule.Evaluate(new[] { Row(1, 0, 100m, 50m) }, Today));
        Assert.Equal(2, a.Severity);
    }
}
```

- [ ] **Step 2: Chạy fail.** — [ ] **Step 3: Implement**

```csharp
// Services/Digest/PaymentWatchdogRule.cs
namespace TourkitAiProxy.Services.Digest;

public record TourPaymentRow(int TourId, string Title, string? CustomerName, string? SellerName,
    DateTime DepartureDate, decimal Revenue, decimal ActualRevenue);

public record PaymentAlert(int TourId, string Title, string? CustomerName, string? SellerName,
    decimal Outstanding, DateTime DepartureDate, int DaysLeft, int Severity, string AlertKey);

/// O2 — rule thuần: tour khởi hành trong [hôm nay, +windowDays] còn nợ (ActualRevenue < Revenue).
/// Severity: D≤3 → 2 (critical), còn lại 1 (warning). AlertKey "payment:{tourId}" cho dedup 24h.
public static class PaymentWatchdogRule
{
    public static List<PaymentAlert> Evaluate(IEnumerable<TourPaymentRow> rows, DateTime todayLocal, int windowDays = 7)
    {
        var result = new List<PaymentAlert>();
        foreach (var r in rows)
        {
            var daysLeft = (r.DepartureDate.Date - todayLocal.Date).Days;
            if (daysLeft < 0 || daysLeft > windowDays) continue;
            var outstanding = r.Revenue - r.ActualRevenue;
            if (outstanding <= 0) continue;
            result.Add(new PaymentAlert(r.TourId, r.Title, r.CustomerName, r.SellerName,
                outstanding, r.DepartureDate, daysLeft,
                Severity: daysLeft <= 3 ? 2 : 1,
                AlertKey: $"payment:{r.TourId}"));
        }
        return result.OrderBy(a => a.DaysLeft).ThenByDescending(a => a.Outstanding).ToList();
    }
}
```

- [ ] **Step 4:** 6/6 PASS. **Step 5: Commit** — `git commit -am "feat(digest): PaymentWatchdogRule (O2) — 6 test cửa sổ/severity/AlertKey"`

---

### Task 6: PaymentWatchdogWorkflow + đăng ký DI

**Files:**
- Create: `Services/Workflows/PaymentWatchdogWorkflow.cs`
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs` — thêm vào `AddWorkflowStack()`:
  `services.AddSingleton<InsightRepository>(); services.AddSingleton<DigestSubscriptionRepository>(); services.AddSingleton<TenantChannelSettingsStore>(); services.AddSingleton<Channels.IDigestChannel, Channels.InAppChannel>(); services.AddSingleton<Channels.IDigestChannel, Channels.EmailChannel>(); services.AddSingleton<Channels.IDigestChannel, Channels.TelegramChannel>(); services.AddSingleton<Channels.IDigestChannel, Channels.ZaloOaChannel>(); services.AddSingleton<DigestDispatcher>(); services.AddSingleton<IScheduledWorkflow, PaymentWatchdogWorkflow>();` (đúng namespace using; đọc file để đặt cạnh các workflow sẵn có)

**Interfaces:**
- Consumes: `PaymentWatchdogRule.Evaluate` (Task 5), `InsightRepository.InsertAsync` (Task 3), `TenantServiceAccountStore.Get`, `TkSessionStore.GetOrCreateServiceSessionAsync`/`GetValidJwtAsync`, `TourKitApiClient.GetAsync`
- Produces: workflow type `"payment-watchdog"` (UI `/workflows` tự pickup qua registry sẵn có)

- [ ] **Step 1: Implement workflow** (JSON mapping upstream: `/api/ai/tours` items có `Id, Title, CustomerName, SellerName, DepartureDate, Revenue, ActualRevenue` — verify 11/08 trong `TourDtos`/`BookingListItem`; field thiếu ở item nào thì skip item đó):

```csharp
// Services/Workflows/PaymentWatchdogWorkflow.cs
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// O2 — Canh thanh toán trước khởi hành. PerTenant, rule thuần (KHÔNG tốn quota AI).
/// Quét tour khởi hành trong 7 ngày còn nợ → ghi Insight 'payment-alert' (dedup AlertKey 24h).
public class PaymentWatchdogWorkflow : IScheduledWorkflow
{
    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly ILogger<PaymentWatchdogWorkflow> _log;

    public PaymentWatchdogWorkflow(TenantServiceAccountStore accounts, TkSessionStore sessions,
        TourKitApiClient api, InsightRepository insights, ILogger<PaymentWatchdogWorkflow> log)
    { _accounts = accounts; _sessions = sessions; _api = api; _insights = insights; _log = log; }

    public string Type => "payment-watchdog";
    public string Label => "Canh thanh toán trước khởi hành";
    public string Description => "Tour sắp khởi hành (7 ngày) còn nợ → cảnh báo vào Thông báo. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động (trang Tự động hóa) — workflow cần nó để đọc dữ liệu tour.");

        var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
        var jwt = await _sessions.GetValidJwtAsync(sid, ct);

        // Tour sắp khởi hành: lọc server-side theo StartDate nếu upstream hỗ trợ; luôn lọc lại client-side bằng rule.
        var todayVn = DigestDue.NowVn(DateTime.UtcNow).Date;
        var to = todayVn.AddDays(7);
        var data = await _api.GetAsync(jwt,
            $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);

        var rows = new List<TourPaymentRow>();
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (!TryGetInt(it, "id", out var id)) continue;
                if (!TryGetDate(it, "departureDate", out var dep)) continue;
                rows.Add(new TourPaymentRow(id,
                    GetStr(it, "title") ?? GetStr(it, "tourCode") ?? $"Tour #{id}",
                    GetStr(it, "customerName"), GetStr(it, "sellerName"),
                    dep, GetDec(it, "revenue"), GetDec(it, "actualRevenue")));
            }
        }

        var alerts = PaymentWatchdogRule.Evaluate(rows, todayVn);
        int created = 0, deduped = 0;
        foreach (var a in alerts)
        {
            var body = $"**{a.Title}** — khách {a.CustomerName ?? "?"} còn thiếu **{a.Outstanding:N0}đ**, "
                     + $"khởi hành {a.DepartureDate:dd/MM} (còn {a.DaysLeft} ngày). Phụ trách: {a.SellerName ?? "?"}.";
            var id = await _insights.InsertAsync(new AgentInsight(0, tenantId, "", "payment-alert", a.Severity,
                $"Thu nốt tiền tour trước khởi hành ({a.DaysLeft} ngày)", body,
                JsonSerializer.Serialize(new { a.TourId, a.Outstanding, a.DaysLeft }),
                a.AlertKey, false, DateTime.UtcNow), ct);
            if (id == null) deduped++; else created++;
        }
        await _insights.PruneAsync(90, ct);
        return new(true, $"Quét {rows.Count} tour sắp khởi hành → {alerts.Count} còn nợ ({created} cảnh báo mới, {deduped} đã báo trước đó).", null);
    }

    // JSON helpers — envelope /api/ai/* serialize camelCase.
    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal GetDec(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
    private static bool TryGetInt(JsonElement e, string name, out int val)
    { val = 0; if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetInt32(); return true; } return false; }
    private static bool TryGetDate(JsonElement e, string name, out DateTime val)
    { val = default; var s = GetStr(e, name); return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out val); }
}
```

- [ ] **Step 2:** Sửa `WorkflowStackRegistration` như phần Files. Build 0 error, test cũ pass.
- [ ] **Step 3: Verify chạy tay** — `dotnet run --project TourkitAiProxy.csproj`, mở `/workflows` thấy card "Canh thanh toán trước khởi hành"; bấm "Chạy ngay" với tenant có service account → summary "Quét N tour…" (hoặc lỗi thiếu service account đúng thông điệp).
- [ ] **Step 4: Commit** — `git commit -am "feat(digest): PaymentWatchdogWorkflow (O2) + wire DI digest stack"`

---

### Task 7: SaleBriefBuilder (pure — S1 + S5)

**Files:**
- Create: `Services/Digest/SaleBriefBuilder.cs`
- Test: `TourkitAiProxy.Tests/Digest/SaleBriefBuilderTests.cs`

**Interfaces (Produces):**
- Input records (định nghĩa trong cùng file): `DealLine(int DealId, string Title, string? CustomerName, int WinRate, int SilentDays, string? StatusText)` · `ApptLine(string Time, string Title, string? CustomerName)` · `CustomerLine(string Name, string Rank, int DaysSinceLastBooking)` · `QuoteLine(string Title, string? CustomerName, int DaysSinceUpdate)` · `record SaleBriefInput(string Username, string? FullName, List<DealLine> CoolingDeals, List<ApptLine> TodayAppointments, List<CustomerLine> SleepingVips, List<QuoteLine> StaleQuotes, int TenantMailPending, int TenantMailQuoteRequests, List<DealLine> HygieneDeals, List<PaymentAlert> MyPaymentAlerts, bool MailSourceOk)`
- `SaleBriefBuilder.Build(SaleBriefInput input, DateTime todayLocal)` → `DigestMessage` (Kind = `BriefTypes.Sale`)

- [ ] **Step 1: Test fail**

```csharp
// TourkitAiProxy.Tests/Digest/SaleBriefBuilderTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class SaleBriefBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 11);
    private static SaleBriefInput Empty(string user = "sale1") => new(user, "Nguyễn A",
        new(), new(), new(), new(), 0, 0, new(), new(), MailSourceOk: true);

    [Fact] public void Ban_tin_rong_van_co_loi_chuc()
    {
        var m = SaleBriefBuilder.Build(Empty(), Today);
        Assert.Contains("chưa có việc gấp", m.BodyMarkdown);
        Assert.Equal(BriefTypes.Sale, m.Kind);
    }
    [Fact] public void Deal_nguoi_hien_ten_va_so_ngay()
    {
        var input = Empty() with { CoolingDeals = new() { new DealLine(9, "Tour Đà Nẵng", "Anh Tú", 70, 6, "Đang tư vấn") } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.Contains("Tour Đà Nẵng", m.BodyMarkdown);
        Assert.Contains("6 ngày", m.BodyMarkdown);
        Assert.Contains("70%", m.BodyMarkdown);
    }
    [Fact] public void Top_5_moi_muc_khong_tran()
    {
        var deals = Enumerable.Range(1, 9)
            .Select(i => new DealLine(i, $"Deal {i}", null, 50, i, null)).ToList();
        var m = SaleBriefBuilder.Build(Empty() with { CoolingDeals = deals }, Today);
        Assert.Contains("Deal 5", m.BodyMarkdown);
        Assert.DoesNotContain("Deal 6", m.BodyMarkdown);
        Assert.Contains("và 4 deal khác", m.BodyMarkdown);
    }
    [Fact] public void Nguon_mail_loi_ghi_na()
    {
        var m = SaleBriefBuilder.Build(Empty() with { MailSourceOk = false, TenantMailPending = 0 }, Today);
        Assert.Contains("Hộp thư: n/a", m.BodyMarkdown);
    }
    [Fact] public void Payment_alert_cua_toi_xuat_hien()
    {
        var input = Empty() with { MyPaymentAlerts = new() {
            new PaymentAlert(3, "Tour Huế", "Chị Lan", 5_000_000m, Today.AddDays(2), 2, 2, "payment:3") } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.Contains("Tour Huế", m.BodyMarkdown);
        Assert.Contains("5.000.000", m.BodyMarkdown.Replace(",", "."));   // VND format vi-VN
    }
    [Fact] public void Tieu_de_co_ngay_va_ten()
    {
        var m = SaleBriefBuilder.Build(Empty(), Today);
        Assert.Contains("11/08", m.Title);
    }
}
```

- [ ] **Step 2: Chạy fail.** — [ ] **Step 3: Implement**

```csharp
// Services/Digest/SaleBriefBuilder.cs
using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Digest;

public record DealLine(int DealId, string Title, string? CustomerName, int WinRate, int SilentDays, string? StatusText);
public record ApptLine(string Time, string Title, string? CustomerName);
public record CustomerLine(string Name, string Rank, int DaysSinceLastBooking);
public record QuoteLine(string Title, string? CustomerName, int DaysSinceUpdate);

public record SaleBriefInput(
    string Username, string? FullName,
    List<DealLine> CoolingDeals, List<ApptLine> TodayAppointments,
    List<CustomerLine> SleepingVips, List<QuoteLine> StaleQuotes,
    int TenantMailPending, int TenantMailQuoteRequests,
    List<DealLine> HygieneDeals, List<PaymentAlert> MyPaymentAlerts,
    bool MailSourceOk);

/// S1 + S5 — render RULE THUẦN (0 AI). Markdown + HTML từ cùng 1 nguồn section.
public static class SaleBriefBuilder
{
    private const int TopN = 5;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi) + "đ";

    public static DigestMessage Build(SaleBriefInput input, DateTime todayLocal)
    {
        var md = new StringBuilder();
        int sections = 0;

        void Section(string heading, IReadOnlyList<string> lines, int total)
        {
            if (lines.Count == 0) return;
            sections++;
            md.AppendLine($"**{heading}**");
            foreach (var l in lines) md.AppendLine($"- {l}");
            var more = total - lines.Count;
            if (more > 0) md.AppendLine($"- … và {more} deal khác");
            md.AppendLine();
        }

        Section($"📞 Deal cần gọi lại ({input.CoolingDeals.Count})",
            input.CoolingDeals.Take(TopN).Select(d =>
                $"**{d.Title}**{(d.CustomerName != null ? $" — {d.CustomerName}" : "")} · im lặng {d.SilentDays} ngày · WinRate {d.WinRate}%").ToList(),
            input.CoolingDeals.Count);

        Section($"🗓 Lịch hẹn hôm nay ({input.TodayAppointments.Count})",
            input.TodayAppointments.Take(TopN).Select(a =>
                $"{a.Time} — {a.Title}{(a.CustomerName != null ? $" ({a.CustomerName})" : "")}").ToList(),
            input.TodayAppointments.Count);

        Section($"💰 Khách sắp đi còn thiếu tiền ({input.MyPaymentAlerts.Count})",
            input.MyPaymentAlerts.Take(TopN).Select(p =>
                $"**{p.Title}** — {p.CustomerName ?? "?"} thiếu {Vnd(p.Outstanding)}, còn {p.DaysLeft} ngày").ToList(),
            input.MyPaymentAlerts.Count);

        Section($"💤 Khách VIP lâu không chăm ({input.SleepingVips.Count})",
            input.SleepingVips.Take(TopN).Select(c =>
                $"{c.Name} (hạng {c.Rank}) — {c.DaysSinceLastBooking} ngày chưa có booking mới").ToList(),
            input.SleepingVips.Count);

        Section($"📄 Báo giá lâu chưa động ({input.StaleQuotes.Count})",
            input.StaleQuotes.Take(TopN).Select(q =>
                $"{q.Title}{(q.CustomerName != null ? $" — {q.CustomerName}" : "")} · {q.DaysSinceUpdate} ngày chưa cập nhật").ToList(),
            input.StaleQuotes.Count);

        Section($"🧹 Deal cần dọn ({input.HygieneDeals.Count})",
            input.HygieneDeals.Take(3).Select(d =>
                $"{d.Title} — kẹt \"{d.StatusText ?? "?"}\" {d.SilentDays} ngày, chưa có bước tiếp theo").ToList(),
            input.HygieneDeals.Count);

        // Hộp thư (tenant-wide) — luôn 1 dòng
        md.AppendLine(input.MailSourceOk
            ? $"📬 Hộp thư: {input.TenantMailPending} mail chờ xử lý ({input.TenantMailQuoteRequests} hỏi giá)."
            : "📬 Hộp thư: n/a (không đọc được).");

        if (sections == 0)
            md.Insert(0, "Hôm nay chưa có việc gấp 🎉 — dành thời gian chăm khách cũ nhé.\n\n");

        var title = $"Bản tin sáng {todayLocal:dd/MM} — {input.FullName ?? input.Username}";
        var bodyMd = md.ToString().TrimEnd();
        var bodyHtml = "<div style=\"font-family:sans-serif;line-height:1.6\">"
            + System.Text.RegularExpressions.Regex.Replace(
                System.Net.WebUtility.HtmlEncode(bodyMd), @"\*\*(.+?)\*\*", "<b>$1</b>")
                .Replace("\n", "<br>")
            + "</div>";
        return new DigestMessage(title, bodyMd, bodyHtml, BriefTypes.Sale);
    }
}
```

- [ ] **Step 4:** 6/6 PASS. **Step 5: Commit** — `git commit -am "feat(digest): SaleBriefBuilder (S1+S5) rule thuần — 6 test"`

---

### Task 8: SaleBriefWorkflow (fetch + per-recipient + dispatch)

**Files:**
- Create: `Services/Workflows/SaleBriefWorkflow.cs`
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs` — thêm `services.AddSingleton<IScheduledWorkflow, SaleBriefWorkflow>();`

**Interfaces:**
- Consumes: `DigestDue.IsDue`, `DigestSubscriptionRepository.ListEnabledAsync/MarkSentAsync`, `SaleBriefBuilder.Build`, `DigestDispatcher.SendAsync`, `PaymentWatchdogRule` types, `MailRepository.Counts`, `TkSessionRepository` (lấy `CrmUserId` + `FullName` của recipient qua `GetByUserAsync(tenant, username, ct)`), Dapper trực tiếp lên `dbo.DealScores`/`dbo.Reviews`/`dbo.TourQuotes` (cột đã verify: DealScores có `DataJson`; TourQuotes có `CreatedBy/UpdatedAt/Title/CustomerName`)
- Nguyên tắc fetch: **service account fetch 1 lần** → filter per-recipient; nguồn nào lỗi → section rỗng + `MailSourceOk=false` kiểu "n/a", KHÔNG fail run.

- [ ] **Step 1: Implement workflow.** Khung (code đầy đủ — phần đọc `DealScores.DataJson` lấy field `assigneeName`/`statusText`/`winRate`/`lastActivityDays` theo shape DealRepository đã lưu; NGƯỜI THỰC HIỆN đọc `Services/Deals/DealRepository.cs` 5 phút để khớp đúng tên property trong DataJson trước khi viết `ParseDealLine` — đây là bước bắt buộc, không đoán):

```csharp
// Services/Workflows/SaleBriefWorkflow.cs
using Dapper;
using System.Text.Json;
using TourkitAiProxy.Services.Db;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Mail;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Workflows;

/// S1 — Bản tin sáng cho Sale. PerTenant interval 60': mỗi lần chạy chọn subscription "đến giờ".
/// Fetch data 1 lần bằng service account → filter per-recipient (CrmUserId/username) → render rule thuần → dispatch.
public class SaleBriefWorkflow : IScheduledWorkflow
{
    private readonly DigestSubscriptionRepository _subs;
    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TkSessionRepository _sessionRepo;
    private readonly TourKitApiClient _api;
    private readonly TourkitAiDb _db;
    private readonly MailRepository _mails;
    private readonly InsightRepository _insights;
    private readonly DigestDispatcher _dispatcher;
    private readonly ILogger<SaleBriefWorkflow> _log;

    public SaleBriefWorkflow(DigestSubscriptionRepository subs, TenantServiceAccountStore accounts,
        TkSessionStore sessions, TkSessionRepository sessionRepo, TourKitApiClient api, TourkitAiDb db,
        MailRepository mails, InsightRepository insights, DigestDispatcher dispatcher, ILogger<SaleBriefWorkflow> log)
    { _subs = subs; _accounts = accounts; _sessions = sessions; _sessionRepo = sessionRepo;
      _api = api; _db = db; _mails = mails; _insights = insights; _dispatcher = dispatcher; _log = log; }

    public string Type => "sale-brief";
    public string Label => "Bản tin sáng cho Sale";
    public string Description => "Mỗi sáng gom việc cần làm (deal nguội, lịch hẹn, khách VIP, báo giá) gửi từng sale đã đăng ký. Không tốn lượt AI.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        var due = (await _subs.ListEnabledAsync(tenantId, BriefTypes.Sale, ct))
            .Where(s => DigestDue.IsDue(s, utcNow)).ToList();
        if (due.Count == 0) return new(true, "Chưa tới giờ gửi của ai (0 đăng ký đến hạn).", null);

        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động — bản tin cần nó để đọc dữ liệu.");
        var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
        var jwt = await _sessions.GetValidJwtAsync(sid, ct);
        var todayVn = DigestDue.NowVn(utcNow).Date;

        // ── Fetch 1 lần cho cả tenant — mỗi nguồn fail-soft ──
        List<JsonElement> appts = new(); bool apptOk = true;
        try
        {
            var a = await _api.GetAsync(jwt, "/api/ai/appointments?DateFilter=1&PageSize=200", ct);
            if (a.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                appts = arr.EnumerateArray().Select(x => x.Clone()).ToList();
            else if (a.TryGetProperty("appointments", out var arr2) && arr2.ValueKind == JsonValueKind.Array)
                appts = arr2.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        catch (Exception ex) { apptOk = false; _log.LogWarning(ex, "[SaleBrief] fetch lịch hẹn fail {T}", tenantId); }

        List<(string Assignee, DealLine Line, bool Hygiene)> deals = new();
        try { deals = await FetchDealsAsync(tenantId, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "[SaleBrief] đọc DealScores fail {T}", tenantId); }

        List<(string CreatedBy, QuoteLine Line)> quotes = new();
        try { quotes = await FetchStaleQuotesAsync(tenantId, todayVn, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "[SaleBrief] đọc TourQuotes fail {T}", tenantId); }

        MailCounts? mailCounts = null;
        try { mailCounts = _mails.Counts(tenantId); }
        catch (Exception ex) { _log.LogWarning(ex, "[SaleBrief] đọc MailCounts fail {T}", tenantId); }

        List<(string? Seller, PaymentAlert Alert)> payments = new();
        try { payments = await FetchPaymentAlertsAsync(jwt, todayVn, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "[SaleBrief] fetch tours fail {T}", tenantId); }

        // ── Per-recipient build + dispatch ──
        int sent = 0, failed = 0;
        var summaries = new List<string>();
        foreach (var sub in due)
        {
            try
            {
                var session = await _sessionRepo.GetByUserAsync(tenantId, sub.Username, ct);
                var fullName = session?.FullName;
                // So khớp assignee: theo FullName (dữ liệu CRM trả tên) — fallback username.
                bool Mine(string? assignee) =>
                    !string.IsNullOrEmpty(assignee) &&
                    (string.Equals(assignee, fullName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(assignee, sub.Username, StringComparison.OrdinalIgnoreCase));

                var input = new SaleBriefInput(sub.Username, fullName,
                    CoolingDeals: deals.Where(d => !d.Hygiene && Mine(d.Assignee)).Select(d => d.Line)
                        .OrderByDescending(l => l.SilentDays).ToList(),
                    TodayAppointments: appts.Where(a => Mine(GetStr(a, "assignee"))).Select(a =>
                        new ApptLine(GetStr(a, "scheduleTimeFormatted") ?? "", GetStr(a, "title") ?? "",
                                     GetStr(a, "customerName"))).ToList(),
                    SleepingVips: new(),   // Đợt 1 mức thô: chưa join lần-mua-cuối per user (ghi rõ trong spec §5.1 mục 3 — bật ở bước sau khi có nguồn per-user ổn)
                    StaleQuotes: quotes.Where(q => string.Equals(q.CreatedBy, sub.Username, StringComparison.OrdinalIgnoreCase))
                        .Select(q => q.Line).ToList(),
                    TenantMailPending: (mailCounts?.ByStatus.GetValueOrDefault("moi") ?? 0),
                    TenantMailQuoteRequests: (mailCounts?.ByCategory.GetValueOrDefault("xin_bao_gia") ?? 0)
                        + (mailCounts?.ByCategory.GetValueOrDefault("hoi_dat_tour") ?? 0),
                    HygieneDeals: deals.Where(d => d.Hygiene && Mine(d.Assignee)).Select(d => d.Line).ToList(),
                    MyPaymentAlerts: payments.Where(p => Mine(p.Seller)).Select(p => p.Alert).ToList(),
                    MailSourceOk: mailCounts != null);

                var msg = SaleBriefBuilder.Build(input, todayVn);
                var chSummary = await _dispatcher.SendAsync(sub, msg, ct);
                await _subs.MarkSentAsync(tenantId, sub.Username, BriefTypes.Sale, utcNow, todayVn, ct);
                sent++;
                summaries.Add($"{sub.Username}[{chSummary}]");
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "[SaleBrief] gửi fail user {U} tenant {T}", sub.Username, tenantId);
            }
        }
        if (!apptOk) summaries.Add("(lịch hẹn: n/a)");
        await _insights.PruneAsync(90, ct);
        return new(failed == 0, $"Gửi {sent}/{due.Count} bản tin: {string.Join(", ", summaries)}",
            failed > 0 ? $"{failed} người gửi lỗi (xem log)" : null);
    }

    /// DealScores: parse DataJson lấy assignee + trạng thái + winrate + số ngày im lặng.
    /// LƯU Ý NGƯỜI THỰC HIỆN: đọc Services/Deals/DealRepository.cs để khớp CHÍNH XÁC tên property
    /// trong DataJson (assigneeName / sellerName / statusText / winRate / silentDays / lastActivity...).
    /// Ngưỡng: nguội ≥ 5 ngày → CoolingDeal; kẹt ≥ 14 ngày → Hygiene.
    private async Task<List<(string Assignee, DealLine Line, bool Hygiene)>> FetchDealsAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(int DealId, string DataJson)>(
            "SELECT DealId, DataJson FROM dbo.DealScores WHERE TenantId = @tenantId AND IsFinalized = 0",
            new { tenantId });
        var result = new List<(string, DealLine, bool)>();
        foreach (var (dealId, dataJson) in rows)
        {
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                var e = doc.RootElement;
                var assignee = GetStr(e, "assigneeName") ?? GetStr(e, "sellerName") ?? "";
                if (string.IsNullOrEmpty(assignee)) continue;
                var silent = GetInt(e, "silentDays") ?? GetInt(e, "coolingDays") ?? 0;
                var line = new DealLine(dealId, GetStr(e, "title") ?? $"Deal #{dealId}",
                    GetStr(e, "customerName"), GetInt(e, "winRate") ?? 0, silent, GetStr(e, "statusText"));
                if (silent >= 14) result.Add((assignee, line, true));
                else if (silent >= 5) result.Add((assignee, line, false));
            }
            catch { /* row DataJson rác → skip */ }
        }
        return result;
    }

    private async Task<List<(string CreatedBy, QuoteLine Line)>> FetchStaleQuotesAsync(string tenantId, DateTime todayVn, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<(string? CreatedBy, string? Title, string? CustomerName, DateTime UpdatedAt)>(
            @"SELECT CreatedBy, Title, CustomerName, UpdatedAt FROM dbo.TourQuotes
              WHERE TenantId = @tenantId AND UpdatedAt < DATEADD(DAY, -5, SYSUTCDATETIME())", new { tenantId });
        return rows.Where(r => !string.IsNullOrEmpty(r.CreatedBy))
            .Select(r => (r.CreatedBy!, new QuoteLine(r.Title ?? "(chưa đặt tên)", r.CustomerName,
                (int)(DateTime.UtcNow - r.UpdatedAt).TotalDays))).ToList();
    }

    private async Task<List<(string? Seller, PaymentAlert Alert)>> FetchPaymentAlertsAsync(string jwt, DateTime todayVn, CancellationToken ct)
    {
        var to = todayVn.AddDays(7);
        var data = await _api.GetAsync(jwt, $"/api/ai/tours?StartDate={todayVn:yyyy-MM-dd}&EndDate={to:yyyy-MM-dd}&PageSize=200", ct);
        var rows = new List<TourPaymentRow>();
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                var id = GetInt(it, "id"); if (id == null) continue;
                var dep = GetStr(it, "departureDate");
                if (!DateTime.TryParse(dep, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var depDate)) continue;
                rows.Add(new TourPaymentRow(id.Value, GetStr(it, "title") ?? $"Tour #{id}", GetStr(it, "customerName"),
                    GetStr(it, "sellerName"), depDate, GetDec(it, "revenue"), GetDec(it, "actualRevenue")));
            }
        return PaymentWatchdogRule.Evaluate(rows, todayVn).Select(a => (a.SellerName, a)).ToList();
    }

    private static string? GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static decimal GetDec(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
```

**Chú ý bắt buộc trước khi code:** mở `Services/Deals/DealRepository.cs` + 1 row `DealScores.DataJson` thật để khớp tên property (bước "đọc 5 phút" ghi trong comment `FetchDealsAsync`); cột `IsFinalized` phải kiểm tồn tại trong schema DealScores (nếu tên khác — vd `Finalized` — sửa query theo tên thật).

- [ ] **Step 2:** Build + test cũ pass. Chạy tay: tạo 1 subscription trực tiếp SQL (INSERT dbo.DigestSubscriptions với SendHourLocal = giờ hiện tại VN) → `/workflows` "Chạy ngay" `sale-brief` → summary `Gửi 1/1...`; kiểm `dbo.AgentInsights` có row mới.
- [ ] **Step 3: Commit** — `git commit -am "feat(digest): SaleBriefWorkflow — fetch 1 lần, filter per-recipient, dispatch đa kênh"`

---

### Task 9: CeoBriefBuilder + CeoBriefWorkflow (+ `AiFeatures.Digest`) — ✅ XONG 12/08/2026

> **[SỬA khi làm thật] 4 điểm khác bản kế hoạch:**
> 1. **Tên field financial-summary trong plan là ĐOÁN SAI.** Plan ghi `revenue/expense/profit`; tên
>    thật (đối chiếu `DashboardService.GetAiFinancialSummaryAsync` bên TourKit.Api) là
>    `kpiRevenue` / `kpiTotalExpense` / `kpiGrossProfit`. Đoán sai thì không khớp field nào, bản tin
>    vẫn gửi nhưng báo 0đ khắp nơi — sai mà trông như chạy tốt. Đã khoá bằng test.
>    Tương tự: booking-tickets KHÔNG có `CreatedFrom/CreatedTo`, dùng `StartDate/EndDate` (lọc theo
>    `InsDttm` = ngày tạo).
> 2. **Không dùng `TenantServiceAccounts`** (theo sửa 12/08 ở đầu plan): fetch bằng phiên của chính
>    người nhận. Kéo theo: bỏ luôn phần re-check `CH_XEM_ALL` mỗi lần gửi.
> 3. **"1 lượt AI/tenant/ngày" đổi thành "1 lượt AI/BỘ SỐ".** Gộp theo tenant chỉ đúng khi mọi người
>    thấy cùng bộ số — không còn đúng khi mỗi người fetch bằng token riêng. Nay cache theo dấu vân
>    tay bộ số: thường vẫn 1 lượt, ai thấy số khác thì được viết riêng cho đúng số của mình.
> 4. **Model phải qua `AiModelRegistry.Resolve(AiFeature.Digest)`**, không gọi `ProviderRegistry`
>    trần. Bản đầu gọi trần → provider tự lấy model "Recommended" của nó (Sonnet) và bỏ qua
>    `Models:Primary` (Haiku) trong cấu hình → hoá đơn tính giá Sonnet. Người dùng phát hiện khi soi
>    `dbo.AiUsageHistory`. Đã thêm `AiFeature.Digest` + mục `Models:Digest` trong appsettings.example.
>
> **Đã sửa thêm sau khi đọc bản tin thật:** con số "quá hạn" (2.338 cuộc) là TỒN ĐỌNG tích luỹ nhiều
> năm, AI viết thành "cần xử lý ngay lập tức" → đọc như công ty đang cháy. Nay gọi đúng tên "tồn đọng
> (tích luỹ từ trước)" + dặn prompt đừng coi là khẩn cấp trong ngày; và dặn thêm: chi phí 0đ nghĩa là
> CHƯA GHI NHẬN, không được kết luận "lãi trọn doanh thu".

**Files:**
- Create: `Services/Digest/CeoBriefBuilder.cs`
- Create: `Services/Workflows/CeoBriefWorkflow.cs`
- Modify: `Services/AiCallContext.cs` — thêm `public const string Digest = "digest";` vào `AiFeatures` (khu HTTP/workflow features)
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs` — `services.AddSingleton<IScheduledWorkflow, CeoBriefWorkflow>();`
- Test: `TourkitAiProxy.Tests/Digest/CeoBriefBuilderTests.cs`

**Interfaces (Produces):**
- `record CeoNumbers(decimal Revenue, decimal Expense, decimal Profit)`
- `record CeoBriefData(CeoNumbers ThisMtd, CeoNumbers PrevMtd, List<string> TopSellers, int NewDealsYesterday, int OpenPaymentAlerts)`
- `CeoBriefBuilder.BuildPrompt(CeoBriefData d, DateTime todayLocal)` → `string` (prompt cho AI — chứa số đã format + lệnh cấm bịa số)
- `CeoBriefBuilder.RenderFallback(CeoBriefData d, DateTime todayLocal)` → `DigestMessage` (rule-based khi AI fail)
- `CeoBriefBuilder.WrapAiReply(string aiProse, CeoBriefData d, DateTime todayLocal)` → `DigestMessage`
- `CeoBriefBuilder.PctChange(decimal cur, decimal prev)` → `string` (vd `"+12%"`, prev=0 → `"n/a"`)

- [ ] **Step 1: Test fail**

```csharp
// TourkitAiProxy.Tests/Digest/CeoBriefBuilderTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class CeoBriefBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 11);
    private static CeoBriefData Data() => new(
        ThisMtd: new(1_000_000_000m, 700_000_000m, 300_000_000m),
        PrevMtd: new(800_000_000m, 600_000_000m, 200_000_000m),
        TopSellers: new() { "An — 500tr", "Bình — 300tr" },
        NewDealsYesterday: 4, OpenPaymentAlerts: 2);

    [Theory]
    [InlineData(120, 100, "+20%")] [InlineData(80, 100, "-20%")] [InlineData(100, 0, "n/a")]
    public void PctChange_dung(decimal cur, decimal prev, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.PctChange(cur, prev));

    [Fact] public void Prompt_chua_so_thuc_va_lenh_cam_bia()
    {
        var p = CeoBriefBuilder.BuildPrompt(Data(), Today);
        Assert.Contains("1.000.000.000", p.Replace(",", "."));
        Assert.Contains("không", p.ToLowerInvariant());   // lệnh "không bịa số"
    }
    [Fact] public void Fallback_render_du_3_so_chinh()
    {
        var m = CeoBriefBuilder.RenderFallback(Data(), Today);
        Assert.Contains("Doanh thu", m.BodyMarkdown);
        Assert.Contains("+25%", m.BodyMarkdown);   // doanh thu 1000 vs 800
        Assert.Equal(BriefTypes.Ceo, m.Kind);
    }
    [Fact] public void WrapAiReply_giu_prose_va_gan_so_goc()
    {
        var m = CeoBriefBuilder.WrapAiReply("Doanh thu tăng tốt.", Data(), Today);
        Assert.StartsWith("Doanh thu tăng tốt.", m.BodyMarkdown);
        Assert.Contains("Doanh thu:", m.BodyMarkdown);   // bảng số gốc đính kèm dưới prose
    }
}
```

- [ ] **Step 2: Chạy fail.** — [ ] **Step 3: Implement**

```csharp
// Services/Digest/CeoBriefBuilder.cs
using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Digest;

public record CeoNumbers(decimal Revenue, decimal Expense, decimal Profit);
public record CeoBriefData(CeoNumbers ThisMtd, CeoNumbers PrevMtd, List<string> TopSellers,
    int NewDealsYesterday, int OpenPaymentAlerts);

/// C1 — số tính server-side; AI CHỈ viết prose từ số cho sẵn. AI fail → RenderFallback.
public static class CeoBriefBuilder
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string Vnd(decimal v) => v.ToString("N0", Vi) + "đ";

    public static string PctChange(decimal cur, decimal prev)
    {
        if (prev == 0) return "n/a";
        var pct = Math.Round((cur - prev) / prev * 100);
        return (pct >= 0 ? "+" : "") + pct + "%";
    }

    private static string NumbersBlock(CeoBriefData d) => new StringBuilder()
        .AppendLine($"- Doanh thu: {Vnd(d.ThisMtd.Revenue)} ({PctChange(d.ThisMtd.Revenue, d.PrevMtd.Revenue)} so cùng kỳ tháng trước)")
        .AppendLine($"- Chi phí: {Vnd(d.ThisMtd.Expense)} ({PctChange(d.ThisMtd.Expense, d.PrevMtd.Expense)})")
        .AppendLine($"- Lợi nhuận: {Vnd(d.ThisMtd.Profit)} ({PctChange(d.ThisMtd.Profit, d.PrevMtd.Profit)})")
        .AppendLine($"- Deal mới hôm qua: {d.NewDealsYesterday} · Cảnh báo thanh toán đang mở: {d.OpenPaymentAlerts}")
        .AppendLine($"- Top seller MTD: {(d.TopSellers.Count > 0 ? string.Join("; ", d.TopSellers.Take(3)) : "n/a")}")
        .ToString().TrimEnd();

    public static string BuildPrompt(CeoBriefData d, DateTime todayLocal) =>
        $"Bạn là trợ lý điều hành cho giám đốc công ty du lịch. Hôm nay {todayLocal:dd/MM/yyyy}.\n" +
        "Viết 5-8 câu tiếng Việt tổng kết tình hình từ CHÍNH XÁC các số dưới đây. " +
        "TUYỆT ĐỐI không bịa thêm số nào ngoài input, không markdown heading, giọng tự nhiên, đi thẳng vào ý chính:\n\n" +
        NumbersBlock(d);

    public static DigestMessage RenderFallback(CeoBriefData d, DateTime todayLocal)
        => Wrap(NumbersBlock(d), d, todayLocal);

    public static DigestMessage WrapAiReply(string aiProse, CeoBriefData d, DateTime todayLocal)
        => Wrap(aiProse.Trim() + "\n\n**Số liệu:**\n" + NumbersBlock(d), d, todayLocal);

    private static DigestMessage Wrap(string bodyMd, CeoBriefData d, DateTime todayLocal)
    {
        var title = $"Bản tin điều hành {todayLocal:dd/MM}";
        var bodyHtml = "<div style=\"font-family:sans-serif;line-height:1.6\">"
            + System.Text.RegularExpressions.Regex.Replace(
                System.Net.WebUtility.HtmlEncode(bodyMd), @"\*\*(.+?)\*\*", "<b>$1</b>").Replace("\n", "<br>")
            + "</div>";
        return new DigestMessage(title, bodyMd, bodyHtml, BriefTypes.Ceo);
    }
}
```

- [ ] **Step 4:** Test PASS (6). — [ ] **Step 5: Implement `CeoBriefWorkflow`** (khung giống `SaleBriefWorkflow` — khác phần fetch + AI):

```csharp
// Services/Workflows/CeoBriefWorkflow.cs
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Workflows;

/// C1 — Bản tin điều hành. PerTenant interval 60'. Số server-side; ĐÚNG 1 AI call/tenant/ngày
/// (share cho mọi subscriber cùng tenant); AI fail → fallback rule-based, bản tin không bao giờ mất.
public class CeoBriefWorkflow : IScheduledWorkflow
{
    private readonly DigestSubscriptionRepository _subs;
    private readonly TenantServiceAccountStore _accounts;
    private readonly TkSessionStore _sessions;
    private readonly TourKitApiClient _api;
    private readonly InsightRepository _insights;
    private readonly DigestDispatcher _dispatcher;
    private readonly ProviderRegistry _providers;
    private readonly AiCallContext _ctx;
    private readonly ILogger<CeoBriefWorkflow> _log;

    public CeoBriefWorkflow(DigestSubscriptionRepository subs, TenantServiceAccountStore accounts,
        TkSessionStore sessions, TourKitApiClient api, InsightRepository insights,
        DigestDispatcher dispatcher, ProviderRegistry providers, AiCallContext ctx, ILogger<CeoBriefWorkflow> log)
    { _subs = subs; _accounts = accounts; _sessions = sessions; _api = api; _insights = insights;
      _dispatcher = dispatcher; _providers = providers; _ctx = ctx; _log = log; }

    public string Type => "ceo-brief";
    public string Label => "Bản tin điều hành (CEO)";
    public string Description => "Doanh thu – chi phí – lợi nhuận so cùng kỳ + biến động chính, gửi mỗi sáng cho người có quyền xem toàn bộ. Tối đa 1 lượt AI/ngày.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        var allDue = (await _subs.ListEnabledAsync(tenantId, BriefTypes.Ceo, ct))
            .Where(s => DigestDue.IsDue(s, utcNow)).ToList();
        if (allDue.Count == 0) return new(true, "Chưa tới giờ gửi của ai.", null);

        // Re-check quyền mỗi lần gửi: quyền bị thu → ngừng gửi (đọc PermissionsJson session mới nhất của user).
        var due = new List<DigestSubscription>();
        foreach (var s in allDue)
        {
            var sess = _sessions.ListActive().FirstOrDefault(x =>
                x.TenantId == tenantId && string.Equals(x.Username, s.Username, StringComparison.OrdinalIgnoreCase));
            if (sess != null && sess.Permissions.Any(p => string.Equals(p, TkPermissionCodes.XemToanBoCoHoi, StringComparison.OrdinalIgnoreCase)))
                due.Add(s);
            else _log.LogInformation("[CeoBrief] {U}@{T} không còn quyền CH_XEM_ALL → skip", s.Username, tenantId);
        }
        if (due.Count == 0) return new(true, $"{allDue.Count} đăng ký đến hạn nhưng không ai còn quyền.", null);

        var acc = _accounts.Get(tenantId);
        if (acc == null || !acc.Enabled)
            return new(false, null, "Chưa cấu hình tài khoản tự động.");
        var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, acc.Username, acc.Password, ct);
        var jwt = await _sessions.GetValidJwtAsync(sid, ct);
        var todayVn = DigestDue.NowVn(utcNow).Date;

        var data = await FetchDataAsync(jwt, todayVn, ct);

        // 1 AI call/tenant/ngày — mọi subscriber dùng chung 1 bản.
        DigestMessage msg;
        try
        {
            using var _ = _ctx.Push(AiFeatures.Digest, tenantId, sid);
            var provider = _providers.Resolve(null);
            var r = await provider.CompleteAsync(new CompleteRequest
            { Prompt = CeoBriefBuilder.BuildPrompt(data, todayVn), MaxTokens = 1200, Temperature = 0.4 }, ct);
            msg = string.IsNullOrWhiteSpace(r.Text)
                ? CeoBriefBuilder.RenderFallback(data, todayVn)
                : CeoBriefBuilder.WrapAiReply(r.Text, data, todayVn);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CeoBrief] AI fail tenant {T} → fallback rule-based", tenantId);
            msg = CeoBriefBuilder.RenderFallback(data, todayVn);
        }

        int sent = 0;
        var summaries = new List<string>();
        foreach (var sub in due)
        {
            var chSummary = await _dispatcher.SendAsync(sub, msg, ct);
            await _subs.MarkSentAsync(tenantId, sub.Username, BriefTypes.Ceo, utcNow, todayVn, ct);
            sent++; summaries.Add($"{sub.Username}[{chSummary}]");
        }
        return new(true, $"Gửi {sent}/{due.Count} bản tin điều hành: {string.Join(", ", summaries)}", null);
    }

    /// financial-summary 2 kỳ (MTD này vs MTD tháng trước) + top-sellers + deal mới hôm qua + payment alerts mở.
    private async Task<CeoBriefData> FetchDataAsync(string jwt, DateTime todayVn, CancellationToken ct)
    {
        var mtdStart = new DateTime(todayVn.Year, todayVn.Month, 1);
        var prevStart = mtdStart.AddMonths(-1);
        var prevEnd = prevStart.AddDays(todayVn.Day - 1);

        async Task<CeoNumbers> Fin(DateTime s, DateTime e)
        {
            try
            {
                var d = await _api.GetAsync(jwt, $"/api/ai/financial-summary?StartDate={s:yyyy-MM-dd}&EndDate={e:yyyy-MM-dd}", ct);
                decimal rev = 0, exp = 0, prof = 0;
                if (d.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray())
                    {
                        var key = it.TryGetProperty("key", out var k) ? k.GetString() : null;
                        var val = it.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
                        // key theo envelope financial-summary (revenue/expense/profit — người thực hiện
                        // đối chiếu key thật bằng 1 lần gọi /api/ai/financial-summary trước khi hoàn thiện)
                        if (key is "revenue" or "totalRevenue") rev = val;
                        else if (key is "expense" or "totalExpense") exp = val;
                        else if (key is "profit" or "grossProfit") prof = val;
                    }
                if (prof == 0 && (rev != 0 || exp != 0)) prof = rev - exp;
                return new CeoNumbers(rev, exp, prof);
            }
            catch { return new CeoNumbers(0, 0, 0); }
        }

        var thisMtd = await Fin(mtdStart, todayVn);
        var prevMtd = await Fin(prevStart, prevEnd);

        var sellers = new List<string>();
        try
        {
            var ts = await _api.GetAsync(jwt, $"/api/ai/top-sellers?StartDate={mtdStart:yyyy-MM-dd}&EndDate={todayVn:yyyy-MM-dd}", ct);
            if (ts.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray().Take(3))
                {
                    var name = it.TryGetProperty("fullName", out var n) ? n.GetString() : null;
                    var revF = it.TryGetProperty("totalRevenueFormatted", out var r) ? r.GetString() : null;
                    if (name != null) sellers.Add($"{name} — {revF ?? "?"}");
                }
        }
        catch { /* n/a */ }

        int newDeals = 0;
        try
        {
            var yesterday = todayVn.AddDays(-1);
            var bt = await _api.GetAsync(jwt, $"/api/ai/booking-tickets?CreatedFrom={yesterday:yyyy-MM-dd}&CreatedTo={todayVn:yyyy-MM-dd}&PageSize=1", ct);
            if (bt.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number) newDeals = t.GetInt32();
        }
        catch { /* n/a */ }

        int openAlerts = 0;
        try
        {
            var list = await _insights.ListAsync(_currentTenant!, "", "payment-alert", unreadOnly: true, 0, 100, ct);
            openAlerts = list.Count;
        }
        catch { /* n/a */ }

        return new CeoBriefData(thisMtd, prevMtd, sellers, newDeals, openAlerts);
    }

    // Tenant hiện hành cho FetchDataAsync (set đầu RunAsync).
    private string? _currentTenant;
}
```

**Sửa nhỏ bắt buộc khi code:** `_currentTenant` phải set `_currentTenant = tenantId;` ở đầu `RunAsync` (trước `FetchDataAsync`) — hoặc gọn hơn: truyền `tenantId` thành tham số `FetchDataAsync(jwt, tenantId, todayVn, ct)` (khuyến nghị — bỏ field). Thêm const vào `TkPermissionCodes`:

```csharp
/// Cơ hội bán hàng — xem TOÀN BỘ (gate ceo-brief digest). PermissionCodes.cs:243 upstream.
public const string XemToanBoCoHoi = "CH_XEM_ALL";
```

- [ ] **Step 6:** Build + toàn bộ test pass. Chạy tay `/workflows` → "Chạy ngay" `ceo-brief` (tenant có service account + 1 sub SQL) → nhận bản tin.
- [ ] **Step 7: Commit** — `git commit -am "feat(digest): CeoBriefWorkflow (C1) — 1 AI call/tenant/ngày + fallback + AiFeatures.Digest + gate CH_XEM_ALL"`

---

### Task 10: Endpoints — insights + digest subscriptions — ✅ XONG 12/08/2026

> **[SỬA khi làm thật] BỎ HẲN cửa quyền `DigestGate`/`CH_XEM_ALL` mà plan yêu cầu.**
> Người dùng nhắc: "phần quyền API nó lo rồi, chỉ cần truyền tài khoản lên". Kiểm lại đúng vậy —
> `DashboardService.ResolveSpUserIdAsync` (TourKit.Api) chỉ truyền "xem tất cả" cho tài khoản có
> **`BC_NV_XEM`**, còn lại truyền chính user id nên SP tự lọc về số của riêng họ. Và proxy không hề
> truyền `userId`: `AiController.GetClaims()` bóc `userId`+`tenantId` từ JWT.
> Quan trọng hơn: quyền API thật sự kiểm là `BC_NV_XEM`, **không phải `CH_XEM_ALL`** như plan ghi —
> tự gác bằng `CH_XEM_ALL` sẽ **chặn oan** người có quyền báo cáo mà không có quyền xem mọi cơ hội,
> tức hỏng đúng việc nó định bảo vệ, lại thêm một chỗ phải đồng bộ tay với mã quyền upstream.
> Còn giữ gate `CH_HT_XEM` cho **cấu hình Zalo OA** — token cấp công ty do proxy tự giữ, TourKit
> không biết gì để lọc giúp.
>
> **Thêm ngoài plan:** validate lúc lưu (bật bản tin mà 0 kênh → 400; bật kênh mà trống nơi nhận →
> 400) — nói ngay lúc lưu còn hơn để người dùng chờ tới sáng mới biết không nhận được gì.
> `GET /zalo-config` KHÔNG trả access token về client kể cả cho người có quyền.
> Tách `Endpoints/SessionAuth.cs` dùng chung thay vì copy `RequireSession` lần thứ 5.
>
> **Lỗi bắt được nhờ E2E:** `GET /insights` ném 500 — `AgentInsights.Severity` là TINYINT nhưng
> record khai `int` → Dapper không dựng được đối tượng (đúng cái đã cắn ở `DigestSubscriptions`).
> Lỗi nằm im từ lúc tạo bảng vì workflow chỉ INSERT, chỉ nổ ở chỗ ĐỌC đầu tiên. Đã thêm DTO có setter
> + rà toàn bộ repo còn lại theo mọi cột TINYINT/SMALLINT: không còn chỗ nào cùng bẫy.
>
> E2E chính thức: `scripts/e2e/features-digest.ps1` (26 PASS) — tự sao lưu + khôi phục đăng ký thật.

**Files:**
- Create: `Endpoints/InsightEndpoints.cs`
- Create: `Endpoints/DigestEndpoints.cs`
- Modify: `Program.cs` (web) — thêm `app.MapInsightEndpoints(); app.MapDigestEndpoints();` cạnh các MapX sẵn có
- Test: `TourkitAiProxy.Tests/Digest/SubscriptionGateTests.cs`

**Interfaces:**
- Consumes: Task 3 repos, Task 4 store, `TkSessionStore.HasPermission/EnsurePermissionsAsync`, `TkPermissionCodes.XemToanBoCoHoi` (Task 9), `DigestDispatcher` + builders (test-send)
- Produces: API surface theo spec §7 (bảng endpoint)

- [ ] **Step 1: Test fail — gate logic thuần** (tách hàm `DigestGate.CanSubscribe(briefType, permissions)` để test không cần HTTP):

```csharp
// TourkitAiProxy.Tests/Digest/SubscriptionGateTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class SubscriptionGateTests
{
    [Fact] public void Sale_brief_ai_cung_dang_ky_duoc()
        => Assert.True(DigestGate.CanSubscribe(BriefTypes.Sale, new List<string>()));
    [Fact] public void Ceo_brief_can_CH_XEM_ALL()
        => Assert.False(DigestGate.CanSubscribe(BriefTypes.Ceo, new List<string> { "CV_TAOMOI" }));
    [Fact] public void Ceo_brief_co_quyen_thi_ok_case_insensitive()
        => Assert.True(DigestGate.CanSubscribe(BriefTypes.Ceo, new List<string> { "ch_xem_all" }));
    [Fact] public void Brief_type_la_thi_tu_choi()
        => Assert.False(DigestGate.CanSubscribe("hacker-brief", new List<string> { "CH_XEM_ALL" }));
}
```

- [ ] **Step 2: Implement `DigestGate` (trong `Services/Digest/DigestGate.cs`)**

```csharp
// Services/Digest/DigestGate.cs
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Services.Digest;

/// Gate đăng ký bản tin: sale-brief mở cho mọi user; ceo-brief đòi quyền xem toàn bộ (CH_XEM_ALL).
public static class DigestGate
{
    public static bool CanSubscribe(string briefType, IReadOnlyCollection<string> permissions)
    {
        if (!BriefTypes.IsValid(briefType)) return false;
        if (briefType == BriefTypes.Sale) return true;
        return permissions.Any(p => string.Equals(p, TkPermissionCodes.XemToanBoCoHoi, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 3: Test PASS (4).** — [ ] **Step 4: Implement endpoints** (pattern `RequireSession` như `VisaEndpoints` — local helper):

```csharp
// Endpoints/InsightEndpoints.cs
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Insight Feed (F1): GET list + unread-count + mark read. Require X-Session-Id.
public static class InsightEndpoints
{
    private static (string Sid, string Tenant, string User)? Auth(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                  ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = string.IsNullOrEmpty(sid) ? null : sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId, s.Username);
    }
    private static IResult Unauthorized() => Results.Json(new { error = "Cần đăng nhập (X-Session-Id)." }, statusCode: 401);

    public static void MapInsightEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/insights");

        g.MapGet("", async (HttpContext ctx, TkSessionStore sessions, InsightRepository repo,
            string? kind, bool? unread, int? offset, int? limit, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            var items = await repo.ListAsync(a.Value.Tenant, a.Value.User, kind,
                unread == true, offset ?? 0, limit ?? 30, ct);
            return Results.Json(new { items }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        });

        g.MapGet("/unread-count", async (HttpContext ctx, TkSessionStore sessions, InsightRepository repo, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            return Results.Json(new { count = await repo.UnreadCountAsync(a.Value.Tenant, a.Value.User, ct) });
        });

        g.MapPost("/{id:long}/read", async (long id, HttpContext ctx, TkSessionStore sessions, InsightRepository repo, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            await repo.MarkReadAsync(a.Value.Tenant, a.Value.User, id, ct);
            return Results.Json(new { ok = true });
        });

        g.MapPost("/read-all", async (HttpContext ctx, TkSessionStore sessions, InsightRepository repo, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            await repo.MarkAllReadAsync(a.Value.Tenant, a.Value.User, ct);
            return Results.Json(new { ok = true });
        });
    }
}
```

```csharp
// Endpoints/DigestEndpoints.cs
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// Đăng ký bản tin (F5) + test-send + telegram detect + zalo config. Require X-Session-Id.
public static class DigestEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static (string Sid, string Tenant, string User)? Auth(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault()
                  ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        var s = string.IsNullOrEmpty(sid) ? null : sessions.Get(sid);
        return s == null ? null : (sid!, s.TenantId, s.Username);
    }
    private static IResult Unauthorized() => Results.Json(new { error = "Cần đăng nhập (X-Session-Id)." }, statusCode: 401);

    /// Body PUT subscription (camelCase từ frontend).
    public record SubBody(bool Enabled, int SendHourLocal, bool ChannelInApp,
        bool ChannelEmail, string? Email, bool ChannelTelegram, string? TelegramChatId,
        bool ChannelZalo, string? ZaloUserId);

    public static void MapDigestEndpoints(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/digest");

        g.MapGet("/subscriptions", async (HttpContext ctx, TkSessionStore sessions, DigestSubscriptionRepository repo, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            var items = await repo.ListForUserAsync(a.Value.Tenant, a.Value.User, ct);
            // Kèm cờ quyền để UI disable card ceo-brief khi thiếu quyền.
            await sessions.EnsurePermissionsAsync(a.Value.Sid, ct);
            var canCeo = sessions.HasPermission(a.Value.Sid, TkPermissionCodes.XemToanBoCoHoi);
            return Results.Json(new { items, canCeoBrief = canCeo }, Web);
        });

        g.MapPut("/subscriptions/{briefType}", async (string briefType, HttpContext ctx, TkSessionStore sessions,
            DigestSubscriptionRepository repo, SubBody body, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            if (!BriefTypes.IsValid(briefType))
                return Results.BadRequest(new { error = "Loại bản tin không hợp lệ." });
            await sessions.EnsurePermissionsAsync(a.Value.Sid, ct);
            var perms = sessions.Get(a.Value.Sid)?.Permissions ?? new List<string>();
            if (!DigestGate.CanSubscribe(briefType, perms))
                return Results.Json(new { error = "Bạn cần quyền xem toàn bộ (CH_XEM_ALL) để nhận bản tin điều hành." }, statusCode: 403);

            await repo.UpsertAsync(new DigestSubscription(a.Value.Tenant, a.Value.User, briefType,
                body.Enabled, DigestSubscription.ClampHour(body.SendHourLocal),
                body.ChannelInApp, body.ChannelEmail, body.Email,
                body.ChannelTelegram, body.TelegramChatId, body.ChannelZalo, body.ZaloUserId,
                null, null), ct);
            return Results.Json(new { ok = true });
        });

        // Gửi thử NGAY qua pipeline thật (không đợi tới giờ; không đụng LastSent).
        g.MapPost("/subscriptions/{briefType}/test", async (string briefType, HttpContext ctx, TkSessionStore sessions,
            DigestSubscriptionRepository repo, DigestDispatcher dispatcher, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            var sub = (await repo.ListForUserAsync(a.Value.Tenant, a.Value.User, ct))
                .FirstOrDefault(s => s.BriefType == briefType);
            if (sub == null) return Results.BadRequest(new { error = "Chưa lưu đăng ký — bấm Lưu trước khi Gửi thử." });
            var today = DigestDue.NowVn(DateTime.UtcNow);
            var msg = new DigestMessage($"[Gửi thử] Bản tin {today:dd/MM HH:mm}",
                "Đây là bản tin THỬ để kiểm tra kênh nhận. Nếu bạn đọc được tin này, kênh hoạt động tốt ✅.",
                "<p>Đây là bản tin <b>THỬ</b> để kiểm tra kênh nhận ✅.</p>", briefType);
            var summary = await dispatcher.SendAsync(sub, msg, ct);
            return Results.Json(new { ok = true, summary });
        });

        // Tự phát hiện Telegram chat id: user nhắn mã ngắn cho bot → server quét getUpdates.
        g.MapPost("/telegram/detect", async (HttpContext ctx, TkSessionStore sessions,
            IHttpClientFactory http, IConfiguration cfg, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            var token = cfg["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token))
                return Results.Json(new { error = "Hệ thống chưa cấu hình bot Telegram." }, statusCode: 503);
            var code = "TK-" + a.Value.Sid[..6].ToUpperInvariant();   // mã ngắn hiện trên UI
            var client = http.CreateClient("telegram");
            var resp = await client.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates?limit=100", ct);
            using var doc = JsonDocument.Parse(resp);
            string? chatId = null;
            if (doc.RootElement.TryGetProperty("result", out var updates))
                foreach (var u in updates.EnumerateArray())
                    if (u.TryGetProperty("message", out var m)
                        && m.TryGetProperty("text", out var t) && (t.GetString() ?? "").Contains(code, StringComparison.OrdinalIgnoreCase)
                        && m.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idEl))
                        chatId = idEl.GetRawText();
            return chatId != null
                ? Results.Json(new { chatId, code })
                : Results.Json(new { chatId = (string?)null, code,
                    hint = $"Nhắn \"{code}\" cho bot rồi bấm lại nút này." });
        });

        // Zalo OA config per-tenant — gate quyền cấu hình hệ thống (cùng gate trang tích hợp).
        g.MapPut("/zalo-config", async (HttpContext ctx, TkSessionStore sessions,
            TenantChannelSettingsStore store, ZaloConfigBody body, CancellationToken ct) =>
        {
            var a = Auth(ctx, sessions); if (a == null) return Unauthorized();
            await sessions.EnsurePermissionsAsync(a.Value.Sid, ct);
            if (!sessions.HasPermission(a.Value.Sid, TkPermissionCodes.CauHinhHeThong))
                return Results.Json(new { error = "Cần quyền cấu hình hệ thống (CH_HT_XEM)." }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(body.OaId) || string.IsNullOrWhiteSpace(body.AccessToken))
                return Results.BadRequest(new { error = "Cần đủ OA Id + Access Token." });
            await store.SaveZaloConfigAsync(a.Value.Tenant, body.OaId.Trim(), body.AccessToken.Trim(), ct);
            return Results.Json(new { ok = true });
        });
    }

    public record ZaloConfigBody(string? OaId, string? AccessToken);
}
```

- [ ] **Step 5:** Build; map 2 endpoint group vào `Program.cs`; chạy `dotnet run` → smoke bằng curl:
  - `curl -H "X-Session-Id: <sid>" http://localhost:5080/api/v1/insights/unread-count` → `{count:N}`
  - PUT subscription sale-brief → `{ok:true}`; PUT ceo-brief với user thiếu quyền → 403 đúng thông điệp.
- [ ] **Step 6: Commit** — `git commit -am "feat(digest): endpoints insights + digest subscriptions (gate CH_XEM_ALL) + telegram detect + zalo config"`

---

### Task 11: Frontend — `/insights` + `/digest` + badge chuông

**Files:**
- Create: `wwwroot/pages/insights.jsx`
- Create: `wwwroot/pages/digest.jsx`
- Modify: `wwwroot/index.html` — thêm 2 `<script type="text/babel" src="pages/insights.jsx">` + `digest.jsx` sau các page hiện có
- Modify: `wwwroot/bundle-entry.js` — thêm `import "./pages/insights.jsx"; import "./pages/digest.jsx";` (**BẮT BUỘC** — thiếu = prod trắng trang)
- Modify: `wwwroot/app.jsx` — 2 `<Route>` + 2 `<Link>` (nav group "Tích hợp": "Bản tin AI" `/digest`; "Thông báo" `/insights` kèm badge chuông ở topbar)

**Nội dung chính (theo pattern trang sẵn có — dùng `window.tourkitAuth.authedFetch`, `window.tourkitUtil.fmtAgo`, KHÔNG copy-paste helper):**

- `insights.jsx`: list card (severity 2 = viền đỏ, 1 = cam, 0 = xám), filter dropdown Kind (Tất cả / Bản tin sáng / Bản tin điều hành / Cảnh báo thanh toán), toggle "Chỉ chưa đọc", body render markdown-lite (`**x**` → `<b>`), click card → POST `/read`, nút "Đọc tất cả". `window.InsightsPage = InsightsPage;`
- `digest.jsx`: 2 card đăng ký (`sale-brief` luôn hiện; `ceo-brief` disable + tooltip "Cần quyền xem toàn bộ" khi `canCeoBrief=false`); mỗi card: toggle Enabled, dropdown giờ gửi (5→20h), 4 kênh: In-app (checkbox), Email (checkbox + input), Telegram (checkbox + input chat id + nút "Tự phát hiện" gọi `/telegram/detect` hiện mã + hướng dẫn nhắn bot), Zalo (checkbox + input user id + ghi chú "chỉ nhận được khi bạn đã nhắn OA trong 48h"); nút "Lưu" (PUT) + "Gửi thử" (POST test → hiện summary per-channel). Khu "Cấu hình Zalo OA (toàn công ty)" chỉ hiện khi có quyền — form OA Id + Access Token → PUT `/zalo-config`. `window.DigestPage = DigestPage;`
- Badge chuông trong `app.jsx` topbar: poll `GET /api/v1/insights/unread-count` mỗi 60s (chỉ khi có session), hiện số đỏ, click → navigate `/insights`.

- [ ] **Step 1:** Viết 2 page + wire 3 chỗ + badge.
- [ ] **Step 2: Verify tay:** `dotnet run` → login `/assistant` lấy session → mở `/digest` đăng ký sale-brief (in-app) → "Gửi thử" → chuông nhảy badge → `/insights` thấy bản tin thử → click đánh dấu đọc → badge giảm.
- [ ] **Step 3:** `.\build-frontend.ps1` chạy OK (bundle prod không lỗi — bắt lỗi thiếu bundle-entry).
- [ ] **Step 4: Commit** — `git commit -am "feat(digest): trang /insights + /digest + badge chuông (wire đủ index.html + bundle-entry + app.jsx)"`

---

### Task 12: Docs + config + verify tổng

**Files:**
- Modify: `docs/database-schema.md` — thêm 3 bảng mới (#23 AgentInsights, #24 DigestSubscriptions, #25 TenantChannelSettings) + cột `TkSessions.CrmUserId` theo format bảng sẵn có
- Modify: `CLAUDE.md` — thêm các endpoint mới vào bảng API surface + đoạn ngắn mô tả feature "Bản tin AI (Đợt 1)" trỏ về spec
- Modify: `appsettings.example.json` — thêm `"Telegram": { "BotToken": "REPLACE_WITH_TELEGRAM_BOT_TOKEN" }`
- Thao tác tay (ops, ghi vào summary khi giao): tạo template `daily-brief` trong `/admin-trav-ai/mail-templates` (Subject `{{title}}`, Body dùng `{{bodyHtml}}`) — thiếu template worker vẫn fallback render từ Params

- [ ] **Step 1:** Cập nhật 3 file docs/config như trên.
- [ ] **Step 2: Verify tổng:** `dotnet build TourkitAiProxy.csproj` + `dotnet build TourkitAiProxy.Worker/` (nếu csproj worker tách — build cả solution nếu có) + `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → **0 fail**; `dotnet run` → log startup có `TourkitAiDb schema OK (…/AgentInsights/DigestSubscriptions/TenantChannelSettings…)`; `/workflows` hiện đủ 3 card mới.
- [ ] **Step 3: Commit** — `git commit -am "docs(digest): schema doc + API surface + Telegram config example (Đợt 1 hoàn tất)"`

---

## Self-review đã chạy (2026-08-11)

- **Spec coverage:** F1→Task 1/3/10/11 · F2→Task 4/6/8/9 · F5→Task 1/3/10/11 · S1+S5→Task 7/8 · C1→Task 9 · O2→Task 5/6 · kênh Zalo/Telegram/Email→Task 4/10/11 · gate quyền→Task 9/10 · docs→Task 12. Mục spec §5.1 dòng 3 (KH VIP lâu không chăm): Đợt 1 để danh sách rỗng có chủ đích (ghi comment trong Task 8) — mức thô hơn spec; chấp nhận, đã ghi chú ngay tại code.
- **Placeholder:** không còn TBD; 2 chỗ yêu cầu "đọc file thật trước khi code" (DealScores.DataJson shape — Task 8; key envelope financial-summary — Task 9) là bước verify dữ liệu BẮT BUỘC có hướng dẫn cụ thể, không phải placeholder.
- **Type consistency:** `DigestSubscription`/`DigestMessage`/`AgentInsight` (Task 1) khớp chữ ký dùng ở Task 3/4/8/9/10; `PaymentAlert` (Task 5) khớp `SaleBriefInput.MyPaymentAlerts` (Task 7); `TkPermissionCodes.XemToanBoCoHoi` khai ở Task 9, dùng ở Task 9/10; `AiFeatures.Digest` khai + dùng ở Task 9. Đã sửa: `CeoBriefWorkflow._currentTenant` → khuyến nghị truyền tham số (ghi chú ngay trong Task 9).
