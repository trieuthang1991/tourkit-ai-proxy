# NCC mẫu + Retriever chọn nguồn giá — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps dùng checkbox (`- [ ]`).

**Goal:** Dựng dataset NCC mẫu (`__sample__` trong `dbo.TourPriceCatalog`) + retriever chọn nguồn giá (mẫu / thật / cả 2 ưu tiên thật) để tenant data thưa vẫn dựng được giá tour.

**Architecture:** Tái dùng bảng/repo/luật mảng 1; NCC mẫu là rows với `TenantId="__sample__"`. Retriever gọi repo lấy rows theo nguồn + logic trộn thuần `PriceMerge.PreferReal`. Seed từ file (dựng 1 lần từ TopTour local).

**Tech Stack:** .NET 8 · Dapper (KHÔNG EF) · SQL Server · xUnit · Minimal API.

**Spec:** [docs/superpowers/specs/2026-07-18-tour-price-sample-and-source-retriever-design.md](../specs/2026-07-18-tour-price-sample-and-source-retriever-design.md)

## Global Constraints
- **Tiếng Việt** mọi comment/log/chuỗi/commit.
- **Dapper, không EF.** Reuse `TourkitAiDb.OpenAsync` + `CatalogRow` (mảng 1).
- **DateTime UTC + `Z`** (`DateTime.UtcNow`/`SYSUTCDATETIME()`).
- **`__sample__` KHÔNG phải tenant thật** — không được lọt vào thống kê/quota/admin cross-tenant. Reserved id (tenant thật là domain nên không trùng).
- **net8.0 pinned**, không thêm package mới. **Không copy-paste helper.**
- Test: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`.
- **GitNexus impact trước khi sửa symbol có sẵn** (Task 4 sửa repo mảng 1).

## File Structure
| File | Trách nhiệm |
|---|---|
| `Services/TourPrices/SampleCatalog.cs` | **Tạo** — hằng `TenantId="__sample__"` + `IsSample()`. 1 nguồn reserved id. |
| `Models/TourPriceModels.cs` | **Sửa** — thêm `PriceSource` enum + `PriceCandidate` + `PriceQuery`. |
| `Services/TourPrices/PriceMerge.cs` | **Tạo** — logic thuần trộn "ưu tiên thật" (test không cần DB). |
| `Services/TourPrices/TourPriceCatalogRepository.cs` | **Sửa** — thêm `QueryAsync` (lọc city/category/price). |
| `Services/TourPrices/TourPriceRetriever.cs` | **Tạo** — điều phối nguồn → repo + PriceMerge. |
| `Services/TourPrices/SampleCatalogSeeder.cs` | **Tạo** — nạp seed file → `__sample__` (idempotent). |
| `data/seed/tour-price-sample.json` | **Tạo** — seed (stub trước; full từ TopTour ở Task 7). |
| `Services/Bootstrap/WorkflowStackRegistration.cs` | **Sửa** — DI retriever + seeder. |
| `Tests/TourPrices/*` | **Tạo** — test SampleCatalog, PriceMerge, Seeder-parse. |

---

## Task 1: Hằng SampleCatalog

**Files:** Create `Services/TourPrices/SampleCatalog.cs`; Test `TourkitAiProxy.Tests/TourPrices/SampleCatalogTests.cs`

**Interfaces:** Produces `SampleCatalog.TenantId : string` (="__sample__") · `SampleCatalog.IsSample(string?) : bool`

- [ ] **Step 1: Test trước**
```csharp
using TourkitAiProxy.Services.TourPrices;
using Xunit;
namespace TourkitAiProxy.Tests.TourPrices;
public class SampleCatalogTests
{
    [Fact] public void TenantId_la_sample_reserved() => Assert.Equal("__sample__", SampleCatalog.TenantId);
    [Theory]
    [InlineData("__sample__", true)]
    [InlineData("erp.tourkit.vn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSample_dung(string? t, bool expected) => Assert.Equal(expected, SampleCatalog.IsSample(t));
}
```
- [ ] **Step 2: Chạy → FAIL** `dotnet test ... --filter SampleCatalogTests` → build error (SampleCatalog chưa có).
- [ ] **Step 3: Impl**
```csharp
namespace TourkitAiProxy.Services.TourPrices;

/// NCC mẫu (dữ liệu hệ thống) lưu trong dbo.TourPriceCatalog với TenantId dành riêng này.
/// Reserved — tenant thật là domain (vd erp.tourkit.vn) nên không bao giờ trùng "__sample__".
public static class SampleCatalog
{
    public const string TenantId = "__sample__";
    public static bool IsSample(string? tenantId) => tenantId == TenantId;
}
```
- [ ] **Step 4: Chạy → PASS** (5 test).
- [ ] **Step 5: Commit**
```bash
git add Services/TourPrices/SampleCatalog.cs TourkitAiProxy.Tests/TourPrices/SampleCatalogTests.cs
git commit -m "feat(tour-price): hằng SampleCatalog (__sample__ reserved tenant)"
```

---

## Task 2: Model PriceSource + PriceCandidate + PriceQuery

**Files:** Modify `Models/TourPriceModels.cs`

**Interfaces:** Produces `PriceSource { Sample, Real, Both }` · `PriceCandidate(CatalogRow Row, string Source)` (Source="real"|"sample") · `PriceQuery(string? CityNorm, int? CategoryId, decimal? MinPrice, decimal? MaxPrice)`

- [ ] **Step 1: Thêm vào cuối `Models/TourPriceModels.cs`**
```csharp
/// Nguồn giá khi retriever lấy ứng viên. Both = ưu tiên thật, lấp mẫu vào chỗ thiếu.
public enum PriceSource { Sample, Real, Both }

/// 1 dòng ứng viên + nhãn nguồn ("real" hoặc "sample") để UI/AI biết dòng nào là mẫu.
public record PriceCandidate(CatalogRow Row, string Source);

/// Điều kiện lọc ứng viên. Field null = không lọc theo trục đó.
public record PriceQuery(string? CityNorm, int? CategoryId, decimal? MinPrice, decimal? MaxPrice);
```
- [ ] **Step 2: Build** `dotnet build TourkitAiProxy.csproj` → 0 error.
- [ ] **Step 3: Commit**
```bash
git add Models/TourPriceModels.cs
git commit -m "feat(tour-price): model PriceSource + PriceCandidate + PriceQuery"
```

---

## Task 3: PriceMerge.PreferReal (logic trộn — core)

**Files:** Create `Services/TourPrices/PriceMerge.cs`; Test `TourkitAiProxy.Tests/TourPrices/PriceMergeTests.cs`

**Interfaces:**
- Consumes: `CatalogRow` (mảng 1) · `PriceCandidate` (Task 2)
- Produces: `PriceMerge.PreferReal(IReadOnlyList<CatalogRow> real, IReadOnlyList<CatalogRow> sample) → List<PriceCandidate>`

Quy tắc: giữ HẾT dòng thật (nhãn "real"); với mỗi cặp (CityNorm, CategoryId) mà thật KHÔNG có dòng nào → thêm dòng mẫu của cặp đó (nhãn "sample"). Cặp thật đã có → BỎ mẫu của cặp đó.

- [ ] **Step 1: Test trước**
```csharp
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TourPrices;
using Xunit;
namespace TourkitAiProxy.Tests.TourPrices;

public class PriceMergeTests
{
    static CatalogRow Row(string tenant, int id, string cityNorm, int cat) => new(
        TenantId: tenant, PricingId: id, ProviderServiceId: id, ProviderId: id,
        ProviderName: "NCC" + id, ProviderCode: null, City: cityNorm, CityNorm: cityNorm,
        CategoryId: cat, CategoryName: "Khách sạn", PriceName: null, Description: null,
        ContractPrice: 1_000_000m, PublicPrice: 1_200_000m, Stars: null);

    [Fact]
    public void ThatThieu_thi_lap_bang_mau_dung_cap()
    {
        var real = new[] { Row("t", 1, "da nang", 1) };           // thật: Đà Nẵng/KS
        var sample = new[] { Row("__sample__", 10, "da nang", 1), // mẫu: Đà Nẵng/KS (BỎ vì thật có)
                             Row("__sample__", 11, "nha trang", 1) }; // mẫu: Nha Trang/KS (GIỮ vì thật thiếu)
        var m = PriceMerge.PreferReal(real, sample);
        Assert.Equal(2, m.Count);
        Assert.Contains(m, c => c.Source == "real" && c.Row.PricingId == 1);
        Assert.Contains(m, c => c.Source == "sample" && c.Row.PricingId == 11);
        Assert.DoesNotContain(m, c => c.Row.PricingId == 10);      // mẫu Đà Nẵng bị bỏ
    }

    [Fact]
    public void That_rong_thi_toan_mau()
    {
        var sample = new[] { Row("__sample__", 10, "hue", 1) };
        var m = PriceMerge.PreferReal(System.Array.Empty<CatalogRow>(), sample);
        Assert.Single(m);
        Assert.Equal("sample", m[0].Source);
    }

    [Fact]
    public void Mau_rong_thi_toan_that()
    {
        var real = new[] { Row("t", 1, "da nang", 1), Row("t", 2, "da nang", 2) };
        var m = PriceMerge.PreferReal(real, System.Array.Empty<CatalogRow>());
        Assert.Equal(2, m.Count);
        Assert.All(m, c => Assert.Equal("real", c.Source));
    }
}
```
- [ ] **Step 2: Chạy → FAIL** (PriceMerge chưa có).
- [ ] **Step 3: Impl**
```csharp
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.TourPrices;

/// Logic thuần trộn nguồn giá — tách khỏi retriever/DB để test không cần SQL.
public static class PriceMerge
{
    /// "Cả 2, ưu tiên thật": giữ hết dòng thật; lấp mẫu vào cặp (CityNorm, CategoryId) mà thật thiếu.
    public static List<PriceCandidate> PreferReal(IReadOnlyList<CatalogRow> real, IReadOnlyList<CatalogRow> sample)
    {
        var result = new List<PriceCandidate>(real.Count + sample.Count);
        foreach (var r in real) result.Add(new PriceCandidate(r, "real"));
        var realKeys = new HashSet<(string, int)>(real.Select(r => (r.CityNorm ?? "", r.CategoryId)));
        foreach (var s in sample)
            if (!realKeys.Contains((s.CityNorm ?? "", s.CategoryId)))
                result.Add(new PriceCandidate(s, "sample"));
        return result;
    }
}
```
- [ ] **Step 4: Chạy → PASS** (3 test).
- [ ] **Step 5: Commit**
```bash
git add Services/TourPrices/PriceMerge.cs TourkitAiProxy.Tests/TourPrices/PriceMergeTests.cs
git commit -m "feat(tour-price): PriceMerge.PreferReal — trộn thật+mẫu theo (city,category)"
```

---

## Task 4: Repository.QueryAsync (lọc ứng viên)

**Files:** Modify `Services/TourPrices/TourPriceCatalogRepository.cs`

**Interfaces:**
- Consumes: `TourkitAiDb.OpenAsync` · `CatalogRow`
- Produces: `QueryAsync(string tenantId, PriceQuery q, int cap, CancellationToken ct) → Task<List<CatalogRow>>`

- [ ] **Step 1: Impact analysis** `gitnexus_impact({target:"TourPriceCatalogRepository", direction:"upstream", repo:"tourkit-ai-proxy"})` — chỉ THÊM method → rủi ro thấp. HIGH/CRITICAL → báo user.
- [ ] **Step 2: Thêm method (sau `CountAsync`)**
```csharp
    /// Lấy ứng viên giá theo bộ lọc (city/category/khoảng giá). IsActive=1. Cap số dòng.
    /// Dùng cho TourPriceRetriever — chỉ ĐỌC.
    public async Task<List<CatalogRow>> QueryAsync(string tenantId, PriceQuery q, int cap, CancellationToken ct)
    {
        if (cap <= 0 || cap > 500) cap = 60;
        var where = new List<string> { "TenantId = @tenantId", "IsActive = 1" };
        if (!string.IsNullOrWhiteSpace(q.CityNorm)) where.Add("CityNorm = @cityNorm");
        if (q.CategoryId is not null)              where.Add("CategoryId = @categoryId");
        if (q.MinPrice is not null)                where.Add("ContractPrice >= @minPrice");
        if (q.MaxPrice is not null)                where.Add("ContractPrice <= @maxPrice");
        var sql = $@"SELECT TOP (@cap)
            TenantId, PricingId, ProviderServiceId, ProviderId, ProviderName, ProviderCode,
            City, CityNorm, CategoryId, CategoryName, PriceName, Description,
            ContractPrice, PublicPrice, Stars
            FROM dbo.TourPriceCatalog WHERE {string.Join(" AND ", where)}
            ORDER BY ContractPrice";
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<CatalogRow>(sql, new {
            tenantId, cap, q.CityNorm, q.CategoryId, q.MinPrice, q.MaxPrice });
        return rows.ToList();
    }
```
- [ ] **Step 3: Build** → 0 error. (Dapper map CatalogRow qua constructor — tên cột khớp param record.)
- [ ] **Step 4: Commit**
```bash
git add Services/TourPrices/TourPriceCatalogRepository.cs
git commit -m "feat(tour-price): repo QueryAsync lọc ứng viên giá cho retriever"
```

---

## Task 5: TourPriceRetriever (điều phối nguồn)

**Files:** Create `Services/TourPrices/TourPriceRetriever.cs`

**Interfaces:**
- Consumes: `TourPriceCatalogRepository.QueryAsync` · `PriceMerge.PreferReal` · `SampleCatalog.TenantId` · `PriceSource`/`PriceQuery`/`PriceCandidate`
- Produces: `TourPriceRetriever.RetrieveAsync(string tenantId, PriceQuery q, PriceSource source, CancellationToken ct) → Task<List<PriceCandidate>>`

- [ ] **Step 1: Impl**
```csharp
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.TourPrices;

/// Lấy ứng viên giá theo NGUỒN người dùng chọn (mẫu / thật / cả 2 ưu tiên thật).
public class TourPriceRetriever
{
    private const int Cap = 60;
    private readonly TourPriceCatalogRepository _repo;
    private readonly ILogger<TourPriceRetriever> _log;
    public TourPriceRetriever(TourPriceCatalogRepository repo, ILogger<TourPriceRetriever> log)
    { _repo = repo; _log = log; }

    public async Task<List<PriceCandidate>> RetrieveAsync(string tenantId, PriceQuery q, PriceSource source, CancellationToken ct)
    {
        switch (source)
        {
            case PriceSource.Sample:
                return Tag(await _repo.QueryAsync(SampleCatalog.TenantId, q, Cap, ct), "sample");
            case PriceSource.Real:
                return Tag(await _repo.QueryAsync(tenantId, q, Cap, ct), "real");
            default: // Both — ưu tiên thật, lấp mẫu
                var real   = await _repo.QueryAsync(tenantId, q, Cap, ct);
                var sample = await _repo.QueryAsync(SampleCatalog.TenantId, q, Cap, ct);
                var merged = PriceMerge.PreferReal(real, sample);
                _log.LogDebug("[price-retriever] tenant={T} both: thật={R} mẫu-lấp={S}", tenantId, real.Count, merged.Count - real.Count);
                return merged;
        }
    }

    private static List<PriceCandidate> Tag(List<CatalogRow> rows, string src)
        => rows.Select(r => new PriceCandidate(r, src)).ToList();
}
```
- [ ] **Step 2: Build** → 0 error.
- [ ] **Step 3: Commit**
```bash
git add Services/TourPrices/TourPriceRetriever.cs
git commit -m "feat(tour-price): TourPriceRetriever chọn nguồn (mẫu/thật/cả 2)"
```

---

## Task 6: SampleCatalogSeeder + seed stub + DI

**Files:** Create `Services/TourPrices/SampleCatalogSeeder.cs` + `data/seed/tour-price-sample.json`; Modify `Services/Bootstrap/WorkflowStackRegistration.cs`; Test `TourkitAiProxy.Tests/TourPrices/SampleCatalogSeederTests.cs`

**Interfaces:**
- Consumes: `TourPriceCatalogRepository.UpsertBatchAsync`/`CountAsync` · `CatalogRow`
- Produces: `SampleCatalogSeeder.ParseSeed(string json) → List<CatalogRow>` (pure, test được) · `SampleCatalogSeeder.SeedIfEmptyAsync(CancellationToken) → Task<int>`

- [ ] **Step 1: Seed stub `data/seed/tour-price-sample.json`** (2 dòng ví dụ — Task 7 thay bằng full TopTour)
```json
[
  {"pricingId":900001,"providerServiceId":900001,"providerId":90001,"providerName":"Khách sạn 4* Mẫu Đà Nẵng","providerCode":null,"city":"Đà Nẵng","categoryId":1,"categoryName":"Khách sạn","priceName":"Phòng đôi","description":"Mùa thấp điểm (5,6,9)","contractPrice":1200000,"publicPrice":1500000},
  {"pricingId":900002,"providerServiceId":900002,"providerId":90002,"providerName":"Nhà hàng Mẫu Nha Trang","providerCode":null,"city":"Khánh Hòa","categoryId":3,"categoryName":"Nhà Hàng","priceName":"Set 6 người","description":null,"contractPrice":450000,"publicPrice":600000}
]
```
- [ ] **Step 2: Test parse trước**
```csharp
using TourkitAiProxy.Services.TourPrices;
using Xunit;
namespace TourkitAiProxy.Tests.TourPrices;
public class SampleCatalogSeederTests
{
    [Fact]
    public void ParseSeed_map_dung_va_ep_tenant_sample()
    {
        var json = "[{\"pricingId\":1,\"providerServiceId\":1,\"providerId\":1,\"providerName\":\"A\",\"city\":\"Đà Nẵng\",\"categoryId\":1,\"categoryName\":\"Khách sạn\",\"contractPrice\":1000000,\"publicPrice\":1200000}]";
        var rows = SampleCatalogSeeder.ParseSeed(json);
        Assert.Single(rows);
        Assert.Equal("__sample__", rows[0].TenantId);       // ép về sample
        Assert.Equal("da nang", rows[0].CityNorm);          // norm từ City
        Assert.Equal(1000000m, rows[0].ContractPrice);
    }
    [Fact] public void ParseSeed_rong_tra_list_rong() => Assert.Empty(SampleCatalogSeeder.ParseSeed("[]"));
}
```
- [ ] **Step 3: Chạy → FAIL**.
- [ ] **Step 4: Impl `SampleCatalogSeeder.cs`**
```csharp
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TextUtil;

namespace TourkitAiProxy.Services.TourPrices;

/// Nạp NCC mẫu từ file seed vào rows __sample__ (idempotent: chỉ nạp khi bảng chưa có dòng mẫu nào).
/// Seed dựng 1 lần từ TopTour local (Task 7). ParseSeed thuần để test không cần DB.
public class SampleCatalogSeeder
{
    private readonly TourPriceCatalogRepository _repo;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SampleCatalogSeeder> _log;
    public SampleCatalogSeeder(TourPriceCatalogRepository repo, IWebHostEnvironment env, ILogger<SampleCatalogSeeder> log)
    { _repo = repo; _env = env; _log = log; }

    private sealed class SeedItem
    {
        public int PricingId { get; set; } public int ProviderServiceId { get; set; } public int ProviderId { get; set; }
        public string? ProviderName { get; set; } public string? ProviderCode { get; set; } public string? City { get; set; }
        public int CategoryId { get; set; } public string? CategoryName { get; set; }
        public string? PriceName { get; set; } public string? Description { get; set; }
        public decimal ContractPrice { get; set; } public decimal PublicPrice { get; set; }
    }

    /// Đọc JSON → CatalogRow (TenantId ép "__sample__", CityNorm norm, Stars bóc từ tên). Lỗi field → bỏ dòng.
    public static List<CatalogRow> ParseSeed(string json)
    {
        var items = JsonSerializer.Deserialize<List<SeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var rows = new List<CatalogRow>(items.Count);
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.ProviderName)) continue;
            rows.Add(new CatalogRow(
                TenantId: SampleCatalog.TenantId, PricingId: it.PricingId,
                ProviderServiceId: it.ProviderServiceId, ProviderId: it.ProviderId,
                ProviderName: it.ProviderName!, ProviderCode: it.ProviderCode,
                City: it.City, CityNorm: VietnameseText.Norm(it.City),
                CategoryId: it.CategoryId, CategoryName: it.CategoryName,
                PriceName: it.PriceName, Description: it.Description,
                ContractPrice: it.ContractPrice, PublicPrice: it.PublicPrice,
                Stars: PriceCatalogRules.ParseStars(it.ProviderName)));
        }
        return rows;
    }

    /// Nạp nếu chưa có dòng mẫu nào. Trả số dòng đã nạp (0 nếu đã có / seed thiếu).
    public async Task<int> SeedIfEmptyAsync(CancellationToken ct)
    {
        if (await _repo.CountAsync(SampleCatalog.TenantId, ct) > 0) { _log.LogDebug("[sample-seed] đã có mẫu — bỏ qua"); return 0; }
        var path = Path.Combine(_env.ContentRootPath, "data", "seed", "tour-price-sample.json");
        if (!File.Exists(path)) { _log.LogWarning("[sample-seed] thiếu file seed {Path}", path); return 0; }
        var rows = ParseSeed(await File.ReadAllTextAsync(path, ct));
        var n = await _repo.UpsertBatchAsync(rows, ct);
        _log.LogInformation("[sample-seed] nạp {N} dòng NCC mẫu vào __sample__", n);
        return n;
    }
}
```
- [ ] **Step 5: Chạy test → PASS** (2 test).
- [ ] **Step 6: DI + gọi lúc startup**
Trong `WorkflowStackRegistration.AddWorkflowStack` (cạnh `TourPriceCatalogRepository`):
```csharp
        s.AddSingleton<TourPrices.TourPriceRetriever>();
        s.AddSingleton<TourPrices.SampleCatalogSeeder>();
```
Trong `Program.cs`, sau khi `TourkitAiDb.InitAsync` chạy (chỗ schema init), gọi seed (non-blocking, nuốt lỗi):
```csharp
// Nạp NCC mẫu (idempotent) — không chặn startup nếu lỗi.
try { await app.Services.GetRequiredService<TourkitAiProxy.Services.TourPrices.SampleCatalogSeeder>().SeedIfEmptyAsync(CancellationToken.None); }
catch (Exception ex) { app.Logger.LogWarning(ex, "[sample-seed] nạp mẫu lỗi (bỏ qua)"); }
```
- [ ] **Step 7: Build + chạy app** → log `[sample-seed] nạp 2 dòng...` lần đầu, lần 2 bỏ qua. Test toàn bộ pass.
- [ ] **Step 8: Commit**
```bash
git add Services/TourPrices/SampleCatalogSeeder.cs data/seed/tour-price-sample.json Services/Bootstrap/WorkflowStackRegistration.cs Program.cs TourkitAiProxy.Tests/TourPrices/SampleCatalogSeederTests.cs
git commit -m "feat(tour-price): SampleCatalogSeeder nạp NCC mẫu từ seed (idempotent)"
```

---

## Task 7: Trích seed đầy đủ từ TopTour local (CHẶN — cần tên DB)

**Điều kiện:** user cung cấp **tên DB TopTour trong SQL instance local**.

- [ ] Viết script trích 1 lần: JOIN 4 bảng (như `GetProviderPricesAsync`) trên DB TopTour → áp `PriceCatalogRules.IsExcluded` + `ParseStars` → ghi đè `data/seed/tour-price-sample.json` (full).
- [ ] Đo số dòng sau lọc, cân nhắc nén nếu file lớn.
- [ ] Xoá rows `__sample__` cũ + chạy lại seeder để nạp full.
- [ ] Commit seed full.

---

## Task 8: Tích hợp wizard phân tích giá (NỐI TIẾP — cần khảo sát flow)

**Điều kiện:** khảo sát `Services/Tour/TourBuilderService.cs` + `Endpoints/TourBuilderEndpoints.cs` + `wwwroot/pages/wizard.jsx` để biết flow AI dựng giá hiện tại.

- [ ] Thêm dropdown "Nguồn giá" (mẫu/thật/cả 2, mặc định Cả 2) trong wizard.
- [ ] Truyền `source` xuống TourBuilder → gọi `TourPriceRetriever.RetrieveAsync` → đưa ứng viên (kèm nhãn nguồn) cho AI thay vì để AI bịa giá.
- [ ] Badge nguồn (thật/mẫu) trên UI kết quả.
- [ ] (Kế thừa spec §8.2 mảng 2: DestinationMap điểm đến→tỉnh để fill `PriceQuery.CityNorm`.)

---

## Sau plan này
Task 7 + 8 là phần còn lại (cần input/khảo sát). Core backend (Task 1–6) đủ để test retriever + seeder độc lập.
