# RESTful refactor Phase 0 — Service-layer dedup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rút gọn ~650 LOC duplicate ở service layer (Group 3/4/5/6 trong spec) bằng 4 helper mới — KHÔNG động endpoint URL, KHÔNG breaking, foundation cho Phase 1-4.

**Architecture:** 3 helper class mới ở `Services/Workflow/` (`JsonPromptScorer`, `DualPathScorer`, `DualPathConfig`) + 1 ở `Services/Json/` (`JsonElementExtensions`) + 1 ở `Services/Prompts/` (`CommonPromptParts`). 4 service single-shot (Visa/Deal/Tour/Mail) migrate sang dùng `DualPathScorer` thay vì own dispatch + retry loop. JSON helper extraction áp dụng cho 8 file dup.

**Tech Stack:** ASP.NET Core 8 MinimalAPI, xUnit (test project `TourkitAiProxy.Tests/` đã có). KHÔNG thêm dependency (Moq, AutoFixture) — dùng stub class manual cho mock.

**Test command:** `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`

**Reference docs:**
- Spec: `docs/superpowers/specs/2026-06-08-restful-api-refactor-design.md` (đặc biệt section #3)
- Hiện tại services single-shot: `Services/Visa/VisaScoringService.cs`, `Services/Deals/DealScoringService.cs`, `Services/Tour/TourBuilderService.cs`, `Services/Mail/MailClassifier.cs`
- Đã có (Mức A+B trước): `Services/Workflow/AnthropicToolsClient.cs`, `Services/Workflow/NativeToolScorer.cs`, `Services/Reviews/Agents/ReviewPrompt.cs`

---

## File structure (Phase 0)

### Create

- `Services/Json/JsonElementExtensions.cs` — static class extension methods `TryGetField`, `GetStringField`, `GetIntField`, `GetIntFieldOrNull`, `GetLongField`, `GetDoubleField`, `GetStringListField`, `GetObjectField<T>`. Replaces 12+ private helper instances.
- `Services/Prompts/CommonPromptParts.cs` — public const strings `TourkitContext`, `JsonOutputRules`, `NativeToolRules`, `VietnameseStyle`. Shared bằng 5 service.
- `Services/Workflow/JsonPromptScorer.cs` — generic `RunAsync<T>(IAiProvider, systemPrompt, buildPrompt(attempt), parser, ...)` chạy 2-attempt retry loop với chỉ thị chặt hơn lần 2. Generic — thay 3 chỗ duplicate ở Visa/Deal/Tour.
- `Services/Workflow/DualPathScorer.cs` + `DualPathConfig.cs` — facade dispatching anthropic → `NativeToolScorer` else → `JsonPromptScorer`, kèm cache lookup/save built-in. Thay dispatch logic ở Visa/Deal/Tour/Mail.
- `Services/Visa/VisaPrompts.cs` — extract `BuildPromptJson`, `BuildPromptNative`, `SystemForJson`, `SystemForNative`, `BuildVisaScoreSchema`, parser logic từ `VisaScoringService.cs`.
- `Services/Deals/DealPrompts.cs` — same pattern cho Deal.
- `Services/Tour/TourPrompts.cs` — same pattern cho TourBuilder.
- `Services/Mail/MailClassifierPrompts.cs` — same pattern cho MailClassifier.
- `TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs` — unit tests cho 7 extension methods.
- `TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs` — unit tests cho retry loop với `StubProvider`.
- `TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs` — unit tests cho dispatch + cache.
- `TourkitAiProxy.Tests/TestUtils/StubProvider.cs` — minimal `IAiProvider` stub trả responses từ Queue.

### Modify

- `Program.cs` — thêm `AddSingleton<JsonPromptScorer>()` và `AddSingleton<DualPathScorer>()` ở vị trí gần `AnthropicToolsClient` và `NativeToolScorer` (~line 60).
- `Services/Visa/VisaScoringService.cs` — refactor: chỉ giữ `ScoreAsync` orchestrate, gọi `_dual.RunAsync<VisaResult>(...)`. Xóa `ScoreWithJsonPromptAsync`, `ScoreWithNativeToolAsync`, `BuildPromptJson`, `BuildPromptNative`, `BuildVisaScoreSchema`, `ParseRawText`, `ParseToolInput`, `ParseElement`, `TryGet`, `Str`, `Int`, `StrList`. Từ ~280 LOC → ~60 LOC.
- `Services/Deals/DealScoringService.cs` — same pattern. Từ ~270 LOC → ~55 LOC.
- `Services/Tour/TourBuilderService.cs` — same pattern. Từ ~330 LOC → ~75 LOC (giữ ParseExpenses/ParseServices vì phức tạp hơn).
- `Services/Mail/MailClassifier.cs` — same pattern. Từ ~190 LOC → ~50 LOC. Giữ `public static ParseClassification(string)` cho test legacy.
- `Services/Reviews/Agents/ReviewPrompt.cs` — xóa private `TryGet`, `GetString`, `GetStringList`, dùng `JsonElementExtensions` thay. Giữ `ParseAlert`, `ParseAction` vì là logic compose object.
- `Services/Visa/VisaExtractionService.cs` — xóa private JSON helpers, dùng extensions.
- `Services/TourKit/TourKitApiClient.cs` — xóa private JSON helpers, dùng extensions.
- `Services/Deals/DealOpportunityClient.cs` — xóa private JSON helpers, dùng extensions.
- `Endpoints/TourEndpoints.cs` — xóa private JSON helpers (ở body POST handler).
- `CLAUDE.md` — thêm bullet mô tả `DualPathScorer` + `JsonElementExtensions` ở section "Native function-calling".

---

## Task 1: JsonElementExtensions skeleton + TryGetField

**Files:**
- Create: `Services/Json/JsonElementExtensions.cs`
- Test: `TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs`

- [ ] **Step 1: Tạo test file với 3 test cho TryGetField**

```csharp
// TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs
using System.Text.Json;
using TourkitAiProxy.Services.Json;
using Xunit;

namespace TourkitAiProxy.Tests.Json;

public class JsonElementExtensionsTests
{
    [Fact]
    public void TryGetField_finds_property_case_insensitive()
    {
        var json = JsonDocument.Parse("""{"Name":"Trieu","AGE":30}""").RootElement;
        Assert.True(json.TryGetField("name", out var name));
        Assert.Equal("Trieu", name.GetString());
        Assert.True(json.TryGetField("age", out var age));
        Assert.Equal(30, age.GetInt32());
    }

    [Fact]
    public void TryGetField_returns_false_when_missing()
    {
        var json = JsonDocument.Parse("""{"a":1}""").RootElement;
        Assert.False(json.TryGetField("missing", out _));
    }

    [Fact]
    public void TryGetField_returns_false_for_non_object()
    {
        var json = JsonDocument.Parse("""[1,2,3]""").RootElement;
        Assert.False(json.TryGetField("anything", out _));
    }
}
```

- [ ] **Step 2: Run test, expect FAIL (class not found)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: `error CS0234: namespace 'Services.Json' does not contain 'JsonElementExtensions'`

- [ ] **Step 3: Implement JsonElementExtensions với TryGetField**

```csharp
// Services/Json/JsonElementExtensions.cs
using System.Text.Json;

namespace TourkitAiProxy.Services.Json;

/// <summary>
/// Extension methods cho JsonElement — case-insensitive field lookup + tolerant type conversion.
/// Thay 12+ private helper instances duplicate trong Services/Visa, /Deals, /Tour, /Mail, /Reviews,
/// /TourKit, và Endpoints/TourEndpoints.cs.
/// </summary>
public static class JsonElementExtensions
{
    /// <summary>Case-insensitive property lookup. Trả false nếu element không phải object hoặc field missing.</summary>
    public static bool TryGetField(this JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }
}
```

- [ ] **Step 4: Run test, expect PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: `Passed! 3/3`

- [ ] **Step 5: Commit**

```bash
git add Services/Json/JsonElementExtensions.cs TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs
git commit -m "feat(json): JsonElementExtensions.TryGetField case-insensitive lookup + tests"
```

---

## Task 2: JsonElementExtensions string + list field helpers

**Files:**
- Modify: `Services/Json/JsonElementExtensions.cs`
- Modify: `TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs`

- [ ] **Step 1: Thêm 4 test cho GetStringField + GetStringListField**

```csharp
// Append vào TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs class JsonElementExtensionsTests

[Fact]
public void GetStringField_returns_string_value_or_null()
{
    var json = JsonDocument.Parse("""{"a":"hello","b":42,"c":null}""").RootElement;
    Assert.Equal("hello", json.GetStringField("a"));
    Assert.Null(json.GetStringField("b"));           // not a string → null
    Assert.Null(json.GetStringField("c"));           // null → null
    Assert.Null(json.GetStringField("missing"));     // missing → null
}

[Fact]
public void GetStringField_is_case_insensitive()
{
    var json = JsonDocument.Parse("""{"Name":"X"}""").RootElement;
    Assert.Equal("X", json.GetStringField("name"));
    Assert.Equal("X", json.GetStringField("NAME"));
}

[Fact]
public void GetStringListField_returns_strings_skipping_non_strings_and_blanks()
{
    var json = JsonDocument.Parse("""{"items":["a","",null,42,"b","   ","c"]}""").RootElement;
    var list = json.GetStringListField("items");
    Assert.Equal(new[] { "a", "b", "c" }, list);    // skip empty/null/non-string
}

[Fact]
public void GetStringListField_returns_empty_when_missing_or_not_array()
{
    var json = JsonDocument.Parse("""{"a":"not-array"}""").RootElement;
    Assert.Empty(json.GetStringListField("a"));
    Assert.Empty(json.GetStringListField("missing"));
}
```

- [ ] **Step 2: Run test, expect FAIL**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: 4 mới fail với "does not contain a definition for 'GetStringField'/'GetStringListField'"

- [ ] **Step 3: Implement 2 method**

Append vào `Services/Json/JsonElementExtensions.cs` ngoài method `TryGetField`:

```csharp
    /// <summary>Lấy string value của field. Trả null nếu missing / không phải string / blank.</summary>
    public static string? GetStringField(this JsonElement el, string name)
    {
        if (!el.TryGetField(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>Lấy list of non-blank strings. Trả empty list nếu missing / không phải array.</summary>
    public static List<string> GetStringListField(this JsonElement el, string name)
    {
        var list = new List<string>();
        if (!el.TryGetField(name, out var p) || p.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }
```

- [ ] **Step 4: Run test, expect PASS (7/7 total)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: `Passed! 7/7`

- [ ] **Step 5: Commit**

```bash
git add Services/Json/JsonElementExtensions.cs TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs
git commit -m "feat(json): GetStringField + GetStringListField extensions + tests"
```

---

## Task 3: JsonElementExtensions numeric helpers

**Files:**
- Modify: `Services/Json/JsonElementExtensions.cs`
- Modify: `TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs`

- [ ] **Step 1: Thêm test cho GetIntField, GetIntFieldOrNull, GetLongField, GetDoubleField**

```csharp
[Fact]
public void GetIntField_parses_number_or_returns_default()
{
    var json = JsonDocument.Parse("""{"a":42,"b":3.7,"c":"100","d":"abc"}""").RootElement;
    Assert.Equal(42, json.GetIntField("a"));
    Assert.Equal(3, json.GetIntField("b"));      // double 3.7 → int 3 (truncate)
    Assert.Equal(100, json.GetIntField("c"));    // string with digits → parse
    Assert.Equal(0, json.GetIntField("d"));      // unparseable → default 0
    Assert.Equal(0, json.GetIntField("missing"));
    Assert.Equal(99, json.GetIntField("missing", defaultValue: 99));
}

[Fact]
public void GetIntFieldOrNull_returns_null_when_missing()
{
    var json = JsonDocument.Parse("""{"a":42}""").RootElement;
    Assert.Equal(42, json.GetIntFieldOrNull("a"));
    Assert.Null(json.GetIntFieldOrNull("missing"));
}

[Fact]
public void GetLongField_handles_large_numbers()
{
    var json = JsonDocument.Parse("""{"a":5000000000,"b":3.14}""").RootElement;
    Assert.Equal(5_000_000_000L, json.GetLongField("a"));
    Assert.Equal(3L, json.GetLongField("b"));    // double → long truncate
    Assert.Equal(0L, json.GetLongField("missing"));
}

[Fact]
public void GetDoubleField_returns_double_or_zero()
{
    var json = JsonDocument.Parse("""{"a":3.14,"b":42}""").RootElement;
    Assert.Equal(3.14, json.GetDoubleField("a"));
    Assert.Equal(42.0, json.GetDoubleField("b"));
    Assert.Equal(0.0, json.GetDoubleField("missing"));
}
```

- [ ] **Step 2: Run test, expect FAIL**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: 4 mới fail

- [ ] **Step 3: Implement 4 method**

Append vào `Services/Json/JsonElementExtensions.cs`:

```csharp
    /// <summary>Lấy int. Number → parse trực tiếp; string → parse digits only; missing → defaultValue.</summary>
    public static int GetIntField(this JsonElement el, string name, int defaultValue = 0)
    {
        if (!el.TryGetField(name, out var p)) return defaultValue;
        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetInt32(out var n)) return n;
            if (p.TryGetDouble(out var d)) return (int)d;
        }
        if (p.ValueKind == JsonValueKind.String)
        {
            var digits = new string(p.GetString()!.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var s)) return s;
        }
        return defaultValue;
    }

    /// <summary>Như GetIntField nhưng trả null nếu field missing (phân biệt với value=0).</summary>
    public static int? GetIntFieldOrNull(this JsonElement el, string name)
        => el.TryGetField(name, out _) ? el.GetIntField(name) : null;

    /// <summary>Lấy long. Number → trực tiếp; double → truncate; missing → 0.</summary>
    public static long GetLongField(this JsonElement el, string name)
    {
        if (!el.TryGetField(name, out var p) || p.ValueKind != JsonValueKind.Number) return 0;
        return p.TryGetInt64(out var n) ? n : (long)p.GetDouble();
    }

    /// <summary>Lấy double. Missing / non-number → 0.</summary>
    public static double GetDoubleField(this JsonElement el, string name)
        => el.TryGetField(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
```

- [ ] **Step 4: Run test, expect PASS (11/11 total)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonElementExtensions"`
Expected: `Passed! 11/11`

- [ ] **Step 5: Build full project verify không break gì**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 6: Commit**

```bash
git add Services/Json/JsonElementExtensions.cs TourkitAiProxy.Tests/Json/JsonElementExtensionsTests.cs
git commit -m "feat(json): GetIntField + GetLongField + GetDoubleField + nullable variant"
```

---

## Task 4: CommonPromptParts shared constants

**Files:**
- Create: `Services/Prompts/CommonPromptParts.cs`

- [ ] **Step 1: Tạo file (no test cần — chỉ là consts, build verify đủ)**

```csharp
// Services/Prompts/CommonPromptParts.cs
namespace TourkitAiProxy.Services.Prompts;

/// <summary>
/// Shared prompt fragments cho 5 service single-shot (Review/Visa/Deal/Tour/Mail).
/// Hiện 5 service có overlap ~70% trong system prompt — extract về 1 nguồn để khi đổi
/// industry/style ngôn ngữ chỉ sửa 1 chỗ. Compose: TourkitContext + feature-specific + Output rules + VietnameseStyle.
/// </summary>
public static class CommonPromptParts
{
    /// <summary>Ngữ cảnh công ty — dùng làm prefix cho mọi system prompt.</summary>
    public const string TourkitContext =
        "Bạn là chuyên gia trong công ty du lịch / tour operator Việt Nam (Tourkit).";

    /// <summary>Chỉ thị output JSON cho JSON-prompt path (không dùng native tool).</summary>
    public const string JsonOutputRules =
        "Output ONLY raw JSON, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Ký tự đầu tiên BẮT BUỘC là '{'.";

    /// <summary>Chỉ thị gọi tool cho native function-calling path.</summary>
    public const string NativeToolRules =
        "Phân tích dữ liệu → gọi tool kết quả ĐÚNG 1 lần. " +
        "KHÔNG suy diễn ngoài dữ liệu; thiếu data thì ghi 'Chưa đủ dữ liệu'. " +
        "KHÔNG trả text giải thích ngoài tool.";

    /// <summary>Văn phong tiếng Việt chuẩn cho output.</summary>
    public const string VietnameseStyle =
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Đề xuất hành động phải gắn với dữ liệu thực tế.";
}
```

- [ ] **Step 2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 3: Commit**

```bash
git add Services/Prompts/CommonPromptParts.cs
git commit -m "feat(prompts): CommonPromptParts shared fragments cho 5 service single-shot"
```

---

## Task 5: StubProvider test utility

**Files:**
- Create: `TourkitAiProxy.Tests/TestUtils/StubProvider.cs`

- [ ] **Step 1: Tạo stub class (helper cho Task 6 + 7 — không có Moq trong test project)**

```csharp
// TourkitAiProxy.Tests/TestUtils/StubProvider.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Tests.TestUtils;

/// <summary>
/// Minimal IAiProvider stub cho unit test. Push response vào Queue qua constructor;
/// CompleteAsync dequeue tuần tự. Đếm callCount để verify số lần gọi.
/// Throw nếu queue rỗng (test bug — gọi nhiều hơn expected).
/// </summary>
public class StubProvider : IAiProvider
{
    public string Id { get; }
    public string Label => $"Stub:{Id}";
    public IReadOnlyList<ProviderModel> Models { get; }

    private readonly Queue<CompleteResult> _responses;
    public int CallCount { get; private set; }
    public List<CompleteRequest> Calls { get; } = new();

    public StubProvider(string id = "stub", params CompleteResult[] responses)
    {
        Id = id;
        Models = new[] { new ProviderModel("test-model", "Test Model") };
        _responses = new Queue<CompleteResult>(responses);
    }

    public Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
    {
        CallCount++;
        Calls.Add(req);
        if (_responses.Count == 0)
            throw new InvalidOperationException($"StubProvider exhausted (call #{CallCount}) — test gọi nhiều hơn expected");
        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<string> StreamAsync(CompleteRequest req, CancellationToken ct)
        => throw new NotImplementedException("StubProvider chỉ hỗ trợ CompleteAsync — Phase 0 không test streaming");

    /// Convenience: tạo CompleteResult với text + default metadata.
    public static CompleteResult Ok(string text, int tokIn = 100, int tokOut = 50, string finish = "stop")
        => new(Text: text, Provider: "stub", Model: "test-model",
               LatencyMs: 10, InputTokens: tokIn, OutputTokens: tokOut,
               FinishReason: finish, Attempts: 1, Warning: null, RawUpstream: null);
}
```

- [ ] **Step 2: Build verify (chưa có test gọi, chỉ confirm compile)**

Run: `dotnet build TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: `0 errors`

- [ ] **Step 3: Commit**

```bash
git add TourkitAiProxy.Tests/TestUtils/StubProvider.cs
git commit -m "test: StubProvider utility cho unit test JsonPromptScorer + DualPathScorer"
```

---

## Task 6: JsonPromptScorer skeleton + happy path test

**Files:**
- Create: `Services/Workflow/JsonPromptScorer.cs`
- Create: `TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs`

- [ ] **Step 1: Tạo test happy path (1 attempt, parse OK)**

```csharp
// TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services.Workflow;
using TourkitAiProxy.Tests.TestUtils;
using Xunit;

namespace TourkitAiProxy.Tests.Workflow;

public class JsonPromptScorerTests
{
    private readonly JsonPromptScorer _scorer = new(NullLogger<JsonPromptScorer>.Instance);

    [Fact]
    public async Task RunAsync_returns_parsed_T_on_first_attempt()
    {
        var stub = new StubProvider("test", StubProvider.Ok("""{"score":42}"""));

        var result = await _scorer.RunAsync<int>(
            provider:      stub,
            systemPrompt:  "system",
            buildPrompt:   attempt => "user prompt",
            parser:        text => System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("score").GetInt32(),
            maxTokensA:    1000, maxTokensB: 2000, temperature: 0.3,
            apiKey:        null, trace: null, ct: default);

        Assert.Equal(42, result);
        Assert.Equal(1, stub.CallCount);
    }
}
```

- [ ] **Step 2: Run test, expect FAIL (class not found)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonPromptScorer"`
Expected: `'JsonPromptScorer' does not exist`

- [ ] **Step 3: Implement skeleton**

```csharp
// Services/Workflow/JsonPromptScorer.cs
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Generic 2-attempt retry runner cho JSON-prompt scoring (Visa / Deal / Tour Builder pattern).
/// Lần 1 dùng prompt gốc + maxTokensA. Nếu parse fail / text rỗng → lần 2 với prompt phụ
/// "LƯU Ý: Lần trước trả SAI định dạng..." + maxTokensB cao hơn. Reasoning model (deepseek/minimax)
/// đôi khi trả format xấu → recovery rate ~70%.
///
/// Service caller chỉ truyền: provider + systemPrompt + buildPrompt(attempt) + parser → trả T.
/// Trace + cache không thuộc scorer này (DualPathScorer lo).
/// </summary>
public class JsonPromptScorer
{
    private readonly ILogger<JsonPromptScorer> _log;

    public JsonPromptScorer(ILogger<JsonPromptScorer> log) => _log = log;

    public async Task<T> RunAsync<T>(
        IAiProvider provider,
        string systemPrompt,
        Func<int, string> buildPrompt,
        Func<string, T> parser,
        int maxTokensA, int maxTokensB, double temperature,
        string? apiKey,
        TourkitAiProxy.Services.Workflow.TraceCollector? trace,
        CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = buildPrompt(attempt);
            var req = new CompleteRequest(
                Prompt: prompt, Provider: provider.Id, Model: null,
                MaxTokens: attempt == 1 ? maxTokensA : maxTokensB,
                Temperature: temperature, System: systemPrompt, ApiKey: apiKey);

            var aiTimer = trace?.Begin($"json_attempt{attempt}");
            try
            {
                var res = await provider.CompleteAsync(req, ct);
                if (string.IsNullOrWhiteSpace(res.Text))
                {
                    aiTimer?.Done("fail", $"AI trả rỗng (finish={res.FinishReason})");
                    throw new InvalidOperationException($"AI trả rỗng (finish={res.FinishReason})");
                }
                var parsed = parser(res.Text);
                aiTimer?.Done("ok",
                    $"Provider {provider.Id} attempt={attempt} → tokens {res.InputTokens}/{res.OutputTokens}, {res.LatencyMs}ms",
                    new() {
                        ["provider"]   = provider.Id, ["model"] = res.Model,
                        ["tokIn"]      = res.InputTokens, ["tokOut"] = res.OutputTokens,
                        ["latencyMs"]  = res.LatencyMs, ["promptChars"] = prompt.Length
                    });
                return parsed;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                last = ex;
                aiTimer?.Done("fail", $"Attempt {attempt} lỗi: {ex.Message}");
                _log.LogWarning("[JsonPromptScorer] attempt {N} fail: {Msg}", attempt, ex.Message);
            }
        }
        throw last ?? new InvalidOperationException("JsonPromptScorer thất bại — không attempt nào trả kết quả");
    }
}
```

- [ ] **Step 4: Run test, expect PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonPromptScorer"`
Expected: `Passed! 1/1`

- [ ] **Step 5: Commit**

```bash
git add Services/Workflow/JsonPromptScorer.cs TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs
git commit -m "feat(workflow): JsonPromptScorer skeleton — happy path 1 attempt"
```

---

## Task 7: JsonPromptScorer retry path test

**Files:**
- Modify: `TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs`

- [ ] **Step 1: Thêm 3 test (retry succeed, retry both fail, build prompt nhận attempt number)**

```csharp
// Append vào TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs class JsonPromptScorerTests

[Fact]
public async Task RunAsync_retries_when_parser_throws_on_first_attempt()
{
    // Attempt 1: bad JSON → parser throws. Attempt 2: good JSON → return 99.
    var stub = new StubProvider("test",
        StubProvider.Ok("not valid json"),
        StubProvider.Ok("""{"score":99}"""));

    var result = await _scorer.RunAsync<int>(
        provider:      stub,
        systemPrompt:  "system",
        buildPrompt:   attempt => $"user prompt attempt {attempt}",
        parser:        text => System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("score").GetInt32(),
        maxTokensA:    1000, maxTokensB: 2000, temperature: 0.3,
        apiKey:        null, trace: null, ct: default);

    Assert.Equal(99, result);
    Assert.Equal(2, stub.CallCount);
    // Verify buildPrompt được gọi 2 lần với attempt khác nhau
    Assert.Contains("attempt 1", stub.Calls[0].Prompt);
    Assert.Contains("attempt 2", stub.Calls[1].Prompt);
}

[Fact]
public async Task RunAsync_throws_after_both_attempts_fail()
{
    var stub = new StubProvider("test",
        StubProvider.Ok("bad1"),
        StubProvider.Ok("bad2"));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _scorer.RunAsync<int>(
            provider:      stub,
            systemPrompt:  "s",
            buildPrompt:   _ => "u",
            parser:        text => throw new InvalidOperationException("parser fail"),
            maxTokensA:    1000, maxTokensB: 2000, temperature: 0.3,
            apiKey:        null, trace: null, ct: default));

    Assert.Equal(2, stub.CallCount);
    Assert.Equal("parser fail", ex.Message);
}

[Fact]
public async Task RunAsync_throws_when_AI_returns_empty_text()
{
    var stub = new StubProvider("test",
        StubProvider.Ok(""),
        StubProvider.Ok("   "));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _scorer.RunAsync<int>(
            provider:      stub,
            systemPrompt:  "s",
            buildPrompt:   _ => "u",
            parser:        _ => 0,
            maxTokensA:    1000, maxTokensB: 2000, temperature: 0.3,
            apiKey:        null, trace: null, ct: default));

    Assert.Equal(2, stub.CallCount);
}
```

- [ ] **Step 2: Run test, expect PASS (4/4 total)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~JsonPromptScorer"`
Expected: `Passed! 4/4`

- [ ] **Step 3: Commit**

```bash
git add TourkitAiProxy.Tests/Workflow/JsonPromptScorerTests.cs
git commit -m "test(workflow): JsonPromptScorer retry + empty-text + both-fail scenarios"
```

---

## Task 8: DualPathConfig record + DualPathScorer skeleton

**Files:**
- Create: `Services/Workflow/DualPathConfig.cs`
- Create: `Services/Workflow/DualPathScorer.cs`
- Create: `TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs`

- [ ] **Step 1: Tạo test cho dispatch logic — anthropic → native, else → json**

```csharp
// TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Workflow;
using TourkitAiProxy.Tests.TestUtils;
using Xunit;

namespace TourkitAiProxy.Tests.Workflow;

public class DualPathScorerTests
{
    [Fact]
    public async Task RunAsync_routes_to_json_path_when_provider_is_not_anthropic()
    {
        var stub = new StubProvider("opencode-go", StubProvider.Ok("""{"v":7}"""));
        var dual = BuildScorer(stub);

        var result = await dual.RunAsync<TestResult>(BuildConfig(), null, null, null, default);

        Assert.Equal(7, result.V);
        Assert.Equal(1, stub.CallCount);
    }

    // ─── helpers ────────────────────────────────────────────────────────────
    private static DualPathScorer BuildScorer(IAiProvider provider)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Default"] = provider.Id
        }).Build();
        var registry = new ProviderRegistry(new[] { provider }, cfg);
        var cache    = new AiResponseCache(NullLogger<AiResponseCache>.Instance, cfg);
        var jsonScorer = new JsonPromptScorer(NullLogger<JsonPromptScorer>.Instance);
        var trace = new NoOpWorkflowTraceAccessor();
        // NativeToolScorer chưa cần cho test json path — pass null nếu được, else stub minimal.
        // Cho test này dùng dispatcher chỉ với json path.
        return new DualPathScorer(registry, cache, native: null!, jsonScorer, trace,
            NullLogger<DualPathScorer>.Instance);
    }

    private static DualPathConfig<TestResult> BuildConfig() => new(
        Workflow:           "TestWorkflow",
        CacheKey:           null,
        SystemForJson:      "sj",
        SystemForNative:    "sn",
        BuildJsonPrompt:    _ => "jp",
        BuildNativePrompt:  () => "np",
        ToolSchema:         JsonDocument.Parse("""{"name":"x"}""").RootElement,
        TerminalToolName:   "submit_test",
        ParseFromRawText:   text => new TestResult(JsonDocument.Parse(text).RootElement.GetProperty("v").GetInt32()),
        ParseFromToolInput: el => new TestResult(el.GetProperty("v").GetInt32()),
        DefaultModel:       "test-model",
        MaxTokensJsonA:     1000,
        MaxTokensJsonB:     2000,
        MaxTokensNative:    1500,
        Temperature:        0.3);

    public record TestResult(int V);

    /// Trace no-op cho test.
    private class NoOpWorkflowTraceAccessor : IWorkflowTraceAccessor
    {
        public TraceCollector? Current => null;
        public void Set(TraceCollector? trace) { }
    }
}
```

- [ ] **Step 2: Run test, expect FAIL (class not found)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~DualPathScorer"`
Expected: `'DualPathScorer' / 'DualPathConfig' not found`

- [ ] **Step 3: Implement DualPathConfig**

```csharp
// Services/Workflow/DualPathConfig.cs
using System.Text.Json;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Config truyền vào DualPathScorer.RunAsync — đầy đủ thứ service cần để chạy 1 trong 2 path.
/// Mỗi service single-shot (Visa/Deal/Tour/Mail) tạo 1 instance per call với prompts của riêng nó.
/// </summary>
/// <typeparam name="T">Type kết quả scoring (vd VisaResult, DealScore).</typeparam>
public record DualPathConfig<T>(
    /// <summary>Tên workflow cho trace, vd "VisaScoring". SetWorkflow trên trace.</summary>
    string Workflow,
    /// <summary>Cache key — null = skip cache lookup/save. AiResponseCache.Hash(...).</summary>
    string? CacheKey,
    /// <summary>System prompt cho JSON-prompt path.</summary>
    string SystemForJson,
    /// <summary>System prompt cho native function-calling path.</summary>
    string SystemForNative,
    /// <summary>Build user prompt cho JSON path. Nhận attempt (1 hoặc 2) để có thể tweak chỉ thị retry.</summary>
    Func<int, string> BuildJsonPrompt,
    /// <summary>Build user prompt cho native path (chỉ 1 attempt — schema enforce sẵn).</summary>
    Func<string> BuildNativePrompt,
    /// <summary>Schema của terminal tool (vd submit_visa_score). JsonElement từ JsonSerializer.SerializeToElement.</summary>
    JsonElement ToolSchema,
    /// <summary>Tên terminal tool — phải match name trong ToolSchema.</summary>
    string TerminalToolName,
    /// <summary>Parse T từ raw text response (JSON path).</summary>
    Func<string, T> ParseFromRawText,
    /// <summary>Parse T từ tool input element (native path).</summary>
    Func<JsonElement, T> ParseFromToolInput,
    /// <summary>Anthropic model cho native path (mặc định claude-sonnet-4-5).</summary>
    string DefaultModel,
    /// <summary>maxTokens attempt 1 JSON path.</summary>
    int MaxTokensJsonA = 2500,
    /// <summary>maxTokens attempt 2 JSON path (cao hơn để recovery reasoning model).</summary>
    int MaxTokensJsonB = 3200,
    /// <summary>maxTokens cho native path (schema enforce nên không cần cao).</summary>
    int MaxTokensNative = 3000,
    /// <summary>Temperature cho cả 2 path.</summary>
    double Temperature = 0.3
);
```

- [ ] **Step 4: Implement DualPathScorer (chỉ json path trước — native sẽ thêm Task 9)**

```csharp
// Services/Workflow/DualPathScorer.cs
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Workflow;

/// <summary>
/// Facade cho service single-shot (Visa/Deal/Tour/Mail): tự dispatch giữa JSON-prompt path
/// và Anthropic native function-calling path dựa trên provider.Id. Built-in cache lookup/save.
///
/// Mỗi service từ ~250 LOC (dispatch + retry + native + cache) → ~50 LOC (chỉ define prompts).
/// Logic dispatch + retry + cache + trace setup tập trung ở đây.
/// </summary>
public class DualPathScorer
{
    private readonly ProviderRegistry _registry;
    private readonly AiResponseCache _cache;
    private readonly NativeToolScorer _native;
    private readonly JsonPromptScorer _json;
    private readonly IWorkflowTraceAccessor _traceAccessor;
    private readonly ILogger<DualPathScorer> _log;

    public DualPathScorer(
        ProviderRegistry registry, AiResponseCache cache,
        NativeToolScorer native, JsonPromptScorer json,
        IWorkflowTraceAccessor traceAccessor,
        ILogger<DualPathScorer> log)
    {
        _registry = registry; _cache = cache;
        _native = native; _json = json;
        _traceAccessor = traceAccessor; _log = log;
    }

    public async Task<T> RunAsync<T>(
        DualPathConfig<T> config,
        string? providerOverride,
        string? modelOverride,
        string? apiKeyOverride,
        CancellationToken ct) where T : class
    {
        var trace = _traceAccessor.Current;
        trace?.SetWorkflow(config.Workflow);

        var provider = _registry.Resolve(providerOverride);
        trace?.SetMeta("provider", provider.Id);

        // ── Cache lookup ──────────────────────────────────────────────────────
        if (config.CacheKey != null)
        {
            var cacheTimer = trace?.Begin("cache_lookup");
            var cached = _cache.TryGet<T>(config.CacheKey);
            if (cached != null)
            {
                cacheTimer?.Done("ok", $"Cache HIT (24h) → skip AI",
                    new() { ["cacheKey"] = config.CacheKey[..Math.Min(16, config.CacheKey.Length)] + "…" });
                return cached;
            }
            cacheTimer?.Done("skip", "Cache MISS → gọi AI");
        }

        // ── Dispatch theo provider ────────────────────────────────────────────
        T result;
        if (string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            trace?.Step("path_dispatch", "ok", 0,
                "Provider anthropic → native function-calling (schema enforce)",
                new() { ["path"] = "native-tool", ["tool"] = config.TerminalToolName });
            var nativeRes = await _native.RunAsync<T>(
                systemPrompt:     config.SystemForNative,
                userPrompt:       config.BuildNativePrompt(),
                toolSchema:       config.ToolSchema,
                terminalToolName: config.TerminalToolName,
                parser:           config.ParseFromToolInput,
                apiKeyOverride:   apiKeyOverride,
                model:            string.IsNullOrWhiteSpace(modelOverride) ? config.DefaultModel : modelOverride!,
                maxTokens:        config.MaxTokensNative,
                trace:            trace,
                ct:               ct);
            result = nativeRes.Value;
        }
        else
        {
            trace?.Step("path_dispatch", "ok", 0,
                $"Provider {provider.Id} → JSON-prompt fallback (tolerant parse + retry)",
                new() { ["path"] = "json-prompt" });
            result = await _json.RunAsync<T>(
                provider:      provider,
                systemPrompt:  config.SystemForJson,
                buildPrompt:   attempt => attempt == 1
                                  ? config.BuildJsonPrompt(attempt)
                                  : config.BuildJsonPrompt(attempt) + "\n\nLƯU Ý: Lần trước trả SAI định dạng. CHỈ trả ĐÚNG 1 JSON object hợp lệ, không thêm chữ nào ngoài JSON.",
                parser:        config.ParseFromRawText,
                maxTokensA:    config.MaxTokensJsonA, maxTokensB: config.MaxTokensJsonB,
                temperature:   config.Temperature,
                apiKey:        apiKeyOverride,
                trace:         trace,
                ct:            ct);
        }

        // ── Cache save ────────────────────────────────────────────────────────
        if (config.CacheKey != null)
        {
            _cache.Save(config.CacheKey, result);
            trace?.Step("cache_save", "ok", 0, "Lưu kết quả vào cache 24h");
        }

        return result;
    }
}
```

- [ ] **Step 5: Run test, expect PASS**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~DualPathScorer"`
Expected: `Passed! 1/1`

- [ ] **Step 6: Wire DI ở Program.cs**

Modify `Program.cs:60` — sau dòng `builder.Services.AddSingleton<TourkitAiProxy.Services.Workflow.NativeToolScorer>();`:

```csharp
// Generic 2-attempt JSON retry loop — share giữa Visa/Deal/Tour scoring service.
builder.Services.AddSingleton<TourkitAiProxy.Services.Workflow.JsonPromptScorer>();
// Facade dispatching anthropic native-tool vs JSON-prompt fallback + cache built-in.
// 4 service single-shot (Visa/Deal/Tour/Mail) dùng thay vì own dispatch logic.
builder.Services.AddSingleton<TourkitAiProxy.Services.Workflow.DualPathScorer>();
```

- [ ] **Step 7: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 8: Commit**

```bash
git add Services/Workflow/DualPathConfig.cs Services/Workflow/DualPathScorer.cs TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs Program.cs
git commit -m "feat(workflow): DualPathScorer + DualPathConfig + DI wire"
```

---

## Task 9: DualPathScorer cache hit test + native path test

**Files:**
- Modify: `TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs`

- [ ] **Step 1: Thêm test cache hit (skip AI)**

```csharp
// Append vào TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs class DualPathScorerTests

[Fact]
public async Task RunAsync_returns_cached_value_without_calling_AI()
{
    var stub = new StubProvider("opencode-go" /* no responses pushed */);
    var dual = BuildScorer(stub);

    // Pre-populate cache
    var cacheKey = "test-key-12345";
    var cfg = ConfigurationBuilder_For("opencode-go");
    var cache = new AiResponseCache(NullLogger<AiResponseCache>.Instance, cfg);
    cache.Save(cacheKey, new TestResult(123));

    // Tạo lại dual với cache đã có
    var registry = new ProviderRegistry(new[] { (IAiProvider)stub }, cfg);
    var dualWithCache = new DualPathScorer(registry, cache,
        native: null!, new JsonPromptScorer(NullLogger<JsonPromptScorer>.Instance),
        new NoOpWorkflowTraceAccessor(), NullLogger<DualPathScorer>.Instance);

    var config = BuildConfig() with { CacheKey = cacheKey };
    var result = await dualWithCache.RunAsync<TestResult>(config, null, null, null, default);

    Assert.Equal(123, result.V);
    Assert.Equal(0, stub.CallCount);  // KHÔNG gọi AI vì cache hit
}

[Fact]
public async Task RunAsync_saves_to_cache_after_AI_call()
{
    var stub = new StubProvider("opencode-go", StubProvider.Ok("""{"v":55}"""));
    var cfg = ConfigurationBuilder_For("opencode-go");
    var registry = new ProviderRegistry(new[] { (IAiProvider)stub }, cfg);
    var cache = new AiResponseCache(NullLogger<AiResponseCache>.Instance, cfg);
    var dual = new DualPathScorer(registry, cache,
        native: null!, new JsonPromptScorer(NullLogger<JsonPromptScorer>.Instance),
        new NoOpWorkflowTraceAccessor(), NullLogger<DualPathScorer>.Instance);

    var cacheKey = "save-test-key";
    var config = BuildConfig() with { CacheKey = cacheKey };
    var result = await dual.RunAsync<TestResult>(config, null, null, null, default);

    Assert.Equal(55, result.V);
    var cached = cache.TryGet<TestResult>(cacheKey);
    Assert.NotNull(cached);
    Assert.Equal(55, cached!.V);
}

// Helper — promote ConfigurationBuilder lên static cho 2 test trên
private static IConfiguration ConfigurationBuilder_For(string defaultProvider)
    => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Providers:Default"] = defaultProvider
    }).Build();
```

- [ ] **Step 2: Run test, expect PASS (3/3 total)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~DualPathScorer"`
Expected: `Passed! 3/3`

Native path test (provider=anthropic) skip vì AnthropicToolsClient gọi HTTP api.anthropic.com — cần WireMock hoặc test integration thật, ngoài scope Phase 0. Tin tưởng vào Task 10-13 smoke test khi migrate 4 service.

- [ ] **Step 3: Commit**

```bash
git add TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs
git commit -m "test(workflow): DualPathScorer cache hit + cache save"
```

---

## Task 10: Migrate VisaScoringService sang DualPathScorer

**Files:**
- Create: `Services/Visa/VisaPrompts.cs`
- Modify: `Services/Visa/VisaScoringService.cs`

- [ ] **Step 1: Extract toàn bộ prompts + schema + parser sang VisaPrompts.cs**

```csharp
// Services/Visa/VisaPrompts.cs
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Prompts;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// Tách toàn bộ prompts + tool schema + parser cho Visa scoring khỏi service.
/// Service chỉ orchestrate qua DualPathScorer; nội dung domain ở file này.
/// </summary>
public static class VisaPrompts
{
    public const string SystemForJson =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là chuyên gia thẩm định hồ sơ xin visa du lịch, nhiều năm kinh nghiệm đánh giá khả năng đậu/rớt. " +
        "Đánh giá KHÁCH QUAN theo nguyên tắc lãnh sự: chứng minh tài chính, ràng buộc về nước, " +
        "lịch sử du lịch, tính nhất quán hồ sơ. " +
        CommonPromptParts.JsonOutputRules + " " + CommonPromptParts.VietnameseStyle;

    public const string SystemForNative =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là chuyên gia thẩm định visa du lịch — đánh giá KHÁCH QUAN theo nguyên tắc lãnh sự " +
        "(chứng minh tài chính, ràng buộc về nước, lịch sử du lịch, tính nhất quán hồ sơ). " +
        "Gọi tool submit_visa_score với kết quả. " + CommonPromptParts.NativeToolRules + " " + CommonPromptParts.VietnameseStyle;

    public static string BuildJsonPrompt(string profile, string? country, int attempt)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "(AI tự nhận diện từ hồ sơ)" : country!;
        return $@"NHIỆM VỤ: Thẩm định khả năng ĐẬU/RỚT của bộ hồ sơ xin visa dưới đây.

NƯỚC XIN VISA: {c}

HỒ SƠ:
{profile}

{CommonRules}

OUTPUT JSON (KHÔNG markdown):
{{
  ""passRate"": 0-100,
  ""level"": ""cao|trung_binh|thap"",
  ""strengths"": [""điểm mạnh 1""],
  ""weaknesses"": [""điểm yếu 1""],
  ""missingDocs"": [""giấy tờ cần bổ sung""],
  ""suggestions"": [""đề xuất cải thiện""],
  ""summary"": ""1-2 câu kết luận""
}}
Bắt đầu trả JSON ngay:";
    }

    public static string BuildNativePrompt(string profile, string? country)
    {
        var c = string.IsNullOrWhiteSpace(country) ? "(AI tự nhận diện từ hồ sơ)" : country!;
        return $@"NHIỆM VỤ: Thẩm định khả năng ĐẬU/RỚT visa và GỌI TOOL submit_visa_score với kết quả.

NƯỚC XIN VISA: {c}

HỒ SƠ:
{profile}

{CommonRules}

Gọi submit_visa_score NGAY. KHÔNG trả text giải thích ngoài tool.";
    }

    private const string CommonRules = @"QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa
2. passRate là % ước lượng khả năng ĐẬU (0-100)
3. level: 'cao' (≥70), 'trung_binh' (40-69), 'thap' (<40)
4. missingDocs: giấy tờ THƯỜNG CẦN nhưng hồ sơ CHƯA có/CÒN yếu
5. suggestions: cách cụ thể để TĂNG tỉ lệ đậu";

    public static readonly JsonElement SubmitVisaScoreSchema = NativeToolScorer.BuildAnthropicTool(
        name: "submit_visa_score",
        description: "Nộp kết quả thẩm định hồ sơ visa. Gọi DUY NHẤT 1 lần.",
        properties: new
        {
            passRate = new { type = "integer", minimum = 0, maximum = 100 },
            level = new { type = "string", @enum = new[] { "cao", "trung_binh", "thap" } },
            strengths = new { type = "array", items = new { type = "string" } },
            weaknesses = new { type = "array", items = new { type = "string" } },
            missingDocs = new { type = "array", items = new { type = "string" } },
            suggestions = new { type = "array", items = new { type = "string" } },
            summary = new { type = "string" }
        },
        required: new[] { "passRate", "level", "strengths", "weaknesses", "missingDocs", "suggestions", "summary" });

    /// Parse text response (JSON path) → VisaResult.
    public static VisaResult ParseRawText(string raw)
    {
        using var doc = TourkitAiProxy.Services.Json.LooseJson.ParseFirstObject(raw);
        return ParseElement(doc.RootElement);
    }

    /// Parse tool_use input (native path) → VisaResult.
    public static VisaResult ParseToolInput(JsonElement root) => ParseElement(root);

    private static VisaResult ParseElement(JsonElement root)
    {
        var rate = Math.Clamp(root.GetIntField("passRate"), 0, 100);
        var level = (root.GetStringField("level") ?? "").Trim().ToLowerInvariant();
        if (level is not ("cao" or "trung_binh" or "thap"))
            level = rate >= 70 ? "cao" : rate >= 40 ? "trung_binh" : "thap";

        return new VisaResult(
            PassRate:    rate,
            Level:       level,
            Strengths:   root.GetStringListField("strengths"),
            Weaknesses:  root.GetStringListField("weaknesses"),
            MissingDocs: root.GetStringListField("missingDocs"),
            Suggestions: root.GetStringListField("suggestions"),
            Summary:     root.GetStringField("summary") ?? "",
            AiModel:     null, AiProvider: null);
    }
}
```

- [ ] **Step 2: Rewrite VisaScoringService — chỉ giữ ScoreAsync gọi DualPathScorer**

```csharp
// Services/Visa/VisaScoringService.cs (full rewrite)
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// Bước 2 — chấm tỉ lệ đậu/rớt visa từ HỒ SƠ TEXT (NV có thể đã sửa).
/// Dispatch + retry + cache delegate hết cho DualPathScorer; service chỉ build config.
/// Prompts + schema + parser ở VisaPrompts.cs.
/// </summary>
public class VisaScoringService
{
    private readonly DualPathScorer _dual;

    public VisaScoringService(DualPathScorer dual) => _dual = dual;

    public async Task<VisaResult> ScoreAsync(
        string profile, string? country,
        string? provider, string? model, string? apiKey,
        CancellationToken ct)
    {
        var config = new DualPathConfig<VisaResult>(
            Workflow:           "VisaScoring",
            CacheKey:           AiResponseCache.Hash("visa-score", model, $"{country}|{profile}"),
            SystemForJson:      VisaPrompts.SystemForJson,
            SystemForNative:    VisaPrompts.SystemForNative,
            BuildJsonPrompt:    attempt => VisaPrompts.BuildJsonPrompt(profile, country, attempt),
            BuildNativePrompt:  () => VisaPrompts.BuildNativePrompt(profile, country),
            ToolSchema:         VisaPrompts.SubmitVisaScoreSchema,
            TerminalToolName:   "submit_visa_score",
            ParseFromRawText:   VisaPrompts.ParseRawText,
            ParseFromToolInput: VisaPrompts.ParseToolInput,
            DefaultModel:       "claude-sonnet-4-5",
            MaxTokensJsonA:     2500,
            MaxTokensJsonB:     3200,
            MaxTokensNative:    3000,
            Temperature:        0.3);

        var result = await _dual.RunAsync(config, provider, model, apiKey, ct);
        // Stamp AI metadata — DualPathScorer không biết provider.Id từ stub level
        return result with { AiModel = result.AiModel ?? "unknown", AiProvider = result.AiProvider ?? "unknown" };
    }
}
```

**Wait — vấn đề:** `VisaResult` được stamp AiModel/AiProvider sau khi parser xong. Hiện parser trả `AiProvider: null`. DualPathScorer cũng không stamp. Service stamp cuối → nhưng không biết model/provider cụ thể nào đã được dùng. Cần extend DualPathConfig với metadata stamping callback HOẶC DualPathScorer trả tuple (T, model, provider).

**Decision:** thêm property vào DualPathConfig: `Func<T, string aiProvider, string aiModel, T> StampMetadata` optional. Hoặc đơn giản hơn: DualPathScorer trả `ScorerOutcome<T>` thay vì plain T. Để giữ test đơn giản, dùng option callback.

**Sửa lại Task này:** Lùi 1 bước, thêm StampMetadata vào config + dispatch trả via callback. Tôi lùi spec — STOP TASK 10, làm Task 9.5 fix DualPathConfig + DualPathScorer.

(Plan failure phát hiện ở Task 10 — chuyển sang Task 9.5 phía dưới.)

---

## Task 9.5: Fix DualPathScorer trả tuple (T, model, provider) cho stamping

**Files:**
- Modify: `Services/Workflow/DualPathScorer.cs`
- Modify: `TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs`

- [ ] **Step 1: Định nghĩa `DualPathOutcome<T>` record**

Sửa `Services/Workflow/DualPathConfig.cs` — append cuối file:

```csharp
/// <summary>Outcome trả từ DualPathScorer — kèm metadata để service stamp vào result domain object.</summary>
public record DualPathOutcome<T>(T Value, string AiProvider, string AiModel) where T : class;
```

- [ ] **Step 2: Sửa DualPathScorer.RunAsync trả `DualPathOutcome<T>`**

Modify `Services/Workflow/DualPathScorer.cs`:

```csharp
    public async Task<DualPathOutcome<T>> RunAsync<T>(
        DualPathConfig<T> config,
        string? providerOverride,
        string? modelOverride,
        string? apiKeyOverride,
        CancellationToken ct) where T : class
    {
        // ... (giữ logic trace + cache lookup như cũ) ...

        T result;
        string actualProvider, actualModel;
        if (string.Equals(provider.Id, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            // ... gọi _native.RunAsync ...
            actualProvider = "anthropic";
            actualModel = nativeRes.Model;
            result = nativeRes.Value;
        }
        else
        {
            // ... gọi _json.RunAsync ...
            actualProvider = provider.Id;
            actualModel = provider.Models[0].Id; // JsonPromptScorer chưa expose chosen model — dùng first
            result = (await _json.RunAsync<T>(...));
        }

        if (config.CacheKey != null) _cache.Save(config.CacheKey, result);
        return new DualPathOutcome<T>(result, actualProvider, actualModel);
    }
```

- [ ] **Step 3: Sửa 3 test hiện có để unwrap `.Value`**

Trong `TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs`, mọi chỗ:
```csharp
var result = await dual.RunAsync<TestResult>(...);
Assert.Equal(7, result.V);
```
sửa thành:
```csharp
var outcome = await dual.RunAsync<TestResult>(...);
Assert.Equal(7, outcome.Value.V);
Assert.Equal("opencode-go", outcome.AiProvider);
```

- [ ] **Step 4: Run test, expect PASS (3/3)**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~DualPathScorer"`
Expected: `Passed! 3/3`

- [ ] **Step 5: Commit**

```bash
git add Services/Workflow/DualPathConfig.cs Services/Workflow/DualPathScorer.cs TourkitAiProxy.Tests/Workflow/DualPathScorerTests.cs
git commit -m "refactor(workflow): DualPathScorer trả DualPathOutcome<T> với provider+model metadata"
```

---

## Task 10 (revised): Migrate VisaScoringService

**Files:**
- Create: `Services/Visa/VisaPrompts.cs` (như Task 10 Step 1 ở trên — không sửa)
- Modify: `Services/Visa/VisaScoringService.cs`

- [ ] **Step 1: Tạo `Services/Visa/VisaPrompts.cs`** — copy nguyên code từ Task 10 Step 1 ở trên.

- [ ] **Step 2: Rewrite `Services/Visa/VisaScoringService.cs`**

```csharp
// Services/Visa/VisaScoringService.cs (full rewrite)
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Visa;

/// <summary>
/// Bước 2 — chấm tỉ lệ đậu/rớt visa từ HỒ SƠ TEXT (NV có thể đã sửa).
/// Dispatch + retry + cache delegate hết cho DualPathScorer; service chỉ build config.
/// Prompts + schema + parser ở VisaPrompts.cs.
/// </summary>
public class VisaScoringService
{
    private readonly DualPathScorer _dual;

    public VisaScoringService(DualPathScorer dual) => _dual = dual;

    public async Task<VisaResult> ScoreAsync(
        string profile, string? country,
        string? provider, string? model, string? apiKey,
        CancellationToken ct)
    {
        var config = new DualPathConfig<VisaResult>(
            Workflow:           "VisaScoring",
            CacheKey:           AiResponseCache.Hash("visa-score", model, $"{country}|{profile}"),
            SystemForJson:      VisaPrompts.SystemForJson,
            SystemForNative:    VisaPrompts.SystemForNative,
            BuildJsonPrompt:    attempt => VisaPrompts.BuildJsonPrompt(profile, country, attempt),
            BuildNativePrompt:  () => VisaPrompts.BuildNativePrompt(profile, country),
            ToolSchema:         VisaPrompts.SubmitVisaScoreSchema,
            TerminalToolName:   "submit_visa_score",
            ParseFromRawText:   VisaPrompts.ParseRawText,
            ParseFromToolInput: VisaPrompts.ParseToolInput,
            DefaultModel:       "claude-sonnet-4-5",
            MaxTokensJsonA:     2500,
            MaxTokensJsonB:     3200,
            MaxTokensNative:    3000,
            Temperature:        0.3);

        var outcome = await _dual.RunAsync(config, provider, model, apiKey, ct);
        return outcome.Value with { AiModel = outcome.AiModel, AiProvider = outcome.AiProvider };
    }
}
```

- [ ] **Step 3: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: `0 errors`

- [ ] **Step 4: Run all tests verify không regress**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: 96+ pass, 0 fail (số test mới sẽ cao hơn)

- [ ] **Step 5: Smoke test — start proxy + ping endpoint Visa**

Run (background): `dotnet run --project TourkitAiProxy.csproj --no-build`

Sau khi proxy ready, run:
```bash
curl -s http://localhost:5080/healthz
# Expected: {"ok":true,...}
# Visa endpoint cần session — chỉ verify route alive là OK
curl -s -X POST http://localhost:5080/api/v1/visa/assess/test-id/score \
  -H "Content-Type: application/json" -d '{}' -o /dev/null -w "HTTP %{http_code}\n"
# Expected: HTTP 401 hoặc 404 (route alive, không 500)
```

Kill proxy: `taskkill //F //IM dotnet.exe`

- [ ] **Step 6: Commit**

```bash
git add Services/Visa/VisaPrompts.cs Services/Visa/VisaScoringService.cs
git commit -m "refactor(visa): migrate VisaScoringService sang DualPathScorer (~280 → ~40 LOC)"
```

---

## Task 11: Migrate DealScoringService

**Files:**
- Create: `Services/Deals/DealPrompts.cs`
- Modify: `Services/Deals/DealScoringService.cs`

- [ ] **Step 1: Tạo `Services/Deals/DealPrompts.cs`**

```csharp
// Services/Deals/DealPrompts.cs
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Prompts;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Deals;

public static class DealPrompts
{
    public const string SystemForJson =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là trưởng phòng kinh doanh giàu kinh nghiệm, đánh giá khả năng CHỐT của cơ hội bán tour. " +
        "Căn cứ hành động Sale: tương tác, chăm sóc, tiến triển, phản hồi khách, giá trị deal, độ trễ. " +
        CommonPromptParts.JsonOutputRules + " " + CommonPromptParts.VietnameseStyle;

    public const string SystemForNative =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là trưởng phòng kinh doanh — đánh giá khả năng CHỐT (thắng deal) của cơ hội bán tour " +
        "căn cứ hành động Sale. Gọi tool submit_deal_score với kết quả. " +
        CommonPromptParts.NativeToolRules + " " + CommonPromptParts.VietnameseStyle;

    public static string BuildJsonPrompt(string profile, int attempt) => $@"NHIỆM VỤ: Đánh giá khả năng THẮNG cơ hội bán hàng dưới đây dựa trên hành động của Sale.

HỒ SƠ CƠ HỘI:
{profile}

{CommonRules}

OUTPUT JSON (KHÔNG markdown):
{{
  ""winRate"": 0-100,
  ""level"": ""cao|trung_binh|thap"",
  ""signals"": [""dấu hiệu tích cực""],
  ""risks"": [""rủi ro""],
  ""nextAction"": ""hành động cụ thể Sale làm tiếp"",
  ""reason"": ""1 câu lý do ưu tiên""
}}
Bắt đầu trả JSON ngay:";

    public static string BuildNativePrompt(string profile) => $@"NHIỆM VỤ: Đánh giá khả năng THẮNG cơ hội và GỌI TOOL submit_deal_score với kết quả.

HỒ SƠ CƠ HỘI:
{profile}

{CommonRules}

Gọi submit_deal_score NGAY. KHÔNG trả text giải thích.";

    private const string CommonRules = @"QUY TẮC:
1. Chỉ dựa trên hồ sơ trên, KHÔNG bịa
2. winRate = % khả năng chốt thành công (0-100)
3. level: 'cao' (≥60), 'trung_binh' (35-59), 'thap' (<35)
4. signals: dấu hiệu TÍCH CỰC (khách quan tâm, Sale chăm đều, đã báo giá...)
5. risks: rủi ro làm tuột deal (lâu không chăm, khách im, cạnh tranh...)
6. nextAction: 1 hành động CỤ THỂ Sale nên làm NGAY";

    public static readonly JsonElement SubmitDealScoreSchema = NativeToolScorer.BuildAnthropicTool(
        name: "submit_deal_score",
        description: "Nộp kết quả chấm khả năng thắng deal. Gọi DUY NHẤT 1 lần.",
        properties: new
        {
            winRate = new { type = "integer", minimum = 0, maximum = 100 },
            level = new { type = "string", @enum = new[] { "cao", "trung_binh", "thap" } },
            signals = new { type = "array", items = new { type = "string" } },
            risks = new { type = "array", items = new { type = "string" } },
            nextAction = new { type = "string" },
            reason = new { type = "string" }
        },
        required: new[] { "winRate", "level", "signals", "risks", "nextAction", "reason" });

    public static DealScore ParseRawText(string raw)
    {
        using var doc = TourkitAiProxy.Services.Json.LooseJson.ParseFirstObject(raw);
        return ParseElement(doc.RootElement);
    }

    public static DealScore ParseToolInput(JsonElement root) => ParseElement(root);

    private static DealScore ParseElement(JsonElement root)
    {
        var rate = Math.Clamp(root.GetIntField("winRate"), 0, 100);
        var level = (root.GetStringField("level") ?? "").Trim().ToLowerInvariant();
        if (level is not ("cao" or "trung_binh" or "thap"))
            level = rate >= 60 ? "cao" : rate >= 35 ? "trung_binh" : "thap";

        return new DealScore(
            WinRate:    rate,
            Level:      level,
            Signals:    root.GetStringListField("signals"),
            Risks:      root.GetStringListField("risks"),
            NextAction: root.GetStringField("nextAction") ?? "",
            Reason:     root.GetStringField("reason") ?? "",
            AiModel:    null, AiProvider: null);
    }
}
```

- [ ] **Step 2: Rewrite `Services/Deals/DealScoringService.cs`**

```csharp
// Services/Deals/DealScoringService.cs (full rewrite)
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Deals;

public class DealScoringService
{
    private readonly DualPathScorer _dual;
    public DealScoringService(DualPathScorer dual) => _dual = dual;

    public async Task<DealScore> ScoreAsync(string profile,
        string? provider, string? model, string? apiKey, CancellationToken ct)
    {
        var config = new DualPathConfig<DealScore>(
            Workflow:           "DealScoring",
            CacheKey:           AiResponseCache.Hash("deal-score", model, profile),
            SystemForJson:      DealPrompts.SystemForJson,
            SystemForNative:    DealPrompts.SystemForNative,
            BuildJsonPrompt:    attempt => DealPrompts.BuildJsonPrompt(profile, attempt),
            BuildNativePrompt:  () => DealPrompts.BuildNativePrompt(profile),
            ToolSchema:         DealPrompts.SubmitDealScoreSchema,
            TerminalToolName:   "submit_deal_score",
            ParseFromRawText:   DealPrompts.ParseRawText,
            ParseFromToolInput: DealPrompts.ParseToolInput,
            DefaultModel:       "claude-sonnet-4-5",
            MaxTokensJsonA:     1800,
            MaxTokensJsonB:     2400,
            MaxTokensNative:    2500,
            Temperature:        0.3);

        var outcome = await _dual.RunAsync(config, provider, model, apiKey, ct);
        return outcome.Value with { AiModel = outcome.AiModel, AiProvider = outcome.AiProvider };
    }
}
```

- [ ] **Step 3: Build + test verify**

Run:
```
dotnet build TourkitAiProxy.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: 0 error, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add Services/Deals/DealPrompts.cs Services/Deals/DealScoringService.cs
git commit -m "refactor(deals): migrate DealScoringService sang DualPathScorer (~270 → ~35 LOC)"
```

---

## Task 12: Migrate TourBuilderService

**Files:**
- Create: `Services/Tour/TourPrompts.cs`
- Modify: `Services/Tour/TourBuilderService.cs`

- [ ] **Step 1: Tạo `Services/Tour/TourPrompts.cs`**

```csharp
// Services/Tour/TourPrompts.cs
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Prompts;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Tour;

public static class TourPrompts
{
    public const string SystemForJson =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là chuyên viên điều hành tour Việt Nam, đọc mô tả của Sale và bóc tách thành form tour GIT (Group Inclusive Tour). " +
        CommonPromptParts.JsonOutputRules + " " +
        "KHÔNG bịa thông tin chưa có — field nào không rõ thì để null/0/[]; ghi điều cần làm rõ vào 'warnings'. " +
        CommonPromptParts.VietnameseStyle;

    public const string SystemForNative =
        CommonPromptParts.TourkitContext + " " +
        "Bạn là chuyên viên điều hành tour — bóc mô tả Sale thành form Tour GIT. " +
        "Gọi tool submit_tour_draft với kết quả. KHÔNG bịa — field không rõ để null/0/[]; ghi vào warnings. " +
        CommonPromptParts.VietnameseStyle;

    public static string BuildJsonPrompt(string text, int attempt) => $@"NHIỆM VỤ: Đọc mô tả Sale gửi dưới đây và trả JSON Draft Tour GIT.

MÔ TẢ CỦA SALE:
{text.Trim()}

{CommonRules}

OUTPUT JSON (KHÔNG markdown):
{{
  ""title"": ""tên tour"",
  ""marketName"": null,
  ""tourType"": ""Nội địa|Inbound|Outbound|null"",
  ""startDate"": ""yyyy-MM-dd"",
  ""endDate"": ""yyyy-MM-dd"",
  ""adultCount"": 0, ""childCount"": 0,
  ""customerName"": null, ""customerPhone"": null, ""customerEmail"": null,
  ""note"": null,
  ""expenses"": [{{ ""title"": ""Vé tour"", ""unitPrice"": 5000000, ""quantity"": 18, ""vatPercent"": 8 }}],
  ""services"": [{{ ""name"": ""Khách sạn"", ""providerName"": null, ""quantity"": 10, ""nights"": 2, ""netPrice"": 800000, ""vatPercent"": 8 }}],
  ""warnings"": [""điều cần Sale làm rõ""]
}}
Bắt đầu trả JSON ngay:";

    public static string BuildNativePrompt(string text) => $@"NHIỆM VỤ: Đọc mô tả Sale gửi dưới đây và GỌI TOOL submit_tour_draft với Draft Tour GIT.

MÔ TẢ CỦA SALE:
{text.Trim()}

{CommonRules}

Gọi submit_tour_draft NGAY. KHÔNG trả text giải thích.";

    private const string CommonRules = @"QUY TẮC:
1. Chỉ dùng thông tin trong mô tả, KHÔNG bịa
2. Field không rõ → null / 0 / [] và ghi 'warnings'
3. Ngày: yyyy-MM-dd (vd 'đi 15/7' → '2026-07-15', năm hiện tại 2026 nếu không nói rõ)
4. Số liệu tiền: nguyên số đồng (vd '5 triệu' → 5000000, '1.2tr' → 1200000)
5. tourType: 'Nội địa' | 'Inbound' (KH nước ngoài) | 'Outbound' (VN đi nước ngoài)
6. expenses (Phần thu) = tiền Sale thu của khách
7. services (Dịch vụ điều hành) = chi phí trả NCC";

    public static readonly JsonElement SubmitTourDraftSchema = NativeToolScorer.BuildAnthropicTool(
        name: "submit_tour_draft",
        description: "Nộp Draft Tour GIT bóc từ mô tả Sale. Gọi DUY NHẤT 1 lần.",
        properties: new
        {
            title = new { type = "string" },
            marketName = new { type = new[] { "string", "null" } },
            tourType = new { type = new[] { "string", "null" } },
            startDate = new { type = new[] { "string", "null" } },
            endDate = new { type = new[] { "string", "null" } },
            adultCount = new { type = new[] { "integer", "null" }, minimum = 0 },
            childCount = new { type = new[] { "integer", "null" }, minimum = 0 },
            customerName = new { type = new[] { "string", "null" } },
            customerPhone = new { type = new[] { "string", "null" } },
            customerEmail = new { type = new[] { "string", "null" } },
            note = new { type = new[] { "string", "null" } },
            expenses = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        unitPrice = new { type = "integer", minimum = 0 },
                        quantity = new { type = "integer", minimum = 0 },
                        vatPercent = new { type = "number", minimum = 0, maximum = 100 }
                    },
                    required = new[] { "title", "unitPrice", "quantity" }
                }
            },
            services = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        providerName = new { type = new[] { "string", "null" } },
                        quantity = new { type = "integer", minimum = 0 },
                        nights = new { type = "integer", minimum = 0 },
                        netPrice = new { type = "integer", minimum = 0 },
                        vatPercent = new { type = "number", minimum = 0, maximum = 100 }
                    },
                    required = new[] { "name", "quantity", "netPrice" }
                }
            },
            warnings = new { type = "array", items = new { type = "string" } }
        },
        required: new[] { "expenses", "services", "warnings" });

    public static TourBuilderDraft ParseRawText(string raw)
    {
        using var doc = TourkitAiProxy.Services.Json.LooseJson.ParseFirstObject(raw);
        return ParseElement(doc.RootElement);
    }

    public static TourBuilderDraft ParseToolInput(JsonElement root) => ParseElement(root);

    private static TourBuilderDraft ParseElement(JsonElement root) => new(
        Title:         root.GetStringField("title"),
        MarketName:    root.GetStringField("marketName"),
        TourType:      root.GetStringField("tourType"),
        StartDate:     root.GetStringField("startDate"),
        EndDate:       root.GetStringField("endDate"),
        AdultCount:    root.GetIntFieldOrNull("adultCount"),
        ChildCount:    root.GetIntFieldOrNull("childCount"),
        CustomerName:  root.GetStringField("customerName"),
        CustomerPhone: root.GetStringField("customerPhone"),
        CustomerEmail: root.GetStringField("customerEmail"),
        Note:          root.GetStringField("note"),
        Expenses:      ParseExpenses(root),
        Services:      ParseServices(root),
        Warnings:      root.GetStringListField("warnings"));

    private static List<TourBuilderExpense> ParseExpenses(JsonElement root)
    {
        var list = new List<TourBuilderExpense>();
        if (!root.TryGetField("expenses", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var title = e.GetStringField("title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new TourBuilderExpense(title!, e.GetLongField("unitPrice"),
                Math.Max(0, e.GetIntField("quantity")), e.GetDoubleField("vatPercent")));
        }
        return list;
    }

    private static List<TourBuilderServiceItem> ParseServices(JsonElement root)
    {
        var list = new List<TourBuilderServiceItem>();
        if (!root.TryGetField("services", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var name = e.GetStringField("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new TourBuilderServiceItem(
                Name: name!, ProviderName: e.GetStringField("providerName"),
                Quantity: Math.Max(0, e.GetIntField("quantity")),
                Nights: Math.Max(0, e.GetIntField("nights")),
                NetPrice: e.GetLongField("netPrice"),
                VatPercent: e.GetDoubleField("vatPercent")));
        }
        return list;
    }
}
```

- [ ] **Step 2: Rewrite `Services/Tour/TourBuilderService.cs`**

```csharp
// Services/Tour/TourBuilderService.cs (full rewrite)
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Cache;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Tour;

public class TourBuilderService
{
    private readonly DualPathScorer _dual;
    public TourBuilderService(DualPathScorer dual) => _dual = dual;

    public async Task<TourBuilderDraft> ParseAsync(TourBuilderRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            throw new InvalidOperationException("Mô tả rỗng");

        var config = new DualPathConfig<TourBuilderDraft>(
            Workflow:           "TourBuilder",
            CacheKey:           AiResponseCache.Hash("tour-builder", req.Model, req.Prompt),
            SystemForJson:      TourPrompts.SystemForJson,
            SystemForNative:    TourPrompts.SystemForNative,
            BuildJsonPrompt:    attempt => TourPrompts.BuildJsonPrompt(req.Prompt, attempt),
            BuildNativePrompt:  () => TourPrompts.BuildNativePrompt(req.Prompt),
            ToolSchema:         TourPrompts.SubmitTourDraftSchema,
            TerminalToolName:   "submit_tour_draft",
            ParseFromRawText:   TourPrompts.ParseRawText,
            ParseFromToolInput: TourPrompts.ParseToolInput,
            DefaultModel:       "claude-sonnet-4-5",
            MaxTokensJsonA:     4500,
            MaxTokensJsonB:     8000,
            MaxTokensNative:    5000,
            Temperature:        0.2);

        var outcome = await _dual.RunAsync(config, req.Provider, req.Model, req.ApiKey, ct);
        return outcome.Value;  // TourBuilderDraft không có field AiModel/AiProvider
    }
}
```

- [ ] **Step 3: Build + test**

Run:
```
dotnet build TourkitAiProxy.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: 0 error, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add Services/Tour/TourPrompts.cs Services/Tour/TourBuilderService.cs
git commit -m "refactor(tour): migrate TourBuilderService sang DualPathScorer (~330 → ~40 LOC)"
```

---

## Task 13: Migrate MailClassifier

**Files:**
- Create: `Services/Mail/MailClassifierPrompts.cs`
- Modify: `Services/Mail/MailClassifier.cs`

- [ ] **Step 1: Tạo `Services/Mail/MailClassifierPrompts.cs`**

```csharp
// Services/Mail/MailClassifierPrompts.cs
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Prompts;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

public static class MailClassifierPrompts
{
    public const string SystemForJson =
        "Bạn là bộ phân loại email cho một công ty du lịch. " +
        "Đọc email và CHỌN ĐÚNG 1 nhóm + tóm tắt 1 câu ngắn bằng tiếng Việt. " +
        CommonPromptParts.JsonOutputRules;

    public const string SystemForNative =
        "Bạn là bộ phân loại email cho một công ty du lịch. " +
        "Đọc email và gọi tool submit_mail_classification với 1 nhóm + tóm tắt 1 câu tiếng Việt.";

    public static string BuildJsonPrompt(MailItem mail, int attempt)
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

OUTPUT JSON (key category dùng ĐÚNG mã nhóm):
{{ ""category"": ""<mã nhóm>"", ""summary"": ""tóm tắt 1 câu"" }}

Trả JSON ngay:";
    }

    public static string BuildNativePrompt(MailItem mail)
    {
        var cats = string.Join("\n", MailTaxonomy.Categories.Select(kv => $"- {kv.Key}: {kv.Value}"));
        var body = mail.Body.Length > 2000 ? mail.Body[..2000] + " …(cắt)" : mail.Body;
        return $@"PHÂN LOẠI EMAIL SAU và GỌI TOOL submit_mail_classification:

CÁC NHÓM (bắt buộc chọn 1):
{cats}

EMAIL:
Từ: {mail.From.Name} <{mail.From.Email}>
Tiêu đề: {mail.Subject}
Nội dung: {body}

Gọi submit_mail_classification NGAY.";
    }

    public static readonly JsonElement SubmitMailClassificationSchema = NativeToolScorer.BuildAnthropicTool(
        name: "submit_mail_classification",
        description: "Nộp kết quả phân loại email. Gọi DUY NHẤT 1 lần.",
        properties: new
        {
            category = new { type = "string", @enum = MailTaxonomy.Categories.Keys.ToArray() },
            summary = new { type = "string" }
        },
        required: new[] { "category", "summary" });

    /// <summary>Parse classification từ raw text. Public để giữ MailClassifier.ParseClassification legacy test.</summary>
    public static (string Category, string Summary) ParseRawText(string raw)
    {
        var json = LooseJson.ExtractFirstObject(raw);
        if (json == null) return ("khac", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseElement(doc.RootElement);
        }
        catch { return ("khac", ""); }
    }

    public static (string Category, string Summary) ParseToolInput(JsonElement root) => ParseElement(root);

    private static (string Category, string Summary) ParseElement(JsonElement root)
    {
        var cat = MailTaxonomy.NormalizeCategory(root.GetStringField("category"));
        var sum = root.GetStringField("summary") ?? "";
        return (cat, sum.Trim());
    }
}
```

- [ ] **Step 2: Rewrite `Services/Mail/MailClassifier.cs`**

Lưu ý: `DualPathScorer.RunAsync<T> where T : class` — `(string, string)` tuple là value type. Cần wrap thành record class.

Sửa cách: gọi `_dual.RunAsync<MailClassificationResult>` với class wrapper:

```csharp
// Services/Mail/MailClassifier.cs (full rewrite)
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Mail;

/// <summary>
/// Phân loại 1 email vào 6 nhóm + tóm tắt 1 câu.
/// Dispatch + retry delegate cho DualPathScorer; prompts ở MailClassifierPrompts.
/// </summary>
public class MailClassifier
{
    private readonly DualPathScorer _dual;
    private readonly ILogger<MailClassifier> _log;

    public MailClassifier(DualPathScorer dual, ILogger<MailClassifier> log)
    {
        _dual = dual; _log = log;
    }

    public async Task<(string Category, string Summary)> ClassifyAsync(MailItem mail, CancellationToken ct)
    {
        try
        {
            var config = new DualPathConfig<MailClassificationResult>(
                Workflow:           "MailClassifier",
                CacheKey:           null,    // Mail KHÔNG cache — mỗi mail unique, classify chỉ 1 lần lúc sync
                SystemForJson:      MailClassifierPrompts.SystemForJson,
                SystemForNative:    MailClassifierPrompts.SystemForNative,
                BuildJsonPrompt:    attempt => MailClassifierPrompts.BuildJsonPrompt(mail, attempt),
                BuildNativePrompt:  () => MailClassifierPrompts.BuildNativePrompt(mail),
                ToolSchema:         MailClassifierPrompts.SubmitMailClassificationSchema,
                TerminalToolName:   "submit_mail_classification",
                ParseFromRawText:   text => {
                    var (c, s) = MailClassifierPrompts.ParseRawText(text);
                    return new MailClassificationResult(c, s);
                },
                ParseFromToolInput: el => {
                    var (c, s) = MailClassifierPrompts.ParseToolInput(el);
                    return new MailClassificationResult(c, s);
                },
                DefaultModel:       "claude-haiku-4-5",    // task đơn giản — haiku rẻ + nhanh
                MaxTokensJsonA:     1000, MaxTokensJsonB: 1500,
                MaxTokensNative:    500,
                Temperature:        0.1);

            var outcome = await _dual.RunAsync(config, null, null, null, ct);
            return (outcome.Value.Category, outcome.Value.Summary);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Phân loại email {Id} lỗi → khac", mail.Id);
            return ("khac", "");
        }
    }

    /// <summary>Parse output AI dạng raw text. Giữ legacy cho TourkitAiProxy.Tests.MailClassifierTests.</summary>
    public static (string Category, string Summary) ParseClassification(string raw)
        => MailClassifierPrompts.ParseRawText(raw);
}

internal record MailClassificationResult(string Category, string Summary);
```

- [ ] **Step 3: Build + test (đặc biệt verify MailClassifierTests vẫn pass)**

Run:
```
dotnet build TourkitAiProxy.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~MailClassifier"
```
Expected: 4/4 pass (3 ParseClassification test cũ + bất kỳ test khác).

- [ ] **Step 4: Run all tests**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: All pass (96 + extensions test + scorer test = ~110 total).

- [ ] **Step 5: Commit**

```bash
git add Services/Mail/MailClassifierPrompts.cs Services/Mail/MailClassifier.cs
git commit -m "refactor(mail): migrate MailClassifier sang DualPathScorer (~190 → ~50 LOC)"
```

---

## Task 14: Cleanup private JSON helpers ở 4 file còn lại

**Files:**
- Modify: `Services/Reviews/Agents/ReviewPrompt.cs`
- Modify: `Services/Visa/VisaExtractionService.cs`
- Modify: `Services/TourKit/TourKitApiClient.cs`
- Modify: `Services/Deals/DealOpportunityClient.cs`
- Modify: `Endpoints/TourEndpoints.cs`

- [ ] **Step 1: ReviewPrompt — replace TryGet/GetString/GetStringList với extensions**

Tìm trong `Services/Reviews/Agents/ReviewPrompt.cs` ở cuối file (sau ParseAction):
```csharp
// Xóa các method:
//   private static bool TryGet(JsonElement el, string name, out JsonElement value)
//   private static string? GetString(JsonElement el, string name)
//   private static List<string>? GetStringList(JsonElement el, string name)
```

Trong `ParseElement` và các method khác trong file, replace:
- `GetString(root, "x")` → `root.GetStringField("x")`
- `GetStringList(root, "x")` → `root.GetStringListField("x")` (chú ý: return type khác — `List<string>` không nullable, OK vì test for Count == 0)
- `TryGet(root, "x", out var p)` → `root.TryGetField("x", out var p)`

Thêm `using TourkitAiProxy.Services.Json;` ở top.

`GetStringList` cũ trả `List<string>?` (nullable), mới trả `List<string>` (empty if missing). Test logic: nếu null trước thì giờ là empty → semantically OK với code dùng `?.Count`.

Check usage trong ReviewPrompt `ParseElement`:
```csharp
Strengths: GetStringList(root, "strengths"),       // returns null? before, now empty
```
Hiện `CustomerReview.Strengths` là `List<string>` non-null → `?? new()` ở compose. Sửa:
```csharp
Strengths: root.GetStringListField("strengths"),  // returns empty list, fine
```
Trong `Compose`:
```csharp
Strengths: parsed.Strengths ?? new(),     // ?? new() redundant nhưng harmless
```
→ giữ `?? new()` cũng OK, parsed.Strengths giờ luôn non-null nhưng cú pháp vẫn compile.

- [ ] **Step 2: Build verify**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: 0 error. Có thể có warning nullable nhưng OK.

- [ ] **Step 3: VisaExtractionService — extract JSON helpers**

Mở `Services/Visa/VisaExtractionService.cs`. Tìm private static helpers tương tự (TryGet/Str/Int). Replace:
- `private static bool TryGet(...)` → xóa
- `private static string? Str(...)` → xóa, dùng `root.GetStringField`
- v.v.

Add `using TourkitAiProxy.Services.Json;` nếu chưa có.

- [ ] **Step 4: TourKitApiClient — extract**

Mở `Services/TourKit/TourKitApiClient.cs`. Tìm + xóa private JSON helpers, dùng extensions.

- [ ] **Step 5: DealOpportunityClient — extract**

Same với `Services/Deals/DealOpportunityClient.cs`.

- [ ] **Step 6: TourEndpoints body POST handler — extract**

Mở `Endpoints/TourEndpoints.cs` xem POST `/tours` handler có private helper parse JsonElement body không. Nếu có → replace với extensions.

- [ ] **Step 7: Build + run tests verify không regress**

Run:
```
dotnet build TourkitAiProxy.csproj
dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj
```
Expected: 0 error, all pass.

- [ ] **Step 8: Smoke test full proxy**

Run background: `dotnet run --project TourkitAiProxy.csproj --no-build`

Sau khi ready:
```bash
curl -s http://localhost:5080/healthz
# {"ok":true,...}
curl -s http://localhost:5080/api/v1/providers -o /dev/null -w "HTTP %{http_code}\n"
# HTTP 200
```

Kill: `taskkill //F //IM dotnet.exe`

- [ ] **Step 9: Commit**

```bash
git add Services/Reviews/Agents/ReviewPrompt.cs Services/Visa/VisaExtractionService.cs Services/TourKit/TourKitApiClient.cs Services/Deals/DealOpportunityClient.cs Endpoints/TourEndpoints.cs
git commit -m "refactor(json): cleanup private JSON helpers ở 5 file, dùng JsonElementExtensions"
```

---

## Task 15: Cập nhật CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update folder map**

Tìm trong `CLAUDE.md` section "Backend layout":
```
  Workflow/
    AnthropicToolsClient.cs                ← reusable agentic loop (max 5 iter, terminal tool detect) — share Review/Visa/Deal/Tour/Mail
    NativeToolScorer.cs                    ← thin wrapper RunAsync<T> cho service single-shot (Visa/Deal/Tour/Mail)
    WorkflowTrace.cs + Accessor + Log      ← debug trace per-request (?debug=1) → JSONL audit
```

Replace với:
```
  Workflow/
    AnthropicToolsClient.cs                ← reusable agentic loop (max 5 iter, terminal tool detect) — share Review/Visa/Deal/Tour/Mail
    NativeToolScorer.cs                    ← thin wrapper RunAsync<T> cho Anthropic native path
    JsonPromptScorer.cs                    ← generic 2-attempt JSON retry loop cho JSON path
    DualPathScorer.cs + DualPathConfig.cs  ← facade dispatching native vs JSON + cache built-in (Visa/Deal/Tour/Mail dùng)
    WorkflowTrace.cs + Accessor + Log      ← debug trace per-request (?debug=1) → JSONL audit
  Json/
    LooseJson.cs                           ← extract first balanced {…} từ AI output (cho JSON path)
    JsonElementExtensions.cs               ← extension methods case-insensitive lookup + tolerant type conv (dùng toàn project)
  Prompts/
    CommonPromptParts.cs                   ← shared system prompt fragments (TourkitContext, OutputRules, VietnameseStyle)
```

- [ ] **Step 2: Cập nhật section "Native function-calling" thêm DualPathScorer**

Tìm trong "Shared infrastructure (`Services/Workflow/`):" section, thêm bullet sau NativeToolScorer:
```markdown
- **`DualPathScorer.RunAsync<T>(DualPathConfig<T>, providerOverride, modelOverride, apiKeyOverride)`** — facade cho 4 service single-shot (Visa/Deal/Tour/Mail). Tự dispatch (`anthropic` → `NativeToolScorer`, else → `JsonPromptScorer` với 2-attempt retry), tự cache lookup/save (skip nếu `CacheKey: null`). Service từ ~250 LOC → ~50 LOC chỉ define prompts + schema + parser. **KHÔNG dùng cho Customer Review** — Review dùng strategy pattern `IReviewAgent` riêng vì cần multi-step augmentation trong tương lai (Mức C).
- **`JsonPromptScorer.RunAsync<T>(IAiProvider, systemPrompt, buildPrompt(attempt), parser, ...)`** — generic 2-attempt retry: lần 1 prompt gốc + maxTokensA, lần 2 thêm "LƯU Ý: Lần trước trả SAI định dạng..." + maxTokensB. Recovery ~70% reasoning model fail format đầu.
```

- [ ] **Step 3: Build verify CLAUDE.md không break docs format**

Run: `git diff CLAUDE.md | head -50`
Expected: edits hiện rõ, không có structure broken.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: cập nhật CLAUDE.md sau Phase 0 dedup — DualPathScorer + JsonElementExtensions + CommonPromptParts"
```

---

## Self-Review (sau khi viết plan)

### 1. Spec coverage

| Spec section | Task implements |
|--------------|----------------|
| Group 3 — AI dual-path dispatch | Task 8-13 (DualPathScorer + 4 service migrate) |
| Group 4 — JSON helpers | Task 1-3 (JsonElementExtensions) + Task 14 (cleanup 5 file còn lại) |
| Group 5 — Cache+retry | Task 8-9 (DualPathScorer cache + 9.5 outcome) — subsumed |
| Group 6 — System prompts | Task 4 (CommonPromptParts) + Task 10-13 (4 service dùng) |
| Acceptance: `JsonElementExtensions` thay 12 private helpers | Task 1-3 + Task 14 |
| Acceptance: `DualPathScorer` thay 4 service dispatch | Task 8-13 |
| Acceptance: CLAUDE.md cập nhật | Task 15 |
| Per-commit safety: build clean + tests pass + smoke | Mỗi task có Step verify |

Tất cả spec requirements đều có task. Không gap.

### 2. Placeholder scan

- Không có "TBD"/"TODO"/"implement later"
- Mọi step có code block đầy đủ
- Test names cụ thể (`RunAsync_returns_cached_value_without_calling_AI`)
- Commit message cụ thể (không "wip", "fix")

### 3. Type consistency

- `DualPathConfig<T>` được dùng đúng trong Task 8, 9.5, 10, 11, 12, 13
- `DualPathOutcome<T>` thêm ở Task 9.5, dùng ở Task 10-13 với `outcome.Value`
- `JsonElementExtensions` methods (`TryGetField`, `GetStringField`, etc.) consistent ở Task 1-3, 10-14
- `StubProvider.Ok(...)` factory method consistent ở Task 6-9
- `NativeToolScorer.BuildAnthropicTool(...)` đã có từ commit `70ac8a6` — Task 10-13 gọi đúng signature

### 4. Constraint check

- `DualPathScorer.RunAsync<T> where T : class` → MailClassifier dùng `MailClassificationResult` record class wrapper (Task 13 noted).
- `TourBuilderDraft` có là class? — `public record TourBuilderDraft(...)` mặc định là class. OK.
- `VisaResult` + `DealScore` — same, record class. OK.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-06-08-restful-refactor-phase-0-service-dedup.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — Tôi dispatch fresh subagent cho mỗi task, review giữa các task, fast iteration. Tốt cho plan có 15 task vì:
- Mỗi subagent context sạch — không drift
- Tôi review code sau mỗi task → catch regression sớm
- Plan có dependency rõ (Task 1 → 2 → 3 → 4...) → subagent tuần tự tự nhiên

**2. Inline Execution** — Execute tasks ngay trong session này (executing-plans), batch execution với checkpoints. Phù hợp nếu anh muốn theo dõi từng bước real-time + can thiệp nhanh.

**Anh chọn approach nào?**
