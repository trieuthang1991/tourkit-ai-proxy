# Database Schema — tourkit-ai-proxy

> **1 nguồn cho mọi bảng SQL Server mà proxy đang dùng**. Khi thêm bảng mới hoặc đổi schema → cập nhật file này song song với [Services/Db/TourkitAiDb.cs](../Services/Db/TourkitAiDb.cs).

## Tổng quan

- **Instance**: dùng chung với hệ TourKit Push (`PushLogs`, `ScheduledTask` — không thuộc proxy, đừng đụng).
- **Conn string**: `ConnectionStrings:PushDb` trong `appsettings.json`. Giá trị `ENC:<base64>` được decrypt bằng [Crypton](../Services/Security/Crypton.cs) (AES-256/CBC verbatim port từ `TourKit.Shared`). Có thể set plaintext qua env `ConnectionStrings__PushDb` lúc deploy.
- **ORM**: Dapper + raw SQL. KHÔNG dùng EF migrations — schema sống trong const `SchemaSql` ở `TourkitAiDb.cs`, idempotent qua `IF OBJECT_ID(...) IS NULL` / `IF NOT EXISTS`. Mỗi lần startup `InitAsync()` chạy 1 lần.
- **Multi-tenant**: hầu hết bảng có composite PK `(TenantId, ...)`. Cross-tenant access → null/404 ở repo layer. Resolver tenant: [`HttpTenantContext`](../Services/Tenancy/HttpTenantContext.cs) đọc `X-Session-Id` header → tra [`TkSessionStore`](../Services/TourKit/TkSessionStore.cs) → ra `TenantId`.

## Bảng đang sử dụng

| # | Bảng | Mục đích | Repository | PK |
|---|------|----------|------------|-----|
| 1 | `dbo.Reviews` | Customer Review (rank A–D, alert, actions) — cache theo `Fingerprint` để skip AI khi data KH không đổi. | [`ReviewRepository`](../Services/Reviews/ReviewRepository.cs) | `(TenantId, CustomerId)` (KHÔNG composite — `CustomerId` đứng trước trong PK gốc; xem code) |
| 2 | `dbo.DealScores` | Deal scoring (WinRate %, Level Cao/TB/Thấp) — cùng pattern Reviews + cờ `IsSync` cho worker đẩy về CRM. | [`DealRepository`](../Services/Deals/DealRepository.cs) | `(TenantId, DealId)` |
| 3 | `dbo.MailAccounts` | SmartMail: cấu hình Gmail per-tenant per-username (App Password Crypton-enc + chữ ký công ty). | [`MailAccountStore`](../Services/Mail/MailAccountStore.cs) | `(TenantId, Username)` |
| 4 | `dbo.Mails` | SmartMail: lưu email IMAP đã kéo (subject/body/category/status/draft). | [`MailRepository`](../Services/Mail/MailRepository.cs) | `(TenantId, Id)` — `Id` = Message-Id (MimeKit chuẩn hóa). |
| 5 | `dbo.MailSyncState` | SmartMail: state đồng bộ IMAP incremental (`{uidValidity, lastUid}` per address). | [`MailSyncStore`](../Services/Mail/MailSyncStore.cs) | `(TenantId, Address)` |
| 6 | `dbo.TourQuotes` | Báo giá tour (GIT/FIT) — user lưu nháp/sửa nhiều lần, persist sang server (không localStorage). | [`TourQuoteRepository`](../Services/TourQuotes/TourQuoteRepository.cs) | `(TenantId, Id)` |
| 7 | `dbo.VisaAssessments` | Visa wizard: kết quả AI assessment (ExtractionJson + ResultJson + file count). | [`VisaRepository`](../Services/Visa/VisaRepository.cs) | `(TenantId, Id)` |
| 8 | `dbo.QuotaOrders` | Đơn nạp quota AI: pending → paid qua VietQR/Tingee webhook. Memo CK = `Id` (cho webhook match). | [`QuotaOrderRepository`](../Services/Quota/QuotaOrderRepository.cs) | `Id` global unique (`TKAI-{hash6}-{ts}-{rand4}`) — webhook không có TenantId, tra theo Id. |
| 9 | `dbo.WidgetTokens` | Chat widget tokens — tenant gen token paste vào `<script data-token="">` ở site khách. Có thể wire qua CRM TourKit (Phase 2: `TourKitSessionId` + `AllowedTools`). | [`WidgetTokenRepository`](../Services/Widget/WidgetTokenRepository.cs) | `Token` |
| 10 | `dbo.VisaQuestionSets` | Visa wizard: custom câu hỏi per-tenant (override default embedded ở frontend). | [`VisaQuestionRepository`](../Services/Visa/VisaQuestionRepository.cs) | `TenantId` |
| 11 | `dbo.TkSessions` | Phiên login TourKit CRM (share cross-process). PasswordEnc Crypton; JWT KHÔNG persist (re-login khi cần). ChatMemoryJson = lịch sử chat /assistant. | [`TkSessionRepository`](../Services/TourKit/TkSessionRepository.cs) | `Id` |
| 12 | `dbo.TenantQuota` | Quota AI per-tenant (`Limit`/`Used`). Atomic Consume cross-instance qua SQL `UPDATE Used = Used + 1`. In-mem cache + 5s batch flush ([`QuotaFlushService`](../Services/Quota/QuotaFlushService.cs)). | [`TenantQuotaRepository`](../Services/Quota/TenantQuotaRepository.cs) | `TenantId` |
| 13 | `dbo.AiUsageCounters` | **Aggregate** daily AI usage per model — `(DateUtc, Model)` MERGE upsert. Rẻ cho `/api/v1/usage` (group by Model). | [`UsageRepository`](../Services/UsageRepository.cs) | `(DateUtc, Model)` |
| 14 | `dbo.AiUsageHistory` | **Granular** per-request AI usage history (mỗi AI call = 1 row). Bổ sung cho `AiUsageCounters` khi cần breakdown theo feature/session/tenant. Trước đây file `data/ai-usage.jsonl` → mất khi deploy → đã migrate sang đây 2026-06-24. **Source của admin cross-tenant view `/admin-trav-ai/ai-usage`** (xem [AdminUsageRepository](../Services/Admin/AdminUsageRepository.cs)). | [`AiUsageHistoryRepository`](../Services/AiUsageHistoryRepository.cs) | `Id` IDENTITY |

### Tổng cộng: **14 bảng** owned by proxy.

## Bảng đã bỏ

| Bảng | Lý do drop | Khi nào |
|------|-----------|---------|
| `dbo.AiHistory` | Orphan — schema declare từ plan multi-tenancy nhưng KHÔNG repo nào INSERT/SELECT. Use case audit-trail "ai gen review/deal nào" đã được [`AiUsageHistory`](../Services/AiUsageHistoryRepository.cs) cover tốt hơn (granular per-request, có tokens + latency + cost). | 2026-06-24 |

> **Đừng "phục hồi" bảng đã drop**. Nếu cần audit trail mới, mở rộng `AiUsageHistory` (thêm `EntityId`/`Fingerprint` columns) thay vì tạo lại `AiHistory`.

## Bảng KHÔNG thuộc proxy (đừng đụng)

Cùng instance SQL nhưng owned bởi hệ khác — phòng trường hợp ai đó nhìn thấy nhầm.

| Bảng | Owner | Ghi chú |
|------|-------|---------|
| `dbo.PushLogs` | TourKit Push system | Log push notification (vài chục nghìn rows). |
| `dbo.ScheduledTask` | TourKit Push system | Lịch task push. |

Verify: `Grep "dbo\.(PushLogs|ScheduledTask)" --include="*.cs"` trong repo này → 0 match.

## Conventions

### 1. Multi-tenant first
- Mọi bảng business-data MUST có cột `TenantId NVARCHAR(128) NOT NULL`.
- PK clustered MUST có `TenantId` (thường ở vị trí đầu) để page nằm gần nhau theo tenant.
- Repo layer MUST filter `WHERE TenantId = @TenantId` cho mọi SELECT/UPDATE/DELETE. Không bao giờ trust id từ user mà bỏ filter tenant.

### 2. Idempotent schema migration
- Mỗi block `CREATE TABLE` bọc trong `IF OBJECT_ID('dbo.X', 'U') IS NULL`.
- Thêm cột vào bảng cũ: `IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.X') AND name = 'Y') ALTER TABLE...`.
- Thêm index: `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_X' AND object_id = OBJECT_ID('dbo.X')) ...`.
- **KHÔNG dùng MIGRATION TOOLS** (Flyway/EF Migrations) — schema sống trong code C#.

### 3. JSON cho payload phức tạp
- Field cấu trúc phức tạp → `NVARCHAR(MAX)` chứa JSON (vd `DataJson`, `DraftJson`, `ChatMemoryJson`). Tránh schema rigid khi shape còn evolve.
- Field index thường xuyên (search/filter/sort) → cột riêng (vd `Rank`, `Level`, `Status`).

### 4. Time columns
- `BIGINT` ms-epoch UTC cho field bot/AI ghi (vd `Reviews.GeneratedAt`) — gọn, không TZ headache.
- `DATETIME2` UTC cho field user-facing (vd `CreatedAt`/`UpdatedAt`). Default `SYSUTCDATETIME()` ở phía DB.

### 5. Sync flag pattern (Reviews/DealScores/TourQuotes)
- `IsSync BIT NOT NULL DEFAULT 0`: cờ cho worker (Hangfire bên TourKit) đồng bộ sang bảng chính của CRM.
- Mọi INSERT/UPDATE trong proxy reset `IsSync = 0`. Worker SELECT `WHERE IsSync = 0` rồi update lại `= 1` sau khi đẩy thành công.
- Filtered index `IX_X_Unsynced` (WHERE `IsSync = 0`) cho worker query rẻ.

### 6. Crypton-encrypted secrets
- Bất kỳ secret nào persist (App Password, TourKit password) MUST encrypt bằng [`Crypton`](../Services/Security/Crypton.cs) trước khi insert. Cột naming `*Enc` (vd `AppPasswordEnc`, `PasswordEnc`).
- KHÔNG bao giờ trả secret encrypted ra client — decrypt server-side rồi consume nội bộ.

## Thêm bảng mới — checklist

1. **Thêm `IF OBJECT_ID(...) IS NULL CREATE TABLE...` block** vào `SchemaSql` const trong [`TourkitAiDb.cs`](../Services/Db/TourkitAiDb.cs). Đặt ở cuối, KHÔNG xen kẽ giữa các block legacy ALTER.
2. **Tạo repository** ở `Services/<feature>/XxxRepository.cs`. Pattern:
   ```csharp
   public class XxxRepository {
       private readonly TourkitAiDb _db;
       public XxxRepository(TourkitAiDb db) { _db = db; }
       public async Task<...> ReadAsync(string tenantId, ..., CancellationToken ct = default) {
           await using var c = await _db.OpenAsync(ct);
           return await c.QueryFirstOrDefaultAsync<...>("SELECT ... WHERE TenantId = @tenantId", new { tenantId });
       }
   }
   ```
3. **Đăng ký DI** ở [`Program.cs`](../Program.cs): `builder.Services.AddSingleton<XxxRepository>();`.
4. **Update log message** ở `TourkitAiDb.InitAsync` thêm tên bảng mới.
5. **Update file này** — thêm row vào bảng "Bảng đang sử dụng" + bump tổng cộng count.
6. **Verify ở dev DB**: restart proxy → check log `TourkitAiDb schema OK (...)` có tên bảng mới.

## Đọc thêm

- [CLAUDE.md](../CLAUDE.md) — overall project conventions + section "Tenant scoping" (multi-tenant fix 2026-06-09).
- [docs/smartmail-ai-design.md](smartmail-ai-design.md) — design doc cho SmartMail (dùng `Mails`/`MailAccounts`/`MailSyncState`).
- [docs/Crypton-Integration.md](Crypton-Integration.md) — chi tiết Crypton AES-256/CBC port.
