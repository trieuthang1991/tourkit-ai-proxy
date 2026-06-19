# Centralize AI Model Config — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor cấu hình AI model về 1 nguồn duy nhất qua `AiModelRegistry` (per-feature trong `Models:*`), xóa toàn bộ hardcode model + cross-section lookup rải rác trong codebase.

**Architecture:** Tạo `AiModelRegistry` service với enum `AiFeature` (12 feature) + record `ResolvedModel`. Resolution chain: client override → `Models:{F}` → `Models:Primary` → provider local default. Mọi service AI call (Visa/Deal/Tour/Review/Chat/Mail/Widget/NCC/Wizard) inject Registry thay vì đọc `_cfg["Models:..."]` hoặc hardcode `"claude-sonnet-4-5"`.

**Tech Stack:** ASP.NET Core 8 (DI/IConfiguration), xUnit (test pure logic Registry), `dotnet build` để verify compile.

**Spec:** `docs/superpowers/specs/2026-06-19-centralize-ai-model-config-design.md`

---

## File map

**Tạo mới:**
- `Services/Providers/AiModelRegistry.cs` — service + enum + record
- `TourkitAiProxy.Tests/AiModelRegistryTests.cs` — unit tests pure logic

**Sửa:**
- `appsettings.example.json` — schema mới
- `Program.cs` — register Registry, xóa ModelDefaults registration
- `Services/Providers/ProviderRegistry.cs` — bỏ fallback `Providers:Default`
- `Services/Providers/ProviderKeyStore.cs` — rút gọn thành wrapper Registry.KeyFor
- `Services/Providers/AnthropicProvider.cs` — DefaultModel local catalog
- `Services/Providers/OpenCodeProvider.cs` — tương tự
- `Services/Providers/DeepSeekProvider.cs` — tương tự
- `Services/Workflow/NativeToolScorer.cs` — bỏ default `model` param
- `Services/Workflow/AnthropicToolsClient.cs` — bỏ default `model` param
- `Services/Reviews/ReviewService.cs` — inject Registry, resolve `CustomerReview`
- `Services/Reviews/Agents/NativeToolReviewAgent.cs` — bỏ `const DefaultModel`
- `Services/Reviews/Agents/JsonPromptReviewAgent.cs` — (nếu có hardcode)
- `Services/Mail/MailClassifier.cs` — resolve `MailClassify`
- `Services/Mail/MailReplyService.cs` — resolve `MailDraft` + `MailCompose`
- `Services/Visa/VisaScoringService.cs` — bỏ hardcode haiku, resolve `VisaScoring`
- `Services/Visa/VisaExtractionService.cs` — resolve `VisaExtraction` khi caller không truyền
- `Services/Deals/DealScoringService.cs` — bỏ hardcode sonnet, resolve `DealScoring`
- `Services/Tour/TourBuilderService.cs` — bỏ hardcode sonnet, resolve `TourBuilder`
- `Services/Chat/NativeToolUseAgent.cs` — bỏ fallback `_cfg["Models:Primary:Model"]`
- `Services/Chat/JsonPlannerAgent.cs` — bỏ hardcode (nếu có)
- `Services/Chat/ChatAgentService.cs` — inject Registry cho 2 stage
- `Services/Widget/WidgetChatService.cs` — resolve `Widget`
- `Services/Widget/WidgetChatCrmService.cs` — resolve `Widget`
- `Services/NccImport/NccImportService.cs` — resolve `NccImport`
- `Endpoints/AiEndpoints.cs` — Wizard endpoint resolve `Wizard` khi req thiếu provider/model

**Xóa:**
- `Services/Providers/ModelDefaults.cs`

---

## Pre-flight checklist (anh đọc trước khi start)

- [ ] Backup `appsettings.json` hiện tại (chưa commit, gitignored).
- [ ] Đang ở branch `main`, working tree clean.
- [ ] `dotnet build TourkitAiProxy.csproj` pass ngay từ đầu (baseline 0 error 0 warning).
- [ ] Hiểu rõ: AiModelRegistry đọc các key `Models:CustomerReview`, `Models:MailClassify`, v.v. — local `appsettings.json` PHẢI có các key này (hoặc null) trước khi run service đã migrate. Khi đang giữa quá trình refactor, anh có thể clone `Models:Review` thành `Models:CustomerReview` ngay từ đầu để app vẫn chạy.

---

## Task 1: Tạo `AiFeature` enum + `ResolvedModel` record + `AiModelRegistry` skeleton

**Files:**
- Create: `Services/Providers/AiModelRegistry.cs`

- [ ] **Step 1.1: Tạo file**

```csharp
namespace TourkitAiProxy.Services.Providers;

/// 12 feature dùng AI trong proxy. Mỗi enum value 1-1 với key `Models:{Name}` trong appsettings.
/// "Primary" KHÔNG có ở đây — nó chỉ là root fallback internal Registry.
public enum AiFeature
{
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

/// <summary>
/// Single source of truth cho cấu hình AI model per-feature.
///
/// Resolution chain (xem spec §5):
///   Provider: override → Models:{F}:Provider → Models:Primary:Provider → throw
///   Model:    override → Models:{F}:Model    → Models:Primary:Model    → provider.DefaultModel
///   ApiKey:   Models:{F}:ApiKey → Models:Primary:ApiKey (nếu provider == Primary's) →
///             Providers:{Provider}:ApiKey → env var → null (provider tự throw khi gọi upstream)
/// </summary>
public class AiModelRegistry
{
    private readonly IConfiguration _cfg;
    private readonly ProviderRegistry _providers;

    private static readonly Dictionary<string, (string Section, string Env)> ProviderKeyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"]   = ("Anthropic",  "ANTHROPIC_API_KEY"),
            ["deepseek"]    = ("DeepSeek",   "DEEPSEEK_API_KEY"),
            ["openai"]      = ("OpenAI",     "OPENAI_API_KEY"),
            ["opencode-go"] = ("OpenCode",   "OPENCODE_API_KEY"),
            ["nine-routes"] = ("NineRoutes", "NINE_ROUTES_API_KEY"),
        };

    public AiModelRegistry(IConfiguration cfg, ProviderRegistry providers)
    {
        _cfg = cfg;
        _providers = providers;

        // Fail-fast nếu Primary thiếu — đây là invariant.
        if (string.IsNullOrWhiteSpace(_cfg["Models:Primary:Provider"]))
            throw new InvalidOperationException(
                "Cấu hình thiếu Models:Primary:Provider — đây là root fallback bắt buộc của AiModelRegistry.");
    }

    public ResolvedModel Resolve(AiFeature feature, string? overrideProvider = null, string? overrideModel = null)
    {
        var section = feature.ToString();

        var provider = NotEmpty(overrideProvider)
            ?? NotEmpty(_cfg[$"Models:{section}:Provider"])
            ?? NotEmpty(_cfg["Models:Primary:Provider"])
            ?? throw new InvalidOperationException(
                $"Không resolve được provider cho feature {feature} — Models:Primary:Provider rỗng?");

        // Validate provider đã đăng ký trong DI (case-insensitive)
        var providerInstance = _providers.All.FirstOrDefault(p =>
            string.Equals(p.Id, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Provider '{provider}' (feature {feature}) chưa đăng ký trong DI — kiểm tra Program.cs.");

        var model = NotEmpty(overrideModel)
            ?? NotEmpty(_cfg[$"Models:{section}:Model"])
            ?? NotEmpty(_cfg["Models:Primary:Model"])
            ?? providerInstance.Models.FirstOrDefault(m => m.Recommended)?.Id
            ?? providerInstance.Models.First().Id;

        // ApiKey resolution
        var apiKey = NotEmpty(_cfg[$"Models:{section}:ApiKey"]);
        if (apiKey == null)
        {
            var primaryProv = _cfg["Models:Primary:Provider"];
            if (string.Equals(primaryProv, provider, StringComparison.OrdinalIgnoreCase))
                apiKey = NotEmpty(_cfg["Models:Primary:ApiKey"]);
        }
        apiKey ??= KeyFor(provider);

        return new ResolvedModel(provider, model, apiKey);
    }

    /// Key fallback theo TÊN PROVIDER (không qua feature). Dùng cho code không có feature context.
    /// Thứ tự: Providers:{X}:ApiKey → env var.
    public string? KeyFor(string providerId)
    {
        if (!ProviderKeyMap.TryGetValue(providerId, out var m)) return null;
        return NotEmpty(_cfg[$"Providers:{m.Section}:ApiKey"])
            ?? NotEmpty(Environment.GetEnvironmentVariable(m.Env));
    }

    /// Dump toàn bộ resolution — dùng cho admin endpoint debug.
    public IReadOnlyDictionary<AiFeature, ResolvedModel> Snapshot()
        => Enum.GetValues<AiFeature>().ToDictionary(f => f, f => Resolve(f));

    private static string? NotEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
```

- [ ] **Step 1.2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: PASS, 0 error 0 warning. (Registry chưa được DI register, nhưng compile OK.)

- [ ] **Step 1.3: Commit**

```bash
git add Services/Providers/AiModelRegistry.cs
git commit -m "feat(ai-config): introduce AiModelRegistry + AiFeature enum (phase 1 step 1)"
```

---

## Task 2: Unit tests cho `AiModelRegistry`

**Files:**
- Create: `TourkitAiProxy.Tests/AiModelRegistryTests.cs`

- [ ] **Step 2.1: Tạo file test**

```csharp
using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;
using Xunit;

namespace TourkitAiProxy.Tests;

public class AiModelRegistryTests
{
    private static AiModelRegistry MakeRegistry(Dictionary<string, string?> config)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var providers = new List<IAiProvider> { new FakeProvider("anthropic"), new FakeProvider("deepseek") };
        var pReg = new ProviderRegistry(providers, cfg);
        return new AiModelRegistry(cfg, pReg);
    }

    [Fact]
    public void Throws_at_construct_when_Primary_Provider_missing()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var pReg = new ProviderRegistry(new[] { new FakeProvider("anthropic") }, cfg);
        var ex = Assert.Throws<InvalidOperationException>(() => new AiModelRegistry(cfg, pReg));
        Assert.Contains("Models:Primary:Provider", ex.Message);
    }

    [Fact]
    public void Feature_with_null_section_inherits_Primary()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
            ["Models:Primary:Model"]    = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]   = "sk-primary",
        });

        var rm = r.Resolve(AiFeature.Wizard);
        Assert.Equal("anthropic", rm.Provider);
        Assert.Equal("claude-haiku-4-5", rm.Model);
        Assert.Equal("sk-primary", rm.ApiKey);
    }

    [Fact]
    public void Feature_with_explicit_section_overrides_Primary()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]        = "anthropic",
            ["Models:Primary:Model"]           = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]          = "sk-primary",
            ["Models:CustomerReview:Provider"] = "deepseek",
            ["Models:CustomerReview:Model"]    = "deepseek-chat",
            ["Models:CustomerReview:ApiKey"]   = "sk-deepseek-review",
        });

        var rm = r.Resolve(AiFeature.CustomerReview);
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("deepseek-chat", rm.Model);
        Assert.Equal("sk-deepseek-review", rm.ApiKey);
    }

    [Fact]
    public void Partial_section_inherits_missing_fields_from_Primary()
    {
        // Chỉ override Model, Provider + ApiKey kế thừa Primary
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]     = "anthropic",
            ["Models:Primary:Model"]        = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]       = "sk-primary",
            ["Models:VisaScoring:Model"]    = "claude-sonnet-4-5",
        });

        var rm = r.Resolve(AiFeature.VisaScoring);
        Assert.Equal("anthropic", rm.Provider);                // inherited
        Assert.Equal("claude-sonnet-4-5", rm.Model);           // override
        Assert.Equal("sk-primary", rm.ApiKey);                 // inherited (cùng provider Primary)
    }

    [Fact]
    public void ApiKey_falls_back_to_Providers_section_when_feature_uses_different_provider()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"]        = "anthropic",
            ["Models:Primary:Model"]           = "claude-haiku-4-5",
            ["Models:Primary:ApiKey"]          = "sk-primary-anthropic",
            ["Models:MailClassify:Provider"]   = "deepseek",
            ["Models:MailClassify:Model"]      = "deepseek-chat",
            // KHÔNG có Models:MailClassify:ApiKey → fallback Providers:DeepSeek:ApiKey
            ["Providers:DeepSeek:ApiKey"]      = "sk-shared-deepseek",
        });

        var rm = r.Resolve(AiFeature.MailClassify);
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("sk-shared-deepseek", rm.ApiKey);
        // KHÔNG dính sk-primary-anthropic vì provider khác Primary
    }

    [Fact]
    public void Per_request_override_takes_highest_priority()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
            ["Models:Primary:Model"]    = "claude-haiku-4-5",
        });

        var rm = r.Resolve(AiFeature.Wizard, overrideProvider: "deepseek", overrideModel: "deepseek-reasoner");
        Assert.Equal("deepseek", rm.Provider);
        Assert.Equal("deepseek-reasoner", rm.Model);
    }

    [Fact]
    public void Throws_when_resolved_provider_not_registered_in_DI()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "openai",   // chỉ có anthropic + deepseek đăng ký
            ["Models:Primary:Model"]    = "gpt-4o",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => r.Resolve(AiFeature.Wizard));
        Assert.Contains("openai", ex.Message);
        Assert.Contains("chưa đăng ký", ex.Message);
    }

    [Fact]
    public void Model_falls_back_to_provider_recommended_when_Primary_Model_missing()
    {
        var r = MakeRegistry(new()
        {
            ["Models:Primary:Provider"] = "anthropic",
            // KHÔNG có Models:Primary:Model
        });

        var rm = r.Resolve(AiFeature.Wizard);
        Assert.Equal("anthropic", rm.Provider);
        Assert.Equal("fake-recommended", rm.Model);   // catalog Recommended:true (xem FakeProvider)
    }

    // ─── Fake provider for tests (no upstream calls) ──────────────────────────
    private class FakeProvider : IAiProvider
    {
        public FakeProvider(string id) => Id = id;
        public string Id { get; }
        public string Label => Id;
        public IReadOnlyList<ProviderModel> Models => new[]
        {
            new ProviderModel("fake-default", "Fake Default"),
            new ProviderModel("fake-recommended", "Fake Recommended", Recommended: true),
        };
        public Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2.2: Run tests**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~AiModelRegistryTests"`
Expected: 8 tests passed.

- [ ] **Step 2.3: Commit**

```bash
git add TourkitAiProxy.Tests/AiModelRegistryTests.cs
git commit -m "test(ai-config): unit tests cho AiModelRegistry resolution chain"
```

---

## Task 3: Register `AiModelRegistry` trong DI

**Files:**
- Modify: `Program.cs:165` (around `ModelDefaults` registration)

- [ ] **Step 3.1: Thêm registration NGAY SAU dòng đăng ký `ModelDefaults`**

Mở `Program.cs`, tìm dòng:
```csharp
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.ModelDefaults>();   // Models:Primary + Models:Review từ appsettings
```

Thêm NGAY SAU:
```csharp
// Single source of truth mới cho cấu hình AI model per-feature (replaces ModelDefaults).
// ModelDefaults vẫn giữ tạm thời cho service chưa migrate xong; sẽ xóa ở task cuối.
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.AiModelRegistry>();
```

- [ ] **Step 3.2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: PASS 0 error 0 warning.

- [ ] **Step 3.3: Smoke test app start**

Run: `dotnet run --project TourkitAiProxy.csproj` (Ctrl+C sau ~5s)
Expected: app start không throw. Nếu throw `Models:Primary:Provider rỗng` → đảm bảo `appsettings.json` local có `"Models": { "Primary": { "Provider": "anthropic", ... } }`.

- [ ] **Step 3.4: Commit**

```bash
git add Program.cs
git commit -m "chore(ai-config): register AiModelRegistry trong DI (chạy song song ModelDefaults)"
```

---

## Task 4: Cập nhật `appsettings.example.json` với schema mới

**Files:**
- Modify: `appsettings.example.json`

- [ ] **Step 4.1: Đọc file hiện tại để biết block hiện có**

Run: `cat appsettings.example.json`

- [ ] **Step 4.2: Ghi đè với schema mới**

Thay block `"Models": { … }` hiện tại bằng:

```json
"Models": {
  "_comment": "Primary là MẶC ĐỊNH. Feature null/thiếu field → tự kế thừa Primary. ApiKey có thể tách riêng nếu muốn account khác. Spec: docs/superpowers/specs/2026-06-19-centralize-ai-model-config-design.md",

  "Primary":         { "Provider": "anthropic", "Model": "claude-haiku-4-5", "ApiKey": "REPLACE_WITH_ANTHROPIC_KEY" },

  "CustomerReview":  { "Provider": "deepseek",  "Model": "deepseek-chat",    "ApiKey": "REPLACE_WITH_DEEPSEEK_KEY" },
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

Cũng xóa block `"Providers": { "Default": "opencode-go", ... }` — phần `Default` không còn dùng, các sub-block `OpenCode`/`OpenAI`/`Anthropic`/`NineRoutes` giữ nguyên. Block `Providers` rút gọn còn:

```json
"Providers": {
  "_comment": "API key DÙNG CHUNG per provider. Fallback khi Models:{Feature}:ApiKey rỗng và provider khác Primary.",
  "OpenCode":   { "ApiKey": "REPLACE_WITH_OPENCODE_KEY" },
  "NineRoutes": { "BaseUrl": "http://localhost:20128/v1", "ApiKey": "REPLACE_WITH_9ROUTES_KEY", "AllowInsecureTls": false },
  "OpenAI":     { "ApiKey": "" },
  "Anthropic":  { "ApiKey": "" },
  "DeepSeek":   { "ApiKey": "" }
}
```

- [ ] **Step 4.3: Cập nhật `appsettings.json` LOCAL của anh (manual, không commit)**

Đây là file gitignored, anh tự edit theo template ở §11 spec. **Khuyên anh làm NGAY** để service đã migrate (task 5+) resolve được. Clone giá trị `Models:Review` cũ thành `Models:CustomerReview` mới + thêm `Models:MailClassify` clone cùng giá trị.

- [ ] **Step 4.4: Build + smoke**

Run: `dotnet build TourkitAiProxy.csproj` → PASS.
Run nhanh: `dotnet run --project TourkitAiProxy.csproj` 5s, Ctrl+C. App phải start sạch.

- [ ] **Step 4.5: Commit**

```bash
git add appsettings.example.json
git commit -m "chore(ai-config): appsettings.example.json schema mới per-feature Models"
```

---

## Task 5: Migrate `ReviewService` — dùng `AiFeature.CustomerReview`

**Files:**
- Modify: `Services/Reviews/ReviewService.cs`

- [ ] **Step 5.1: Đổi inject từ `ModelDefaults` → `AiModelRegistry`**

Tìm constructor `ReviewService(ReviewRepository, ProviderRegistry, ModelDefaults, IEnumerable<IReviewAgent>, IWorkflowTraceAccessor, ILogger<ReviewService>)`. Đổi tham số `ModelDefaults defaults` → `AiModelRegistry registry`, field `_defaults` → `_registry`.

Trong block resolve provider/model/apiKey (xung quanh line 53-66):
- Trước:
  ```csharp
  var review = _defaults.Review;
  var resolvedProvider = providerOverride ?? review.Provider;
  var resolvedModel    = modelOverride    ?? review.Model;
  var resolvedApiKey   = apiKeyOverride   ?? review.ApiKey;
  ```
- Sau:
  ```csharp
  var resolved = _registry.Resolve(AiFeature.CustomerReview, providerOverride, modelOverride);
  var resolvedProvider = resolved.Provider;
  var resolvedModel    = resolved.Model;
  var resolvedApiKey   = apiKeyOverride ?? resolved.ApiKey;   // apiKeyOverride cho admin debug
  ```

Trace message sửa lại tham chiếu `Models:CustomerReview` thay vì `Models:Review`.

- [ ] **Step 5.2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: PASS.

- [ ] **Step 5.3: Smoke test 1 review**

Run app, mở `/customers`, click review 1 KH bất kỳ. Confirm:
- Response 200 OK.
- `data/ai-usage.jsonl` dòng mới có `provider=deepseek model=deepseek-chat` (Models:CustomerReview).

- [ ] **Step 5.4: Commit**

```bash
git add Services/Reviews/ReviewService.cs
git commit -m "refactor(review): ReviewService dùng AiModelRegistry(CustomerReview)"
```

---

## Task 6: Migrate `MailClassifier` — dùng `AiFeature.MailClassify`

**Files:**
- Modify: `Services/Mail/MailClassifier.cs`

- [ ] **Step 6.1: Đọc constructor + chỗ dùng `ModelDefaults`**

Run: `grep -n "ModelDefaults\|_defaults" Services/Mail/MailClassifier.cs`

- [ ] **Step 6.2: Thay `_defaults.Review` bằng `_registry.Resolve(AiFeature.MailClassify)`**

Đổi inject `ModelDefaults` → `AiModelRegistry`. Field `_defaults` → `_registry`. Mọi chỗ đọc `_defaults.Review.{Provider/Model/ApiKey}` → đổi sang biến `resolved.{Provider/Model/ApiKey}` từ `var resolved = _registry.Resolve(AiFeature.MailClassify);`.

Sửa comment header file (dòng 9-13): `Models:Review` → `Models:MailClassify`.

- [ ] **Step 6.3: Build + smoke**

Run: `dotnet build` → PASS.
Smoke: vào `/mail`, bấm Refresh sync. Confirm log `data/ai-usage.jsonl` có `provider=deepseek model=deepseek-chat` cho feature mail-classify (KHÔNG bị thay đổi).

- [ ] **Step 6.4: Commit**

```bash
git add Services/Mail/MailClassifier.cs
git commit -m "refactor(mail): MailClassifier dùng AiModelRegistry(MailClassify)"
```

---

## Task 7: Migrate `VisaScoringService` — xóa hardcode haiku

**Files:**
- Modify: `Services/Visa/VisaScoringService.cs:227`

- [ ] **Step 7.1: Đọc context line 220-235**

Run: `sed -n '215,235p' Services/Visa/VisaScoringService.cs` (hoặc dùng Read tool)

- [ ] **Step 7.2: Đổi**

Inject `AiModelRegistry registry`. Field `_registry`.

Tại đầu method `ScoreAsync` (line ~91, ngay sau resolve `IAiProvider p`), resolve feature:
```csharp
var resolved = _registry.Resolve(AiFeature.VisaScoring, provider, model);
// Override `provider`, `model`, `apiKey` cho phần còn lại của method:
provider = resolved.Provider;
model    = resolved.Model;
apiKey   = apiKey ?? resolved.ApiKey;
```

Line 227 — bỏ default hardcode:
- Trước: `model: string.IsNullOrWhiteSpace(model) ? "claude-haiku-4-5" : model!,`
- Sau:    `model: model!,`

(Sau khi resolved phía trên, `model` chắc chắn không null/empty.)

- [ ] **Step 7.3: Build + smoke**

Run: `dotnet build` → PASS.
Smoke: vào `/visa`, upload 1 PDF test, chấm. Confirm `data/ai-usage.jsonl` có provider `anthropic` model `claude-haiku-4-5` (Primary, vì Models:VisaScoring=null kế thừa).

- [ ] **Step 7.4: Commit**

```bash
git add Services/Visa/VisaScoringService.cs
git commit -m "refactor(visa): VisaScoringService dùng AiModelRegistry(VisaScoring), bỏ hardcode haiku"
```

---

## Task 8: Migrate `VisaExtractionService` — resolve `VisaExtraction`

**Files:**
- Modify: `Services/Visa/VisaExtractionService.cs`

- [ ] **Step 8.1: Đọc signature `ExtractAsync` + chỗ nhận `model/apiKey`**

Run: Read `Services/Visa/VisaExtractionService.cs` lines 1-60.

- [ ] **Step 8.2: Inject Registry + resolve khi caller không truyền model**

Constructor thêm `AiModelRegistry registry`. Đầu `ExtractAsync(uploads, provider, model, apiKey, ct)`:
```csharp
var resolved = _registry.Resolve(AiFeature.VisaExtraction, provider, model);
provider = resolved.Provider;
model    = resolved.Model;
apiKey   = apiKey ?? resolved.ApiKey;
```

Áp dụng cho mọi entry point của service nếu có nhiều (vd `ExtractOneAsync` nhận từ caller — KHÔNG cần resolve lại, caller đã pass-through giá trị đã resolve).

- [ ] **Step 8.3: Build + smoke**

`dotnet build` → PASS.
Smoke: visa upload 1 file ảnh/PDF, confirm log đúng provider/model.

- [ ] **Step 8.4: Commit**

```bash
git add Services/Visa/VisaExtractionService.cs
git commit -m "refactor(visa): VisaExtractionService dùng AiModelRegistry(VisaExtraction)"
```

---

## Task 9: Migrate `DealScoringService` — xóa hardcode sonnet

**Files:**
- Modify: `Services/Deals/DealScoringService.cs`

- [ ] **Step 9.1: Inject Registry + resolve `AiFeature.DealScoring`**

Constructor thêm `AiModelRegistry registry`. Field `_registry`.

Trong `ScoreAsync` đầu method (sau resolve `IAiProvider p` line 48), thêm:
```csharp
var resolved = _registry.Resolve(AiFeature.DealScoring, provider, model);
provider = resolved.Provider;
model    = resolved.Model;
apiKey   = apiKey ?? resolved.ApiKey;
// Re-resolve `p` vì provider có thể đổi sau registry.Resolve
p = _registry  // KHÔNG có method này; dùng ProviderRegistry hiện tại:
// → đổi: p = providerRegistry.Resolve(provider);
```

Sửa rõ: KHÔNG đụng `ProviderRegistry _registry` field cũ trong service — đặt tên field mới `_modelRegistry` cho `AiModelRegistry` để khỏi conflict tên với `ProviderRegistry`. Tổng kết constructor mới:
```csharp
public DealScoringService(ProviderRegistry registry, AiResponseCache cache,
    NativeToolScorer native, IWorkflowTraceAccessor trace,
    AiModelRegistry modelRegistry, ILogger<DealScoringService> log)
{
    _registry = registry; _cache = cache; _native = native; _trace = trace;
    _modelRegistry = modelRegistry; _log = log;
}
```

Và đầu `ScoreAsync`:
```csharp
var resolved = _modelRegistry.Resolve(AiFeature.DealScoring, provider, model);
provider = resolved.Provider;
model    = resolved.Model;
apiKey   = apiKey ?? resolved.ApiKey;
var p    = _registry.Resolve(provider);
trace?.SetMeta("provider", p.Id);
```

Line 98 — bỏ hardcode:
- Trước: `model: string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-5" : model!,`
- Sau:    `model: model!,`

- [ ] **Step 9.2: Build + smoke**

`dotnet build` → PASS.
Smoke: vào `/deals` (nếu có), chấm 1 cơ hội. Confirm provider Primary.

- [ ] **Step 9.3: Commit**

```bash
git add Services/Deals/DealScoringService.cs
git commit -m "refactor(deal): DealScoringService dùng AiModelRegistry(DealScoring), bỏ hardcode sonnet"
```

---

## Task 10: Migrate `TourBuilderService` — xóa hardcode sonnet

**Files:**
- Modify: `Services/Tour/TourBuilderService.cs`

- [ ] **Step 10.1: Pattern y hệt task 9**

Inject `AiModelRegistry modelRegistry` vào constructor. Field `_modelRegistry`. Đầu method chính (`BuildAsync` hoặc tên tương đương):
```csharp
var resolved = _modelRegistry.Resolve(AiFeature.TourBuilder, req.Provider, req.Model);
var apiKey   = req.ApiKey ?? resolved.ApiKey;
var provider = resolved.Provider;
var model    = resolved.Model;
```

Line ~98 — bỏ hardcode `"claude-sonnet-4-5"`.

- [ ] **Step 10.2: Build + smoke**

`dotnet build` → PASS. Smoke: gọi `/api/v1/tour-builder/...` 1 lần (qua UI nếu có).

- [ ] **Step 10.3: Commit**

```bash
git add Services/Tour/TourBuilderService.cs
git commit -m "refactor(tour): TourBuilderService dùng AiModelRegistry(TourBuilder), bỏ hardcode sonnet"
```

---

## Task 11: Migrate `Chat` (NativeToolUseAgent + JsonPlannerAgent + ChatAgentService)

**Files:**
- Modify: `Services/Chat/NativeToolUseAgent.cs:111-112`
- Modify: `Services/Chat/JsonPlannerAgent.cs` (kiểm tra có hardcode không)
- Modify: `Services/Chat/ChatAgentService.cs`

- [ ] **Step 11.1: `NativeToolUseAgent.cs`**

Tìm dòng 112:
```csharp
var model  = input.Model ?? _cfg["Models:Primary:Model"] ?? "claude-sonnet-4-5";
```

Inject `AiModelRegistry registry` vào constructor. Đổi dòng trên thành:
```csharp
var resolved = _registry.Resolve(AiFeature.ChatAnalytics, input.Provider, input.Model);
var model    = resolved.Model;
var provider = resolved.Provider;
var apiKey   = input.ApiKey ?? resolved.ApiKey;
```

Xóa `_cfg` field nếu không còn chỗ nào dùng (grep `_cfg\.` trong file).

- [ ] **Step 11.2: `JsonPlannerAgent.cs`**

Run: `grep -n "Models:Primary\|claude-sonnet\|claude-haiku\|_cfg" Services/Chat/JsonPlannerAgent.cs`

Nếu có hardcode hoặc `_cfg["Models:..."]` → áp dụng pattern y hệt step 11.1. Nếu không → skip.

- [ ] **Step 11.3: `ChatAgentService.cs`**

Tìm chỗ resolve provider/model cho 2 stage planner + analyze. Cả 2 đều thuộc `AiFeature.ChatAnalytics`. Inject Registry, resolve 1 lần ở đầu method công khai (`AskAsync` / `AskStreamAsync`), dùng cho cả 2 stage.

- [ ] **Step 11.4: Build + smoke**

`dotnet build` → PASS.
Smoke: vào `/assistant`, login token + 1 câu hỏi "doanh thu tháng này". Confirm response, log đúng provider Primary.

- [ ] **Step 11.5: Commit**

```bash
git add Services/Chat/
git commit -m "refactor(chat): Chat agents + service dùng AiModelRegistry(ChatAnalytics), bỏ hardcode"
```

---

## Task 12: Migrate Widget (FAQ + CRM)

**Files:**
- Modify: `Services/Widget/WidgetChatService.cs`
- Modify: `Services/Widget/WidgetChatCrmService.cs`

- [ ] **Step 12.1: Cả 2 service**

Inject `AiModelRegistry`. Resolve `AiFeature.Widget` ở entry point. Áp dụng pattern y hệt task 11.

- [ ] **Step 12.2: Build verify**

`dotnet build` → PASS.

- [ ] **Step 12.3: Smoke (nếu có UI widget test)**

Nếu chưa có UI test thì skip — chỉ verify build pass. Smoke đầy đủ ở task 24.

- [ ] **Step 12.4: Commit**

```bash
git add Services/Widget/
git commit -m "refactor(widget): WidgetChat + WidgetCrm dùng AiModelRegistry(Widget)"
```

---

## Task 13: Migrate `NccImportService` — resolve `NccImport`

**Files:**
- Modify: `Services/NccImport/NccImportService.cs`

- [ ] **Step 13.1: Resolve `AiFeature.NccImport`**

Inject `AiModelRegistry`. Resolve đầu method công khai. Pattern y hệt các task trước.

- [ ] **Step 13.2: Build + smoke**

`dotnet build` → PASS.
Smoke: vào `/ncc-import`, upload 1 file mẫu (PDF/Excel NCC). Confirm response.

- [ ] **Step 13.3: Commit**

```bash
git add Services/NccImport/NccImportService.cs
git commit -m "refactor(ncc): NccImportService dùng AiModelRegistry(NccImport)"
```

---

## Task 14: Migrate `MailReplyService` — Draft + Compose (2 feature)

**Files:**
- Modify: `Services/Mail/MailReplyService.cs`

- [ ] **Step 14.1: Resolve 2 feature riêng cho 2 method**

Inject `AiModelRegistry`. 

- `DraftStreamAsync` (trả lời) → `_registry.Resolve(AiFeature.MailDraft, ...)`
- `ComposeNewStreamAsync` (soạn mới) → `_registry.Resolve(AiFeature.MailCompose, ...)`

Pattern y hệt các task trước.

- [ ] **Step 14.2: Build + smoke**

`dotnet build` → PASS.
Smoke: vào `/mail`, mở 1 email, click "Soạn nháp". Confirm stream chạy + log đúng provider Primary.
Smoke compose: click "Soạn email mới" + brief + send draft. Confirm tương tự.

- [ ] **Step 14.3: Commit**

```bash
git add Services/Mail/MailReplyService.cs
git commit -m "refactor(mail): MailReplyService dùng AiModelRegistry(MailDraft + MailCompose)"
```

---

## Task 15: Migrate Wizard endpoint trong `AiEndpoints.cs`

**Files:**
- Modify: `Endpoints/AiEndpoints.cs`

- [ ] **Step 15.1: Tìm 2 handler `/completions` + `/completions/stream`**

Run: `grep -n "completions\|MapPost" Endpoints/AiEndpoints.cs | head -20`

- [ ] **Step 15.2: Resolve `AiFeature.Wizard` khi `req.Provider` hoặc `req.Model` rỗng**

Trong handler `/completions` + `/completions/stream` (cả 2), TRƯỚC khi gọi `ProviderRegistry.Resolve(req.Provider)`, chèn:
```csharp
if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.Model))
{
    var resolved = modelRegistry.Resolve(AiFeature.Wizard, req.Provider, req.Model);
    req = req with { Provider = resolved.Provider, Model = resolved.Model, ApiKey = req.ApiKey ?? resolved.ApiKey };
}
```

Trong signature handler, thêm `AiModelRegistry modelRegistry` param (DI tự inject vào minimal API delegate).

- [ ] **Step 15.3: Build + smoke**

`dotnet build` → PASS.
Smoke: gọi Wizard (tour quote) 1 lần qua UI. Confirm log dùng Primary.

- [ ] **Step 15.4: Commit**

```bash
git add Endpoints/AiEndpoints.cs
git commit -m "refactor(ai-endpoint): Wizard /completions dùng AiModelRegistry(Wizard) khi req thiếu"
```

---

## Task 16: Provider `DefaultModel` — bỏ đọc config, dùng catalog local

**Files:**
- Modify: `Services/Providers/AnthropicProvider.cs:42-46`
- Modify: `Services/Providers/OpenCodeProvider.cs:41-45`
- Modify: `Services/Providers/DeepSeekProvider.cs:50` (xem context)

- [ ] **Step 16.1: `AnthropicProvider.cs`**

Tìm property/field `DefaultModel`:
```csharp
private string DefaultModel
{
    get
    {
        var prov = _cfg["Models:Primary:Provider"];
        var mod  = _cfg["Models:Primary:Model"];
        ...
    }
}
```

Đổi thành:
```csharp
private string DefaultModel
    => Models.FirstOrDefault(m => m.Recommended)?.Id ?? Models.First().Id;
```

Xóa field `_cfg` nếu sau đó không còn dùng (`grep _cfg AnthropicProvider.cs`).

- [ ] **Step 16.2: `OpenCodeProvider.cs`** — pattern y hệt.

- [ ] **Step 16.3: `DeepSeekProvider.cs`** — pattern y hệt.

- [ ] **Step 16.4: Build verify**

`dotnet build` → PASS, 0 warning về `_cfg` unused (nếu còn dùng thì giữ).

- [ ] **Step 16.5: Commit**

```bash
git add Services/Providers/AnthropicProvider.cs Services/Providers/OpenCodeProvider.cs Services/Providers/DeepSeekProvider.cs
git commit -m "refactor(providers): DefaultModel dùng catalog local, bỏ cross-section Models:Primary lookup"
```

---

## Task 17: Bỏ default param `model = "claude-sonnet-4-5"` ở workflow

**Files:**
- Modify: `Services/Workflow/NativeToolScorer.cs:54`
- Modify: `Services/Workflow/AnthropicToolsClient.cs:64`

- [ ] **Step 17.1: `NativeToolScorer.cs`**

Đổi signature:
- Trước: `string model = "claude-sonnet-4-5",`
- Sau:    `string model,`

(Bỏ default value, caller PHẢI truyền. Sau task 7/9/10 các caller đã truyền `resolved.Model` rồi.)

- [ ] **Step 17.2: `AnthropicToolsClient.cs`**

Tương tự — bỏ default `model = "claude-sonnet-4-5"`.

- [ ] **Step 17.3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: PASS. Nếu fail với "no argument given for 'model'" → tìm caller còn thiếu, sửa truyền `resolved.Model` từ Registry.

- [ ] **Step 17.4: Commit**

```bash
git add Services/Workflow/NativeToolScorer.cs Services/Workflow/AnthropicToolsClient.cs
git commit -m "refactor(workflow): bỏ default param model ở NativeToolScorer + AnthropicToolsClient"
```

---

## Task 18: Reviews Agents — bỏ `const DefaultModel`

**Files:**
- Modify: `Services/Reviews/Agents/NativeToolReviewAgent.cs:28`
- Modify: `Services/Reviews/Agents/JsonPromptReviewAgent.cs` (kiểm tra)

- [ ] **Step 18.1: `NativeToolReviewAgent.cs`**

Tìm:
```csharp
private const string DefaultModel = "claude-sonnet-4-5";
```

Xóa hằng số này. Mọi chỗ dùng `DefaultModel` → thay bằng giá trị `model` mà caller (`ReviewService` đã resolve qua Registry) truyền vào `RunAsync(...)`.

Nếu caller (`ReviewService`) đang truyền `modelOverride` (có thể null) — confirm sau task 5, `resolved.Model` luôn non-null. Truyền `modelOverride` xuống agent.

- [ ] **Step 18.2: `JsonPromptReviewAgent.cs`** — kiểm tra `grep claude- Services/Reviews/Agents/JsonPromptReviewAgent.cs`. Nếu có hardcode tương tự, xóa.

- [ ] **Step 18.3: Build verify**

`dotnet build` → PASS.

- [ ] **Step 18.4: Smoke review 1 lần**

Confirm review KH chạy đúng, log provider/model = `Models:CustomerReview`.

- [ ] **Step 18.5: Commit**

```bash
git add Services/Reviews/Agents/
git commit -m "refactor(review-agents): bỏ const DefaultModel, dùng model từ caller (Registry-resolved)"
```

---

## Task 19: Rút gọn `ProviderKeyStore`

**Files:**
- Modify: `Services/Providers/ProviderKeyStore.cs`

- [ ] **Step 19.1: Viết lại thành wrapper Registry.KeyFor**

Ghi đè toàn bộ file:

```csharp
namespace TourkitAiProxy.Services.Providers;

/// <summary>
/// Legacy wrapper quanh AiModelRegistry.KeyFor — chỉ còn để code cũ (chưa qua AiCallGateway phase 2)
/// vẫn build được. Sẽ xóa hẳn ở phase 2.
/// </summary>
public class ProviderKeyStore
{
    private readonly AiModelRegistry _registry;
    public ProviderKeyStore(AiModelRegistry registry) => _registry = registry;

    public string? Get(string providerId) => _registry.KeyFor(providerId);
    public bool HasKey(string providerId) => !string.IsNullOrWhiteSpace(_registry.KeyFor(providerId));
}
```

- [ ] **Step 19.2: Build verify**

`dotnet build` → PASS.

- [ ] **Step 19.3: Commit**

```bash
git add Services/Providers/ProviderKeyStore.cs
git commit -m "refactor(provider-key): ProviderKeyStore rút gọn thành wrapper AiModelRegistry.KeyFor"
```

---

## Task 20: `ProviderRegistry` — bỏ fallback `Providers:Default`

**Files:**
- Modify: `Services/Providers/ProviderRegistry.cs:19`

- [ ] **Step 20.1: Đổi logic default**

Trước:
```csharp
var defaultId = cfg["Models:Primary:Provider"] ?? cfg["Providers:Default"];
_default = (defaultId != null && _byId.TryGetValue(defaultId, out var d))
    ? d
    : _byId.Values.First();
```

Sau:
```csharp
var defaultId = cfg["Models:Primary:Provider"]
    ?? throw new InvalidOperationException(
        "Cấu hình thiếu Models:Primary:Provider — đây là default provider bắt buộc.");
if (!_byId.TryGetValue(defaultId, out var d))
    throw new InvalidOperationException(
        $"Provider '{defaultId}' (Models:Primary:Provider) chưa đăng ký trong DI.");
_default = d;
```

- [ ] **Step 20.2: Build + smoke**

`dotnet build` → PASS.
Smoke: app start. Nếu throw → confirm `Models:Primary:Provider` có trong appsettings.json.

- [ ] **Step 20.3: Commit**

```bash
git add Services/Providers/ProviderRegistry.cs
git commit -m "refactor(provider-registry): bỏ fallback Providers:Default, throw khi Primary thiếu"
```

---

## Task 21: Xóa `ModelDefaults`

**Files:**
- Delete: `Services/Providers/ModelDefaults.cs`
- Modify: `Program.cs` (xóa registration)

- [ ] **Step 21.1: Verify 0 caller còn lại**

Run: `grep -rn "ModelDefaults" Services/ Endpoints/ Program.cs`
Expected: 0 match (sau khi tasks 5/6/14 đã migrate). Nếu còn match → migrate caller đó trước.

- [ ] **Step 21.2: Xóa file**

```bash
rm Services/Providers/ModelDefaults.cs
```

- [ ] **Step 21.3: Xóa registration trong `Program.cs`**

Tìm:
```csharp
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.ModelDefaults>();   // Models:Primary + Models:Review từ appsettings
```

Xóa cả dòng. Giữ dòng `AddSingleton<...AiModelRegistry>();` ngay sau (đã thêm task 3).

- [ ] **Step 21.4: Build verify**

`dotnet build TourkitAiProxy.csproj` → PASS 0 error 0 warning.

- [ ] **Step 21.5: Commit**

```bash
git add Services/Providers/ModelDefaults.cs Program.cs
git commit -m "refactor(ai-config): xóa ModelDefaults — thay hoàn toàn bởi AiModelRegistry"
```

---

## Task 22: Verification — grep guards

- [ ] **Step 22.1: 0 hardcode model trong service/endpoint layer**

Run:
```bash
grep -rn '"claude-sonnet-4-5"' Services/ Endpoints/
grep -rn '"claude-haiku-4-5"' Services/ Endpoints/
```

Expected: CHỈ còn match trong provider catalog (`AnthropicProvider.cs`, `NineRoutesProvider.cs`, `Services/AiUsageLog.cs` pricing table). KHÔNG có match trong service feature (Visa/Deal/Tour/Review/Chat/Mail/Widget).

- [ ] **Step 22.2: 0 cross-section lookup `Models:*` ngoài Registry**

Run:
```bash
grep -rn 'Models:Primary' Services/ Endpoints/
grep -rn 'Models:Review' Services/ Endpoints/
```

Expected: 
- `Models:Primary` CHỈ match trong `AiModelRegistry.cs` và `ProviderRegistry.cs`.
- `Models:Review` → 0 match (đã rename hoặc xóa).

- [ ] **Step 22.3: 0 reference `ModelDefaults`**

Run: `grep -rn "ModelDefaults" Services/ Endpoints/ Program.cs`
Expected: 0 match.

- [ ] **Step 22.4: Document kết quả grep vào commit message (nếu cần)**

Nếu có grep nào fail → quay lại task tương ứng fix. Nếu pass hết → tiếp tục task 23.

---

## Task 23: Verification — full build + run tests

- [ ] **Step 23.1: Build clean toàn solution**

```bash
dotnet clean TourkitAiProxy.csproj
dotnet build TourkitAiProxy.csproj
```

Expected: 0 error, 0 warning.

- [ ] **Step 23.2: Run unit tests**

```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```

Expected: tất cả test pass (Mail tests cũ + AiModelRegistryTests mới).

- [ ] **Step 23.3: Cập nhật e2e test nếu cần**

File `e2e/tests/05-api-direct.spec.js` test "6 AI feature dùng Models:Primary/Review". Đọc file, nếu reference `Models:Review` → đổi sang `Models:CustomerReview`. Nếu test chạy hard-coded API, có thể bỏ qua step này.

```bash
grep -n "Models:Review\|Models:Primary" e2e/tests/05-api-direct.spec.js
```

Nếu match → cập nhật. Commit nếu sửa.

---

## Task 24: Smoke test E2E mỗi feature

Chạy app: `dotnet run --project TourkitAiProxy.csproj`. Mỗi feature gọi 1 lần qua UI (hoặc curl), xem `data/ai-usage.jsonl` confirm provider/model.

- [ ] **Step 24.1: Wizard tour-quote** — `/` (homepage), tạo 1 báo giá nhỏ. Expected: Primary (anthropic haiku).
- [ ] **Step 24.2: Customer Review** — `/customers`, review 1 KH. Expected: deepseek deepseek-chat.
- [ ] **Step 24.3: Mail Classify** — `/mail`, Refresh sync. Expected: deepseek deepseek-chat (cho email mới).
- [ ] **Step 24.4: Mail Draft** — mở 1 mail, "Soạn nháp". Expected: Primary.
- [ ] **Step 24.5: Mail Compose** — "Soạn email mới". Expected: Primary.
- [ ] **Step 24.6: Visa Score** — `/visa`, upload + chấm 1 hồ sơ. Expected: Primary (anthropic haiku).
- [ ] **Step 24.7: Visa Extract** — cùng flow visa, kiểm tra log có dòng extract. Expected: Primary.
- [ ] **Step 24.8: Deal Score** — `/deals`, chấm 1 cơ hội. Expected: Primary.
- [ ] **Step 24.9: Tour Builder** — `/tour-builder`, paste mô tả tour, build. Expected: Primary.
- [ ] **Step 24.10: Chat Analytics** — `/assistant`, hỏi "doanh thu tháng này". Expected: Primary.
- [ ] **Step 24.11: NCC Import** — `/ncc-import`, upload file mẫu. Expected: Primary.
- [ ] **Step 24.12: Widget** — (skip nếu chưa có UI test widget; verify build pass là đủ).

- [ ] **Step 24.13: Tag verification commit**

```bash
git tag -a phase1-centralize-config -m "Phase 1 complete: AiModelRegistry, 0 hardcode, 0 cross-section lookup"
```

---

## Tổng kết deliverable

Sau khi xong tất cả task:

- 1 file mới: `Services/Providers/AiModelRegistry.cs` (~95 dòng)
- 1 file test mới: `TourkitAiProxy.Tests/AiModelRegistryTests.cs` (~140 dòng)
- 1 file đã xóa: `Services/Providers/ModelDefaults.cs`
- ~22 file đã sửa (provider catalog, services, endpoints, Program.cs, appsettings.example.json)
- 1 tag git `phase1-centralize-config`
- 0 hardcode `"claude-sonnet-4-5"` / `"claude-haiku-4-5"` ngoài provider catalog
- 0 `_cfg["Models:..."]` lookup ngoài Registry
- 12 feature đều resolve qua `_registry.Resolve(AiFeature.X)`
- Spec phase 2 (`centralize-ai-call-gateway`) chuẩn bị bắt đầu khi Phase 1 ổn định.

---

## Notes khi execute

1. **Mỗi task = 1 commit**, không gộp. Commit message theo convention `refactor(<area>): ...` ngoại trừ task 1+2 dùng `feat`/`test`.
2. **KHÔNG sửa `appsettings.json` thật** trong commit nào — file gitignored. Anh tự edit local theo step 4.3.
3. Nếu task nào build fail → revert commit, fix, retry. Không skip.
4. Smoke step có thể delay nếu UI feature đó chưa test được lúc đó — nhưng PHẢI xong tất cả ở task 24 trước khi tag.
5. Nếu phát hiện file ngoài migration table có hardcode `_cfg["Models:..."]` hoặc `"claude-..."`  — bổ sung task riêng, KHÔNG silent fix trong task khác.
