# RESTful API refactor + service-layer dedup + docs/ — Design Spec

> Trạng thái: **đã brainstorm xong, chờ chủ dự án review spec → writing-plans.** Chưa code.
> Ngày: 2026-06-08. Phụ thuộc: native function-calling đã hoàn tất (5 commit `2cec9dc` → `92ed3f0`).

## Goal

Chuyển toàn bộ API surface (60 endpoint, 11 file) sang chuẩn RESTful pragmatic, áp dụng SOLID qua MinimalAPI endpoint filters, rút gọn ~1000 LOC duplicate, và xuất bản `docs/` đầy đủ về kiến trúc + flow nghiệp vụ từng tính năng để team mới onboard không cần đọc code.

## Quyết định kiến trúc đã chốt (brainstorm)

| # | Quyết định |
|---|---|
| Scope | **Toàn bộ API** (~57 endpoint), KHÔNG giới hạn ở Reviews. |
| Framework | **Giữ MinimalAPI** (.NET 8). Tổ chức lại theo resource group + endpoint filters cho cross-cutting. Không chuyển Controllers/MediatR. |
| REST level | **Pragmatic REST** + RFC 7807 ProblemDetails + OpenAPI/Swagger auto. KHÔNG HATEOAS/JSON:API. RPC giữ cho AI workflow (`/completions`, `/chat`). |
| Backward compat | **Break ngay**, update frontend trong cùng commit. KHÔNG giữ alias legacy. Xóa hết `/api/ai/*` (4 endpoint). |
| Resource naming | Singular giữ singleton (account, current session, review). Plural cho collection. English consistent (vd `ncc` → `suppliers`). |
| Action verbs | Google colon style (`POST /admin/reviews:backfill-tenant`) cho admin operations không fit CRUD. |
| Dedup order | **Service-layer dedup TRƯỚC RESTful** (chỉ động Services/, không động endpoint). Endpoint-layer dedup TỰ ĐỘNG bị nuốt vào RESTful Phase 1 (Filters/Common). |

## Phases

- **0 — Service-layer dedup**: DualPathScorer + JsonElementExtensions + CommonPromptParts. ~650 LOC ↓, API surface không đổi.
- **1 — RESTful infra**: `Endpoints/Filters/`, `Endpoints/Common/`, ProblemDetails + OpenAPI + `ITenantContext`. Split `Program.cs`. ~365 LOC ↓ qua filter dedup. Endpoint URL chưa đổi.
- **2 — RESTful rename**: 1 commit/feature (Auth, Customer+Review, ReviewBatch, Mail, Visa, Deal, Chat, Tour, Usage). Endpoint file + frontend page update cùng commit. **Breaking change.**
- **3 — OpenAPI polish**: metadata (`.WithName().Produces<T>()`) + DTO DataAnnotation validation. Non-breaking.
- **4 — Docs**: `docs/` folder đầy đủ (architecture / features / guides / api). Non-breaking.

---

## #1 — URL rewrite map (60 → 55 endpoint)

**Legend:** K=Keep, R=Rename, S=Reshape (verb/body), X=Remove. Tổng remove: 6 (4 legacy `/api/ai/*` + 2 subsumed `/reviews/customer/{id}/refresh`, `/api/v1/ai/usage/log`). Tổng add: 1 (DELETE `/sessions/current` logout).

### Auth / Sessions (split khỏi `ChatEndpoints.cs` → `AuthEndpoints.cs`)

| | Cũ | Mới |
|---|---|---|
| R | POST `/api/v1/login` | POST `/api/v1/sessions` (creds: `{username,password,domain}`) |
| R | POST `/api/v1/login-token` | POST `/api/v1/sessions:by-token` (action style, body `{token}`) |
| R | GET `/api/v1/session` | GET `/api/v1/sessions/current` |
| + | (mới) | DELETE `/api/v1/sessions/current` (logout — hiện chưa có) |

### Providers / Models / Completions / Usage

| | Cũ | Mới |
|---|---|---|
| K | GET `/api/v1/providers`, `/providers/{id}/models`, `/models` | giữ |
| K | POST `/api/v1/completions`, `/completions/stream` | giữ (SSE pragmatic) |
| K | GET `/api/v1/usage` (snapshot in-memory) | giữ |
| R | GET `/api/v1/ai/usage` (jsonl log per-request) | GET `/api/v1/usage/log` |
| X | GET `/api/v1/ai/usage/log` (duplicate cũ) | xóa, merge vào `/usage/log` |
| K | GET `/api/v1/workflow-traces` | giữ |
| X | GET/POST `/api/ai/{models,usage,complete,stream}` (4 legacy aliases) | xóa toàn bộ |

### Customers / Reviews

| | Cũ | Mới |
|---|---|---|
| R | GET `/api/v1/customers/lookups` | GET `/api/v1/customers/lookup-tables` |
| K | GET `/api/v1/customers`, `/customers/{id}` | giữ |
| **S** | POST `/api/v1/reviews/customer/{id}` | **PUT** `/api/v1/customers/{id}/review` (idempotent singleton 1-1) |
| X | POST `/api/v1/reviews/customer/{id}/refresh` | xóa (subsumed bởi PUT body `forceFresh:true`) |
| R | POST `/api/v1/reviews/{id}/feedback` | POST `/api/v1/customers/{id}/review/feedback` |
| R | POST `/api/v1/reviews/admin/backfill-tenant` | POST `/api/v1/admin/reviews:backfill-tenant` |
| R | POST `/api/v1/reviews/batch` | POST `/api/v1/review-batches` |
| R | GET `/api/v1/reviews/batch/{id}/stream` | GET `/api/v1/review-batches/{id}/events` |
| **S** | POST `/api/v1/reviews/batch/{id}/cancel` | **DELETE** `/api/v1/review-batches/{id}` |

### Chat

| | Cũ | Mới |
|---|---|---|
| K | POST `/api/v1/chat`, `/chat/stream` | giữ (RPC cho AI + SSE) |
| R | DELETE `/api/v1/chat/memory` | DELETE `/api/v1/sessions/current/chat-memory` |
| R | GET `/api/v1/chat/unresolved` | GET `/api/v1/chat/unresolved-questions` |
| **S** | POST `/api/v1/chat/cache/clear` | **DELETE** `/api/v1/chat/cache` |

### Mail

| | Cũ | Mới |
|---|---|---|
| K | GET `/api/v1/mail/account` | giữ (singleton) |
| **S** | POST `/api/v1/mail/account` | **PUT** `/api/v1/mail/account` (upsert idempotent) |
| R | POST `/api/v1/mail/sync` | POST `/api/v1/mail/syncs` |
| R | GET `/api/v1/mail` | GET `/api/v1/mails` (plural) |
| R | GET `/api/v1/mail/{id}` | GET `/api/v1/mails/{id}` |
| **S** | POST `/api/v1/mail/{id}/read` | **PATCH** `/api/v1/mails/{id}` body `{isRead:true}` |
| **S** | PATCH `/api/v1/mail/{id}/status` | merge vào PATCH `/api/v1/mails/{id}` body `{status:"..."}` |
| R | POST `/api/v1/mail/{id}/reply/draft` (SSE) | POST `/api/v1/mails/{id}/reply-drafts` |
| R | POST `/api/v1/mail/{id}/reply/send` | POST `/api/v1/mails/{id}/replies` |
| R | POST `/api/v1/mail/compose/draft` (SSE) | POST `/api/v1/mail-drafts` |
| R | POST `/api/v1/mail/compose/send` | POST `/api/v1/mails` (create + send) |

### Visa

| | Cũ | Mới |
|---|---|---|
| R | POST `/api/v1/visa/assess` (multipart upload) | POST `/api/v1/visa-assessments` |
| **S** | POST `/api/v1/visa/assess/{id}/score` | **PUT** `/api/v1/visa-assessments/{id}/score` (singleton, re-score = replace) |
| R | GET `/api/v1/visa/assessments[/{id}]` | GET `/api/v1/visa-assessments[/{id}]` |
| K | DELETE `/api/v1/visa/assessments/{id}` | giữ verb, sửa path → DELETE `/api/v1/visa-assessments/{id}` |

### Deals

| | Cũ | Mới |
|---|---|---|
| R | POST `/api/v1/deals/analyze` | POST `/api/v1/deal-batches` |
| R | GET `/api/v1/deals/analyze/{id}/stream` | GET `/api/v1/deal-batches/{id}/events` |
| **S** | POST `/api/v1/deals/analyze/{id}/cancel` | **DELETE** `/api/v1/deal-batches/{id}` |
| R | GET `/api/v1/deals/board` | GET `/api/v1/deals` |

### Tours / Suppliers (TenantStore)

| | Cũ | Mới |
|---|---|---|
| K | GET/POST/DELETE `/api/v1/tours[/...]` | giữ (đã RESTful) |
| R | GET `/api/v1/ncc/categories` | GET `/api/v1/supplier-categories` |
| R | GET `/api/v1/ncc/providers` | GET `/api/v1/suppliers` |
| R | GET `/api/v1/ncc/providers/{id}/services` | GET `/api/v1/suppliers/{id}/services` |

### Tour Builder

| | Cũ | Mới |
|---|---|---|
| R | POST `/api/v1/tour-builder/parse` | POST `/api/v1/tour-drafts` |

### System

| | Cũ | Mới |
|---|---|---|
| K | GET `/healthz` | giữ (k8s probe convention) |

---

## #2 — Folder structure + SOLID

### New layout

```
Endpoints/
  Filters/                                ← IEndpointFilter
    AuthFilter.cs                         — 401 nếu missing/invalid session, set HttpContext.Items["session"]
    TenantFilter.cs                       — resolve TenantId từ session, set HttpContext.Items["tenantId"]
    ValidationFilter.cs                   — chạy DataAnnotations trước handler, trả 400 ProblemDetails
  Common/
    SessionAccessor.cs                    — extension HttpContext.GetSession() / GetTenantId()
    ProblemDetailsFactory.cs              — helper tạo ProblemDetails với type URL convention
    SseWriter.cs                          — extract header setup + WriteAsync loop (5 chỗ dup hiện tại)
    TourKitJwtExecutor.cs                 — wrap `ExecuteWithJwtRefresh<T>(sid, op)` cho 17 chỗ try-catch 401 + re-login
  Extensions/
    EndpointRouteBuilderExtensions.cs     — `.MapXxxFeature()` extension methods, gọi trong Program.cs
    ServiceCollectionExtensions.cs        — `services.AddXxxFeature()` thay 50 dòng AddSingleton<>
  v1/                                     — version namespace (future-proof v2)
    AuthEndpoints.cs                      ← POST/GET/DELETE /sessions
    ProviderEndpoints.cs                  ← /providers, /models
    CompletionEndpoints.cs                ← /completions[/stream]
    CustomerEndpoints.cs                  ← /customers[/{id}], /customers/lookup-tables
    ReviewEndpoints.cs                    ← PUT /customers/{id}/review + feedback + admin
    ReviewBatchEndpoints.cs               ← /review-batches/*
    ChatEndpoints.cs                      ← /chat, /chat/cache, /chat/unresolved-questions
    MailEndpoints.cs                      ← /mails, /mail/account, /mail/syncs, drafts, replies
    VisaEndpoints.cs                      ← /visa-assessments[/{id}/score]
    DealEndpoints.cs                      ← /deal-batches/*, /deals
    TourEndpoints.cs                      ← /tours, /suppliers, /supplier-categories
    TourBuilderEndpoints.cs               ← /tour-drafts
    UsageEndpoints.cs                     ← /usage, /usage/log, /workflow-traces
    SystemEndpoints.cs                    ← /healthz
  RequestDtos/                            — 1 DTO per endpoint với DataAnnotations
  ResponseDtos/                           — shape chuẩn data + meta (khác Models/ business records)
```

### SOLID checklist

| Nguyên tắc | Áp dụng |
|------------|---------|
| **S**RP | 1 file = 1 resource (split `ReviewEndpoints` → Review + ReviewBatch; split `ChatEndpoints` → Auth + Chat). |
| **O**CP | Thêm endpoint = add vào group file, KHÔNG sửa filter/Program. Thêm provider/agent = implement interface (đã có). |
| **L**SP | `IReviewAgent`, `IAiProvider`, `IMailSource` đã có, giữ. |
| **I**SP | DTO per-endpoint thay vì share `JsonElement body`. `ITenantContext` interface nhỏ inject thay vì `TkSessionStore` toàn bộ. |
| **D**IP | Endpoint filter dùng `ITenantContext` injected. Service tiếp tục depend abstraction. |

### Cross-cutting infra

| Concern | Component | Impact |
|---------|-----------|--------|
| Auth | `AuthFilter` áp ở group level: `v1.MapGroup("/customers").AddEndpointFilter<AuthFilter>()` | Thay 17 lần lặp `Sid(ctx)` + `sessions.Get(sid)==null → 401` |
| Tenant | `TenantFilter` chạy sau AuthFilter | Endpoint inject `ITenantContext` thay vì `sessions.Get(sid)?.TenantId ?? ""` lặp |
| Error shape | `ProblemDetailsExceptionHandler` cấp app + `app.UseExceptionHandler()` | RFC 7807 JSON. Type URLs: `https://api.tourkit.vn/errors/{slug}` (unauthorized, not-found, invalid-payload, upstream-failure...) |
| Validation | `ValidationFilter<TDto>` + DataAnnotations | 400 ProblemDetails với extension `errors:{field:[msg]}` |
| OpenAPI | `Microsoft.AspNetCore.OpenApi` + Swashbuckle UI | `/swagger` (UI), `/swagger/v1/swagger.json`. Metadata qua `.WithName().WithSummary().Produces<T>(200).ProducesProblem(401)` |
| SSE | `SseWriter` extension methods trên `HttpContext` | 5 chỗ duplicate → 1 helper với `await ctx.WriteSseHeadersAsync()` + `await ctx.WriteSseDataAsync(payload)` |
| TourKit 401 retry | `TourKitJwtExecutor.ExecuteAsync<T>(sid, async jwt => await api.Get(jwt, ...))` | 17 chỗ dup → 1 helper tự re-login + retry 1 lần |

---

## #3 — Service-layer dedup (Phase 0 detail)

### Group 3: AI dual-path dispatch (Visa/Deal/Tour/Mail)

**Hiện trạng:** 4 service đều có pattern:
```csharp
public async Task<T> ScoreAsync(...) {
    var trace = ...; var p = _registry.Resolve(provider); ...
    var key = AiResponseCache.Hash(...); 
    var cached = _cache.TryGet<T>(key); if (cached != null) return cached;
    
    T result;
    if (p.Id == "anthropic") result = await ScoreWithNativeToolAsync(...);
    else result = await ScoreWithJsonPromptAsync(...);
    
    _cache.Save(key, result); return result;
}

private async Task<T> ScoreWithJsonPromptAsync(...) {
    Exception? last = null;
    for (int attempt = 1; attempt <= 2; attempt++) {
        var prompt = attempt == 1 ? BuildPromptJson(...) : BuildPromptJson(...) + "\n\nLƯU Ý: ...";
        var req = new CompleteRequest(..., MaxTokens: attempt == 1 ? A : B, ...);
        var aiTimer = trace?.Begin($"ai_attempt{attempt}");
        try {
            var res = await p.CompleteAsync(req, ct);
            if (string.IsNullOrWhiteSpace(res.Text)) throw new ...;
            return ParseRawText(res.Text) with { AiModel = res.Model, AiProvider = p.Id };
        }
        catch (InvalidOperationException ex) { last = ex; ... }
    }
    throw last ?? ...;
}

private async Task<T> ScoreWithNativeToolAsync(...) {
    var res = await _native.RunAsync<T>(systemNative, userPrompt, schema, terminalName, parser, apiKey, model, maxTokens, trace, ct);
    return res.Value with { AiModel = res.Model, AiProvider = "anthropic" };
}
```

**Đề xuất:**

```csharp
// Services/Workflow/JsonPromptScorer.cs (NEW)
public class JsonPromptScorer {
    public async Task<T> RunAsync<T>(
        IAiProvider provider, string systemPrompt,
        Func<int, string> buildPrompt,        // attempt → prompt string
        Func<string, T> parser,
        int maxTokensA, int maxTokensB, double temperature,
        string? apiKey, TraceCollector? trace, CancellationToken ct,
        ILogger? log = null) { ... 2-attempt retry loop generic ... }
}

// Services/Workflow/DualPathScorer.cs (NEW)
public class DualPathScorer {
    public DualPathScorer(ProviderRegistry registry, AiResponseCache cache,
        NativeToolScorer native, JsonPromptScorer json, IWorkflowTraceAccessor trace) { ... }
    
    public async Task<T> RunAsync<T>(
        DualPathConfig<T> config,
        string? providerOverride, string? modelOverride, string? apiKeyOverride,
        CancellationToken ct) where T : class {
        // 1. Resolve provider, trace setup
        // 2. Cache lookup (skip if cacheKey null)
        // 3. Dispatch anthropic → native; else → json
        // 4. Cache save
        // 5. Return
    }
}

public record DualPathConfig<T>(
    string Workflow,                              // "VisaScoring" → trace
    string? CacheKey,                             // null = skip cache
    string SystemForJson, string SystemForNative,
    Func<string> BuildJsonPrompt,                 // attempt-agnostic (retry handled inside)
    Func<string> BuildNativePrompt,
    JsonElement ToolSchema, string TerminalToolName,
    Func<string, T> ParseFromRawText,
    Func<JsonElement, T> ParseFromToolInput,
    string DefaultModel,
    int MaxTokensJsonA = 2500, int MaxTokensJsonB = 3200,
    int MaxTokensNative = 3000,
    double Temperature = 0.3);
```

**Kết quả mỗi service:**
```csharp
public async Task<VisaResult> ScoreAsync(...) {
    return await _dual.RunAsync(new DualPathConfig<VisaResult>(
        Workflow: "VisaScoring",
        CacheKey: AiResponseCache.Hash("visa-score", model, $"{country}|{profile}"),
        SystemForJson: VisaPrompts.SystemJson, SystemForNative: VisaPrompts.SystemNative,
        BuildJsonPrompt: () => VisaPrompts.BuildJson(profile, country),
        BuildNativePrompt: () => VisaPrompts.BuildNative(profile, country),
        ToolSchema: VisaPrompts.SubmitVisaScoreSchema, TerminalToolName: "submit_visa_score",
        ParseFromRawText: ParseRawText, ParseFromToolInput: ParseToolInput,
        DefaultModel: "claude-sonnet-4-5"),
        provider, model, apiKey, ct);
}
```

Mỗi service từ ~250 LOC → ~50 LOC + 1 file `XxxPrompts.cs` chứa schema + prompts.

### Group 4: JSON helpers (8 file dup)

```csharp
// Services/Json/JsonElementExtensions.cs (NEW)
public static class JsonElementExtensions {
    public static bool TryGetField(this JsonElement el, string name, out JsonElement value) { ... case-insensitive }
    public static string? GetStringField(this JsonElement el, string name) { ... }
    public static int GetIntField(this JsonElement el, string name, int defaultValue = 0) { ... }
    public static long GetLongField(this JsonElement el, string name) { ... }
    public static double GetDoubleField(this JsonElement el, string name) { ... }
    public static List<string> GetStringListField(this JsonElement el, string name) { ... }
    public static T? GetObjectField<T>(this JsonElement el, string name, Func<JsonElement, T> parser) { ... }
}
```

Xóa hết private helpers ở 8 file (VisaScoringService, DealScoringService, TourBuilderService, VisaExtractionService, ReviewPrompt, TourKitApiClient, DealOpportunityClient, TourEndpoints).

### Group 5: Cache+retry — subsumed bởi DualPathScorer.

### Group 6: System prompt overlap

```csharp
// Services/Prompts/CommonPromptParts.cs (NEW)
public static class CommonPromptParts {
    public const string TourkitContext = 
        "Bạn là chuyên gia trong công ty du lịch / tour operator Việt Nam (Tourkit).";
    public const string JsonOutputRules =
        "Output ONLY raw JSON, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Ký tự đầu tiên BẮT BUỘC là '{'.";
    public const string NativeToolRules =
        "Phân tích → gọi tool kết quả. KHÔNG suy diễn ngoài dữ liệu; " +
        "thiếu data ghi 'Chưa đủ dữ liệu'. KHÔNG trả text giải thích ngoài tool.";
    public const string VietnameseStyle =
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng.";
}
```

Mỗi feature prompt: `TourkitContext + " " + <feature-specific> + " " + JsonOutputRules/NativeToolRules + " " + VietnameseStyle`.

---

## #4 — Docs deliverable

### Structure

```
docs/
  README.md                                ← index: "đọc theo thứ tự nào, ai dành cho ai"
  architecture/
    01-overview.md                         ← layered architecture, request lifecycle 1 trang
    02-request-flow.md                     ← sequence Mermaid: client → filter → endpoint → service → AI → response
    03-native-function-calling.md          ← deep dive dual-path (mở rộng CLAUDE.md section)
    04-session-and-tenant.md               ← Crypton token → JWT → memory (tk-sessions.json)
    05-cache-layers.md                     ← L1/L2 chat cache, Redis/in-memory, fingerprint cache Review
    06-error-handling.md                   ← RFC 7807 convention + type URLs catalog
    07-tracing-and-debug.md                ← ?debug=1 → TraceCollector → workflow-traces.jsonl
  features/
    01-customer-review.md                  ← KH thật → fingerprint → AI agent → batch SSE → feedback
    02-visa-assessment.md                  ← upload PDF → vision extract → text score → cache 24h
    03-deal-scoring.md                     ← batch analyze → AI rank → board ưu tiên
    04-tour-builder.md                     ← NL Sale → JSON Tour GIT prefill
    05-mail-smartmail.md                   ← IMAP sync → AI classify → reply drafts SSE → SMTP send
    06-chat-analytics.md                   ← planner → tool catalog → CRM fetch → AI analysis (multi-step v2)
    07-auth-sessions.md                    ← 2 login flow + JWT lifecycle + auto re-login + persistence
    template.md                            ← template feature mới (8 mục bắt buộc)
  guides/
    01-adding-a-restful-endpoint.md        ← checklist: file location, filter, DTO, OpenAPI metadata, frontend
    02-adding-an-ai-provider.md            ← IAiProvider + DI register + per-provider quirks
    03-adding-a-review-agent.md            ← IReviewAgent strategy hoặc in-service routing
    04-switching-to-anthropic.md           ← bật native function-calling cho 5 service
    05-debugging-with-trace.md             ← ?debug=1 → đọc trace → common scenarios
    06-running-locally.md                  ← dotnet run, appsettings, troubleshoot
  api/
    swagger-redirect.md                    ← link /swagger (single source of truth auto)
    error-codes.md                         ← bảng ProblemDetails type URLs + ý nghĩa
```

### Feature doc template (8 mục bắt buộc)

1. **Mục đích nghiệp vụ** (1 đoạn: ai dùng, giải quyết bài toán gì)
2. **Sequence diagram** (Mermaid: actor → component → response)
3. **API endpoints** (bảng method/path/body/response)
4. **Data flow** (cache layers, persistence, fingerprint, JWT)
5. **DI dependencies** (service nào inject service nào)
6. **Edge cases** (retry, cancel, cache miss, JWT expiry)
7. **Trace example** (1 đoạn `workflow-traces.jsonl` với chú thích từng bước)
8. **Common debug scenarios** ("tại sao review không update", "tại sao classify ra 'khac'")

---

## #5 — Rollout phasing

| Phase | Scope | Effort | Commits | Breaking? |
|-------|-------|--------|---------|-----------|
| 0 | Service-layer dedup (Group 3/4/5/6) | ~2h | 1-2 | Không |
| 1 | RESTful infra: Filters/, Common/, ProblemDetails, OpenAPI, ITenantContext, Program.cs split | ~3h | 1-2 | Không |
| 2 | Endpoint rename per-resource (Auth, Customer+Review, ReviewBatch, Mail, Visa, Deal, Chat, Tour, Usage) + frontend update | ~4h | 8-10 | **CÓ, per resource** |
| 3 | OpenAPI metadata + DTO DataAnnotation polish | ~1h | 1 | Không |
| 4 | `docs/` folder đầy đủ (architecture + features + guides + api) | ~3-4h | 1-2 | Không |

**Total:** ~13h, ~15-20 commit, multiple sessions.

### Per-commit safety

- `dotnet build` clean
- `dotnet test` 96/96 pass
- Smoke test endpoint affected (`dotnet run` + curl)
- Frontend page manual test cho Phase 2
- Mỗi commit revert độc lập

### CLAUDE.md cập nhật

Mỗi phase cập nhật CLAUDE.md tương ứng:
- Phase 0: bảng "Native function-calling" cập nhật reference tới DualPathScorer
- Phase 1: section mới "API conventions" (filters, ProblemDetails, OpenAPI)
- Phase 2: bảng API surface rewrite hoàn toàn
- Phase 4: thêm pointer `→ Xem chi tiết tại docs/features/...`

---

## #6 — Acceptance criteria

- Toàn bộ 51 endpoint follow `[verb] /api/v1/[resource]/[id]/[sub-resource]` pattern (trừ admin `:action` + AI RPC giữ).
- 0 verb trong URL ngoại lệ noted (admin colon actions).
- Mọi error response = RFC 7807 ProblemDetails với type URL `https://api.tourkit.vn/errors/{slug}`.
- OpenAPI `/swagger` UI accessible, swagger.json generate từ MinimalAPI metadata.
- Mọi endpoint cần auth = áp `AuthFilter` qua group, KHÔNG còn `Sid(ctx)` inline.
- Mọi endpoint cần TourKit JWT = dùng `TourKitJwtExecutor.ExecuteAsync`, KHÔNG còn try-catch 401 inline.
- Mọi SSE endpoint = dùng `SseWriter` extension, KHÔNG còn duplicate header setup.
- 4 service Visa/Deal/Tour/Mail dùng `DualPathScorer` thay vì own dispatch + retry loop.
- 0 file còn private JSON helper TryGet/Str/Int/StrList — tất cả dùng `JsonElementExtensions`.
- Frontend mọi page update sang URL mới, không gọi URL cũ.
- `docs/` đủ 4 folder (architecture / features / guides / api), 7 feature doc đủ 8 mục template.
- 96/96 tests pass + smoke test endpoint mới.

---

## #7 — Risks + rollback

| Risk | Mitigation |
|------|-----------|
| Phase 2 break frontend production | Per-resource commit; frontend update cùng commit; revert commit = revert cả 2 |
| DualPathScorer abstraction sai (vd cache key mismatch) | Phase 0 chạy `dotnet test` + smoke 1 Visa/Deal call mỗi commit; rollback nếu fail |
| Filter chạy ngược thứ tự (AuthFilter sau TenantFilter) | Tests integration cho 401 unauthorized + 403 wrong tenant ở Phase 1 |
| OpenAPI metadata sai → swagger.json corrupt | Phase 3 chạy `curl /swagger/v1/swagger.json | jq` mỗi commit |
| Docs outdated nhanh sau Phase 4 | CLAUDE.md có pointer tới docs/; mỗi commit tương lai phải update docs nếu thay đổi behavior |

---

## #8 — Out of scope (defer)

- Pagination chuẩn (RFC 5988 Link header / `?page=&size=`) — anh đã chốt không cần
- FluentValidation thay DataAnnotations — anh đã chốt option 1
- HATEOAS / JSON:API — đã chốt pragmatic
- Idempotency-Key cho POST — chưa cần
- Rate limiting — chưa scope
- API v2 song song — đã chốt break-now
- Authentication mới (vd OAuth) — giữ Crypton + JWT TourKit
- Migration của AiUsageLog từ JSONL → DB — out of scope
- WebSocket thay SSE — SSE đủ
