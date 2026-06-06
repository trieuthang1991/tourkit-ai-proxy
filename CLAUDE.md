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
```

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
    ReviewService.cs                       ← prompt → IAiProvider → tolerant JSON parse → save
    BatchService.cs                        ← Parallel.ForEachAsync (cap 10) → BatchJob.Events channel
    BatchJobStore.cs                       ← in-memory ConcurrentDictionary of running jobs
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
    MailAccountStore.cs                    ← creds Gmail: data/mail-account.json (App Password Crypton-enc) → config/env fallback
    IMailSource.cs                         ← interface nguồn mail (để sau cắm OAuth)
    MailMapper.cs                          ← pure: MimeMessage → MailItem (test được)
    GmailImapClient.cs                     ← IMailSource qua IMAP Gmail (MailKit), incremental theo UID + \Seen→IsRead
    MailSyncStore.cs                       ← state đồng bộ data/mail-sync.json (per-address uidValidity+lastUid)
    IMailSender.cs + GmailSmtpClient.cs    ← gửi (trả lời + soạn mới) qua SMTP Gmail (587, App Password), thread qua In-Reply-To
    MailRepository.cs                      ← file-backed data/mails.json (lock) + Filter/Counts (mẫu ReviewRepository)
    MailClassifier.cs                      ← prompt → provider → parse {category, summary} (tolerant LooseJson)
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
  mails.json                               ← SmartMail cache (gitignored; email + phân loại AI + đọc/chưa đọc)
  mail-account.json                        ← creds Gmail + chữ ký (gitignored; App Password Crypton-encrypted)
  mail-sync.json                           ← state đồng bộ IMAP theo UID (gitignored)
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
| POST   | `/api/v1/reviews/customer/{id}`   | sync review 1 customer; body optional `{forceFresh:bool}` |
| POST   | `/api/v1/reviews/customer/{id}/refresh` | alias for `forceFresh=true`                    |
| POST   | `/api/v1/reviews/batch`           | start batch job → `{jobId, total, streamUrl, cancelUrl}` (max 200 ids) |
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

**Legacy aliases** (`POST /api/ai/complete`, `POST /api/ai/stream`, `GET /api/ai/models`, `GET /api/ai/usage`) point to the same handlers — keep until all clients migrate.

**Request shape** (`CompleteRequest` — flat, NOT OpenAI `messages[]`):
```json
{ "prompt": "...", "provider": "opencode-go", "model": "deepseek-v4-flash",
  "maxTokens": 8192, "temperature": 0.3, "system": "optional override" }
```
- `provider` blank → falls back to `Providers:Default` in config, then first registered.
- `system` blank → backend injects anti-reasoning prompt (see `OpenCodeClient.DefaultSystem`).
- `temperature` default `0.3` (tuned for JSON/structured output).
- `apiKey` optional: client may send a per-request key for BYO-key providers (OpenAI/Anthropic). `OpenAIProvider`/`AnthropicProvider` use `req.ApiKey ?? ProviderKeyStore.Get(id)` (client key wins, config is fallback). Key is used transiently and **never persisted server-side**. The frontend stores these keys in `localStorage["tourkit_ai_keys"]` (per the project owner's explicit choice — note the XSS tradeoff) and sends them via `apiKey` on `/completions`, `/completions/stream`, and `/chat`.

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

## Customer Review feature

AI grades a customer (rank A–D, alert level, strengths/concerns, action-now + 30-day ideas, product suggestions) and persists the result. Flows through `ReviewEndpoints` → `ReviewService` → a provider → `ReviewRepository`.

- **Storage is file-backed, not a DB.** Customers are read-only from `data/customers.seed.json` (`CustomerRepository`, loaded once at startup). Reviews persist to `data/reviews.json` (`ReviewRepository`, lock-guarded, camelCase JSON to match the JS frontend). Both are explicitly MVP placeholders — swap for EF/Dapper/SQLite to scale. `reviews.json` is mutable runtime state.
- **Caching via data fingerprint.** `ReviewRepository.FingerprintFor(customer)` is a SHA-256 (first 32 hex) of the canonical customer JSON. `ReviewService.ReviewAsync` returns the cached review (no AI call) when the stored `DataFingerprint` matches and `forceFresh` is false. The customer-list endpoint reports `fresh`/`stale`/`none` by comparing fingerprints.
- **Buffered, not streamed, to the model.** `ReviewService` deliberately calls `CompleteAsync` (not `StreamAsync`): DeepSeek/Kimi reasoning models interleave `reasoning_content` with `content`, so streaming would mix prose into the JSON. The `onStage` callback (`preparing` → `calling` → `parsing`) still gives the UI lifecycle visibility without live chunks. Uses the **default provider** (`Resolve(null)`), `maxTokens: 8000`, `temperature: 0.4`, and a tour-operator `SYSTEM_PROMPT` — change the industry by editing that const + the rank criteria in `BuildPrompt`.
- **Tolerant JSON parse.** `ParseReviewJson` strips ``` fences, trims to the first balanced top-level `{...}` object (string/escape aware), and does case-insensitive + camelCase/snake_case key lookup. Missing fields fall back to sensible defaults rather than throwing.
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

- **Source = Gmail IMAP via MailKit, NOT OAuth.** `GmailImapClient` (implements `IMailSource`) connects `imap.gmail.com:993` read-only with an **App Password** (requires Gmail 2-Step Verification + IMAP enabled). The interface keeps OAuth swappable later. Creds resolved by `MailAccountStore`: persisted `data/mail-account.json` (App Password Crypton-encrypted, never plaintext, never returned to client) entered via UI → fallback `Mail:Gmail:Address`/`AppPassword` config or `MAIL_GMAIL_ADDRESS`/`MAIL_GMAIL_APP_PASSWORD` env.
- **Sync is on-demand (Refresh button), not a background poller.** `POST /mail/sync` is **incremental theo UID** (`MailSyncStore` lưu `data/mail-sync.json` per-address `{uidValidity, lastUid}`): chỉ kéo email có UID > lần trước → KHÔNG sót dù >N email mới giữa 2 lần sync. Lần đầu/khi UidValidity đổi → kéo `max` (30) mới nhất. Cờ `\Seen` của Gmail map sang `IsRead` lúc kéo. Vẫn **classify chỉ email MỚI** (`repo.Has(id)` skip → tiết kiệm token). Email id = Message-Id (MimeKit chuẩn hóa/tự sinh), fallback `{address}:{uid}`.
- **Đọc/chưa đọc:** `POST /mail/{id}/read` đánh dấu đã đọc khi mở; `MailCounts.Unread` cho badge. Frontend in đậm + chấm cam dòng chưa đọc.
- **Soạn thư MỚI:** `POST /mail/compose/draft` (SSE, AI viết từ `brief`) + `/mail/compose/send` (gửi tới người nhận bất kỳ) — `MailReplyService.ComposeNewStreamAsync` + `IMailSender.SendAsync`. Chữ ký công ty (`MailAccountStore.Signature()`, cấu hình ở UI, lưu trong `mail-account.json`) được dệt vào prompt soạn.
- **Classification + reply reuse `ProviderRegistry`.** `MailClassifier.ClassifyAsync` (buffered, mẫu `ReviewService`) → `{category, summary}`; 6 categories normalized to a known set (lạ → `khac`). `MailReplyService.DraftStreamAsync` streams a tone-aware draft (4 tones) + staff instruction via `provider.StreamAsync`, saves the draft + flips status → `dang_xu_ly`. Both client AI prefs (`provider`/`model`/`apiKey`) flow through like the other features.
- **Sending = SMTP Gmail (`IMailSender`/`GmailSmtpClient`).** `POST /mail/{id}/reply/send` gửi nội dung (đã sửa) tới người gửi gốc qua `smtp.gmail.com:587` STARTTLS bằng chính App Password — gửi AS the company Gmail, nên KHÔNG dính SPF/DKIM/spam như giả mạo domain. Gắn `In-Reply-To`/`References` để vào đúng luồng. Gửi xong → lưu nội dung + status `da_phan_hoi`. Frontend confirm trước khi gửi.
- **Storage = file-backed `data/mails.json`** (`MailRepository`, lock-guarded, camelCase, `Filter`/`Counts` with diacritics-insensitive search). MVP placeholder — swap for DB to scale.
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

**Frontend reaches AI via `window.claude.complete` or `window.tourkit.ai.complete`/`completeStream`.** `core/ai-provider.jsx` shims `window.claude.complete` to delegate to `window.tourkit.ai`, which POSTs to `/api/v1/completions`. **Provider keys for OpenCode/9routes live server-side in `appsettings.json` / env vars.** Exception: OpenAI/Anthropic (BYO-key) keys are entered in the AI Settings UI and stored **client-side** in `localStorage["tourkit_ai_keys"]`, sent per-request via `apiKey`, and used transiently server-side (not persisted) — this was an explicit owner decision overriding the "no keys in client" default; the XSS tradeoff is documented in the AI Settings dialog. `localStorage["tourkit_ai_config"]` only holds `{provider, model, _v}`. Bump `CONFIG_VERSION` in `ai-provider.jsx` when changing the shape.

**Static files.** `UseStaticFiles` has `ServeUnknownFileTypes = true` + `DefaultContentType = "text/plain"` so `.jsx` loads without a registered MIME type. `.jsx`/`.js`/`.css`/`.html` are served with `Cache-Control: no-cache` so edits show on a plain reload.

**Usage tracking is in-memory only.** `UsageTracker` is a singleton with a lock; counters reset on process restart. Cost estimate in `Snapshot()` is hardcoded to DeepSeek V4 Pro retail pricing ($0.27/$1.10 per Mtok) regardless of which model was called. The streaming endpoint only calls `Track` when `outTok > 0`. Usage is keyed by `"{providerId}:{model}"`.

**CORS is wide open in dev.** `CorsSetup.cs` lists allowed origins but calls `SetIsOriginAllowed(_ => true)`, which overrides the allowlist. Remove that line before production.

## Conventions

- User-facing strings, log messages, comments, and README are in Vietnamese — preserve that when editing.
- `appsettings.json` currently contains real-looking API keys. Treat them as secrets: don't echo them, and prefer env vars (e.g. `Providers__OpenCode__ApiKey`, `OPENCODE_API_KEY`, `NINE_ROUTES_API_KEY`) for any production-bound change.
- Frontend exposes singletons via `window.tourkit*` namespaces (`tourkit.ai`, `tourkitStorage`, `tourkitParsers`, `tourkitRouter`, `tourkitHistory`).
