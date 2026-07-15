# Tour Price Catalog — Implementation Plan (Mảng 1: Catalog + Sync)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đồng bộ bảng giá nhà cung cấp thật từ TourKit về `dbo.TourPriceCatalog` (per-tenant), để mảng 2 (retriever + wizard) có dữ liệu thật mà dựng giá thay vì để AI bịa.

**Architecture:** Endpoint phân trang mới bên `toutkit-app` (`GET /api/ai/provider-prices`) JOIN sẵn 4 bảng → `TourKitNccClient` gọi qua service account → `TourPriceCatalogSyncWorkflow` (`IScheduledWorkflow`, PerTenant, 1 lần/ngày) upsert vào `dbo.TourPriceCatalog` trong `TourKit_Push`. Catalog chỉ đọc, không ghi ngược TourKit.

**Tech Stack:** .NET 8 · Dapper (KHÔNG EF Core) · SQL Server · xUnit · ASP.NET Core Minimal API

**Spec:** [docs/superpowers/specs/2026-07-15-tour-price-catalog-design.md](../specs/2026-07-15-tour-price-catalog-design.md)

## Global Constraints

- **DateTime = UTC, luôn kèm `Z`** — lưu bằng `DateTime.UtcNow` / `SYSUTCDATETIME()`. KHÔNG `DateTime.Now`/`GETDATE()`. Xem [docs/datetime-convention.md](../../datetime-convention.md).
- **Tiếng Việt** cho mọi comment, log, chuỗi hiển thị, commit message.
- **Dapper, không EF Core.** Schema là SQL thuần trong `TourkitAiDb.SchemaSql`, idempotent `IF OBJECT_ID(...) IS NULL`.
- **Per-tenant scoping**: mọi bảng mới có `TenantId` là cột đầu của PK. Xem [docs/database-schema.md](../../database-schema.md) — **thêm bảng mới PHẢI update file đó**.
- **AI call nền PHẢI `AiCallContext.Push(...)`** — thiếu là bypass quota tenant + log `feature=unknown`. (Plan này chưa gọi AI, nhưng mảng 2 sẽ.)
- **Target framework pinned**: proxy `net8.0`. KHÔNG đổi TFM, KHÔNG thêm package version mới ngoài các package đã có.
- **KHÔNG copy-paste helper** — dùng lại (CLAUDE.md § Conventions).
- Test chạy: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
- **GitNexus**: chạy `mcp__gitnexus__impact` trước khi sửa symbol có sẵn (Task 5 sửa `TourKitNccClient`).

## Phụ thuộc ngoài (BLOCKER — đọc trước khi bắt đầu)

Task 4 nằm ở **repo khác** (`D:\MiGroup\tourkitapp\toutkit-app`) và **phải được deploy lên `api.travelai.vn`** thì Task 6 mới chạy thật được. Task 1–3, 5 không bị chặn. Nếu chưa deploy được, vẫn làm hết Task 1–6 rồi nghiệm thu Task 7 sau.

---

## File Structure

| File | Trách nhiệm |
|---|---|
| `Services/Text/VietnameseText.cs` | **Tạo** — 1 nguồn chuẩn hóa tiếng Việt (bỏ dấu, thường hóa). Repo đang có **4 bản sao** (`ActionResolver.Norm`, `ActionResolutionMemory.Norm`, `AgentCacheKeys.Normalize`, `JsonPlannerAgent.Norm`) — plan này KHÔNG đụng chúng (ngoài phạm vi), chỉ tránh tạo bản thứ 5. Migrate 4 chỗ cũ = việc riêng sau. |
| `Models/TourPriceModels.cs` | **Tạo** — `CatalogRow` (1 dòng giá), `ProviderPriceItem` (DTO upstream). |
| `Services/TourPrices/PriceCatalogRules.cs` | **Tạo** — logic thuần: bóc `Stars`, luật loại trừ. Tách riêng để test không cần DB. |
| `Services/TourPrices/TourPriceCatalogRepository.cs` | **Tạo** — Dapper: upsert theo mẻ, deactivate, đếm. |
| `Services/TourPrices/TourPriceCatalogSyncWorkflow.cs` | **Tạo** — `IScheduledWorkflow`, PerTenant. |
| `Services/Db/TourkitAiDb.cs` | **Sửa** — thêm `dbo.TourPriceCatalog` vào `SchemaSql` + tên bảng vào log. |
| `Services/TourKit/TourKitNccClient.cs` | **Sửa** — thêm `ProviderPricesAsync` (phân trang). |
| `Services/Bootstrap/WorkflowStackRegistration.cs` | **Sửa** — đăng ký repository + workflow. |
| `docs/database-schema.md` | **Sửa** — bảng thứ 15 (STRICT: CLAUDE.md yêu cầu). |
| `TourkitAiProxy.Tests/TourPrices/PriceCatalogRulesTests.cs` | **Tạo** — test logic thuần. |
| `TourkitAiProxy.Tests/TourPrices/VietnameseTextTests.cs` | **Tạo** — test chuẩn hóa. |
| **`toutkit-app`** `TourKit.Api/Controllers/AiController.cs` | **Sửa** — `GET /api/ai/provider-prices`. |
| **`toutkit-app`** `TourKit.Services/TourService.cs` | **Sửa** — SQL JOIN 4 bảng, phân trang. |
| **`toutkit-app`** `TourKit.Shared/DTOs/ProviderDtos.cs` | **Sửa** — `ProviderPriceRowItem`. |

---

## Task 1: Chuẩn hóa tiếng Việt (1 nguồn)

**Files:**
- Create: `Services/Text/VietnameseText.cs`
- Test: `TourkitAiProxy.Tests/TourPrices/VietnameseTextTests.cs`

**Interfaces:**
- Consumes: (không)
- Produces: `TourkitAiProxy.Services.Text.VietnameseText.Norm(string? s) → string` — trả chuỗi thường, bỏ dấu, `đ`→`d`, gộp khoảng trắng. Task 3 và Task 6 dùng.

- [ ] **Step 1: Viết test cho trước**

```csharp
// TourkitAiProxy.Tests/TourPrices/VietnameseTextTests.cs
using TourkitAiProxy.Services.Text;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class VietnameseTextTests
{
    [Theory]
    [InlineData("Đà Nẵng", "da nang")]
    [InlineData("Khánh Hòa", "khanh hoa")]
    [InlineData("Thừa Thiên - Huế", "thua thien - hue")]
    [InlineData("Bà Rịa - Vũng Tàu", "ba ria - vung tau")]
    [InlineData("Hà  Nội", "ha noi")]          // 2 khoảng trắng (có thật trong DB demo2)
    [InlineData("  NHA TRANG  ", "nha trang")]  // hoa + thừa khoảng trắng (có thật)
    public void Norm_bo_dau_va_thuong_hoa(string input, string expected)
        => Assert.Equal(expected, VietnameseText.Norm(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Norm_rong_tra_chuoi_rong(string? input)
        => Assert.Equal("", VietnameseText.Norm(input));
}
```

- [ ] **Step 2: Chạy test để chắc nó FAIL**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter VietnameseTextTests`
Expected: FAIL — build error `The type or namespace name 'Text' does not exist in namespace 'TourkitAiProxy.Services'`

- [ ] **Step 3: Viết implementation tối thiểu**

```csharp
// Services/Text/VietnameseText.cs
using System.Globalization;
using System.Text;

namespace TourkitAiProxy.Services.Text;

/// <summary>
/// Chuẩn hóa tiếng Việt để so khớp: thường hóa + bỏ dấu + đ→d + gộp khoảng trắng.
///
/// 1 NGUỒN cho code mới. Repo đang có 4 bản sao cũ (ActionResolver.Norm,
/// ActionResolutionMemory.Norm, AgentCacheKeys.Normalize, JsonPlannerAgent.Norm) —
/// chưa migrate (ngoài phạm vi); đừng tạo thêm bản thứ 6.
/// </summary>
public static class VietnameseText
{
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.Trim().ToLowerInvariant();
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            // Bỏ dấu thanh/dấu phụ (combining marks).
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            // đ/Đ không phân rã được bằng FormD → map tay.
            sb.Append(ch == 'đ' ? 'd' : ch);
        }
        // Gộp khoảng trắng thừa ("Hà  Nội" → "ha noi").
        return string.Join(' ', sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
```

- [ ] **Step 4: Chạy test để chắc nó PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter VietnameseTextTests`
Expected: PASS — 9 passed

- [ ] **Step 5: Commit**

```bash
git add Services/Text/VietnameseText.cs TourkitAiProxy.Tests/TourPrices/VietnameseTextTests.cs
git commit -m "feat(text): 1 nguồn chuẩn hóa tiếng Việt cho code mới

Repo đang có 4 bản sao hàm bỏ dấu. Tách 1 nguồn để code mới (tour price
catalog) khỏi thành bản thứ 5. Chưa migrate 4 chỗ cũ — việc riêng."
```

---

## Task 2: Luật catalog (bóc hạng sao + loại trừ)

**Files:**
- Create: `Models/TourPriceModels.cs`
- Create: `Services/TourPrices/PriceCatalogRules.cs`
- Test: `TourkitAiProxy.Tests/TourPrices/PriceCatalogRulesTests.cs`

**Interfaces:**
- Consumes: `VietnameseText.Norm` (Task 1)
- Produces:
  - `TourkitAiProxy.Models.ProviderPriceItem` — record DTO đọc từ upstream.
  - `TourkitAiProxy.Models.CatalogRow` — record 1 dòng catalog (dùng bởi Task 3, và mảng 2).
  - `PriceCatalogRules.ParseStars(string? providerName) → int?`
  - `PriceCatalogRules.IsExcluded(string? categoryName, decimal contractPrice, IReadOnlyList<string> blockedCategories) → bool`
  - `PriceCatalogRules.DefaultBlockedCategories → IReadOnlyList<string>`
  - `PriceCatalogRules.MinPrice = 50_000m`

Dữ liệu thật (spec §2.2): bóc sao chỉ đúng **59%** → `Stars` nullable, KHÔNG BAO GIỜ dùng làm lọc cứng. Giá rác 25/9.460 dòng.

- [ ] **Step 1: Viết test cho trước**

```csharp
// TourkitAiProxy.Tests/TourPrices/PriceCatalogRulesTests.cs
using TourkitAiProxy.Services.TourPrices;
using Xunit;

namespace TourkitAiProxy.Tests.TourPrices;

public class PriceCatalogRulesTests
{
    // Tên NCC thật lấy từ TopTour (đo 2026-07-15).
    [Theory]
    [InlineData("Khu Nghỉ Dưỡng 5* Alibu Resort Nha Trang", 5)]
    [InlineData("Khách sạn 4* Mường Thanh Grand Cửa Lò", 4)]
    [InlineData("Khách Sạn 3* Minh Chiến Đà Lạt", 3)]
    [InlineData("Romana Resort & Spa 4* ", 4)]
    [InlineData("P & T Hotel Vũng Tàu - 3 sao", 3)]
    [InlineData("KHÁCH SẠN 5* ROYAL HA LONG HOTEL", 5)]
    public void ParseStars_boc_duoc_hang_sao(string ten, int expected)
        => Assert.Equal(expected, PriceCatalogRules.ParseStars(ten));

    // 41% KHÔNG bóc được — phải trả null, KHÔNG được đoán bừa.
    [Theory]
    [InlineData("Golden Lotus Luxury - Đà Nẵng")]
    [InlineData("NOVELA MUINE RESORT")]
    [InlineData("Affa Boutique Hotel")]
    [InlineData("Swandor Resort Cam Ranh")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseStars_khong_ro_tra_null(string? ten)
        => Assert.Null(PriceCatalogRules.ParseStars(ten));

    // "Hanasa Pu Luong Resort - 2025" — số 2025 KHÔNG phải hạng sao.
    [Fact]
    public void ParseStars_khong_nham_nam_thanh_hang_sao()
    {
        Assert.Null(PriceCatalogRules.ParseStars("Hanasa Pu Luong Resort - 2025"));
        Assert.Null(PriceCatalogRules.ParseStars("HALIOS HẠ LONG HOTEL - 2025"));
    }

    [Fact]
    public void IsExcluded_chan_ve_may_bay_vi_chua_ten_hanh_khach()
    {
        var blocked = PriceCatalogRules.DefaultBlockedCategories;
        Assert.True(PriceCatalogRules.IsExcluded("Vé máy bay HHK", 4_452_000m, blocked));
        Assert.True(PriceCatalogRules.IsExcluded("Vé máy bay", 1_000_000m, blocked));
        Assert.True(PriceCatalogRules.IsExcluded("VÉ MÁY BAY", 1_000_000m, blocked));   // hoa
        Assert.True(PriceCatalogRules.IsExcluded("Ve may bay", 1_000_000m, blocked));   // không dấu
    }

    [Fact]
    public void IsExcluded_giu_lai_loai_DV_hop_le()
    {
        var blocked = PriceCatalogRules.DefaultBlockedCategories;
        Assert.False(PriceCatalogRules.IsExcluded("Khách sạn", 1_650_000m, blocked));
        Assert.False(PriceCatalogRules.IsExcluded("LandTour", 38_990_000m, blocked));
        Assert.False(PriceCatalogRules.IsExcluded("Nhà Hàng", 450_000m, blocked));
    }

    // Rác nhập tay: 25/9.460 dòng khách sạn TopTour có giá < 50k (25đ, 330đ, 700đ).
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(330)]
    [InlineData(700)]
    [InlineData(49_999)]
    public void IsExcluded_chan_gia_rac(decimal gia)
        => Assert.True(PriceCatalogRules.IsExcluded("Khách sạn", gia, PriceCatalogRules.DefaultBlockedCategories));

    [Fact]
    public void IsExcluded_giu_gia_hop_le_tu_50k()
        => Assert.False(PriceCatalogRules.IsExcluded("Khách sạn", 50_000m, PriceCatalogRules.DefaultBlockedCategories));
}
```

- [ ] **Step 2: Chạy test để chắc nó FAIL**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter PriceCatalogRulesTests`
Expected: FAIL — build error `The type or namespace name 'TourPrices' does not exist`

- [ ] **Step 3: Viết model**

```csharp
// Models/TourPriceModels.cs
namespace TourkitAiProxy.Models;

// KHÔNG khai DTO cho item upstream: Task 7 đọc thẳng JsonElement (giống ChatAgentService /
// TourKitNccClient hiện tại) → thiếu field thì bỏ dòng, không ném. Thêm 1 record chỉ để
// deserialize rồi map lại là dư thừa.

/// 1 dòng trong `dbo.TourPriceCatalog`.
/// `Description` BẮT BUỘC giữ — chứa điều kiện áp giá viết tay ("Mùa thấp điểm (5,6,9)",
/// "T6-T7", "Lễ 2/9", "Trên 10 phòng"). Cột có cấu trúc `ngay_di` chỉ 9,3% được dùng,
/// nên đây là NGUỒN DUY NHẤT về mùa vụ. Xem spec §2.4.
public record CatalogRow(
    string TenantId,
    int PricingId,
    int ProviderServiceId,
    int ProviderId,
    string ProviderName,
    string? ProviderCode,
    string? City,
    string CityNorm,
    int CategoryId,
    string? CategoryName,
    string? PriceName,
    string? Description,
    decimal ContractPrice,
    decimal PublicPrice,
    int? Stars
);
```

- [ ] **Step 4: Viết luật**

```csharp
// Services/TourPrices/PriceCatalogRules.cs
using System.Text.RegularExpressions;
using TourkitAiProxy.Services.Text;

namespace TourkitAiProxy.Services.TourPrices;

/// <summary>
/// Luật thuần cho catalog — tách khỏi repository/workflow để test không cần DB.
///
/// Số liệu thật (TopTour, đo 2026-07-15 — xem spec §2.2):
///   • Bóc hạng sao từ tên NCC chỉ đúng 346/588 = 59% → Stars NULLABLE, và
///     KHÔNG BAO GIỜ được dùng làm lọc cứng (sẽ âm thầm bỏ sót 41%).
///   • contract_price sạch 99,7% — chỉ 25/9.460 dòng < 50k (25đ, 330đ, 700đ).
/// </summary>
public static class PriceCatalogRules
{
    /// Ngưỡng rác nhập tay. Dưới ngưỡng này chắc chắn là gõ nhầm, không phải giá thật.
    public const decimal MinPrice = 50_000m;

    /// Loại DV chặn mặc định. "Vé máy bay" chứa TÊN HÀNH KHÁCH THẬT
    /// ("TRINH/XUAN PHONG MR (ADT)") → đồng bộ sang DB khác là bê PII đi không lý do;
    /// mà vé theo từng chuyến nên tái dùng cũng vô nghĩa. Xem spec §4.3.
    public static readonly IReadOnlyList<string> DefaultBlockedCategories = new[] { "ve may bay" };

    // "5*" / "4 sao" / "3*". KHÔNG khớp năm ("- 2025") vì bắt buộc chữ số 1-5 ĐƠN LẺ
    // (không có chữ số liền trước/sau) rồi mới tới '*' hoặc " sao".
    private static readonly Regex StarRx = new(
        @"(?<![0-9])([1-5])\s*(?:\*|sao\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Bóc hạng sao từ tên NCC. Không rõ → null (KHÔNG đoán).
    public static int? ParseStars(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        var m = StarRx.Match(providerName);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// Dòng có bị loại khỏi catalog không?
    public static bool IsExcluded(string? categoryName, decimal contractPrice,
        IReadOnlyList<string> blockedCategories)
    {
        if (contractPrice < MinPrice) return true;
        var cat = VietnameseText.Norm(categoryName);
        if (cat.Length == 0) return false;
        foreach (var b in blockedCategories)
            if (cat.Contains(VietnameseText.Norm(b), StringComparison.Ordinal)) return true;
        return false;
    }
}
```

- [ ] **Step 5: Chạy test để chắc nó PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter PriceCatalogRulesTests`
Expected: PASS — 24 passed

- [ ] **Step 6: Commit**

```bash
git add Models/TourPriceModels.cs Services/TourPrices/PriceCatalogRules.cs TourkitAiProxy.Tests/TourPrices/PriceCatalogRulesTests.cs
git commit -m "feat(tour-price): model + luật catalog (bóc sao, loại trừ)

Stars nullable vì bóc từ tên NCC chỉ đúng 59% (346/588 TopTour) — không bao
giờ làm lọc cứng. Chặn 'Vé máy bay' vì chứa tên hành khách thật (PII) và vé
theo từng chuyến nên tái dùng vô nghĩa. Chặn giá < 50k (rác nhập tay)."
```

---

## Task 3: Schema `dbo.TourPriceCatalog`

**Files:**
- Modify: `Services/Db/TourkitAiDb.cs` (thêm vào `SchemaSql`; sửa dòng log `_log.LogInformation("TourkitAiDb schema OK (...)")`)
- Modify: `docs/database-schema.md`

**Interfaces:**
- Consumes: (không)
- Produces: bảng `dbo.TourPriceCatalog` — Task 4 (repository) đọc/ghi.

- [ ] **Step 1: Thêm bảng vào `SchemaSql`**

Chèn NGAY TRƯỚC block `IF OBJECT_ID('dbo.VisaAssessments', 'U') IS NULL`:

```sql
-- Bảng giá NCC đồng bộ từ TourKit — nguồn để AI CHỌN dòng giá có thật thay vì bịa số.
-- Chỉ đọc (không ghi ngược TourKit). Đơn vị truy xuất là NHÀ CUNG CẤP, không phải dòng giá:
-- 1.013 dòng "Khách sạn/Khánh Hòa" thực chất là 57 khách sạn × ~18 loại phòng (spec §2.3).
IF OBJECT_ID('dbo.TourPriceCatalog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TourPriceCatalog (
        TenantId          NVARCHAR(128)   NOT NULL,
        PricingId         INT             NOT NULL,   -- provider_service_pricing.id (bên TourKit)
        ProviderServiceId INT             NOT NULL,
        ProviderId        INT             NOT NULL,
        ProviderName      NVARCHAR(512)   NOT NULL,   -- mang phần lớn ngữ nghĩa: hạng sao + địa danh
        ProviderCode      NVARCHAR(64)    NULL,
        City              NVARCHAR(128)   NULL,       -- tên TỈNH thô (sale nói điểm đến → cần DestinationMap ở mảng 2)
        CityNorm          NVARCHAR(128)   NULL,       -- bỏ dấu + thường hóa, để lọc
        CategoryId        INT             NOT NULL,
        CategoryName      NVARCHAR(256)   NULL,
        PriceName         NVARCHAR(512)   NULL,       -- chỉ 63,5% có nội dung → không đủ tin làm khóa
        Description       NVARCHAR(MAX)   NULL,       -- ĐIỀU KIỆN ÁP GIÁ viết tay ("Mùa thấp điểm (5,6,9)",
                                                      -- "T6-T7", "Lễ 2/9"). ngay_di chỉ 9,3% dùng → đây là
                                                      -- NGUỒN DUY NHẤT về mùa vụ. TUYỆT ĐỐI không bỏ cột này.
        ContractPrice     DECIMAL(18,2)   NOT NULL,   -- trục lọc CHÍNH (sạch 99,7%)
        PublicPrice       DECIMAL(18,2)   NOT NULL,
        Stars             INT             NULL,       -- bóc từ ProviderName, chỉ 59% → LỌC PHỤ, không lọc cứng
        IsActive          BIT             NOT NULL CONSTRAINT DF_TourPriceCatalog_IsActive DEFAULT 1,
        SyncedUtc         DATETIME2       NOT NULL,
        CONSTRAINT PK_TourPriceCatalog PRIMARY KEY CLUSTERED (TenantId, PricingId)
    );
    CREATE INDEX IX_TourPriceCatalog_Tenant_Cat_City
        ON dbo.TourPriceCatalog(TenantId, CategoryId, CityNorm) INCLUDE (ProviderId, ContractPrice);
    CREATE INDEX IX_TourPriceCatalog_Tenant_Cat_Price
        ON dbo.TourPriceCatalog(TenantId, CategoryId, ContractPrice);
END;
```

- [ ] **Step 2: Cập nhật dòng log tên bảng**

Trong `TourkitAiDb.InitAsync`, thêm `TourPriceCatalog` vào chuỗi log (sau `TourQuotes`):

```csharp
_log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/MailAccounts/Mails/MailSyncState/TourQuotes/TourPriceCatalog/VisaAssessments/QuotaOrders/WidgetTokens/VisaQuestionSets/TkSessions/TenantQuota/AiUsageCounters/AiUsageHistory/UserWorkflows/WorkflowRuns/OutboundMails/CrmActionQueue/MailTemplates/TenantServiceAccounts đã có/đã tạo)");
```

- [ ] **Step 3: Chạy app để schema tự tạo**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: build thành công, 0 error.

`InitAsync()` chạy lúc startup (`Program.cs:249`) và idempotent — bảng sẽ tự tạo lần chạy tới.

- [ ] **Step 4: Cập nhật `docs/database-schema.md`** (STRICT — CLAUDE.md bắt buộc)

Thêm 1 dòng vào bảng inventory, và sửa "Tổng cộng: **21 bảng**" → **22 bảng**:

```markdown
| `dbo.TourPriceCatalog` | Bảng giá NCC đồng bộ từ TourKit (per-tenant). Nguồn để AI chọn dòng giá THẬT thay vì bịa. Chỉ đọc, không ghi ngược. Cột `Description` chứa điều kiện áp giá viết tay — không được bỏ. |
```

- [ ] **Step 5: Commit**

```bash
git add Services/Db/TourkitAiDb.cs docs/database-schema.md
git commit -m "feat(tour-price): schema dbo.TourPriceCatalog

Bảng giá NCC per-tenant (PK TenantId+PricingId, theo convention dbo.Mails).
Description giữ nguyên vì là NGUỒN DUY NHẤT về mùa vụ — cột ngay_di có cấu
trúc chỉ 9,3% được dùng, điều kiện giá nằm trong chữ viết tay."
```

---

## Task 4: Endpoint upstream `GET /api/ai/provider-prices` (repo `toutkit-app`)

**Files (repo khác — `D:\MiGroup\tourkitapp\toutkit-app`):**
- Modify: `TourKit.Shared/DTOs/ProviderDtos.cs`
- Modify: `TourKit.Services/TourService.cs`
- Modify: `TourKit.Api/Controllers/AiController.cs`

**Interfaces:**
- Produces: `GET /api/ai/provider-prices?pageIndex=0&pageSize=500` → envelope `{success, data: {items[], total, pageIndex, pageSize}, message}`; mỗi item khớp `ProviderPriceItem` (Task 2).

**Vì sao cần:** đường hiện tại phải gọi `providers-by-service` cho từng loại DV rồi `providers/{id}/services` cho **từng NCC** → `DTour` = **5.163 lời gọi/lần sync**, × 280 tenant. Endpoint phân trang → **~30 lời gọi**. Nó cũng trả kèm `City` (mà `providers/{id}/services` hiện KHÔNG có → đang phải ghép 2 endpoint).

- [ ] **Step 1: Thêm DTO**

Trong `TourKit.Shared/DTOs/ProviderDtos.cs`:

```csharp
/// 1 dòng bảng giá NCC (JOIN providers + provider_services + provider_service_pricing + services).
/// Phục vụ AI proxy đồng bộ catalog — khác ProviderServiceItem (chỉ 6 field, không có City).
public class ProviderPriceRowItem
{
    public int PricingId { get; set; }
    public int ProviderServiceId { get; set; }
    public int ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderCode { get; set; }
    public string? City { get; set; }
    public int CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? PriceName { get; set; }
    public string? Description { get; set; }
    public decimal ContractPrice { get; set; }
    public decimal PublicPrice { get; set; }
}

public class ProviderPricePageResult
{
    public List<ProviderPriceRowItem> Items { get; set; } = new();
    public int Total { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
}
```

- [ ] **Step 2: Thêm method vào `TourService`**

Đặt cạnh `GetServicesByProviderAsync` (~`TourService.cs:3191`), theo đúng pattern inline SQL của nó:

```csharp
/// Bảng giá NCC phân trang — JOIN sẵn 4 bảng cho AI proxy đồng bộ catalog.
/// Khác GetServicesByProviderAsync: có City + CategoryName, và phân trang (không trả full).
public async Task<ProviderPricePageResult> GetProviderPricesAsync(int pageIndex, int pageSize)
{
    if (pageSize <= 0 || pageSize > 1000) pageSize = 500;
    if (pageIndex < 0) pageIndex = 0;

    const string sql = @"
SELECT COUNT(1)
FROM provider_service_pricing psp
JOIN provider_services ps ON ps.id = psp.provider_service_id
JOIN providers p ON p.id = ps.provider_id
JOIN services s ON s.id = ps.service_id
WHERE ps.status = 1 AND ISNULL(psp.status,0) <> 4 AND ISNULL(psp.type,0) = 0
  AND p.status NOT IN (4,5);

SELECT psp.id AS PricingId, ps.id AS ProviderServiceId, p.id AS ProviderId,
       p.provider_name AS ProviderName, p.provider_code AS ProviderCode, p.city AS City,
       s.id AS CategoryId, s.service_name AS CategoryName,
       psp.price_name AS PriceName, psp.description AS Description,
       ISNULL(psp.contract_price,0) AS ContractPrice, ISNULL(psp.public_price,0) AS PublicPrice
FROM provider_service_pricing psp
JOIN provider_services ps ON ps.id = psp.provider_service_id
JOIN providers p ON p.id = ps.provider_id
JOIN services s ON s.id = ps.service_id
WHERE ps.status = 1 AND ISNULL(psp.status,0) <> 4 AND ISNULL(psp.type,0) = 0
  AND p.status NOT IN (4,5)
ORDER BY psp.id
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;";

    var result = new ProviderPricePageResult { PageIndex = pageIndex, PageSize = pageSize };
    using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync();
    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@skip", pageIndex * pageSize);
    cmd.Parameters.AddWithValue("@take", pageSize);
    using var r = await cmd.ExecuteReaderAsync();
    if (await r.ReadAsync()) result.Total = r.GetInt32(0);
    await r.NextResultAsync();
    while (await r.ReadAsync())
    {
        result.Items.Add(new ProviderPriceRowItem
        {
            PricingId         = r.GetInt32(r.GetOrdinal("PricingId")),
            ProviderServiceId = r.GetInt32(r.GetOrdinal("ProviderServiceId")),
            ProviderId        = r.GetInt32(r.GetOrdinal("ProviderId")),
            ProviderName      = r["ProviderName"] as string,
            ProviderCode      = r["ProviderCode"] as string,
            City              = r["City"] as string,
            CategoryId        = r.GetInt32(r.GetOrdinal("CategoryId")),
            CategoryName      = r["CategoryName"] as string,
            PriceName         = r["PriceName"] as string,
            Description       = r["Description"] as string,
            ContractPrice     = r.GetDecimal(r.GetOrdinal("ContractPrice")),
            PublicPrice       = r.GetDecimal(r.GetOrdinal("PublicPrice")),
        });
    }
    return result;
}
```

**Lưu ý:** `_connectionString` là tên field per-tenant trong `TourService` — kiểm lại tên thật ở
`GetServicesByProviderAsync` (`TourService.cs:3191-3227`) và dùng ĐÚNG tên đó, đừng đoán.

- [ ] **Step 3: Thêm action vào `AiController`**

Đặt cạnh `GET /api/ai/providers` (~`AiController.cs:346`), theo đúng pattern trả envelope của các action quanh đó:

```csharp
/// Bảng giá NCC phân trang — cho AI proxy đồng bộ TourPriceCatalog.
[HttpGet("provider-prices")]
public async Task<IActionResult> GetProviderPrices([FromQuery] int pageIndex = 0, [FromQuery] int pageSize = 500)
{
    var data = await _tourService.GetProviderPricesAsync(pageIndex, pageSize);
    return Ok(MResponse<ProviderPricePageResult>.Success(data));
}
```

**Lưu ý:** tên helper envelope (`MResponse<T>.Success`) phải khớp các action lân cận — đọc
`AiController.cs:346-380` rồi copy đúng cách chúng trả về, đừng đoán.

- [ ] **Step 4: Build + thử tay**

Run: `dotnet build toutkit-app/Migroup.sln`
Expected: build thành công.

Chạy API local rồi gọi thử (cần JWT):
```bash
curl -H "Authorization: Bearer <jwt>" "http://localhost:<port>/api/ai/provider-prices?pageIndex=0&pageSize=5"
```
Expected: `{"success":true,"data":{"items":[...5 dòng có city + categoryName...],"total":<n>,...}}`

- [ ] **Step 5: Commit (repo toutkit-app)**

```bash
cd D:/MiGroup/tourkitapp/toutkit-app
git add TourKit.Shared/DTOs/ProviderDtos.cs TourKit.Services/TourService.cs TourKit.Api/Controllers/AiController.cs
git commit -m "feat(ai): GET /api/ai/provider-prices — bảng giá NCC phân trang

AI proxy cần đồng bộ bảng giá về catalog. Đường cũ phải gọi providers-by-service
cho từng loại DV rồi providers/{id}/services cho TỪNG NCC — tenant lớn (DTour
5.163 NCC) là 5.163 lời gọi mỗi lần sync, nhân 280 tenant. Endpoint phân trang
này còn ~30 lời gọi.

Cũng trả kèm City — ProviderServiceItem hiện KHÔNG có, nên proxy đang phải ghép
2 endpoint mới biết NCC ở tỉnh nào."
```

---

## Task 5: Client `TourKitNccClient.ProviderPricesAsync`

**Files:**
- Modify: `Services/TourKit/TourKitNccClient.cs`

**Interfaces:**
- Consumes: `GET /api/ai/provider-prices` (Task 4)
- Produces: `TourKitNccClient.ProviderPricesAsync(string sessionId, int pageIndex, int pageSize, CancellationToken ct) → Task<JsonElement>` — trả envelope `data` (đã unwrap `{success,data}`), tức object có `items[]` + `total`.

- [ ] **Step 1: Chạy impact analysis trước khi sửa (CLAUDE.md STRICT)**

Run: `mcp__gitnexus__impact({target: "TourKitNccClient", direction: "upstream", repo: "tourkit-ai-proxy"})`
Expected: liệt kê caller. Task này CHỈ THÊM method mới, không sửa method cũ → rủi ro thấp. Nếu báo HIGH/CRITICAL → dừng, báo user.

- [ ] **Step 2: Thêm method**

Thêm vào `TourKitNccClient` (sau `ProviderListAsync`), dùng lại `GetAsync` private sẵn có (nó đã tự re-login khi 401):

```csharp
/// Bảng giá NCC phân trang — nguồn cho TourPriceCatalogSyncWorkflow.
/// Trả envelope `data` = { items[], total, pageIndex, pageSize }.
public Task<JsonElement> ProviderPricesAsync(string sessionId, int pageIndex, int pageSize, CancellationToken ct)
    => GetAsync(sessionId, $"/api/ai/provider-prices?pageIndex={pageIndex}&pageSize={pageSize}", ct);
```

- [ ] **Step 3: Cập nhật XML doc của class**

Thêm 1 dòng vào block `///` đầu class (liệt kê endpoint):

```csharp
///   • ProviderPricesAsync    — `/api/ai/provider-prices?pageIndex=&pageSize=`  (bảng giá phân trang, có City — cho catalog sync)
```

- [ ] **Step 4: Build**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: build thành công, 0 error.

- [ ] **Step 5: Commit**

```bash
git add Services/TourKit/TourKitNccClient.cs
git commit -m "feat(tour-price): client đọc bảng giá NCC phân trang

Chỉ thêm method, không đụng method cũ. Dùng lại GetAsync sẵn có nên thừa
hưởng luôn cơ chế tự re-login khi JWT hết hạn."
```

---

## Task 6: Repository `TourPriceCatalogRepository`

**Files:**
- Create: `Services/TourPrices/TourPriceCatalogRepository.cs`

**Interfaces:**
- Consumes: `TourkitAiDb.OpenAsync` · `CatalogRow` (Task 2) · bảng ở Task 3
- Produces:
  - `UpsertBatchAsync(IReadOnlyList<CatalogRow> rows, CancellationToken ct) → Task<int>`
  - `DeactivateMissingAsync(string tenantId, DateTime syncedFromUtc, CancellationToken ct) → Task<int>`
  - `CountAsync(string tenantId, CancellationToken ct) → Task<int>`

- [ ] **Step 1: Viết repository**

```csharp
// Services/TourPrices/TourPriceCatalogRepository.cs
using Dapper;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.TourPrices;

/// <summary>
/// Dapper CRUD cho `dbo.TourPriceCatalog` — 1 nguồn persistence của catalog.
/// Chỉ đọc từ TourKit, không bao giờ ghi ngược.
/// </summary>
public class TourPriceCatalogRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TourPriceCatalogRepository> _log;

    public TourPriceCatalogRepository(TourkitAiDb db, ILogger<TourPriceCatalogRepository> log)
    {
        _db = db; _log = log;
    }

    /// Upsert 1 mẻ. MERGE theo PK (TenantId, PricingId). Trả số dòng đã ghi.
    public async Task<int> UpsertBatchAsync(IReadOnlyList<CatalogRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const string sql = @"
MERGE dbo.TourPriceCatalog AS t
USING (SELECT @TenantId AS TenantId, @PricingId AS PricingId) AS s
    ON t.TenantId = s.TenantId AND t.PricingId = s.PricingId
WHEN MATCHED THEN UPDATE SET
    ProviderServiceId = @ProviderServiceId, ProviderId = @ProviderId,
    ProviderName = @ProviderName, ProviderCode = @ProviderCode,
    City = @City, CityNorm = @CityNorm,
    CategoryId = @CategoryId, CategoryName = @CategoryName,
    PriceName = @PriceName, Description = @Description,
    ContractPrice = @ContractPrice, PublicPrice = @PublicPrice,
    Stars = @Stars, IsActive = 1, SyncedUtc = @SyncedUtc
WHEN NOT MATCHED THEN INSERT
    (TenantId, PricingId, ProviderServiceId, ProviderId, ProviderName, ProviderCode,
     City, CityNorm, CategoryId, CategoryName, PriceName, Description,
     ContractPrice, PublicPrice, Stars, IsActive, SyncedUtc)
    VALUES (@TenantId, @PricingId, @ProviderServiceId, @ProviderId, @ProviderName, @ProviderCode,
     @City, @CityNorm, @CategoryId, @CategoryName, @PriceName, @Description,
     @ContractPrice, @PublicPrice, @Stars, 1, @SyncedUtc);";

        var now = DateTime.UtcNow;   // UTC — STRICT (docs/datetime-convention.md)
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, rows.Select(r => new
        {
            r.TenantId, r.PricingId, r.ProviderServiceId, r.ProviderId,
            r.ProviderName, r.ProviderCode, r.City, r.CityNorm,
            r.CategoryId, r.CategoryName, r.PriceName, r.Description,
            r.ContractPrice, r.PublicPrice, r.Stars,
            SyncedUtc = now
        }));
    }

    /// Tắt cờ các dòng KHÔNG được chạm trong lần sync này (NCC đã xóa/ngừng bên TourKit).
    /// KHÔNG xóa dòng — giữ lịch sử, và báo giá cũ còn tham chiếu PricingId.
    public async Task<int> DeactivateMissingAsync(string tenantId, DateTime syncedFromUtc, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.TourPriceCatalog SET IsActive = 0
WHERE TenantId = @tenantId AND IsActive = 1 AND SyncedUtc < @from;";
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new { tenantId, from = syncedFromUtc });
    }

    /// Đếm dòng đang hiệu lực (nghiệm thu + log).
    public async Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM dbo.TourPriceCatalog WHERE TenantId = @tenantId AND IsActive = 1",
            new { tenantId });
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: build thành công, 0 error.

- [ ] **Step 3: Commit**

```bash
git add Services/TourPrices/TourPriceCatalogRepository.cs
git commit -m "feat(tour-price): repository catalog (Dapper MERGE)

DeactivateMissing tắt cờ thay vì xóa — giữ lịch sử, và báo giá đã lưu còn
tham chiếu PricingId."
```

---

## Task 7: Workflow `tour-price-catalog-sync`

**Files:**
- Create: `Services/TourPrices/TourPriceCatalogSyncWorkflow.cs`
- Modify: `Services/Bootstrap/WorkflowStackRegistration.cs`

**Interfaces:**
- Consumes: `TourKitNccClient.ProviderPricesAsync` (Task 5) · `TourPriceCatalogRepository` (Task 6) · `PriceCatalogRules` (Task 2) · `VietnameseText.Norm` (Task 1) · `TkSessionStore.GetOrCreateServiceSessionAsync`
- Produces: `IScheduledWorkflow` với `Type = "tour-price-catalog-sync"` — scheduler + `/api/v1/workflows` tự pickup.

- [ ] **Step 1: Viết workflow**

```csharp
// Services/TourPrices/TourPriceCatalogSyncWorkflow.cs
using System.Diagnostics;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Text;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflows;

namespace TourkitAiProxy.Services.TourPrices;

/// Option per-tenant (OptionsJson).
public record TourPriceSyncOptions(int PageSize = 500, string[]? BlockedCategories = null);

/// <summary>
/// Đồng bộ bảng giá NCC từ TourKit về `dbo.TourPriceCatalog`. PerTenant, mặc định 1 lần/ngày.
///
/// Auth = service account (`dbo.TenantServiceAccounts`) → tự login, KHÔNG cần user online.
/// Tenant chưa cấu hình service account → workflow báo lỗi rõ, không crash.
///
/// Loại trừ (spec §4.3): "Vé máy bay" (chứa tên hành khách thật) + giá < 50k (rác nhập tay).
/// </summary>
public class TourPriceCatalogSyncWorkflow : IScheduledWorkflow
{
    private readonly TourKitNccClient _ncc;
    private readonly TkSessionStore _sessions;
    private readonly TourPriceCatalogRepository _repo;
    private readonly ILogger<TourPriceCatalogSyncWorkflow> _log;

    public string Type => "tour-price-catalog-sync";
    public string Label => "Đồng bộ bảng giá nhà cung cấp";
    public string Description => "Kéo bảng giá NCC từ TourKit về để AI dựng giá tour bằng số thật thay vì ước lượng.";
    public WorkflowScope Scope => WorkflowScope.PerTenant;

    public TourPriceCatalogSyncWorkflow(TourKitNccClient ncc, TkSessionStore sessions,
        TourPriceCatalogRepository repo, ILogger<TourPriceCatalogSyncWorkflow> log)
    {
        _ncc = ncc; _sessions = sessions; _repo = repo; _log = log;
    }

    public async Task<WorkflowRunResult> RunAsync(string tenantId, string username, string? optionsJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var opt = ParseOptions(optionsJson);
        var blocked = opt.BlockedCategories?.Length > 0
            ? opt.BlockedCategories
            : PriceCatalogRules.DefaultBlockedCategories;

        _log.LogInformation("[tour-price-sync] START tenant={T} pageSize={P} blocked=[{B}]",
            tenantId, opt.PageSize, string.Join(",", blocked));

        string sessionId;
        try
        {
            var s = await _sessions.GetOrCreateServiceSessionAsync(tenantId, ct);
            sessionId = s.Id;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[tour-price-sync] tenant={T} chưa cấu hình tài khoản tự động: {Msg}", tenantId, ex.Message);
            return new WorkflowRunResult(false, null,
                "Chưa cấu hình tài khoản tự động cho tenant này — vào Tự động hóa để thêm.");
        }

        var syncStartUtc = DateTime.UtcNow;
        int page = 0, total = -1, fetched = 0, saved = 0, skipped = 0;

        while (!ct.IsCancellationRequested)
        {
            var data = await _ncc.ProviderPricesAsync(sessionId, page, opt.PageSize, ct);
            if (total < 0 && data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                total = t.GetInt32();
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            var n = items.GetArrayLength();
            if (n == 0) break;
            fetched += n;

            var rows = new List<CatalogRow>(n);
            foreach (var it in items.EnumerateArray())
            {
                var row = MapRow(tenantId, it);
                if (row is null) { skipped++; continue; }
                if (PriceCatalogRules.IsExcluded(row.CategoryName, row.ContractPrice, blocked)) { skipped++; continue; }
                rows.Add(row);
            }
            saved += await _repo.UpsertBatchAsync(rows, ct);

            _log.LogDebug("[tour-price-sync] tenant={T} trang {P}: lấy {N}, lưu {S}, bỏ {K}", tenantId, page, n, rows.Count, n - rows.Count);
            page++;
            if (total >= 0 && fetched >= total) break;
            if (page > 500) { _log.LogWarning("[tour-price-sync] tenant={T} vượt 500 trang — dừng phòng lặp vô tận", tenantId); break; }
        }

        var deactivated = await _repo.DeactivateMissingAsync(tenantId, syncStartUtc, ct);
        var active = await _repo.CountAsync(tenantId, ct);
        sw.Stop();

        var summary = $"Lấy {fetched} dòng, lưu {saved}, bỏ {skipped}, tắt {deactivated}. Đang hiệu lực: {active}.";
        _log.LogInformation("[tour-price-sync] FINISH tenant={T} ({Ms}ms) {Sum}", tenantId, sw.ElapsedMilliseconds, summary);
        return new WorkflowRunResult(true, summary, null);
    }

    private static TourPriceSyncOptions ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TourPriceSyncOptions();
        try
        {
            return JsonSerializer.Deserialize<TourPriceSyncOptions>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TourPriceSyncOptions();
        }
        catch { return new TourPriceSyncOptions(); }
    }

    /// Map 1 item JSON → CatalogRow. Thiếu field bắt buộc → null (bỏ dòng, không ném).
    private static CatalogRow? MapRow(string tenantId, JsonElement e)
    {
        int? I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

        var pricingId = I("pricingId");
        var providerId = I("providerId");
        var categoryId = I("categoryId");
        var providerName = S("providerName");
        if (pricingId is null || providerId is null || categoryId is null || string.IsNullOrWhiteSpace(providerName))
            return null;

        var city = S("city");
        return new CatalogRow(
            TenantId: tenantId,
            PricingId: pricingId.Value,
            ProviderServiceId: I("providerServiceId") ?? 0,
            ProviderId: providerId.Value,
            ProviderName: providerName!,
            ProviderCode: S("providerCode"),
            City: city,
            CityNorm: VietnameseText.Norm(city),
            CategoryId: categoryId.Value,
            CategoryName: S("categoryName"),
            PriceName: S("priceName"),
            Description: S("description"),
            ContractPrice: D("contractPrice"),
            PublicPrice: D("publicPrice"),
            Stars: PriceCatalogRules.ParseStars(providerName));
    }
}
```

- [ ] **Step 2: Đăng ký DI**

Trong `Services/Bootstrap/WorkflowStackRegistration.cs`, thêm vào `AddWorkflowStack` (cạnh các `AddSingleton<IScheduledWorkflow, ...>` sẵn có):

```csharp
// ─── Tour Price Catalog ──────────────────────────────────────────────
s.AddSingleton<TourkitAiProxy.Services.TourPrices.TourPriceCatalogRepository>();
s.AddSingleton<IScheduledWorkflow, TourkitAiProxy.Services.TourPrices.TourPriceCatalogSyncWorkflow>();
```

Đăng ký ở ĐÂY (không phải `Program.cs`) → web + worker cùng pickup, không phải deploy 2 lần (CLAUDE.md § Deploy tách site).

**Về yêu cầu "rải lịch, 280 tenant không được bắn cùng 3h sáng" (spec §4.3):** KHÔNG cần code
thêm. `WorkflowSchedulerService` đã tính `NextRunUtc` từ **thời điểm tenant bật workflow**, không
phải từ một giờ cố định trong ngày — nên các tenant tự nhiên lệch nhau theo lúc họ bật. Đây là
opt-in per-tenant, không phải bật đồng loạt. **Nếu** sau này có nhu cầu bật hàng loạt cho mọi
tenant cùng lúc thì mới phải rải bằng `hash(tenantId)` — lúc đó là việc của scheduler, không phải
của workflow này. Ghi lại ở đây để người sau khỏi tưởng bị bỏ sót.

- [ ] **Step 3: Build + chạy toàn bộ test**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: build thành công.

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --nologo -v q`
Expected: PASS — toàn bộ test cũ (143) + test mới, 0 failed.

- [ ] **Step 4: Kiểm tay — workflow xuất hiện trong danh sách**

Chạy app (`dotnet run --project TourkitAiProxy.csproj`), rồi:
```bash
curl -H "X-Session-Id: <sessionId>" http://localhost:5080/api/v1/workflows
```
Expected: JSON có entry `{"type":"tour-price-catalog-sync","label":"Đồng bộ bảng giá nhà cung cấp",...}`

- [ ] **Step 5: Commit**

```bash
git add Services/TourPrices/TourPriceCatalogSyncWorkflow.cs Services/Bootstrap/WorkflowStackRegistration.cs
git commit -m "feat(tour-price): workflow đồng bộ bảng giá NCC

PerTenant, service account nên không cần user online. Đăng ký ở
WorkflowStackRegistration để web + worker cùng pickup.

Dòng không còn thấy → tắt IsActive chứ không xóa. Tenant chưa cấu hình tài
khoản tự động → trả lỗi rõ, không crash."
```

---

## Task 8: Nghiệm thu trên dữ liệu thật

**Files:** (không sửa code — chỉ kiểm chứng)

**Interfaces:**
- Consumes: toàn bộ Task 1–7.

Điều kiện: Task 4 **đã deploy** lên `api.travelai.vn`.

- [ ] **Step 1: Cấu hình workflow cho 1 tenant có dữ liệu tốt**

`TopTour` là tenant chăm dữ liệu nhất (13.775 dòng có giá / 97,3% điền — đo 2026-07-15).
Cần tenant đó có service account trong `dbo.TenantServiceAccounts`.

```bash
curl -X PUT -H "X-Session-Id: <sessionId>" -H "Content-Type: application/json" \
  -d '{"enabled":true,"intervalMinutes":1440}' \
  http://localhost:5080/api/v1/workflows/tour-price-catalog-sync
```

- [ ] **Step 2: Chạy ngay**

```bash
curl -X POST -H "X-Session-Id: <sessionId>" \
  http://localhost:5080/api/v1/workflows/tour-price-catalog-sync/run-now
```
Expected: `{"ok":true,"summary":"Lấy N dòng, lưu N, bỏ K, tắt 0. Đang hiệu lực: N."}`

- [ ] **Step 3: Đối chiếu số với DB gốc**

Với `TopTour` (bản local đã khôi phục), số dòng catalog phải khớp câu đếm sau **trừ đi** vé máy bay và giá < 50k:

```sql
SELECT COUNT(*)
FROM TopTour.dbo.provider_service_pricing psp
JOIN TopTour.dbo.provider_services ps ON ps.id = psp.provider_service_id
JOIN TopTour.dbo.providers p ON p.id = ps.provider_id
JOIN TopTour.dbo.services s ON s.id = ps.service_id
WHERE ps.status = 1 AND ISNULL(psp.status,0) <> 4 AND ISNULL(psp.type,0) = 0
  AND p.status NOT IN (4,5)
  AND ISNULL(psp.contract_price,0) >= 50000
  AND s.service_name NOT LIKE N'%máy bay%';
```

Expected: chênh lệch **0**. Lệch → so từng `PricingId` để tìm dòng rơi.

- [ ] **Step 4: Kiểm 3 bất biến quan trọng**

```sql
-- (a) Description PHẢI còn nguyên — nguồn duy nhất về mùa vụ
SELECT TOP 5 ProviderName, PriceName, Description, ContractPrice
FROM dbo.TourPriceCatalog
WHERE TenantId = '<tenant>' AND Description LIKE N'%Mùa%' OR Description LIKE N'%T6-T7%';
-- Expected: ra dòng, Description có nội dung

-- (b) KHÔNG có tên hành khách lọt vào (vé máy bay bị chặn)
SELECT COUNT(*) FROM dbo.TourPriceCatalog
WHERE TenantId = '<tenant>' AND CategoryName LIKE N'%máy bay%';
-- Expected: 0

-- (c) Stars bóc được ~59%, phần còn lại NULL (không đoán bừa)
SELECT COUNT(*) AS Tong, COUNT(Stars) AS CoSao FROM dbo.TourPriceCatalog
WHERE TenantId = '<tenant>' AND CategoryName LIKE N'%Khách sạn%';
-- Expected: CoSao/Tong ≈ 0,59 — LỆCH NHIỀU nghĩa là regex sai
```

- [ ] **Step 5: Ghi kết quả nghiệm thu vào plan**

Sửa chính file này, thêm số thật đo được vào cuối (để lần sau có mốc so sánh), rồi commit:

```bash
git add docs/superpowers/plans/2026-07-15-tour-price-catalog.md
git commit -m "docs(tour-price): số nghiệm thu catalog sync trên dữ liệu thật"
```

---

## Sau plan này

**Mảng 2 (spec §8.2)** — plan riêng: `DestinationMap` (điểm đến → tỉnh), `ITourPriceRetriever` +
`StructuredPriceRetriever` (lọc tỉnh + loại + khoảng giá ±25%, cắt 60, log `retrieval_overflow` khi > 80),
2 tầng chọn giá (A: chọn NCC → B: chọn dòng giá theo `Description`), composer + `source` badge, sửa
`wizard.jsx` bỏ dòng prompt bảo AI tự bịa giá.

**Không làm** (spec §6): tham chiếu báo giá cũ (23 báo giá toàn hệ thống — không đủ dữ liệu) ·
vector/embedding (chưa tenant nào chạm ngưỡng) · upload file bảng giá · ghi ngược TourKit.
