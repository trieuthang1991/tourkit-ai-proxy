# Pipeline gửi bản tin v3 — hàng đợi đa kênh — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chuẩn hoá đường gửi bản tin: PREPARE trước giờ (dựng nội dung → `AgentInsights` → enqueue `OutboundMails` đa kênh với `ScheduledUtc`), gửi đúng giờ qua queue; `DigestSubscriptions` về thuần cấu hình; 1 enum kênh duy nhất; KHÔNG retry (hoãn — phương án riêng sau).

**Spec:** [2026-08-13-digest-queue-pipeline-design.md](../specs/2026-08-13-digest-queue-pipeline-design.md) · [Phân tích lưu trữ](../specs/2026-08-13-digest-db-storage-analysis.md) — ĐỌC CẢ 2 trước khi làm.

**Tech Stack:** ASP.NET Core 8, Dapper + SQL Server (`TourkitAiDb.SchemaSql` idempotent), xUnit (`TourkitAiProxy.Tests`), React no-build.

## Global Constraints
- Comment/log/string tiếng Việt. DateTime UTC (`SYSUTCDATETIME()`/`DateTime.UtcNow`); giờ VN qua `TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")` (đã có trong `DigestDue`).
- KHÔNG retry đợt này: gửi lỗi → `Status=2` + `ErrorMessage` + log ERROR, dòng nằm lại.
- KHÔNG migrate `dbo.UserWorkflows`. KHÔNG xoá cột DB nào (SentMask/... giữ trong DB, code ngừng dùng).
- Config mới đọc từ `Digest:*` (LeadMinutes=10, CheckIntervalMinutes=5, InsightKeepDays=30 default trong code — `cfg.GetValue`).
- DI đăng ký trong `WorkflowStackRegistration.AddWorkflowStack` (KHÔNG Program.cs), trừ AddHostedService (caller quyết).
- Interface sẵn có: `MailQueueRepository.EnqueueAsync(OutboundMailInput)`; `InsightRepository.InsertAsync(AgentInsight)`; `IScheduledWorkflow`; `DigestDue.NowVn(utc)`.
- ⚠️ Thứ tự DEPLOY (ghi vào README hợp đồng, Task 1): worker toutkit-app lọc `Channel=0` TRƯỚC, proxy enqueue kênh khác SAU.

---

### Task 1: Enum kênh duy nhất + cột `Channel` + queue repo hỗ trợ

**Files:**
- Create: `Services/Digest/OutboundChannel.cs`
- Modify: `Services/Db/TourkitAiDb.cs` (thêm block cuối `SchemaSql`, cập nhật log "schema OK")
- Modify: `Services/Mail/MailQueueRepository.cs` (`OutboundMailInput`, `OutboundMail`, `EnqueueAsync`, `ListForMonitorAsync`, `ListForAdminAsync`)
- Modify: `docs/database-schema.md` (cột mới của bảng OutboundMails), `docs/mail-templates/README.md` (hợp đồng worker)

- [ ] **Step 1: Tạo `Services/Digest/OutboundChannel.cs`:**
```csharp
namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Kênh gửi ra ngoài — enum kênh DUY NHẤT toàn hệ (thay DigestChannel/ChannelMask cũ đã gỡ).
/// SỐ tường minh, lưu thẳng cột dbo.OutboundMails.Channel (TINYINT): số để tránh lỗi gõ chuỗi,
/// default 0 = email nên dòng cũ trong DB tự đúng nghĩa. In-app KHÔNG nằm đây — nó là kho lưu
/// luôn-bật (AgentInsights), không phải kênh gửi.
/// Worker toutkit-app MIRROR đúng bảng số này (xem docs/mail-templates/README.md) —
/// thêm kênh mới = thêm 1 member ở CẢ 2 repo + 1 case trong OutboundChannelDrainer.
/// </summary>
public enum OutboundChannel : byte
{
    Email    = 0,
    Telegram = 1,
    Zalo     = 2,
}

/// Helper thuần quanh OutboundChannel (test được).
public static class OutboundChannels
{
    /// Các kênh NGOÀI người này đang bật VÀ đã khai đủ nơi nhận. Bật mà thiếu nơi nhận → bỏ,
    /// không thì enqueue ra dòng không bao giờ gửi được.
    public static List<OutboundChannel> EnabledOf(DigestSubscription s)
    {
        var list = new List<OutboundChannel>(3);
        if (s.ChannelEmail    && !string.IsNullOrWhiteSpace(s.Email))          list.Add(OutboundChannel.Email);
        if (s.ChannelTelegram && !string.IsNullOrWhiteSpace(s.TelegramChatId)) list.Add(OutboundChannel.Telegram);
        if (s.ChannelZalo     && !string.IsNullOrWhiteSpace(s.ZaloUserId))     list.Add(OutboundChannel.Zalo);
        return list;
    }

    /// Tên tiếng Việt cho log/summary.
    public static string Describe(OutboundChannel ch) => ch switch
    {
        OutboundChannel.Email => "email", OutboundChannel.Telegram => "telegram",
        OutboundChannel.Zalo => "zalo", _ => ((byte)ch).ToString(),
    };
}
```
- [ ] **Step 2: Schema** — trong `TourkitAiDb.cs`, append vào cuối `SchemaSql` (trước dấu đóng const, cùng pattern các block `IF NOT EXISTS sys.columns` sẵn có):
```sql
--
-- Kênh gửi cho hàng đợi đa kênh (0=email 1=telegram 2=zalo — enum OutboundChannel).
-- Default 0: mọi dòng cũ tự thành email, không cần migrate data.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.OutboundMails') AND name = 'Channel')
    ALTER TABLE dbo.OutboundMails ADD Channel TINYINT NOT NULL CONSTRAINT DF_OutboundMails_Channel DEFAULT 0;
```
- [ ] **Step 3: `MailQueueRepository.cs`** — (a) `OutboundMailInput` thêm param cuối `TourkitAiProxy.Services.Digest.OutboundChannel Channel = TourkitAiProxy.Services.Digest.OutboundChannel.Email` (thêm `using TourkitAiProxy.Services.Digest;` đầu file, dùng tên ngắn); (b) `OutboundMail` record thêm field `byte Channel` (đặt sau `Data`); (c) `EnqueueAsync` INSERT thêm cột `Channel` + value `@Channel` (param `Channel = (byte)m.Channel`); (d) `ListForMonitorAsync` + `ListForAdminAsync`: SELECT thêm `Channel`, thêm param `int? channel = null` với điều kiện `AND (@channel IS NULL OR Channel = @channel)`.
- [ ] **Step 4: Docs** — `docs/database-schema.md`: thêm cột Channel vào mô tả bảng OutboundMails. `docs/mail-templates/README.md`: sửa câu poll của worker thành `... WHERE Status=0 AND Channel=0 AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME()) ...`, thêm bảng enum `OutboundChannel {Email=0, Telegram=1, Zalo=2}` với ghi chú "worker MIRROR enum này; CHỈ xử lý Channel=0; deploy filter này TRƯỚC khi proxy enqueue kênh khác".
- [ ] **Step 5:** `dotnet build TourkitAiProxy.csproj` → 0 error. `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → 0 fail.
- [ ] **Step 6: Commit** — `feat(digest): enum OutboundChannel + cột Channel trên hàng đợi (0=email, dòng cũ tự đúng)`

---

### Task 1b: PK `DigestSubscriptions` → `(TenantId, Username)` — mỗi người 1 dòng (Q11)

**Files:** Modify `Services/Db/TourkitAiDb.cs` (migration idempotent) · `Services/Digest/DigestSubscriptionRepository.cs` · `Endpoints/DigestEndpoints.cs` · `Services/Workflows/SaleBriefWorkflow.cs`+`CeoBriefWorkflow.cs` (nếu Task 5 chưa chạy thì gộp lúc đó)

- [ ] **Step 1: Migration** — append vào `SchemaSql` (SAU block Channel của Task 1):
```sql
--
-- [Q11] Mỗi người đúng 1 dòng đăng ký: BriefType ra khỏi khoá chính. Đổi loại = UPDATE cột,
-- giờ + kênh đi theo người; luật "1 người 1 loại" thành bất biến cấu trúc.
IF EXISTS (
    SELECT 1 FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.object_id = OBJECT_ID('dbo.DigestSubscriptions') AND i.is_primary_key = 1
    GROUP BY i.index_id HAVING COUNT(*) = 3)
BEGIN
    -- Dedupe: giữ dòng ĐANG BẬT (ưu tiên), rồi dòng sửa gần nhất — không mất đăng ký đang chạy.
    ;WITH ranked AS (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY TenantId, Username
                 ORDER BY Enabled DESC, UpdatedUtc DESC) AS rn
        FROM dbo.DigestSubscriptions)
    DELETE FROM ranked WHERE rn > 1;
    ALTER TABLE dbo.DigestSubscriptions DROP CONSTRAINT PK_DigestSubscriptions;
    ALTER TABLE dbo.DigestSubscriptions ADD CONSTRAINT PK_DigestSubscriptions PRIMARY KEY (TenantId, Username);
END;
```
- [ ] **Step 2: Repo** — `DigestSubscriptionRepository`: `UpsertAsync` MERGE ON 2 cột `(TenantId, Username)`, UPDATE SET thêm `BriefType = @BriefType`; XOÁ `DeactivateOthersAsync` + `MarkSentAsync` (Task 5/7 không dùng nữa); `ListForUserAsync` giữ (giờ trả ≤1 dòng); `ListEnabledAsync(tenant, briefType)` giữ nguyên WHERE (BriefType giờ là cột lọc).
- [ ] **Step 3: Endpoint** — `PUT /digest/subscriptions/{briefType}`: nghĩa mới = "đặt loại của TÔI thành {briefType} + lưu cấu hình" (upsert 1 dòng, BriefType=route param); XOÁ lời gọi `DeactivateOthersAsync` + comment luật (thay bằng comment "1 dòng/người — cấu trúc tự bảo đảm 1 loại"). `GET /subscriptions` trả items như cũ (≤1 phần tử — frontend đọc theo briefType vẫn chạy).
- [ ] **Step 4: Frontend** — `digest.jsx`/`workflows.jsx`: `subOf(type)` giờ chỉ khớp khi `sub.briefType === type` — card loại kia tự hiện "chưa bật" sau khi đổi loại (hành vi cũ giữ nguyên nhờ reload). Kiểm + chỉnh hint 1-loại: "Đổi loại sẽ chuyển đăng ký của bạn sang loại này (giờ và kênh nhận giữ nguyên)."
- [ ] **Step 5:** Build + suite xanh + E2E mục luật-1-loại vẫn PASS (bật ceo → GET thấy sale không còn enabled — giờ vì CHÍNH dòng đó đổi type). **Commit** — `feat(digest): mỗi người 1 dòng đăng ký — BriefType ra khỏi PK, xoá DeactivateOthers (Q11)`

---

### Task 2: `DigestDue.ShouldPrepare` — so PHÚT + cửa sổ lead (TDD)

**Files:** Modify `Services/Digest/DigestDue.cs` · Rewrite `TourkitAiProxy.Tests/Digest/DigestDueTests.cs`

- [ ] **Step 1: Viết test MỚI (thay toàn bộ nội dung `DigestDueTests.cs`):**
```csharp
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestDueTests
{
    private static DigestSubscription Sub(int hour, bool enabled = true)
        => new("t", "u", BriefTypes.Sale, enabled, hour, true, false, null, false, null, false, null, null, null);

    // 07:00 VN = 00:00 UTC. Lead 10' → chuẩn bị được từ 06:50 VN = 23:50 UTC hôm trước.
    [Fact] public void Truoc_cua_so_lead_thi_chua_chuan_bi()
        => Assert.False(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 12, 23, 49, 0, DateTimeKind.Utc), 10));
    [Fact] public void Trong_cua_so_lead_thi_chuan_bi()
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 12, 23, 50, 0, DateTimeKind.Utc), 10));
    [Fact] public void Dung_gio_gui_van_chuan_bi_duoc()   // fallback dựng-tại-chỗ
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void Qua_gio_nhieu_tieng_van_chuan_bi()  // server sập rồi sống lại — KHÔNG mất bản tin
        => Assert.True(DigestDue.ShouldPrepare(Sub(7), new DateTime(2026, 8, 13, 8, 30, 0, DateTimeKind.Utc), 10));
    [Fact] public void Disabled_khong_chuan_bi()
        => Assert.False(DigestDue.ShouldPrepare(Sub(7, enabled: false), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void Gio_0h_cua_so_lead_lui_ve_hom_truoc_theo_VN()
    {
        // 00:00 VN ngày 13 = 17:00 UTC ngày 12; lead 10' → từ 16:50 UTC ngày 12.
        Assert.True(DigestDue.ShouldPrepare(Sub(0), new DateTime(2026, 8, 12, 16, 50, 0, DateTimeKind.Utc), 10));
        Assert.False(DigestDue.ShouldPrepare(Sub(0), new DateTime(2026, 8, 12, 16, 49, 0, DateTimeKind.Utc), 10));
    }
    [Fact] public void Gio_rac_kep_ve_7h()
        => Assert.True(DigestDue.ShouldPrepare(Sub(99), new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10));
    [Fact] public void ScheduledUtc_dung_moc_gio_VN()
    {
        // Giờ gửi 7h VN ngày 13 = 00:00 UTC ngày 13.
        var utcNow = new DateTime(2026, 8, 12, 23, 55, 0, DateTimeKind.Utc);   // 06:55 VN
        Assert.Equal(new DateTime(2026, 8, 13, 0, 0, 0), DigestDue.SendMomentUtc(Sub(7), utcNow));
        // Đã QUA giờ (dựng muộn) → gửi ngay: trả chính utcNow.
        var late = new DateTime(2026, 8, 13, 1, 30, 0, DateTimeKind.Utc);      // 08:30 VN
        Assert.Equal(late, DigestDue.SendMomentUtc(Sub(7), late));
    }
}
```
- [ ] **Step 2: Chạy fail** — filter `DigestDueTests` → FAIL (chưa có `ShouldPrepare`/`SendMomentUtc`).
- [ ] **Step 3: Implement** — trong `DigestDue.cs` GIỮ `NowVn`, XOÁ `IsDue` + `PendingFor` (caller sửa ở Task 4/5), thêm:
```csharp
    /// <summary>
    /// Đã tới lúc CHUẨN BỊ bản tin hôm nay chưa: từ mốc (giờ gửi − leadMinutes) trở đi, cho tới hết
    /// ngày VN. So theo PHÚT (bản cũ so Hour == làm MẤT bản tin nếu server sập trọn khung giờ).
    /// "Hôm nay đã chuẩn bị chưa" do caller kiểm (InsightRepository.ExistsTodayAsync) — hàm này thuần.
    /// </summary>
    public static bool ShouldPrepare(DigestSubscription sub, DateTime utcNow, int leadMinutes)
    {
        if (!sub.Enabled) return false;
        var vn = NowVn(utcNow);
        var sendAt = vn.Date.AddHours(DigestSubscription.ClampHour(sub.SendHourLocal));
        return vn >= sendAt.AddMinutes(-Math.Max(0, leadMinutes));
    }

    /// Mốc UTC để đặt ScheduledUtc: đúng giờ người chọn (giờ VN đổi ra UTC); đã QUA giờ (dựng muộn
    /// do sập/lỡ cửa sổ) → gửi ngay (trả utcNow). Trả Kind=Unspecified để ghi thẳng DATETIME2.
    public static DateTime SendMomentUtc(DigestSubscription sub, DateTime utcNow)
    {
        var vn = NowVn(utcNow);
        var sendAtVn = vn.Date.AddHours(DigestSubscription.ClampHour(sub.SendHourLocal));
        if (vn >= sendAtVn) return DateTime.SpecifyKind(utcNow, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(sendAtVn, DateTimeKind.Unspecified), VnTz);
    }
```
(`VnTz` là field private sẵn có trong file.) Lưu ý test `ScheduledUtc_dung_moc_gio_VN` so `DateTime` không Kind — nếu Equal fail vì Kind, so bằng `Assert.Equal(expected, actual)` trên giá trị (DateTime.Equals bỏ qua Kind — đúng như viết).
- [ ] **Step 4:** Chạy pass toàn bộ `DigestDueTests`. Build có thể VỠ ở caller của `PendingFor` (workflows) — nếu vỡ, tạm giữ `PendingFor` lại (đánh dấu `[Obsolete]`) và xoá hẳn ở Task 5; test vẫn phải xanh.
- [ ] **Step 5: Commit** — `feat(digest): DigestDue.ShouldPrepare + SendMomentUtc — so phút, vá mất bản tin khi sập đúng khung giờ`

---

### Task 3: `InsightRepository` — `ExistsTodayAsync` + `GetAsync` (đọc nội dung cho drainer)

**Files:** Modify `Services/Digest/InsightRepository.cs`

- [ ] **Step 1:** Thêm 2 method (sau `ListAsync`):
```csharp
    /// Hôm nay (theo ngày VN, đổi sang khoảng UTC) đã có bản tin loại này cho người này chưa —
    /// chốt chống dựng/gửi trùng của pipeline queue (thay LastSentLocalDate cũ).
    public async Task<bool> ExistsTodayAsync(string tenant, string username, string kind,
        DateTime todayVn, CancellationToken ct = default)
    {
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(todayVn.Date, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.AgentInsights
WHERE TenantId = @tenant AND Username = @username AND Kind = @kind
  AND CreatedUtc >= @fromUtc AND CreatedUtc < DATEADD(DAY, 1, @fromUtc)",
            new { tenant, username, kind, fromUtc }) > 0;
    }

    /// Đọc 1 dòng theo Id (kẹp tenant) — drainer lấy nội dung gửi telegram/zalo qua SourceId.
    public async Task<AgentInsight?> GetAsync(string tenant, long id, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<InsightRow>(@"
SELECT Id, TenantId, Username, Kind, Severity, Title, Body, DataJson, AlertKey, IsRead, CreatedUtc
FROM dbo.AgentInsights WHERE Id = @id AND TenantId = @tenant", new { id, tenant });
        return row?.ToModel();
    }
```
- [ ] **Step 2:** Build 0 error, test cũ xanh. **Commit** — `feat(digest): InsightRepository.ExistsTodayAsync + GetAsync (nền pipeline queue)`

---

### Task 4: `DigestEnqueuePlanner` — dựng dòng queue từ đăng ký (thuần, TDD)

**Files:** Create `Services/Digest/DigestEnqueuePlanner.cs` · Test `TourkitAiProxy.Tests/Digest/DigestEnqueuePlannerTests.cs`

- [ ] **Step 1: Test fail:**
```csharp
using System.Text.Json;
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class DigestEnqueuePlannerTests
{
    private static DigestSubscription Sub(bool email = false, bool tele = false, bool zalo = false)
        => new("t", "u", BriefTypes.Sale, true, 7, true,
               email, email ? "a@b.vn" : null, tele, tele ? "123" : null, zalo, zalo ? "z9" : null, null, null);
    private static readonly DigestMessage Msg = new("Tiêu đề", "body md", "<p>html</p>", BriefTypes.Sale);
    private static readonly DateTime Sched = new(2026, 8, 13, 0, 0, 0);

    [Fact] public void Moi_kenh_ngoai_dang_bat_ra_1_dong()
        => Assert.Equal(3, DigestEnqueuePlanner.BuildRows(Sub(true, true, true), 42, Msg, Sched, "13/08/2026").Count);
    [Fact] public void Khong_kenh_ngoai_thi_khong_dong_nao()   // chỉ in-app → archive đủ, queue rỗng
        => Assert.Empty(DigestEnqueuePlanner.BuildRows(Sub(), 42, Msg, Sched, "13/08/2026"));
    [Fact] public void Bat_kenh_ma_trong_noi_nhan_thi_bo_kenh_do()
    {
        var s = new DigestSubscription("t", "u", BriefTypes.Sale, true, 7, true,
            true, "  ", true, "123", false, null, null, null);  // email bật nhưng địa chỉ trống
        var rows = DigestEnqueuePlanner.BuildRows(s, 42, Msg, Sched, "13/08/2026");
        Assert.Single(rows);
        Assert.Equal(OutboundChannel.Telegram, rows[0].Channel);
    }
    [Fact] public void Dong_email_giu_nguyen_hop_dong_worker()
    {
        var r = DigestEnqueuePlanner.BuildRows(Sub(email: true), 42, Msg, Sched, "13/08/2026").Single();
        Assert.Equal(OutboundChannel.Email, r.Channel);
        Assert.Equal("daily-brief", r.Kind);  Assert.Equal("daily-brief", r.TemplateCode);
        Assert.Equal("a@b.vn", r.ToEmail);    Assert.Equal("Tiêu đề", r.Subject);
        Assert.Equal("42", r.SourceId);       Assert.Equal(Sched, r.ScheduledUtc);
        var p = JsonDocument.Parse(r.Params!).RootElement;
        Assert.Equal("<p>html</p>", p.GetProperty("bodyHtml").GetString());
        Assert.Equal("13/08/2026", p.GetProperty("date").GetString());
    }
    [Fact] public void Dong_telegram_zalo_nhe_khong_mang_body()
    {
        var rows = DigestEnqueuePlanner.BuildRows(Sub(tele: true, zalo: true), 42, Msg, Sched, "13/08/2026");
        var tg = rows.Single(r => r.Channel == OutboundChannel.Telegram);
        var za = rows.Single(r => r.Channel == OutboundChannel.Zalo);
        Assert.Null(tg.Params);  Assert.Null(za.Params);
        Assert.Equal("123", JsonDocument.Parse(tg.Data!).RootElement.GetProperty("chatId").GetString());
        Assert.Equal("z9", JsonDocument.Parse(za.Data!).RootElement.GetProperty("zaloUserId").GetString());
    }
}
```
- [ ] **Step 2:** Chạy fail (chưa có type).
- [ ] **Step 3: Implement `Services/Digest/DigestEnqueuePlanner.cs`:**
```csharp
using System.Text.Json;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Dựng danh sách dòng hàng đợi cho 1 bản tin đã chuẩn bị xong: mỗi kênh NGOÀI đang bật = 1 dòng,
/// ScheduledUtc = giờ người chọn, SourceId = Id bản tin trong AgentInsights. THUẦN → test được.
/// Email mang Params (hợp đồng worker toutkit-app giữ NGUYÊN); telegram/zalo chỉ mang nơi nhận
/// trong Data — nội dung drainer đọc lại từ AgentInsights qua SourceId (1 nguồn).
/// </summary>
public static class DigestEnqueuePlanner
{
    public const string Kind = "daily-brief";

    public static List<OutboundMailInput> BuildRows(DigestSubscription sub, long insightId,
        DigestMessage m, DateTime scheduledUtc, string dateVn)
    {
        var rows = new List<OutboundMailInput>(3);
        foreach (var ch in OutboundChannels.EnabledOf(sub))
        {
            rows.Add(ch switch
            {
                OutboundChannel.Email => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    TemplateCode: "daily-brief", ToEmail: sub.Email!.Trim(), Subject: m.Title,
                    Params: JsonSerializer.Serialize(new { title = m.Title, bodyHtml = m.BodyHtml, briefType = m.Kind, date = dateVn }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Email),
                OutboundChannel.Telegram => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title, Data: JsonSerializer.Serialize(new { chatId = sub.TelegramChatId!.Trim() }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Telegram),
                _ => new OutboundMailInput(
                    sub.TenantId, Kind, SourceId: insightId.ToString(), Username: sub.Username,
                    Subject: m.Title, Data: JsonSerializer.Serialize(new { zaloUserId = sub.ZaloUserId!.Trim() }),
                    ScheduledUtc: scheduledUtc, Channel: OutboundChannel.Zalo),
            });
        }
        return rows;
    }
}
```
- [ ] **Step 4:** Chạy pass (planner + toàn suite). **Commit** — `feat(digest): DigestEnqueuePlanner — dựng dòng queue theo kênh đang bật (TDD)`

---

### Task 5: Rework 2 workflow → PREPARE-only

**Files:** Modify `Services/Workflows/SaleBriefWorkflow.cs`, `Services/Workflows/CeoBriefWorkflow.cs` (đọc kỹ cả 2 trước khi sửa)

- [ ] **Step 1: `SaleBriefWorkflow.RunAsync`** — thay thân vòng chính. Cấu trúc mới (ctor thêm `MailQueueRepository queue`, `InsightRepository insights`, `IConfiguration cfg` — sale hiện CHƯA inject 2 cái sau; giữ các dependency cũ trừ `DigestDispatcher` nếu không còn dùng):
```csharp
        var lead = _cfg.GetValue("Digest:LeadMinutes", 10);
        var subs = await _subs.ListEnabledAsync(tenantId, BriefTypes.Sale, ct);
        var due = new List<DigestSubscription>();
        foreach (var s in subs)
            if (DigestDue.ShouldPrepare(s, utcNow, lead)
                && !await _insights.ExistsTodayAsync(tenantId, s.Username, BriefTypes.Sale, todayVn, ct))
                due.Add(s);
        if (due.Count == 0) return new(true, "Chưa tới giờ chuẩn bị của ai (0 đăng ký đến hạn).", null);
```
Trong vòng `foreach (var sub in due)`: giữ nguyên phần lấy session/jwt/`BuildInputAsync`/`SaleBriefBuilder.Build` → sau đó THAY khối dispatcher+MarkSent bằng:
```csharp
                // NỘI DUNG + KHO LƯU: bản tin ghi vào Bảng tin (in-app luôn-bật — kho lưu để
                // xem/nghe lại), Id của dòng này là nguồn nội dung cho các kênh ngoài.
                var insightId = await _insights.InsertAsync(new AgentInsight(
                    0, tenantId, sub.Username, BriefTypes.Sale, 0,
                    msg.Title, msg.BodyMarkdown, null, null, false, DateTime.UtcNow), ct);
                if (insightId == null) { skipped++; continue; }   // đã có (đua giữa 2 tick) → thôi

                // Enqueue kênh ngoài — gửi do queue lo đúng giờ, workflow KHÔNG gửi gì.
                var schedUtc = DigestDue.SendMomentUtc(sub, utcNow);
                var rows = DigestEnqueuePlanner.BuildRows(sub, insightId.Value, msg, schedUtc,
                    todayVn.ToString("dd/MM/yyyy"));
                foreach (var r in rows) await _queue.EnqueueAsync(r, ct);
                prepared++;
                parts.Add($"{sub.Username}[inapp+{rows.Count} kênh queue]");
```
Xoá `MarkSentAsync`, xoá mọi tham chiếu `ChannelMask`/`pending`. Counter đổi tên cho đúng nghĩa (`prepared`/`skipped`), summary: `"{due.Count} đăng ký đến hạn → chuẩn bị {prepared}, bỏ qua {noSession} (chưa đăng nhập), lỗi {failed}"`.
- [ ] **Step 2: `CeoBriefWorkflow.RunAsync`** — cùng pattern (ctor thêm `MailQueueRepository`, `IConfiguration`; đã có `_insights`). Giữ nguyên `FetchDataAsync` + `Fingerprint` + `ComposeAsync` (dedup AI theo bộ số), thay khối dispatcher+MarkSent y như Step 1 (Kind = `BriefTypes.Ceo`).
- [ ] **Step 3: Prune theo config** — cuối `RunAsync` của CẢ 2 workflow (trước return, best-effort try/catch): `await _insights.PruneAsync(_cfg.GetValue("Digest:InsightKeepDays", 30), ct);`
- [ ] **Step 4:** Xoá `[Obsolete] PendingFor` còn treo từ Task 2 (nếu có). Build 0 error. Toàn suite xanh (test builder không đổi).
- [ ] **Step 5: Commit** — `feat(digest): workflow bản tin chỉ còn PREPARE — dựng trước giờ, gửi giao cho queue`

---

### Task 6: `OutboundChannelDrainer` — gửi telegram/zalo từ queue

**Files:**
- Create: `Services/Digest/OutboundChannelDrainer.cs`
- Modify: `Services/Mail/MailQueueRepository.cs` (2 method mới), `Services/Digest/Channels/TelegramChannel.cs` + `ZaloOaChannel.cs` (tách method gửi lõi), `Services/Bootstrap/WorkflowStackRegistration.cs` (DI), `Program.cs` web + `TourkitAiProxy.Worker/Program.cs` (AddHostedService cùng chỗ/điều kiện với `WorkflowSchedulerService` — grep tên đó trong 2 Program để tìm đúng chỗ)

- [ ] **Step 1: Repo — 2 method** (thêm vào `MailQueueRepository`):
```csharp
    /// Dòng kênh NGOÀI-EMAIL đến hạn (drainer proxy xử lý; email là của worker toutkit-app).
    public async Task<List<OutboundMail>> ListDueNonEmailAsync(int take, CancellationToken ct = default)
    {
        if (take < 1) take = 1; if (take > 200) take = 200;
        await using var c = await _db.OpenAsync(ct);
        var rows = await c.QueryAsync<OutboundMail>(@"
SELECT TOP (@take) Id, TenantId, Kind, SourceId, Username, TemplateCode, ToEmail, ToName, ToUserId, Cc,
    Subject, [Params] AS [Params], Data, Channel, [Status], RetryCount, ErrorMessage, ScheduledUtc, CreatedUtc, ProcessedUtc
FROM dbo.OutboundMails
WHERE [Status] = 0 AND Channel <> 0
  AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME())
ORDER BY Id", new { take });
        return rows.AsList();
    }

    /// Ghi kết quả 1 lượt gửi của drainer. Điều kiện Status=0 chống 2 tiến trình cùng xử 1 dòng
    /// (dòng đã bị bên kia chốt thì UPDATE này không trúng — trả false, caller bỏ qua).
    public async Task<bool> MarkProcessedAsync(long id, bool ok, string? error, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var n = await c.ExecuteAsync(@"
UPDATE dbo.OutboundMails SET
    [Status] = @st, ErrorMessage = @error, RetryCount = RetryCount + CASE WHEN @st = 2 THEN 1 ELSE 0 END,
    ProcessedUtc = SYSUTCDATETIME()
WHERE Id = @id AND [Status] = 0",
            new { id, st = ok ? 1 : 2, error = error is { Length: > 1000 } ? error[..1000] : error });
        return n > 0;
    }
```
- [ ] **Step 2: Tách method gửi lõi trên 2 kênh** (KHÔNG đổi hành vi `IDigestChannel` cũ — dispatcher Gửi thử vẫn dùng): trong `TelegramChannel` tách phần thân `SendAsync` thành `public Task<bool> SendToChatAsync(string chatId, string title, string bodyMd, string tenantId, string username, CancellationToken ct)` (body = code hiện tại, thay `sub.TelegramChatId`→`chatId`, `m.Title/m.BodyMarkdown`→`title/bodyMd`, log giữ tenant/user); `SendAsync(sub, m, ct)` gọi lại nó. Tương tự `ZaloOaChannel` tách `public Task<bool> SendToUserAsync(string tenantId, string zaloUserId, string title, string bodyMd, string username, CancellationToken ct)`.
- [ ] **Step 3: Drainer `Services/Digest/OutboundChannelDrainer.cs`:**
```csharp
using System.Text.Json;
using TourkitAiProxy.Services.Digest.Channels;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Gửi các dòng hàng đợi kênh NGOÀI-EMAIL (telegram/zalo) đến hạn. Email là việc của worker
/// toutkit-app (poll Channel=0) — hai bên không giẫm nhau nhờ cột Channel.
/// ĐỢT NÀY KHÔNG RETRY (quyết định 13/08): lỗi → Status=2 + ErrorMessage + log ERROR, dòng nằm
/// lại để theo dõi; phương án retry thiết kế riêng sau (chỉ thêm chính sách lật 2→0 ở đây).
/// Nội dung đọc từ AgentInsights qua SourceId (1 nguồn); token OA/bot resolve LÚC GỬI.
/// </summary>
public class OutboundChannelDrainer : BackgroundService
{
    private readonly MailQueueRepository _queue;
    private readonly InsightRepository _insights;
    private readonly TelegramChannel _telegram;
    private readonly ZaloOaChannel _zalo;
    private readonly ILogger<OutboundChannelDrainer> _log;

    public OutboundChannelDrainer(MailQueueRepository queue, InsightRepository insights,
        TelegramChannel telegram, ZaloOaChannel zalo, ILogger<OutboundChannelDrainer> log)
    { _queue = queue; _insights = insights; _telegram = telegram; _zalo = zalo; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken st)
    {
        _log.LogInformation("[digest/drainer] khởi động — tick 60s, kênh ngoài-email");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(st).ConfigureAwait(false))
        {
            try { await DrainOnceAsync(st); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[digest/drainer] tick lỗi (không thoát vòng lặp)"); }
        }
    }

    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        var due = await _queue.ListDueNonEmailAsync(100, ct);
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            bool ok = false; string? err = null;
            try
            {
                var (title, body) = await ResolveContentAsync(row, ct);
                ok = (OutboundChannel)row.Channel switch
                {
                    OutboundChannel.Telegram => await _telegram.SendToChatAsync(
                        Addr(row.Data, "chatId"), title, body, row.TenantId, row.Username ?? "", ct),
                    OutboundChannel.Zalo => await _zalo.SendToUserAsync(
                        row.TenantId, Addr(row.Data, "zaloUserId"), title, body, row.Username ?? "", ct),
                    _ => false,   // kênh lạ (enum 2 bên lệch?) → Failed cho lộ ra, không nuốt
                };
                if (!ok) err = "kênh trả false — xem log Warning cùng thời điểm";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { err = ex.Message; }

            await _queue.MarkProcessedAsync(row.Id, ok, err, ct);
            if (!ok)
                _log.LogError("[digest/drainer] GỬI HỎNG queueId={Id} kênh={Ch} tenant={T} user={U}: {Err}",
                    row.Id, ((OutboundChannel)row.Channel), row.TenantId, row.Username, err);
        }
    }

    /// Nội dung: ưu tiên AgentInsights qua SourceId (1 nguồn); thiếu thì rơi về Subject của dòng.
    private async Task<(string Title, string Body)> ResolveContentAsync(OutboundMail row, CancellationToken ct)
    {
        if (long.TryParse(row.SourceId, out var insightId))
        {
            var ins = await _insights.GetAsync(row.TenantId, insightId, ct);
            if (ins != null) return (ins.Title, ins.Body);
        }
        return (row.Subject ?? "(bản tin)", "");
    }

    private static string Addr(string? dataJson, string prop)
    {
        using var doc = JsonDocument.Parse(dataJson ?? "{}");
        return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";
    }
}
```
- [ ] **Step 4: DI** — `WorkflowStackRegistration`: đổi 2 dòng đăng ký kênh thành đăng ký lớp cụ thể + forward interface (drainer cần inject lớp cụ thể):
```csharp
        s.AddSingleton<Digest.Channels.TelegramChannel>();
        s.AddSingleton<Digest.Channels.IDigestChannel>(sp => sp.GetRequiredService<Digest.Channels.TelegramChannel>());
        s.AddSingleton<Digest.Channels.ZaloOaChannel>();
        s.AddSingleton<Digest.Channels.IDigestChannel>(sp => sp.GetRequiredService<Digest.Channels.ZaloOaChannel>());
        s.AddSingleton<Digest.OutboundChannelDrainer>();
```
Trong `Program.cs` (web) + `TourkitAiProxy.Worker/Program.cs`: NGAY CẠNH chỗ `AddHostedService` của `WorkflowSchedulerService` (cùng điều kiện `Workflows:RunScheduler`), thêm `AddHostedService(sp => sp.GetRequiredService<TourkitAiProxy.Services.Digest.OutboundChannelDrainer>());` — drainer sống cùng chỗ với scheduler (thường worker).
- [ ] **Step 5:** Build web + worker 0 error, suite xanh. **Commit** — `feat(digest): OutboundChannelDrainer — gửi telegram/zalo từ queue đúng giờ (không retry, lỗi nằm lại)`

---

### Task 7: Gỡ ChannelMask/DigestChannel + ripple backend

**Files:** Delete `Services/Digest/ChannelMask.cs`, `TourkitAiProxy.Tests/Digest/ChannelMaskTests.cs`, `Services/Digest/Channels/InAppChannel.cs` · Modify `Services/Digest/DigestDispatcher.cs`, `Endpoints/DigestEndpoints.cs`, `Services/Admin/AdminDigestRepository.cs`, `TourkitAiProxy.Tests/Digest/DigestDispatcherTests.cs`, `TourkitAiProxy.Tests/Digest/DigestDueTests.cs` (nếu còn ref)

- [ ] **Step 1: `DigestDispatcher`** (chỉ còn phục vụ Gửi thử): `SendResult` đổi thành `readonly record struct SendResult(string Summary, List<string> SentChannels)`; bỏ tham số `onlyMask` + mọi `ChannelMask.*`; vòng gửi giữ nguyên try-riêng-từng-kênh, `if (ok) sent.Add(ch.Id)`.
- [ ] **Step 2: `DigestEndpoints`** — handler test-send: `ok = res.SentChannels.Count > 0`, `sentChannels = string.Join("+", res.SentChannels)`. Handler PUT: XOÁ khối validate "bật mà 0 kênh → 400" (in-app luôn có — ghi comment vì sao bỏ); GIỮ validate kênh-bật-thiếu-nơi-nhận. In-app: server ép `ChannelInApp = true` khi upsert (`body.ChannelInApp` bỏ qua) + comment "in-app là kho lưu luôn-bật (xem/nghe lại), không phải kênh tắt được".
- [ ] **Step 3: Gửi thử vẫn ghi Bảng tin** — `InAppChannel` xoá; trong handler test-send, TRƯỚC `dispatcher.SendAsync`, ghi thẳng insight `[Gửi thử]` qua `InsightRepository.InsertAsync` (giữ hành vi E2E "thấy bản tin thử trong Bảng tin"). Dispatcher giờ chỉ chứa email/telegram/zalo.
- [ ] **Step 4: `AdminDigestRepository`** — đọc file, thay phần đọc `SentMask/SentAttempts/LastSentLocalDate` bằng JOIN queue hôm nay:
```sql
-- per (TenantId, Username): trạng thái giao HÔM NAY từ hàng đợi
SELECT TenantId, Username, Channel, [Status], COUNT(*) AS Cnt
FROM dbo.OutboundMails
WHERE Kind = 'daily-brief' AND CreatedUtc >= @fromUtc   -- @fromUtc = 00:00 VN hôm nay đổi UTC
GROUP BY TenantId, Username, Channel, [Status]
```
Map ra: `channelsSentToday` (Status=1), `channelsFailed` (Status=2), `channelsPending` (Status=0); `sentAttempts` bỏ (không còn khái niệm lượt). `DetectProblem` giữ thứ tự nguyên nhân gốc: (1) chưa bật lịch chạy; (2) bật kênh mà trống nơi nhận; (3) hôm nay có dòng Status=2 → "kênh gửi hỏng: {tên kênh}". Response shape đổi → sửa cả `Endpoints/AdminUiEndpoints.cs` mapping + `wwwroot/pages/admin.jsx` DigestPage đọc field mới (đọc file, đổi tên cột hiển thị tương ứng — "Đã gửi/Hỏng/Chờ" thay cột mask).
- [ ] **Step 5: Endpoint theo dõi** — `Endpoints/WorkflowEndpoints.cs` handler `GET /workflows/outbound-mails`: thêm query `int? channel`, truyền vào `ListForMonitorAsync`, response mỗi item thêm `channel` (số). (Đọc handler trước — nó map thủ công từng field.)
- [ ] **Step 6:** Build web+worker 0 error; sửa/xoá test còn đỏ (`DigestDispatcherTests` viết lại theo `SentChannels`; `ChannelMaskTests` xoá). Toàn suite 0 fail. **Commit** — `refactor(digest): gỡ ChannelMask/DigestChannel/InAppChannel — 1 enum kênh, in-app luôn-bật, admin đọc từ queue`

---

### Task 8: Frontend digest.jsx — in-app khoá luôn-bật

**Files:** Modify `wwwroot/pages/digest.jsx`

- [ ] **Step 1:** Trong `DigestSubBlock`: (a) checkbox "Trong app" → `checked disabled` + note đổi thành "Luôn bật — bản tin luôn được lưu ở tab Bảng tin để xem/nghe lại"; (b) `EMPTY_SUB.channelInApp` giữ `true`; (c) hàm `problem` bỏ nhánh "0 kênh" (in-app luôn có); (d) `digestSummary` giữ nguyên (in-app luôn hiện "trong app").
- [ ] **Step 2:** Verify tay dev-mode (đọc lại JSX cân bằng). **Commit** — `feat(digest): UI khoá kênh trong-app luôn-bật (kho lưu xem/nghe lại)`

---

### Task 9: E2E + docs + CHANGELOG + verify tổng

**Files:** Modify `scripts/e2e/features-digest.ps1`, `CLAUDE.md`, `CHANGELOG.md`, `appsettings.example.json`

- [ ] **Step 1: E2E** — sửa `features-digest.ps1`: (a) assertion `Bat ban tin ma 0 kenh = 400` → đổi thành `Bat ban tin khong kenh ngoai = 200 (in-app luon co)` (expect 200); (b) GIỮ: loại lạ 400, email trống 400, luật 1-loại, gửi thử, speakText; (c) thêm assertion sau gửi thử: `GET /workflows/outbound-mails?kind=daily-brief` trả 200 (nếu sub có kênh ngoài thì thấy dòng queue; chỉ in-app thì items có thể rỗng — assert mềm theo code, không theo chuỗi TV). Parse-check PowerShell 5.1 như lần trước.
- [ ] **Step 2: `appsettings.example.json`** — thêm mục `"Digest": { "LeadMinutes": 10, "CheckIntervalMinutes": 5, "InsightKeepDays": 30 }` kèm comment-key kiểu file này đang dùng.
- [ ] **Step 3: `CLAUDE.md`** — section "Bản tin AI": cập nhật đoạn cơ chế gửi (PREPARE → AgentInsights + queue → worker email/drainer; KHÔNG retry đợt này; in-app luôn-bật; enum `OutboundChannel {0,1,2}`; thứ tự deploy worker trước). Bảng API: dòng `GET /workflows/outbound-mails` thêm `channel`.
- [ ] **Step 4: `CHANGELOG.md`** — mục mới cho NGƯỜI DÙNG (theo rule trong file): "Bản tin đến đúng giờ hơn (hệ chuẩn bị sẵn từ trước, đến giờ chỉ việc gửi)", "Bản tin luôn được lưu trong app để xem/nghe lại — kể cả khi bạn chỉ nhận qua Zalo/Telegram", "Trước đây server bận/khởi động lại đúng giờ gửi có thể mất bản tin của ngày — nay gửi bù ngay khi hệ hoạt động lại".
- [ ] **Step 5: Verify tổng** — build web + worker 0 error; `dotnet test` 0 fail; `dotnet run` → log schema OK có Channel; E2E digest chạy với session thật (mượn cách lấy session của lần trước) → 0 FAIL; kiểm tay: bật telegram cho 1 đăng ký, đặt giờ = giờ hiện tại + 12 phút, chạy run-now → thấy dòng queue Pending với ScheduledUtc đúng; đợi qua giờ → drainer gửi (hoặc lỗi thì Status=2 + ERROR log).
- [ ] **Step 6: Commit** — `test+docs(digest): E2E theo pipeline queue + config mẫu + CHANGELOG người dùng`

---

## Self-review
- **Spec coverage:** Q1→T1/T6 · Q2→T3/T5 · Q3→T5/T7 (ngừng MarkSent/mask) · Q4→không đụng UserWorkflows · Q5→T6 (không retry, MarkProcessed giữ RetryCount cho sau) · Q6→T5 (insert insight luôn) + T7 (ép ChannelInApp) + T8 · Q7→log ERROR T6 · Q8→config đọc `Digest:*` T5/T9 · Q9→`ShouldPrepare` cả-ngày + `SendMomentUtc=now` khi muộn (T2/T5) · Q10→lead 10' config. Ripple bảng spec §4: PUT (T7), digest.jsx (T8), Admin (T7), outbound-mails filter (T7), E2E (T9), DigestDueTests (T2), ChannelMask gỡ (T7), test-send (T7 Step 3), DigestAlert.cs đã xoá trước đó.
- **Type consistency:** `OutboundMailInput.Channel` (T1) ↔ planner (T4) ↔ Enqueue SQL (T1); `SendMomentUtc` trả DateTime unspecified ↔ ScheduledUtc DATETIME2; `InsertAsync` trả `long?` (null = dedup AlertKey — với AlertKey=null không xảy ra, nhưng vẫn guard skipped++); `OutboundMail.Channel` là `byte` ↔ cast `(OutboundChannel)row.Channel` (T6).
- **Điểm cần đọc-file-thật khi làm** (đã ghi trong task): tail `CeoBriefWorkflow`, `AdminDigestRepository`, handler outbound-mails, `admin.jsx` DigestPage, 2 `Program.cs` — mỗi chỗ đều có mô tả đích + code/SQL đích, không TBD.
