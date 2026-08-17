# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ASP.NET Core 8 Minimal API that proxies multiple AI providers (OpenCode Go, 9routes) for the Tourkit frontend. Backend is organized by feature folders. Frontend (React via UMD + Babel standalone, no build step) lives in `wwwroot/` and is served by the same process — there is no separate frontend build pipeline.

Four features sit on top of the proxy: (1) the **tour-quote wizard** (the original AI proxy use-case), (2) **Customer Review** — AI-graded customer cards (rank A–D + action suggestions) generated single or in parallel batches with SSE progress, (3) **Chat-Analytics ("Trợ lý số liệu")** — a chat-left / data-right assistant where the AI picks which TourKit CRM API to call, fetches real numbers, and analyzes them, and (4) **SmartMail AI ("Hộp thư AI")** — a Gmail inbox synced on demand via IMAP (MailKit), AI-classified into 6 categories, with AI-drafted replies in 4 tones.

## Commands

```bash
# Setup lần đầu: copy template + điền key thật (file appsettings.json đang gitignore)
cp appsettings.example.json appsettings.json
# Sửa appsettings.json: thay REPLACE_WITH_OPENCODE_KEY + REPLACE_WITH_9ROUTES_KEY

# Run locally (binds http://localhost:5080 per Properties/launchSettings.json)
dotnet run --project TourkitAiProxy.csproj

# Build / publish
dotnet build TourkitAiProxy.csproj
dotnet publish TourkitAiProxy.csproj -c Release -o out

# Docker (exposes :8080 inside container)
docker build -t tourkit-ai-proxy .
docker run -p 5080:8080 -e Providers__OpenCode__ApiKey="sk-..." tourkit-ai-proxy

# Frontend bundle (prod mode — speedup ~70× cold start: 3-5s → 50ms)
# THƯỜNG KHÔNG CẦN CHẠY THỦ CÔNG — MSBuild target tự fire khi `dotnet publish -c Release`
.\build-frontend.ps1            # (Tùy chọn) bundle thủ công 1 lần
.\build-frontend.ps1 -Watch     # Watch mode dev — rebuild ~20ms/lần save, F5 thấy ngay
.\build-frontend.ps1 -Clean     # Xóa dist/ → quay về dev mode (Babel in-browser, hot reload)
```

Frontend có **2 mode auto-switch** qua tồn tại của `wwwroot/dist/app.bundle.js`:
- **Dev mode** (`dotnet run` Debug — DEFAULT): 35 file .jsx + Babel standalone → edit 1 file = F5 thấy ngay; cold start 3-5s. MSBuild target SKIP ở Debug.
- **Prod-bundle mode** (`dotnet publish -c Release` HOẶC `dotnet build -c Release`): MSBuild target `BuildFrontendBundle` trong [TourkitAiProxy.csproj](TourkitAiProxy.csproj) tự chạy `npx esbuild`, ghi `wwwroot/dist/app.bundle.js` (~470KB minified). `StaticFilesSetup.ServeIndex` detect dist/ → tự strip 35 `<script type="text/babel">` + Babel CDN + `babel-cache.js` + `lib/data.js`, inject 1 thẻ `<script src="dist/app.bundle.js?v=hash">`. Cold start ~50ms.
- **Incremental**: MSBuild compare mtime `wwwroot/**/*.jsx` vs `dist/app.bundle.js` → skip nếu bundle còn fresh (lần publish thứ 2 không thay đổi → bỏ qua esbuild ~3s).
- **Docker**: [Dockerfile](Dockerfile) đã install `nodejs` ở stage `build` → `dotnet publish` trong container chạy `npx esbuild` được.

**Khi cần dev nhanh với bundle**: `.\build-frontend.ps1 -Watch` (chạy song song `dotnet run`) — esbuild rebuild ~20ms/lần save, F5 thấy ngay. Hoặc `-Clean` để xóa dist/ về Babel mode (hot reload Babel nhanh hơn nhưng cold start chậm).

There is no test project. `appsettings.json` ở `.gitignore` (chứa API keys); commit `appsettings.example.json` làm template.

## Backend layout (folder-by-feature)

```
Program.cs                                 ← thin bootstrap: DI + UseX + MapX
Configuration/
  CorsSetup.cs                             ← AddTourkitCors() extension
  StaticFilesSetup.cs                      ← UseTourkitStaticFiles() — serves wwwroot
Models/
  Dtos.cs                                  ← CompleteRequest (flat shape, see below)
  ModelRegistry.cs                         ← (legacy, used by older endpoint helper code)
  CustomerModels.cs                        ← Customer, Metrics, CustomerListItem (review feature)
  ReviewModels.cs                          ← CustomerReview, BatchJob/BatchEvent, Feedback DTOs
  ChatModels.cs                            ← LoginToken/LoginCred req+resp, Chat req/reply, ChatData (Chat-Analytics)
Services/
  UpstreamParser.cs                        ← Parse Anthropic + OpenAI shapes
  UsageTracker.cs                          ← in-memory singleton, lock-based
  OpenCodeClient.cs                        ← shared upstream helpers (DefaultSystem const)
  Providers/
    IAiProvider.cs                         ← interface: Id, Label, Models, Complete, Stream
    ProviderRegistry.cs                    ← resolve by id, default from Providers:Default
    OpenCodeProvider.cs                    ← OpenCode Go (dual-protocol Anthropic + OpenAI)
    NineRoutesProvider.cs                  ← 9routes (OpenAI-compat local router)
    OpenAIProvider.cs                      ← ChatGPT (api.openai.com) — key from req.ApiKey (client) → config fallback
    AnthropicProvider.cs                   ← Claude (api.anthropic.com/v1/messages) — same key resolution
    ProviderKeyStore.cs                    ← config/env key reader (fallback only; client key sent per-request wins)
  Reviews/                                 ← Customer Review feature (see section below)
    CustomerRepository.cs                  ← read-only loader for data/customers.seed.json
    ReviewRepository.cs                    ← file-backed KV store (data/reviews.json), lock + fingerprint
    ReviewService.cs                       ← fingerprint cache → dispatch IReviewAgent → save (NO prompt/parse here)
    BatchService.cs                        ← Parallel.ForEachAsync (cap 10) → BatchJob.Events channel
    BatchJobStore.cs                       ← in-memory ConcurrentDictionary of running jobs
    Agents/
      IReviewAgent.cs                      ← strategy contract: Supports(providerId) + RunAsync(...)
      ReviewPrompt.cs                      ← shared SYSTEM_PROMPT + user prompts + tool schema + tolerant parser (1 nguồn)
      NativeToolReviewAgent.cs             ← Anthropic native function-calling (submit_customer_review schema enforce)
      JsonPromptReviewAgent.cs             ← fallback prompt-JSON + tolerant parse (mọi provider khác)
  Workflow/
    AnthropicToolsClient.cs                ← reusable agentic loop (max 5 iter, terminal tool detect) — share Review/Visa/Deal/Tour/Mail
    NativeToolScorer.cs                    ← thin wrapper RunAsync<T> cho service single-shot (Visa/Deal/Tour/Mail)
    WorkflowTrace.cs + Accessor + Log      ← debug trace per-request (?debug=1) → JSONL audit
  Security/
    Crypton.cs                             ← AES-256/CBC — VERBATIM port of TourKit.Shared/Crypton.cs (token decrypt)
  Json/
    LooseJson.cs                           ← extract first balanced {…} from AI output (shared helper)
  TourKit/
    TourKitApiClient.cs                    ← calls TourKit.Api: login + authed GET, unwraps {success,data,…}
    TkSessionStore.cs                      ← sessions persisted to dbo.TkSessions (pwd Crypton-encrypted, JWT not persisted); in-mem cache + write-through SQL → cross-process share; auto re-login on JWT soft-expire (50min)/401; idle prune 30 ngày
    TkSessionRepository.cs                 ← Dapper CRUD cho dbo.TkSessions (1 nguồn cho persistence)
  Chat/
    ChatTools.cs                           ← tool catalog (read-only TourKit GET endpoints) + dispatch
    ChatAgentService.cs                    ← planner → CRM fetch → server-side stats → analysis (Chat-Analytics)
  Mail/                                    ← SmartMail AI feature (see section below)
    MailTaxonomy.cs                        ← 6 category / 4 status / 4 tone maps (Việt) + chuẩn hóa
    MailAccountStore.cs                    ← creds Gmail: dbo.MailAccounts per-tenant (App Password Crypton-enc); KHÔNG fallback config/env
    IMailSource.cs                         ← interface nguồn mail (để sau cắm OAuth)
    MailMapper.cs                          ← pure: MimeMessage → MailItem (test được)
    GmailImapClient.cs                     ← IMailSource qua IMAP Gmail (MailKit), incremental theo UID + \Seen→IsRead
    MailSyncStore.cs                       ← state đồng bộ dbo.MailSyncState per-tenant (per-address uidValidity+lastUid)
    IMailSender.cs + GmailSmtpClient.cs    ← gửi (trả lời + soạn mới) qua SMTP Gmail (587, App Password), thread qua In-Reply-To
    MailRepository.cs                      ← DB-backed dbo.Mails per-tenant (PK TenantId,Id) + Filter/Counts (diacritics-insensitive)
    MailClassifier.cs                      ← classify qua `Models:MailClassify` (DeepSeek qua nine-routes) — chỉ JSON-prompt, không native tool; prompt lấy ĐỊNH NGHĨA nhóm + LUẬT GỠ HOÀ từ MailTaxonomy, Temperature=0
    MailReplyService.cs                    ← soạn nháp theo tone + chỉ thị NV (stream)
Endpoints/
  SystemEndpoints.cs                       ← GET /healthz
  AiEndpoints.cs                           ← All /api/v1/* AI routes + /api/ai/* legacy aliases
  ReviewEndpoints.cs                       ← /api/v1/customers/* + /api/v1/reviews/* (review feature)
  ChatEndpoints.cs                         ← /api/v1/login-token + /login + GET /session + /chat + /chat/stream (Chat-Analytics)
  MailEndpoints.cs                         ← /api/v1/mail/* (SmartMail AI: account/sync/list/draft-SSE/status)
data/
  customers.seed.json                      ← seed customer list (replace with CRM/DB in prod)
  reviews.json                             ← persisted reviews (gitignored-ish runtime state)
  tk-sessions.json.migrated                ← (chỉ tồn tại sau migration) — file legacy đã import vào SQL, rename để khỏi re-run; an toàn xóa
  visa-files/{tenantId}/{assessmentId}/    ← Visa attachments per-tenant (gitignored runtime state)
  # Mail/Visa JSON stores đã migrate sang SQL Server (xem multi-tenant fix 2026-06-09):
  #   data/mails.json          → dbo.Mails           (composite PK TenantId,Id)
  #   data/mail-account.json   → dbo.MailAccounts    (per-tenant)
  #   data/mail-sync.json      → dbo.MailSyncState   (per-tenant)
  #   data/visa-assessments.json → dbo.VisaAssessments (per-tenant)
```

**Database schema** — 25 bảng SQL Server (cùng instance với TourKit Push, conn string `ConnectionStrings:PushDb` thường ENC: Crypton). Full inventory + conventions + checklist thêm bảng mới: **[docs/database-schema.md](docs/database-schema.md)**. Schema sống trong [Services/Db/TourkitAiDb.cs](Services/Db/TourkitAiDb.cs) (`SchemaSql` const, idempotent `IF OBJECT_ID(...) IS NULL`). Khi thêm/sửa bảng → update cả file MD đó.

**Adding a new provider** (e.g. OpenAI direct, Anthropic direct, Ollama local):
1. Implement `IAiProvider` in `Services/Providers/MyProvider.cs`.
2. `builder.Services.AddSingleton<IAiProvider, MyProvider>();` in `Program.cs`.
3. Read API key from `Providers:MyProvider:ApiKey` in `appsettings.json` (or env var). Never echo keys.
4. `/api/v1/providers` auto-includes the new entry — no frontend table edit needed.

## API surface (versioned + RESTful)

| Method | Path                              | Notes                                                |
|--------|-----------------------------------|------------------------------------------------------|
| GET    | `/healthz`                        | k8s-style liveness probe                             |
| GET    | `/api/v1/providers`               | list providers + models + `needsKey` flag (openai/anthropic = BYO key) |
| GET    | `/api/v1/models`                  | flat models list across all providers                |
| GET    | `/api/v1/usage`                   | UsageTracker snapshot                                |
| POST   | `/api/v1/completions`             | buffered completion                                  |
| POST   | `/api/v1/completions/stream`      | SSE stream                                           |
| GET    | `/api/v1/customers`               | list + filter (`segment`, `search`, `lastDays`); each item carries review `status` (none/fresh/stale) |
| GET    | `/api/v1/customers/{id}`          | `{ customer, review }`                               |
| POST   | `/api/v1/reviews/customer/{id}`   | sync review 1 customer; body optional `{forceFresh?, provider?, model?, apiKey?}` — 3 override sau cho A/B test 2 path |
| POST   | `/api/v1/reviews/customer/{id}/refresh` | alias `forceFresh=true`; cũng nhận `{provider?, model?, apiKey?}` để refresh bằng provider khác |
| POST   | `/api/v1/reviews/batch`           | start batch job; body `{customerIds[], forceFresh?, provider?, model?, apiKey?}` (max 200 ids; 3 override apply cho TẤT CẢ KH trong batch) → `{jobId, total, streamUrl, cancelUrl}` |
| GET    | `/api/v1/reviews/batch/{jobId}/stream` | SSE progress; job auto-removed on completion   |
| POST   | `/api/v1/reviews/batch/{jobId}/cancel` | cancel a running batch                          |
| POST   | `/api/v1/reviews/{customerId}/feedback` | thumbs `helpful`/`not_helpful` + note          |
| GET    | `/api/v1/mail/account`            | SmartMail: trạng thái cấu hình hộp thư `{address, configured}` |
| POST   | `/api/v1/mail/account`            | lưu creds Gmail `{address, appPassword}` (App Password mã hóa Crypton) |
| POST   | `/api/v1/mail/sync`               | IMAP kéo ≤30 thư mới nhất, phân loại email MỚI, lưu → `{items, counts, classified}` |
| GET    | `/api/v1/mail`                    | list + filter (`status`, `category`, `search`) + counts |
| GET    | `/api/v1/mail/{id}`               | chi tiết 1 email                                     |
| POST   | `/api/v1/mail/{id}/read`          | đánh dấu đã đọc (khi mở email)                        |
| POST   | `/api/v1/mail/refresh-content`    | đọc lại nội dung thư ĐÃ CÓ từ IMAP `?max=` → `{items, counts, refreshed, fetched}`; giữ nhóm/trạng thái/nháp, KHÔNG tốn lượt AI |
| POST   | `/api/v1/mail/{id}/reclassify`    | phân loại lại 1 email đã lưu → `{ok, before, after, summary, changed}`; giữ nguyên trạng thái xử lý/nháp |
| POST   | `/api/v1/mail/{id}/reply/draft`   | SSE: stream nháp trả lời theo `{tone, instruction}`  |
| POST   | `/api/v1/mail/{id}/reply/send`    | gửi nháp (đã sửa) cho khách qua SMTP Gmail → status `da_phan_hoi` |
| POST   | `/api/v1/mail/compose/draft`      | SSE: AI soạn email MỚI từ `{to, subject, brief, tone}` |
| POST   | `/api/v1/mail/compose/send`       | gửi email mới qua SMTP `{to, subject, text}`         |
| PATCH  | `/api/v1/mail/{id}/status`        | đổi trạng thái email (moi/dang_xu_ly/da_phan_hoi/da_dong) |
| GET    | `/api/v1/features`                | Tính năng nào đang mở → `{digest}` (KHÔNG cần đăng nhập; giao diện đọc để ẩn phần chưa ra mắt) |
| GET    | `/api/v1/insights`                | Bảng tin trong app `?kind=&unread=&offset=&limit=` → `{items[…]}`; item bản tin (sale/ceo) kèm `speakText` (đã bỏ markdown/emoji để đọc TTS) (require X-Session-Id) |
| GET    | `/api/v1/insights/unread-count`   | Số chưa đọc cho badge chuông `?kind=` → `{count}` (require X-Session-Id) |
| POST   | `/api/v1/insights/{id}/read`      | Đánh dấu 1 dòng đã đọc — repo kẹp theo tenant/user nên id của công ty khác không đánh dấu được |
| POST   | `/api/v1/insights/read-all`       | Đánh dấu đã đọc tất cả |
| GET    | `/api/v1/digest/subscriptions`    | Đăng ký nhận bản tin của chính mình → `{items[…], briefTypes[…], scopeNote}` (require X-Session-Id) |
| PUT    | `/api/v1/digest/subscriptions/{briefType}` | Lưu đăng ký (`sale-brief`\|`ceo-brief`) — validate: loại lạ → 400, bật mà 0 kênh → 400, bật kênh mà trống nơi nhận → 400; giờ rác kẹp về 7h; CỐ Ý không đụng mốc "đã gửi hôm nay" |
| POST   | `/api/v1/digest/subscriptions/{briefType}/test` | Gửi thử NGAY qua đúng đường gửi thật → `{ok, summary, sentChannels}`; CỐ Ý không cập nhật mốc "đã gửi" (không thì bản tin thật sáng mai bị bỏ) |
| POST   | `/api/v1/digest/telegram/detect`   | Tự tìm chat id: trả mã `TK-xxxxxx` (gắn theo PHIÊN) → user nhắn mã cho bot → gọi lại để lấy `chatId`. Lỗi mạng Telegram → 502 kèm gợi ý tự dán, KHÔNG 500 |
| POST   | `/api/v1/assistant/action/execute` | Thực thi 1 hành động trợ lý đã xác nhận `{actionId, action, params, provider?, model?}` → `{action, message, data?, warning?}` (require X-Session-Id) — idempotent theo `actionId` (double-confirm không gửi/enqueue trùng); hành động `assign_task`/`create_appointment` chỉ **enqueue** `dbo.CrmActionQueue`, KHÔNG POST thẳng CRM |
| POST   | `/api/v1/admin/auth/login`        | Admin login `{username,password}` → `{token,username,expiresAt}` |
| POST   | `/api/v1/admin/auth/logout`       | header `X-Admin-Session` → `{ok}` |
| GET    | `/api/v1/admin/auth/me`           | header `X-Admin-Session` → `{username,expiresAt}` |
| GET    | `/api/v1/admin/ui/ai-usage`       | cross-tenant AI usage `?days=30&tenantId=` (require X-Admin-Session) |
| GET    | `/api/v1/admin/ui/quota`          | list quota mọi tenant `{items[{tenantId, displayName, limit, used, remaining, usedPct, warn, exhausted, updatedAtUtc}]}` (require X-Admin-Session) |
| POST   | `/api/v1/admin/ui/quota/{tenant}/topup` | cộng `{amount: 1..100000}` lượt cho tenant → snapshot mới (require X-Admin-Session) |
| GET    | `/api/v1/admin/ui/digest`         | Theo dõi bản tin xuyên tenant `?tenantId=&briefType=&problemsOnly=` → `{items[{tenantId,tenantName,username,briefType,enabled,sendHourLocal,channelsEnabled,channelsSentToday,channelsMissing,sentAttempts,lastSentUtc,scheduleEnabled,pausedReason,problem}], totals{all,enabled,problems,scheduleOff,tenants}}` — `problem` do server tính, nói thẳng nguyên nhân gốc (require X-Admin-Session) |
| GET    | `/api/v1/admin/ui/consult-leads`  | đăng ký tư vấn từ landing `?status=all|pending|contacted` → `{items[…], totals{all,pending,contacted}}` (require X-Admin-Session) |
| POST   | `/api/v1/admin/ui/consult-leads/{id}/contacted` | đánh dấu lead đã/chưa liên hệ `{contacted:bool}` — lưu vào side-car `data/consult-leads-status.json`, KHÔNG sửa JSONL gốc (require X-Admin-Session) |
| GET    | `/api/v1/admin/ui/chat-unresolved` | "AI bí câu hỏi" — Chat-Analytics unresolved log `?days=1..90&tag=` → `{items[{ts,tag,tenantId,tenantName,question,toolChosen,plannerRaw,aiReplyPreview,provider,model,iterations,latencyMs,tokensIn,tokensOut,history[]}], totals{<tagName>:count, all}}` (max 500 entries, require X-Admin-Session) |
| GET    | `/api/v1/admin/ui/tk-sessions`    | "Phiên đăng nhập" — list TourKit sessions in-mem cache `{items[{id,tenantId,username,fullName,companyName,lastUsedUtc,idleSeconds,chatTurns,lastTool,hasJwt}], total}` (KHÔNG hit SQL; require X-Admin-Session) |
| DELETE | `/api/v1/admin/ui/tk-sessions/{id}` | kick 1 phiên (xóa cache + SQL) → `{ok, kicked, by}` — user sẽ phải đăng nhập lại lần dùng tiếp theo (require X-Admin-Session) |
| GET    | `/api/v1/workflows`               | list workflow catalog + config hiện tại `{items[{type,label,description,scope,enabled,intervalMinutes,consecutiveFailures,pausedReason,nextRunUtc,lastRunUtc,lastRunStatus,lastRunSummary}]}` (require X-Session-Id) |
| PUT    | `/api/v1/workflows/{type}`        | upsert config `{enabled, intervalMinutes}` — khi `enabled=true` & `pausedReason!=null` → reset failures + clear pausedReason (require X-Session-Id) |
| POST   | `/api/v1/workflows/{type}/run-now` | chạy ngay 1 lần — **CHẠY NỀN**, trả về ngay khi đã khởi động; `summary` LUÔN rỗng ở response này, kết quả xem ở `/runs` (require X-Session-Id) |
| GET    | `/api/v1/workflows/{type}/runs`   | lịch sử run `?limit=20` → `{items[{id,triggerKind,startedUtc,finishedUtc,status,summary,error,durationMs}]}` (require X-Session-Id) |
| POST   | `/api/v1/workflows/service-account` | Lưu tài khoản tự động per-tenant `{username,password,domain?}` — **validate login TourKit + đếm deal** trước khi lưu (Crypton-enc) → `{ok, dealsVisible, warning?}`; login fail → `{ok:false,error}` (require X-Session-Id) |
| GET    | `/api/v1/workflows/service-account` | Trạng thái cấu hình `{configured, username}` (KHÔNG trả password) (require X-Session-Id) |
| DELETE | `/api/v1/workflows/service-account` | Xóa tài khoản tự động → workflow ngừng tự login → `{ok, removed}` (require X-Session-Id) |
| GET    | `/api/v1/workflows/outbound-mails` | Theo dõi hàng đợi gửi `?kind=&status=&channel=&limit=50` → `{items[{id,kind,sourceId,templateCode,toEmail,subject,channel(0=email/1=telegram/2=zalo),status(int),retryCount,errorMessage,scheduledUtc,createdUtc,processedUtc}]}` (require X-Session-Id) |
| GET    | `/api/v1/workflows/crm-queue`     | Theo dõi hàng đợi hành động CRM (giao việc/lịch hẹn) từ trợ lý `?kind=&status=&limit=50` → `{items[{id,tenantId,username,kind,payloadJson,status(int),resultJson,retryCount,errorMessage,createdUtc,processedUtc}]}` (require X-Session-Id) — chỉ ĐỌC, `Status`/`ResultJson` do worker app-side ghi |

**Tenant scoping** (multi-tenant fix 2026-06-09): tất cả endpoint `/api/v1/mail/*` và `/api/v1/visa/*` YÊU CẦU `X-Session-Id` header (hoặc `sessionId` query/body) — backend resolve `TenantId` qua `ITenantContext`/`HttpTenantContext` từ `TkSessionStore`. KHÔNG session → 401. Cross-tenant access (resource thuộc tenant khác) → null/404.

**Legacy aliases** (`POST /api/ai/complete`, `POST /api/ai/stream`, `GET /api/ai/models`, `GET /api/ai/usage`) point to the same handlers — keep until all clients migrate.

**Request shape** (`CompleteRequest` — flat, NOT OpenAI `messages[]`):
```json
{ "prompt": "...", "provider": "opencode-go", "model": "deepseek-v4-flash",
  "maxTokens": 8192, "temperature": 0.3, "system": "optional override" }
```
- `provider` blank → falls back to `Providers:Default` in config, then first registered.
- `system` blank → backend injects anti-reasoning prompt (see `OpenCodeClient.DefaultSystem`).
- `temperature` default `0.3` (tuned for JSON/structured output).
- `apiKey` optional: legacy per-request channel (DTO still accepts it for backward compat). **As of v9 (`CONFIG_VERSION` in `ai-provider.jsx`), the frontend NO LONGER stores or sends keys.** All keys come from server: `ProviderKeyStore.Get(id)` resolves `Providers:{X}:ApiKey` → `Models:Primary:ApiKey` (if `Models:Primary:Provider==id`) → env var. Old `localStorage["tourkit_ai_keys"]` is auto-cleared on first load by the v8→v9 migration.

**Response shape (`/completions`):**
```json
{ "text": "...", "provider": "opencode-go", "model": "deepseek-v4-flash",
  "latencyMs": 1234, "inputTokens": 100, "outputTokens": 50,
  "finishReason": "stop", "attempts": 1, "warning": null, "rawUpstream": null }
```

**SSE shape (`/completions/stream`)**: a series of `data: {"delta":"..."}` events followed by terminal `data: {"done":true, text, provider, model, latencyMs, inputTokens, outputTokens, finishReason}`; on error the server emits `data: {"error":"...", status?, body?}` then `data: {"done":true}` — client must treat `error` as terminal.

## Provider details

**OpenCode Go** uses two upstream paths depending on model id:
- `minimax-m2.5` / `minimax-m2.7` → `zen/go/v1/messages`, Anthropic format. Requires BOTH `anthropic-version: 2023-06-01` AND `x-api-key` headers (bearer-only is rejected). Stream events: `message_start` / `content_block_delta` / `message_delta`.
- Everything else → `zen/go/v1/chat/completions`, OpenAI format. Streaming uses `stream_options.include_usage=true` for token counts on the final chunk. Response content falls back to `reasoning_content` / `reasoning` for DeepSeek-style models. `stop_reason: max_tokens` is normalized to `finishReason: "length"` so both paths surface OpenAI-style values.

**Retry policy in `OpenCodeProvider.CompleteAsync` (non-streaming only):**
1. *Transient retries* (up to 2): on network exception, 408, 429, or 5xx, exponential backoff (500ms × 2ⁿ on exception, 1000ms × 2ⁿ on HTTP status).
2. *Budget bump* (up to 1): if upstream returns empty `text` AND `finishReason == "length"`, double `maxTokens` (cap 16384) and retry — catches reasoning models that spend the entire budget on hidden thinking. If still empty, returns `{text: "", warning, rawUpstream}` for client-side debugging.

Streaming has NO retry.

**9routes** is an OpenAI-compatible local router (default `http://localhost:20128/v1`). Quirk: non-stream calls sometimes return SSE-formatted body — `NineRoutesProvider.ParseResponse` detects `data:` prefix and walks chunks before falling back to plain JSON.

## Native function-calling (Anthropic) — dual-path scoring

5 single-shot AI feature (Customer Review / Visa / Deal / Tour Builder / Mail Classify) đều có **2 path**:

| Provider hiện hành | Path chạy | Output enforce |
|--------------------|-----------|----------------|
| `anthropic` (`Providers:Default=anthropic`) | NATIVE function-calling: AI gọi terminal tool (`submit_*`) với JSON Schema enforce | Schema validate type/enum/required → 0% leak markdown/thinking |
| `opencode-go` / `nine-routes` / `openai` (default hiện tại) | JSON-prompt: AI in JSON ra text + tolerant parse + retry x1 | Legacy — phụ thuộc prompt discipline |

**Switch path:** đổi `appsettings.json` → `"Providers": { "Default": "anthropic" }` + nhập `"Anthropic": { "ApiKey": "sk-ant-..." }` (hoặc env `ANTHROPIC_API_KEY`). Trace sẽ hiện `path_dispatch: native-tool` thay vì `json-prompt`. **No breaking change** khi giữ default cũ — JSON path vẫn chạy như trước.

**Shared infrastructure (`Services/Workflow/`):**
- **`AnthropicToolsClient`** — agentic loop tổng quát cho `api.anthropic.com/v1/messages` với `tools[]`. Max 5 iter, terminal tool detection (dừng khi AI gọi `submit_*`), wall-clock 60s, tự ghi trace cho mỗi iter + tool dispatch. Trả `ToolsResult { TerminalInput, Iterations, TokensIn/Out, Latency, Warning }`. Reusable cho mọi feature single-shot HOẶC multi-step.
- **`NativeToolScorer.RunAsync<T>(systemPrompt, userPrompt, schema, terminalToolName, parser, apiKey, model, maxTokens, trace)`** — thin wrapper cho score-like service: resolve apiKey (override → `ProviderKeyStore` fallback), gọi `AnthropicToolsClient`, throw nếu terminal null, parse → `T`, ghi `AiUsageLog`. `BuildAnthropicTool(name, description, properties, required[])` helper để khỏi nhớ shape `{name, description, input_schema:{type,properties,required}}`.

**2 routing pattern:**
1. **Strategy pattern (Customer Review)** — `IReviewAgent` interface + 2 class (`NativeToolReviewAgent`, `JsonPromptReviewAgent`). Đăng ký `IEnumerable<IReviewAgent>` ở DI (NativeTool TRƯỚC, Json SAU — thứ tự quan trọng). `ReviewService` resolve agent đầu tiên `Supports(defaultProviderId)`. Áp dụng khi schema rich + có thể mở rộng (vd Mức C multi-step augmentation).
2. **In-service routing (Visa / Deal / Tour / Mail)** — `ScoreAsync` top: `if provider.Id == "anthropic" → ScoreWithNativeToolAsync; else → ScoreWithJsonPromptAsync`. Đơn giản hơn, không cần interface. Áp dụng khi schema nhỏ + ít kịch bản mở rộng.

**Tool schema convention:** `submit_<entity>_<action>` (vd `submit_visa_score`, `submit_tour_draft`). Properties với `type` + `enum` + `description`; nullable dùng `type: ["string", "null"]` (JSON Schema 2020-12, Anthropic accepts). `required[]` chỉ list field BẮT BUỘC có — optional field có thể omit hoặc null. Parser dùng chung helper case-insensitive lookup từ `ReviewPrompt.ParseElement` hoặc local `TryGet/Str/Int/StrList`.

**Tradeoffs:**
- Native: 0% format error, dùng được haiku rẻ (vd Mail Classifier), không cần retry. Phụ thuộc API có function-calling (chỉ Anthropic, sau này thêm OpenAI Responses).
- JSON: chạy mọi provider (kể cả reasoning model), nhưng ~5-10% trả format xấu → retry x1.

## Customer Review feature

AI grades a customer (rank A–D, alert level, strengths/concerns, action-now + 30-day ideas, product suggestions) and persists the result. Flows through `ReviewEndpoints` → `ReviewService` → dispatch tới `IReviewAgent` → `ReviewRepository`.

- **Storage is file-backed, not a DB.** Customers are read-only from `data/customers.seed.json` (`CustomerRepository`, loaded once at startup). Reviews persist to `data/reviews.json` (`ReviewRepository`, lock-guarded, camelCase JSON to match the JS frontend). Both are explicitly MVP placeholders — swap for EF/Dapper/SQLite to scale. `reviews.json` is mutable runtime state.
- **Caching via data fingerprint.** `ReviewRepository.FingerprintFor(customer)` is a SHA-256 (first 32 hex) of the canonical customer JSON. `ReviewService.ReviewAsync` returns the cached review (no AI call) when the stored `DataFingerprint` matches and `forceFresh` is false. The customer-list endpoint reports `fresh`/`stale`/`none` by comparing fingerprints.
- **Strategy pattern dispatch.** `ReviewService` chỉ orchestrate (fingerprint check + Save) — KHÔNG hold prompt/parse logic nữa. Dispatch tới `IReviewAgent` đầu tiên `Supports(defaultProviderId)`. Xem section "Native function-calling" ở trên cho dual-path. Cả 2 agent dùng chung `ReviewPrompt.SYSTEM_*`, `BuildUserPrompt*`, `ParseElement`, `Compose` → 1 nguồn schema, không drift.
- **Buffered, not streamed, to the model.** Cả 2 agent đều dùng buffered call (Json: `CompleteAsync`; Native: `AnthropicToolsClient.RunAsync` returns sau khi terminal tool gọi). DeepSeek/Kimi reasoning models interleave `reasoning_content` với `content`, streaming sẽ mix prose vào JSON. `onStage` callback (`preparing` → `calling` → `parsing`) cho UI lifecycle.
- **Defaults (JSON path):** `Resolve(null)` default provider, `maxTokens: 8000`, `temperature: 0.4`, tour-operator system prompt ở `ReviewPrompt.SystemForJsonPrompt`. **Defaults (Native path):** `claude-sonnet-4-5`, `maxTokens: 4000` (schema enforce nên không leak → 4000 đủ). Đổi ngành = sửa `ReviewPrompt.SYSTEM_*` + `RankingCriteria` const.
- **Batch is parallel + SSE.** `BatchService.Start` is fire-and-forget; `Parallel.ForEachAsync` runs up to `CONCURRENCY = 10` reviews, pushing `BatchEvent`s into the job's `Channel`. The SSE endpoint drains that channel to the client and removes the job when done. `BatchJobStore` is in-memory only — jobs are lost on restart and clients must re-trigger. Cancel via the cancel endpoint or by closing the SSE connection.

## Chat-Analytics feature ("Trợ lý số liệu")

A chat-left / data-right assistant. The user asks in natural language; the AI decides which **TourKit CRM API** (the `toutkit-app` backend, NOT the Google-Doc CRM) to call, the proxy fetches real data, computes numbers server-side, and the AI writes the analysis. Flow lives in `ChatEndpoints` → `ChatAgentService`.

- **Upstream is TourKit.Api's dedicated AI surface `/api/ai/*`** (`D:\MiGroup\tourkitapp\toutkit-app\TourKit.Api\Controllers\AiController.cs` + `docs/ai-api-guide.md`). Host via config `TourKit:BaseUrl` (the AI surface must be deployed there — prod `mobile-api.tourkit.vn` did NOT have it as of last check; staging `mobile-test-api-2.tourkit.vn` did). Every `/api/ai/{section}` returns a **uniform envelope** `{section,title,count,total,period,summary,items[]}` (b-wrapped in `{success,data,message}`); items carry `value`+`*Formatted` and codes carry `*Name`/`*Label`/`statusText` (Vietnamese, server-formatted). `TourKitApiClient.GetAsync` unwraps `data` (the envelope); throws `TourKitApiException` on `success:false`/non-2xx.
- **Auth = token-decrypt, NOT api-key.** TourKit.Api uses JWT (`POST /api/auth/login` with `{tenantId, username, password}`). The client doesn't store credentials in config. Instead: `POST /api/v1/login-token {token}` where `token = Crypton.Encrypt(JSON {username,password,domain})`. `Crypton` is a **verbatim port** of `TourKit.Shared/Crypton.cs` (AES-256/CBC, `PassPhrase="Pas5pr@se"`, `Salt="s@1tValue"`, `IV="@1B2c3D4e5F6g7H8"`, `PasswordDeriveBytes`/SHA1/iterations=2) — DO NOT change the constants or tokens won't decrypt. `domain` maps to TenantId. The proxy logs in, creates a server-side session (`TkSessionStore`), and returns only a `sessionId` — **the JWT never reaches the client**. Sessions hold the decrypted creds to silently re-login on JWT expiry or a 401 (one retry in `ChatAgentService`). **Sessions persist to SQL `dbo.TkSessions`** (password Crypton-encrypted, JWT NOT persisted — re-login on first use sau restart) → cross-process share giữa nhiều instance, survives restart/deploy mà user khỏi login lại; in-mem cache cho hot path Get, write-through SQL mọi mutation. Soft-TTL JWT ~50min, idle prune sau 30 ngày. File legacy `data/tk-sessions.json` auto-migrate vào SQL ở startup (one-shot, rename `.migrated`).
- **Single-shot agent, no native function-calling.** `ChatAgentService.AskAsync` (buffered) / `AskStreamAsync` (SSE): (1) planner prompt with the `ChatTools` catalog → AI returns `{tool, params}` JSON (parsed via `LooseJson`); (2) dispatch to a `/api/ai/{section}` GET (`ChatTools.BuildPath` whitelists params; `ResolveMarketAsync` turns `marketName`→`marketId`); (3) **`BuildChatData`** maps the envelope → `ChatData` (items→Raw for table/chart, `summary`+`total`→stat cards, `title`); financial-summary's items become the stat cards. (4) analysis prompt → AI prose. Two AI calls; both have provider-fallback to the default provider on upstream/key failure.
- **Streaming + caches.** `AskStreamAsync` emits SSE events `{stage}` (planning→fetching→analyzing, data attached early) then `{delta}` (token-streamed analysis) then `{done}`. **SSE payloads MUST be serialized camelCase** (`SseJson = new(JsonSerializerDefaults.Web)` in `ChatEndpoints`) to match the client — default PascalCase silently breaks `data.stats`/`title`/`raw`. Caching via `Services/Cache/ChatCache.cs`: **CHỈ CRM-data** (`d|{tenant}|{path}`), TTL 30m, values as JSON. **KHÔNG cache câu trả lời AI** — `r1|`/`r2|` đã bị **gỡ bỏ hẳn 2026-08-11**: key của chúng (câu hỏi, hoặc tool+params) không bao giờ bắt đủ mọi chiều quyết định câu trả lời (câu chữ + ngữ cảnh hội thoại + focus doanh thu/chi phí + ý so sánh + model), đã gây **3 bug "trả lời cũ" liên tiếp** (ý so sánh `ca2d68f`; ngữ cảnh hội thoại; focus). `d|` giữ lại vì key của nó — tenant + đường dẫn API — xác định **trọn vẹn** dữ liệu trả về. Đừng thêm lại cache câu trả lời: xem `docs/test-plans/2026-08-11-chat-e2e-question-bank.md` và bộ E2E `scripts/e2e/specs/`. **Backend = Redis if `Redis:ConnectionString` is set (shared across instances + survives restart), else in-memory fallback.** The connection string may be `ENC:`-encrypted (copied verbatim from TourKit.Api) — `ChatCache` decrypts it with `Crypton` at runtime; keys are prefixed `tkai:` to avoid colliding with TourKit's own Redis keys; `AbortOnConnectFail=false` so a down Redis never blocks startup. **Never cache empty results** (`HasContent`/`IsUsableData`) or a transient empty poisons the path for 30m.
- **Tools are read-only `/api/ai/*` sections** (financial-summary, cashflow, marketing, departures, top-customers, top-sellers, tours, booking-tickets, tasks, customers, appointments, vouchers, notifications) + `list_markets` (still `/api/tours/markets` for the resolver). Add a tool = add one `ChatTool` entry in `ChatTools.All`. Discovery endpoints `/api/ai/catalog` + `/api/ai/reference` exist upstream (not yet wired into the proxy). Write endpoints excluded.
- **Name→id resolver (controlled multi-step).** Some filters need an id the user only knows by name (e.g. market "Nội địa miền Nam"). The planner fills a `marketName` param; `ChatAgentService.ResolveMarketAsync` looks it up against the tenant's market list (`GET /api/tours/markets`, cached 6h per tenant) and rewrites it to `marketId` before the call. `MatchMarket` normalizes (lowercase, strip Vietnamese diacritics, đ→d, drop punctuation, token-subset) so "Nội địa miền Nam" matches "Nội địa - Miền Nam". Customer-by-market questions route to `list_booking_tickets` (carries `MarketId`), since `/api/customers` has no market filter.
- **Caching + heuristic fallback.** Chỉ CRM-data caching, delegated to `ChatCache` (`d|…` keys — Redis-backed when configured, so NOT lost on restart; câu trả lời AI KHÔNG cache, xem "Streaming + caches" ở trên). `ChatAgentService`'s only own cache is `_markets` (the 6h-per-tenant market-resolver list). The fallback `HeuristicRoute` keyword-routes when the planner emits non-JSON (reasoning models sometimes do), so a clear data question never silently returns "none".
- **Endpoints:** `POST /api/v1/login-token` (`{token}` → `{sessionId, tenantId, fullName, companyName, expiresAt}`), `POST /api/v1/chat` + `POST /api/v1/chat/stream` (`{messages, sessionId?, provider?, model?}`; sessionId may also come via `X-Session-Id` header → `{reply, toolName, data:{kind,title,raw,stats[]}, …}`; the `/stream` variant emits the SSE `{stage}`/`{delta}`/`{done}` sequence), `GET /api/v1/session` (validate the current sessionId).
- **Login UX:** two modes on `/assistant` — a direct form (`POST /api/v1/login {username,password,domain}`, server-side login, no client-side crypto) and the encrypted-token paste (`/login-token`). Both return a `sessionId`.
- **Frontend:** `wwwroot/pages/assistant.jsx` (route `/assistant`). Stores `sessionId` in `localStorage["tourkit_tk_session"]`, renders chat on the left and on the right: `data.stats` cards + a **Chart.js** chart + a generic table. Chart.js is loaded via CDN `<script>` in `index.html` (no build step); `ChartView` picks horizontal bars for categorical data and vertical grouped bars for time-series, with a metric-toggle (Doanh thu/Chi phí/Lợi nhuận). `ChatData.Focus` (derived in `ChatAgentService.DetectFocus` from question keywords like "chi phí"→`expense`) restricts the chart/table/stats to the requested metric. Money formatted with `fmtVND`.

### Trợ lý hành động (action tools, 2026-07-14)

Ngoài đọc số liệu, `/assistant` và `/travai` (JARVIS voice) giờ có thêm **ACTION tools** (ghi/thao
tác) song song `ChatTools` (read-only): `check_mail`, `send_mail_reply`, `compose_mail`,
`review_customer`, `prepare_meeting`, `score_deal`, `assign_task`, `create_appointment` — catalog 1 nguồn ở
[`Services/Chat/ActionTools.cs`](Services/Chat/ActionTools.cs). Hành động hướng ra ngoài/khó undo
(gửi mail, giao việc, tạo lịch hẹn) là **confirm-first**: planner phát `ActionProposal` (thẻ xác
nhận, field sửa được) → user bấm "Xác nhận" → FE gọi `POST /api/v1/assistant/action/execute` →
[`ActionExecutor`](Services/Chat/ActionExecutor.cs) re-resolve + re-check tenant server-side rồi
thực thi, idempotent theo `actionId`. `review_customer`/`prepare_meeting`/`score_deal`/`check_mail`
(đọc/non-destructive với 1 thực thể) chạy thẳng không cần xác nhận. Tên → id (nhân viên/khách hàng/deal)
resolve qua [`Services/Chat/ActionResolver.cs`](Services/Chat/ActionResolver.cs) (mơ hồ → hỏi lại, không đoán).

**`prepare_meeting` — "thẻ chuẩn bị gặp khách" (S4, 2026-08-14).** Gom hồ sơ + lịch sử mua + nhật ký
chăm sóc + hạng đã chấm (`dbo.Reviews`) + thư gần nhất CỦA CHÍNH khách đó → AI viết "khách này là ai /
nên nói gì / cần tránh gì" ([`MeetingBriefService`](Services/Chat/MeetingBriefService.cs)). Bốn quyết
định cố ý, đừng "sửa": (1) **theo yêu cầu, KHÔNG phải workflow nền** — spec gợi ý "trước lịch hẹn X giờ"
nhưng làm nền thì tốn 1 lượt AI cho MỌI cuộc hẹn, kể cả cuộc chẳng ai cần chuẩn bị; (2) **không lưu kết
quả** — khác `review_customer` (bản chấm hạng là dữ liệu dùng lại, worker sync xuống CRM), thẻ chuẩn bị
chỉ đúng cho cuộc gặp sắp tới, lưu lại thì lần sau đọc phải bản cũ mà tưởng mới; (3) **thư khớp theo
EMAIL, không theo tên** — trùng tên là chuyện thường, đưa nhầm thư của người khác vào thẻ thì nhân viên
nói sai chuyện ngay trước mặt khách; (4) **lời AI về khung chat, dữ kiện thô về panel phải** (`ChatData.Kind
= "meeting-brief"`) — in cả hai chỗ là đọc hai lần cùng một thứ. AI hỏng → vẫn trả dữ kiện thô kèm câu
nói rõ là chưa có gợi ý.
**Ghi vào CRM (`assign_task`/`create_appointment`) chỉ ENQUEUE** vào
[`dbo.CrmActionQueue`](Services/Crm/CrmActionQueueRepository.cs) — proxy KHÔNG POST thẳng
`/api/tasks`/`/api/customer-care`; worker phía `toutkit-app` (viết sau) drain hàng đợi + sync CRM
theo hợp đồng ở [docs/crm-action-contract/README.md](docs/crm-action-contract/README.md). Endpoint
routing ở [`Endpoints/AssistantActionEndpoints.cs`](Endpoints/AssistantActionEndpoints.cs); theo
dõi hàng đợi qua `GET /api/v1/workflows/crm-queue`. Thiết kế đầy đủ:
[docs/superpowers/specs/2026-07-14-assistant-action-tools-design.md](docs/superpowers/specs/2026-07-14-assistant-action-tools-design.md).

## SmartMail AI feature ("Hộp thư AI")

Gmail inbox synced on demand, AI-classified, with AI-drafted replies. Flow lives in `MailEndpoints` → `Services/Mail/*`. Design doc: `docs/smartmail-ai-design.md`; implementation plan: `docs/superpowers/plans/2026-06-05-smartmail-ai.md`.

- **Source = Gmail IMAP via MailKit, NOT OAuth.** `GmailImapClient` (implements `IMailSource`) connects `imap.gmail.com:993` read-only with an **App Password** (requires Gmail 2-Step Verification + IMAP enabled). The interface keeps OAuth swappable later. Creds resolved by `MailAccountStore`: DB-backed `dbo.MailAccounts` per-tenant (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI per tenant. KHÔNG còn fallback config/env (đã drop từ commit multi-tenant fix 2026-06-09).
- **Sync is on-demand (Refresh button), not a background poller.** `POST /mail/sync` is **incremental theo UID** (`MailSyncStore` lưu `dbo.MailSyncState` per-tenant per-address `{uidValidity, lastUid}`): chỉ kéo email có UID > lần trước → KHÔNG sót dù >N email mới giữa 2 lần sync. Lần đầu/khi UidValidity đổi → kéo `max` (30) mới nhất. Cờ `\Seen` của Gmail map sang `IsRead` lúc kéo. Vẫn **classify chỉ email MỚI** (`repo.Has(id)` skip → tiết kiệm token). Email id = Message-Id (MimeKit chuẩn hóa/tự sinh), fallback `{address}:{uid}`.
- **Đọc/chưa đọc:** `POST /mail/{id}/read` đánh dấu đã đọc khi mở; `MailCounts.Unread` cho badge. Frontend in đậm + chấm cam dòng chưa đọc.
- **Thư CHUYỂN TIẾP DẠNG ĐÍNH KÈM** (sửa 14/08): `msg.HtmlBody`/`msg.TextBody` của MimeKit chỉ trả
  phần VỎ ngoài. Gmail bấm "Chuyển tiếp" thì chèn nội tuyến (không sao), nhưng Outlook + nhiều app
  doanh nghiệp đính kèm thư gốc dạng `message/rfc822` → vỏ rỗng → **mở lên trắng trơn**. `MailMapper`
  nay duyệt đệ quy `MessagePart` (chặn 5 lớp / 10 thư) và ghép nội dung bên trong kèm dòng phân cách.
  ⚠️ Chỉ áp dụng LÚC BÓC thư — thư cũ đã lưu Body/BodyHtml theo bản cũ, `reclassify` KHÔNG chữa được
  (nó chỉ chạy lại AI trên body đã lưu). Chữa bằng `POST /api/v1/mail/refresh-content` (dưới).
- **Đính kèm: BÁO TÊN, chưa tải được file.** `MailMapper` ghép dòng `📎 Tệp đính kèm: …` vào CHÍNH
  thân thư (cả text lẫn HTML) — cố ý KHÔNG thêm cột vào `dbo.Mails` (bảng cũ). Gom cả tệp của thư
  lồng bên trong, vì thư chuyển tiếp thường đính kèm ở lớp trong. **Bỏ qua phần `inline`** (logo chữ
  ký, ảnh `cid:`) — liệt kê cả logo thì gần như mọi email công ty đều hiện "image001.png", nhiễu tới
  mức lúc có tệp thật không ai để ý. Tên tệp do người ngoài đặt nên **phải escape** trước khi nhét
  vào HTML. Tải/mở file vẫn là Phase 2.
- **Phân loại: định nghĩa + luật gỡ hoà, KHÔNG chỉ tên nhóm** (sửa 14/08). Prompt cũ liệt kê trần
  `- spam: Spam` → thư máy-gửi (không phải khách, cũng không phải quảng cáo) kẹt giữa `spam`/`khac`,
  mỗi lần chọn một kiểu: soát 1.215 thư thật thấy `Thông báo có công việc mới được giao` rải **143
  `spam` / 52 `khac` / 41 `xac_nhan`**. Nay `MailTaxonomy.CategoryHints` + `MailTaxonomy.TieBreakRules`
  là 1 nguồn, nhúng vào prompt; `Temperature` = **0**. ⚠️ **Luật phải phân biệt theo MỤC ĐÍCH thư, KHÔNG
  theo người gửi** — bản đầu viết "máy gửi từ dịch vụ đang dùng → không bao giờ spam" thì **quảng cáo
  Grab cũng thoát khỏi spam**, tức là nhóm `spam` rỗng dần trong im lặng. Sửa lớp này thì phải đo **cả
  hai chiều** (thông báo rời spam VÀ quảng cáo ở lại spam), đo một chiều sẽ kết luận sai.
- **`POST /mail/refresh-content`** — kéo lại N thư mới nhất từ IMAP (`ignoreCursor: true` → bỏ mốc
  UID) và **ghi đè Body/BodyHtml** cho thư ĐÃ có. Cần vì phần bóc thư chỉ chạy lúc kéo về, nên mọi bản
  sửa `MailMapper` đều không tự áp dụng cho thư đang nằm trong hộp — mà đó đúng là thư người dùng đang
  nhìn. `MailSyncService.MergeForContentRefresh` (pure, có test) **giữ nguyên** `Category`/`AiSummary`/
  `Status`/`Draft`/`IsRead`/`AutoReplyError`: đè chúng đi là nhân viên mất nháp viết dở và thư đang xử
  lý bị đẩy về "mới" — tệ hơn cái đang định chữa. KHÔNG gọi AI → **0 lượt**. Chạy thật trên staging:
  30 thư, nhóm/trạng thái không đổi một dòng nào.
- **`POST /mail/{id}/reclassify`** — phân loại chỉ chạy MỘT LẦN lúc kéo thư về, nên sửa classifier xong
  thư cũ vẫn giữ nhãn sai vĩnh viễn. Endpoint này chạy lại cho 1 thư, **giữ nguyên** `Status`/`Draft`/
  `IsRead` (đẩy thư đang xử lý về "mới" là mất việc đang làm dở). CỐ Ý không có bản chạy hàng loạt —
  mỗi thư tốn 1 lượt AI.
- **Soạn thư MỚI:** `POST /mail/compose/draft` (SSE, AI viết từ `brief`) + `/mail/compose/send` (gửi tới người nhận bất kỳ) — `MailReplyService.ComposeNewStreamAsync` + `IMailSender.SendAsync`. Chữ ký công ty (`MailAccountStore.Signature()`, cấu hình ở UI per-tenant, lưu trong `dbo.MailAccounts`) được dệt vào prompt soạn.
- **Classification + reply reuse `ProviderRegistry`.** `MailClassifier.ClassifyAsync` (buffered, dual-path — xem "Native function-calling" section: Anthropic → `submit_mail_classification` tool với Haiku; else → JSON-prompt) → `{category, summary}`; 6 categories normalized to a known set (lạ → `khac`); lỗi cả 2 path → `("khac", "")` để mail vẫn lưu. `MailReplyService.DraftStreamAsync` streams a tone-aware draft (4 tones) + staff instruction via `provider.StreamAsync`, saves the draft + flips status → `dang_xu_ly`. Both client AI prefs (`provider`/`model`/`apiKey`) flow through like the other features.
- **Sending = SMTP Gmail (`IMailSender`/`GmailSmtpClient`).** `POST /mail/{id}/reply/send` gửi nội dung (đã sửa) tới người gửi gốc qua `smtp.gmail.com:587` STARTTLS bằng chính App Password — gửi AS the company Gmail, nên KHÔNG dính SPF/DKIM/spam như giả mạo domain. Gắn `In-Reply-To`/`References` để vào đúng luồng. Gửi xong → lưu nội dung + status `da_phan_hoi`. Frontend confirm trước khi gửi.
- **Storage = SQL Server `dbo.Mails`** per-tenant scoped (`MailRepository`, composite PK `(TenantId, Id)`, index `IX_Mails_Tenant_Received` cho list/sort). Cross-tenant access trả null/404. KHÔNG fallback file — DB lỗi → 503.
- **Taxonomy** (`MailTaxonomy`, single source): categories `hoi_dat_tour|xin_bao_gia|khieu_nai|xac_nhan|spam|khac`, statuses `moi|dang_xu_ly|da_phan_hoi|da_dong`, tones `lich_su|than_thien|dam_phan|xin_loi` — all with Vietnamese labels.
- **Frontend:** `wwwroot/pages/mail.jsx` (route `/mail`), 3-column (filters / list / detail+compose). A built-in **config form** (`GET`/`POST /mail/account`) lets staff paste Gmail address + App Password to test without editing JSON. Draft uses the same SSE `{delta}`/`{done}` reader as `assistant.jsx`. Statuses/categories color-coded via CSS.
- **Phase 2 (deferred):** 2-way sync (write `\Seen` back / mirror deletes), incremental UID fetch (hiện kéo 30 mới nhất/lần), OAuth source, assign-to-staff ("Của tôi"), attachments.
- **Tests:** `TourkitAiProxy.Tests` (xUnit, project nằm trong thư mục con → main csproj `<Compile Remove="TourkitAiProxy.Tests/**" />`). Covers pure logic only: `MailTaxonomy`, `MailMapper`, `MailClassifier.ParseClassification`, `MailRepository`. Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`. IMAP/frontend verified manually. (This is the repo's first test project — the rest of the codebase still has none.)

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
- **MailSyncService (extract):** logic `POST /mail/sync` được extract ra `Services/Mail/MailSyncService.cs` → dùng chung giữa HTTP endpoint và `MailAutoSyncWorkflow`. Response shape `/mail/sync` giữ nguyên (`{items, counts, classified, fetched}`).
- **Endpoint:** require `X-Session-Id` (pattern giống MailEndpoints). Manual trigger (`/run-now`) **fire-and-forget, KHÔNG đồng bộ** — trả về sau ~100ms với `summary` rỗng, workflow chạy tiếp ở nền qua pipeline scheduler (vẫn đếm failure + auto-pause + ghi `WorkflowRuns`). ⚠️ Đừng "sửa" lại thành đồng bộ: đã từng đồng bộ và run dài 100s+ làm request trình duyệt timeout → user thấy báo **lỗi giả** dù workflow chạy xong bình thường. Kết quả đọc ở `GET /workflows/{type}/runs`, không đọc ở response của `/run-now`.
- **Frontend:** `/workflows` page (`wwwroot/pages/workflows.jsx`), card per workflow + toggle + interval dropdown + run history collapsible. Nav entry "Tự động hóa" trong group "Bản tin & Tự động" (chuyển khỏi "Tích hợp" 12/08 vì trang có thêm phần cá nhân — xem section "Bản tin AI").

## Bản tin AI ("Đợt 1" — bản tin sáng + Bảng tin)

Bản tin chủ động gửi mỗi sáng, thay vì bắt người dùng tự vào hỏi. Spec + plan đầy đủ:
[specs/2026-08-11-dot1-digest-insight-design.md](docs/superpowers/specs/2026-08-11-dot1-digest-insight-design.md) ·
[plans/2026-08-11-dot1-digest-insight.md](docs/superpowers/plans/2026-08-11-dot1-digest-insight.md).

⚠️ **CẢ CỤM NÀY NẰM SAU CỜ `Features:Digest` — mặc định TẮT** (chưa ra mắt; thiếu key = tắt, cố ý sai
theo hướng an toàn). Một cờ cho cả 3 tác vụ `sale-brief` · `ceo-brief` · `payment-watchdog` + Bảng tin,
vì với người dùng chúng là MỘT tính năng: cả 3 đều ghi vào Bảng tin và Bảng tin là chỗ đọc lại.
Bật: `appsettings.json` → `"Features": { "Digest": true }` **ở CẢ web lẫn worker** (worker mới là nơi
thật sự chạy tác vụ nền — web tắt mà worker bật thì bản tin vẫn gửi cho khách dù giao diện đã ẩn sạch),
rồi restart. Tắt thì: 3 workflow không đăng ký DI ([`WorkflowStackRegistration`](Services/Bootstrap/WorkflowStackRegistration.cs))
→ biến mất khỏi scheduler + `GET /api/v1/workflows` → thẻ tự mất khỏi trang; `/api/v1/insights|digest/*`
trả 404 tường minh; chuông + tab Bảng tin + khối Zalo OA + mục admin "Bản tin" bị ẩn qua
[`GET /api/v1/features`](Endpoints/SystemEndpoints.cs) → [`window.tourkitFeatures`](wwwroot/core/features.js).
**Không xoá dữ liệu** — `dbo.DigestSubscriptions`/`dbo.UserWorkflows` giữ nguyên, bật lại là còn đủ.
Cờ này KHÁC phân quyền: tắt là tắt cho tất cả, kể cả admin.

> **Bẫy đã dính 1 lần:** không map endpoint ≠ 404. `app.MapFallback` (SPA deep-link) nuốt mọi đường dẫn
> không khớp kể cả `/api/**` và trả `index.html` **status 200** → client gọi API nhận HTML thay vì lỗi.
> Vì thế nhánh `else` trong [Program.cs](Program.cs) phải map tay 2 tiền tố về 404 JSON.

**2 loại bản tin** (`BriefTypes`): `sale-brief` — việc cần làm của từng nhân viên bán hàng (cơ hội cần
gọi, lịch hẹn, việc, báo giá, tour còn thiếu tiền) — **số do máy chủ lấy, AI sắp xếp lại cho gọn** (tốn 1 lượt/người/ngày, tắt bằng tuỳ chọn `useAi=false`; AI lỗi → rơi về bản rule); `ceo-brief` — doanh thu/chi
phí/lợi nhuận so cùng kỳ, **AI chỉ viết lời còn số do máy chủ tính**, AI lỗi → in bảng số
([`CeoBriefBuilder.RenderFallback`](Services/Digest/CeoBriefBuilder.cs)).

**Cách chạy — CHUẨN BỊ TRƯỚC, GỬI QUA HÀNG ĐỢI** (đổi 13/08, xem
[plan](docs/superpowers/plans/2026-08-13-digest-queue-pipeline.md)). Cả 2 là `PerTenant` (1 bản ghi
scheduler, bật 1 lần) nhưng workflow **tự đổi phiên theo từng người nhận**; workflow KHÔNG gửi gì cả:

1. **PREPARE** — từ mốc `giờ người chọn − Digest:LeadMinutes` (mặc định 10') trở đi, workflow dựng nội
   dung ([`DigestDue.ShouldPrepare`](Services/Digest/DigestDue.cs) — so theo **phút**, mở tới hết ngày VN).
2. **GHI Bảng tin** — `dbo.AgentInsights`. Đây là kênh "trong app" **luôn bật** (kho lưu để xem/nghe lại).
3. **ENQUEUE** — mỗi kênh ngoài đang bật = 1 dòng `dbo.OutboundMails` với `ScheduledUtc`
   ([`DigestEnqueuePlanner`](Services/Digest/DigestEnqueuePlanner.cs) + [`DigestDue.SendMomentUtc`](Services/Digest/DigestDue.cs)).
   Dòng mang theo **đủ thứ cần để gửi**: email → `Params`; telegram/zalo → `Data` chứa nơi nhận +
   `title` + `body`.
4. **GỬI** — **KHÔNG phải việc của proxy.** Cả 3 kênh do **`TourKit.PushWorker` bên toutkit-app** rút
   (`PushNotification.Worker/OutboundQueueWorker.cs`, nhịp 30s).

⚠️ **MỘT hàng đợi, MỘT nơi tiêu thụ** (sửa 14/08). Trước đó proxy có bộ rút riêng cho telegram/zalo →
hai tiến trình cùng poll một bảng, và cái nhanh hơn nuốt mất dòng của cái kia: worker mail (30s) vớ dòng
telegram, không thấy email nên đánh dấu `Status=4` "thiếu người nhận"; bộ rút proxy chỉ tìm `Status=0`
nên **không bao giờ thấy nữa** → bản tin Telegram biến mất im lặng. Nay proxy chỉ XẾP hàng đợi.
Đừng thêm bộ rút thứ hai ở đây.

Chống dựng trùng trong ngày = `InsightRepository.ExistsTodayAsync` (không còn `LastSentLocalDate`).
Vì "đã dựng" đọc từ Bảng tin và mốc gửi nằm trên dòng hàng đợi, **máy chủ sập đúng khung giờ không còn
làm mất bản tin của ngày** — bật lại là dựng/gửi bù. Điều kiện: người đó đã từng đăng nhập
(`dbo.TkSessions` giữ mật khẩu mã hoá + tự re-login, 30 ngày).

**Ba trạng thái kết thúc** (worker quyết, xem `IOutboundChannelSender`): gửi được → `Status=1`; hỏng mà
thử lại vô ích (thiếu nơi nhận, công ty chưa khai OA, Zalo hết cửa sổ 48h) → `Status=4` + lý do; hỏng
tạm thời (mạng, nhà cung cấp 5xx) → tăng `RetryCount`, hết lượt (`OutboundMail:MaxRetries`, mặc định 3)
mới thành `Status=2`.

**`Telegram:BotToken` — BẮT BUỘC ở worker, gần như không cần ở proxy.** Worker cần để gửi bản tin thật.
Proxy chỉ còn dùng cho MỘT tiện ích: `POST /digest/telegram/detect` (quét `getUpdates` để tìm chat id
giúp người dùng). Thiếu ở proxy thì endpoint đó trả 503 kèm gợi ý tự dán chat id — không ảnh hưởng gì
tới bản tin.

⚠️ **Thứ tự deploy:** `TourKit.PushWorker` (bản có adapter kênh) phải lên **TRƯỚC**, proxy bật
`Features:Digest` **SAU** — worker cũ không biết cột `Channel`, vớ dòng telegram/zalo rồi đánh dấu
"thiếu email người nhận" là mất tin.

⚠️ **Fetch bằng phiên CỦA NGƯỜI NHẬN, KHÔNG dùng service account** — đây là quyết định có chủ đích:
service account có quyền xem toàn công ty, lọc sai 1 dòng là nhân viên A đọc được cơ hội của nhân viên B.
Dùng token của chính họ thì **CRM tự chặn** → lọc sai chỉ thiếu, không lộ. Vì thế proxy cũng **KHÔNG tự
gác quyền** khi đăng ký: `DashboardService.ResolveSpUserIdAsync` (TourKit.Api) chỉ cho "xem tất cả" khi
tài khoản có `BC_NV_XEM`, còn lại SP tự lọc về số của riêng họ; và proxy không truyền `userId` —
`AiController.GetClaims()` bóc từ JWT.

**Nơi nhận — 1 kho lưu + 3 kênh gửi.** "Trong app" (`dbo.AgentInsights`) **KHÔNG phải kênh gửi** mà là
**kho lưu luôn bật**: bản tin ghi vào đó lúc dựng, trước khi nghĩ tới chuyện gửi đi đâu — nên mọi kênh
ngoài hỏng hết thì vẫn còn chỗ xem/nghe lại. Server ép `ChannelInApp=true` khi lưu đăng ký; UI khoá ô
tick. 3 kênh gửi thật ([`Services/Digest/Channels/`](Services/Digest/Channels/)): email (`TemplateCode=daily-brief`,
worker toutkit-app gửi) · Telegram (bot DÙNG CHUNG `Telegram:BotToken` — miễn phí nên hệ thống cấp) ·
Zalo (**ZNS — nhắn theo SỐ ĐIỆN THOẠI**, qua **OA RIÊNG của từng công ty**, khai ở mục "Theo tổ
chức" — xem 3 điều dễ hiểu sai bên dưới). Một kênh hỏng KHÔNG làm chết kênh còn lại.

**Nơi nhận khai MỘT LẦN cho mọi thông báo** (17/08). `dbo.DigestSubscriptions` vốn đã là *một dòng
mỗi người* (PK `TenantId+Username`) và giữ sẵn email/chat id/số Zalo — nên nó **là** hồ sơ nơi nhận
dùng chung, chỉ từng bị đặt tên và đặt vị trí như thể của riêng bản tin sáng. Nay tách rõ: khối
**"Nơi nhận của tôi"** ([`MyChannelsBlock`](wwwroot/pages/digest.jsx)) đứng đầu mục "Theo người dùng",
lưu qua endpoint RIÊNG `PUT /api/v1/digest/my-channels` — **không đụng** `BriefType`/`Enabled`/
`SendHourLocal`. Gộp vào endpoint đăng ký thì mỗi lần đổi email lại phải gửi kèm loại bản tin + giờ
nhận, client quên một trường là **âm thầm tắt đăng ký của chính người đó**. Cảnh báo cấp công ty
(vd `payment-watchdog`) đọc cùng hồ sơ này qua `ListWithChannelsAsync` — **không** lọc theo `Enabled`
(cờ đó nói về bản tin sáng; một người có thể không nhận bản tin nhưng vẫn muốn nhận cảnh báo).
KHÔNG thêm bảng mới cho việc này.

⚠️ **Zalo: 3 điều dễ hiểu sai** (đổi 14/08 — trước đó code dùng API `message/cs` theo Zalo user id):
1. **Nơi nhận là SỐ ĐIỆN THOẠI**, không phải Zalo user id. Người dùng chỉ nhập số của mình; server
   chuẩn hoá về `0xxxxxxxxx` ngay lúc lưu ([`DigestPhone`](Services/Digest/DigestPhone.cs)), worker đổi
   sang `84…` lúc gọi API. Cột DB tên `ZaloPhone` (đổi từ `ZaloUserId`
   ngày 14/08 bằng `sp_rename` trong `SchemaSql` — tên cột nói sai nội dung là bẫy cho người sau).
2. **ZNS KHÔNG gửi được chữ tự do** — chỉ điền tham số vào mẫu đã được Zalo duyệt. Nên tin Zalo là
   **lời nhắc ngắn**; bản tin đầy đủ đọc ở Bảng tin (kênh trong app luôn bật nên chắc chắn có).
3. **OA RIÊNG từng công ty** (quay lại per-tenant 17/08 — bản 14/08 từng gỡ để dùng OA chung).
   Lý do đảo quyết định: đi gặp khách hàng thì **không công ty nào chịu dùng OA chung** — tin ZNS
   hiện **tên OA người gửi**, nên gửi bằng OA của bên cung cấp dịch vụ nghĩa là khách của họ nhận
   tin mang tên một công ty khác. Lập luận cũ ("bắt mỗi công ty tự đăng ký mẫu thì họ bỏ dở") tính
   đúng chi phí khai báo nhưng bỏ qua chuyện thương hiệu, mà đó mới là thứ quyết định.
   - 3 endpoint `/api/v1/digest/zalo-config` (GET/PUT/DELETE) **khôi phục**, gác `CH_HT_XEM`;
     lưu ở `dbo.TenantChannelSettings` (`Channel='zalo'`) qua
     [`TenantChannelSettingsStore`](Services/Digest/TenantChannelSettingsStore.cs). Giao diện nằm
     **cùng thẻ với tài khoản dịch vụ** trong mục "Theo tổ chức" — cả hai đều là thông tin đăng
     nhập cấp công ty, khai một lần.
   - **Hai chế độ, MỘT đường code — `mode` chỉ là nhãn.** Cả `own` lẫn `provided` khai CÙNG bộ
     `oaId` + `appId` + `secretKey` + `refreshTokenSeed`; khác nhau duy nhất ở chỗ giá trị từ đâu ra:
     `own` công ty tự đăng ký OA, `provided` bên cung cấp dịch vụ **đưa sẵn thông tin OA hệ thống**
     để công ty khỏi phải đi đăng ký — họ vẫn dán vào đúng những ô đó, không phải "khỏi nhập gì".
     Vì thế phần gửi không tách nhánh theo `mode`; nhãn chỉ đổi lời hướng dẫn trên màn hình và cho
     biết đang gửi dưới tên OA của ai.
     ⚠️ `refreshTokenSeed` **bắt buộc**: App ID + Secret không lấy được token — Zalo đổi refresh
     token lấy access token, mà refresh token đầu chỉ có sau bước cấp quyền OA. Thiếu nó thì công ty
     khai xong tưởng chạy được, worker không bao giờ lấy nổi token.
     **KHÔNG có đường rơi ngầm**: chưa khai đủ → kênh Zalo không gửi và nói thẳng, tuyệt đối không
     lặng lẽ gửi bằng danh nghĩa đơn vị khác.
   - **Mã mẫu ZNS khai theo TỪNG CHỨC NĂNG** (`sale-brief` · `ceo-brief` · `payment-alert`): Zalo
     duyệt mẫu theo nội dung nên bản tin sáng và nhắc thu tiền là hai mẫu khác nhau. Danh sách 1
     nguồn ở `DigestEndpoints.ZaloTemplateFeatures` — thêm chức năng gửi Zalo mới = thêm 1 dòng,
     giao diện tự mọc ô nhập. Mã mẫu được **đính kèm ngay trên dòng hàng đợi** (`Data.templateId`)
     chứ không bắt worker tự tra: worker đọc bảng của proxy càng ít càng tốt, và lúc gửi mới tra
     thì mẫu có thể đã đổi so với lúc dựng nội dung.
   - ⚠️ **Lưu là HỢP NHẤT, không ghi đè cả cục.** `ConfigJson` có hai chủ: phần khai tay (giao diện)
     và `refreshToken`/`accessToken` do **worker xoay vòng** ghi lại. Ghi đè trọn gói từ giao diện
     sẽ xoá token worker vừa làm mới → kênh Zalo chết ngay sau lần lưu cấu hình kế tiếp mà không
     lỗi nào hiện lên. Bí mật gửi lên rỗng = **giữ nguyên** bản đang lưu (giao diện không đọc lại
     được bí mật nên không thể gửi lại).

⚠️ **Proxy KHÔNG có lớp gửi nào** (gỡ 14/08: `IDigestChannel`, `DigestDispatcher`, 3 lớp kênh,
`TelegramFormat`). Kể cả nút **"Gửi thử"** cũng chỉ **xếp hàng đợi** bằng CHÍNH `DigestEnqueuePlanner`
mà workflow dùng mỗi sáng. Trước đó gửi thử có đường riêng, nghĩa là "Gửi thử OK" **không chứng minh
được** bản tin thật gửi được — hai đường khác nhau. Nay chung một đường: thử thành công là bằng chứng
thật, và khoá OA/bot không phải nhân đôi sang proxy. Đổi lại kết quả không tức thì (tới nhịp rút kế,
~1 phút) — endpoint nói rõ điều đó trong `summary`.

⚠️ **"Gửi thử" CỐ Ý không ghi vào `dbo.AgentInsights`.** Hai lý do, cái đầu là lỗi thật đã suýt lọt:
(1) mốc chống trùng của bản tin thật (`InsightRepository.ExistsTodayAsync`) đếm dòng trong bảng đó —
bản thử ghi vào thì ai bấm "Gửi thử" buổi trưa sẽ **mất bản tin thật sáng mai**, vì workflow tưởng hôm
nay chuẩn bị rồi; (2) Bảng tin là nơi xem/nghe **lại bản tin thật**, nhét bản thử vào làm bẩn lịch sử.
Gửi thử là để thử **kênh ngoài** — kênh trong app luôn bật, không cần thử. Không bật kênh ngoài nào thì
endpoint trả `ok:false` nói thẳng là không có gì để thử.

**Một enum kênh duy nhất** — [`OutboundChannel`](Services/Digest/OutboundChannel.cs): `0=Email`,
`1=Telegram`, `2=Zalo`, lưu thẳng cột `dbo.OutboundMails.Channel` (TINYINT). Default 0 nên dòng cũ trong
DB tự đúng nghĩa. Worker toutkit-app **mirror đúng bảng số này**
([docs/mail-templates/README.md](docs/mail-templates/README.md)) — thêm kênh mới = thêm 1 member ở CẢ 2
repo + 1 lớp `IOutboundChannelSender` bên worker (KHÔNG đụng vòng lặp, KHÔNG đụng kênh cũ).
`ChannelMask`/`DigestChannel`/`InAppChannel` **đã gỡ hẳn**
(13/08): cờ bit "đã gửi kênh nào hôm nay" hết lý do tồn tại khi mỗi kênh đã là một dòng có `Status` riêng.
Cột `SentMask`/`SentAttempts` còn trong DB nhưng **code không ghi nữa**.

**Giao diện — GỘP trong trang Tự động hoá, KHÔNG có trang riêng** (chốt 12/08: đăng ký bản tin chính là
cấu hình của 2 tác vụ đó). `/workflows` có 2 tab: "Tác vụ" (thẻ bản tin chứa khối **"Bản tin của tôi"** —
[`digest.jsx`](wwwroot/pages/digest.jsx)) và "Bảng tin" ([`insights.jsx`](wwwroot/pages/insights.jsx)).
Zalo OA nằm cạnh tài khoản dịch vụ trong nhóm "Theo tổ chức". `/insights` + `/digest` là 2 đường cũ trỏ
về đúng tab (chuông ở thanh trên dùng `/insights`). Item bản tin trong Bảng tin có nút **Nghe** (đọc qua
`/api/v1/speech/tts`, giọng server đồng nhất) — lời đọc do `BriefNarration.ToSpeakable` làm sạch từ
markdown; và mỗi người chỉ nhận **1 loại** bản tin theo vai trò (bật loại này tự tắt loại kia).

**Phân vai quyền:** "Bản tin của tôi" (nơi nhận của chính mình) → KHÔNG cần quyền, giống hộp thư cá nhân.
Lịch chạy + tài khoản dịch vụ + Zalo OA → cần `CH_HT_XEM`. Vì trang này nay có phần cá nhân nên mục menu
nằm ở khối **"Bản tin & Tự động"** (không phải "Tích hợp") và route KHÔNG gate cứng.

**Theo dõi (admin):** `/admin-trav-ai` → **Bản tin**. Cần trang này vì **cả 3 kiểu hỏng của tính năng
đều IM LẶNG** — người dùng chỉ thấy sáng ra không có gì, không lỗi nào hiện lên: (1) đã đăng ký nhưng
công ty chưa bật lịch chạy, (2) bật kênh mà bỏ trống nơi nhận, (3) kênh gửi hỏng. Cột "Hôm nay" đọc từ
**hàng đợi** (đã gửi / hỏng / còn chờ tới giờ) thay cờ bit cũ; "Gửi lần cuối" = `MAX(ProcessedUtc)` của
dòng đã gửi. Cột "Vấn đề" tính ở server ([`AdminDigestRepository.DetectProblem`](Services/Admin/AdminDigestRepository.cs))
theo thứ tự nguyên nhân GỐC trước. Bộ đếm luôn là tổng THẬT kể cả khi đang lọc "chỉ lỗi" — lọc ở SQL thì
"3/12 có vấn đề" biến thành "3/3", đọc xong tưởng cả hệ thống hỏng.

**Cấu hình cần có:** `Telegram:BotToken` (rỗng = kênh Telegram tự tắt) · `Models:Digest` (thiếu → kế thừa
`Models:Primary`) · template mail `daily-brief` trong `/admin-trav-ai` → Mail Templates (thiếu thì worker
vẫn render từ `Params`) · `Digest:LeadMinutes|InsightKeepDays` (thiếu → 10/30).

⚠️ **Nhịp quét KHÔNG nằm trong config.** `Digest:CheckIntervalMinutes` từng có trong appsettings nhưng
**không dòng code nào đọc** — đã gỡ 14/08. Nhịp thật là `dbo.UserWorkflows.IntervalMinutes` của chính 2
tác vụ bản tin (ô "Kiểm tra ai đến giờ, mỗi" trên trang Tự động hoá; mặc định 15' do
`WorkflowEndpoints.DefaultInterval`). Quan hệ với `LeadMinutes`: workflow dựng nội dung từ mốc
`giờ chọn − Lead` và **hẹn `ScheduledUtc` đúng giờ người chọn**, nên trễ tối đa = `max(0, Interval − Lead)`
— đặt Interval ≤ Lead thì luôn đúng giờ. Sàn cứng là tick 60s của `WorkflowSchedulerService`.

**E2E:** [`scripts/e2e/features-digest.ps1`](scripts/e2e/features-digest.ps1) (tự sao lưu + khôi phục đăng
ký thật) · sơ đồ luồng: `node scripts/e2e/features-flow-diagram.check.js`.

## Deploy tách site: TourkitAiProxy.Worker

Scheduler workflow (`WorkflowSchedulerService` tick 60s) chạy trên project riêng `TourkitAiProxy.Worker` (Sdk=Microsoft.NET.Sdk.Worker) để ổn định:
- Web restart / IIS AppPool recycle / crash → automation KHÔNG rớt.
- Worker fail → UI + API vẫn sống. Deploy độc lập.
- Share code qua `<ProjectReference Include="../TourkitAiProxy.csproj" />` — worker dùng NGUYÊN `Services/Workflows/*`, `Services/Mail/*`, `Services/Deals/*`... không copy code.
- DI wiring shared qua [`Services/Bootstrap/WorkflowStackRegistration.cs`](Services/Bootstrap/WorkflowStackRegistration.cs) extension `AddWorkflowStack()` — cả web và worker gọi cùng 1 method → 1 nguồn wiring.

**Cấu hình tách:**
- Web `appsettings.json`: `"Workflows": { "RunScheduler": false }` (default sau khi split — xem `appsettings.example.json`).
- Worker `appsettings.json`: `ConnectionStrings:PushDb` + `Redis:ConnectionString` + `Providers:*:ApiKey` + `Models:*:ApiKey` + `TourKit:BaseUrl` TRÙNG web (share `dbo.UserWorkflows` / `dbo.WorkflowRuns` / `dbo.TkSessions` / `dbo.TenantServiceAccounts` / quota / cache). Gitignored.

**Endpoint `/api/v1/workflows/*` giữ nguyên trên WEB** (worker không expose HTTP). "Chạy ngay" (`/run-now`) gọi `WorkflowSchedulerService.RunOneAsync` — Singleton đã đăng ký ở web nên vẫn chạy được dù web không có hosted tick.

Deploy: Windows Service via `sc.exe create` (mặc định), systemd + Docker documented. Xem [`TourkitAiProxy.Worker/README.md`](TourkitAiProxy.Worker/README.md).

**Khi thêm workflow mới:** implement `IScheduledWorkflow` + `AddSingleton<IScheduledWorkflow, X>()` **trong `WorkflowStackRegistration`** (không phải `Program.cs`) → worker + web tự pickup, không cần deploy 2 lần.

## Admin governance (`/admin-trav-ai/`)

Hệ quản trị admin riêng biệt với user-facing app. Entry HTML `wwwroot/admin-trav-ai.html` (KHÔNG share `index.html`). Toàn bộ shell + page components nằm trong 1 file `wwwroot/pages/admin.jsx`.

- **Auth**: cấu hình `Admin:Users` JSON trong `appsettings.json` (plain text password — admin pool nhỏ, self-host, file gitignore). `AdminUserStore.Authenticate` string-compare. Session in-mem `AdminSessionStore` (token GUID, 12h idle, KHÔNG persist). Client gửi `X-Admin-Session` header. Endpoint require qua extension `.RequireAdminSession()`.
- **Compatibility**: `/api/v1/admin/quota/*` (webhook ops) GIỮ NGUYÊN `Admin:Token` cũ — KHÔNG đụng. Mọi endpoint admin UI mới dùng `/api/v1/admin/ui/*` với `RequireAdminSession()`.
- **Cross-tenant digest**: `Services/Admin/AdminDigestRepository.cs` — JOIN `dbo.DigestSubscriptions` với `dbo.UserWorkflows` (`Username=''` vì 2 tác vụ bản tin đều PerTenant) để biết đăng ký nào đang "chết lặng". Mask chỉ đọc khi `LastSentLocalDate` ĐÚNG là hôm nay — sang ngày mới mask chưa reset tới lượt gửi đầu, đọc nhầm sẽ báo "đã gửi" cho hôm nay.
- **Cross-tenant usage**: `Services/Admin/AdminUsageRepository.cs` aggregate trên `dbo.AiUsageHistory` (4 query: totals/byModel/byTenant/byDay). Filter `Status='ok'` để khỏi double-count retry. `Tenant IS NULL` group thành `(system)`. Tenant name resolve qua `TkSessionRepository.GetTenantNamesAsync` (SELECT TOP 1 per tenant ORDER BY LastUsedUtc DESC, fallback `tenantId`).

### Thêm trang admin mới — 3 dòng

1. **Backend** — endpoint mới trong `Endpoints/AdminUiEndpoints.cs`:
   ```csharp
   g.MapGet("/orders", async (...) => { ... });
   // Filter `.RequireAdminSession()` đã apply ở group level — KHÔNG cần lặp.
   ```
2. **Frontend** — component mới trong `wwwroot/pages/admin.jsx`:
   ```jsx
   function OrdersPage() { /* ... */ }
   ```
3. **Nav** — push 1 entry vào `ADMIN_NAV`:
   ```js
   { path: "orders", label: "Đơn nạp quota", icon: "💳", component: OrdersPage }
   ```

Sidebar, sub-router, auth gate tự pick up. KHÔNG cần đụng `Program.cs`, không cần đụng `admin.css` (trừ khi page mới có style riêng → namespace `.admin-orders-*`).

## SEO cho trang public

Toàn bộ ở **server** ([`Configuration/SeoSetup.cs`](Configuration/SeoSetup.cs), nối vào
`ServeIndex` của [`StaticFilesSetup`](Configuration/StaticFilesSetup.cs)) — vì trang vẽ bằng JS nên
HTML gốc **không có một chữ nào** của nội dung; máy tìm kiếm và bộ xem trước link (Zalo/Facebook/
LinkedIn/Bing — phần lớn KHÔNG chạy JS) nhìn vào chỉ thấy trang trắng.

- **Nội dung dựng sẵn** (`SeoSetup.LandingBody()`) nhét vào `#root` **chỉ cho `/` và `/landing`**.
  `ReactDOM.createRoot().render()` xoá sạch container khi khởi động nên người dùng thấy đúng trang
  thật — không phải hydrate, không có chuyện lệch markup.
  ⚠️ **Chữ ở đây phải TRÙNG nguyên văn với `wwwroot/pages/landing.jsx`.** Trùng thì đó chỉ là "gửi
  sớm hơn"; lệch thì Google coi là gian lận nội dung (cloaking) và phạt. Cố ý chỉ lấy phần chữ ỔN
  ĐỊNH (tiêu đề, tên tính năng, tên các bước), KHÔNG lấy đoạn giới thiệu dài của từng tính năng —
  chữ càng dài càng hay sửa, sửa một bên quên bên kia là lệch.
- **`SeoSetup.Routes` là 1 nguồn cho 3 việc**: tiêu đề từng trang · `noindex` cho trang nội bộ ·
  **danh sách đường hợp lệ để trả 404**. Thêm trang mới mà quên khai ở đây thì mở link trực tiếp vào
  trang đó ra **404** (trong app bấm qua vẫn chạy vì router client không hỏi server — hỏng kiểu khó lần).
- **Không hardcode tên miền.** `canonical`/`sitemap` dựng từ request, đọc `X-Forwarded-Host`/`-Proto`
  trước (sau IIS/nginx thì `Request.Host` là host nội bộ → canonical thành `http://localhost/`, tức
  khai với Google rằng bản chính nằm ở localhost).
- `/landing` **canonical về `/`** — hai đường cùng nội dung, không khai thì tự chia điểm.
- `robots.txt` + `sitemap.xml` map **TRƯỚC `MapFallback`**, không thì fallback nuốt và trả
  `index.html` kèm 200 (đúng cái bẫy đã ghi ở mục `Features:Digest`). Sitemap **chỉ có trang chủ**:
  khai trang nội bộ vào sitemap là tự mời Google index đúng những trang vừa gắn `noindex` — hai tín
  hiệu chỏi nhau, Search Console báo lỗi.
- **Escape tối thiểu** (`SeoSetup.EscapeText`, chỉ `& < > "`), KHÔNG dùng `WebUtility.HtmlEncode`:
  nó mã hoá cả chữ có dấu thành `&#7897;` nên "Hộp thư AI" thành một dãy số — meta phình gấp mấy
  lần, mô tả vượt giới hạn ký tự của Google, và xem mã nguồn không đọc được gì.
- **Bộ kiểm chống lệch**: `node scripts/e2e/seo-prerender.check.js` — đối chiếu từng câu dựng sẵn với
  `landing.jsx`, đối chiếu `SeoSetup.Routes` với các `<Route path>` trong `app.jsx`, và chặn việc mở
  `Index: true` cho trang nội bộ.

## Frontend layout

```
wwwroot/
  index.html                                ← controls load order; <script src="..."> imperative
  styles.css
  lib/
    data.js                                 ← demo data + fmtVND
    icons.jsx                               ← Icon component
  core/
    router.jsx                              ← hash router (Router, Route, Link, navigate)
    storage.js                              ← TourCache + RequestHistory + tour stats
    parsers.js                              ← parseLooseJSON + parseTourText
    ai-provider.jsx                         ← thin client → /api/v1/completions; AISettingsDialog
    features.js                             ← window.tourkitFeatures — cờ tính năng chưa ra mắt (đọc /api/v1/features 1 lần); plain .js vì CẢ index.html lẫn admin-trav-ai.html dùng
  components/
    dialogs.jsx                             ← ConfirmDialog, ShareDialog, AIAssistantPanel
    tweaks-panel.jsx                        ← editorial Tweaks UI
    customer-review-card.jsx                ← rendered review card (rank/alert/actions) for the drawer
  steps/
    step1.jsx … step4.jsx                   ← sub-views inside the wizard page
  pages/
    wizard.jsx                              ← 4-step wizard (handleGenerate orchestration here)
    quotes.jsx                              ← list of cached tours — example of a 2nd page
    customers.jsx                           ← Customer Review page: list + batch confirm + SSE progress + review drawer
    assistant.jsx                           ← Chat-Analytics page: token login + chat-left + data-right (stats + table)
    mail.jsx                                ← SmartMail AI page: Gmail config form + 3-col (filters/list/detail) + AI compose (SSE)
    digest.jsx                              ← KHỐI (không phải trang): "Bản tin của tôi" + cấu hình OA Zalo, nhúng vào thẻ tác vụ
    insights.jsx                            ← KHỐI: "Bảng tin" — tab thứ 2 của trang Tự động hoá
  app.jsx                                   ← App shell: header + nav + <Router> + global state
```

**Adding a new page:**
1. `pages/<name>.jsx`: `function MyPage({ pushToast }) {...} window.MyPage = MyPage;`
2. `index.html`: add `<script type="text/babel" src="pages/<name>.jsx"></script>` after existing pages.
3. **`bundle-entry.js`: add `import "./pages/<name>.jsx";`** — BẮT BUỘC, dễ quên. Thiếu bước này thì dev (Babel) chạy được nhưng **prod bundle thiếu trang → trắng trang + `React #130`**. `index.html` (dev) và `bundle-entry.js` (prod esbuild) phải LUÔN khớp danh sách.
4. `app.jsx`: add `<Route path="/<name>" render={() => <window.MyPage pushToast={pushToast} />} />` inside `<Router>`.
5. `app.jsx`: add `<Link to="/<name>">Tên</Link>` in the nav.

No bundler, no npm install. `<script type="text/babel">` is transformed in-browser by `@babel/standalone`.

**Thêm một file `.js` THƯỜNG (không phải `text/babel`)** — vd `core/features.js`, `lib/data.js`: khai thẻ
`<script src>` trong `index.html` VÀ `import` trong `bundle-entry.js` thì phải thêm tên nó vào
`_bundledPlainJsRegex` ([StaticFilesSetup.cs](Configuration/StaticFilesSetup.cs)). Quên thì ở prod file
chạy **hai lần** (thẻ script + bản trong bundle) — dev không bao giờ lộ ra vì dev không có bundle.

**Dùng lại helper, KHÔNG copy-paste:** React hook chung ở [`wwwroot/lib/hooks.jsx`](wwwroot/lib/hooks.jsx) (`window.tourkitHooks` — vd `useIsMobile`); util thuần ở [`wwwroot/lib/util.js`](wwwroot/lib/util.js) (`window.tourkitUtil` — `readSSE`, `fmtAgo`, `fmtDate`, `copyText`); tiền VND ở `window.fmtVND` (lib/data.js); auth/fetch ở `window.tourkitAuth.authedFetch`. Cần thêm helper dùng nhiều nơi → thêm vào các file này thay vì định nghĩa lại trong từng page.

## Cross-cutting

**Cờ tính năng chưa ra mắt — `Features:*`, 1 nguồn ở [`FeatureFlags`](Services/Bootstrap/FeatureFlags.cs).**
KHÁC phân quyền: quyền trả lời "người này được xem gì", cờ trả lời "tính năng đã ra mắt chưa" — tắt là
tắt cho tất cả, kể cả admin. **Thiếu key = TẮT** (cố ý sai theo hướng an toàn: quên khai lúc deploy thì
tính năng bị ẩn — phiền nhưng sửa 1 dòng; mặc định bật thì thứ chưa ra mắt lọt thẳng ra bản public).

| Cờ | Che cái gì | Phụ thuộc |
|---|---|---|
| `Features:Digest` | Cụm bản tin: `sale-brief` · `ceo-brief` · `payment-watchdog` + Bảng tin | — |
| `Features:TourReadiness` | Tác vụ `tour-readiness` (kiểm tra sẵn sàng khởi hành) | **CẦN `Digest`** — nó ghi vào Bảng tin; bật riêng thì cảnh báo nằm đó không ai đọc được |
| `Features:MeetingBrief` | Action `prepare_meeting` (thẻ chuẩn bị gặp khách) | — |
| `Features:AnomalyWatchdog` | Tác vụ `anomaly-watchdog` (canh doanh thu bất thường) | **CẦN `Digest`** — ghi vào Bảng tin |
| `Features:AutoCare` | Tác vụ `customer-auto-care` (nhắc chăm lại khách ngủ quên) | **CẦN `Digest`** — ghi vào Bảng tin |

⚠️ `AutoCare` là cờ **quan trọng nhất**: tính năng duy nhất của cả hệ đụng tới KHÁCH HÀNG THẬT. Mọi
thứ khác chỉ ghi vào Bảng tin cho người trong công ty đọc. Bản hiện tại **KHÔNG gửi gì cho khách** —
xem ghi chú trong [`CustomerAutoCareWorkflow`](Services/Workflows/CustomerAutoCareWorkflow.cs): đo
thật thấy số điện thoại có ở 100/100 khách còn email chỉ 14/100, nên việc đúng với dữ liệu là **nhắc
nhân viên gọi**. Nếu sau này thêm khâu gửi, cờ này là chỗ chặn.

**Tắt một tính năng phải chặn ở chỗ nó SINH RA, không phải chỗ nó chạy.** Workflow → không đăng ký DI
([`WorkflowStackRegistration`](Services/Bootstrap/WorkflowStackRegistration.cs)) nên scheduler + `GET
/api/v1/workflows` không thấy → thẻ tự mất khỏi giao diện. Action tool → gỡ khỏi danh mục gửi cho AI
(`ActionTools.Enabled(cfg)`) nên **AI không biết là có nó để mà gọi**; chặn lúc thực thi thôi là muộn,
AI đã hứa với người dùng rồi mới báo lỗi. Vẫn giữ chốt chặn thứ hai ở `ActionExecutor` cho tab mở từ
trước lúc tắt cờ — ném [`FeatureDisabledException`](Services/Bootstrap/FeatureDisabledException.cs) →
**403**, KHÔNG để rơi vào bộ bắt lỗi chung thành 500 (nói sai với người dùng, và trộn cảnh báo giả vào
log lỗi thật).

Thêm cờ mới: thêm 1 method vào `FeatureFlags` → gate chỗ sinh ra → thêm 1 field vào `GET
/api/v1/features` (giao diện đọc qua [`window.tourkitFeatures`](wwwroot/core/features.js)) → khai key ở
**CẢ** `appsettings.example.json` lẫn bản của worker. Action tool thì thêm 1 dòng vào `ActionTools.Gated`.

**Frontend reaches AI via `window.claude.complete` or `window.tourkit.ai.complete`/`completeStream`.** `core/ai-provider.jsx` shims `window.claude.complete` to delegate to `window.tourkit.ai`, which POSTs to `/api/v1/completions`. **ALL provider keys (OpenCode/9routes/OpenAI/Anthropic) live server-side** in `appsettings.json` (`Providers:{X}:ApiKey` or `Models:Primary/Review:ApiKey`) or env vars. The AI Settings UI lets users pick provider/model only — no key input. `localStorage["tourkit_ai_config"]` only holds `{provider, model, _v}` (v9). Bump `CONFIG_VERSION` in `ai-provider.jsx` when changing the shape. (Pre-v9: had client-side localStorage key store + dialog input — removed because operationally fragile; see v8→v9 migration comment.)

**Static files.** `UseStaticFiles` has `ServeUnknownFileTypes = true` + `DefaultContentType = "text/plain"` so `.jsx` loads without a registered MIME type. `.jsx`/`.js`/`.css`/`.html` are served with `Cache-Control: no-cache` so edits show on a plain reload.

**Cấu hình model AI — khai ĐỦ 14 feature, đừng để rơi ngầm.** `AiModelRegistry.Resolve` đi theo
`Models:{Feature}` → `Models:Primary` → default của provider. Nghĩa là **thiếu một khoá thì tính năng đó
âm thầm chạy bằng `Models:Primary`** — không log, không cảnh báo, chỉ hoá đơn cuối tháng biết. Đã dính
thật (14/08): appsettings prod thiếu `Models:MailClassify` nên phân loại mail chạy bằng `claude-haiku`
suốt, mà đó là task chạy **hàng trăm lần mỗi lần đồng bộ hộp thư**; `Models:Digest` cũng thiếu tương tự.
Danh sách 14 = enum `AiFeature` ([AiModelRegistry.cs](Services/Providers/AiModelRegistry.cs)) — khai đủ
ở **CẢ** `appsettings.json` của web **VÀ** của worker (worker mới là nơi chạy `mail-auto-sync`,
`deal-auto-review`, `customer-auto-review`, `ceo-brief`).

⚠️ **Cấu hình đúng KHÔNG chứng minh được là nó đang chạy đúng.** Hai file appsettings nằm trên 2 máy,
đều gitignore, nên bản trên server có thể là bản cũ mà không chỗ nào lộ ra. Cách duy nhất biết chắc là
**đọc ngược từ log dùng thật**: [`scripts/check-model-drift.ps1`](scripts/check-model-drift.ps1) gom
`dbo.AiUsageHistory` theo (feature, provider, model) rồi so với cấu hình web tại chỗ. Read-only, chạy
được mọi lúc. Đã bắt được thật (15/08): worker chạy `mail-auto-sync` bằng `claude-haiku-4-5` và 2 tác vụ
tự chấm bằng `ds/deepseek-chat` — model KHÔNG có trong cấu hình hiện tại, tức worker cầm bản cũ.
Lưu ý khi đọc kết quả: feature nào **trùng đúng `Models:Primary`** thì không phân biệt được là khai đúng
hay đang rơi ngầm về Primary — script đánh dấu riêng, đừng coi là đã xác nhận.

⚠️ Đổi provider cho một feature thì phải có khoá provider đó trong `Providers:*`. Thiếu khoá, provider
ném lỗi — mà vài chỗ **bắt lỗi rồi đi tiếp** (vd `MailClassifier` ghi Warning rồi trả nhóm `khac` cho
mọi thư, giao diện nhìn vẫn bình thường). Nên thiếu khoá còn tệ hơn chọn sai model. DeepSeek đi **qua
`nine-routes`** (`ds/deepseek-v4-flash`) chứ không gọi `api.deepseek.com` trực tiếp — khoá nine-routes
đã có sẵn và đã chạy thật cho Chat, `Providers:DeepSeek:ApiKey` thường để rỗng.

**Usage tracking trong SQL** `dbo.AiUsageCounters` (daily aggregate per-model, MERGE upsert). `UsageTracker.Track` fire-and-forget UPSERT (không block AI call); `Snapshot()` đọc cache in-mem 10s, miss → `UsageRepository.ReadAggregateAsync(30 ngày)` → SUM GROUP BY Model. Cross-process: 2 instance cùng SQL share counter tự động. Cost estimate hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) bất kể model. Streaming chỉ Track khi `outTok > 0`. Key dạng `"{providerId}:{model}"`.

**Tenant AI quota** ([Services/Quota/TenantQuotaStore.cs](Services/Quota/TenantQuotaStore.cs)). Mỗi tenant mặc định 1000 lượt AI (lĩnh 1 lần, KHÔNG tự reset). Storage: in-mem `ConcurrentDictionary` source of truth + ghi đè file `data/tenant-quota.json` mỗi lần thay đổi + mirror Redis best-effort (cross-instance visibility). Provider check ở đầu `CompleteAsync`/`StreamAsync` (5 providers — `EnsureQuota()`); consume ở `LogUsage`/sau khi `_usage.Append` khi status=ok và có tenant. Hết quota → throw `QuotaExhaustedException` → middleware [`QuotaExceptionMiddleware`](Services/Quota/QuotaExceptionMiddleware.cs) convert → 429 JSON `{error, quota}`. Frontend: chip `.tb-quota` ở topbar (`AI <used>/<limit>`), warn ở 90%, pulse đỏ ở 100%. Endpoints: `GET /api/v1/quota` (user), `GET /api/v1/admin/quota` + `POST /api/v1/admin/quota/{tenant}/topup` (admin gate qua `Admin:Token` config). System calls không có tenant (no session) → skip check.

**Cost UI hidden by default.** Menu "Chi phí AI" + page `/ai-usage` chỉ hiện khi user toggle debug ON (icon info ở topbar). URL `/ai-usage` vẫn accessible trực tiếp (giữ cho admin xem nhanh).

**CORS is wide open in dev.** `CorsSetup.cs` lists allowed origins but calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production.

## Code lookup (CodeGraph MCP)

Khi câu hỏi liên quan đến **cấu trúc code** (callers/callees, "X dùng ở đâu", flow nghiệp vụ, blast-radius trước khi đổi tên), **PHẢI dùng CodeGraph trước** `Grep`/`Glob`. CodeGraph chạy trên knowledge graph SQLite build sẵn (auto-sync khi sửa file) → nhanh hơn nhiều lần so với re-scan file, bắt đúng symbol thay vì khớp text mù, và trả luôn source code kèm số dòng.

**3 repo đều đã index bằng CodeGraph** — mỗi repo 1 `.codegraph/` RIÊNG (CodeGraph là per-project, KHÔNG gộp nhiều repo trong 1 query như GitNexus cũ):
- `tourkit-ai-proxy` — project này (proxy + `wwwroot/`).
- `toutkit-app` — TourKit.Api mobile (upstream CRM mà proxy gọi qua `/api/ai/*`). Hỏi bằng cách chạy `codegraph` trong thư mục đó, hoặc truyền `projectPath` cho `codegraph_explore`.
- `tourkit` — CMS Web KojiCRM (ASP.NET WebForms; nghiệp vụ gốc TourKit).

**Chọn lệnh:**
- `codegraph explore "<concept>"` — concept search ("deal scoring flow?", "mail classification owner?"). Trả symbols liên quan + source + call paths + blast-radius trong 1 lần. Lệnh dùng nhiều nhất.
- `codegraph node <Symbol>` — 360° view 1 symbol (source + callers/callees). Dùng sau khi `explore` thu hẹp.
- `codegraph impact <Symbol>` — blast-radius TRƯỚC khi rename/sửa method/field. Liệt kê mọi caller + dependent (symbol-level, kèm số dòng).
- `codegraph callers <Symbol>` / `codegraph callees <Symbol>` — đi call graph 2 chiều.
- MCP: `mcp__codegraph__codegraph_explore` (tương đương `codegraph explore`; dùng `projectPath` để hỏi repo khác).

**KHI vẫn dùng Grep/Glob:** tìm chuỗi text trong comment / config / JSON / Markdown (graph chỉ index code symbol); list file theo glob.

**Re-index:** CodeGraph có watcher tự đồng bộ, NHƯNG ⚠️ **daemon của nó tự tắt sau 5 phút không có
truy vấn nào** (`inactivity backstop`, xem `.codegraph/daemon.log`). Nghĩa là một đợt sửa code dài mà
không tra cứu gì thì watcher đã chết từ lâu và index lạc hậu **trong im lặng** — `codegraph node X` vẫn
trả kết quả trông bình thường, chỉ thiếu đúng phần vừa sửa. Đã dính thật 14/08: lệch 21 file, chỉ phát
hiện khi tra một symbol mới thêm.

Vì thế repo này cài 3 git hook (`.git/hooks/post-commit|post-merge|post-checkout`) chạy `codegraph sync -q`.
Hook nằm trong `.git/` nên KHÔNG theo repo — máy mới phải tự cài lại (3 file, mỗi file gọi
`codegraph sync -q` rồi `exit 0`). Ép tay khi cần: `codegraph sync` (incremental) / `codegraph index`
(full). Cross-repo question (proxy ↔ TourKit.Api) → chạy `codegraph` trong `toutkit-app/` cho signature
upstream.

## Logging (log4net + middleware)

**Sink chính:** log4net qua bridge `Microsoft.Extensions.Logging.Log4Net.AspNetCore` — mọi `ILogger<T>` của app + ASP.NET Core routing đều chảy qua log4net. Wire ở `Program.cs` (web) và `TourkitAiProxy.Worker/Program.cs`.

**Config**: [`log4net.config`](log4net.config) ở root (worker link vào bin qua csproj), `Watch=true` → hot reload khi sửa level/appender, không cần restart.

**3 appender**:
- `RollingFileAppender` → `logs/app-YYYY-MM-DD.log` (giữ 30 file/~1 tháng)
- `ErrorFileAppender` → `logs/error-YYYY-MM-DD.log` (chỉ ERROR/FATAL, giữ 90 file/~3 tháng — tách để audit nhanh)
- `ConsoleAppender` → stdout (dev + Docker)

**Layout kèm 2 property**: `[req=%property{RequestId}|tenant=%property{TenantId}]` — nghĩa là mọi log trong 1 request có cùng `RequestId` (12-char GUID), grep 1 lần ra full flow.

**3 middleware bọc pipeline** (thứ tự ngoài → trong, đăng ký sớm nhất trong `Program.cs`):
1. `CorrelationIdMiddleware` ([Services/Logging/CorrelationIdMiddleware.cs](Services/Logging/CorrelationIdMiddleware.cs)) — reuse `X-Request-Id` header hoặc sinh mới, push vào `log4net.LogicalThreadContext`, echo response header
2. `RequestLoggingMiddleware` ([Services/Logging/RequestLoggingMiddleware.cs](Services/Logging/RequestLoggingMiddleware.cs)) — log 1 line/request `{Method} {Path} → {Status} ({Ms}ms) tenant={T} ip={IP}`. 2xx/3xx=Info · 4xx=Warn · 5xx=Error. Skip static asset (`.js`/`.jsx`/`.css`/`.png`/`/dist/`/`/lib/`/`/pages/`) để tránh spam
3. `UseExceptionHandler()` với `GlobalExceptionHandler` (`IExceptionHandler`, [Services/Logging/GlobalExceptionHandler.cs](Services/Logging/GlobalExceptionHandler.cs)) — bắt exception KHÔNG được endpoint handle → log ERROR có full stack + trả JSON `{error, detail, type, requestId}` 500

**DB logging** (song song với log4net — độc lập): `dbo.AppLogs` bật qua `Logging:Database:Enabled=true` cho cross-instance search bằng SQL. Đã có sẵn `DbLoggerProvider` + `DbLogWriter`. Web mặc định OFF (log ra file đủ dùng); worker khuyến nghị ON khi scale nhiều instance.

**Level tuning**: đổi `<level value="INFO"/>` trong log4net.config, hot reload trong 60s. Namespace-specific: uncomment block `<logger name="TourkitAiProxy.Services.Workflows">` để bật `DEBUG` cho workflow mà không đụng root.

**Nội dung log workflow** (áp dụng cho `CustomerAutoReviewWorkflow` + `DealAutoReviewWorkflow`):
- START — kèm option đầy đủ, tenantId
- Login OK/FAIL + duration
- Mỗi phase (Pass 1/Pass 2/Cooling) — fetch count + bulk pre-fetch count + duration
- Per-page (Customer) hoặc per-pass (Deal) — breakdown counter (reviewed/skipped/…)
- TIMEOUT (`OperationCanceledException`) → Warning (không fail run)
- QUOTA hit → Warning (không fail run, không auto-pause)
- FINISH — tổng duration + full counter breakdown

**Upstream call log** ([Services/TourKit/TourKitApiClient.cs](Services/TourKit/TourKitApiClient.cs)):
- LOGIN OK/FAIL kèm tenantId + username + duration
- GET/POST duration + status + bytes trên success (Debug); Warning cho 401/non-2xx/network error

**Không log**: JWT (có trong session raw), password, email body, phone number đầy đủ.

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains real-looking API keys. Treat them as secrets: don't echo them, and prefer env vars (e.g. `Providers__OpenCode__ApiKey`, `OPENCODE_API_KEY`, `NINE_ROUTES_API_KEY`) for any production-bound change.
- Frontend exposes singletons via `window.tourkit*` namespaces (`tourkit.ai`, `tourkitStorage`, `tourkitParsers`, `tourkitRouter`, `tourkitHistory`, `tourkitHooks`, `tourkitUtil`).
- **DateTime = UTC, luôn kèm `Z`** (STRICT — xem [docs/datetime-convention.md](docs/datetime-convention.md)). Lưu DB bằng `DateTime.UtcNow` / SQL `SYSUTCDATETIME()` (KHÔNG `DateTime.Now`/`GETDATE()`). Parse chuỗi ngày để lưu → `DateTimeStyles.AssumeUniversal | AdjustToUniversal` (TryParse trần ra `Kind=Local` → lưu sai). Trả client: field `DateTime` tự có `Z` qua [`UtcDateTimeConverter`](Services/Json/UtcDateTimeConverter.cs) (global); chuỗi `ToString("o")` từ SQL phải `DateTime.SpecifyKind(x, DateTimeKind.Utc)` trước (Dapper đọc DATETIME2 ra `Kind=Unspecified` → thiếu `Z` → frontend lệch +7h). Frontend dùng `window.tourkitUtil.fmtAgo/fmtDate`, không tự cộng/trừ giờ.
- **Viết tài liệu hướng dẫn người dùng** (`docs/features/*.md`): dùng agent [`tourkit-doc-writer`](.claude/agents/tourkit-doc-writer.md). Quy tắc: ưu tiên sự rõ ràng, dễ hiểu hơn chi tiết kỹ thuật; dùng CodeGraph kiểm flow THẬT trước khi viết + tham khảo internal knowledge base (claude-memory-compiler, nếu có) để giải thích ngắn gọn "tại sao"; mỗi trang tối thiểu có **Mô tả / Hướng dẫn từng bước / Lưu ý / FAQ**; luôn viết tiếng Việt, giọng thân thiện; viết xong **đề xuất các ảnh chụp màn hình cần bổ sung**.
- **CHANGELOG.md — BẮT BUỘC cập nhật mỗi lần public code** (STRICT). Bất cứ khi nào chuẩn bị phát hành (merge vào `main`/`dev`, tạo bản release, hoặc user nói "public/ra mắt/deploy"), PHẢI thêm/cập nhật một mục trong [`CHANGELOG.md`](CHANGELOG.md) mô tả **tính năng mới** + **lỗi đã sửa** của đợt đó. Nếu một thay đổi có ảnh hưởng tới người dùng mà chưa có dòng trong CHANGELOG → coi như **chưa xong**, đừng phát hành.
  - **Viết CHO NGƯỜI DÙNG CUỐI, không phải cho dev**: mô tả theo *trải nghiệm người dùng* ("Bạn có thể…", "Trước đây … nay …"). TUYỆT ĐỐI không đưa mã commit/SHA, tên file/hàm/class, tên bảng SQL, hay thuật ngữ kỹ thuật (Dapper, TINYINT, race, token…) vào CHANGELOG.
  - **Mỗi mục** = tiêu đề `## Phiên bản dd/MM/yyyy — <tên ngắn>`, rồi `### ✨ Tính năng mới`, `### 🔧 Đã khắc phục` (nói rõ *người dùng gặp vấn đề gì, nay hết thế nào*), tùy chọn `### 📌 Lưu ý` / `## 🔜 Sắp có`. Mới nhất ở TRÊN CÙNG. Tiếng Việt, giọng thân thiện.
  - Chi tiết kỹ thuật/nội bộ (SHA, tên hàm, lý do sâu) để trong commit message hoặc plan/spec — KHÔNG để trong CHANGELOG.

<!-- codegraph:start -->
# CodeGraph — Code Intelligence

This project is indexed by **CodeGraph** (`@colbymchenry/codegraph`) — a local SQLite knowledge graph in `.codegraph/` (no embeddings, no API key, fully offline). The index **auto-syncs as you edit**, so it's normally fresh with no manual re-index step. Use it to understand code, assess impact, and navigate safely before editing.

Two ways in:
- **MCP tool** `mcp__codegraph__codegraph_explore` — one call returns the relevant symbols' verbatim, line-numbered source **plus** their call paths **plus** a blast-radius summary (replaces a grep + Read loop).
- **CLI** `codegraph <cmd>` — `explore` / `query` / `node` / `callers` / `callees` / `impact` / `status`.

## Always Do

- **Assess blast radius before editing any symbol.** Run `codegraph impact <Symbol>` (or `codegraph_explore`) and report the direct callers + affected symbols before modifying a function/class/method. Warn the user when the radius is wide.
- When exploring unfamiliar code, use `codegraph explore "<concept>"` (or the `codegraph_explore` MCP tool) instead of grepping — it returns the relevant symbols' source + call paths in one shot.
- For a single symbol's 360° view (source + callers/callees), use `codegraph node <Symbol>`.

## When Debugging

1. `codegraph explore "<error or symptom>"` — surface the relevant symbols + call paths.
2. `codegraph node <suspect function>` — its source, callers, and callees.
3. `codegraph callers <Symbol>` / `codegraph callees <Symbol>` — walk the call graph in either direction.

## When Refactoring

- **Before moving/renaming**: `codegraph impact <Symbol>` to list every caller. CodeGraph has **no automatic safe-rename** — update the callers it reports by hand, then re-check.
- The index auto-syncs; if a result looks stale right after a large change, force it with `codegraph sync` (incremental) or `codegraph index` (full rebuild).

## Never Do

- NEVER edit a function/class/method without first checking `codegraph impact` (or `codegraph_explore`) on it.
- NEVER rename symbols with blind find-and-replace — list callers with `codegraph impact` first, then update each.

## Tools Quick Reference

| Command | When to use |
|---------|-------------|
| `codegraph explore "<q>"` | Answer almost any code question in one call (source + call paths + blast radius) |
| `codegraph query <name>` | Find a symbol by name |
| `codegraph node <sym\|file>` | One symbol's source + callers/callees, or a file with its dependents |
| `codegraph callers <sym>` | Who calls this |
| `codegraph callees <sym>` | What this calls |
| `codegraph impact <sym>` | Blast radius before editing |
| `codegraph status` | Index stats / freshness |

## Keeping the Index Fresh

CodeGraph auto-syncs via its background daemon as files change — there is **no** PostToolUse re-index hook and none is needed. To force it: `codegraph sync` (incremental) or `codegraph index` (full rebuild). Inspect state with `codegraph status`.
<!-- codegraph:end -->
