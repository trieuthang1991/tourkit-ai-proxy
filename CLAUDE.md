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
    TkSessionStore.cs                      ← sessions persisted to data/tk-sessions.json (pwd Crypton-encrypted, JWT not persisted): JWT server-side only, auto re-login on expiry/401, survives restart
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
    MailClassifier.cs                      ← classify qua Models:Review (DeepSeek deepseek-chat) — chỉ JSON-prompt, không native tool
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
  tk-sessions.json                         ← persisted TourKit sessions (gitignored; pwd Crypton-encrypted, no JWT)
  visa-files/{tenantId}/{assessmentId}/    ← Visa attachments per-tenant (gitignored runtime state)
  # Mail/Visa JSON stores đã migrate sang SQL Server (xem multi-tenant fix 2026-06-09):
  #   data/mails.json          → dbo.Mails           (composite PK TenantId,Id)
  #   data/mail-account.json   → dbo.MailAccounts    (per-tenant)
  #   data/mail-sync.json      → dbo.MailSyncState   (per-tenant)
  #   data/visa-assessments.json → dbo.VisaAssessments (per-tenant)
```

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
| POST   | `/api/v1/mail/{id}/reply/draft`   | SSE: stream nháp trả lời theo `{tone, instruction}`  |
| POST   | `/api/v1/mail/{id}/reply/send`    | gửi nháp (đã sửa) cho khách qua SMTP Gmail → status `da_phan_hoi` |
| POST   | `/api/v1/mail/compose/draft`      | SSE: AI soạn email MỚI từ `{to, subject, brief, tone}` |
| POST   | `/api/v1/mail/compose/send`       | gửi email mới qua SMTP `{to, subject, text}`         |
| PATCH  | `/api/v1/mail/{id}/status`        | đổi trạng thái email (moi/dang_xu_ly/da_phan_hoi/da_dong) |

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
- `apiKey` optional: legacy per-request channel (DTO still accepts it for backward compat). **As of v9 (`CONFIG_VERSION` in `ai-provider.jsx`), the frontend NO LONGER stores or sends keys.** All keys come from server: `ProviderKeyStore.Get(id)` resolves `Providers:{X}:ApiKey` → `Models:Primary:ApiKey` (if `Models:Primary:Provider==id`) → `Models:Review:ApiKey` (same) → env var. Old `localStorage["tourkit_ai_keys"]` is auto-cleared on first load by the v8→v9 migration.

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
- **Auth = token-decrypt, NOT api-key.** TourKit.Api uses JWT (`POST /api/auth/login` with `{tenantId, username, password}`). The client doesn't store credentials in config. Instead: `POST /api/v1/login-token {token}` where `token = Crypton.Encrypt(JSON {username,password,domain})`. `Crypton` is a **verbatim port** of `TourKit.Shared/Crypton.cs` (AES-256/CBC, `PassPhrase="Pas5pr@se"`, `Salt="s@1tValue"`, `IV="@1B2c3D4e5F6g7H8"`, `PasswordDeriveBytes`/SHA1/iterations=2) — DO NOT change the constants or tokens won't decrypt. `domain` maps to TenantId. The proxy logs in, creates a server-side session (`TkSessionStore`), and returns only a `sessionId` — **the JWT never reaches the client**. Sessions hold the decrypted creds to silently re-login on JWT expiry or a 401 (one retry in `ChatAgentService`). **Sessions persist to `data/tk-sessions.json`** (password Crypton-encrypted, JWT NOT persisted — re-login on first use) so they survive restart/deploy without forcing the user to log in again; soft-TTL ~50min, stale sessions pruned after 30 days.
- **Single-shot agent, no native function-calling.** `ChatAgentService.AskAsync` (buffered) / `AskStreamAsync` (SSE): (1) planner prompt with the `ChatTools` catalog → AI returns `{tool, params}` JSON (parsed via `LooseJson`); (2) dispatch to a `/api/ai/{section}` GET (`ChatTools.BuildPath` whitelists params; `ResolveMarketAsync` turns `marketName`→`marketId`); (3) **`BuildChatData`** maps the envelope → `ChatData` (items→Raw for table/chart, `summary`+`total`→stat cards, `title`); financial-summary's items become the stat cards. (4) analysis prompt → AI prose. Two AI calls; both have provider-fallback to the default provider on upstream/key failure.
- **Streaming + caches.** `AskStreamAsync` emits SSE events `{stage}` (planning→fetching→analyzing, data attached early) then `{delta}` (token-streamed analysis) then `{done}`. **SSE payloads MUST be serialized camelCase** (`SseJson = new(JsonSerializerDefaults.Web)` in `ChatEndpoints`) to match the client — default PascalCase silently breaks `data.stats`/`title`/`raw`. Caching via `Services/Cache/ChatCache.cs`: full-response (`r|{tenant}|{question}`) + CRM-data (`d|{tenant}|{path}`), TTL 30m, values as JSON. **Backend = Redis if `Redis:ConnectionString` is set (shared across instances + survives restart), else in-memory fallback.** The connection string may be `ENC:`-encrypted (copied verbatim from TourKit.Api) — `ChatCache` decrypts it with `Crypton` at runtime; keys are prefixed `tkai:` to avoid colliding with TourKit's own Redis keys; `AbortOnConnectFail=false` so a down Redis never blocks startup. **Never cache empty results** (`HasContent`/`IsUsableData`) or a transient empty poisons the path for 30m.
- **Tools are read-only `/api/ai/*` sections** (financial-summary, cashflow, marketing, departures, top-customers, top-sellers, tours, booking-tickets, tasks, customers, appointments, vouchers, notifications) + `list_markets` (still `/api/tours/markets` for the resolver). Add a tool = add one `ChatTool` entry in `ChatTools.All`. Discovery endpoints `/api/ai/catalog` + `/api/ai/reference` exist upstream (not yet wired into the proxy). Write endpoints excluded.
- **Name→id resolver (controlled multi-step).** Some filters need an id the user only knows by name (e.g. market "Nội địa miền Nam"). The planner fills a `marketName` param; `ChatAgentService.ResolveMarketAsync` looks it up against the tenant's market list (`GET /api/tours/markets`, cached 6h per tenant) and rewrites it to `marketId` before the call. `MatchMarket` normalizes (lowercase, strip Vietnamese diacritics, đ→d, drop punctuation, token-subset) so "Nội địa miền Nam" matches "Nội địa - Miền Nam". Customer-by-market questions route to `list_booking_tickets` (carries `MarketId`), since `/api/customers` has no market filter.
- **Caching + heuristic fallback.** Response + CRM-data caching is delegated to `ChatCache` (`r|…` / `d|…` keys, see "Streaming + caches" above — Redis-backed when configured, so NOT lost on restart). `ChatAgentService`'s only own cache is `_markets` (the 6h-per-tenant market-resolver list). The fallback `HeuristicRoute` keyword-routes when the planner emits non-JSON (reasoning models sometimes do), so a clear data question never silently returns "none".
- **Endpoints:** `POST /api/v1/login-token` (`{token}` → `{sessionId, tenantId, fullName, companyName, expiresAt}`), `POST /api/v1/chat` + `POST /api/v1/chat/stream` (`{messages, sessionId?, provider?, model?}`; sessionId may also come via `X-Session-Id` header → `{reply, toolName, data:{kind,title,raw,stats[]}, …}`; the `/stream` variant emits the SSE `{stage}`/`{delta}`/`{done}` sequence), `GET /api/v1/session` (validate the current sessionId).
- **Login UX:** two modes on `/assistant` — a direct form (`POST /api/v1/login {username,password,domain}`, server-side login, no client-side crypto) and the encrypted-token paste (`/login-token`). Both return a `sessionId`.
- **Frontend:** `wwwroot/pages/assistant.jsx` (route `/assistant`). Stores `sessionId` in `localStorage["tourkit_tk_session"]`, renders chat on the left and on the right: `data.stats` cards + a **Chart.js** chart + a generic table. Chart.js is loaded via CDN `<script>` in `index.html` (no build step); `ChartView` picks horizontal bars for categorical data and vertical grouped bars for time-series, with a metric-toggle (Doanh thu/Chi phí/Lợi nhuận). `ChatData.Focus` (derived in `ChatAgentService.DetectFocus` from question keywords like "chi phí"→`expense`) restricts the chart/table/stats to the requested metric. Money formatted with `fmtVND`.

## SmartMail AI feature ("Hộp thư AI")

Gmail inbox synced on demand, AI-classified, with AI-drafted replies. Flow lives in `MailEndpoints` → `Services/Mail/*`. Design doc: `docs/smartmail-ai-design.md`; implementation plan: `docs/superpowers/plans/2026-06-05-smartmail-ai.md`.

- **Source = Gmail IMAP via MailKit, NOT OAuth.** `GmailImapClient` (implements `IMailSource`) connects `imap.gmail.com:993` read-only with an **App Password** (requires Gmail 2-Step Verification + IMAP enabled). The interface keeps OAuth swappable later. Creds resolved by `MailAccountStore`: DB-backed `dbo.MailAccounts` per-tenant (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI per tenant. KHÔNG còn fallback config/env (đã drop từ commit multi-tenant fix 2026-06-09).
- **Sync is on-demand (Refresh button), not a background poller.** `POST /mail/sync` is **incremental theo UID** (`MailSyncStore` lưu `dbo.MailSyncState` per-tenant per-address `{uidValidity, lastUid}`): chỉ kéo email có UID > lần trước → KHÔNG sót dù >N email mới giữa 2 lần sync. Lần đầu/khi UidValidity đổi → kéo `max` (30) mới nhất. Cờ `\Seen` của Gmail map sang `IsRead` lúc kéo. Vẫn **classify chỉ email MỚI** (`repo.Has(id)` skip → tiết kiệm token). Email id = Message-Id (MimeKit chuẩn hóa/tự sinh), fallback `{address}:{uid}`.
- **Đọc/chưa đọc:** `POST /mail/{id}/read` đánh dấu đã đọc khi mở; `MailCounts.Unread` cho badge. Frontend in đậm + chấm cam dòng chưa đọc.
- **Soạn thư MỚI:** `POST /mail/compose/draft` (SSE, AI viết từ `brief`) + `/mail/compose/send` (gửi tới người nhận bất kỳ) — `MailReplyService.ComposeNewStreamAsync` + `IMailSender.SendAsync`. Chữ ký công ty (`MailAccountStore.Signature()`, cấu hình ở UI per-tenant, lưu trong `dbo.MailAccounts`) được dệt vào prompt soạn.
- **Classification + reply reuse `ProviderRegistry`.** `MailClassifier.ClassifyAsync` (buffered, dual-path — xem "Native function-calling" section: Anthropic → `submit_mail_classification` tool với Haiku; else → JSON-prompt) → `{category, summary}`; 6 categories normalized to a known set (lạ → `khac`); lỗi cả 2 path → `("khac", "")` để mail vẫn lưu. `MailReplyService.DraftStreamAsync` streams a tone-aware draft (4 tones) + staff instruction via `provider.StreamAsync`, saves the draft + flips status → `dang_xu_ly`. Both client AI prefs (`provider`/`model`/`apiKey`) flow through like the other features.
- **Sending = SMTP Gmail (`IMailSender`/`GmailSmtpClient`).** `POST /mail/{id}/reply/send` gửi nội dung (đã sửa) tới người gửi gốc qua `smtp.gmail.com:587` STARTTLS bằng chính App Password — gửi AS the company Gmail, nên KHÔNG dính SPF/DKIM/spam như giả mạo domain. Gắn `In-Reply-To`/`References` để vào đúng luồng. Gửi xong → lưu nội dung + status `da_phan_hoi`. Frontend confirm trước khi gửi.
- **Storage = SQL Server `dbo.Mails`** per-tenant scoped (`MailRepository`, composite PK `(TenantId, Id)`, index `IX_Mails_Tenant_Received` cho list/sort). Cross-tenant access trả null/404. KHÔNG fallback file — DB lỗi → 503.
- **Taxonomy** (`MailTaxonomy`, single source): categories `hoi_dat_tour|xin_bao_gia|khieu_nai|xac_nhan|spam|khac`, statuses `moi|dang_xu_ly|da_phan_hoi|da_dong`, tones `lich_su|than_thien|dam_phan|xin_loi` — all with Vietnamese labels.
- **Frontend:** `wwwroot/pages/mail.jsx` (route `/mail`), 3-column (filters / list / detail+compose). A built-in **config form** (`GET`/`POST /mail/account`) lets staff paste Gmail address + App Password to test without editing JSON. Draft uses the same SSE `{delta}`/`{done}` reader as `assistant.jsx`. Statuses/categories color-coded via CSS.
- **Phase 2 (deferred):** 2-way sync (write `\Seen` back / mirror deletes), incremental UID fetch (hiện kéo 30 mới nhất/lần), OAuth source, assign-to-staff ("Của tôi"), attachments.
- **Tests:** `TourkitAiProxy.Tests` (xUnit, project nằm trong thư mục con → main csproj `<Compile Remove="TourkitAiProxy.Tests/**" />`). Covers pure logic only: `MailTaxonomy`, `MailMapper`, `MailClassifier.ParseClassification`, `MailRepository`. Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`. IMAP/frontend verified manually. (This is the repo's first test project — the rest of the codebase still has none.)

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
  app.jsx                                   ← App shell: header + nav + <Router> + global state
```

**Adding a new page:**
1. `pages/<name>.jsx`: `function MyPage({ pushToast }) {...} window.MyPage = MyPage;`
2. `index.html`: add `<script type="text/babel" src="pages/<name>.jsx"></script>` after existing pages.
3. `app.jsx`: add `<Route path="/<name>" render={() => <window.MyPage pushToast={pushToast} />} />` inside `<Router>`.
4. `app.jsx`: add `<Link to="/<name>">Tên</Link>` in the nav.

No bundler, no npm install. `<script type="text/babel">` is transformed in-browser by `@babel/standalone`.

## Cross-cutting

**Frontend reaches AI via `window.claude.complete` or `window.tourkit.ai.complete`/`completeStream`.** `core/ai-provider.jsx` shims `window.claude.complete` to delegate to `window.tourkit.ai`, which POSTs to `/api/v1/completions`. **ALL provider keys (OpenCode/9routes/OpenAI/Anthropic) live server-side** in `appsettings.json` (`Providers:{X}:ApiKey` or `Models:Primary/Review:ApiKey`) or env vars. The AI Settings UI lets users pick provider/model only — no key input. `localStorage["tourkit_ai_config"]` only holds `{provider, model, _v}` (v9). Bump `CONFIG_VERSION` in `ai-provider.jsx` when changing the shape. (Pre-v9: had client-side localStorage key store + dialog input — removed because operationally fragile; see v8→v9 migration comment.)

**Static files.** `UseStaticFiles` has `ServeUnknownFileTypes = true` + `DefaultContentType = "text/plain"` so `.jsx` loads without a registered MIME type. `.jsx`/`.js`/`.css`/`.html` are served with `Cache-Control: no-cache` so edits show on a plain reload.

**Usage tracking is in-memory only.** `UsageTracker` is a singleton with a lock; counters reset on process restart. Cost estimate in `Snapshot()` is hardcoded to DeepSeek V4 Pro retail pricing ($0.27/$1.10 per Mtok) regardless of which model was called. The streaming endpoint only calls `Track` when `outTok > 0`. Usage is keyed by `"{providerId}:{model}"`.

**Tenant AI quota** ([Services/Quota/TenantQuotaStore.cs](Services/Quota/TenantQuotaStore.cs)). Mỗi tenant mặc định 1000 lượt AI (lĩnh 1 lần, KHÔNG tự reset). Storage: in-mem `ConcurrentDictionary` source of truth + ghi đè file `data/tenant-quota.json` mỗi lần thay đổi + mirror Redis best-effort (cross-instance visibility). Provider check ở đầu `CompleteAsync`/`StreamAsync` (5 providers — `EnsureQuota()`); consume ở `LogUsage`/sau khi `_usage.Append` khi status=ok và có tenant. Hết quota → throw `QuotaExhaustedException` → middleware [`QuotaExceptionMiddleware`](Services/Quota/QuotaExceptionMiddleware.cs) convert → 429 JSON `{error, quota}`. Frontend: chip `.tb-quota` ở topbar (`AI <used>/<limit>`), warn ở 90%, pulse đỏ ở 100%. Endpoints: `GET /api/v1/quota` (user), `GET /api/v1/admin/quota` + `POST /api/v1/admin/quota/{tenant}/topup` (admin gate qua `Admin:Token` config). System calls không có tenant (no session) → skip check.

**Cost UI hidden by default.** Menu "Chi phí AI" + page `/ai-usage` chỉ hiện khi user toggle debug ON (icon info ở topbar). URL `/ai-usage` vẫn accessible trực tiếp (giữ cho admin xem nhanh).

**CORS is wide open in dev.** `CorsSetup.cs` lists allowed origins but calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production.

## Code lookup (GitNexus MCP)

Khi câu hỏi liên quan đến **cấu trúc code** (callers/callees, "X dùng ở đâu", flow nghiệp vụ, blast-radius trước khi đổi tên), **PHẢI dùng `mcp__gitnexus__*` trước** `Grep`/`Glob`. GitNexus chạy trên knowledge graph đã build sẵn → nhanh hơn nhiều lần so với re-scan file, và bắt đúng symbol thay vì khớp text mù.

**3 repo đã index** (vì có nhiều repo, MỌI gitnexus call PHẢI truyền `repo`):
- `tourkit-ai-proxy` — project này (proxy + `wwwroot/`).
- `toutkit-app` — TourKit.Api mobile (upstream CRM mà proxy gọi qua `/api/ai/*`).
- `tourkit` — CMS Web KojiCRM (ASP.NET WebForms; nghiệp vụ gốc TourKit).

**Chọn tool:**
- `query` — concept search ("deal scoring flow?", "mail classification owner?"). Trả ranked execution flows.
- `context` — 360° view 1 symbol (callers/callees/overrides). Dùng sau khi `query` thu hẹp.
- `impact` — blast-radius TRƯỚC khi rename/sửa method/field. Liệt kê mọi caller + dependent.
- `cypher` — Cypher trực tiếp khi muốn shape custom (vd "tất cả handler trả `IResult` trong Endpoints/").
- `route_map` / `api_impact` — HTTP routes + cross-repo contract (proxy → TourKit.Api).

**KHI vẫn dùng Grep/Glob:** tìm chuỗi text trong comment / config / JSON / Markdown (graph chỉ index code symbol); list file theo glob; khi repo đang sửa nhiều mà chưa re-index (`gitnexus status` cảnh báo stale).

**Re-index khi stale:** `gitnexus analyze --embeddings` chạy ở root repo. Cross-repo question (proxy ↔ TourKit.Api) → query repo `toutkit-app` cho signature upstream.

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains real-looking API keys. Treat them as secrets: don't echo them, and prefer env vars (e.g. `Providers__OpenCode__ApiKey`, `OPENCODE_API_KEY`, `NINE_ROUTES_API_KEY`) for any production-bound change.
- Frontend exposes singletons via `window.tourkit*` namespaces (`tourkit.ai`, `tourkitStorage`, `tourkitParsers`, `tourkitRouter`, `tourkitHistory`).

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **tourkit-ai-proxy** (7197 symbols, 22686 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## When Debugging

1. `gitnexus_query({query: "<error or symptom>"})` — find execution flows related to the issue
2. `gitnexus_context({name: "<suspect function>"})` — see all callers, callees, and process participation
3. `READ gitnexus://repo/tourkit-ai-proxy/process/{processName}` — trace the full execution flow step by step
4. For regressions: `gitnexus_detect_changes({scope: "compare", base_ref: "main"})` — see what your branch changed

## When Refactoring

- **Renaming**: MUST use `gitnexus_rename({symbol_name: "old", new_name: "new", dry_run: true})` first. Review the preview — graph edits are safe, text_search edits need manual review. Then run with `dry_run: false`.
- **Extracting/Splitting**: MUST run `gitnexus_context({name: "target"})` to see all incoming/outgoing refs, then `gitnexus_impact({target: "target", direction: "upstream"})` to find all external callers before moving code.
- After any refactor: run `gitnexus_detect_changes({scope: "all"})` to verify only expected files changed.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Tools Quick Reference

| Tool | When to use | Command |
|------|-------------|---------|
| `query` | Find code by concept | `gitnexus_query({query: "auth validation"})` |
| `context` | 360-degree view of one symbol | `gitnexus_context({name: "validateUser"})` |
| `impact` | Blast radius before editing | `gitnexus_impact({target: "X", direction: "upstream"})` |
| `detect_changes` | Pre-commit scope check | `gitnexus_detect_changes({scope: "staged"})` |
| `rename` | Safe multi-file rename | `gitnexus_rename({symbol_name: "old", new_name: "new", dry_run: true})` |
| `cypher` | Custom graph queries | `gitnexus_cypher({query: "MATCH ..."})` |

## Impact Risk Levels

| Depth | Meaning | Action |
|-------|---------|--------|
| d=1 | WILL BREAK — direct callers/importers | MUST update these |
| d=2 | LIKELY AFFECTED — indirect deps | Should test |
| d=3 | MAY NEED TESTING — transitive | Test if critical path |

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/tourkit-ai-proxy/context` | Codebase overview, check index freshness |
| `gitnexus://repo/tourkit-ai-proxy/clusters` | All functional areas |
| `gitnexus://repo/tourkit-ai-proxy/processes` | All execution flows |
| `gitnexus://repo/tourkit-ai-proxy/process/{name}` | Step-by-step execution trace |

## Self-Check Before Finishing

Before completing any code modification task, verify:
1. `gitnexus_impact` was run for all modified symbols
2. No HIGH/CRITICAL risk warnings were ignored
3. `gitnexus_detect_changes()` confirms changes match expected scope
4. All d=1 (WILL BREAK) dependents were updated

## Keeping the Index Fresh

After committing code changes, the GitNexus index becomes stale. Re-run analyze to update it:

```bash
npx gitnexus analyze
```

If the index previously included embeddings, preserve them by adding `--embeddings`:

```bash
npx gitnexus analyze --embeddings
```

To check whether embeddings exist, inspect `.gitnexus/meta.json` — the `stats.embeddings` field shows the count (0 means no embeddings). **Running analyze without `--embeddings` will delete any previously generated embeddings.**

> Claude Code users: A PostToolUse hook handles this automatically after `git commit` and `git merge`.

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
