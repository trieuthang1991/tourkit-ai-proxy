# Hộp thư chat — Đợt 3: Nối khách CRM (mục 4, việc 1 + việc 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Khi khách chat đã cho số điện thoại, hộp thư **gợi ý** đúng khách CRM để nhân viên xác nhận nối; khi CRM chưa có, nhân viên **tạo khách mới** ngay từ hội thoại bằng tên/số/email đã có.

**Architecture:** Việc 1 thuần giao diện: `NoiCrm` gọi `GET …/crm-search?q=<số điện thoại>` (đường có sẵn, tìm bằng phiên của chính nhân viên) và bày kết quả như "có thể là…". Việc 2 là một endpoint mới `POST /conversations/{id}/crm-customer` (không thân) gọi `POST /api/customers` của CRM rồi `LinkCrmAsync`. Proxy **tự kiểm quyền** `KH_KH_TAOMOI` vì CRM không kiểm ở `CreateAsync`.

**Tech Stack:** .NET 8 minimal API · `TourKitApiClient.PostAsync` (trả `data` đã bóc) · React/Babel UMD · xUnit · Playwright.

**Spec:** [docs/superpowers/specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §5.

**Việc 3 (đẩy nhật ký chăm sóc sang CRM) KHÔNG nằm trong plan này** — chờ chốt "theo hội thoại hay theo lượt" (spec §10 câu 4).

## Global Constraints

- Chữ hiển thị, log, chú thích: tiếng Việt. Ngày giờ UTC kèm `Z`. `CHANGELOG.md` bắt buộc.
- `codegraph impact` trước khi sửa: `LinkCrmAsync` (1 caller), `NoiCrm` (1 chỗ vẽ).
- Route mới **không thân** → không khai tham số thân. Mọi dữ liệu lấy từ `chat_contacts`.
- **CRM `CustomerService.CreateAsync` không kiểm quyền** (đã soát) → proxy kiểm `KH_KH_TAOMOI` qua `TkSessionStore.HasPermission` sau `EnsurePermissionsAsync`.
- CRM từ chối số điện thoại trùng bằng `InvalidOperationException("Số điện thoại đã tồn tại")` → HTTP 400 → `TourKitApiException(Status 400)`; proxy trả nguyên câu đó cho người dùng.
- Nhật ký: hành động mới `tao-khach-crm` PHẢI có nhãn trong `TEN_HANH_DONG` (chat-inbox.jsx:682) — `ChatAuditGuardTests` đỏ nếu thiếu.
- `ChatCrmLinkGuardTests` đang giữ: `crm-search`, `link-crm`, `LinkCrmAsync` kẹp `tenant_id = @tenant`, tìm bằng `a.SessionId`. Không phá.
- **Ghi vào CRM staging** là được phép; **cấm ghi erp**. E2E dùng số điện thoại thử cố định để lần chạy sau không tạo thêm khách rác.
- Máy chủ khoá DLL → dừng trước build/test. Toàn bộ `dotnet test` cuối mỗi task. E2E worker tắt, cấm `/send`.
- Commit trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; không commit 5 tệp đang dở.

---

## File Structure

| Tệp | Trách nhiệm |
|---|---|
| `wwwroot/pages/chat-inbox.jsx` — `NoiCrm` (1125–1205) | việc 1: gợi ý theo số ĐT; việc 2: nút *Tạo khách mới trên CRM*; nhãn nhật ký |
| `TourkitAiProxy.Infrastructure/TourKit/TkPermissionCodes.cs` | `TaoKhachHang = "KH_KH_TAOMOI"` |
| `TourkitAiProxy.Endpoints/SessionAuth.cs` | `ForbiddenTaoKhachHang()` |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `POST /conversations/{id:long}/crm-customer` |
| `TourkitAiProxy.Tests/Chat/ChatCrmLinkGuardTests.cs` | thêm chốt: tạo khách phải kiểm quyền + nối ngay + ghi nhật ký |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `G — Tạo khách CRM` |
| `CHANGELOG.md` | hai mục |

---

### Task 1: Gợi ý nối theo số điện thoại (thuần giao diện)

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — `NoiCrm`

**Interfaces:**
- Consumes: `GET /api/v1/chat/conversations/{id}/crm-search?q=` → `{items:[{id,name,phone,code}]}`; `chiTiet.contact.phone`.

- [ ] **Step 1: State + effect** — trong `NoiCrm`, sau `const [dangLam, setDangLam] = useState(false);`:

```jsx
    // Gợi ý theo SỐ ĐIỆN THOẠI khi kênh cho số (WhatsApp luôn có; Zalo khi khách chia sẻ).
    // Chỉ GỢI Ý, người bấm mới nối: đoán sai một lần là bot đọc lịch sử mua của khách khác.
    const [goiY, setGoiY] = useState(null);   // null = chưa hỏi/không có; [] = hỏi rồi, không khớp
    useEffect(() => {
      const so = (lh?.phone || '').trim();
      if (lh?.crmCustomerId || !so || !v?.id) { setGoiY(null); return; }
      let song = true;
      authedFetch('/api/v1/chat/conversations/' + v.id + '/crm-search?q=' + encodeURIComponent(so))
        .then(r => (r.ok ? r.json() : { items: [] }))
        .then(j => { if (song) setGoiY((j.items || []).slice(0, 3)); })
        .catch(() => { if (song) setGoiY([]); });
      return () => { song = false; };
    }, [v?.id, lh?.phone, lh?.crmCustomerId]);
```

- [ ] **Step 2: Bày gợi ý** — trong nhánh `if (!mo)` (khối "Chưa nối với khách hàng trong CRM…"), thêm **trước** `<div className="ci-hs-crm-nut">`:

```jsx
          {goiY && goiY.length > 0 && (
            <div className="ci-hs-goiy">
              <span>Cùng số điện thoại trên CRM:</span>
              {goiY.map(k => (
                <button key={k.id} className="ci-hs-crm-kq" disabled={dangLam} onClick={() => doiNoi(k.id)}>
                  <b>{k.name}</b>
                  <span>{[k.code, k.phone].filter(Boolean).join(' · ') || '#' + k.id}</span>
                </button>
              ))}
            </div>
          )}
```
và CSS (styles.css, cạnh `.ci-hs-crm-kq`):
```css
.ci-hs-goiy { display: grid; gap: 6px; margin: 8px 0; }
.ci-hs-goiy > span { font-size: 11.5px; color: var(--text-3); }
```

- [ ] **Step 3: Dựng bundle, kiểm tay** — hội thoại WhatsApp hoặc Zalo đã chia sẻ số trên staging: mở hồ sơ → thấy "Cùng số điện thoại trên CRM: …" (nếu CRM có khách cùng số) → bấm → nối. Không có hội thoại nào có số thì ghi rõ trong commit là **chưa kiểm tay**, đừng viết "đã kiểm".

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx wwwroot/styles.css
git commit -m "feat(chat): gợi ý khách CRM cùng số điện thoại — người xác nhận mới nối

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Mã quyền + câu từ chối

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/TourKit/TkPermissionCodes.cs` (sau `XemKhachHang`)
- Modify: `TourkitAiProxy.Endpoints/SessionAuth.cs` (sau `ForbiddenCreateTour`, dòng ~118)

- [ ] **Step 1:**

```csharp
    /// Khách hàng — tạo mới. PermissionCodes.cs:67. Gate "Tạo khách mới trên CRM" từ hộp thư chat —
    /// CRM KHÔNG kiểm ở CustomerService.CreateAsync, nên proxy phải kiểm thay.
    public const string TaoKhachHang = "KH_KH_TAOMOI";
```

```csharp
    public static IResult ForbiddenTaoKhachHang()
        => Results.Json(new { error = "Bạn không có quyền tạo khách hàng (KH_KH_TAOMOI)." }, statusCode: 403);
```

- [ ] **Step 2: Build xanh. Commit** — `git commit -m "feat(auth): mã quyền tạo khách hàng cho hộp thư chat"` (kèm trailer).

---

### Task 3: `POST /conversations/{id}/crm-customer` — tạo khách rồi nối

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` — ngay sau route `link-crm`
- Modify: `wwwroot/pages/chat-inbox.jsx:682` — `TEN_HANH_DONG` thêm `'tao-khach-crm': 'tạo khách mới trên CRM'`
- Test: `TourkitAiProxy.Tests/Chat/ChatCrmLinkGuardTests.cs`

**Interfaces:**
- Consumes: `repo.GetContactAsync(tenant, kenh, externalId, ct)` (ChatRepository.cs:580) → `ChatContact {DisplayName, Phone, Email, CrmCustomerId}`; `api.PostAsync(jwt, "/api/customers", body, ct)` → `JsonElement` là `data` = `{ "id": <int> }`; `sessions.GetValidJwtAsync`, `sessions.ForceReloginAsync`; `repo.LinkCrmAsync`.
- Produces: `200 {ok, crmCustomerId}` · `403` thiếu quyền · `409 {error}` đã nối · `422 {error}` thiếu tên · `400 {error}` CRM từ chối (trùng số).

- [ ] **Step 1: Chốt canh ĐỎ** — thêm vào `ChatCrmLinkGuardTests`:

```csharp
    private static string ThanTaoKhach()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
        src = string.Join("\n", src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var i = src.IndexOf("MapPost(\"/conversations/{id:long}/crm-customer\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "Chưa có route crm-customer");
        var sau = src.Substring(i);
        var het = sau.IndexOf("\n        g.Map", 10, StringComparison.Ordinal);
        return het > 0 ? sau.Substring(0, het) : sau;
    }

    [Fact]
    public void Tao_khach_CRM_phai_kiem_quyen_o_proxy_vi_CRM_khong_kiem()
    {
        var than = ThanTaoKhach();
        Assert.Contains("TkPermissionCodes.TaoKhachHang", than);
        Assert.Contains("ForbiddenTaoKhachHang", than);
    }

    [Fact]
    public void Tao_khach_CRM_xong_phai_NOI_NGAY_va_ghi_nhat_ky()
    {
        var than = ThanTaoKhach();
        Assert.Contains("LinkCrmAsync", than);
        Assert.Contains("\"tao-khach-crm\"", than);
        Assert.Contains("\"/api/customers\"", than);
    }
```

- [ ] **Step 2: Chạy — ĐỎ** ("Chưa có route crm-customer").

- [ ] **Step 3: Viết route**

```csharp
        // Tạo khách MỚI trên CRM từ chính hồ sơ chat, rồi nối ngay. Không có thân: tên/số/email
        // lấy từ chat_contacts — nhân viên muốn sửa thì sửa bên CRM sau, ở đây chỉ một chạm.
        //
        // Proxy KIỂM QUYỀN thay CRM: CustomerService.CreateAsync bên đó không kiểm KH_KH_TAOMOI
        // (đã soát 09/09/2026), web cũ kiểm ở tầng màn hình. Bỏ dòng này là ai vào được hộp thư
        // cũng tạo được khách.
        g.MapPost("/conversations/{id:long}/crm-customer", async (long id, HttpContext ctx,
            TkSessionStore sessions, ChatRepository repo, ChatAssignRepository assign,
            TourKitApiClient api, ChatEventBus bus, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("chat.crm-customer");
            var p = await SessionAuth.ReadNguoiXemAsync(ctx, sessions, ct);
            if (p == null) return SessionAuth.Unauthorized();
            var (a, xem) = p.Value;
            if (!repo.Configured) return NotConfigured();

            await sessions.EnsurePermissionsAsync(a.SessionId, ct);
            if (!sessions.HasPermission(a.SessionId, TkPermissionCodes.TaoKhachHang))
                return SessionAuth.ForbiddenTaoKhachHang();

            var v = await repo.GetConversationAsync(a.TenantId, id, xem, ct);
            if (v is null) return Results.NotFound();
            var lh = await repo.GetContactAsync(a.TenantId, v.Channel, v.ContactExternalId, ct);
            if (lh is null) return Results.NotFound();
            if (lh.CrmCustomerId is { } daCo)
                return Results.Json(new { error = "Khách này đã nối với khách CRM #" + daCo + ". Gỡ nối trước nếu muốn tạo mới." }, statusCode: 409);
            var ten = (lh.DisplayName ?? "").Trim();
            if (ten.Length == 0)
                return Results.Json(new { error = "Chưa có tên khách để tạo — kênh không gửi tên. Nối tay với khách có sẵn." }, statusCode: 422);

            // CRM trả {id}; trùng số điện thoại thì CRM từ chối bằng 400 kèm câu — trả nguyên cho
            // người dùng, họ sẽ dùng "Nối khách CRM" tìm theo số thay vì tạo.
            var payload = new { FullName = ten, PhoneNumber = lh.Phone, Email = lh.Email };
            JsonElement data;
            try
            {
                var jwt = await sessions.GetValidJwtAsync(a.SessionId, ct);
                try { data = await api.PostAsync(jwt, "/api/customers", payload, ct); }
                catch (TourKitApiException ex) when (ex.Status == 401)
                {
                    jwt = await sessions.ForceReloginAsync(a.SessionId, ct);
                    data = await api.PostAsync(jwt, "/api/customers", payload, ct);
                }
            }
            catch (TourKitApiException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }

            if (!data.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var maKhach) || maKhach <= 0)
            {
                log.LogWarning("[chat/crm-customer] CRM tạo khách nhưng không trả id đọc được: {Json}", data.ToString());
                return Results.Json(new { error = "CRM không trả mã khách vừa tạo — kiểm tra bên CRM rồi nối tay." }, statusCode: 502);
            }

            await repo.LinkCrmAsync(a.TenantId, v.Channel, v.ContactExternalId, maKhach, ct);
            await GhiNhatKyAsync(ctx, repo, sessions, a, id, "tao-khach-crm",
                new JsonObject { ["khachCrm"] = maKhach }.ToJsonString(), ct);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null) { AssignedUserId = v.AssignedUserId });
            return Results.Json(new { ok = true, crmCustomerId = maKhach }, Web);
        });
```

- [ ] **Step 4: Nhãn nhật ký** — `TEN_HANH_DONG` thêm `'tao-khach-crm': 'tạo khách mới trên CRM',` (đặt cạnh `'noi-crm'`/`'go-noi-crm'` nếu có; không có thì cạnh `'chuyen-viec'`).

- [ ] **Step 5: Toàn bộ test — XANH** (kể cả `ChatAuditGuardTests` nhờ nhãn ở bước 4). Chứng minh chốt đỏ: xoá tạm dòng `HasPermission` → chốt quyền ĐỎ; khôi phục.

- [ ] **Step 6: Gọi thật trên staging** — hội thoại thử `e2e-1` (khách "Khach thu E2E 1", chưa nối):
```
curl -s -X POST http://localhost:5080/api/v1/chat/conversations/<id>/crm-customer -H "X-Session-Id: <sid admin>"
```
Expected lần 1: `{"ok":true,"crmCustomerId":N}`; lần 2: 409 "đã nối". Với phiên `ketoan1` (không có KH_KH_TAOMOI): 403. **Sau khi thử: gỡ nối** (`POST …/link-crm` thân `{}`) để dữ liệu thử về nguyên trạng; khách CRM "Khach thu E2E 1" giữ lại làm khách thử cố định cho e2e.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs wwwroot/pages/chat-inbox.jsx TourkitAiProxy.Tests/Chat/ChatCrmLinkGuardTests.cs
git commit -m "feat(chat): tạo khách mới trên CRM từ hồ sơ chat rồi nối ngay

Proxy kiểm KH_KH_TAOMOI vì CRM không kiểm ở CreateAsync (đã chứng minh chốt đỏ).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Nút **Tạo khách mới trên CRM**

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — `NoiCrm`, nhánh `if (!mo)`

- [ ] **Step 1: Hàm**

```jsx
    async function taoKhach() {
      const ok = window.appConfirm
        ? await window.appConfirm('Tạo khách "' + (lh?.displayName || '') + '" trên CRM với số/email đang có, rồi nối ngay?',
                                  { title: 'Tạo khách mới trên CRM', confirmLabel: 'Tạo và nối' })
        : window.confirm('Tạo khách mới trên CRM rồi nối?');
      if (!ok) return;
      setDangLam(true);
      try {
        const r = await authedFetch('/api/v1/chat/conversations/' + v.id + '/crm-customer', { method: 'POST' });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { pushToast(j.error || 'Không tạo được khách', 'error'); return; }
        pushToast('Đã tạo khách CRM #' + j.crmCustomerId + ' và nối', 'success');
      } finally { setDangLam(false); }
    }
```

- [ ] **Step 2: Nút** — trong `<div className="ci-hs-crm-nut">` của nhánh chưa nối, sau nút *Nối khách CRM*:

```jsx
            {lh?.displayName && (
              <button className="ci-nut nho" disabled={dangLam} onClick={taoKhach}
                      title="Tạo khách trên CRM bằng tên, số và email đang có">
                Tạo khách mới
              </button>
            )}
```

- [ ] **Step 3: Dựng bundle, kiểm tay** — bấm → hộp hỏi → tạo → panel đổi sang "Đã nối với khách #N" (nhờ sự kiện `doi-hoi-thoai` làm mới hồ sơ; nếu không tự đổi, gọi `onDoi` — xem `onGuiXong` ở dòng 3271 làm mẫu và truyền `onDoi={() => taiChiTiet(chon)}` vào `NoiCrm`).

- [ ] **Step 4: Commit** — `git commit -m "feat(chat): nút Tạo khách mới trên CRM trong hồ sơ khách"` (kèm trailer).

---

### Task 5: E2E + CHANGELOG

- [ ] **Step 1: E2E** — nhóm mới trong 07 spec:

```js
test.describe('G — Tạo khách CRM từ chat', () => {
  test('G1 — nhân viên không có quyền tạo khách → 403 JSON', async () => {
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/crm-customer`, { headers: nhu(PHIEN_NHAN_VIEN) }));
    expect(r.laHtml).toBe(false);
    expect([403, 404], `mã ${r.ma}`).toContain(r.ma);   // 404 nếu luật xem chặn trước quyền
  });

  test('G2 — quản trị: tạo được (200) hoặc bị CRM từ chối có câu (400/409) — không bao giờ 500/HTML', async () => {
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/crm-customer`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.laHtml).toBe(false);
    expect([200, 400, 409, 422], `mã ${r.ma}: ${JSON.stringify(r.json)}`).toContain(r.ma);
    if (r.ma !== 200) expect(r.json.error, 'từ chối phải có câu đọc được').toBeTruthy();
    // Trả lại nguyên trạng: hội thoại thử không nối ai.
    await api.post(`${GOC}/conversations/${maHoiThoai}/link-crm`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: {},
    });
  });
});
```

- [ ] **Step 2: CHANGELOG** — `### ✨ Tính năng mới`:

```markdown
- **Gợi ý khách CRM cùng số điện thoại.** Khách chat có số (WhatsApp, hoặc Zalo khi khách chia
  sẻ số) thì hồ sơ khách hiện ngay những khách CRM trùng số để bạn bấm nối — hệ thống không tự
  nối, vì nối nhầm là trợ lý đọc lịch sử mua của người khác.
- **Tạo khách mới trên CRM từ hội thoại.** Chưa có trên CRM thì một nút tạo bằng tên, số và
  email đang có, rồi nối luôn. Cần quyền tạo khách hàng; trùng số thì CRM báo và bạn nối với
  khách sẵn có thay vì tạo.
```

- [ ] **Step 3: Chạy toàn bộ; commit**

```bash
git add e2e/tests/07-chat-phan-cong-api.spec.js CHANGELOG.md
git commit -m "test(e2e)+docs: tạo khách CRM từ chat — quyền, và không bao giờ 500/HTML

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** §5 việc 1 (gợi ý theo số, không tự nối) → Task 1; việc 2 (tạo khách, tiền đề mục 3) → Task 2–4; ràng buộc "CRM không kiểm quyền" → Task 2–3 + chốt; việc 3 → cố ý ngoài phạm vi, ghi ở đầu.
- **Placeholder:** không.
- **Nhất quán tên:** `TaoKhachHang`/`ForbiddenTaoKhachHang` (Task 2) ↔ dùng ở Task 3 ↔ chốt canh; hành động `tao-khach-crm` ở route ↔ `TEN_HANH_DONG`; route `crm-customer` ở Task 3–5.
