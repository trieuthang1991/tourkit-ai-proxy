# Hộp thư chat — Đợt 4: Tạo Cơ hội bán hàng từ hội thoại (mục 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Từ một hội thoại đã nối khách CRM, nhân viên bấm một nút để tạo **Cơ hội bán hàng** (= BookingTicket) trên CRM, mang theo tóm tắt đoạn chat.

**Architecture:** CRM đã có `POST /api/booking-tickets` (`CreateBookingTicketRequest`). Proxy thêm `POST /conversations/{id}/co-hoi` (có thân: tiêu đề + ghi chú tuỳ chọn), **tự kiểm quyền `CH_TAO_MOI`** vì CRM không kiểm ở `CreateAsync`, đòi hội thoại đã nối khách (`IdKhachHang > 0` là bắt buộc bên CRM), dựng `NoiDungPhieu` từ tóm tắt tin bằng một hàm thuần, gọi CRM, ghi nhật ký. Không lưu mã phiếu phía chat (nhật ký đã ghi).

**Tech Stack:** .NET 8 minimal API · `TourKitApiClient.PostAsync` · React/Babel UMD · xUnit · Playwright.

**Spec:** [docs/superpowers/specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §6. **Phụ thuộc:** Đợt 3 (hội thoại phải nối được khách CRM).

## Global Constraints

- Chữ hiển thị, log, chú thích: tiếng Việt. Ngày giờ UTC kèm `Z`. `CHANGELOG.md` bắt buộc.
- **Cơ hội bán hàng = BookingTicket** (`toutkit-app/docs/module-mapping.md`). Không nhầm sang Lead.
- Route có thân → giao diện **phải** gửi `Content-Type: application/json`; thân là record **không nullable** (`CoHoiReq`), như `AssignReq`.
- **CRM `BookingTicketService.CreateAsync` không kiểm quyền** (chỉ `CH_XEM*` khi xem, `CH_SUA` khi sửa — đã soát) → proxy kiểm `CH_TAO_MOI`.
- CRM bắt buộc `IdKhachHang > 0` và `TenKH` — thiếu là `FailMsg` 400.
- `NguonPhieu` là mã số; web cũ dùng `3` cho đại lý. **Chưa có mã cho "chat"** → đọc từ cấu hình `Chat:NguonPhieuCoHoi` (mặc định `1`), đổi khi bên CRM cấp mã (spec §10 câu 5).
- Nhật ký: hành động mới `tao-co-hoi` phải có nhãn trong `TEN_HANH_DONG`.
- Ghi vào CRM staging được; cấm erp. E2E: bài tạo THẬT chỉ chạy khi `E2E_TAO_CO_HOI=1` (mỗi lần chạy là một phiếu mới trên staging).
- Máy chủ khoá DLL → dừng trước build/test. Toàn bộ `dotnet test` cuối mỗi task. E2E worker tắt, cấm `/send`.
- Commit trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; không commit 5 tệp đang dở.

---

## File Structure

| Tệp | Trách nhiệm |
|---|---|
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | `TomTatChoCoHoi` — thuần: N tin cuối thành văn bản "Khách: … / Nhân viên: …" |
| `TourkitAiProxy.Infrastructure/TourKit/TkPermissionCodes.cs` | `TaoCoHoi = "CH_TAO_MOI"` |
| `TourkitAiProxy.Endpoints/SessionAuth.cs` | `ForbiddenTaoCoHoi()` |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `POST /conversations/{id:long}/co-hoi` + `record CoHoiReq` |
| `wwwroot/pages/chat-inbox.jsx` | khối **Cơ hội bán hàng** trong tab Khách hàng CRM; nhãn nhật ký |
| `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs` | test `TomTatChoCoHoi` |
| `TourkitAiProxy.Tests/Chat/ChatCoHoiGuardTests.cs` (mới) | chốt: kiểm quyền, đòi nối khách, ghi nhật ký |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `H — Tạo Cơ hội` |
| `CHANGELOG.md` | một mục |

---

### Task 1: Hàm thuần tóm tắt đoạn chat

**Files:**
- Modify: `TourkitAiProxy.Domain/Chat/ChatRules.cs`
- Test: `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs`

**Interfaces:**
- Produces: `public static string TomTatChoCoHoi(IEnumerable<ChatMessage> tin, int soTin, string duongDan)`.

- [ ] **Step 1: Test ĐỎ**

```csharp
    [Fact]
    public void Tom_tat_cho_co_hoi__N_tin_cuoi__ghi_ro_ai_noi__kem_duong_dan()
    {
        var ds = new[]
        {
            Tin((short)ChatDirection.In,  "Tin cũ nhất, phải bị cắt"),
            Tin((short)ChatDirection.In,  "Cho hỏi tour Nhật"),
            Tin((short)ChatDirection.Out, "Dạ anh đi tháng mấy ạ?"),
            Tin((short)ChatDirection.In,  null, kind: (short)ChatKind.Image),
            Tin((short)ChatDirection.In,  "Tháng 10, 4 người"),
        };
        var ra = ChatRules.TomTatChoCoHoi(ds, 3, "https://travelai.vn/chat-inbox?hoi-thoai=53");
        Assert.DoesNotContain("Tin cũ nhất", ra);
        Assert.Contains("Khách: Cho hỏi tour Nhật", ra);
        Assert.Contains("Nhân viên: Dạ anh đi tháng mấy ạ?", ra);
        Assert.Contains("Khách: Tháng 10, 4 người", ra);
        Assert.EndsWith("https://travelai.vn/chat-inbox?hoi-thoai=53", ra.TrimEnd());
    }

    [Fact]
    public void Tom_tat_cho_co_hoi__khong_co_tin_chu_thi_van_co_duong_dan()
    {
        var ra = ChatRules.TomTatChoCoHoi(System.Array.Empty<ChatMessage>(), 5, "https://x/y");
        Assert.Contains("https://x/y", ra);
    }
```
(`Tin(...)` là helper đã thêm ở Đợt 2 Task 1; nếu Đợt 2 chưa chạy, chép helper đó vào đây.)

- [ ] **Step 2: Chạy — ĐỎ.** **Step 3: Viết hàm** (đặt cạnh `TachCauHoiCuoi`):

```csharp
    /// <summary>
    /// Tóm tắt đoạn chat để ghi vào <c>NoiDungPhieu</c> của Cơ hội bán hàng: N tin có chữ gần nhất,
    /// mỗi dòng ghi rõ ai nói, và đường dẫn quay lại hội thoại ở cuối. Không gọi AI — đây là
    /// TRÍCH, không phải diễn giải; người đọc phiếu cần đúng lời khách, không cần lời máy.
    /// </summary>
    public static string TomTatChoCoHoi(IEnumerable<ChatMessage> tin, int soTin, string duongDan)
    {
        var dong = tin
            .Where(m => m.State != (short)ChatState.Failed && !string.IsNullOrWhiteSpace(m.Body))
            .TakeLast(Math.Max(1, soTin))
            .Select(m => (m.Direction == (short)ChatDirection.In ? "Khách: " : "Nhân viên: ") + m.Body!.Trim());
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Trích từ hộp thư chat:");
        foreach (var d in dong) sb.AppendLine(d);
        sb.AppendLine();
        sb.Append("Xem hội thoại: ").Append(duongDan);
        return sb.ToString();
    }
```

- [ ] **Step 4: XANH. Commit** — `git commit -m "feat(chat): tóm tắt đoạn chat cho Cơ hội bán hàng — trích, không diễn giải"` (kèm trailer).

---

### Task 2: Mã quyền + câu từ chối

**Files:** `TkPermissionCodes.cs`, `SessionAuth.cs`

- [ ] **Step 1:**

```csharp
    /// Cơ hội bán hàng (BookingTicket) — tạo mới. PermissionCodes.cs:247. CRM KHÔNG kiểm ở
    /// BookingTicketService.CreateAsync (chỉ kiểm xem/sửa), nên proxy kiểm thay.
    public const string TaoCoHoi = "CH_TAO_MOI";
```
```csharp
    public static IResult ForbiddenTaoCoHoi()
        => Results.Json(new { error = "Bạn không có quyền tạo Cơ hội bán hàng (CH_TAO_MOI)." }, statusCode: 403);
```

- [ ] **Step 2: Build xanh. Commit** — `git commit -m "feat(auth): mã quyền tạo Cơ hội bán hàng cho hộp thư chat"` (kèm trailer).

---

### Task 3: Endpoint `POST /conversations/{id}/co-hoi`

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` — route sau `crm-customer` (Đợt 3) hoặc sau `link-crm`; record `CoHoiReq` cạnh `AssignReq` (dòng ~3191)
- Modify: `wwwroot/pages/chat-inbox.jsx:682` — `TEN_HANH_DONG` thêm `'tao-co-hoi': 'tạo Cơ hội bán hàng'`
- Test: `TourkitAiProxy.Tests/Chat/ChatCoHoiGuardTests.cs` (mới)

**Interfaces:**
- Consumes: `ChatRules.TomTatChoCoHoi`, `repo.GetContactAsync`, `repo.ListMessagesAsync(tenant, id, 12, ct)`, `api.PostAsync(jwt, "/api/booking-tickets", …)` → `data` là **số** (id phiếu), `PublicOrigin(ctx, cfg)` (helper có sẵn trong file, dùng ở `/channels`).
- Produces: `200 {ok, crmTicketId}` · `403` · `409 {error}` chưa nối khách · `400/502 {error}` CRM từ chối.

- [ ] **Step 1: Chốt canh ĐỎ**

```csharp
// TourkitAiProxy.Tests/Chat/ChatCoHoiGuardTests.cs
using System;
using System.Linq;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Tạo Cơ hội bán hàng là lượt GHI đầu tiên từ hộp thư chat sang dữ liệu bán hàng thật của công
/// ty. Ba chốt, mỗi chốt chặn một cách hỏng đã thấy ở nơi khác:
///  · kiểm quyền ở proxy — vì CRM không kiểm ở CreateAsync;
///  · đòi khách đã nối — vì CRM đòi IdKhachHang, thiếu là 400 vô nghĩa với người dùng;
///  · ghi nhật ký — vì phía chat không lưu mã phiếu, nhật ký là dấu vết duy nhất.
/// </summary>
public class ChatCoHoiGuardTests
{
    private static string Than()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        src = string.Join("\n", src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/co-hoi\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "Chưa có route co-hoi");
        var sau = src.Substring(i);
        var het = sau.IndexOf("\n        g.Map", 10, StringComparison.Ordinal);
        return het > 0 ? sau.Substring(0, het) : sau;
    }

    [Fact] public void Kiem_quyen_o_proxy() { var t = Than(); Assert.Contains("TkPermissionCodes.TaoCoHoi", t); Assert.Contains("ForbiddenTaoCoHoi", t); }
    [Fact] public void Doi_khach_da_noi() { Assert.Contains("CrmCustomerId", Than()); Assert.Contains("statusCode: 409", Than()); }
    [Fact] public void Ghi_nhat_ky_va_goi_dung_duong_CRM() { var t = Than(); Assert.Contains("\"tao-co-hoi\"", t); Assert.Contains("\"/api/booking-tickets\"", t); }
}
```

- [ ] **Step 2: ĐỎ.** **Step 3: Record + route**

```csharp
    /// Tiêu đề và ghi chú thêm cho Cơ hội — cả hai tuỳ chọn, nhưng THÂN thì bắt buộc (như AssignReq):
    /// giao diện luôn gửi JSON, và tham số thân nullable là dính bẫy Content-Type.
    public record CoHoiReq(string? TieuDe, string? GhiChu);
```

```csharp
        // ── Tạo Cơ hội bán hàng (BookingTicket) từ hội thoại ───────────────────
        g.MapPost("/conversations/{id:long}/co-hoi", async (long id, CoHoiReq body, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatAssignRepository assign,
            TourKitApiClient api, IConfiguration cfg, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.co-hoi");
            var p = await SessionAuth.ReadNguoiXemAsync(ctx, sessions, ct);
            if (p == null) return SessionAuth.Unauthorized();
            var (a, xem) = p.Value;
            if (!repo.Configured) return NotConfigured();

            // Proxy kiểm thay CRM — BookingTicketService.CreateAsync không kiểm CH_TAO_MOI.
            await sessions.EnsurePermissionsAsync(a.SessionId, ct);
            if (!sessions.HasPermission(a.SessionId, TkPermissionCodes.TaoCoHoi))
                return SessionAuth.ForbiddenTaoCoHoi();

            var v = await repo.GetConversationAsync(a.TenantId, id, xem, ct);
            if (v is null) return Results.NotFound();
            var lh = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            // CRM bắt buộc IdKhachHang > 0. Đòi ở đây để câu lỗi nói đúng việc phải làm.
            if (lh?.CrmCustomerId is not { } maKhach || maKhach <= 0)
                return Results.Json(new { error = "Nối hội thoại với khách CRM trước, rồi mới tạo Cơ hội." }, statusCode: 409);

            var ten = (lh.DisplayName ?? v.ContactExternalId).Trim();
            var duongDan = $"{PublicOrigin(ctx, cfg)}/chat-inbox?hoi-thoai={id}";
            var tin = await repo.ListMessagesAsync(a.TenantId, id, 12, ct);
            var tomTat = ChatRules.TomTatChoCoHoi(tin, 8, duongDan);
            var noiDung = string.IsNullOrWhiteSpace(body.GhiChu) ? tomTat : body.GhiChu.Trim() + "\n\n" + tomTat;

            var payload = new
            {
                TenKH = ten, SoDienThoaiKH = lh.Phone, EmailKH = lh.Email,
                TenPhieu = string.IsNullOrWhiteSpace(body.TieuDe) ? $"Chat {ten}" : body.TieuDe.Trim(),
                NoiDungPhieu = noiDung,
                IdKhachHang = maKhach,
                SoLuong = 1,
                TrangThaiPhieu = 1,
                // Mã nguồn phiếu cho "từ chat" chưa được CRM cấp — đọc từ cấu hình, mặc định 1.
                NguonPhieu = cfg.GetValue("Chat:NguonPhieuCoHoi", 1),
            };

            JsonElement data;
            try
            {
                var jwt = await sessions.GetValidJwtAsync(a.SessionId, ct);
                try { data = await api.PostAsync(jwt, "/api/booking-tickets", payload, ct); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(a.SessionId, ct);
                    data = await api.PostAsync(jwt, "/api/booking-tickets", payload, ct);
                }
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }

            // CRM trả OkData(id) → data là một SỐ.
            if (data.ValueKind != JsonValueKind.Number || !data.TryGetInt32(out var maPhieu) || maPhieu <= 0)
            {
                log.LogWarning("[chat/co-hoi] CRM tạo phiếu nhưng không trả id đọc được: {Json}", data.ToString());
                return Results.Json(new { error = "CRM không trả mã Cơ hội vừa tạo — kiểm tra bên CRM." }, statusCode: 502);
            }

            await GhiNhatKyAsync(ctx, repo, sessions, a, id, "tao-co-hoi",
                new JsonObject { ["coHoi"] = maPhieu, ["khachCrm"] = maKhach }.ToJsonString(), ct);
            return Results.Json(new { ok = true, crmTicketId = maPhieu }, Web);
        });
```

- [ ] **Step 4: Nhãn nhật ký** — `'tao-co-hoi': 'tạo Cơ hội bán hàng',` vào `TEN_HANH_DONG`.

- [ ] **Step 5: Toàn bộ test XANH; chứng minh chốt đỏ** (xoá tạm `HasPermission` → đỏ; khôi phục).

- [ ] **Step 6: Gọi thật trên staging** — hội thoại thử đã nối khách (dùng Đợt 3 Task 3 để nối `e2e-1` với khách thử):
```
curl -s -X POST http://localhost:5080/api/v1/chat/conversations/<id>/co-hoi -H "X-Session-Id: <sid admin>" -H "Content-Type: application/json" -d '{"tieuDe":"Thử từ chat","ghiChu":null}'
```
Expected: `{"ok":true,"crmTicketId":N}`; mở CRM staging xem phiếu N có `NoiDungPhieu` là trích chat + đường dẫn. Chưa nối → 409. `ketoan1` → 403.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs wwwroot/pages/chat-inbox.jsx TourkitAiProxy.Tests/Chat/ChatCoHoiGuardTests.cs
git commit -m "feat(chat): tạo Cơ hội bán hàng trên CRM từ hội thoại, kèm trích đoạn chat

Proxy kiểm CH_TAO_MOI vì CRM không kiểm ở CreateAsync; đòi khách đã nối vì CRM
đòi IdKhachHang. NguonPhieu đọc từ Chat:NguonPhieuCoHoi (mặc định 1) chờ CRM cấp mã.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Khối **Cơ hội bán hàng** trong tab Khách hàng CRM

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — sau `<div className="ci-hs-muc"><h4>Khách hàng CRM</h4><NoiCrm …/></div>` (dòng ~1431–1434)

- [ ] **Step 1: Component nhỏ** (đặt cạnh `NoiCrm`):

```jsx
  function TaoCoHoi({ chiTiet, pushToast }) {
    const v = chiTiet?.conversation;
    const lh = chiTiet?.contact;
    const [dangLam, setDangLam] = useState(false);
    const daNoi = !!lh?.crmCustomerId;

    async function tao() {
      const tieuDe = window.appPrompt
        ? await window.appPrompt('Tiêu đề Cơ hội', { defaultValue: 'Chat ' + (lh?.displayName || '') })
        : window.prompt('Tiêu đề Cơ hội', 'Chat ' + (lh?.displayName || ''));
      if (tieuDe === null) return;
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + v.id + '/co-hoi', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tieuDe, ghiChu: null }),
        });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không tạo được Cơ hội', 'error'); return; }
        pushToast('Đã tạo Cơ hội #' + j.crmTicketId + ' trên CRM', 'success');
      } finally { setDangLam(false); }
    }

    if (!v) return null;
    return (
      <div className="ci-hs-muc">
        <h4>Cơ hội bán hàng</h4>
        <div className="ci-hs-crm-nut">
          <button className="ci-nut nho" disabled={dangLam || !daNoi} onClick={tao}
                  title={daNoi ? 'Tạo Cơ hội trên CRM kèm trích đoạn chat' : 'Nối khách CRM trước'}>
            {dangLam ? 'Đang tạo…' : 'Tạo Cơ hội từ hội thoại'}
          </button>
          {!daNoi && <span className="ci-pc-phu">Nối khách CRM trước.</span>}
        </div>
      </div>
    );
  }
```
> Nếu `window.appPrompt` không tồn tại trong `components/dialogs.jsx`, dùng `window.prompt` (đã có nhánh dự phòng). Đừng thêm tệp .jsx mới.

- [ ] **Step 2: Vẽ** — ngay sau khối `Khách hàng CRM`: `<TaoCoHoi chiTiet={chiTiet} pushToast={pushToast} />`.

- [ ] **Step 3: Dựng bundle, kiểm tay** — chưa nối: nút mờ + câu nhắc; đã nối: bấm → hỏi tiêu đề → toast mã phiếu; nhật ký hội thoại có dòng "tạo Cơ hội bán hàng".

- [ ] **Step 4: Commit** — `git commit -m "feat(chat): nút Tạo Cơ hội từ hội thoại trong hồ sơ khách"` (kèm trailer).

---

### Task 5: E2E + CHANGELOG

- [ ] **Step 1: E2E**

```js
test.describe('H — Tạo Cơ hội bán hàng', () => {
  const than = { tieuDe: 'E2E thử', ghiChu: null };
  test('H1 — chưa nối khách CRM → 409 có câu, không phải 400 mù của CRM', async () => {
    // Bảo đảm hội thoại thử đang KHÔNG nối ai.
    await api.post(`${GOC}/conversations/${maHoiThoai}/link-crm`, { headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: {} });
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/co-hoi`, { headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: than }));
    expect(r.laHtml).toBe(false);
    expect([409, 403], `mã ${r.ma}`).toContain(r.ma);   // 403 nếu phiên quản trị không có CH_TAO_MOI
    expect(r.json.error).toBeTruthy();
  });
  test('H2 — nhân viên thường → 403/404 JSON', async () => {
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/co-hoi`, { headers: nhu(PHIEN_NHAN_VIEN, { 'Content-Type': 'application/json' }), data: than }));
    expect(r.laHtml).toBe(false);
    expect([403, 404]).toContain(r.ma);
  });
  test('H3 — tạo THẬT (chỉ khi E2E_TAO_CO_HOI=1)', async () => {
    test.skip(process.env.E2E_TAO_CO_HOI !== '1', 'Tạo phiếu thật trên staging — bật bằng E2E_TAO_CO_HOI=1');
    // Nối tạm với khách thử cố định (mã đặt trong E2E_KHACH_THU), tạo, rồi gỡ nối.
    const khach = Number(process.env.E2E_KHACH_THU || 0);
    test.skip(!khach, 'Cần E2E_KHACH_THU = mã khách CRM thử trên staging');
    await api.post(`${GOC}/conversations/${maHoiThoai}/link-crm`, { headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { customerId: khach } });
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/co-hoi`, { headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: than }));
    await api.post(`${GOC}/conversations/${maHoiThoai}/link-crm`, { headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: {} });
    expect(r.ma, JSON.stringify(r.json)).toBe(200);
    expect(r.json.crmTicketId).toBeGreaterThan(0);
  });
});
```

- [ ] **Step 2: CHANGELOG** — `### ✨ Tính năng mới`:

```markdown
- **Tạo Cơ hội bán hàng ngay từ hội thoại.** Hội thoại đã nối khách CRM thì trong hồ sơ khách
  có nút tạo Cơ hội: bạn đặt tiêu đề, hệ thống điền tên, số, email của khách và trích sẵn mấy
  tin gần nhất kèm đường dẫn quay lại hội thoại vào nội dung phiếu. Cần quyền tạo Cơ hội; chưa
  nối khách thì nút báo rõ việc phải làm trước.
```

- [ ] **Step 3: Chạy toàn bộ; commit**

```bash
git add e2e/tests/07-chat-phan-cong-api.spec.js CHANGELOG.md
git commit -m "test(e2e)+docs: tạo Cơ hội từ chat — quyền, đòi nối khách, tạo thật khi bật cờ

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** §6 ràng buộc 1 (IdKhachHang) → Task 3 (409 + chốt); ràng buộc 2 (kiểm quyền) → Task 2–3 (+ chốt); ràng buộc 3 (NguonPhieu) → cấu hình `Chat:NguonPhieuCoHoi`; "kéo nội dung chat sang Cơ hội" → Task 1 + `NoiDungPhieu`; đính kèm tệp → cố ý để đợt sau như tracker.
- **Placeholder:** không.
- **Nhất quán tên:** `TaoCoHoi`/`ForbiddenTaoCoHoi` (Task 2) ↔ Task 3 ↔ chốt; `TomTatChoCoHoi` (Task 1) ↔ Task 3; route `co-hoi`, hành động `tao-co-hoi`, trường `crmTicketId` thống nhất Task 3–5.
