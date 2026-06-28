# Workflow "Tự động review & cảnh báo deal nguội" + hàng đợi mail dùng chung

**Ngày:** 2026-06-28
**Trạng thái:** Draft (brainstorm)
**Tác giả:** Claude (theo brief CEO)

## Mục tiêu

Thêm workflow tự động thứ 2 (`deal-auto-review`) vào framework `IScheduledWorkflow` đã có. Mỗi chu kỳ workflow:

1. **Tự động review deal** (giống mail-auto-sync là bản tự động của việc bấm Refresh ở `/mail`): tự AI-chấm các deal **chưa được chấm** — thay người dùng khỏi phải vào trang `/deals` bấm thủ công.
2. **Cảnh báo deal nguội**: phát hiện deal **đang mở + nguội** (lâu không chăm sóc) → **đẩy 1 mail vào hàng đợi mail dùng chung** (`dbo.OutboundMails`). Một **worker riêng (CEO tự viết sau)** đọc hàng đợi, render template HTML + tham số, resolve email NV phụ trách từ DB tenant, gửi, cập nhật trạng thái.

Bảng `dbo.OutboundMails` được **tổng quát hoá** thành hàng đợi mail dùng chung — cảnh báo deal chỉ là 1 producer; mọi feature sau cần gửi mail nền đều enqueue vào đây.

**Phân chia rõ ràng:**
- **Proxy (repo này)**: tạo bảng + workflow phát hiện/chấm deal + **insert** vào hàng đợi (`TemplateCode` + `Params`, status `pending`). KHÔNG gửi email, KHÔNG soạn HTML.
- **Worker (toutkit-app, CEO viết sau)**: poll `pending` → load template HTML theo `TemplateCode` → replace `Params` → resolve người nhận → gửi SMTP → cập nhật trạng thái.

## Decision matrix (đã chốt qua brainstorm)

| Khía cạnh | Quyết định | Lý do |
|---|---|---|
| Loại workflow | `deal-auto-review` (**PerTenant**) | Chạy theo tenant, KHÔNG theo nhân viên (chốt C) |
| autoReview | Giữ, là **option mặc định BẬT** | Bản tự động của thao tác chấm tay ở `/deals`; mail cảnh báo kèm % chốt + gợi ý AI |
| 2 pass review | Pass 1: chấm deal MỚI (`rank=-1` + `statuses` + `createdWithinDays`); Pass 2: review LẠI deal đã chấm (đọc ScoreDeals) | Tách rõ "get bản ghi để review" vs "review lại từ ScoreDeals" |
| Chống review lại vô tận | `AutoReviewCount` + `maxAutoReviews` + **Pass 2 tự đánh cờ** `IsFinalized`+`FinalizedReason` khi status đổi khỏi list/quá hạn ngày | Deal hết đủ điều kiện → đánh cờ 1 lần → lần sau skip ngay, không kiểm/chấm lại |
| `IsFinalized` chặn cả cảnh báo | Deal `IsFinalized=1` → bỏ qua CẢ auto-review LẪN cảnh báo nguội (chốt B) | "Đã chốt sổ deal" → đừng nhắc nguội nữa |
| Lọc deal đáng giá | Option `minWinRateToNotify` (0 = mọi deal) | Chỉ cảnh báo deal đã chấm điểm cao đang nguội = "tiền đang tuột" (chốt D) — đỡ mail rác |
| Lọc trạng thái | Option `statuses` (list `TrangThaiPhieu`; rỗng = mọi) | User chọn trạng thái cần xử lý (vd chỉ "Mới"); ngoài list = đã xử lý → bỏ review + nhắc |
| Lọc thời gian tạo | Option `createdWithinDays` (vd 30) → upstream `startDate` | Chỉ deal mới tạo gần đây; deal cũ → bỏ qua, tránh phát sinh nhiều + đỡ review deal cũ |
| Validate service account | `POST /service-account` thử login + đếm deal trước khi lưu | Không lưu tài khoản sai/thiếu quyền → tránh workflow fail im lặng |
| Chu kỳ | Dropdown 5/10/15/30/60 như mail, user tự chọn (chốt E) | UI cảnh báo khi chọn quá ngắn (autoReview đốt quota) |
| **Quota + log AI nền** | Workflow **PHẢI** `AiCallContext.Push("<feature>", tenant, sessionId)` quanh AI call | Nếu KHÔNG Push → bypass quota + log `feature=unknown,tenant=null`. **Fix luôn cho mail-auto-sync (bug có sẵn)** |
| Bảng mail | **Tổng quát** `dbo.OutboundMails` (hàng đợi dùng chung) | Worker dùng chung cho mọi loại mail; deal-alert chỉ là 1 producer |
| Nội dung mail | **Template HTML + tham số** (`TemplateCode` + `Params` JSON) | CEO tạo template HTML; proxy chỉ truyền tham số → CEO replace. Proxy KHÔNG soạn HTML |
| Cột `Status` | **int (TINYINT)**: 0=pending,1=sent,2=failed,3=cancelled,4=skipped | CEO dùng enum sau (giống `ScheduledTask.byte Status`) |
| Gửi email | **Worker (CEO viết sau)**, KHÔNG phải proxy | Proxy chỉ enqueue; worker poll + render + gửi |
| Resolve email NV | **Worker tự resolve** từ DB tenant qua `DealId` | KHÔNG cần sửa upstream (toutkit-app) — đỡ động repo khác |
| Người nhận cảnh báo | NV phụ trách từng deal | Worker đọc `BookingTicket.NguoiPhuTrachs` → `Users.email` |
| Chống spam | Dedup qua chính bảng: `maxNotifications`/deal + `notifyMinGapHours` | Bảng vừa là hàng đợi vừa là sổ theo dõi (1 nguồn) |
| Auth gọi TourKit | **Service account per-tenant** (`dbo.TenantServiceAccounts`): workflow tự login → JWT, KHÔNG cần user nào online (chốt) | Chạy nền độc lập user; quyền tài khoản tự động quyết phạm vi quét. KHÔNG sửa upstream (login JWT như thường) |
| Cấu hình service account | **Endpoint API đơn giản** (không UI): `POST/GET /api/v1/workflows/service-account` (require X-Session-Id) | Setup nhẹ; mật khẩu Crypton-enc, không trả về client |
| DateTime | **UTC** (`*Utc` + `SYSUTCDATETIME()`) | STRICT rule proxy. Worker mẫu (`PushLogs`) dùng `DateTime.Now` local → ghi comment cảnh báo lệch giờ |

## Database — thêm vào `Services/Db/TourkitAiDb.cs` `SchemaSql`

```sql
IF OBJECT_ID('dbo.OutboundMails', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutboundMails (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        TenantId      NVARCHAR(64)   NOT NULL,
        Kind          NVARCHAR(64)   NOT NULL,        -- "deal-cooling-alert", ... (worker dispatch/lọc)
        SourceId      NVARCHAR(128)  NULL,            -- "Deal_123" → dedup/cancel theo nghiệp vụ
        Username      NVARCHAR(120)  NULL,            -- hộp thư Gmail nào gửi (per-user); NULL = worker tự chọn
        TemplateCode  NVARCHAR(64)   NULL,            -- mã template HTML worker render (vd "deal-cooling-alert")
        ToEmail       NVARCHAR(256)  NULL,            -- người nhận trực tiếp; NULL = worker tự resolve qua ToUserId/SourceId
        ToName        NVARCHAR(256)  NULL,
        ToUserId      INT            NULL,            -- để worker resolve email từ DB tenant
        Cc            NVARCHAR(512)  NULL,
        Subject       NVARCHAR(512)  NULL,            -- tuỳ chọn override; NULL = template tự quyết subject
        Params        NVARCHAR(MAX)  NULL,            -- JSON {key:value} tham số replace vào template
        Data          NVARCHAR(MAX)  NULL,            -- JSON payload phụ (debug/monitor), linh hoạt mỗi Kind
        Status        TINYINT        NOT NULL CONSTRAINT DF_OutboundMails_Status  DEFAULT 0,  -- 0=pending 1=sent 2=failed 3=cancelled 4=skipped
        RetryCount    INT            NOT NULL CONSTRAINT DF_OutboundMails_Retry   DEFAULT 0,
        ErrorMessage  NVARCHAR(1000) NULL,
        ScheduledUtc  DATETIME2      NULL,            -- gửi hẹn giờ (worker chỉ gửi khi ScheduledUtc <= now UTC); NULL = gửi ngay
        CreatedUtc    DATETIME2      NOT NULL CONSTRAINT DF_OutboundMails_Created DEFAULT SYSUTCDATETIME(),
        ProcessedUtc  DATETIME2      NULL,            -- worker set khi gửi xong
        CONSTRAINT PK_OutboundMails PRIMARY KEY CLUSTERED (Id)
    );
    -- Worker poll: lấy pending (Status=0) tới hạn, cũ trước.
    CREATE INDEX IX_OutboundMails_Poll  ON dbo.OutboundMails(Status, ScheduledUtc, CreatedUtc);
    -- Dedup/cancel theo nghiệp vụ.
    CREATE INDEX IX_OutboundMails_Source ON dbo.OutboundMails(TenantId, Kind, SourceId);
END;
```

**Quy ước `Status` (int → enum sau):** `0=Pending, 1=Sent, 2=Failed, 3=Cancelled, 4=Skipped`. Proxy chỉ ghi `0` (pending). Worker chuyển `1/2/3`.

### Bảng `dbo.TenantServiceAccounts` (tài khoản tự động per-tenant)

```sql
IF OBJECT_ID('dbo.TenantServiceAccounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantServiceAccounts (
        TenantId     NVARCHAR(64)   NOT NULL,
        Username     NVARCHAR(128)  NOT NULL,
        PasswordEnc  NVARCHAR(512)  NOT NULL,        -- Crypton-encrypted (KHÔNG plaintext, KHÔNG trả client)
        Domain       NVARCHAR(128)  NULL,            -- map TenantId nếu login cần (giống flow login-token)
        Enabled      BIT            NOT NULL CONSTRAINT DF_TenantSvcAcc_Enabled DEFAULT 1,
        UpdatedBy    NVARCHAR(120)  NULL,
        UpdatedUtc   DATETIME2      NOT NULL CONSTRAINT DF_TenantSvcAcc_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TenantServiceAccounts PRIMARY KEY CLUSTERED (TenantId)
    );
END;
```

Workflow đăng nhập bằng tài khoản này → JWT → gọi `/api/ai/booking-tickets`. **Không phụ thuộc user nào online.**

**⚠️ Tài khoản BẮT BUỘC có quyền `CH_XEM_ALL`** — đã verify upstream: `/api/ai/booking-tickets` lấy `userId` từ JWT, `SearchAsync` lọc theo quyền (`CH_XEM_ALL`→tất cả; `CH_XEM`→chỉ deal của user đó; không quyền→rỗng). Tài khoản thiếu quyền → workflow quét thiếu/rỗng deal. Endpoint `POST /service-account` nên cảnh báo nếu login thử thấy 0 deal (best-effort).

### Thêm cột kiểm soát auto-review vào `dbo.DealScores` (idempotent ALTER)

`dbo.DealScores` đã tồn tại (CREATE idempotent). Thêm cột bằng ALTER có guard `COL_LENGTH`:

```sql
IF COL_LENGTH('dbo.DealScores', 'AutoReviewCount') IS NULL
    ALTER TABLE dbo.DealScores ADD AutoReviewCount INT NOT NULL CONSTRAINT DF_DealScores_AutoRev DEFAULT 0;
IF COL_LENGTH('dbo.DealScores', 'IsFinalized') IS NULL
    ALTER TABLE dbo.DealScores ADD IsFinalized BIT NOT NULL CONSTRAINT DF_DealScores_Final DEFAULT 0;
IF COL_LENGTH('dbo.DealScores', 'FinalizedReason') IS NULL
    ALTER TABLE dbo.DealScores ADD FinalizedReason NVARCHAR(32) NULL;   -- 'manual' | 'status-changed' | 'aged'
IF COL_LENGTH('dbo.DealScores', 'LastAutoReviewUtc') IS NULL
    ALTER TABLE dbo.DealScores ADD LastAutoReviewUtc DATETIME2 NULL;
```

- `AutoReviewCount` — số lần workflow đã **tự** chấm deal này (chấm tay ở `/deals` KHÔNG tăng). `MarkAutoReviewed` → +1 + set `LastAutoReviewUtc`.
- `IsFinalized` + `FinalizedReason` — cờ "đừng tự chấm/nhắc lại nữa". Set bởi: (a) user chốt tay → `manual`; (b) **workflow Pass 2 tự đánh** khi deal đã chấm nhưng status đổi khỏi `statuses` → `status-changed`, hoặc quá hạn `createdWithinDays` → `aged`. Workflow gặp `IsFinalized=1` → bỏ qua hẳn (cả review lẫn nhắc).

**Lưu ý giờ:** mọi cột thời gian là **UTC**. Worker (toutkit-app) thường dùng `DateTime.Now` local — khi so sánh `ScheduledUtc <= now` phải dùng `DateTime.UtcNow`. Ghi comment rõ trong schema.

Cập nhật `docs/database-schema.md` (thêm 2 bảng: `OutboundMails` + `TenantServiceAccounts`, + 3 cột `DealScores`).

## Backend (proxy)

### Cấu trúc thư mục

```
Services/Mail/
  MailQueueRepository.cs        # MỚI — Dapper: EnqueueAsync, CountRecentBySourceAsync (dedup), ListForMonitorAsync, CancelBySourceAsync
Services/Workflows/
  DealAutoReviewWorkflow.cs     # MỚI — IScheduledWorkflow.Type = "deal-auto-review"; inject AiCallContext + Push
  MailAutoSyncWorkflow.cs       # SỬA — inject AiCallContext + Push("mail-auto-sync") (FIX bug bypass quota)
Services/TourKit/
  TenantServiceAccountStore.cs  # MỚI — Dapper CRUD dbo.TenantServiceAccounts (Crypton enc/dec); Get(tenant)
  TkSessionStore.cs             # SỬA — thêm GetOrCreateServiceSessionAsync(tenant, username, password) (reuse re-login machinery)
Endpoints/
  WorkflowEndpoints.cs          # SỬA — thêm POST/GET /workflows/service-account + GET /workflows/outbound-mails
Services/Db/TourkitAiDb.cs      # SỬA — thêm bảng dbo.OutboundMails
```

**Tái sử dụng (KHÔNG viết lại):** `DealOpportunityClient` (list deal + cooling fields + build profile), `DealScoringService` (AI chấm), `DealRepository` (lưu score → worker `DealScoreSyncService` sync `Rank`).

**Thêm vào `DealRepository` (kiểm soát auto-review):**
- `GetReviewControl(tenant, dealId)` → `(int AutoReviewCount, bool IsFinalized, string? FinalizedReason)?` — đọc cột mới (null nếu chưa có row).
- `MarkAutoReviewed(tenant, dealId)` → `UPDATE dbo.DealScores SET AutoReviewCount=AutoReviewCount+1, LastAutoReviewUtc=SYSUTCDATETIME() WHERE TenantId=@t AND DealId=@id`.
- `SetFinalized(tenant, dealId, reason)` → `UPDATE ... SET IsFinalized=1, FinalizedReason=@reason`. Dùng cho cả workflow Pass 2 (`status-changed`/`aged`) lẫn user chốt tay (`manual`).
- `SaveScore` GIỮ NGUYÊN — KHÔNG reset `AutoReviewCount`/`IsFinalized` (chỉ `MarkAutoReviewed` đụng tới). MERGE `WHEN MATCHED` không liệt kê 2 cột này nên giữ giá trị cũ; `WHEN NOT MATCHED` INSERT để DEFAULT (0/0).

### `MailQueueRepository` (hàng đợi mail dùng chung)

```csharp
public class MailQueueRepository
{
    // Enqueue 1 mail pending (Status=0). Trả Id.
    Task<long> EnqueueAsync(OutboundMail mail, CancellationToken ct);

    // Đếm số mail (Status sent=1 hoặc pending=0) của 1 nghiệp vụ trong N giờ gần nhất — dùng cho dedup/throttle.
    Task<(int total, DateTime? lastUtc)> CountRecentBySourceAsync(
        string tenantId, string kind, string sourceId, int withinHours, CancellationToken ct);

    // Đọc cho trang theo dõi (lọc Kind/Status, phân trang).
    Task<List<OutboundMail>> ListForMonitorAsync(
        string tenantId, string? kind, int? status, int take, CancellationToken ct);
}

public record OutboundMail(
    long Id, string TenantId, string Kind, string? SourceId, string? Username, string? TemplateCode,
    string? ToEmail, string? ToName, int? ToUserId, string? Cc,
    string? Subject, string? Params, string? Data,
    byte Status, int RetryCount, string? ErrorMessage,
    DateTime? ScheduledUtc, DateTime CreatedUtc, DateTime? ProcessedUtc);

// Hằng số trạng thái (nguồn để FE/worker tham chiếu; CEO chuyển thành enum trong worker).
public static class OutboundMailStatus
{
    public const byte Pending = 0, Sent = 1, Failed = 2, Cancelled = 3, Skipped = 4;
}
```

Repository thuần Dapper, KHÔNG cache. Lỗi DB → throw (caller workflow xử lý như fail run).

### `DealAutoReviewWorkflow`

```csharp
public string Type => "deal-auto-review";
public string Label => "Tự động review & cảnh báo deal";
public string Description => "Tự AI-chấm deal mới chưa chấm + cảnh báo deal nguội (đẩy mail vào hàng đợi)";
public WorkflowScope Scope => WorkflowScope.PerTenant;   // chạy theo tenant; framework gọi username=""
```

**Options (parse từ `optionsJson`, mặc định an toàn):**

```json
{
  "statuses": [1],             // CHỈ xử lý deal có TrangThaiPhieu trong list (rỗng = mọi trạng thái). Ngoài list = coi như "đã xử lý" → bỏ qua review + nhắc
  "createdWithinDays": 30,     // CHỈ xử lý deal tạo trong N ngày gần đây (deal cũ hơn → bỏ qua, đỡ review + tránh phát sinh nhiều)
  "autoReview": true,          // tự chấm deal chưa review
  "reviewMax": 20,             // số deal chấm tối đa / run (chặn đốt quota)
  "maxAutoReviews": 5,         // số lần tự chấm tối đa / deal (chống review lại vô tận)
  "coolingDays": 7,            // ngưỡng "nguội" (ngày không chạm)
  "minWinRateToNotify": 0,     // chỉ cảnh báo deal đã chấm winRate ≥ ngưỡng (0 = mọi deal nguội)
  "maxNotifications": 3,       // số lần cảnh báo tối đa / deal
  "notifyMinGapHours": 24      // giãn cách tối thiểu giữa 2 cảnh báo cùng deal
}
```

`statuses` là list `TrangThaiPhieu` (int) tenant-config (Mới / Đã liên hệ / ...) — UI cho user tick chọn (vd chỉ "Mới" để chỉ nhắc lead mới chưa ai đụng). `createdWithinDays` map xuống upstream `startDate = today - N` (lọc server-side) + guard client-side.

Record `DealAutoReviewOptions(List<int> Statuses, int CreatedWithinDays, bool AutoReview, int ReviewMax, int MaxAutoReviews, int CoolingDays, int MinWinRateToNotify, int MaxNotifications, int NotifyMinGapHours)` + `Parse(string?)` (giống `MailAutoSyncOptions.Parse`, clamp: `createdWithinDays` 1..365, `reviewMax` 1..100, `maxAutoReviews` 1..50, `coolingDays` 1..90, `minWinRateToNotify` 0..100, `maxNotifications` 1..20, `notifyMinGapHours` 1..720; `statuses` parse list int, bỏ giá trị ≤0).

### Luồng 1 run (`RunAsync`)

```
0. // PerTenant → username="" . Login bằng SERVICE ACCOUNT của tenant — KHÔNG cần user online.
   svc = serviceAccounts.Get(tenantId)
   svc == null || !svc.Enabled → return Fail("Chưa cấu hình tài khoản tự động cho tenant (POST /workflows/service-account)")
   sessionId = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct)
               // tái dùng TkSession: có sẵn → reuse (auto re-login khi JWT hết hạn/401); chưa có → CreateAsync (login)
   // Lưu ý: service account = NGƯỜI ĐỌC deal (BẮT BUỘC CH_XEM_ALL để thấy hết). KHÁC với NGƯỜI NHẬN mail
   //        (= NV phụ trách từng deal, worker resolve từ dealId → Users.email). Service account KHÔNG nhận mail.
   //        Mailbox gửi để worker tự chọn (hộp thư cấu hình của tenant) → OutboundMail.Username = null.

   // QUOTA + LOG: bọc TOÀN BỘ phần gọi AI bằng AiCallContext.Push để trừ quota + log đúng tên chức năng.
   using var _aiScope = _aiCtx.Push("deal-auto-review", tenantId, sessionId)

   startDate = today - opt.CreatedWithinDays      // lọc server-side: chỉ deal tạo trong N ngày
   InStatuses(d) = opt.Statuses.Count == 0 || opt.Statuses.Contains(d.Status)   // lọc trạng thái (rỗng = mọi trạng thái)

   Eligible(d) = InStatuses(d) && (AgeDays(d.CreatedAt) <= opt.CreatedWithinDays)   // = Mới(status trong list) + ngày còn trong hạn review

1A. PASS 1 — REVIEW DEAL MỚI (chưa chấm), nếu opt.AutoReview:
     page = client.ListPagedAsync(sessionId, pageIndex:1, pageSize:opt.ReviewMax, rank:-1, startDate:startDate, ct)   // rank=-1 = CHƯA chấm
     foreach deal in page.Items.Where(InStatuses) (cap reviewMax):    // trạng thái cần review (user cấu hình `statuses`)
        ctx   = client.GetContextAsync(sessionId, deal, ct)
        score = scoring.ScoreAsync(ctx.Profile, ...)                  // ← AI: trừ quota tenant (nhờ Push)
        dealRepo.SaveScore(tenant, deal.Id, ctx.Fingerprint, score)  // worker sync Rank; AutoReviewCount khởi 0
        dealRepo.MarkAutoReviewed(tenant, deal.Id)                    // AutoReviewCount=1
        reviewed++

1B. PASS 2 — REVIEW LẠI (đọc từ ScoreDeals = deal ĐÃ chấm), nếu opt.AutoReview:
     scored = client.ListPagedAsync(sessionId, pageIndex:1, pageSize:200, rank: SENTINEL_SCORED /*đã chấm*/, ct)   // KHÔNG lọc status/ngày → để thấy deal đã đổi/đã cũ
     foreach deal in scored:
        meta = dealRepo.GetReviewControl(tenant, deal.Id)            // (AutoReviewCount, IsFinalized, FinalizedReason)
        if meta?.IsFinalized == true: { finalizedSkipped++; continue }    // đã đánh cờ bỏ qua → skip luôn
        if NOT Eligible(deal):                                        // status đã đổi khỏi list HOẶC quá hạn ngày
            dealRepo.SetFinalized(tenant, deal.Id, reason: InStatuses(deal) ? "aged" : "status-changed")  // ĐÁNH CỜ để lần sau bỏ qua
            autoFinalized++; continue
        if meta.AutoReviewCount >= opt.MaxAutoReviews: { cappedSkipped++; continue }   // đủ số lần tự chấm
        ctx = client.GetContextAsync(sessionId, deal, ct)
        if dealRepo.GetScore(tenant, deal.Id, ctx.Fingerprint) != null: continue       // nội dung CHƯA đổi (fingerprint trùng) → khỏi chấm lại
        score = scoring.ScoreAsync(ctx.Profile, ...)                  // nội dung đổi → chấm lại (AI, trừ quota)
        dealRepo.SaveScore(tenant, deal.Id, ctx.Fingerprint, score)
        dealRepo.MarkAutoReviewed(tenant, deal.Id)                    // AutoReviewCount++
        rereviewed++

2. // Cảnh báo nguội — quét deal (A-FIX: dùng ListPagedAsync vì có map cooling; ListOpenAsync KHÔNG map → luôn false)
     page2 = client.ListPagedAsync(sessionId, pageIndex:1, pageSize:200, startDate:startDate, ct)   // cùng cửa sổ createdWithinDays
     open  = page2.Items.Where(d => d.Status != 5 /*Hủy*/ && !IsClosedWon(d.StatusName) && InStatuses(d))  // mở + trạng thái cần xử lý
     cooling = open.Where(d => d.IsCooling && d.CoolingDays >= opt.CoolingDays)
     foreach deal in cooling:
        if string.IsNullOrWhiteSpace(deal.Assignees): { skippedNoAssignee++; continue }  // chưa giao NV → worker không resolve được người nhận
        meta = dealRepo.GetReviewControl(tenant, deal.Id)
        if meta?.IsFinalized == true: { skipped++; continue }                    // B-FIX: đã chốt sổ → không nhắc nguội
        score = dealRepo.PeekCached(tenant, deal.Id)?.Score                       // điểm AI nếu đã chấm
        if opt.MinWinRateToNotify > 0 && (score?.WinRate ?? 0) < opt.MinWinRateToNotify: { skipped++; continue }  // D: chỉ deal đáng giá
        (sent, last) = mailRepo.CountRecentBySourceAsync(tenant, "deal-cooling-alert", $"Deal_{deal.Id}", 24*30, ct)
        if sent >= opt.MaxNotifications: { skipped++; continue }                  // đủ số lần
        if last != null && (UtcNow - last) < opt.NotifyMinGapHours h: { skipped++; continue }  // chưa tới giãn cách
        params = BuildAlertParams(deal, score)                                    // dict tham số cho template
        mailRepo.EnqueueAsync(new OutboundMail{
            Kind="deal-cooling-alert", SourceId=$"Deal_{deal.Id}", Username=null /*worker chọn hộp thư tenant*/,
            TemplateCode="deal-cooling-alert",
            ToEmail=null, ToUserId=null,        // worker resolve NV phụ trách từ DealId
            Subject=null,                       // template tự quyết subject
            Params=JSON(params),
            Data=JSON{ dealId, code, customerName, assigneeNames, coolingDays, winRate, nextAction },
            Status=0 /*pending*/
        }, ct)
        queued++

3. Summary = JSON{ reviewed, rereviewed, autoFinalized, finalizedSkipped, cappedSkipped, cooling=cooling.Count, queued, skipped, skippedNoAssignee }
   return Ok(Summary)
```

**Giới hạn quét (không âm thầm bỏ sót):** bước 2 quét tối đa `pageSize:200` deal/run. Tenant có >200 deal → log cảnh báo số bị cắt; backlog drain dần qua các chu kỳ (deal nguội vẫn còn nguội lần sau). Nếu thực tế vượt nhiều → phase sau phân trang vòng lặp. `IsClosedWon(statusName)` = helper normalize check "chốt đơn/thành công/hoàn thành/đã bán" (bê từ `DealOpportunityClient.ListOpenAsync`).

**Lỗi/cancel:**
- Mỗi deal độc lập trong vòng lặp (try/catch quanh từng deal) — lỗi 1 cái KHÔNG chặn cái khác.
- `QuotaExhaustedException` (autoReview hết quota) → bubble lên scheduler → fail run → đếm 5-strike auto-pause (cơ chế có sẵn).
- `OperationCanceledException` (quá 5 phút) → fail run "Vượt quá thời gian 5 phút".
- TkSession null / TourKit 401 không re-login được → fail run.

### Tham số template (`BuildAlertParams`)

Proxy KHÔNG soạn HTML — chỉ build dict tham số, worker replace vào template HTML của CEO. Key cố định (CEO dùng làm `{{placeholder}}`):

```json
{
  "dealId": 1203,
  "dealCode": "CH-1203",
  "customerName": "Chị Lan",
  "phone": "09xx",
  "title": "Tour Nhật 6N5Đ",
  "totalPriceFormatted": "32.000.000 đ",
  "statusName": "Đang tư vấn",
  "sourceName": "Facebook",
  "assigneeNames": "Nguyễn Văn A",
  "coolingDays": 9,
  "lastInteractionAt": "2026-06-19",
  "hasReview": true,
  "winRate": 65,
  "level": "cao",
  "nextAction": "Gọi lại hôm nay chốt cọc vì khách đã đồng ý giá",
  "dealUrl": "<deep-link tới deal nếu có>"
}
```

`hasReview=false` khi deal chưa chấm → template ẩn block gợi ý AI. CEO toàn quyền layout/subject trong template; proxy chỉ đảm bảo cung cấp đủ tham số trên (ổn định, versioned theo `TemplateCode`).

### Service account: `TenantServiceAccountStore` + `GetOrCreateServiceSessionAsync`

- **`TenantServiceAccountStore`** (Dapper, `dbo.TenantServiceAccounts`): `Get(tenant) → (Username, Password, Domain, Enabled)?` (Crypton-decrypt password); `Upsert(tenant, username, password, domain, updatedBy)` (Crypton-encrypt). KHÔNG bao giờ trả password ra client.
- **Validate khi lưu (`POST /service-account`)**: thử `TourKitApiClient.LoginAsync(tenant, username, password)` → fail (sai pass / không có quyền) → trả lỗi, **KHÔNG lưu**. OK → thử gọi `/api/ai/booking-tickets?pageSize=1` đếm deal thấy được → cảnh báo nếu 0 (có thể thiếu `CH_XEM_ALL`) → lưu. Tránh lưu tài khoản hỏng để workflow fail im lặng về sau.
- **`TkSessionStore.GetOrCreateServiceSessionAsync(tenant, username, password, ct)`**: tìm session sẵn của (tenant, username) trong cache/SQL → reuse (auto re-login khi dùng); chưa có → `CreateAsync(tenant, username, password)` (login mới). Trả `sessionId`. Tận dụng nguyên máy phiên (re-login 401, cross-process, persist SQL). Service session "sống" vì workflow chạy đều → `LastUsed` cập nhật, không bị idle-prune.
- **KHÔNG còn phụ thuộc user online**: dù không ai đăng nhập `/assistant`, workflow vẫn login được bằng service account.

### Quota + Logging cho AI nền (BẮT BUỘC — sửa cả bug mail-auto-sync)

AI gọi từ workflow nền KHÔNG có `HttpContext` → `AiCallContext.Resolve()` trả `feature=unknown, tenant=null` → **bypass quota + log sai**. Mọi workflow phải bọc AI call bằng `AiCallContext.Push`:

- **`DealAutoReviewWorkflow`**: inject `AiCallContext _aiCtx`; `using var _ = _aiCtx.Push("deal-auto-review", tenantId, sessionId)` bao quanh bước 1 (xem luồng). → autoReview trừ quota tenant + log `feature="deal-auto-review"`.
- **`MailAutoSyncWorkflow` (FIX bug có sẵn)**: inject `AiCallContext`; `using var _ = _aiCtx.Push("mail-auto-sync", tenantId)` bao quanh `_sync.RunAsync(...)` + `AutoReplyAsync(...)`. Trước fix: classify + auto-reply nền **không trừ quota** + log `tenant=null`. Sau fix: trừ đúng + log `feature="mail-auto-sync"`.

Đặt feature riêng cho automation (`mail-auto-sync`, `deal-auto-review`) thay vì `mail`/`deals` → trong `dbo.AiUsageHistory` CEO **tách được** chi phí AI do tự động hoá vs thao tác tay. (Cảnh báo nguội = enqueue, KHÔNG gọi AI → KHÔNG trừ quota; chỉ autoReview trừ.)

### KHÔNG cần viết lại API config (đã hỗ trợ PerTenant sẵn)

4 endpoint workflow hiện có (`GET /workflows`, `PUT /workflows/{type}`, `run-now`, `runs`) đã xử lý scope: `var scopeUser = wf.Scope == WorkflowScope.PerUser ? user : "";` → workflow `PerTenant` tự lưu/đọc key `Username=""`. Scheduler cũng truyền `scopeUser` xuyên suốt. ⇒ **KHÔNG sửa** `WorkflowEndpoints`/`WorkflowRepository`/`WorkflowSchedulerService`; card mới tự xuất hiện qua registry; chỉ THÊM 1 endpoint monitor dưới đây.

### API mới (proxy) — đều require `X-Session-Id` → resolve tenant

| Method | Path | Body / Response |
|---|---|---|
| POST | `/api/v1/workflows/service-account` | Body `{username, password, domain?}` → **thử login TourKit để validate** → OK mới lưu Crypton-enc → `{ok:true, dealsVisible}`; login fail → `{ok:false, error}` (KHÔNG lưu) |
| GET | `/api/v1/workflows/service-account` | `{configured: bool, username}` (KHÔNG trả password) |
| GET | `/api/v1/workflows/outbound-mails?kind=&status=&limit=50` | `{ items: [{ id, kind, sourceId, templateCode, toEmail, toName, subject, status, retryCount, errorMessage, scheduledUtc, createdUtc, processedUtc }] }` (`status` int) |

Service-account: tenant từ session; mọi user trong tenant cấu hình được tài khoản tự động chung. `outbound-mails`: theo dõi trạng thái gửi (KHÔNG cross-tenant).

### DI (`Program.cs`)

```csharp
builder.Services.AddSingleton<MailQueueRepository>();
builder.Services.AddSingleton<TenantServiceAccountStore>();
builder.Services.AddSingleton<IScheduledWorkflow, DealAutoReviewWorkflow>();
// WorkflowRegistry tự pickup IScheduledWorkflow mới; scheduler không cần sửa.
```

## Frontend

`/workflows` (`wwwroot/pages/workflows.jsx`) hiện render card theo `GET /api/v1/workflows` (registry tự liệt kê) → card `deal-auto-review` **tự xuất hiện**, không cần thêm trang.

**Bổ sung options UI** cho card `deal-auto-review` (giống mail có options auto-reply): **tick chọn `statuses`** (lấy danh sách trạng thái deal của tenant — board/SectionWork), `createdWithinDays`, toggle `autoReview`, số `reviewMax`, `maxAutoReviews`, `coolingDays`, `minWinRateToNotify`, `maxNotifications`, `notifyMinGapHours`. Lưu qua `PUT /api/v1/workflows/{type}` (đã có cột `OptionsJson`). Danh sách trạng thái lấy từ endpoint deal có sẵn (board) hoặc nhập ID nếu chưa wire.

**Form service account** (nhỏ, trong card hoặc đầu trang): nhập username/password tài khoản tự động → `POST /workflows/service-account`; hiển thị trạng thái qua `GET` (`configured` + username, KHÔNG password). Bắt buộc cấu hình trước khi bật workflow.

*(Tuỳ chọn, có thể tách phase sau)* mục "Mail đã đẩy" đọc `GET /api/v1/workflows/outbound-mails` hiển thị bảng trạng thái (map int status → nhãn Việt).

## Hợp đồng cho Worker (CEO viết sau — toutkit-app)

Tài liệu để CEO build worker, KHÔNG nằm trong phạm vi proxy:

1. Poll: `SELECT TOP N * FROM dbo.OutboundMails WHERE Status=0 AND (ScheduledUtc IS NULL OR ScheduledUtc <= SYSUTCDATETIME()) ORDER BY CreatedUtc` (chú ý giờ **UTC**).
2. Render: load template HTML theo `TemplateCode` → replace `{{key}}` bằng `Params` (JSON). `Subject` lấy từ template hoặc cột `Subject` nếu có.
3. Với `Kind='deal-cooling-alert'`: đọc `Data.dealId` (hoặc `SourceId`) → tenant DB `BookingTicket.NguoiPhuTrachs` → `Users.email` (1 deal nhiều NV → gửi nhiều / Cc).
4. Gửi SMTP → set `Status=1 (Sent)`, `ProcessedUtc=SYSUTCDATETIME()`.
5. Lỗi → `Status=2 (Failed)`, `ErrorMessage`, `RetryCount++` (tự định nghĩa policy retry/giới hạn).
6. Cancel: proxy có thể set `Status=3 (Cancelled)` theo `SourceId` nếu deal hết nguội (phase sau).

## Đánh giá ảnh hưởng worker (toutkit-app) — đã kiểm tra

Thêm 3 cột vào `dbo.DealScores` + bảng mới `dbo.OutboundMails`. Worker `PushNotification.Worker` **KHÔNG bị ảnh hưởng**:

| Điểm chạm | Kết luận |
|---|---|
| `DealScoreSyncWorker` đọc `dbo.DealScores` | EF LINQ `db.DealScores.Where(!IsSync)` — entity `DealScore` map cột tường minh qua `[Column]`, SELECT chỉ lấy cột đã map → cột mới bị bỏ qua. An toàn. |
| Worker ghi `dbo.DealScores` | Chỉ `UPDATE SET IsSync=1` (EF chỉ update cột đổi). KHÔNG đụng `AutoReviewCount`/`IsFinalized`/`LastAutoReviewUtc`. |
| Schema validation | KHÔNG có `EnsureCreated`/`Database.Migrate`/`FromSqlRaw`/`SELECT *` trên DealScores trong toutkit-app → thêm cột không gây mismatch model↔DB. |
| `DealScoreSyncService` raw SQL | Chạy trên `BookingTickets` của DB tenant (UPDATE `[rank]`), không phải `dbo.DealScores`. Vô can. |
| `dbo.OutboundMails` | Bảng mới, worker hiện tại không tham chiếu. Zero impact. |

**Khuyến nghị (không bắt buộc):** khi CEO viết worker mail mới, nếu muốn map `OutboundMails` qua EF thì thêm entity + `DbSet` trong `PushDbContext`; nếu muốn cập nhật `DealScore` entity để đọc 2 cột mới (vd hiển thị) thì thêm property `[Column]` — cả 2 đều backward-compatible.

## Out of scope (v1)

- Proxy KHÔNG gửi email, KHÔNG soạn HTML (worker + template của CEO lo).
- KHÔNG sửa upstream toutkit-app (worker tự resolve email NV).
- KHÔNG quản lý template trong proxy (CEO tự lưu template HTML; proxy chỉ tham chiếu `TemplateCode`).
- KHÔNG cron/khung giờ (dùng interval dropdown có sẵn).
- Cancel mail khi deal hết nguội (để phase sau — `Status=3` đã sẵn).
- Gộp digest nhiều deal/NV (v1 mỗi deal 1 mail, throttle qua `maxNotifications`/`notifyMinGapHours`).
- Multi-instance leader election cho scheduler (kế thừa hạn chế hiện tại của framework).

## Implementation order

1. SQL `dbo.OutboundMails` + `dbo.TenantServiceAccounts` + ALTER `dbo.DealScores` (3 cột) vào `TourkitAiDb.cs` + `docs/database-schema.md`.
2. `MailQueueRepository.cs` (Dapper enqueue + dedup + monitor) + `OutboundMailStatus` + DI.
3. `DealRepository`: thêm `GetReviewControl` / `MarkAutoReviewed` / `SetFinalized`.
4. `TenantServiceAccountStore.cs` (Dapper + Crypton) + `TkSessionStore.GetOrCreateServiceSessionAsync` + DI.
5. **FIX quota bug**: `MailAutoSyncWorkflow` inject `AiCallContext` + `Push("mail-auto-sync")` (nhỏ, độc lập, làm trước để xác nhận cơ chế).
6. `DealAutoReviewWorkflow.cs` (PerTenant, service-account login, options parse + run flow + `Push("deal-auto-review")` + BuildAlertParams + IsClosedWon) + DI `IScheduledWorkflow`.
7. `WorkflowEndpoints.cs`: thêm `POST/GET /workflows/service-account` (POST **validate login + đếm deal** trước khi lưu) + `GET /workflows/outbound-mails`.
8. Options UI cho card `deal-auto-review` trong `workflows.jsx` (+ form nhập service account + cảnh báo chu kỳ ngắn).
9. Smoke test: cấu hình service account → run-now (KHÔNG cần ai login /assistant) → kiểm `dbo.OutboundMails` có dòng `Status=0` + `Params` đủ tham số; `dbo.DealScores` có score mới; `dbo.AiUsageHistory` có dòng `feature=deal-auto-review` + tenant đúng + quota tenant giảm.
10. Update `CLAUDE.md` (API table + folder layout + section workflow + ghi chú fix quota mail + service account).
11. Commit + push feature branch.

## Test plan (manual)

1. Cấu hình service account: `POST /workflows/service-account {username, password}` → verify: sai pass → `{ok:false}` không lưu; đúng → `{ok:true, dealsVisible>0}` (tài khoản quyền CH_XEM_ALL). Vào `/workflows` → card + trạng thái "đã cấu hình tài khoản".
2. Bật toggle + interval + options (statuses=[Mới], createdWithinDays=30, coolingDays=7) → save. (KHÔNG cần ai đăng nhập `/assistant`.)
   - Verify lọc: deal trạng thái ngoài [Mới] → KHÔNG bị nhắc; deal tạo >30 ngày → KHÔNG quét.
3. "▶ Chạy ngay" → đợi → summary `{reviewed, cooling, queued, skipped}`.
4. SQL: `SELECT * FROM dbo.OutboundMails WHERE Kind='deal-cooling-alert'` có dòng `Status=0`, `TemplateCode='deal-cooling-alert'`, `Params` JSON đủ field; `dbo.DealScores` có score mới (Pass 1).
   - Đổi 1 deal đã chấm sang trạng thái KHÁC [Mới] → run lại → bản ghi đó `IsFinalized=1, FinalizedReason='status-changed'` (Pass 2 tự đánh cờ) → lần sau skip.
5. Chạy lại ngay → các deal vừa cảnh báo bị `skipped` (chưa tới `notifyMinGapHours`).
6. Chạy đủ `maxNotifications` lần (giả lập gap) → deal đó ngừng enqueue.
7. Trang theo dõi `/api/v1/workflows/outbound-mails` liệt kê đúng trạng thái (int).
