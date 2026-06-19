# Centralize AI Model Config — Phase 1

**Date:** 2026-06-19
**Status:** Design — chờ approve
**Phase:** 1/2 (phase 2 = centralize implementation gateway, spec riêng)

## 1. Vấn đề

Cấu hình "feature nào dùng provider/model nào" hiện phân mảnh ở 4 chỗ:

- `appsettings.json` → `Providers:{X}:ApiKey` (legacy per-provider key)
- `appsettings.json` → `Models:Primary` + `Models:Review` (chỉ 2 bucket cho ~12 feature)
- `appsettings.json` → `Speech:*` (section riêng, schema khác)
- Hardcode trong code: `"claude-sonnet-4-5"` ở `NativeToolUseAgent`, `DealScoringService`, `TourBuilderService`, `NativeToolScorer`, `AnthropicToolsClient`, `NativeToolReviewAgent`; `"claude-haiku-4-5"` ở `VisaScoringService` — giá trị KHÁC nhau cho cùng "Primary" → bug-tiềm-ẩn.

Hệ quả:
- Operator không thể trả lời "feature X đang dùng model gì" bằng cách đọc 1 file config.
- Đổi model cho 1 feature = grep + sửa code (vd Visa hiện dùng `claude-haiku-4-5` hardcode, khác Primary).
- `ProviderKeyStore.Get()` có 4-tier fallback cross-reference (`Providers:*` → `Models:Primary:*` → `Models:Review:*` → env), khó trace nguồn key cuối.

## 2. Mục tiêu

1. **1 nguồn duy nhất** trong `appsettings.json` cho mọi feature: section `Models:` liệt kê đủ ~12 feature; section `Providers:` chỉ chứa key fallback dùng chung.
2. **Primary là default** — feature không khai báo (null/missing) → tự kế thừa Primary.
3. **Per-feature override key** — có thể tách `ApiKey` riêng cho feature (vd account billing khác).
4. **0 hardcode model** trong service/agent layer — mọi default model do `AiModelRegistry` resolve.
5. **0 lookup `Models:*` config trực tiếp** ngoài `AiModelRegistry` — grep `_cfg["Models:` ngoài Registry phải = 0.

## 3. Non-goals (phase 2 mới làm)

- Gộp orchestration `Visa/Deal/Tour/Review/MailClassify` thành 1 `AiCallGateway`.
- Xóa strategy `IReviewAgent` / `IAgentRuntime`.
- Đụng vào HTTP layer của provider classes.
- Đụng vào section `Speech:*` (STT dùng provider khác hẳn LLM, schema riêng giữ nguyên).

## 4. Schema mới (`appsettings.json`)

```json
"Providers": {
  "_comment": "API key DÙNG CHUNG per provider. Fallback khi Models:{Feature}:ApiKey rỗng.",
  "Anthropic":  { "ApiKey": "" },
  "DeepSeek":   { "ApiKey": "" },
  "OpenAI":     { "ApiKey": "" },
  "OpenCode":   { "ApiKey": "" },
  "NineRoutes": { "BaseUrl": "http://localhost:20128/v1", "ApiKey": "", "AllowInsecureTls": false }
},

"Models": {
  "_comment": "Primary là MẶC ĐỊNH. Feature null/thiếu field → tự kế thừa Primary. Có thể tách ApiKey riêng nếu muốn account khác.",

  "Primary":         { "Provider": "anthropic", "Model": "claude-haiku-4-5", "ApiKey": "sk-ant-..." },

  "CustomerReview":  { "Provider": "deepseek",  "Model": "deepseek-chat",    "ApiKey": "sk-..." },
  "MailClassify":    { "Provider": "deepseek",  "Model": "deepseek-chat",    "ApiKey": null },

  "ChatAnalytics":   null,
  "Wizard":          null,
  "TourBuilder":     null,
  "VisaScoring":     null,
  "VisaExtraction":  null,
  "DealScoring":     null,
  "MailDraft":       null,
  "MailCompose":     null,
  "Widget":          null,
  "NccImport":       null
}
```

Lưu ý:
- `Models:Review` cũ → rename thành `Models:CustomerReview` (rõ nghĩa, đồng bộ với `MailClassify`).
- `MailClassify` clone cấu hình từ CustomerReview (hiện cùng DeepSeek deepseek-chat). Khi cần tách account → điền ApiKey riêng.
- Mọi feature khác để `null` cho gọn; muốn override → thay null bằng object đầy đủ.
- `Providers:Default` (legacy) → **xóa**. Default provider giờ lấy từ `Models:Primary:Provider`.

## 5. Resolution chain

Cho 1 feature `F`, resolve `(Provider, Model, ApiKey)`:

| Field | Thứ tự ưu tiên (dừng ở nguồn đầu tiên không null/empty) |
|---|---|
| **Provider** | (1) client per-request override (`req.Provider`) → (2) `Models:{F}:Provider` → (3) `Models:Primary:Provider` → throw nếu vẫn null |
| **Model**    | (1) client per-request override (`req.Model`) → (2) `Models:{F}:Model` → (3) `Models:Primary:Model` → (4) `provider.DefaultModel` (catalog[0]) |
| **ApiKey**   | (1) `Models:{F}:ApiKey` → (2) `Models:Primary:ApiKey` (CHỈ nếu resolved provider == `Models:Primary:Provider`) → (3) `Providers:{ResolvedProvider}:ApiKey` → (4) env var (`ANTHROPIC_API_KEY`, …) |

**Ghi chú quan trọng:**
- Client per-request **không nhận `ApiKey` nữa** (frontend v9 đã bỏ — DTO field giữ cho back-compat 1 release nữa rồi xóa ở phase 2).
- ApiKey priority #2 chỉ kích hoạt khi resolved provider trùng Primary's provider — tránh leak key Primary cho feature dùng provider khác.

## 6. `AiModelRegistry` contract

File mới: `Services/Providers/AiModelRegistry.cs`. Thay thế hoàn toàn `Services/Providers/ModelDefaults.cs`.

```csharp
namespace TourkitAiProxy.Services.Providers;

public enum AiFeature
{
    // "Primary" KHÔNG có trong enum — nó chỉ là root fallback nội bộ Registry.
    ChatAnalytics,
    Wizard,
    TourBuilder,
    VisaScoring,
    VisaExtraction,
    DealScoring,
    MailDraft,
    MailCompose,
    MailClassify,
    CustomerReview,
    Widget,
    NccImport
}

public record ResolvedModel(string Provider, string Model, string? ApiKey);

public class AiModelRegistry
{
    public AiModelRegistry(IConfiguration cfg, ProviderRegistry providers);

    /// Resolve theo resolution chain.
    /// `overrideProvider` / `overrideModel` = client per-request override (cao nhất).
    public ResolvedModel Resolve(AiFeature feature, string? overrideProvider = null, string? overrideModel = null);

    /// Key fallback theo TÊN PROVIDER (không qua feature). Dùng cho code path không có feature context
    /// (vd ProviderKeyStore legacy, hoặc admin endpoint test provider).
    /// Thứ tự: Providers:{X}:ApiKey → env var.
    public string? KeyFor(string providerId);

    /// Convenience cho debug/admin endpoint: dump toàn bộ resolution table.
    public IReadOnlyDictionary<AiFeature, ResolvedModel> Snapshot();
}
```

### 6.1 Convention tên config

`AiModelRegistry` map enum `AiFeature.X` ↔ config key `Models:X` 1-1. Đổi tên enum = đổi config; PHẢI cùng commit. Không có alias / back-compat lookup.

### 6.2 Behavior với feature thiếu / null trong config

- `Models:{F}` không tồn tại HOẶC giá trị `null` → fallback Primary cho cả 3 field.
- `Models:{F}` có nhưng thiếu `Provider` → lấy Primary.Provider; thiếu `Model` → lấy Primary.Model.
- `Models:{F}:ApiKey` rỗng/null → bước qua các fallback tiếp theo (xem resolution chain).

### 6.3 Throw rules

- `Models:Primary:Provider` rỗng → throw at startup (DI construct time). Đây là invariant.
- Resolved provider không tồn tại trong `ProviderRegistry._byId` → throw `InvalidOperationException("Provider 'X' chưa đăng ký...")`.
- Resolved ApiKey null sau cả 4 tier → KHÔNG throw ở Registry. Provider tự throw khi gọi upstream (giữ behavior hiện tại).

## 7. `ProviderKeyStore` rút gọn

File hiện tại: `Services/Providers/ProviderKeyStore.cs`. Cross-reference với `Models:Primary`/`Models:Review` bị **xóa**. Sau khi sửa:

```csharp
public class ProviderKeyStore
{
    private readonly AiModelRegistry _registry;
    public ProviderKeyStore(AiModelRegistry registry) => _registry = registry;

    /// Wrapper mỏng, chỉ còn để legacy code chưa kịp inject Registry trực tiếp.
    /// Sẽ xóa hẳn ở phase 2 khi mọi caller đã đi qua AiCallGateway.
    public string? Get(string providerId) => _registry.KeyFor(providerId);

    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(_registry.KeyFor(providerId));
}
```

## 8. `ProviderRegistry` đổi

File: `Services/Providers/ProviderRegistry.cs` line 19.

- Trước: `var defaultId = cfg["Models:Primary:Provider"] ?? cfg["Providers:Default"];`
- Sau: `var defaultId = cfg["Models:Primary:Provider"];` (xóa fallback `Providers:Default` cũ vì spec không cho phép)
- Nếu `Models:Primary:Provider` rỗng → throw startup.

## 9. Files phải sửa (migration table)

| File | Thay đổi | Lý do |
|---|---|---|
| `appsettings.json` | Rename `Models:Review` → `Models:CustomerReview`; thêm `Models:MailClassify`; thêm các feature khác = null; xóa `Providers:Default` | Schema mới |
| `appsettings.example.json` | Viết lại sạch theo schema mới, comment hướng dẫn rõ | Template cho deploy mới |
| `Services/Providers/AiModelRegistry.cs` | **New file** | Service mới |
| `Services/Providers/ModelDefaults.cs` | **Delete** | Thay bởi `AiModelRegistry` |
| `Services/Providers/ProviderKeyStore.cs` | Rút gọn (xem §7) | Bỏ cross-ref Models:Primary/Review |
| `Services/Providers/ProviderRegistry.cs` | Xóa fallback `Providers:Default`; throw nếu Primary rỗng | Single source of truth |
| `Services/Providers/AnthropicProvider.cs` | `DefaultModel`: bỏ đọc `_cfg["Models:Primary:Model"]`; thay bằng `Models.FirstOrDefault(m => m.Recommended)?.Id ?? Models.First().Id` (chỉ nhìn catalog nội bộ) | Provider local default, không nhìn config |
| `Services/Providers/OpenCodeProvider.cs` | Tương tự | Như trên |
| `Services/Providers/DeepSeekProvider.cs` | Tương tự | Như trên |
| `Services/Reviews/ReviewService.cs` | Inject `AiModelRegistry` thay `ModelDefaults`; resolve `AiFeature.CustomerReview` | Bỏ `_defaults.Review` |
| `Services/Reviews/Agents/NativeToolReviewAgent.cs` | Bỏ `const DefaultModel = "claude-sonnet-4-5"`; nhận `model` từ caller (Service đã resolve) | Bỏ hardcode |
| `Services/Reviews/Agents/JsonPromptReviewAgent.cs` | Tương tự (nếu có hardcode) | Bỏ hardcode |
| `Services/Mail/MailClassifier.cs` | Inject `AiModelRegistry` thay `ModelDefaults`; resolve `AiFeature.MailClassify` | Bỏ `_defaults.Review` |
| `Services/Mail/MailReplyService.cs` | Inject Registry; resolve `AiFeature.MailDraft` (DraftStream) + `AiFeature.MailCompose` (ComposeNewStream) | Phân biệt 2 use case |
| `Services/Visa/VisaScoringService.cs` | Bỏ hardcode `"claude-haiku-4-5"` (line 227); resolve `AiFeature.VisaScoring` | Bỏ hardcode |
| `Services/Visa/VisaExtractionService.cs` | Bỏ hardcode (nếu có); resolve `AiFeature.VisaExtraction` | Đồng bộ |
| `Services/Deals/DealScoringService.cs` | Bỏ hardcode `"claude-sonnet-4-5"` (line 98); resolve `AiFeature.DealScoring` | Bỏ hardcode |
| `Services/Tour/TourBuilderService.cs` | Bỏ hardcode `"claude-sonnet-4-5"` (line 98); resolve `AiFeature.TourBuilder` | Bỏ hardcode |
| `Services/Chat/NativeToolUseAgent.cs` | Bỏ fallback `_cfg["Models:Primary:Model"] ?? "claude-sonnet-4-5"` (line 112); resolve `AiFeature.ChatAnalytics` | Bỏ hardcode + config lookup |
| `Services/Chat/JsonPlannerAgent.cs` | Tương tự (nếu có hardcode) | Đồng bộ |
| `Services/Chat/ChatAgentService.cs` | Resolve feature cho 2 stage planner + analyze (cùng `ChatAnalytics`) | Không hardcode |
| `Services/Widget/WidgetChatService.cs` + `WidgetChatCrmService.cs` | Resolve `AiFeature.Widget` | Đồng bộ |
| `Services/NccImport/NccImportService.cs` | Resolve `AiFeature.NccImport` | Đồng bộ |
| `Services/Workflow/NativeToolScorer.cs` | Bỏ default param `model = "claude-sonnet-4-5"`; param `model` bắt buộc | Caller responsibility |
| `Services/Workflow/AnthropicToolsClient.cs` | Bỏ default param `model = "claude-sonnet-4-5"`; param `model` bắt buộc | Caller responsibility |
| `Endpoints/AiEndpoints.cs` | Trong handler `/completions` + `/completions/stream` (Wizard): nếu `req.Model`/`req.Provider` rỗng → resolve `AiFeature.Wizard` qua Registry | Wizard có entry point riêng |
| `Program.cs` | Đăng ký `AiModelRegistry`; xóa `ModelDefaults` registration | DI wiring |

**Tổng: ~22 file touched, ~8 file Service service-layer, 7 file Provider/Registry, 1 config, 1 Program.cs.**

## 10. Verification plan

Sau khi implement xong:

1. **Build clean** — `dotnet build TourkitAiProxy.csproj` → 0 error, 0 warning.
2. **Grep guard** — chạy local:
   - `grep -rn '"claude-sonnet-4-5"' Services/ Endpoints/` → CHỈ còn match trong provider catalog (`AnthropicProvider.cs`, `NineRoutesProvider.cs`).
   - `grep -rn '"claude-haiku-4-5"' Services/ Endpoints/` → CHỈ còn match trong provider catalog.
   - `grep -rn 'Models:Primary' Services/` → CHỈ còn match trong `AiModelRegistry.cs`.
   - `grep -rn 'Models:Review' Services/` → 0 match (đã rename `CustomerReview`).
   - `grep -rn 'ModelDefaults' Services/ Program.cs` → 0 match.
3. **Smoke test** (dùng existing dev mode `dotnet run`, không tốn token):
   - `GET /api/v1/providers` → trả về 5 provider như cũ.
   - `GET /api/v1/quota` với 1 sessionId hợp lệ → OK (không vỡ DI).
   - `POST /api/v1/completions` (Wizard) → 1 prompt nhỏ, confirm response trả về dùng provider Primary (`anthropic`/`claude-haiku-4-5` từ config).
4. **E2E test** (`e2e/tests/05-api-direct.spec.js` đã có sẵn) — xác nhận 6 AI feature đúng Models:Primary/Review. Cập nhật test nếu cần để dùng feature names mới.
5. **Manual smoke 1 round** mỗi feature qua UI (gọi 1 lần, xem log `data/ai-usage.jsonl` để confirm provider/model đúng):
   - Wizard, Customer Review, Mail Classify (qua /mail/sync), Visa Score, Deal Score, Tour Builder, Mail Draft, Chat Analytics.

## 11. Migration cho user hiện hữu

Khi anh deploy:

1. Backup `appsettings.json` hiện tại.
2. Apply diff:
   ```
   - "Models": {
   -   "Primary": { ... },
   -   "Review":  { "Provider": "deepseek", "Model": "deepseek-chat", "ApiKey": "..." }
   - }
   + "Models": {
   +   "Primary":         { ... },                       // giữ nguyên giá trị
   +   "CustomerReview":  { "Provider": "deepseek", "Model": "deepseek-chat", "ApiKey": "..." },
   +   "MailClassify":    { "Provider": "deepseek", "Model": "deepseek-chat", "ApiKey": null },
   +   "ChatAnalytics":   null,
   +   "Wizard":          null,
   +   "TourBuilder":     null,
   +   "VisaScoring":     null,
   +   "VisaExtraction":  null,
   +   "DealScoring":     null,
   +   "MailDraft":       null,
   +   "MailCompose":     null,
   +   "Widget":          null,
   +   "NccImport":       null
   + }
   ```
3. Xóa `"Providers": { "Default": "..." }` (nếu có) — không còn dùng.
4. Khởi động lại app. Nếu Primary thiếu → fail-fast với log rõ ràng.

Không có data migration (config-only refactor).

## 12. Risks & rollback

| Risk | Mitigation | Rollback |
|---|---|---|
| Quên rename ở 1 caller → runtime null model → upstream 400 | Grep guard ở §10 + smoke test mỗi feature | Revert commit, redeploy appsettings cũ |
| Test e2e fail vì feature dùng model khác Primary hiện tại (vd Visa từ hardcode haiku → Primary haiku, may là cùng) | Bảng mapping ở §3 trong brainstorming đã verify Primary = haiku đồng bộ với Visa hardcode cũ | Set `Models:VisaScoring` về `{ Model: "claude-haiku-4-5" }` explicit nếu cần giữ exact |
| Frontend gửi `apiKey` trong CompleteRequest → bị bỏ qua → nhầm tưởng dùng key đó | DTO giữ field cho back-compat, log warn nếu frontend gửi | Không applicable — chỉ là log warn |

## 13. Out of scope (phase 2)

- Gộp `Visa/Deal/Tour/Review/MailClassify` thành 1 `AiCallGateway` (~25 file touch, refactor sâu).
- Xóa `IReviewAgent` + `IAgentRuntime` strategy.
- Xóa `CompleteRequest.ApiKey` (sau khi confirm 0 caller frontend còn gửi).
- Đụng `Speech:*` config (giữ riêng).

Phase 2 sẽ có spec riêng sau khi phase 1 ship + ổn định.
