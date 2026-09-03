# Bề mặt API

> Tách khỏi `CLAUDE.md` ngày 25/08/2026 — file đó đã hơn 1.000 dòng nên không ai đọc hết,
> mà quy ước không đọc thì bằng không có. Xem `CLAUDE.md` để biết khi nào cần đọc file này.
> Kiến trúc và luật đặt file: [ARCHITECTURE.md](ARCHITECTURE.md).

---

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

**Nháp tour + đồng bộ CRM** (màn Tính giá Tour):

| Method | Path | Notes |
|--------|------|-------|
| GET/POST/DELETE | `/api/v1/tours[/{id}]` | Nháp tour của công ty (Redis `tkai:tours:{tenant}`, KHÔNG phải SQL) |
| PATCH  | `/api/v1/tours/{id}/status`  | `draft` \| `sent` \| `success` |
| POST   | `/api/v1/tours/{id}/save-crm` | Body `{tourType: 3\|2, customerName?, customerPhone?, customerEmail?}` — tạo ĐƠN THẬT trên CRM: **3 = GIT** → TourKit.Api `/api/ai/tours`, **2 = FIT** → `/api/tours/sample`. Nháp nhớ `crmTourId` → bấm lần hai trả **409** thay vì đẻ đơn trùng |

**Gác quyền theo CRM** (sheet bug dòng 105 — quyền lấy từ chính CRM theo phòng ban, không có admin auto-grant):

| Nhóm endpoint | Quyền cần |
|---|---|
| `/api/v1/tours*` (ghi) + `/api/v1/tour-quotes*` (ghi) | `TR_TD_TAOMOI` **hoặc** `TR_TM_TAOMOI` — riêng `/save-crm` kiểm ĐÚNG loại đơn đang tạo |
| `/api/v1/customers*` + `/api/v1/reviews/*` | `KH_KH_XEM` |
| `/api/v1/visa/*` | `VISA_XEM` — trừ `/visa/questions` (GET/PUT/DELETE) nhận thêm `CH_HT_XEM` vì đó là màn cấu hình |

Gác bằng `RequirePermissionFilter` gắn ở **nhóm** (không phải từng handler) → đường thêm sau tự được gác. Thiếu quyền → **403** kèm mã quyền còn thiếu; chưa đăng nhập vẫn là **401** như cũ.

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

