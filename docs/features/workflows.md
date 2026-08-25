# Tự động hoá và worker

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

---

## User Workflows ("Tự động hóa")

Tác vụ AI chạy tự động theo lịch (interval), cấu hình per-(Tenant, Username). Framework đủ mở rộng: thêm workflow mới = implement `IScheduledWorkflow` + đăng ký DI + registry tự pickup. Built-in:
- **`mail-auto-sync`** (PerUser) — kéo Gmail + AI phân loại mỗi N phút (+ tùy chọn auto-reply).
- **`deal-auto-review`** (PerTenant) — tự AI-chấm cơ hội bán hàng + **cảnh báo deal nguội** (xem section dưới).
- **`payment-watchdog`** (PerTenant) — tour khởi hành ≤7 ngày mà khách chưa trả đủ → ghi cảnh báo vào `dbo.AgentInsights`. Rule thuần, 0 AI. Dùng service account.
- **`sale-brief`** / **`ceo-brief`** (PerTenant, xem section "Bản tin AI" dưới) — bản tin sáng, gửi theo phiên CỦA TỪNG NGƯỜI NHẬN.
- **`customer-auto-review`** (PerTenant) — tự AI-chấm hạng KH (A–D) chưa review + **review lại định kỳ**. Reuse `ReviewService` (lưu `dbo.Reviews` → worker sync rank về CRM); KH từ `/api/ai/customers` qua `CustomerReviewClient`. Pass 1 = KH chưa review trong `createdWithinDays`; Pass 2 = đọc `GeneratedAt` (ngày review cuối) trong `dbo.Reviews`, re-review khi quá `reReviewDays`. Options `{createdWithinDays, reReviewDays, reviewMax}`. Dùng chung service account + `AiCallContext.Push("customer-auto-review")`.

⚠️ **Quota + log AI nền (STRICT):** workflow chạy nền KHÔNG có HttpContext → PHẢI `AiCallContext.Push("<feature>", tenantId[, sessionId])` bao quanh AI call, nếu không sẽ **bypass quota tenant + log `feature=unknown,tenant=null`**. Dùng feature riêng cho automation (`mail-auto-sync`/`deal-auto-review`/`digest`) để tách chi phí AI tự động vs thao tác tay trong `dbo.AiUsageHistory`.

### `deal-auto-review` — tự review & cảnh báo deal nguội (2026-06-28)

PerTenant. Auth = **service account** per-tenant (`dbo.TenantServiceAccounts`, `TenantServiceAccountStore`): workflow tự login TourKit → JWT (qua `TkSessionStore.GetOrCreateServiceSessionAsync`), KHÔNG cần user online. Tài khoản nên có quyền `CH_XEM_ALL` (quyết phạm vi quét deal). Luồng `DealAutoReviewWorkflow.RunAsync`:
- **Pass 1** — chấm deal MỚI (`rank=-1`) lọc `statuses` (rỗng=mọi) + `createdWithinDays` → `DealScoringService` → `DealRepository.SaveScore` (worker sync `Rank`).
- **Pass 2** — review LẠI deal đã chấm: còn đủ điều kiện (status ∈ `statuses` + tuổi ≤ `createdWithinDays`) → chấm lại khi nội dung đổi; hết điều kiện → `DealRepository.SetFinalized(reason)` (`status-changed`/`aged`) để lần sau bỏ qua. Chống review vô tận: `AutoReviewCount` + `maxAutoReviews` + `IsFinalized`.
- **Cảnh báo nguội** — deal đang mở + nguội ≥ `coolingDays` (+ `minWinRateToNotify`, throttle `maxNotifications`/`notifyMinGapHours`, bỏ qua deal chưa giao NV) → **enqueue** vào `dbo.OutboundMails` (`MailQueueRepository`, Kind=`deal-cooling-alert`, `TemplateCode`+`Params`). Proxy KHÔNG gửi — **`TourKit.PushWorker` bên toutkit-app** render template HTML + resolve email NV phụ trách từ `Data.dealId` + gửi SMTP + cập nhật `Status`. Template mẫu + hợp đồng worker: [`docs/mail-templates/`](docs/mail-templates/).
- Options (per-tenant, `OptionsJson`): `{statuses[], createdWithinDays, autoReview, reviewMax, maxAutoReviews, coolingDays, minWinRateToNotify, maxNotifications, notifyMinGapHours}`. Frontend: form service account + options trong card `deal-auto-review` (`workflows.jsx`).

- **Schema:** `dbo.UserWorkflows` (config, PK `TenantId+Username+WorkflowType`) + `dbo.WorkflowRuns` (lịch sử 100 run/scope, prune tự động). `Username=''` = per-tenant (workflow `Scope=PerTenant`, vd `deal-auto-review`).
- **Scheduler:** `WorkflowSchedulerService` (`BackgroundService`, tick 60s) → `ListDue` → fire-and-forget `Task.Run`. `SetNextRun` chạy ngay trước `Task.Run` để tránh re-fire trong tick kế. Auto-pause sau 5 fail liên tiếp, user "Bật lại" qua PUT endpoint.
- **MailSyncService (extract):** logic `POST /mail/sync` được extract ra `TourkitAiProxy.Services/Mail/MailSyncService.cs` → dùng chung giữa HTTP endpoint và `MailAutoSyncWorkflow`. Response shape `/mail/sync` giữ nguyên (`{items, counts, classified, fetched}`).
- **Endpoint:** require `X-Session-Id` (pattern giống MailEndpoints). Manual trigger (`/run-now`) **fire-and-forget, KHÔNG đồng bộ** — trả về sau ~100ms với `summary` rỗng, workflow chạy tiếp ở nền qua pipeline scheduler (vẫn đếm failure + auto-pause + ghi `WorkflowRuns`). ⚠️ Đừng "sửa" lại thành đồng bộ: đã từng đồng bộ và run dài 100s+ làm request trình duyệt timeout → user thấy báo **lỗi giả** dù workflow chạy xong bình thường. Kết quả đọc ở `GET /workflows/{type}/runs`, không đọc ở response của `/run-now`.
- **Frontend:** `/workflows` page (`wwwroot/pages/workflows.jsx`), card per workflow + toggle + interval dropdown + run history collapsible. Nav entry "Tự động hóa" nằm CUỐI group **"Tích hợp"** (kiểm code 18/08: `app.jsx` — tài liệu này từng ghi nhầm là group "Bản tin & Tự động", chưa bao giờ đúng với code). Mục này KHÔNG gate quyền vì trang có thêm phần cá nhân — xem section "Bản tin AI").

## Deploy tách site: TourkitAiProxy.Worker

Scheduler workflow (`WorkflowSchedulerService` tick 60s) chạy trên project riêng `TourkitAiProxy.Worker` (Sdk=Microsoft.NET.Sdk.Worker) để ổn định:
- Web restart / IIS AppPool recycle / crash → automation KHÔNG rớt.
- Worker fail → UI + API vẫn sống. Deploy độc lập.
- Share code qua `<ProjectReference Include="../TourkitAiProxy.csproj" />` — worker dùng NGUYÊN `Services/Workflows/*`, `Services/Mail/*`, `Services/Deals/*`... không copy code.
- DI wiring shared qua [`TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs`](../../TourkitAiProxy.Services/Bootstrap/WorkflowStackRegistration.cs) extension `AddWorkflowStack()` — cả web và worker gọi cùng 1 method → 1 nguồn wiring.

**Cấu hình tách:**
- Web `appsettings.json`: `"Workflows": { "RunScheduler": false }` (default sau khi split — xem `appsettings.example.json`).
- Worker `appsettings.json`: `ConnectionStrings:PushDb` + `Redis:ConnectionString` + `Providers:*:ApiKey` + `Models:*:ApiKey` + `TourKit:BaseUrl` TRÙNG web (share `dbo.UserWorkflows` / `dbo.WorkflowRuns` / `dbo.TkSessions` / `dbo.TenantServiceAccounts` / quota / cache). Gitignored.

**Endpoint `/api/v1/workflows/*` giữ nguyên trên WEB** (worker không expose HTTP). "Chạy ngay" (`/run-now`) gọi `WorkflowSchedulerService.RunOneAsync` — Singleton đã đăng ký ở web nên vẫn chạy được dù web không có hosted tick.

Deploy: Windows Service via `sc.exe create` (mặc định), systemd + Docker documented. Xem [`TourkitAiProxy.Worker/README.md`](../../TourkitAiProxy.Worker/README.md).

**Khi thêm workflow mới:** implement `IScheduledWorkflow` + `AddSingleton<IScheduledWorkflow, X>()` **trong `WorkflowStackRegistration`** (không phải `Program.cs`) → worker + web tự pickup, không cần deploy 2 lần.

