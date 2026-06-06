# SmartMail AI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm tính năng thứ 4 — hộp thư Gmail đồng bộ theo nút Refresh, AI phân loại 6 nhóm, AI soạn nháp trả lời theo 4 ngữ điệu — vào tourkit-ai-proxy.

**Architecture:** Folder-by-feature như các tính năng cũ. Nguồn mail qua IMAP/SMTP (MailKit) sau interface `IMailSource` để sau cắm OAuth không phải đập lại. Lưu file-backed `data/mails.json` (mẫu `ReviewRepository`). Phân loại + soạn nháp tái dùng `ProviderRegistry`/`IAiProvider` sẵn có; phân loại **chỉ email mới** (email đã có trong repo = đã phân loại → bỏ qua, tiết kiệm token). Frontend là 1 trang React no-build `pages/mail.jsx` (mẫu `assistant.jsx` cho SSE).

**Tech Stack:** ASP.NET Core 8 Minimal API, MailKit (IMAP/SMTP), System.Text.Json (camelCase), React UMD + Babel standalone (no build), xUnit (test project mới — chỉ cho logic parse thuần).

---

## Quyết định về testing (đọc trước khi bắt đầu)

Repo hiện **không có test project** (CLAUDE.md: "There is no test project"). Plan này thêm **1 test project xUnit tối thiểu** `TourkitAiProxy.Tests`, **chỉ test logic thuần rủi ro cao**:
- `MailClassifier.ParseClassification` — parse JSON từ AI (nguồn bug số 1, xem lượng code tolerant-parse trong `ReviewService`/`ChatAgentService`).
- `MailMapper.FromMime` — map MimeMessage → MailItem.
- `MailRepository` — roundtrip lưu/đọc + lọc/đếm.
- `MailTaxonomy` — chuẩn hóa category/tone/status.

**KHÔNG** viết test cho: `GmailImapClient` (cần Gmail thật → verify thủ công), frontend (no build → verify thủ công), endpoint wiring (verify thủ công bằng curl/UI).

> Nếu chủ dự án muốn giữ nguyên hiện trạng "không test", có thể bỏ Task 1 và các bước test — phần code sản phẩm không đổi. Người viết plan **khuyến nghị giữ** vì TDD cho phần parse JSON tiết kiệm rất nhiều thời gian debug về sau.

---

## File Structure

**Backend mới:**
- `Models/MailModels.cs` — `MailItem`, `MailContact`, `MailDraft`, request DTOs. Một file: các type thay đổi cùng nhau.
- `Services/Mail/MailTaxonomy.cs` — bảng category/status/tone (Việt) + chuẩn hóa. Pure, không phụ thuộc.
- `Services/Mail/MailAccountStore.cs` — đọc creds Gmail từ config/env (mẫu `ProviderKeyStore`).
- `Services/Mail/IMailSource.cs` — interface nguồn mail (fetch + phase-2 send).
- `Services/Mail/MailMapper.cs` — pure: `MimeMessage` → `MailItem`. Tách riêng để test được.
- `Services/Mail/GmailImapClient.cs` — implement `IMailSource` bằng MailKit (IMAP fetch).
- `Services/Mail/MailRepository.cs` — file-backed `data/mails.json` (mẫu `ReviewRepository`).
- `Services/Mail/MailClassifier.cs` — prompt → provider → parse `{category, summary}`.
- `Services/Mail/MailReplyService.cs` — soạn nháp theo tone + chỉ thị NV (stream).
- `Endpoints/MailEndpoints.cs` — routes `/api/v1/mail/*`.

**Backend sửa:**
- `Program.cs` — DI + `app.MapMailEndpoints()`.
- `TourkitAiProxy.csproj` — thêm PackageReference MailKit.
- `appsettings.example.json` — thêm khối `Mail`.
- `.gitignore` — thêm `data/mails.json`.

**Test mới:**
- `TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
- `TourkitAiProxy.Tests/FakeWebHostEnvironment.cs`
- `TourkitAiProxy.Tests/MailTaxonomyTests.cs`
- `TourkitAiProxy.Tests/MailMapperTests.cs`
- `TourkitAiProxy.Tests/MailClassifierTests.cs`
- `TourkitAiProxy.Tests/MailRepositoryTests.cs`

**Frontend mới/sửa:**
- `wwwroot/pages/mail.jsx` — trang 3 cột.
- `wwwroot/index.html` — thêm `<script>` mail.jsx.
- `wwwroot/app.jsx` — thêm nav + route `/mail`.

**Docs sửa (cuối):**
- `CLAUDE.md` — thêm mục tính năng + bảng API + layout.

---

### Task 1: Test project scaffold + MailKit package

**Files:**
- Create: `TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` (qua CLI)
- Create: `TourkitAiProxy.Tests/FakeWebHostEnvironment.cs`
- Modify: `TourkitAiProxy.csproj` (thêm MailKit)

- [ ] **Step 1: Tạo test project xUnit + reference project chính**

Run:
```bash
dotnet new xunit -n TourkitAiProxy.Tests -o TourkitAiProxy.Tests
dotnet add TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj reference TourkitAiProxy.csproj
```
Expected: tạo thư mục `TourkitAiProxy.Tests/` với `UnitTest1.cs` mẫu.

- [ ] **Step 2: Xóa file test mẫu**

Run:
```bash
rm TourkitAiProxy.Tests/UnitTest1.cs
```

- [ ] **Step 3: Thêm package MailKit vào project chính**

Run:
```bash
dotnet add TourkitAiProxy.csproj package MailKit --version 4.8.0
```
Expected: `TourkitAiProxy.csproj` có thêm `<PackageReference Include="MailKit" Version="4.8.0" />`.

- [ ] **Step 4: Tạo fake IWebHostEnvironment cho test (để dựng MailRepository trỏ vào temp dir)**

Create `TourkitAiProxy.Tests/FakeWebHostEnvironment.cs`:
```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace TourkitAiProxy.Tests;

/// IWebHostEnvironment giả: ContentRootPath trỏ vào 1 thư mục tạm để test file-backed repo.
public sealed class FakeWebHostEnvironment : IWebHostEnvironment, IDisposable
{
    public FakeWebHostEnvironment()
    {
        ContentRootPath = Path.Combine(Path.GetTempPath(), "tkai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRootPath);
    }

    public string ApplicationName { get; set; } = "TourkitAiProxy.Tests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string EnvironmentName { get; set; } = "Test";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = null!;

    public void Dispose()
    {
        try { if (Directory.Exists(ContentRootPath)) Directory.Delete(ContentRootPath, true); } catch { }
    }
}
```

- [ ] **Step 5: Build cả solution để chắc reference + package OK**

Run:
```bash
dotnet build TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: Build succeeded, 0 Error.

- [ ] **Step 6: Commit**

```bash
git add TourkitAiProxy.Tests/ TourkitAiProxy.csproj
git commit -m "test: scaffold xUnit project + add MailKit package"
```

---

### Task 2: Models — MailItem & DTOs

**Files:**
- Create: `Models/MailModels.cs`

- [ ] **Step 1: Tạo các record model (camelCase JsonPropertyName, mẫu ReviewModels.cs)**

Create `Models/MailModels.cs`:
```csharp
using System.Text.Json.Serialization;

namespace TourkitAiProxy.Models;

/// 1 email trong hộp thư SmartMail. Lưu data/mails.json keyed by Id (Message-Id header, fallback uid).
/// Category null khi chưa phân loại; Status mặc định "moi".
public record MailItem(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("from")]       MailContact From,
    [property: JsonPropertyName("subject")]    string Subject,
    [property: JsonPropertyName("body")]       string Body,
    [property: JsonPropertyName("receivedAt")] string ReceivedAt,   // ISO-8601 (DateTime "o")
    [property: JsonPropertyName("isRead")]     bool IsRead,
    [property: JsonPropertyName("category")]   string? Category,     // hoi_dat_tour|xin_bao_gia|khieu_nai|xac_nhan|spam|khac
    [property: JsonPropertyName("status")]     string Status,        // moi|dang_xu_ly|da_phan_hoi|da_dong
    [property: JsonPropertyName("aiSummary")]  string? AiSummary,
    [property: JsonPropertyName("draft")]      MailDraft? Draft
);

public record MailContact(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("email")] string Email
);

public record MailDraft(
    [property: JsonPropertyName("tone")]        string Tone,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("text")]        string Text,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt
);

// ─── Request DTOs ─────────────────────────────────────────────────────────────
public record DraftReplyRequest(
    [property: JsonPropertyName("tone")]        string Tone,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("provider")]    string? Provider,
    [property: JsonPropertyName("model")]       string? Model,
    [property: JsonPropertyName("apiKey")]      string? ApiKey
);

public record UpdateStatusRequest(
    [property: JsonPropertyName("status")] string Status
);
```

- [ ] **Step 2: Build để chắc model compile**

Run:
```bash
dotnet build TourkitAiProxy.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Models/MailModels.cs
git commit -m "feat(mail): add MailItem models + request DTOs"
```

---

### Task 3: MailTaxonomy (category/status/tone) — TDD

**Files:**
- Create: `Services/Mail/MailTaxonomy.cs`
- Test: `TourkitAiProxy.Tests/MailTaxonomyTests.cs`

- [ ] **Step 1: Viết test thất bại**

Create `TourkitAiProxy.Tests/MailTaxonomyTests.cs`:
```csharp
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailTaxonomyTests
{
    [Theory]
    [InlineData("hoi_dat_tour", "hoi_dat_tour")]
    [InlineData("HOI_DAT_TOUR", "hoi_dat_tour")]   // không phân biệt hoa thường
    [InlineData("  spam  ", "spam")]               // trim
    [InlineData("không-biết", "khac")]             // lạ → khac
    [InlineData("", "khac")]
    [InlineData(null, "khac")]
    public void NormalizeCategory_maps_to_known_set(string? input, string expected)
        => Assert.Equal(expected, MailTaxonomy.NormalizeCategory(input));

    [Fact]
    public void Categories_has_six_entries_with_vietnamese_labels()
    {
        Assert.Equal(6, MailTaxonomy.Categories.Count);
        Assert.Equal("Khiếu nại", MailTaxonomy.Categories["khieu_nai"]);
    }

    [Fact]
    public void Tone_label_returns_vietnamese_for_known_else_default()
    {
        Assert.Equal("Lịch sự, trang trọng", MailTaxonomy.ToneLabel("lich_su"));
        Assert.Equal("Lịch sự, trang trọng", MailTaxonomy.ToneLabel("không-có"));   // fallback
    }

    [Fact]
    public void IsStatus_true_only_for_known()
    {
        Assert.True(MailTaxonomy.IsStatus("da_dong"));
        Assert.False(MailTaxonomy.IsStatus("bừa"));
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailTaxonomyTests"
```
Expected: FAIL — "The type or namespace name 'MailTaxonomy' could not be found".

- [ ] **Step 3: Viết MailTaxonomy tối thiểu để pass**

Create `Services/Mail/MailTaxonomy.cs`:
```csharp
namespace TourkitAiProxy.Services.Mail;

/// Nguồn duy nhất cho danh mục phân loại, trạng thái, ngữ điệu — nhãn tiếng Việt.
/// Dùng cho cả prompt AI, validate endpoint, lẫn hiển thị.
public static class MailTaxonomy
{
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["hoi_dat_tour"] = "Hỏi đặt tour",
        ["xin_bao_gia"]  = "Xin báo giá",
        ["khieu_nai"]    = "Khiếu nại",
        ["xac_nhan"]     = "Xác nhận",
        ["spam"]         = "Spam",
        ["khac"]         = "Khác",
    };

    public static readonly IReadOnlyDictionary<string, string> Statuses = new Dictionary<string, string>
    {
        ["moi"]         = "Mới",
        ["dang_xu_ly"]  = "Đang xử lý",
        ["da_phan_hoi"] = "Đã phản hồi",
        ["da_dong"]     = "Đã đóng",
    };

    /// tone key → mô tả ngữ điệu (nhúng vào prompt + hiển thị nút chọn).
    public static readonly IReadOnlyDictionary<string, string> Tones = new Dictionary<string, string>
    {
        ["lich_su"]    = "Lịch sự, trang trọng",
        ["than_thien"] = "Thân thiện, cởi mở",
        ["dam_phan"]   = "Đàm phán thương lượng",
        ["xin_loi"]    = "Lời xin lỗi chuyên biệt",
    };

    private const string DefaultCategory = "khac";
    private const string DefaultTone = "lich_su";

    /// Chuẩn hóa category AI trả về: trim + lowercase, nếu không thuộc 6 nhóm → "khac".
    public static string NormalizeCategory(string? raw)
    {
        var k = (raw ?? "").Trim().ToLowerInvariant();
        return Categories.ContainsKey(k) ? k : DefaultCategory;
    }

    public static bool IsCategory(string? k) => k != null && Categories.ContainsKey(k);
    public static bool IsStatus(string? k) => k != null && Statuses.ContainsKey(k);

    public static string ToneLabel(string? toneKey)
    {
        var k = (toneKey ?? "").Trim().ToLowerInvariant();
        return Tones.TryGetValue(k, out var v) ? v : Tones[DefaultTone];
    }
}
```

- [ ] **Step 4: Chạy test để xác nhận PASS**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailTaxonomyTests"
```
Expected: PASS — Passed! 4 tests (gồm 6 InlineData của Theory).

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailTaxonomy.cs TourkitAiProxy.Tests/MailTaxonomyTests.cs
git commit -m "feat(mail): add MailTaxonomy with category/status/tone maps"
```

---

### Task 4: MailMapper (MimeMessage → MailItem) — TDD

**Files:**
- Create: `Services/Mail/MailMapper.cs`
- Test: `TourkitAiProxy.Tests/MailMapperTests.cs`

- [ ] **Step 1: Viết test thất bại (dựng MimeMessage trong bộ nhớ)**

Create `TourkitAiProxy.Tests/MailMapperTests.cs`:
```csharp
using MimeKit;
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailMapperTests
{
    private static MimeMessage Build(string from, string fromName, string subject, string body, string? messageId)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, from));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        if (messageId != null) msg.MessageId = messageId;
        msg.Date = new DateTimeOffset(2026, 6, 5, 8, 30, 0, TimeSpan.Zero);
        return msg;
    }

    [Fact]
    public void FromMime_extracts_core_fields()
    {
        var msg = Build("minh.tran@gmail.com", "minh.tran", "Đặt vé combo Phú Quốc", "Cứu! Mình cần 2 combo...", "<abc@mail.gmail.com>");
        var item = MailMapper.FromMime(msg, fallbackId: "fallback:1");

        Assert.Equal("<abc@mail.gmail.com>", item.Id);
        Assert.Equal("minh.tran", item.From.Name);
        Assert.Equal("minh.tran@gmail.com", item.From.Email);
        Assert.Equal("Đặt vé combo Phú Quốc", item.Subject);
        Assert.Equal("Cứu! Mình cần 2 combo...", item.Body);
        Assert.Equal("moi", item.Status);
        Assert.Null(item.Category);
        Assert.False(item.IsRead);
    }

    [Fact]
    public void FromMime_uses_fallback_id_when_no_message_id()
    {
        var msg = Build("a@b.com", "A", "S", "B", messageId: null);
        var item = MailMapper.FromMime(msg, fallbackId: "fallback:42");
        Assert.Equal("fallback:42", item.Id);
    }

    [Fact]
    public void FromMime_handles_missing_subject()
    {
        var msg = Build("a@b.com", "A", "", "B", "<x@y>");
        var item = MailMapper.FromMime(msg, "f:1");
        Assert.Equal("(không tiêu đề)", item.Subject);
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailMapperTests"
```
Expected: FAIL — "MailMapper could not be found".

- [ ] **Step 3: Viết MailMapper**

Create `Services/Mail/MailMapper.cs`:
```csharp
using System.Text.RegularExpressions;
using MimeKit;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Map MimeMessage (MailKit) → MailItem. Pure, không I/O → test được.
public static class MailMapper
{
    public static MailItem FromMime(MimeMessage msg, string fallbackId)
    {
        var from = msg.From.Mailboxes.FirstOrDefault();
        var id = string.IsNullOrWhiteSpace(msg.MessageId) ? fallbackId : msg.MessageId!;

        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(msg.HtmlBody))
            body = HtmlToText(msg.HtmlBody);

        var received = msg.Date == default ? DateTimeOffset.UtcNow : msg.Date;

        return new MailItem(
            Id:         id,
            From:       new MailContact(
                            Name:  from?.Name ?? from?.Address ?? "(không rõ)",
                            Email: from?.Address ?? ""),
            Subject:    string.IsNullOrWhiteSpace(msg.Subject) ? "(không tiêu đề)" : msg.Subject!,
            Body:       (body ?? "").Trim(),
            ReceivedAt: received.UtcDateTime.ToString("o"),
            IsRead:     false,
            Category:   null,
            Status:     "moi",
            AiSummary:  null,
            Draft:      null
        );
    }

    /// Strip thẻ HTML thô → text (đủ cho phân loại + soạn trả lời, không cần render đẹp).
    private static string HtmlToText(string html)
    {
        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}
```

- [ ] **Step 4: Chạy test để xác nhận PASS**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailMapperTests"
```
Expected: PASS — Passed! 3 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailMapper.cs TourkitAiProxy.Tests/MailMapperTests.cs
git commit -m "feat(mail): add MailMapper MimeMessage->MailItem"
```

---

### Task 5: MailClassifier (parse) — TDD

**Files:**
- Create: `Services/Mail/MailClassifier.cs`
- Test: `TourkitAiProxy.Tests/MailClassifierTests.cs`

- [ ] **Step 1: Viết test thất bại cho ParseClassification (chỉ test phần parse thuần)**

Create `TourkitAiProxy.Tests/MailClassifierTests.cs`:
```csharp
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailClassifierTests
{
    [Fact]
    public void Parse_plain_json()
    {
        var (cat, sum) = MailClassifier.ParseClassification(
            "{\"category\":\"hoi_dat_tour\",\"summary\":\"Khách cần 2 combo Phú Quốc gấp\"}");
        Assert.Equal("hoi_dat_tour", cat);
        Assert.Equal("Khách cần 2 combo Phú Quốc gấp", sum);
    }

    [Fact]
    public void Parse_strips_fences_and_thinking_prose()
    {
        var raw = "Để tôi suy nghĩ...\n```json\n{\"category\":\"khieu_nai\",\"summary\":\"Khách phàn nàn trễ chuyến\"}\n```\nXong.";
        var (cat, sum) = MailClassifier.ParseClassification(raw);
        Assert.Equal("khieu_nai", cat);
        Assert.Equal("Khách phàn nàn trễ chuyến", sum);
    }

    [Fact]
    public void Parse_unknown_category_falls_back_to_khac()
    {
        var (cat, _) = MailClassifier.ParseClassification("{\"category\":\"bịa_ra\",\"summary\":\"x\"}");
        Assert.Equal("khac", cat);
    }

    [Fact]
    public void Parse_garbage_returns_khac_and_empty_summary()
    {
        var (cat, sum) = MailClassifier.ParseClassification("không có json ở đây");
        Assert.Equal("khac", cat);
        Assert.Equal("", sum);
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailClassifierTests"
```
Expected: FAIL — "MailClassifier could not be found".

- [ ] **Step 3: Viết MailClassifier (parse + classify gọi provider)**

Create `Services/Mail/MailClassifier.cs`:
```csharp
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Mail;

/// Phân loại 1 email vào 6 nhóm + tóm tắt 1 câu. Buffered (mẫu ReviewService — tránh trộn
/// reasoning_content vào JSON khi stream). Parse tolerant qua LooseJson.
public class MailClassifier
{
    private readonly ProviderRegistry _registry;
    private readonly ILogger<MailClassifier> _log;

    private const string SYSTEM =
        "Bạn là bộ phân loại email cho công ty du lịch Tourkit. " +
        "Đọc email và CHỌN ĐÚNG 1 nhóm + tóm tắt 1 câu ngắn bằng tiếng Việt. " +
        "Output ONLY raw JSON, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Ký tự đầu tiên BẮT BUỘC là '{'.";

    public MailClassifier(ProviderRegistry registry, ILogger<MailClassifier> log)
    {
        _registry = registry; _log = log;
    }

    /// Gọi AI phân loại 1 email → (categoryKey đã chuẩn hóa, summary). Lỗi → (khac, "").
    public async Task<(string Category, string Summary)> ClassifyAsync(MailItem mail, CancellationToken ct)
    {
        var provider = _registry.Resolve(null);
        var req = new CompleteRequest(
            Prompt:      BuildPrompt(mail),
            Provider:    null, Model: null,
            MaxTokens:   1000, Temperature: 0.1,
            System:      SYSTEM, ApiKey: null);

        try
        {
            var result = await provider.CompleteAsync(req, ct);
            return ParseClassification(result.Text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Phân loại email {Id} lỗi → khac", mail.Id);
            return ("khac", "");
        }
    }

    private static string BuildPrompt(MailItem mail)
    {
        var cats = string.Join("\n", MailTaxonomy.Categories.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var body = mail.Body.Length > 2000 ? mail.Body[..2000] + " …(cắt)" : mail.Body;
        return $@"PHÂN LOẠI EMAIL SAU vào ĐÚNG 1 nhóm:

CÁC NHÓM:
{cats}

EMAIL:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {body}

OUTPUT JSON (key category dùng ĐÚNG mã nhóm ở trên):
{{ ""category"": ""<mã nhóm>"", ""summary"": ""tóm tắt 1 câu ngắn"" }}

Trả JSON ngay:";
    }

    /// Parse output AI → (category chuẩn hóa, summary). Pure, không I/O → test được.
    public static (string Category, string Summary) ParseClassification(string raw)
    {
        var json = LooseJson.ExtractFirstObject(raw);
        if (json == null) return ("khac", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cat = MailTaxonomy.NormalizeCategory(GetStr(root, "category"));
            var sum = GetStr(root, "summary") ?? "";
            return (cat, sum.Trim());
        }
        catch
        {
            return ("khac", "");
        }
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }
}
```

- [ ] **Step 4: Chạy test để xác nhận PASS**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailClassifierTests"
```
Expected: PASS — Passed! 4 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailClassifier.cs TourkitAiProxy.Tests/MailClassifierTests.cs
git commit -m "feat(mail): add MailClassifier (6-category + summary)"
```

---

### Task 6: MailRepository (file-backed) — TDD

**Files:**
- Create: `Services/Mail/MailRepository.cs`
- Test: `TourkitAiProxy.Tests/MailRepositoryTests.cs`

- [ ] **Step 1: Viết test thất bại (roundtrip + lọc + đếm)**

Create `TourkitAiProxy.Tests/MailRepositoryTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailRepositoryTests
{
    private static MailItem Item(string id, string category, string status, string subject = "S", string body = "B")
        => new(id, new MailContact("N", "n@x.com"), subject, body, "2026-06-05T08:30:00.0000000Z",
               false, category, status, null, null);

    private static MailRepository NewRepo(out FakeWebHostEnvironment env)
    {
        env = new FakeWebHostEnvironment();
        return new MailRepository(env, NullLogger<MailRepository>.Instance);
    }

    [Fact]
    public void Upsert_then_Get_roundtrips()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<a@x>", "hoi_dat_tour", "moi"));
            var got = repo.Get("<a@x>");
            Assert.NotNull(got);
            Assert.Equal("hoi_dat_tour", got!.Category);
        }
    }

    [Fact]
    public void Has_true_after_upsert()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            Assert.False(repo.Has("<x>"));
            repo.Upsert(Item("<x>", "spam", "moi"));
            Assert.True(repo.Has("<x>"));
        }
    }

    [Fact]
    public void Filter_by_status_and_category_and_search()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<1>", "hoi_dat_tour", "moi", subject: "Đặt tour Phú Quốc"));
            repo.Upsert(Item("<2>", "khieu_nai", "da_dong", subject: "Phàn nàn"));
            repo.Upsert(Item("<3>", "hoi_dat_tour", "da_dong", subject: "Hỏi Đà Nẵng"));

            Assert.Equal(2, repo.Filter(status: null, category: "hoi_dat_tour", search: null).Count);
            Assert.Equal(2, repo.Filter(status: "da_dong", category: null, search: null).Count);
            Assert.Single(repo.Filter(status: null, category: null, search: "phú quốc"));   // search không phân biệt hoa/dấu? -> ít nhất khớp 'Phú Quốc'
        }
    }

    [Fact]
    public void Counts_groups_by_status_and_category()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<1>", "hoi_dat_tour", "moi"));
            repo.Upsert(Item("<2>", "hoi_dat_tour", "moi"));
            repo.Upsert(Item("<3>", "spam", "da_dong"));

            var counts = repo.Counts();
            Assert.Equal(3, counts.Total);
            Assert.Equal(2, counts.ByStatus["moi"]);
            Assert.Equal(2, counts.ByCategory["hoi_dat_tour"]);
        }
    }

    [Fact]
    public void Persists_across_instances()
    {
        var env = new FakeWebHostEnvironment();
        using (env)
        {
            var repo1 = new MailRepository(env, NullLogger<MailRepository>.Instance);
            repo1.Upsert(Item("<keep>", "xac_nhan", "moi"));

            var repo2 = new MailRepository(env, NullLogger<MailRepository>.Instance);
            Assert.NotNull(repo2.Get("<keep>"));
        }
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận FAIL**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailRepositoryTests"
```
Expected: FAIL — "MailRepository could not be found".

- [ ] **Step 3: Viết MailRepository (mẫu ReviewRepository, thêm Filter/Counts)**

Create `Services/Mail/MailRepository.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Counts cho sidebar: tổng + theo trạng thái + theo nhóm.
public record MailCounts(int Total, Dictionary<string, int> ByStatus, Dictionary<string, int> ByCategory);

/// File-backed store: mailId → MailItem. Persist data/mails.json. Threadsafe qua lock.
/// Mẫu ReviewRepository. Production: thay SQLite/Postgres.
public class MailRepository
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, MailItem> _map;
    private readonly ILogger<MailRepository> _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MailRepository(IWebHostEnvironment env, ILogger<MailRepository> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mails.json");

        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                _map = JsonSerializer.Deserialize<Dictionary<string, MailItem>>(json, _jsonOpts) ?? new();
                _log.LogInformation("Loaded {N} mails", _map.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Parse mails.json failed — reset rỗng");
                _map = new();
            }
        }
        else
        {
            _map = new();
            File.WriteAllText(_path, "{}");
        }
    }

    public MailItem? Get(string id)
    {
        lock (_lock) return _map.TryGetValue(id, out var m) ? m : null;
    }

    public bool Has(string id)
    {
        lock (_lock) return _map.ContainsKey(id);
    }

    public void Upsert(MailItem item)
    {
        lock (_lock) { _map[item.Id] = item; Persist(); }
    }

    public bool SetStatus(string id, string status)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var m)) return false;
            _map[id] = m with { Status = status };
            Persist();
            return true;
        }
    }

    public bool SetDraft(string id, MailDraft draft, string status)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var m)) return false;
            _map[id] = m with { Draft = draft, Status = status };
            Persist();
            return true;
        }
    }

    /// Lọc theo status/category/search (search khớp subject+from+body, bỏ dấu, không phân biệt hoa).
    /// Sắp xếp mới nhất trước.
    public IReadOnlyList<MailItem> Filter(string? status, string? category, string? search)
    {
        lock (_lock)
        {
            IEnumerable<MailItem> q = _map.Values;
            if (!string.IsNullOrWhiteSpace(status))   q = q.Where(m => m.Status == status);
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(m => m.Category == category);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = Norm(search);
                q = q.Where(m => Norm($"{m.Subject} {m.From.Name} {m.From.Email} {m.Body}").Contains(s));
            }
            return q.OrderByDescending(m => m.ReceivedAt, StringComparer.Ordinal).ToList();
        }
    }

    public MailCounts Counts()
    {
        lock (_lock)
        {
            var byStatus = new Dictionary<string, int>();
            var byCat = new Dictionary<string, int>();
            foreach (var m in _map.Values)
            {
                byStatus[m.Status] = byStatus.GetValueOrDefault(m.Status) + 1;
                var c = m.Category ?? "khac";
                byCat[c] = byCat.GetValueOrDefault(c) + 1;
            }
            return new MailCounts(_map.Count, byStatus, byCat);
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, _jsonOpts)); }
        catch (Exception ex) { _log.LogError(ex, "Write mails.json failed"); }
    }

    /// Chuẩn hóa search: lowercase + bỏ dấu tiếng Việt + đ→d.
    private static string Norm(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
```

- [ ] **Step 4: Chạy test để xác nhận PASS**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailRepositoryTests"
```
Expected: PASS — Passed! 5 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailRepository.cs TourkitAiProxy.Tests/MailRepositoryTests.cs
git commit -m "feat(mail): add file-backed MailRepository with filter + counts"
```

---

### Task 7: MailAccountStore + IMailSource + GmailImapClient

**Files:**
- Create: `Services/Mail/MailAccountStore.cs`
- Create: `Services/Mail/IMailSource.cs`
- Create: `Services/Mail/GmailImapClient.cs`

> Không TDD (cần Gmail thật) — verify thủ công ở Task 11.

- [ ] **Step 1: MailAccountStore — đọc creds Gmail từ config/env (mẫu ProviderKeyStore)**

Create `Services/Mail/MailAccountStore.cs`:
```csharp
namespace TourkitAiProxy.Services.Mail;

/// Đọc creds hộp thư Gmail từ config (Mail:Gmail:Address / AppPassword) hoặc env
/// (MAIL_GMAIL_ADDRESS / MAIL_GMAIL_APP_PASSWORD). KHÔNG persist plaintext ở đây.
public class MailAccountStore
{
    private readonly IConfiguration _cfg;
    public MailAccountStore(IConfiguration cfg) => _cfg = cfg;

    public (string Address, string AppPassword) Get()
    {
        var addr = _cfg["Mail:Gmail:Address"];
        if (string.IsNullOrWhiteSpace(addr)) addr = Environment.GetEnvironmentVariable("MAIL_GMAIL_ADDRESS");
        var pwd = _cfg["Mail:Gmail:AppPassword"];
        if (string.IsNullOrWhiteSpace(pwd)) pwd = Environment.GetEnvironmentVariable("MAIL_GMAIL_APP_PASSWORD");
        return (addr ?? "", pwd ?? "");
    }

    public bool IsConfigured()
    {
        var (a, p) = Get();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(p);
    }
}
```

- [ ] **Step 2: IMailSource interface (để sau cắm OAuth)**

Create `Services/Mail/IMailSource.cs`:
```csharp
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// Nguồn mail trừu tượng. Phase 1: GmailImapClient (IMAP). Phase 2/sau: OAuth có thể implement
/// interface này mà không đụng phần còn lại.
public interface IMailSource
{
    /// Kéo tối đa `max` email mới nhất từ INBOX. Throw nếu chưa cấu hình / kết nối lỗi.
    Task<IReadOnlyList<MailItem>> FetchRecentAsync(int max, CancellationToken ct);
}
```

- [ ] **Step 3: GmailImapClient (MailKit)**

Create `Services/Mail/GmailImapClient.cs`:
```csharp
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Mail;

/// IMailSource qua IMAP Gmail (MailKit). Đọc-only INBOX. Auth bằng App Password
/// (cần bật 2-Step Verification + bật IMAP trong Gmail). Mỗi lần fetch mở/đóng kết nối riêng.
public class GmailImapClient : IMailSource
{
    private const string ImapHost = "imap.gmail.com";
    private const int ImapPort = 993;

    private readonly MailAccountStore _account;
    private readonly ILogger<GmailImapClient> _log;

    public GmailImapClient(MailAccountStore account, ILogger<GmailImapClient> log)
    {
        _account = account; _log = log;
    }

    public async Task<IReadOnlyList<MailItem>> FetchRecentAsync(int max, CancellationToken ct)
    {
        var (address, appPassword) = _account.Get();
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(appPassword))
            throw new InvalidOperationException(
                "Chưa cấu hình hộp thư Gmail. Đặt Mail:Gmail:Address + Mail:Gmail:AppPassword (App Password 16 ký tự).");

        using var client = new ImapClient();
        await client.ConnectAsync(ImapHost, ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(address, appPassword, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var items = new List<MailItem>();
        var count = inbox.Count;
        if (count > 0)
        {
            var start = Math.Max(0, count - max);
            for (int i = count - 1; i >= start; i--)   // mới nhất trước
            {
                ct.ThrowIfCancellationRequested();
                var msg = await inbox.GetMessageAsync(i, ct);
                items.Add(MailMapper.FromMime(msg, fallbackId: $"{address}:{i}"));
            }
        }

        await client.DisconnectAsync(true, ct);
        _log.LogInformation("IMAP kéo {N} email từ {Addr}", items.Count, address);
        return items;
    }
}
```

- [ ] **Step 4: Build để chắc MailKit API đúng**

Run:
```bash
dotnet build TourkitAiProxy.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailAccountStore.cs Services/Mail/IMailSource.cs Services/Mail/GmailImapClient.cs
git commit -m "feat(mail): add Gmail IMAP source + account store"
```

---

### Task 8: MailReplyService (soạn nháp, stream)

**Files:**
- Create: `Services/Mail/MailReplyService.cs`

> Phần stream gọi provider — verify thủ công. Prompt builder pure (có thể thêm test sau nếu muốn).

- [ ] **Step 1: Viết MailReplyService**

Create `Services/Mail/MailReplyService.cs`:
```csharp
using System.Text;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Mail;

/// Soạn NHÁP trả lời 1 email theo ngữ điệu + chỉ thị nhân viên. Stream token ra qua `onDelta`
/// (mẫu ChatAgentService.AskStreamAsync). Lưu nháp + chuyển trạng thái sang dang_xu_ly.
public class MailReplyService
{
    private readonly ProviderRegistry _registry;
    private readonly MailRepository _repo;
    private readonly ILogger<MailReplyService> _log;

    private const string SYSTEM =
        "Bạn là nhân viên CSKH công ty du lịch Tourkit, soạn email trả lời khách hàng bằng tiếng Việt. " +
        "Viết email hoàn chỉnh, lịch sự, đúng ngữ điệu yêu cầu, bám nội dung email gốc và chỉ thị của nhân viên. " +
        "CHỈ trả nội dung email (có lời chào + thân bài + ký tên 'Đội ngũ Tourkit'), KHÔNG giải thích thêm, KHÔNG markdown.";

    public MailReplyService(ProviderRegistry registry, MailRepository repo, ILogger<MailReplyService> log)
    {
        _registry = registry; _repo = repo; _log = log;
    }

    /// Stream nháp; trả về text đầy đủ đã ghép. Lưu vào repo khi xong.
    public async Task<string> DraftStreamAsync(
        MailItem mail, DraftReplyRequest req, Func<string, Task> onDelta, CancellationToken ct)
    {
        var provider = _registry.Resolve(req.Provider);
        var toneLabel = MailTaxonomy.ToneLabel(req.Tone);

        var completeReq = new CompleteRequest(
            Prompt:      BuildPrompt(mail, toneLabel, req.Instruction),
            Provider:    req.Provider, Model: req.Model,
            MaxTokens:   2000, Temperature: 0.6,
            System:      SYSTEM, ApiKey: req.ApiKey);

        var sb = new StringBuilder();
        await provider.StreamAsync(completeReq, async d => { sb.Append(d); await onDelta(d); }, ct);

        var text = sb.ToString().Trim();
        if (text.Length > 0)
        {
            var draft = new MailDraft(req.Tone, req.Instruction, text, DateTime.UtcNow.ToString("o"));
            _repo.SetDraft(mail.Id, draft, status: "dang_xu_ly");
        }
        return text;
    }

    private static string BuildPrompt(MailItem mail, string toneLabel, string? instruction)
    {
        var instr = string.IsNullOrWhiteSpace(instruction) ? "(không có)" : instruction!.Trim();
        return $@"EMAIL CỦA KHÁCH:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {mail.Body}

NGỮ ĐIỆU YÊU CẦU: {toneLabel}
CHỈ THỊ THÊM CỦA NHÂN VIÊN: {instr}

Soạn email trả lời hoàn chỉnh:";
    }
}
```

- [ ] **Step 2: Build**

Run:
```bash
dotnet build TourkitAiProxy.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Services/Mail/MailReplyService.cs
git commit -m "feat(mail): add MailReplyService (tone-aware streaming draft)"
```

---

### Task 9: MailEndpoints + DI wiring

**Files:**
- Create: `Endpoints/MailEndpoints.cs`
- Modify: `Program.cs`
- Modify: `appsettings.example.json`
- Modify: `.gitignore`

- [ ] **Step 1: Viết MailEndpoints (sync/list/detail/draft-SSE/status)**

Create `Endpoints/MailEndpoints.cs`:
```csharp
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// SmartMail AI — hộp thư Gmail + phân loại AI + soạn nháp trả lời.
///   POST  /api/v1/mail/sync                 — IMAP kéo N thư mới nhất, phân loại email MỚI, lưu → {items, counts}
///   GET   /api/v1/mail?status=&category=&search= — list đã lọc + counts
///   GET   /api/v1/mail/{id}                  — chi tiết 1 email
///   POST  /api/v1/mail/{id}/reply/draft      — SSE: stream nháp trả lời {tone, instruction, provider?, model?, apiKey?}
///   PATCH /api/v1/mail/{id}/status           — đổi trạng thái {status}
/// </summary>
public static class MailEndpoints
{
    private const int SyncMax = 30;
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        // ─── POST /mail/sync ───────────────────────────────────────────────────
        v1.MapPost("/mail/sync", async (
            IMailSource source, MailRepository repo, MailClassifier classifier,
            ILogger<Program> log, HttpContext ctx) =>
        {
            IReadOnlyList<MailItem> fetched;
            try
            {
                fetched = await source.FetchRecentAsync(SyncMax, ctx.RequestAborted);
            }
            catch (InvalidOperationException ex)   // chưa cấu hình
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "IMAP sync lỗi");
                return Results.Json(new { error = "Không kết nối được hộp thư: " + ex.Message }, statusCode: 502);
            }

            int classified = 0;
            foreach (var mail in fetched)
            {
                if (repo.Has(mail.Id)) continue;   // đã có = đã phân loại → bỏ qua (tiết kiệm token)
                var (cat, sum) = await classifier.ClassifyAsync(mail, ctx.RequestAborted);
                repo.Upsert(mail with { Category = cat, AiSummary = sum });
                classified++;
            }
            log.LogInformation("[mail] sync: {Fetched} kéo về, {New} phân loại mới", fetched.Count, classified);

            return Results.Json(new { items = repo.Filter(null, null, null), counts = repo.Counts(), classified });
        });

        // ─── GET /mail ─────────────────────────────────────────────────────────
        v1.MapGet("/mail", (MailRepository repo, string? status, string? category, string? search) =>
            Results.Json(new { items = repo.Filter(status, category, search), counts = repo.Counts() }));

        // ─── GET /mail/{id} ────────────────────────────────────────────────────
        v1.MapGet("/mail/{id}", (string id, MailRepository repo) =>
        {
            var m = repo.Get(id);
            return m == null ? Results.NotFound(new { error = "Không tìm thấy email" }) : Results.Json(m);
        });

        // ─── POST /mail/{id}/reply/draft (SSE) ─────────────────────────────────
        v1.MapPost("/mail/{id}/reply/draft", async (
            string id, DraftReplyRequest req, MailRepository repo, MailReplyService replyService,
            ILogger<Program> log, HttpContext ctx) =>
        {
            var mail = repo.Get(id);
            if (mail == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "Không tìm thấy email" });
                return;
            }

            ctx.Response.Headers["Content-Type"]      = "text/event-stream";
            ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.StartAsync(ctx.RequestAborted);

            async Task Emit(object payload)
            {
                var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(payload, SseJson) + "\n\n");
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                var text = await replyService.DraftStreamAsync(mail, req,
                    async d => await Emit(new { delta = d }), ctx.RequestAborted);
                await Emit(new { done = true, text });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Soạn nháp email {Id} lỗi", id);
                try { await Emit(new { error = "Soạn nháp lỗi: " + ex.Message }); await Emit(new { done = true }); } catch { }
            }
        });

        // ─── PATCH /mail/{id}/status ───────────────────────────────────────────
        v1.MapPatch("/mail/{id}/status", (string id, UpdateStatusRequest req, MailRepository repo) =>
        {
            if (!MailTaxonomy.IsStatus(req.Status))
                return Results.BadRequest(new { error = "status không hợp lệ" });
            return repo.SetStatus(id, req.Status)
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "Không tìm thấy email" });
        });

        return routes;
    }
}
```

- [ ] **Step 2: Đăng ký DI + map route trong Program.cs**

Modify `Program.cs` — thêm khối DI ngay TRƯỚC dòng `var app = builder.Build();`:
```csharp
// SmartMail AI — hộp thư Gmail + phân loại AI + soạn nháp trả lời.
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailAccountStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.IMailSource, TourkitAiProxy.Services.Mail.GmailImapClient>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailClassifier>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailReplyService>();
```

Modify `Program.cs` — thêm vào khối Routes, sau `app.MapChatEndpoints();`:
```csharp
app.MapMailEndpoints();
```

- [ ] **Step 3: Thêm khối Mail vào appsettings.example.json**

Modify `appsettings.example.json` — thêm sau khối `"TourKit"` (trước `"Redis"`):
```json
  "Mail": {
    "_comment": "Gmail App Password (cần bật 2-Step Verification + bật IMAP). Hoặc env MAIL_GMAIL_ADDRESS / MAIL_GMAIL_APP_PASSWORD.",
    "Gmail": {
      "Address": "",
      "AppPassword": ""
    }
  },
```

- [ ] **Step 4: Gitignore data/mails.json**

Modify `.gitignore` — thêm dòng (cạnh `data/reviews.json`):
```
data/mails.json
```

- [ ] **Step 5: Build + chạy thử khởi động**

Run:
```bash
dotnet build TourkitAiProxy.csproj
```
Expected: Build succeeded, 0 Error.

- [ ] **Step 6: Commit**

```bash
git add Endpoints/MailEndpoints.cs Program.cs appsettings.example.json .gitignore
git commit -m "feat(mail): add MailEndpoints + DI wiring + config"
```

---

### Task 10: Frontend — pages/mail.jsx + nav

**Files:**
- Create: `wwwroot/pages/mail.jsx`
- Modify: `wwwroot/index.html`
- Modify: `wwwroot/app.jsx`

> No build step → verify thủ công ở Task 11. Mẫu SSE: assistant.jsx.

- [ ] **Step 1: Tạo trang mail.jsx (3 cột: filter / list / chi tiết + soạn AI)**

Create `wwwroot/pages/mail.jsx`:
```jsx
// pages/mail.jsx — SmartMail AI. 3 cột: TRÁI filter, GIỮA list email, PHẢI chi tiết + soạn AI.
// Luồng: Refresh → POST /api/v1/mail/sync (IMAP + phân loại) → list. Chọn email → soạn nháp (SSE).

const { useState: _mS, useEffect: _mE, useRef: _mR } = React;

const _CAT_VI = {
  hoi_dat_tour: 'Hỏi đặt tour', xin_bao_gia: 'Xin báo giá', khieu_nai: 'Khiếu nại',
  xac_nhan: 'Xác nhận', spam: 'Spam', khac: 'Khác',
};
const _STATUS_VI = { moi: 'Mới', dang_xu_ly: 'Đang xử lý', da_phan_hoi: 'Đã phản hồi', da_dong: 'Đã đóng' };
const _STATUS_ORDER = ['moi', 'dang_xu_ly', 'da_phan_hoi', 'da_dong'];
const _CAT_ORDER = ['hoi_dat_tour', 'xin_bao_gia', 'khieu_nai', 'xac_nhan', 'spam', 'khac'];
const _TONES = [
  { key: 'lich_su', label: 'Lịch sự, trang trọng' },
  { key: 'than_thien', label: 'Thân thiện, cởi mở' },
  { key: 'dam_phan', label: 'Đàm phán thương lượng' },
  { key: 'xin_loi', label: 'Lời xin lỗi chuyên biệt' },
];

function _fmtWhen(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return '';
  return d.toLocaleString('vi-VN', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}
function _initials(name) {
  if (!name) return '✉';
  const w = name.trim().split(/\s+/);
  return (w.slice(-2).map(x => x[0] || '').join('') || name[0] || '?').toUpperCase();
}

function MailPage({ pushToast }) {
  const [items, setItems] = _mS([]);
  const [counts, setCounts] = _mS({ total: 0, byStatus: {}, byCategory: {} });
  const [selId, setSelId] = _mS(null);
  const [fStatus, setFStatus] = _mS(null);
  const [fCategory, setFCategory] = _mS(null);
  const [search, setSearch] = _mS('');
  const [syncing, setSyncing] = _mS(false);
  const [loading, setLoading] = _mS(false);

  const [tone, setTone] = _mS('lich_su');
  const [instruction, setInstruction] = _mS('');
  const [draft, setDraft] = _mS('');
  const [drafting, setDrafting] = _mS(false);

  const sel = items.find(m => m.id === selId) || null;

  function applyData(data) {
    setItems(data.items || []);
    if (data.counts) setCounts(data.counts);
  }

  async function load() {
    setLoading(true);
    try {
      const qs = new URLSearchParams();
      if (fStatus) qs.set('status', fStatus);
      if (fCategory) qs.set('category', fCategory);
      if (search.trim()) qs.set('search', search.trim());
      const r = await fetch('/api/v1/mail?' + qs.toString());
      const data = await r.json();
      if (r.ok) applyData(data); else pushToast(data.error || 'Lỗi tải hộp thư', 'error');
    } catch (e) { pushToast('Lỗi tải hộp thư: ' + e.message, 'error'); }
    finally { setLoading(false); }
  }

  _mE(() => { load(); }, [fStatus, fCategory]);

  async function sync() {
    setSyncing(true);
    try {
      const r = await fetch('/api/v1/mail/sync', { method: 'POST' });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Đồng bộ lỗi', 'error'); return; }
      applyData(data);
      pushToast(`Đã đồng bộ · ${data.classified || 0} email mới được phân loại`);
    } catch (e) { pushToast('Đồng bộ lỗi: ' + e.message, 'error'); }
    finally { setSyncing(false); }
  }

  function selectMail(id) {
    setSelId(id);
    const m = items.find(x => x.id === id);
    setDraft(m && m.draft ? m.draft.text : '');
    setTone(m && m.draft ? m.draft.tone : 'lich_su');
    setInstruction(m && m.draft ? (m.draft.instruction || '') : '');
  }

  async function setStatus(id, status) {
    try {
      const r = await fetch(`/api/v1/mail/${encodeURIComponent(id)}/status`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status }),
      });
      if (r.ok) { setItems(prev => prev.map(m => m.id === id ? { ...m, status } : m)); load(); }
      else pushToast('Đổi trạng thái lỗi', 'error');
    } catch (e) { pushToast(e.message, 'error'); }
  }

  async function composeDraft() {
    if (!sel || drafting) return;
    setDrafting(true); setDraft('');
    const cfg = (window.tourkit && window.tourkit.ai && window.tourkit.ai.getConfig) ? window.tourkit.ai.getConfig() : {};
    try {
      const r = await fetch(`/api/v1/mail/${encodeURIComponent(sel.id)}/reply/draft`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
        body: JSON.stringify({
          tone, instruction, provider: cfg.provider, model: cfg.model,
          apiKey: (window.tourkit.ai.getKey && cfg.provider) ? window.tourkit.ai.getKey(cfg.provider) : undefined,
        }),
      });
      if (!r.ok || !r.body) { const t = await r.text().catch(() => ''); throw new Error(t.slice(0, 200) || ('HTTP ' + r.status)); }

      const reader = r.body.getReader();
      const dec = new TextDecoder('utf-8');
      let buf = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        let i;
        while ((i = buf.indexOf('\n\n')) >= 0) {
          const evt = buf.slice(0, i); buf = buf.slice(i + 2);
          const line = evt.split('\n').find(l => l.startsWith('data:'));
          if (!line) continue;
          let o; try { o = JSON.parse(line.slice(5).trim()); } catch { continue; }
          if (o.error) { pushToast(o.error, 'error'); continue; }
          if (o.delta) setDraft(d => d + o.delta);
          if (o.done) {
            if (o.text) setDraft(o.text);
            setItems(prev => prev.map(m => m.id === sel.id ? { ...m, status: 'dang_xu_ly' } : m));
          }
        }
      }
    } catch (e) { pushToast('Soạn nháp lỗi: ' + e.message, 'error'); }
    finally { setDrafting(false); }
  }

  const cStatus = (k) => counts.byStatus?.[k] || 0;
  const cCat = (k) => counts.byCategory?.[k] || 0;

  return (
    <main className="page mail">
      <div className="page-title-block mail-head">
        <div>
          <h1 className="page-title">Hộp thư SmartMail AI</h1>
          <p className="page-sub">Đồng bộ Gmail, phân loại AI & soạn thảo phản hồi thần tốc.</p>
        </div>
        <button className="btn btn-primary" onClick={sync} disabled={syncing}>
          <Icon name="sparkle" size={14} /> {syncing ? 'Đang đồng bộ…' : 'Đồng bộ hộp thư'}
        </button>
      </div>

      <div className="mail-grid">
        {/* TRÁI: filter */}
        <aside className="mail-filters">
          <div className="mail-fgroup">
            <div className="label">Trạng thái</div>
            <button className={'mail-fitem' + (!fStatus ? ' on' : '')} onClick={() => setFStatus(null)}>
              <span>Tất cả</span><b>{counts.total}</b>
            </button>
            {_STATUS_ORDER.map(k => (
              <button key={k} className={'mail-fitem' + (fStatus === k ? ' on' : '')} onClick={() => setFStatus(k)}>
                <span>{_STATUS_VI[k]}</span><b>{cStatus(k)}</b>
              </button>
            ))}
          </div>
          <div className="mail-fgroup">
            <div className="label">Phân loại AI</div>
            <button className={'mail-fitem' + (!fCategory ? ' on' : '')} onClick={() => setFCategory(null)}>
              <span>Tất cả loại</span><b>{counts.total}</b>
            </button>
            {_CAT_ORDER.map(k => (
              <button key={k} className={'mail-fitem' + (fCategory === k ? ' on' : '')} onClick={() => setFCategory(k)}>
                <span>{_CAT_VI[k]}</span><b>{cCat(k)}</b>
              </button>
            ))}
          </div>
        </aside>

        {/* GIỮA: list */}
        <section className="mail-list">
          <div className="mail-search">
            <input className="input" placeholder="Tìm kiếm nhanh…" value={search}
              onChange={e => setSearch(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') load(); }} />
          </div>
          {loading ? (
            <div className="mail-empty"><p>Đang tải…</p></div>
          ) : items.length === 0 ? (
            <div className="mail-empty">
              <div className="mail-empty-icon"><Icon name="paper" size={26} /></div>
              <p className="mail-empty-title">Hòm thư trống</p>
              <p className="asst-hint">Bấm “Đồng bộ hộp thư” để kéo email từ Gmail.</p>
            </div>
          ) : items.map(m => (
            <button key={m.id} className={'mail-row' + (selId === m.id ? ' on' : '')} onClick={() => selectMail(m.id)}>
              <div className="mail-avatar">{_initials(m.from?.name)}</div>
              <div className="mail-row-body">
                <div className="mail-row-top">
                  <span className="mail-from">{m.from?.name || m.from?.email}</span>
                  <span className="mail-when">{_fmtWhen(m.receivedAt)}</span>
                </div>
                <div className="mail-subject">{m.subject}</div>
                <div className="mail-row-tags">
                  {m.category && <span className={'mail-tag cat-' + m.category}>{_CAT_VI[m.category]}</span>}
                  <span className={'mail-tag st-' + m.status}>{_STATUS_VI[m.status]}</span>
                </div>
              </div>
            </button>
          ))}
        </section>

        {/* PHẢI: chi tiết + soạn AI */}
        <section className="mail-detail">
          {!sel ? (
            <div className="mail-empty">
              <div className="mail-empty-icon"><Icon name="sparkle" size={26} /></div>
              <p className="mail-empty-title">Chọn một email để xem & trả lời</p>
            </div>
          ) : (
            <>
              <div className="mail-detail-head">
                <div className="mail-avatar lg">{_initials(sel.from?.name)}</div>
                <div style={{ flex: 1 }}>
                  <div className="mail-from">{sel.from?.name}</div>
                  <div className="mail-email">{sel.from?.email}</div>
                </div>
                <select className="input mail-status-sel" value={sel.status}
                  onChange={e => setStatus(sel.id, e.target.value)}>
                  {_STATUS_ORDER.map(k => <option key={k} value={k}>{_STATUS_VI[k]}</option>)}
                </select>
              </div>
              <h2 className="mail-detail-subject">{sel.subject}</h2>
              {sel.category && <span className={'mail-tag cat-' + sel.category}>{_CAT_VI[sel.category]}</span>}
              <div className="mail-detail-body">{sel.body}</div>
              {sel.aiSummary && <div className="mail-summary"><b>Tóm tắt AI:</b> {sel.aiSummary}</div>}

              <div className="mail-compose">
                <div className="card-header"><div className="card-icon"><Icon name="sparkle" size={16} /></div>
                  <h3>Bộ soạn thư trả lời tự động bằng AI</h3></div>
                <div className="mail-compose-grid">
                  <div>
                    <div className="label">Chọn ngữ điệu</div>
                    <div className="mail-tones">
                      {_TONES.map(t => (
                        <button key={t.key} className={'mail-tone' + (tone === t.key ? ' on' : '')} onClick={() => setTone(t.key)}>
                          {t.label}
                        </button>
                      ))}
                    </div>
                  </div>
                  <div>
                    <div className="label">Chỉ thị kèm thêm của nhân viên</div>
                    <textarea className="input" rows={3} value={instruction}
                      onChange={e => setInstruction(e.target.value)}
                      placeholder="Ví dụ: Giảm thêm 5%, tặng tour đảo, hẹn họp chiều mai…" />
                    <button className="btn btn-primary mail-compose-btn" onClick={composeDraft} disabled={drafting}>
                      <Icon name="sparkle" size={14} /> {drafting ? 'Đang soạn…' : 'Soạn câu trả lời cùng AI'}
                    </button>
                  </div>
                </div>
                {(draft || drafting) && (
                  <div className="mail-draft">
                    <div className="label">Nháp trả lời</div>
                    <textarea className="input mail-draft-text" rows={10} value={draft}
                      onChange={e => setDraft(e.target.value)} />
                    <button className="btn btn-ghost btn-sm" onClick={() => { navigator.clipboard?.writeText(draft); pushToast('Đã copy nháp'); }}>
                      Copy nháp
                    </button>
                  </div>
                )}
              </div>
            </>
          )}
        </section>
      </div>
    </main>
  );
}

window.MailPage = MailPage;
```

- [ ] **Step 2: Nạp script mail.jsx trong index.html**

Modify `wwwroot/index.html` — thêm sau dòng `<script type="text/babel" src="pages/assistant.jsx"></script>`:
```html
<script type="text/babel" src="pages/mail.jsx"></script>
```

- [ ] **Step 3: Thêm nav + route trong app.jsx**

Modify `wwwroot/app.jsx` — thêm vào mảng `NAV` (sau mục `/assistant`):
```jsx
  { to: '/mail',      icon: 'paper',   label: 'Hộp thư AI' },
```

Modify `wwwroot/app.jsx` — thêm `<Route>` trong `<Router>` (sau route `/assistant`):
```jsx
        <Route path="/mail"      render={() => <window.MailPage pushToast={pushToast} />} />
```

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/mail.jsx wwwroot/index.html wwwroot/app.jsx
git commit -m "feat(mail): add SmartMail AI frontend page + nav"
```

---

### Task 11: CSS + verification thủ công end-to-end

**Files:**
- Modify: `wwwroot/styles.css`

- [ ] **Step 1: Thêm style cho trang mail (3 cột, hàng email, tag, compose)**

Modify `wwwroot/styles.css` — thêm vào CUỐI file:
```css
/* ─── SmartMail AI ───────────────────────────────────────────────────────── */
.mail-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; }
.mail-grid { display: grid; grid-template-columns: 220px 360px 1fr; gap: 16px; align-items: start; }
.mail-filters { display: flex; flex-direction: column; gap: 16px; }
.mail-fgroup { background: #fff; border: 1px solid var(--border, #e5e7eb); border-radius: 10px; padding: 10px; }
.mail-fitem { display: flex; justify-content: space-between; align-items: center; width: 100%;
  background: transparent; border: 0; padding: 7px 9px; border-radius: 7px; cursor: pointer;
  font-size: 13px; color: var(--text-2, #475569); }
.mail-fitem:hover { background: #f1f5f9; }
.mail-fitem.on { background: #fff4ec; color: var(--accent, #f97316); font-weight: 600; }
.mail-fitem b { font-size: 12px; }
.mail-list { background: #fff; border: 1px solid var(--border, #e5e7eb); border-radius: 10px;
  overflow: hidden; max-height: calc(100vh - 200px); overflow-y: auto; }
.mail-search { padding: 10px; border-bottom: 1px solid var(--border, #e5e7eb); position: sticky; top: 0; background: #fff; }
.mail-row { display: flex; gap: 10px; width: 100%; text-align: left; background: transparent;
  border: 0; border-bottom: 1px solid #f1f5f9; padding: 11px 12px; cursor: pointer; }
.mail-row:hover { background: #f8fafc; }
.mail-row.on { background: #fff4ec; }
.mail-avatar { width: 34px; height: 34px; border-radius: 50%; flex: 0 0 34px; display: flex;
  align-items: center; justify-content: center; background: #6366f1; color: #fff; font-size: 12px; font-weight: 700; }
.mail-avatar.lg { width: 44px; height: 44px; flex-basis: 44px; font-size: 15px; }
.mail-row-body { flex: 1; min-width: 0; }
.mail-row-top { display: flex; justify-content: space-between; gap: 8px; }
.mail-from { font-weight: 600; font-size: 13px; color: var(--text-1, #0f172a); }
.mail-email { font-size: 12px; color: var(--text-3, #94a3b8); }
.mail-when { font-size: 11px; color: var(--text-3, #94a3b8); white-space: nowrap; }
.mail-subject { font-size: 13px; color: var(--text-2, #475569); margin: 2px 0 5px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
.mail-row-tags { display: flex; gap: 5px; flex-wrap: wrap; }
.mail-tag { font-size: 10.5px; font-weight: 600; padding: 2px 7px; border-radius: 99px; background: #eef2f6; color: #475569; }
.mail-tag.cat-khieu_nai { background: #fee2e2; color: #b91c1c; }
.mail-tag.cat-hoi_dat_tour { background: #e0e7ff; color: #4338ca; }
.mail-tag.cat-xin_bao_gia { background: #fef3c7; color: #b45309; }
.mail-tag.cat-spam { background: #f1f5f9; color: #64748b; }
.mail-tag.st-moi { background: #dbeafe; color: #1d4ed8; }
.mail-tag.st-da_phan_hoi { background: #dcfce7; color: #15803d; }
.mail-detail { background: #fff; border: 1px solid var(--border, #e5e7eb); border-radius: 10px; padding: 18px; }
.mail-detail-head { display: flex; gap: 12px; align-items: center; }
.mail-status-sel { width: auto; min-width: 130px; }
.mail-detail-subject { font-size: 18px; margin: 14px 0 8px; }
.mail-detail-body { white-space: pre-wrap; background: #f8fafc; border-radius: 8px; padding: 14px;
  font-size: 14px; color: var(--text-1, #0f172a); margin-top: 10px; }
.mail-summary { font-size: 13px; color: var(--text-2, #475569); margin-top: 10px; }
.mail-compose { margin-top: 18px; border-top: 1px solid var(--border, #e5e7eb); padding-top: 14px; }
.mail-compose-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-top: 10px; }
.mail-tones { display: flex; flex-direction: column; gap: 7px; }
.mail-tone { text-align: left; padding: 9px 11px; border: 1px solid var(--border, #e5e7eb);
  border-radius: 8px; background: #fff; cursor: pointer; font-size: 13px; }
.mail-tone.on { border-color: var(--accent, #f97316); background: #fff4ec; color: var(--accent, #f97316); font-weight: 600; }
.mail-compose-btn { width: 100%; margin-top: 10px; }
.mail-draft { margin-top: 14px; }
.mail-draft-text { width: 100%; font-family: inherit; white-space: pre-wrap; }
.mail-empty { text-align: center; padding: 48px 20px; color: var(--text-3, #94a3b8); }
.mail-empty-icon { display: flex; justify-content: center; margin-bottom: 10px; opacity: 0.5; }
.mail-empty-title { font-weight: 600; color: var(--text-2, #475569); }
@media (max-width: 1100px) { .mail-grid { grid-template-columns: 1fr; } }
```

- [ ] **Step 2: Chạy app**

Run:
```bash
dotnet run --project TourkitAiProxy.csproj
```
Expected: lắng nghe `http://localhost:5080`, không exception khởi động.

- [ ] **Step 3: Verify chưa cấu hình → sync báo lỗi rõ ràng**

Run (terminal khác):
```bash
curl -s -X POST http://localhost:5080/api/v1/mail/sync
```
Expected (khi `Mail:Gmail` để trống): JSON `{"error":"Chưa cấu hình hộp thư Gmail..."}` (HTTP 400). Xác nhận không crash.

- [ ] **Step 4: Verify với Gmail thật (cần App Password)**

Đặt env rồi chạy lại app:
```bash
export MAIL_GMAIL_ADDRESS="booking@congty.com"
export MAIL_GMAIL_APP_PASSWORD="<app-password-16-ky-tu>"
dotnet run --project TourkitAiProxy.csproj
```
Rồi:
```bash
curl -s -X POST http://localhost:5080/api/v1/mail/sync | head -c 400
```
Expected: JSON có `items` (mảng email), `counts`, `classified` ≥ 0. Mở `http://localhost:5080/#/mail` trên trình duyệt:
- [ ] Sidebar trái hiện counts theo trạng thái + 6 nhóm
- [ ] Danh sách email giữa, mỗi dòng có tag nhóm (màu) + trạng thái
- [ ] Bấm 1 email → cột phải hiện nội dung + tóm tắt AI
- [ ] Chọn ngữ điệu + nhập chỉ thị → "Soạn câu trả lời cùng AI" → chữ chạy dần (SSE), email chuyển "Đang xử lý"
- [ ] Đổi dropdown trạng thái → list cập nhật

- [ ] **Step 5: Chạy TOÀN BỘ test backend lần cuối**

Run:
```bash
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: PASS — tất cả test (MailTaxonomy + MailMapper + MailClassifier + MailRepository).

- [ ] **Step 6: Commit**

```bash
git add wwwroot/styles.css
git commit -m "feat(mail): add SmartMail AI styles + verify e2e"
```

---

### Task 12: Cập nhật tài liệu CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Thêm SmartMail AI vào phần mô tả + bảng API + layout**

Modify `CLAUDE.md`:
1. Đoạn mở đầu "Three features" → đổi thành "Four features" và thêm mô tả ngắn SmartMail AI.
2. Thêm các dòng vào bảng API surface:
```
| POST   | `/api/v1/mail/sync`               | IMAP kéo ≤30 thư mới nhất, phân loại email mới, lưu → {items, counts, classified} |
| GET    | `/api/v1/mail`                    | list + filter (`status`, `category`, `search`) + counts |
| GET    | `/api/v1/mail/{id}`               | chi tiết 1 email |
| POST   | `/api/v1/mail/{id}/reply/draft`   | SSE: stream nháp trả lời theo `{tone, instruction}` |
| PATCH  | `/api/v1/mail/{id}/status`        | đổi trạng thái email |
```
3. Thêm khối `Services/Mail/` vào sơ đồ Backend layout + `Endpoints/MailEndpoints.cs` + `data/mails.json` + `Models/MailModels.cs` + `wwwroot/pages/mail.jsx`.
4. Thêm 1 mục "## SmartMail AI feature" mô tả: nguồn IMAP (MailKit) sau `IMailSource`, phân loại chỉ email mới (cache theo presence trong repo), soạn nháp stream theo tone, file-backed `data/mails.json`, creds Gmail qua `Mail:Gmail:*` / env.

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document SmartMail AI feature in CLAUDE.md"
```

---

## Self-Review

**1. Spec coverage** (đối chiếu `docs/smartmail-ai-design.md`):
- Nguồn Gmail IMAP/SMTP MailKit → Task 7 (`GmailImapClient`) ✓
- App Password / creds config → Task 7 (`MailAccountStore`) + Task 9 (appsettings) ✓
- Đồng bộ bấm Refresh → Task 9 (`POST /mail/sync`) + Task 10 (nút Đồng bộ) ✓
- Phân loại 6 nhóm, chỉ email mới, cache → Task 5 (`MailClassifier`) + Task 9 (skip `repo.Has`) ✓
- Soạn nháp 4 ngữ điệu + chỉ thị NV → Task 8 (`MailReplyService`) + Task 10 (tones UI) ✓
- 4 trạng thái → Task 3 (`MailTaxonomy.Statuses`) + Task 9 (PATCH status) ✓
- File-backed `data/mails.json` → Task 6 (`MailRepository`) ✓
- FE 3 cột đúng mockup → Task 10 + Task 11 ✓
- NuGet MailKit → Task 1 ✓
- Hoãn: SMTP send / 2-way / OAuth → KHÔNG có task (đúng, đã hoãn) ✓
- "Của tôi" (gán nhân viên) → đã hoãn trong design; FE bỏ qua filter "Của tôi" — khớp.

**2. Placeholder scan:** Không có TBD/TODO/"handle edge cases". Mọi code step có code thật.

**3. Type consistency:**
- `MailItem` field names (Id, From, Subject, Body, ReceivedAt, IsRead, Category, Status, AiSummary, Draft) — dùng nhất quán ở MailMapper, MailRepository, MailEndpoints, mail.jsx ✓
- `MailRepository`: `Has`, `Upsert`, `Get`, `SetStatus`, `SetDraft`, `Filter`, `Counts` — gọi đúng tên ở MailEndpoints + MailReplyService ✓
- `MailCounts(Total, ByStatus, ByCategory)` → serialize camelCase → FE đọc `counts.total/byStatus/byCategory` ✓
- `DraftReplyRequest(Tone, Instruction, Provider, Model, ApiKey)` → FE gửi đúng field ✓
- `MailClassifier.ParseClassification` (static, test) + `ClassifyAsync` (instance) — endpoint gọi `ClassifyAsync` ✓
- `IMailSource.FetchRecentAsync(max, ct)` — endpoint gọi đúng ✓

Không phát hiện lệch. Plan sẵn sàng thực thi.
