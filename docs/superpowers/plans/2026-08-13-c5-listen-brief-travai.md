# C5 — Nghe bản tin sáng qua TRAVAI (nút "Nghe" đọc ceo-brief/sale-brief) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho người dùng bấm 1 nút để **nghe** bản tin sáng (sale-brief / ceo-brief) đọc bằng giọng máy — đứng trên nội dung bản tin ĐÃ có sẵn trong Bảng tin (`dbo.AgentInsights`) và hạ tầng TTS đã chạy (`POST /api/v1/speech/tts`). Kèm chốt luật: **mỗi người chỉ nhận 1 loại bản tin theo vai trò** (sale HOẶC ceo, không cả hai).

**Architecture:** Không dựng đường mới cho TTS — tái dùng `POST /api/v1/speech/tts` (đa engine + fallback, đã chạy cho JARVIS). Thêm 1 hàm THUẦN `BriefNarration.ToSpeakable(markdown)` đổi bản tin markdown-lite → câu chữ đọc được (bỏ `**`, gạch đầu dòng, emoji; đổi `1.234đ`→`1.234 đồng`, `%`→`phần trăm`) — test được bằng xUnit. Endpoint `GET /api/v1/insights` trả thêm `speakText` cho item loại bản tin; frontend thêm nút "Nghe" gọi 1 helper TTS dùng chung `window.tourkitTts`. Luật 1-loại-1-người enforce ở backend (bật loại này → tự tắt loại kia) + hint UI.

**Tech Stack:** ASP.NET Core 8 Minimal API, Dapper + SQL Server, xUnit (`TourkitAiProxy.Tests`), frontend React no-build (`wwwroot/**/*.jsx` + Babel/esbuild dual-mode).

**Spec nguồn:** roadmap Đợt 2 mục **C5** trong [docs/superpowers/specs/2026-08-11-ai-agent-personas-research.md](../specs/2026-08-11-ai-agent-personas-research.md) ("Nghe bản tin qua TRAVAI — reuse C1 + TTS").

## Global Constraints

- Comment/log/string user-facing = **tiếng Việt**. DateTime UTC (không đụng trong plan này).
- Frontend thêm file lib mới = sửa đủ **2 chỗ**: `wwwroot/index.html` (thẻ `<script>`, dev-mode Babel) + `wwwroot/bundle-entry.js` (import, prod-mode esbuild). Thiếu 1 chỗ → dev chạy prod trắng, hoặc ngược lại.
- Không thêm trang mới → KHÔNG đụng `app.jsx`/router.
- `POST /api/v1/speech/tts` YÊU CẦU `X-Session-Id`; mọi call frontend đi qua `window.tourkitAuth.authedFetch` (tự gắn session). Body `{ text, voice? }`, trả `audio/mpeg` (200) hoặc `{ error }` (400). Cắt `text` ≤ 2000 ký tự ở server rồi (`TextToSpeechService.MAX_CHARS`) — không cần cắt lại ở client.
- Test: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` — **chỉ logic thuần** (repo/endpoint verify tay theo quy ước dự án; SmartMail/Speech cũng vậy).
- Interface/kiểu SẴN CÓ (VERBATIM — không đổi):
  - `record DigestMessage(string Title, string BodyMarkdown, string BodyHtml, string Kind, int Severity = 0)`
  - `record AgentInsight(long Id, string TenantId, string Username, string Kind, int Severity, string Title, string Body, string? DataJson, string? AlertKey, bool IsRead, DateTime CreatedUtc)`
  - `static class BriefTypes { const string Sale = "sale-brief"; const string Ceo = "ceo-brief"; static bool IsValid(string?); }`
  - `SessionAuth.Read(ctx, sessions)` → `a` có `a.TenantId`, `a.Username`, `a.SessionId`; `SessionAuth.Unauthorized()` → 401.
  - `InsightRepository.ListAsync(tenant, username, kind, unreadOnly, offset, limit, ct)` → `List<AgentInsight>`.
  - `DigestSubscriptionRepository` — pattern Dapper sẵn có (`ListForUserAsync`, `UpsertAsync`, `MarkSentAsync`); const `Cols` + `_db.OpenAsync(ct)`.
  - Frontend: `window.tourkitAuth.authedFetch(path, opts)`; `window.Icon`; `window.DigestSubBlock`; `window.InsightsFeed`.

## File Structure

- `Services/Digest/BriefNarration.cs` — **MỚI**, thuần: markdown bản tin → text đọc được (1 nguồn, test được).
- `TourkitAiProxy.Tests/Digest/BriefNarrationTests.cs` — **MỚI**, xUnit.
- `Services/Digest/DigestSubscriptionRepository.cs` — **SỬA**: thêm `DeactivateOthersAsync`.
- `Endpoints/DigestEndpoints.cs` — **SỬA**: PUT enforce 1-loại-1-người.
- `Endpoints/InsightEndpoints.cs` — **SỬA**: GET trả thêm `speakText`.
- `wwwroot/lib/tts.js` — **MỚI**: `window.tourkitTts.speak(text, cbs)` (POST /speech/tts + phát, iOS-safe).
- `wwwroot/index.html` + `wwwroot/bundle-entry.js` — **SỬA**: đăng ký `lib/tts.js`.
- `wwwroot/lib/icons.jsx` — **SỬA**: thêm icon `volume` + `stop`.
- `wwwroot/pages/insights.jsx` — **SỬA**: nút "Nghe" cho item bản tin.
- `wwwroot/pages/digest.jsx` — **SỬA**: hint "mỗi người 1 loại".
- `CLAUDE.md` — **SỬA**: ghi chú `speakText` + nút Nghe.

---

### Task 1: Luật "mỗi người chỉ 1 loại bản tin" — enforce ở backend + hint UI

**Files:**
- Modify: `Services/Digest/DigestSubscriptionRepository.cs` (thêm method sau `UpsertAsync`)
- Modify: `Endpoints/DigestEndpoints.cs` (trong handler `PUT /subscriptions/{briefType}`, sau `UpsertAsync`)
- Modify: `wwwroot/pages/digest.jsx` (thêm 1 dòng hint dưới ô "Nhận bản tin này")

**Interfaces (Produces):**
- `DigestSubscriptionRepository.DeactivateOthersAsync(string tenant, string username, string keepBriefType, CancellationToken ct)` → `Task` — tắt (`Enabled=0`) mọi loại KHÁC `keepBriefType` của cùng người.

Lý do enforce ở backend chứ không chỉ UI: một người theo **vai trò** hoặc là sale hoặc là giám đốc — không phải cả hai. Chặn ở server để dù gọi API tay cũng không bật được 2 loại (client có thể qua mặt). Chỉ tắt khi bật loại mới (`body.Enabled=true`); tắt hết (disable) không đụng loại kia.

- [ ] **Step 1: Thêm method repo** — mở `Services/Digest/DigestSubscriptionRepository.cs`, thêm NGAY SAU `UpsertAsync`:

```csharp
    /// Tắt mọi loại bản tin KHÁC của cùng người — dùng khi bật 1 loại để giữ luật "1 người 1 loại".
    /// CHỈ đổi Enabled (giữ nguyên nơi nhận đã khai), và chỉ chạm dòng đang bật → không tạo dòng thừa.
    public async Task DeactivateOthersAsync(string tenant, string username, string keepBriefType,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(@"
UPDATE dbo.DigestSubscriptions SET Enabled = 0, UpdatedUtc = SYSUTCDATETIME()
WHERE TenantId = @tenant AND Username = @username AND BriefType <> @keepBriefType AND Enabled = 1",
            new { tenant, username, keepBriefType });
    }
```

- [ ] **Step 2: Gọi trong PUT handler** — mở `Endpoints/DigestEndpoints.cs`, trong handler `g.MapPut("/subscriptions/{briefType}", ...)`, NGAY SAU khối `await repo.UpsertAsync(new DigestSubscription(...), ct);` và TRƯỚC `return Results.Json(new { ok = true }, Web);`, chèn:

```csharp
            // Luật: mỗi người theo vai trò chỉ nhận 1 loại. Bật loại này → tắt loại kia (server tự lo,
            // không tin mình client). Chỉ khi bật; disable thì để yên loại còn lại.
            if (body.Enabled)
                await repo.DeactivateOthersAsync(a.TenantId, a.Username, briefType, ct);
```

- [ ] **Step 3: Hint UI** — mở `wwwroot/pages/digest.jsx`, trong `DigestSubBlock`, NGAY SAU `<div className="digest-ch"> ... <span className="digest-ch-note">Chỉ áp dụng cho riêng bạn, không ảnh hưởng người khác</span></div>` (kết thúc dòng ~128), chèn:

```jsx
      <div className="digest-role-note">
        <Icon name="info" size={12} /> Mỗi người chỉ nhận <b>một</b> loại bản tin theo vai trò — bật loại
        này sẽ tự tắt loại kia.
      </div>
```

- [ ] **Step 4: Build** — `dotnet build TourkitAiProxy.csproj` → 0 error.

- [ ] **Step 5: Verify tay (không có unit-test DB theo quy ước dự án)** — chạy app, đăng nhập 1 phiên, bật `sale-brief` (Lưu) → bật `ceo-brief` (Lưu) → tải lại `GET /api/v1/digest/subscriptions`: chỉ `ceo-brief` còn `enabled:true`, `sale-brief` về `enabled:false`. (Có thể dùng `scripts/e2e/features-digest.ps1` nếu muốn tự động.)

- [ ] **Step 6: Commit**

```bash
git add Services/Digest/DigestSubscriptionRepository.cs Endpoints/DigestEndpoints.cs wwwroot/pages/digest.jsx
git commit -m "feat(digest): mỗi người chỉ nhận 1 loại bản tin theo vai trò (bật loại này tự tắt loại kia)"
```

---

### Task 2: BriefNarration.ToSpeakable — đổi bản tin markdown sang text đọc được (THUẦN, TDD)

**Files:**
- Create: `Services/Digest/BriefNarration.cs`
- Test: `TourkitAiProxy.Tests/Digest/BriefNarrationTests.cs`

**Interfaces (Produces):**
- `BriefNarration.ToSpeakable(string? markdown)` → `string` — bỏ `**`, gạch đầu dòng, emoji; đổi `<số>đ`→`<số> đồng`, `%`→`phần trăm`; ghép mỗi dòng thành câu kết thúc bằng dấu chấm. Rỗng/null → `""`.

- [ ] **Step 1: Viết test fail**

```csharp
// TourkitAiProxy.Tests/Digest/BriefNarrationTests.cs
using TourkitAiProxy.Services.Digest;
using Xunit;

namespace TourkitAiProxy.Tests.Digest;

public class BriefNarrationTests
{
    [Fact] public void Bo_dau_sao_in_dam()
        => Assert.Equal("Tour Nhật Bản.", BriefNarration.ToSpeakable("**Tour Nhật Bản**"));

    [Fact] public void Bo_emoji_va_gach_dau_dong_ghep_cau()
        => Assert.Equal("Cơ hội cần gọi lại (5). Tour A.",
            BriefNarration.ToSpeakable("**📞 Cơ hội cần gọi lại (5)**\n- Tour A"));

    [Fact] public void Doi_tien_va_phan_tram()
        => Assert.Equal("Doanh thu: 1.234.567 đồng (+12 phần trăm).",
            BriefNarration.ToSpeakable("- Doanh thu: 1.234.567đ (+12%)"));

    [Fact] public void Nhieu_dong_ghep_thanh_cau()
        => Assert.Equal("Xin chào. Hôm nay có 2 việc.",
            BriefNarration.ToSpeakable("Xin chào.\n\nHôm nay có 2 việc"));

    [Fact] public void Giu_nguyen_dau_cau_san_co()
        => Assert.Equal("Hộp thư công ty: 4 thư chờ xử lý.",
            BriefNarration.ToSpeakable("📬 Hộp thư công ty: 4 thư chờ xử lý."));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   \n  ")]
    public void Rong_tra_chuoi_rong(string? input)
        => Assert.Equal("", BriefNarration.ToSpeakable(input));
}
```

- [ ] **Step 2: Chạy để thấy fail** — `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter "FullyQualifiedName~BriefNarration"` → FAIL (compile: chưa có type).

- [ ] **Step 3: Implement**

```csharp
// Services/Digest/BriefNarration.cs
using System.Text;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Digest;

/// <summary>
/// Đổi nội dung bản tin (markdown-lite: **in đậm** + gạch đầu dòng + emoji tiêu đề) sang câu chữ
/// ĐỌC ĐƯỢC cho TTS. Đọc thẳng markdown lên loa nghe "sao sao Cơ hội gạch... đê 1 chấm 234 đê" —
/// nên bỏ ký hiệu, đổi emoji/đơn vị thành lời, ghép dòng thành câu. THUẦN → test được.
/// Dùng cho nút "Nghe" ở Bảng tin; là 1 NGUỒN để không lệch cách đọc giữa các chỗ.
/// </summary>
public static class BriefNarration
{
    // \p{So}=Symbol-other (✅⚠…), \p{Cs}=surrogate (emoji astral như 📞 là cặp surrogate), FE0F=variation, 2022=bullet •
    private static readonly Regex Decor = new(@"[\p{So}\p{Cs}️•]", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(\d)\s*đ\b", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string ToSpeakable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            line = Bold.Replace(line, "$1");            // **x** → x
            line = Decor.Replace(line, "");             // bỏ emoji + ký hiệu trang trí
            if (line.StartsWith("- ")) line = line[2..]; // bỏ gạch đầu dòng
            line = line.Trim();
            if (line.Length == 0) continue;

            line = Money.Replace(line, "$1 đồng");       // 1.234đ → 1.234 đồng
            line = line.Replace("%", " phần trăm");
            line = MultiSpace.Replace(line, " ").Trim();
            if (line.Length == 0) continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            // Chưa có dấu kết câu → thêm chấm để giọng đọc ngắt nghỉ giữa các mục.
            if (line[^1] is not ('.' or '!' or '?' or ':' or ';')) sb.Append('.');
        }
        return sb.ToString().Trim();
    }
}
```

- [ ] **Step 4: Chạy pass** — `dotnet test ... --filter "FullyQualifiedName~BriefNarration"` → tất cả PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/Digest/BriefNarration.cs TourkitAiProxy.Tests/Digest/BriefNarrationTests.cs
git commit -m "feat(digest): BriefNarration.ToSpeakable — đổi bản tin sang lời đọc TTS (7 test)"
```

---

### Task 3: GET /api/v1/insights trả thêm `speakText` cho item bản tin

**Files:**
- Modify: `Endpoints/InsightEndpoints.cs` (handler `g.MapGet("", ...)`)

**Interfaces (Consumes):** `BriefNarration.ToSpeakable` (Task 2), `BriefTypes.IsValid`.
**Produces:** mỗi item trong `GET /api/v1/insights` có thêm field `speakText` (string cho item loại `sale-brief`/`ceo-brief`, `null` cho loại khác).

Đặt narration ở server (không strip markdown trong JS) để dùng chung 1 hàm ĐÃ test — client chỉ cần bơm `speakText` vào TTS. Chỉ tính cho item bản tin: cảnh báo thanh toán không cần đọc cả đoạn.

- [ ] **Step 1: Sửa handler GET** — mở `Endpoints/InsightEndpoints.cs`, thay khối trong `g.MapGet("", ...)`:

```csharp
            var items = await repo.ListAsync(a.TenantId, a.Username, kind,
                unread == true, Math.Max(0, offset ?? 0), limit ?? 30, ct);
            return Results.Json(new { items }, Web);
```

thành:

```csharp
            var items = await repo.ListAsync(a.TenantId, a.Username, kind,
                unread == true, Math.Max(0, offset ?? 0), limit ?? 30, ct);
            // Chỉ bản tin (sale/ceo) mới có nút "Nghe" → tính speakText tại chỗ; loại khác để null.
            var shaped = items.Select(it => new
            {
                it.Id, it.TenantId, it.Username, it.Kind, it.Severity, it.Title, it.Body,
                it.DataJson, it.AlertKey, it.IsRead, it.CreatedUtc,
                speakText = BriefTypes.IsValid(it.Kind) ? BriefNarration.ToSpeakable(it.Body) : null,
            });
            return Results.Json(new { items = shaped }, Web);
```

- [ ] **Step 2: Build** — `dotnet build TourkitAiProxy.csproj` → 0 error. (Namespace `TourkitAiProxy.Services.Digest` đã `using` sẵn ở đầu file — nếu build báo thiếu `BriefNarration`/`BriefTypes`, xác nhận dòng `using TourkitAiProxy.Services.Digest;` có ở đầu `InsightEndpoints.cs`.)

- [ ] **Step 3: Verify tay** — `GET /api/v1/insights` với 1 phiên đã có bản tin: item `kind:"ceo-brief"` phải có `speakText` là chuỗi không dấu `**`, không emoji; item `kind:"payment-alert"` có `speakText:null`. (camelCase nhờ `Web = new(JsonSerializerDefaults.Web)`.)

- [ ] **Step 4: Commit**

```bash
git add Endpoints/InsightEndpoints.cs
git commit -m "feat(digest): GET /insights trả thêm speakText cho item bản tin (đọc bằng TTS)"
```

---

### Task 4: Helper TTS dùng chung `window.tourkitTts` + đăng ký + icon

**Files:**
- Create: `wwwroot/lib/tts.js`
- Modify: `wwwroot/index.html` (thêm `<script>` sau `lib/util.js`)
- Modify: `wwwroot/bundle-entry.js` (thêm `import` sau `lib/util.js`)
- Modify: `wwwroot/lib/icons.jsx` (thêm icon `volume` + `stop`)

**Interfaces (Produces):**
- `window.tourkitTts.speak(text, { onStart?, onEnd?, onError? })` → `{ stop() }` — POST `/api/v1/speech/tts`, phát qua 1 phần tử `<audio>` dùng chung (iOS mở khoá 1 lần). Gọi lại khi đang phát = ngắt cái cũ.
- `window.tourkitTts.stop()` — dừng phần đang phát.

Không dùng `window.speechSynthesis` (giọng trình duyệt lệ thuộc máy) — luôn qua server để mọi máy nghe cùng 1 giọng, khớp cách JARVIS (`jarvis.jsx`) làm. JARVIS có bản streaming nhiều đoạn riêng cho hội thoại; ở đây bản tin ngắn (≤2000 ký tự) nên 1 lần gọi là đủ — helper này CỐ Ý gọn, không gánh logic cắt đoạn của JARVIS.

- [ ] **Step 1: Tạo `wwwroot/lib/tts.js`**

```javascript
// lib/tts.js — Phát giọng đọc (TTS) dùng chung: gọi server /speech/tts rồi phát audio.
// 1 NGUỒN cho mọi nút "Nghe" ngoài JARVIS. Vì sao qua server chứ không speechSynthesis: giọng
// trình duyệt tùy máy (Windows/Mac/điện thoại khác nhau, có máy không có giọng Việt) → server cho
// giọng đồng nhất. Dùng 1 phần tử <audio> DUY NHẤT: iOS chỉ cho play() sau khi phần tử được mở
// khoá bằng 1 cú chạm tay; tạo mới mỗi lần → iOS chặn. Nút "Nghe" là cú chạm đó.
'use strict';
(function () {
  let audioEl = null;
  function el() {
    if (!audioEl) { audioEl = new Audio(); audioEl.preload = 'auto'; }
    return audioEl;
  }
  let curUrl = null;                 // object URL đang phát → revoke khi xong/đổi
  function cleanupUrl() { if (curUrl) { try { URL.revokeObjectURL(curUrl); } catch {} curUrl = null; } }

  function stop() {
    const a = el();
    try { a.pause(); a.currentTime = 0; } catch {}
    a.onended = null; a.onerror = null;
    cleanupUrl();
  }

  // Phát 1 đoạn text. Trả { stop } để caller ngắt giữa chừng.
  function speak(text, cbs) {
    cbs = cbs || {};
    const t = String(text || '').trim();
    if (!t) { cbs.onError && cbs.onError('Không có nội dung để đọc'); return { stop() {} }; }

    stop();                                       // ngắt cái đang phát (nếu có)
    let aborted = false;
    const ac = new AbortController();

    window.tourkitAuth.authedFetch('/api/v1/speech/tts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: t }),
      signal: ac.signal,
    }).then(async (r) => {
      if (aborted) return;
      if (!r.ok) {
        let msg = 'HTTP ' + r.status;
        try { const j = await r.json(); msg = j.error || msg; } catch {}
        throw new Error(msg);
      }
      const buf = await r.arrayBuffer();
      if (aborted) return;
      cleanupUrl();
      curUrl = URL.createObjectURL(new Blob([buf], { type: 'audio/mpeg' }));
      const a = el();
      a.src = curUrl;
      a.onended = () => { cleanupUrl(); cbs.onEnd && cbs.onEnd(); };
      a.onerror = () => { cleanupUrl(); cbs.onError && cbs.onError('Không phát được audio'); };
      cbs.onStart && cbs.onStart();
      await a.play();
    }).catch((e) => {
      if (aborted || e.name === 'AbortError') return;
      cbs.onError && cbs.onError(e.message || 'Lỗi đọc');
    });

    return { stop() { aborted = true; try { ac.abort(); } catch {} stop(); } };
  }

  window.tourkitTts = { speak, stop };
})();
```

- [ ] **Step 2: Đăng ký dev-mode** — `wwwroot/index.html`, ngay SAU dòng `<script src="lib/util.js"></script>` (dòng ~117), thêm:

```html
<script src="lib/tts.js"></script>
```

- [ ] **Step 3: Đăng ký prod-mode** — `wwwroot/bundle-entry.js`, ngay SAU dòng `import "./lib/util.js";` (dòng ~11), thêm:

```javascript
import "./lib/tts.js";
```

- [ ] **Step 4: Thêm icon** — `wwwroot/lib/icons.jsx`, trong object `paths = { ... }`, thêm 2 dòng (đặt cạnh `refresh`/`send` cho dễ tìm):

```jsx
    volume: <><path d="M11 5L6 9H2v6h4l5 4V5z" /><path d="M15.5 8.5a5 5 0 0 1 0 7M19 5a9 9 0 0 1 0 14" /></>,
    stop: <rect x="6" y="6" width="12" height="12" rx="2" />,
```

- [ ] **Step 5: Verify tay** — chạy `dotnet run`, mở console trình duyệt gõ `window.tourkitTts.speak("Xin chào, đây là bản tin thử.")` sau khi đã đăng nhập (có session) → nghe được giọng đọc. `<Icon name="volume" />` và `<Icon name="stop" />` render ra hình (không rỗng).

- [ ] **Step 6: Commit**

```bash
git add wwwroot/lib/tts.js wwwroot/index.html wwwroot/bundle-entry.js wwwroot/lib/icons.jsx
git commit -m "feat(tts): helper phát giọng đọc dùng chung window.tourkitTts + icon volume/stop"
```

---

### Task 5: Nút "Nghe" trong Bảng tin (insights.jsx)

**Files:**
- Modify: `wwwroot/pages/insights.jsx` (thêm component `SpeakButton` + gắn vào card)

**Interfaces (Consumes):** `window.tourkitTts.speak` (Task 4), `it.speakText` (Task 3).

Nút chỉ hiện khi item có `speakText` (tức bản tin sale/ceo). Bấm nút KHÔNG được chỉ đánh dấu-đã-đọc (card có `onClick={markRead}`) → `stopPropagation`. Trạng thái: `Nghe` → `Đang tải…` → `Dừng`.

- [ ] **Step 1: Thêm component `SpeakButton`** — `wwwroot/pages/insights.jsx`, NGAY TRƯỚC `function InsightsFeed(...)` (dòng ~54), chèn:

```jsx
// Nút đọc bản tin bằng giọng máy (server TTS, dùng chung window.tourkitTts). Chỉ dùng cho item
// có speakText (bản tin sale/ceo). Bấm lần nữa khi đang đọc = dừng.
function SpeakButton({ text }) {
  const Icon = window.Icon;
  const [st, setSt] = iS('idle');           // idle | loading | playing
  const ctrl = React.useRef(null);

  iE(() => () => { if (ctrl.current) ctrl.current.stop(); }, []);   // rời trang → dừng đọc

  const toggle = (e) => {
    e.stopPropagation();                      // đừng để bấm nút bị hiểu là "đánh dấu đã đọc"
    if (st !== 'idle') { if (ctrl.current) ctrl.current.stop(); setSt('idle'); return; }
    setSt('loading');
    ctrl.current = window.tourkitTts.speak(text, {
      onStart: () => setSt('playing'),
      onEnd: () => setSt('idle'),
      onError: () => setSt('idle'),
    });
  };

  const icon = st === 'idle' ? 'volume' : (st === 'loading' ? 'refresh' : 'stop');
  const label = st === 'idle' ? 'Nghe' : (st === 'loading' ? 'Đang tải…' : 'Dừng');
  return (
    <button type="button" className={'insights-speak' + (st === 'playing' ? ' is-on' : '')}
      onClick={toggle} title="Nghe bản tin bằng giọng đọc">
      <Icon name={icon} size={13} /> {label}
    </button>
  );
}
```

- [ ] **Step 2: Gắn nút vào card** — cùng file, trong `items.map(it => ...)`, NGAY SAU khối `<div className="insights-card-body" ... />` (dòng ~169) và TRƯỚC dòng `{!it.username && ...}`, chèn:

```jsx
              {it.speakText && (
                <div className="insights-card-actions" onClick={e => e.stopPropagation()}>
                  <SpeakButton text={it.speakText} />
                </div>
              )}
```

- [ ] **Step 3: Verify tay** — `dotnet run`, mở tab Bảng tin (trang Tự động hoá). Với 1 dòng bản tin sáng: có nút **Nghe** → bấm → "Đang tải…" → phát giọng đọc + đổi thành **Dừng**; bấm Dừng → im. Dòng "Cảnh báo thanh toán" KHÔNG có nút. Bấm nút Nghe KHÔNG làm dòng chuyển sang đã đọc (chấm cam vẫn còn tới khi bấm vào phần thân card).

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/insights.jsx
git commit -m "feat(digest): nút Nghe bản tin sáng bằng giọng đọc trong Bảng tin (C5)"
```

---

### Task 6: Docs + verify tổng

**Files:**
- Modify: `CLAUDE.md` (bảng API surface dòng `GET /api/v1/insights` + mục "Bản tin AI")

- [ ] **Step 1: Cập nhật CLAUDE.md** — tại dòng bảng API surface của `GET /api/v1/insights`, đổi phần mô tả trả về, thêm `speakText`:

```
| GET    | `/api/v1/insights`                | Bảng tin trong app `?kind=&unread=&offset=&limit=` → `{items[…]}`; item bản tin (sale/ceo) kèm `speakText` (đã bỏ markdown/emoji để đọc TTS) (require X-Session-Id) |
```

Và trong section "Bản tin AI", thêm 1 câu ở đoạn "Giao diện": `Item bản tin trong Bảng tin có nút **Nghe** (đọc qua `/api/v1/speech/tts`, giọng server đồng nhất) — text đọc do `BriefNarration.ToSpeakable` làm sạch từ markdown; mỗi người chỉ nhận 1 loại bản tin theo vai trò (bật loại này tự tắt loại kia).`

- [ ] **Step 2: Verify tổng**

Run: `dotnet build TourkitAiProxy.csproj`
Expected: 0 error.

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: toàn bộ PASS (bao gồm 7 test `BriefNarration` mới), 0 fail.

Run (nếu bundle prod): `.\build-frontend.ps1`
Expected: esbuild ghi `wwwroot/dist/app.bundle.js` không lỗi (xác nhận `lib/tts.js` vào bundle — grep `tourkitTts` trong dist).

Verify tay end-to-end: đăng nhập → tab Tác vụ bật `ceo-brief` (Lưu) → bật `sale-brief` (Lưu) → tải lại: chỉ `sale-brief` bật (luật 1-loại). Bấm **Gửi thử** → sang tab Bảng tin → dòng `[Gửi thử]` có nút **Nghe** → nghe được.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(digest): C5 — nút Nghe bản tin + speakText + luật 1 loại/người"
```

---

## Self-review (chạy sau khi viết plan)

- **Spec coverage (C5 "reuse C1 + TTS"):** reuse C1 = nội dung ceo-brief/sale-brief đã lưu trong `AgentInsights` (Task 3 đọc `it.Body`); reuse TTS = `POST /speech/tts` sẵn có (Task 4 gọi lại, KHÔNG dựng engine mới). Nghe = Task 5. Luật 1-loại/người (yêu cầu bổ sung của user 13/08) = Task 1. ✓
- **Placeholder scan:** không có TBD; mọi step có code/lệnh cụ thể + kết quả mong đợi. Icon `volume`/`stop` cấp SVG thật (Task 4 Step 4); regex/CSS class đặt tên rõ. Các class CSS mới (`digest-role-note`, `insights-speak`, `insights-card-actions`) chỉ cần style tối thiểu — dùng lại design system `wga-*`; nếu cần tinh chỉnh, thêm vào `styles.css` là việc CSS thuần, không chặn chức năng (nút vẫn bấm được không cần CSS).
- **Type consistency:** `ToSpeakable(string?)` (Task 2) khớp cách gọi ở Task 3; `DeactivateOthersAsync(tenant, username, keepBriefType, ct)` (Task 1 repo) khớp lời gọi ở PUT handler; `window.tourkitTts.speak(text, {onStart,onEnd,onError})` (Task 4) khớp cách dùng trong `SpeakButton` (Task 5); field `speakText` (Task 3 sinh ra) = `it.speakText` (Task 5 đọc). ✓
- **Đánh đổi đã ghi:** narration đặt server (1 hàm test được) thay vì strip trong JS → thêm `speakText` vào payload list (nhẹ, chỉ item bản tin). JARVIS giữ bản TTS streaming riêng — chấp nhận 2 đường vì input khác hẳn (hội thoại dài vs bản tin ngắn).
