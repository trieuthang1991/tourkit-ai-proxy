# Spec Đợt 1 — Insight Feed + Digest Engine + Subscriptions (S1 · C1 · O2 · S5)

**Ngày:** 2026-08-11 · **Nguồn:** [2026-08-11-ai-agent-personas-research.md](2026-08-11-ai-agent-personas-research.md) (roadmap đã kiểm chứng code + DB thật)
**Phạm vi:** F1 Insight Feed · F2 Digest Engine · F5 Digest Subscriptions → phát hành `sale-brief` (S1, kèm S5 vệ sinh pipeline) + `ceo-brief` (C1) + `payment-watchdog` (O2).

## Quyết định đã chốt với user

1. **Kênh gửi = hệ plugin, user tự cấu hình từng kênh**: In-app (mặc định) · Email (`OutboundMailWorker` phía toutkit-app ĐÃ tồn tại — chỉ enqueue + template) · Telegram (1 HTTP call, bot chung của hệ) · **Zalo OA** (per-tenant OA, có điều kiện — xem §6.4).
2. **O2 output = Feed + dòng trong bản tin.** KHÔNG ghi CRM ở Đợt 1 (blast radius thấp nhất).
3. **AI cost**: `sale-brief` render **rule thuần — 0 quota AI**; `ceo-brief` đúng **1 AI call/tenant/ngày** (số tính server-side, AI chỉ viết prose; AI fail → fallback render rule-based, KHÔNG mất bản tin).
4. Trần tự chủ giữ nguyên: digest là READ-only + gửi tới chính người đăng ký (không phải khách hàng) → không cần confirm-first.

## 1. Kiến trúc tổng

```
WorkflowSchedulerService (tick 60s — SẴN CÓ, không sửa)
  ├── SaleBriefWorkflow      (PerTenant, interval 60') ─┐
  ├── CeoBriefWorkflow       (PerTenant, interval 60') ─┼─→ DigestDispatcher ─→ IDigestChannel[]
  └── PaymentWatchdogWorkflow(PerTenant, interval cấu hình, default 24h)      ├── InAppChannel    → dbo.AgentInsights
                                                                              ├── EmailChannel    → dbo.OutboundMails (worker app-side gửi)
Data: service account (TenantServiceAccountStore SẴN) → TourKitApiClient      ├── TelegramChannel → api.telegram.org (bot chung)
      + repo nội bộ (DealRepository/ReviewRepository/TourQuoteRepository/     └── ZaloOaChannel   → OA API (per-tenant, best-effort)
      MailRepository)
Frontend: /insights (feed + badge chuông) + /digest (đăng ký + kênh + giờ gửi + gửi thử)
```

- **Giờ gửi KHÔNG sửa scheduler**: workflow brief chạy interval 60'; mỗi lần chạy chọn các subscription có `SendHourLocal == giờ VN hiện tại` và `LastSentLocalDate != hôm nay (VN)` → build + gửi. Chống trùng bằng `LastSentLocalDate`.
- Mọi fetch dữ liệu lẻ fail → dòng đó ghi "n/a", bản tin vẫn phát (không all-or-nothing).
- Đăng ký DI qua `WorkflowStackRegistration.AddWorkflowStack()` → web + `TourkitAiProxy.Worker` cùng pickup, không đụng `Program.cs`.

## 2. Schema mới (thêm vào `TourkitAiDb.SchemaSql` — idempotent; cập nhật `docs/database-schema.md`)

### 2.1 `dbo.AgentInsights` (F1)
| Cột | Kiểu | Ghi chú |
|---|---|---|
| Id | BIGINT IDENTITY PK | |
| TenantId | NVARCHAR(128) NOT NULL | |
| Username | NVARCHAR(256) NOT NULL DEFAULT '' | `''` = tenant-wide (mọi user thấy) |
| Kind | NVARCHAR(64) NOT NULL | `sale-brief` / `ceo-brief` / `payment-alert` |
| Severity | TINYINT NOT NULL DEFAULT 0 | 0 info · 1 warning · 2 critical |
| Title | NVARCHAR(512) | |
| Body | NVARCHAR(MAX) | markdown |
| DataJson | NVARCHAR(MAX) NULL | payload cấu trúc (tourId, dealIds, link…) |
| AlertKey | NVARCHAR(128) NULL | khóa dedup (vd `payment:{tourId}`) — trước khi tạo, skip nếu đã có cùng AlertKey trong 24h |
| IsRead | BIT NOT NULL DEFAULT 0 | |
| CreatedUtc | DATETIME2 NOT NULL | `SYSUTCDATETIME()` |

Index: `IX_AgentInsights_Tenant_User_Created (TenantId, Username, CreatedUtc DESC)`. Prune: xóa row > **90 ngày** (chạy cuối mỗi workflow run — pattern prune WorkflowRuns sẵn).

### 2.2 `dbo.DigestSubscriptions` (F5)
| Cột | Kiểu | Ghi chú |
|---|---|---|
| TenantId + Username + BriefType | PK composite | BriefType: `sale-brief` / `ceo-brief` |
| Enabled | BIT | |
| SendHourLocal | TINYINT DEFAULT 7 | giờ VN 0–23 |
| ChannelInApp | BIT DEFAULT 1 | |
| ChannelEmail | BIT DEFAULT 0 · Email NVARCHAR(256) NULL | user tự khai (CRM không expose email — gap #7 roadmap) |
| ChannelTelegram | BIT DEFAULT 0 · TelegramChatId NVARCHAR(64) NULL | |
| ChannelZalo | BIT DEFAULT 0 · ZaloUserId NVARCHAR(64) NULL | user id đã follow OA của tenant |
| LastSentUtc DATETIME2 NULL · LastSentLocalDate DATE NULL | chống gửi trùng trong ngày |
| CreatedUtc / UpdatedUtc | DATETIME2 | |

### 2.3 `dbo.TenantChannelSettings`
`(TenantId, Channel)` PK — Đợt 1 chỉ `Channel='zalo-oa'`, `ConfigJson` NVARCHAR(MAX) (access token + OA id, **field nhạy cảm Crypton-enc**), `UpdatedUtc`. Telegram bot token là config **server-level** (`Telegram:BotToken` trong appsettings, gitignored — cập nhật `appsettings.example.json`).

### 2.4 Sửa bảng cũ (additive, idempotent)
- `dbo.TkSessions` **thêm cột `CrmUserId INT NULL`** — decode từ JWT claim `user_id` lúc login/relogin (`TkSessionStore.CreateAsync`/`ReloginAsync`). Lý do: fetch bằng service account nhưng filter per-recipient cần CRM userId của người nhận; user đăng ký digest qua UI ⇒ chắc chắn có session ⇒ có CrmUserId.

## 3. F5 — Subscriptions & gate quyền

- `GET /api/v1/digest/subscriptions` — của user hiện tại (require `X-Session-Id`, pattern `RequireSession` sẵn).
- `PUT /api/v1/digest/subscriptions/{briefType}` — upsert config. **Gate: `briefType == 'ceo-brief'` → `EnsurePermissionsAsync` + `HasPermission(sessionId, CH_XEM_ALL)`**, thiếu quyền → 403 JSON tiếng Việt. Thêm const `TkPermissionCodes.XemToanBoCoHoi = "CH_XEM_ALL"`.
- `POST /api/v1/digest/subscriptions/{briefType}/test` — build + gửi NGAY tới các kênh đang bật của user (để user kiểm cấu hình). Đi qua đúng pipeline builder + dispatcher.
- `POST /api/v1/digest/telegram/detect` — server gọi `getUpdates` của bot, tìm tin nhắn gần nhất chứa mã ngắn user được cấp (hiện trên UI), trả `chatId`. Fallback: user dán chat id thủ công.
- `PUT /api/v1/digest/zalo-config` — per-tenant OA config (gate quyền `CH_HT_XEM` — cùng gate trang tích hợp sẵn có).

## 4. F2 — Digest Engine

### 4.1 Hợp đồng
```csharp
public record DigestMessage(string Title, string BodyMarkdown, string BodyHtml, string Kind, int Severity = 0);
public interface IDigestChannel {
    string Id { get; }                       // "inapp" | "email" | "telegram" | "zalo"
    Task<bool> SendAsync(DigestRecipient r, DigestMessage m, CancellationToken ct);  // false = fail (log Warning, KHÔNG throw)
}
public record DigestRecipient(string TenantId, string Username, DigestSubscription Sub);
```
`DigestDispatcher.SendAsync(sub, message)`: chạy tuần tự các kênh sub bật; kênh fail KHÔNG chặn kênh khác; kết quả per-channel ghi vào WorkflowRuns summary (`inapp ok, email ok, telegram FAIL`).

### 4.2 Kênh
- **InAppChannel**: INSERT `dbo.AgentInsights` (Kind = briefType).
- **EmailChannel**: enqueue `dbo.OutboundMails` `Kind='daily-brief'`, `TemplateCode='daily-brief'`, `ToEmail=sub.Email`, `[Params]={{title, bodyHtml, briefType, date}}`. Seed template `daily-brief` vào `dbo.MailTemplates` lúc startup (pattern seed `deal-cooling-alert` sẵn). Worker app-side gửi — ĐÃ tồn tại (`OutboundMailWorker.cs`, verify 11/08).
- **TelegramChannel**: `POST api.telegram.org/bot{token}/sendMessage` `{chat_id, text, parse_mode:"HTML"}` — escape HTML, cắt 4096 ký tự. Token rỗng → channel tự disable (log 1 lần).
- **ZaloOaChannel (best-effort, có điều kiện)**: gửi qua OA API tin tư vấn tới `ZaloUserId`. **Ràng buộc Zalo ghi thẳng vào UI**: tin miễn phí chỉ gửi được trong cửa sổ 48h sau khi user nhắn OA → digest hằng ngày có thể fail ngoài cửa sổ (log Warning, không fail run); nâng cấp ZNS (phí + duyệt template) là việc Đợt sau. Tenant chưa cấu hình OA → channel ẩn trên UI.

### 4.3 Ba workflow mới (`IScheduledWorkflow`, PerTenant, đăng ký trong `WorkflowStackRegistration`)

> **[SỬA 12/08/2026 — QUAN TRỌNG] Bản tin lấy dữ liệu bằng phiên CỦA CHÍNH NGƯỜI NHẬN, không phải
> tài khoản tự động.** Bản đầu ghi *"fetch 1 lần/tenant rồi filter per-recipient bằng CrmUserId"* —
> tức dùng token có `CH_XEM_ALL` (thấy deal của mọi người) rồi tự lọc. Cái duy nhất ngăn sale A đọc
> được deal của sale B khi đó là **đoạn code lọc của mình**; lọc sai một dòng là rò rỉ nội bộ, CRM
> không chặn giúp vì token vốn có quyền xem tất.
>
> Nay theo đúng quy tắc: **luồng theo người dùng thì chạy bằng tài khoản người dùng; luồng theo tổ
> chức mới dùng tài khoản hệ thống.** Bản tin là nội dung của từng người → fetch bằng
> `TkSessionStore.GetValidJwtAsync(sessionId của người nhận)`. **CRM tự áp quyền** — lọc sai cũng chỉ
> thiếu chứ không lộ. `ceo-brief` nhờ vậy KHÔNG cần re-check `CH_XEM_ALL` mỗi lần gửi nữa (CRM tự từ
> chối nếu quyền bị thu); vẫn giữ gate lúc ĐĂNG KÝ để chặn sớm.
>
> **Digest KHÔNG cần `TenantServiceAccounts`.** Điều kiện thay thế: người nhận phải từng đăng nhập ít
> nhất 1 lần. Nhẹ hơn tưởng — `TkSessions` giữ mật khẩu (Crypton-enc) và **tự đăng nhập lại** khi JWT
> hết hạn, giữ tới 30 ngày không dùng; không cần họ đang mở máy. Không tìm thấy phiên → bỏ qua người
> đó, ghi lý do vào summary ("chưa đăng nhập lần nào").
>
> **Chi tiết thi hành:** scheduler vẫn giữ **1 bản ghi PerTenant** (quản trị bật tính năng một lần),
> workflow tự duyệt `DigestSubscriptions` rồi **đổi sang phiên của từng người** để fetch. Nếu để
> `Scope = PerUser` thì mỗi người phải bật thêm một lần nữa ở trang Tự động hoá — hai chỗ cấu hình
> cho một việc, dễ nhầm. Cái "theo người dùng" nằm ở **tài khoản đi lấy dữ liệu**, không phải ở chỗ
> ai bấm nút bật.
>
> Đánh đổi đã chấp nhận: 20 người = ~100 lượt gọi CRM mỗi sáng thay vì ~5, rải trong khung giờ.
> `CrmUserId` VẪN cần: người có `CH_XEM_ALL` dùng token của chính mình vẫn thấy hết, phải lọc thêm
> để bản tin chỉ nêu việc của họ.
| Type | Interval | Điều kiện chạy nội dung |
|---|---|---|
| `sale-brief` | 60' cố định | subs due theo `SendHourLocal`/`LastSentLocalDate` |
| `ceo-brief` | 60' cố định | như trên + subscriber phải còn quyền (re-check `CH_XEM_ALL` mỗi lần gửi — quyền bị thu thì ngừng gửi) |
| `payment-watchdog` | user cấu hình, default 24h | luôn chạy khi đến hạn |

Chung: cần `TenantServiceAccounts` — chưa cấu hình → run status `skipped` + summary hướng dẫn (pattern deal-auto-review). Bọc `AiCallContext.Push(AiFeatures.Digest, tenantId)` quanh MỌI AI call (STRICT — const mới `AiFeatures.Digest = "digest"`). Log theo chuẩn workflow hiện hành (START/LOGIN/phase/FINISH + counters). DateTime: UTC + `Z`; giờ VN tính qua `TimeZoneInfo` (`SE Asia Standard Time`).

## 5. Nội dung bản tin (builders — pure, test được)

### 5.1 `SaleBriefBuilder` (rule thuần, 0 AI) — dữ liệu fetch 1 lần/tenant, filter per-recipient bằng `CrmUserId`/tên
1. **Deal cần gọi hôm nay** — từ `DealScores` + cooling (đã có logic): deal của user, đang mở, nguội ≥ ngưỡng → top 5, kèm WinRate + số ngày im lặng.
2. **Lịch hẹn hôm nay** — `/api/ai/appointments` `DateFilter=1`, lọc `Assignee` khớp user → giờ + tên khách.
3. **KH hạng A/B lâu không chăm** (mức thô) — `Reviews` rank A/B + không có booking mới X ngày (X=60 default) → top 5.
4. **Báo giá lâu chưa động** (mức thô) — `TourQuotes` của user (`CreatedBy`), `UpdatedAt` > N ngày (N=5) → top 5.
5. **Hộp thư công ty** (tenant-wide) — `MailRepository.Counts`: "còn {n} mail chưa xử lý ({m} hỏi giá)".
6. **S5 — vệ sinh pipeline** — deal mở của user thiếu ngày hẹn tiếp / kẹt 1 trạng thái > 14 ngày → tối đa 3 dòng nhắc.

Không có gì đáng nhắc → gửi bản tin ngắn "Hôm nay chưa có việc gấp 🎉" (vẫn gửi — giữ thói quen).

### 5.2 `CeoBriefBuilder` — số server-side + 1 AI call/tenant
- Số liệu: `financial-summary` + `cashflow` **2 kỳ** (MTD tháng này vs MTD tháng trước — reuse pattern compare sẵn), top-sellers MTD, đếm deal mới hôm qua (`booking-tickets` CreatedFrom/To), tổng cảnh báo `payment-alert` đang mở.
- AI: 1 call (provider default) — input là bảng số ĐÃ TÍNH, prompt yêu cầu 5–8 câu tiếng Việt, cấm bịa số ngoài input; qua `ScrubToolNames`-style guard nếu cần. AI lỗi/quota hết → render rule-based các số chính (bản tin không bao giờ mất).

### 5.3 `PaymentWatchdogRule` (O2 — rule thuần, 0 AI)
- Input: `/api/ai/tours` các tour `DepartureDate ∈ [hôm nay, +7 ngày]`, mọi status đang mở.
- Rule: `ActualRevenue < Revenue` (còn nợ > 0đ) → insight `payment-alert` tenant-wide, `AlertKey='payment:{tourId}'` (dedup 24h), severity: D≤3 → 2 (critical), còn lại 1. Body kèm: tour, khách, seller phụ trách, số còn thiếu (fmt VND), ngày khởi hành.
- Xuất hiện thêm 1 dòng tổng trong `sale-brief` (chỉ tour của user) + `ceo-brief` (tổng).

## 6. Frontend (tuân thủ đủ 3 chỗ: `index.html` + `bundle-entry.js` + `app.jsx`)

- **`pages/insights.jsx`** — route `/insights`: feed (mới→cũ, filter Kind/chưa đọc, phân trang), click mở chi tiết + đánh dấu đọc, nút "đọc tất cả". **Badge chuông** trên topbar (poll `unread-count` 60s) — thêm ở `app.jsx`.
- **`pages/digest.jsx`** — route `/digest` ("Bản tin AI"): 2 card `sale-brief`/`ceo-brief` (ceo-brief disable + tooltip nếu thiếu quyền), toggle kênh + nhập email/chat id/Zalo id, giờ gửi (dropdown 5h–20h), nút **"Gửi thử"**; khu cấu hình Zalo OA per-tenant (chỉ hiện với ai có `CH_HT_XEM`) kèm ghi chú ràng buộc 48h.
- Nav: "Bản tin AI" + "Thông báo" vào group phù hợp trong `app.jsx`. Dùng helper sẵn (`authedFetch`, `fmtAgo`, `readSSE` không cần — không SSE ở đây).

## 7. Endpoints tổng hợp (mới)

| Method | Path | Ghi chú |
|---|---|---|
| GET | `/api/v1/insights` | `?kind=&unread=&offset=&limit=` — của user + tenant-wide; require session |
| GET | `/api/v1/insights/unread-count` | badge |
| POST | `/api/v1/insights/{id}/read` · `/api/v1/insights/read-all` | idempotent |
| GET/PUT | `/api/v1/digest/subscriptions[/{briefType}]` | PUT gate `CH_XEM_ALL` cho ceo-brief |
| POST | `/api/v1/digest/subscriptions/{briefType}/test` | gửi thử qua pipeline thật |
| POST | `/api/v1/digest/telegram/detect` | tìm chatId qua getUpdates + mã ngắn |
| PUT | `/api/v1/digest/zalo-config` | per-tenant, gate `CH_HT_XEM`, ConfigJson Crypton-enc |

## 8. Bảo mật · chi phí · vận hành

- Secrets: Telegram bot token = appsettings (gitignore, cập nhật example); Zalo token Crypton-enc trong DB; KHÔNG log token/chat nội dung đầy đủ (chuẩn logging hiện hành).
- Quota: chỉ `ceo-brief` tiêu AI (≤ 1 call/tenant/ngày ≈ 30 call/tháng/tenant — không đáng kể so quota 1000); `sale-brief` + O2 = 0.
- Insight prune 90 ngày; WorkflowRuns prune sẵn có.
- Rollback: 3 workflow đều tắt được per-tenant qua UI `/workflows` (framework sẵn); bảng mới không đụng flow cũ.

## 9. Testing (TourkitAiProxy.Tests — pure logic)

1. `DigestDueTests` — chọn sub due theo `SendHourLocal` + `LastSentLocalDate` (múi giờ VN, đổi ngày, không gửi trùng).
2. `SaleBriefBuilderTests` — build sections từ data giả: filter đúng user, top-N, dòng "n/a" khi nguồn fail, bản tin rỗng → lời chúc.
3. `PaymentWatchdogRuleTests` — ngưỡng nợ, cửa sổ D-7, severity D≤3, AlertKey dedup.
4. `CeoBriefNumbersTests` — map 2 kỳ MTD → cấu trúc số cho AI prompt; fallback render khi AI fail.
5. `SubscriptionGateTests` — ceo-brief đòi `CH_XEM_ALL` (logic thuần quanh permissions list).
6. `TelegramFormatTests` — escape HTML, cắt 4096, số VND format.
7. `InsightRepositoryTests` — (nếu test được không cần SQL: tách query builder thuần) — tối thiểu test prune cutoff + AlertKey window logic thuần.

## 10. Ngoài phạm vi Đợt 1

`ops-brief` riêng (O1 — Đợt 2) · ZNS trả phí · email HTML cầu kỳ · admin đăng ký hộ · đọc bản tin qua TRAVAI (C5 — Đợt 2) · ghi CRM từ O2.
