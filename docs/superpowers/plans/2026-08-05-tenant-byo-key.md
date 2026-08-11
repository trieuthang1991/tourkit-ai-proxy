# BYO AI Key per-tenant — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mỗi tenant tự mang key AI (`{provider, model, apiKey}`) — bật thì MỌI lệnh AI của tenant chạy bằng key đó và không trừ quota; chưa bật thì dùng key hệ thống + trừ quota như "trial".

**Architecture:** 1 seam ở tầng provider (6 provider) + override provider ở `ProviderRegistry.Resolve`. Key mã hóa Crypton trong `dbo.TenantAiKeys`, resolve qua `TenantAiKeyStore` (cache in-mem, tenant từ `AiCallContext.Resolve().Tenant`).

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper/SQL Server (`PushDb`), Crypton AES, xUnit, React no-build.

## Global Constraints

- Target `net8.0` — KHÔNG đổi TFM/version.
- User-facing string / comment / log = **tiếng Việt**.
- Key AI: **mã hóa Crypton at-rest**, **KHÔNG bao giờ** trả client thô / log / trace. GET chỉ trả `masked`.
- DateTime **UTC + Z** (`SYSUTCDATETIME()` / `DateTime.UtcNow`).
- Schema idempotent trong `TourkitAiDb.SchemaSql`; cập nhật `docs/database-schema.md`.
- Connection qua `TourkitAiDb.OpenAsync()`.
- Endpoint tenant-scoped **require `X-Session-Id`** (mẫu `RequireSession` như MailEndpoints/AssistantProfileEndpoints).
- Quota chỉ áp khi **KHÔNG** dùng key tenant (`UsesTenantKey=false`).
- Frontend no-build: sync `index.html` (Babel) **và** `bundle-entry.js` (esbuild).
- Test xUnit pure-logic; `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`. DB/HTTP/AI verify thủ công.
- `<InternalsVisibleTo Include="TourkitAiProxy.Tests" />` đã có.

---

## File Structure

**Tạo mới:**
- `Services/Providers/TenantAiKey.cs` — record + `MaskOf()`.
- `Services/Providers/TenantAiKeyRepository.cs` — Dapper Get/Upsert/Delete.
- `Services/Providers/TenantAiKeyStore.cs` — cache + `Get()` + `Invalidate()` + `ResolveForProvider()` + static `ResolveCore()`.
- `Endpoints/AssistantAiKeyEndpoints.cs` — GET/PUT/DELETE `/api/v1/assistant/ai-key`.
- `wwwroot/pages/assistant-ai-key.jsx` — form.
- `TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs` — test `ResolveCore` + `MaskOf`.

**Sửa:**
- `Services/Db/TourkitAiDb.cs` — DDL `dbo.TenantAiKeys` + log line.
- 6 provider (`AnthropicProvider`, `GrokProvider`, `DeepSeekProvider`, `OpenAIProvider`, `NineRoutesProvider`, `OpenCodeProvider`) — inject store, đổi khối `EnsureQuota` + resolve key + model.
- `Services/Providers/ProviderRegistry.cs` — `Resolve` tenant-aware (override provider).
- `Services/Quota/QuotaExceptionMiddleware.cs` — thêm `needsByoKey:true` + message.
- `Program.cs` — DI repo + store; `app.MapAssistantAiKeyEndpoints()`.
- `wwwroot/index.html`, `wwwroot/bundle-entry.js`, `wwwroot/app.jsx` — wiring trang.
- `docs/database-schema.md`.

---

## Task 1: Model `TenantAiKey` + MaskOf

**Files:**
- Create: `Services/Providers/TenantAiKey.cs`
- Test: `TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs` (phần MaskOf)

**Interfaces:**
- Produces: `record TenantAiKey(string TenantId, string Provider, string? Model, string ApiKeyEnc, string Masked, bool Enabled, DateTime? ValidatedAtUtc, string? UpdatedBy, DateTime UpdatedAtUtc)`; `static string TenantAiKey.MaskOf(string rawKey)`.

- [ ] **Step 1: Write the failing test**

```csharp
// TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs
using TourkitAiProxy.Services.Providers;
using Xunit;

namespace TourkitAiProxy.Tests;

public class TenantAiKeyResolveTests
{
    [Theory]
    [InlineData("sk-ant-1234567890abcd", "sk-…abcd")]
    [InlineData("short", "••••")]
    public void MaskOf_shows_only_head_and_tail(string raw, string expected)
        => Assert.Equal(expected, TenantAiKey.MaskOf(raw));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter TenantAiKeyResolveTests`
Expected: FAIL — `TenantAiKey` không tồn tại.

- [ ] **Step 3: Write minimal implementation**

```csharp
// Services/Providers/TenantAiKey.cs
namespace TourkitAiProxy.Services.Providers;

/// Cấu hình key AI riêng của 1 tenant (BYO). ApiKeyEnc = Crypton AES; KHÔNG bao giờ trả client thô.
/// Enabled=false → tenant về key hệ thống + quota (trial).
public record TenantAiKey(
    string TenantId,
    string Provider,          // id provider: openai | anthropic | deepseek | grok | opencode-go | nine-routes
    string? Model,            // model mặc định tenant chọn (null → default của provider)
    string ApiKeyEnc,         // Crypton-encrypted
    string Masked,            // "sk-…abcd" để hiển thị
    bool Enabled,
    DateTime? ValidatedAtUtc,
    string? UpdatedBy,
    DateTime UpdatedAtUtc)
{
    /// Che key: chỉ lộ 3 ký tự đầu + 4 ký tự cuối. Key quá ngắn → toàn dấu chấm.
    public static string MaskOf(string rawKey)
        => string.IsNullOrEmpty(rawKey) || rawKey.Length <= 8 ? "••••" : rawKey[..3] + "…" + rawKey[^4..];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter TenantAiKeyResolveTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/Providers/TenantAiKey.cs TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs
git commit -m "feat(byo-key): model TenantAiKey + MaskOf"
```

---

## Task 2: Schema + Repository + Store + ResolveCore (cốt lõi test được)

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs`, `docs/database-schema.md`
- Create: `Services/Providers/TenantAiKeyRepository.cs`, `Services/Providers/TenantAiKeyStore.cs`
- Test: `TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs` (thêm phần ResolveCore)

**Interfaces:**
- Consumes: `TenantAiKey` (Task 1); `TourkitAiDb.OpenAsync()`; `Crypton`.
- Produces: `TenantAiKeyRepository.GetAsync/UpsertAsync/DeleteAsync`; `TenantAiKeyStore.Get(string? tenantId)` → `TenantAiKey?`; `.Invalidate(string)`; `.ResolveForProvider(string? tenantId, string providerId, string? reqApiKey, string? reqModel)` → `ProviderKeyResolution`; `record ProviderKeyResolution(string? ApiKey, string? Model, bool UsesTenantKey)`; `internal static ProviderKeyResolution ResolveCore(TenantAiKey? key, string providerId, string? reqApiKey, string? reqModel, Func<string,string?> decrypt)`.

- [ ] **Step 1: Thêm DDL vào `TourkitAiDb.SchemaSql`** (trước dấu đóng chuỗi `";`)

```sql
-- Key AI riêng per-tenant (BYO). ApiKeyEnc = Crypton. Enabled=1 → mọi lệnh AI tenant dùng key này (skip quota).
IF OBJECT_ID('dbo.TenantAiKeys', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantAiKeys (
        TenantId       NVARCHAR(128)  NOT NULL,
        Provider       NVARCHAR(32)   NOT NULL,
        Model          NVARCHAR(128)  NULL,
        ApiKeyEnc      NVARCHAR(1024) NOT NULL,
        Masked         NVARCHAR(32)   NOT NULL CONSTRAINT DF_TenantAiKeys_Masked DEFAULT '',
        Enabled        BIT            NOT NULL CONSTRAINT DF_TenantAiKeys_Enabled DEFAULT 0,
        ValidatedAtUtc DATETIME2      NULL,
        UpdatedBy      NVARCHAR(128)  NULL,
        UpdatedAtUtc   DATETIME2      NOT NULL CONSTRAINT DF_TenantAiKeys_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TenantAiKeys PRIMARY KEY CLUSTERED (TenantId)
    );
END;
```
Thêm `TenantAiKeys` vào chuỗi log của `InitAsync`. Thêm 1 dòng vào `docs/database-schema.md`.

- [ ] **Step 2: Repository (Dapper)**

```csharp
// Services/Providers/TenantAiKeyRepository.cs
using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Providers;

public class TenantAiKeyRepository
{
    private readonly TourkitAiDb _db;
    public TenantAiKeyRepository(TourkitAiDb db) => _db = db;

    public async Task<TenantAiKey?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QueryFirstOrDefaultAsync<TenantAiKey>(
            "SELECT TenantId, Provider, Model, ApiKeyEnc, Masked, Enabled, ValidatedAtUtc, UpdatedBy, UpdatedAtUtc " +
            "FROM dbo.TenantAiKeys WHERE TenantId = @tenantId", new { tenantId });
    }

    public async Task UpsertAsync(TenantAiKey k, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
MERGE dbo.TenantAiKeys AS tgt USING (SELECT @TenantId AS TenantId) AS src ON tgt.TenantId = src.TenantId
WHEN MATCHED THEN UPDATE SET Provider=@Provider, Model=@Model, ApiKeyEnc=@ApiKeyEnc, Masked=@Masked,
    Enabled=@Enabled, ValidatedAtUtc=@ValidatedAtUtc, UpdatedBy=@UpdatedBy, UpdatedAtUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TenantId, Provider, Model, ApiKeyEnc, Masked, Enabled, ValidatedAtUtc, UpdatedBy, UpdatedAtUtc)
    VALUES (@TenantId, @Provider, @Model, @ApiKeyEnc, @Masked, @Enabled, @ValidatedAtUtc, @UpdatedBy, SYSUTCDATETIME());",
            new { k.TenantId, k.Provider, k.Model, k.ApiKeyEnc, k.Masked, k.Enabled, k.ValidatedAtUtc, k.UpdatedBy });
    }

    public async Task DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("DELETE FROM dbo.TenantAiKeys WHERE TenantId = @tenantId", new { tenantId });
    }
}
```

- [ ] **Step 3: Store + Resolve (thêm test ResolveCore trước)**

Test (thêm vào `TenantAiKeyResolveTests`):

```csharp
    private static TenantAiKey Key(string provider = "openai", bool enabled = true, string enc = "ENCED", string? model = "gpt-x")
        => new("t1", provider, model, enc, "sk-…abcd", enabled, null, null, default);

    // decrypt giả: "ENCED" → "sk-real"; khác → null
    private static string? Dec(string enc) => enc == "ENCED" ? "sk-real" : null;

    [Fact]
    public void Resolve_no_key_uses_system_no_tenant()
    {
        var r = TenantAiKeyStore.ResolveCore(null, "openai", null, null, Dec);
        Assert.False(r.UsesTenantKey);
        Assert.Null(r.ApiKey);
    }

    [Fact]
    public void Resolve_disabled_key_uses_system()
    {
        var r = TenantAiKeyStore.ResolveCore(Key(enabled: false), "openai", null, null, Dec);
        Assert.False(r.UsesTenantKey);
    }

    [Fact]
    public void Resolve_provider_mismatch_uses_system()
    {
        var r = TenantAiKeyStore.ResolveCore(Key(provider: "anthropic"), "openai", null, null, Dec);
        Assert.False(r.UsesTenantKey);
    }

    [Fact]
    public void Resolve_match_uses_tenant_key_and_model()
    {
        var r = TenantAiKeyStore.ResolveCore(Key(), "openai", null, null, Dec);
        Assert.True(r.UsesTenantKey);
        Assert.Equal("sk-real", r.ApiKey);
        Assert.Equal("gpt-x", r.Model);
    }

    [Fact]
    public void Resolve_legacy_reqApiKey_wins_and_keeps_quota()
    {
        var r = TenantAiKeyStore.ResolveCore(Key(), "openai", "sk-client", null, Dec);
        Assert.False(r.UsesTenantKey);         // per-request key → quota vẫn áp như cũ
        Assert.Equal("sk-client", r.ApiKey);
    }

    [Fact]
    public void Resolve_decrypt_fail_falls_back_to_system()
    {
        var r = TenantAiKeyStore.ResolveCore(Key(enc: "BADENC"), "openai", null, null, Dec);
        Assert.False(r.UsesTenantKey);
    }
```

Run: `dotnet test ... --filter TenantAiKeyResolveTests` → FAIL (chưa có `ResolveCore`).

Implementation:

```csharp
// Services/Providers/TenantAiKeyStore.cs
using System.Collections.Concurrent;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Providers;

public sealed record ProviderKeyResolution(string? ApiKey, string? Model, bool UsesTenantKey);

/// Cache in-mem key AI per-tenant + resolve theo provider. DB lỗi → coi như không có (fallback trial).
public class TenantAiKeyStore
{
    private readonly TenantAiKeyRepository _repo;
    private readonly ILogger<TenantAiKeyStore> _log;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (TenantAiKey? K, DateTime Exp)> _cache = new();

    public TenantAiKeyStore(TenantAiKeyRepository repo, ILogger<TenantAiKeyStore> log)
    { _repo = repo; _log = log; }

    public TenantAiKey? Get(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        if (_cache.TryGetValue(tenantId, out var hit) && hit.Exp > DateTime.UtcNow) return hit.K;
        try
        {
            var k = _repo.GetAsync(tenantId).GetAwaiter().GetResult();
            _cache[tenantId] = (k, DateTime.UtcNow + Ttl);
            return k;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TenantAiKeyStore] đọc key {Tenant} lỗi → dùng key hệ thống", tenantId);
            return null;
        }
    }

    public void Invalidate(string tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId)) _cache.TryRemove(tenantId, out _);
    }

    /// Resolve key + model cho 1 provider cụ thể. UsesTenantKey=true → skip quota + dùng key tenant.
    public ProviderKeyResolution ResolveForProvider(string? tenantId, string providerId, string? reqApiKey, string? reqModel)
        => ResolveCore(Get(tenantId), providerId, reqApiKey, reqModel, TryDecrypt);

    private string? TryDecrypt(string enc)
    {
        try { return Crypton.Decrypt(enc); }
        catch (Exception ex) { _log.LogWarning(ex, "[TenantAiKeyStore] decrypt key lỗi"); return null; }
    }

    /// Pure logic (test được): quyết định dùng key tenant hay hệ thống.
    internal static ProviderKeyResolution ResolveCore(
        TenantAiKey? key, string providerId, string? reqApiKey, string? reqModel, Func<string, string?> decrypt)
    {
        // Legacy per-request key (client tự gửi) → giữ nguyên hành vi cũ: dùng key đó, quota VẪN áp.
        if (!string.IsNullOrWhiteSpace(reqApiKey))
            return new ProviderKeyResolution(reqApiKey, reqModel, false);

        bool match = key is { Enabled: true }
                     && !string.IsNullOrWhiteSpace(key.ApiKeyEnc)
                     && string.Equals(key.Provider, providerId, StringComparison.OrdinalIgnoreCase);
        if (match)
        {
            var dec = decrypt(key!.ApiKeyEnc);
            if (!string.IsNullOrWhiteSpace(dec))
                return new ProviderKeyResolution(dec, string.IsNullOrWhiteSpace(reqModel) ? key.Model : reqModel, true);
        }
        return new ProviderKeyResolution(null, reqModel, false);   // ApiKey=null → provider dùng ProviderKeyStore
    }
}
```

Run: `dotnet test ... --filter TenantAiKeyResolveTests` → PASS (6 + MaskOf).

- [ ] **Step 4: Commit**

```bash
git add Services/Db/TourkitAiDb.cs Services/Providers/TenantAiKeyRepository.cs Services/Providers/TenantAiKeyStore.cs docs/database-schema.md TourkitAiProxy.Tests/TenantAiKeyResolveTests.cs
git commit -m "feat(byo-key): bảng dbo.TenantAiKeys + repo + store + ResolveCore (test khóa 6 nhánh)"
```

---

## Task 3: Wire 6 provider — key tenant + skip quota

**Files:**
- Modify: `Services/Providers/{Anthropic,Grok,DeepSeek,OpenAI,NineRoutes,OpenCode}Provider.cs`

**Interfaces:**
- Consumes: `TenantAiKeyStore.ResolveForProvider` (Task 2); `_ctx.Resolve().Tenant`, `EnsureQuota()`, `_keys.Get(Id)` (có sẵn).

- [ ] **Step 1: Inject `TenantAiKeyStore` vào 6 provider**

Mỗi provider: thêm field + tham số ctor:
```csharp
    private readonly TenantAiKeyStore _tenantKeys;
```
Thêm `TenantAiKeyStore tenantKeys` vào ctor, gán `_tenantKeys = tenantKeys;`.

- [ ] **Step 2: Đổi khối resolve ở đầu `CompleteAsync` và `StreamAsync`**

Khối HIỆN TẠI (mẫu — có ở mỗi provider dùng `_keys`, vd AnthropicProvider dòng 47-48):
```csharp
        EnsureQuota();
        var key = !string.IsNullOrWhiteSpace(req.ApiKey) ? req.ApiKey : _keys.Get(Id);
```
ĐỔI THÀNH:
```csharp
        var byo = _tenantKeys.ResolveForProvider(_ctx.Resolve().Tenant, Id, req.ApiKey, req.Model);
        if (!byo.UsesTenantKey) EnsureQuota();     // key tenant → không trừ quota (trial)
        var key = byo.ApiKey ?? _keys.Get(Id);
```
Và tại dòng resolve MODEL (`var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel() : req.Model!;`), đổi `req.Model` → `byo.Model`:
```csharp
        var model = string.IsNullOrWhiteSpace(byo.Model) ? DefaultModel() : byo.Model!;
```

> **NineRoutesProvider / OpenCodeProvider** không có `_keys` (ProviderKeyStore) — chúng resolve key từ config/req riêng. Với 2 file này: vẫn thêm dòng `byo = ResolveForProvider(...)` + `if (!byo.UsesTenantKey) EnsureQuota();`, rồi ở chỗ chúng đang lấy key (tìm `req.ApiKey`), ưu tiên `byo.ApiKey` nếu có: `var key = byo.ApiKey ?? <cách lấy key cũ của provider đó>;`. Model tương tự dùng `byo.Model`.

- [ ] **Step 3: Build + full test (không hồi quy)**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → 0 errors
Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` → PASS

- [ ] **Step 4: Commit**

```bash
git add Services/Providers/AnthropicProvider.cs Services/Providers/GrokProvider.cs Services/Providers/DeepSeekProvider.cs Services/Providers/OpenAIProvider.cs Services/Providers/NineRoutesProvider.cs Services/Providers/OpenCodeProvider.cs
git commit -m "feat(byo-key): 6 provider dùng key tenant + skip quota khi BYO"
```

---

## Task 4: Override provider ở `ProviderRegistry.Resolve`

**Files:**
- Modify: `Services/Providers/ProviderRegistry.cs`

**Interfaces:**
- Consumes: `AiCallContext.Resolve().Tenant`, `TenantAiKeyStore.Get()`.

- [ ] **Step 1: Inject context + store; override trong Resolve**

Đổi ctor + `Resolve`:
```csharp
    private readonly AiCallContext _ctx;
    private readonly TenantAiKeyStore _tenantKeys;

    public ProviderRegistry(IEnumerable<IAiProvider> providers, IConfiguration cfg,
        AiCallContext ctx, TenantAiKeyStore tenantKeys)
    {
        _ctx = ctx; _tenantKeys = tenantKeys;
        // ... giữ nguyên phần _byId + _default hiện có ...
    }

    public IAiProvider Resolve(string? id)
    {
        // BYO: tenant bật key riêng → MỌI lệnh AI route sang provider của họ (bỏ qua id feature yêu cầu).
        var k = _tenantKeys.Get(_ctx.Resolve().Tenant);
        if (k is { Enabled: true } && !string.IsNullOrWhiteSpace(k.ApiKeyEnc) && _byId.TryGetValue(k.Provider, out var bp))
            return bp;

        return string.IsNullOrWhiteSpace(id) ? _default
             : _byId.TryGetValue(id, out var p) ? p
             : _default;
    }
```

> Lưu ý DI: `ProviderRegistry` là singleton; `AiCallContext` + `TenantAiKeyStore` cũng singleton → OK. `AiCallContext.Resolve()` trả tenant=null khi không có HttpContext/override → không override (system call an toàn).

- [ ] **Step 2: Build + smoke**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → 0 errors. Chạy app, `/api/v1/providers` vẫn liệt kê bình thường (dùng `All`, không đụng Resolve).

- [ ] **Step 3: Commit**

```bash
git add Services/Providers/ProviderRegistry.cs
git commit -m "feat(byo-key): override provider theo key tenant tại ProviderRegistry.Resolve"
```

---

## Task 5: 429 → `needsByoKey` (đổi thông điệp hết trial)

**Files:**
- Modify: `Services/Quota/QuotaExceptionMiddleware.cs`

- [ ] **Step 1: Sửa body 429**

Trong khối bắt `QuotaExhaustedException`, đổi JSON trả về (giữ status 429) thành:
```csharp
        var payload = new
        {
            error = "Bạn đã hết lượt dùng thử bằng key hệ thống. Vui lòng nhập key AI riêng để tiếp tục sử dụng.",
            needsByoKey = true,
            quota = new { limit = ex.Limit, used = ex.Used }
        };
        // ... serialize payload như cũ (camelCase Web), status 429 ...
```

> Mở file để khớp cách serialize hiện có (tên biến `ex`, JsonSerializerDefaults.Web). Giữ nguyên status 429.

- [ ] **Step 2: Build + smoke**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Services/Quota/QuotaExceptionMiddleware.cs
git commit -m "feat(byo-key): 429 hết trial trả needsByoKey + thông điệp mời nhập key"
```

---

## Task 6: Endpoints GET/PUT/DELETE + validate + DI

**Files:**
- Create: `Endpoints/AssistantAiKeyEndpoints.cs`
- Modify: `Program.cs`

**Interfaces:**
- Consumes: `TenantAiKeyStore`, `TenantAiKeyRepository`, `ProviderRegistry`, `AiCallContext`, `TkSessionStore`, `Crypton`, `TenantAiKey.MaskOf`.

- [ ] **Step 1: Viết endpoint**

```csharp
// Endpoints/AssistantAiKeyEndpoints.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Security;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// BYO key per-tenant (tenant-scoped qua X-Session-Id).
///   GET    /api/v1/assistant/ai-key → {provider, model, configured, masked, enabled, validatedAtUtc}
///   PUT    /api/v1/assistant/ai-key → {provider, model, apiKey, enabled} → validate → lưu (mã hóa)
///   DELETE /api/v1/assistant/ai-key → xóa → về trial
public static class AssistantAiKeyEndpoints
{
    public record KeyRequest(string Provider, string? Model, string? ApiKey, bool Enabled);

    public static IEndpointRouteBuilder MapAssistantAiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapGet("/assistant/ai-key", (HttpContext ctx, TenantAiKeyStore store, TkSessionStore sessions) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Results.Json(new { error = "Chưa đăng nhập" }, statusCode: 401);
            var k = store.Get(auth.Value.Tenant);
            if (k == null) return Results.Json(new { configured = false });
            return Results.Json(new { k.Provider, k.Model, configured = true, k.Masked, k.Enabled, k.ValidatedAtUtc });
        });

        v1.MapPut("/assistant/ai-key", async (HttpContext ctx, KeyRequest req, TenantAiKeyStore store,
            TenantAiKeyRepository repo, ProviderRegistry registry, AiCallContext callCtx,
            TkSessionStore sessions, CancellationToken ct) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Results.Json(new { error = "Chưa đăng nhập" }, statusCode: 401);
            var (_, tenant, user) = auth.Value;

            if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.ApiKey))
                return Results.Json(new { ok = false, error = "Thiếu provider hoặc key" }, statusCode: 400);

            var provider = registry.All.FirstOrDefault(p => string.Equals(p.Id, req.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider == null) return Results.Json(new { ok = false, error = "Provider không hợp lệ" }, statusCode: 400);

            // Validate: gọi 1 lệnh AI ngắn bằng key mới. Push tenant=null → EnsureQuota bỏ qua (đang hết trial vẫn test được).
            try
            {
                using (callCtx.Push(AiFeatures.Other, tenant: null))
                {
                    var probe = new CompleteRequest("ping", req.Provider, req.Model, 16, 0, null, req.ApiKey, false);
                    _ = await provider.CompleteAsync(probe, ct);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "Key không dùng được: " + ex.Message }, statusCode: 400);
            }

            var enc = Crypton.Encrypt(req.ApiKey.Trim());
            var k = new TenantAiKey(tenant, req.Provider, req.Model, enc, TenantAiKey.MaskOf(req.ApiKey.Trim()),
                req.Enabled, DateTime.UtcNow, user, default);
            await repo.UpsertAsync(k, ct);
            store.Invalidate(tenant);
            return Results.Json(new { ok = true, validated = true, k.Masked, k.Enabled });
        });

        v1.MapDelete("/assistant/ai-key", async (HttpContext ctx, TenantAiKeyStore store,
            TenantAiKeyRepository repo, TkSessionStore sessions, CancellationToken ct) =>
        {
            var auth = RequireSession(ctx, sessions);
            if (auth == null) return Results.Json(new { error = "Chưa đăng nhập" }, statusCode: 401);
            await repo.DeleteAsync(auth.Value.Tenant, ct);
            store.Invalidate(auth.Value.Tenant);
            return Results.Json(new { ok = true });
        });

        return routes;
    }

    private static (string SessionId, string Tenant, string User)? RequireSession(HttpContext ctx, TkSessionStore sessions)
    {
        var sid = ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sid)) return null;
        var s = sessions.Get(sid);
        if (s == null || string.IsNullOrWhiteSpace(s.TenantId)) return null;
        return (sid, s.TenantId, s.Username);
    }
}
```

> Khớp chữ ký `CompleteRequest` với định nghĩa thật ở `Models/Dtos.cs` (thứ tự: Prompt, Provider, Model, MaxTokens, Temperature, System, ApiKey, CacheSystem). `AiFeatures.Other` có trong `Services/AiCallContext.cs`.

- [ ] **Step 2: DI + Map trong `Program.cs`**

Cạnh cụm provider DI:
```csharp
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.TenantAiKeyRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.TenantAiKeyStore>();
```
Cạnh `app.MapMailEndpoints();`:
```csharp
app.MapAssistantAiKeyEndpoints();
```

- [ ] **Step 3: Build + smoke**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → 0 errors.
`GET /api/v1/assistant/ai-key` không kèm session → 401; kèm session chưa cấu hình → `{configured:false}`.

- [ ] **Step 4: Commit**

```bash
git add Endpoints/AssistantAiKeyEndpoints.cs Program.cs
git commit -m "feat(byo-key): endpoint GET/PUT/DELETE ai-key + validate key + DI"
```

---

## Task 7: UI cấu hình key

**Files:**
- Create: `wwwroot/pages/assistant-ai-key.jsx`
- Modify: `wwwroot/index.html`, `wwwroot/bundle-entry.js`, `wwwroot/app.jsx`

- [ ] **Step 1: Viết trang**

```jsx
// wwwroot/pages/assistant-ai-key.jsx
function AssistantAiKeyPage({ pushToast }) {
  const [providers, setProviders] = React.useState([]);
  const [f, setF] = React.useState({ provider: "", model: "", apiKey: "", enabled: true });
  const [cur, setCur] = React.useState(null);
  const [saving, setSaving] = React.useState(false);

  React.useEffect(() => {
    window.tourkitAuth.authedFetch("/api/v1/providers").then(r => r.json())
      .then(d => setProviders(Array.isArray(d) ? d : (d.items || d.providers || []))).catch(() => {});
    window.tourkitAuth.authedFetch("/api/v1/assistant/ai-key").then(r => r.json())
      .then(d => { if (d && d.configured) { setCur(d); setF(x => ({ ...x, provider: d.provider, model: d.model || "", enabled: d.enabled })); } })
      .catch(() => {});
  }, []);

  const save = async () => {
    if (!f.provider || !f.apiKey) { pushToast?.("Chọn provider và nhập key", "error"); return; }
    setSaving(true);
    try {
      const r = await window.tourkitAuth.authedFetch("/api/v1/assistant/ai-key", {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(f)
      });
      const d = await r.json();
      if (!d.ok) { pushToast?.(d.error || "Lưu thất bại", "error"); return; }
      setCur({ provider: f.provider, model: f.model, masked: d.masked, enabled: d.enabled, configured: true });
      setF(x => ({ ...x, apiKey: "" }));
      pushToast?.("Đã kiểm tra & lưu key", "success");
    } catch { pushToast?.("Lưu thất bại", "error"); }
    finally { setSaving(false); }
  };

  const remove = async () => {
    await window.tourkitAuth.authedFetch("/api/v1/assistant/ai-key", { method: "DELETE" });
    setCur(null); setF({ provider: "", model: "", apiKey: "", enabled: true });
    pushToast?.("Đã xóa key — quay lại dùng thử bằng key hệ thống", "success");
  };

  return (
    <div style={{ maxWidth: 640, margin: "0 auto", padding: 24 }}>
      <h1>Key AI riêng của bạn</h1>
      <p>Nhập key AI của doanh nghiệp để dùng không giới hạn (bạn tự trả tiền cho nhà cung cấp). Chưa nhập → dùng thử bằng key hệ thống theo hạn mức.</p>
      {cur && <div style={{ margin: "8px 0", padding: 8, background: "#eef" }}>
        Đang cấu hình: <b>{cur.provider}</b> {cur.model} — {cur.masked} — {cur.enabled ? "Đang bật" : "Đang tắt"}
        <button onClick={remove} style={{ marginLeft: 12 }}>Xóa</button>
      </div>}

      <label>Nhà cung cấp</label>
      <select value={f.provider} onChange={e => setF(x => ({ ...x, provider: e.target.value }))}>
        <option value="">— chọn —</option>
        {providers.map(p => <option key={p.id} value={p.id}>{p.label || p.id}</option>)}
      </select>

      <label>Model (tùy chọn)</label>
      <input value={f.model} onChange={e => setF(x => ({ ...x, model: e.target.value }))} placeholder="để trống = mặc định" />

      <label>API key</label>
      <input type="password" value={f.apiKey} onChange={e => setF(x => ({ ...x, apiKey: e.target.value }))} placeholder="sk-…" style={{ width: "100%" }} />

      <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0" }}>
        <input type="checkbox" checked={f.enabled} onChange={e => setF(x => ({ ...x, enabled: e.target.checked }))} /> Bật dùng key này
      </label>

      <button onClick={save} disabled={saving}>{saving ? "Đang kiểm tra…" : "Kiểm tra & lưu"}</button>
    </div>
  );
}
window.AssistantAiKeyPage = AssistantAiKeyPage;
```

- [ ] **Step 2: `index.html`** — thêm `<script type="text/babel" src="pages/assistant-ai-key.jsx"></script>`.
- [ ] **Step 3: `bundle-entry.js`** — thêm `import "./pages/assistant-ai-key.jsx";`.
- [ ] **Step 4: `app.jsx`** — route `<Route path="/assistant-ai-key" render={() => <window.AssistantAiKeyPage pushToast={pushToast} />} />` + `<Link to="/assistant-ai-key">Key AI</Link>`.

- [ ] **Step 5: Build + manual**

Run: `dotnet build TourkitAiProxy.csproj -c Debug` → 0 errors. Đăng nhập, mở `/assistant-ai-key`, chọn provider + dán key sai → "Key không dùng được"; key đúng → "Đã kiểm tra & lưu".

- [ ] **Step 6: Commit**

```bash
git add wwwroot/pages/assistant-ai-key.jsx wwwroot/index.html wwwroot/bundle-entry.js wwwroot/app.jsx
git commit -m "feat(byo-key): trang /assistant-ai-key (chọn provider + kiểm tra & lưu key)"
```

---

## Task 8: Manual E2E

- [ ] **Step 1:** Tenant chưa cấu hình key → hỏi AI → chạy bằng key hệ thống, quota topbar TĂNG.
- [ ] **Step 2:** Ép hết trial (admin set quota Used=Limit) → hỏi AI → **429** body có `needsByoKey:true` + thông điệp mời nhập key.
- [ ] **Step 3:** `/assistant-ai-key` nhập key SAI → validate fail, không lưu.
- [ ] **Step 4:** Nhập key ĐÚNG (vd OpenAI) → lưu OK. Hỏi AI lại → chạy được **dù quota đã hết**; quota **KHÔNG tăng** (trace/log `byo=true`); provider trong trace = provider tenant chọn (override).
- [ ] **Step 5:** Tắt/xóa key → hỏi AI → lại bị chặn 429 (về trial).
- [ ] **Step 6:** Kiểm tra log/trace **không lộ key thô**; GET ai-key chỉ trả `masked`.

---

## Self-Review (đã chạy)

- **Spec coverage:** §3 seam provider→Task 3; override→Task 4; §4 bảng→Task 2; §5 resolver→Task 2 (ResolveCore); §6 quota=trial + 429 needsByoKey→Task 3 (skip quota) + Task 5 (message); §7 mã hóa/validate/không-log→Task 6 (Crypton + validate + GET masked); §9 endpoint/UI→Task 6+7; §10 test→Task 1/2 (unit) + Task 8 (E2E). §8 ripple VietQR — non-goal, không task (đúng).
- **Placeholder scan:** không TBD; code thật từng step.
- **Type consistency:** `ResolveForProvider`/`ResolveCore`/`ProviderKeyResolution`/`Get`/`Invalidate`/`MaskOf`/`GetAsync`/`UpsertAsync`/`DeleteAsync` nhất quán giữa task.
- **Rủi ro khi thực thi:** (1) 2 provider NineRoutes/OpenCode không có `_keys` → Task 3 Step 2 có ghi chú adapt; (2) chữ ký `CompleteRequest` + tên `AiFeatures.Other` → Task 6 có ghi chú verify; (3) cách serialize trong `QuotaExceptionMiddleware` → Task 5 ghi chú mở file khớp; (4) override ở `ProviderRegistry.Resolve` áp cho MỌI lệnh AI của tenant BYO — đúng ý "1 key cho tất cả", nhưng nếu sau muốn giữ provider theo feature thì bỏ Task 4.
