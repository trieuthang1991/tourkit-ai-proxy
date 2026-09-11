# Hộp thư chat — Đợt 1: Lọc theo nhãn (mục 7) + Tên Trang (mục 6)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Người trực lọc được danh sách hội thoại theo nhãn của khách, và thấy hội thoại đang thuộc Trang/OA nào khi công ty nối nhiều tài khoản cùng kênh.

**Architecture:** Cả hai việc đi đúng đường có sẵn: thêm một tham số vào `GET /conversations` và một mệnh đề `EXISTS` vào câu SQL liệt kê + đếm (nhãn); thêm một trường `accountLabel` vào từng dòng hội thoại, tra từ `label` đã lưu trong cấu hình kênh (Trang). Không bảng mới, không route mới, không tệp .jsx mới.

**Tech Stack:** .NET 8 minimal API · Dapper + Npgsql (CSDL chat) · Dapper + SqlClient (`dbo.TenantChannelSettings`) · React/Babel UMD (`wwwroot/pages/chat-inbox.jsx`) · xUnit · Playwright.

**Spec:** [docs/superpowers/specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §1, §2.

**Mục 8 (cỡ biểu tượng) KHÔNG nằm trong plan này** — chưa biết bảng gốc chỉ vào biểu tượng nào (spec §3). Có câu trả lời thì thêm một task CSS riêng.

## Global Constraints

- Chữ hiển thị, log, chú thích: **tiếng Việt**. Tên định danh theo file đang sửa (C# ở đây trộn Việt/Anh; JSX dùng tiếng Việt không dấu).
- Ngày giờ UTC kèm `Z`.
- `CHANGELOG.md` **bắt buộc**, viết cho người dùng cuối, không tên file/hàm/bảng.
- Trước khi sửa một symbol: `codegraph impact <Symbol>`; bán kính rộng thì báo trước.
- Bước cuối mỗi task: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` **toàn bộ, không lọc** (1265 bài, < 1 giây).
- **Mảng vào Postgres qua Dapper**: `= ANY(@x)` với `x = arr.ToArray()` — khuôn `MarkProcessedAsync` (ChatRepository.cs:1299). Tham số có thể null thì **ép kiểu tường minh** `@x::text[]`, như `@sauLuc::timestamptz` đã làm trong cùng câu.
- **Chốt canh nhãn** `ChatTagCatalogGuardTests.Moi_cau_lenh_cham_bang_nhan_deu_phai_kep_theo_cong_ty` đòi mọi câu SQL chạm `chat_contact_tags` có đúng chữ `tenant_id = @tenant`. Viết `t.tenant_id = @tenant`, **không** viết `t.tenant_id = v.tenant_id`.
- Minimal API: route không có thân thì **không khai tham số thân** (bẫy Content-Type ở tầng định tuyến).
- Máy chủ đang chạy khoá DLL: `taskkill //IM TourkitAiProxy.exe //F` trước khi `dotnet build`/`dotnet test`.
- Giao diện: sau khi sửa .jsx phải `.\build-frontend.ps1` rồi khởi động lại máy chủ mới thấy.
- E2E: `E2E_TARGET=local`, worker chat tắt (`Workflows__RunChatWorkers=false`), **cấm** gọi `/send`, `/send-template`. Chạy bằng `scratchpad/chay-e2e-phancong.ps1` (đã có sẵn trong phiên) hoặc `npx playwright test tests/07-chat-phan-cong-api.spec.js --reporter=list` trong `e2e/`.
- Chốt canh mới phải **chứng minh ĐỎ** bằng cách gây lại lỗi rồi khôi phục (so mã băm).
- Commit trailer: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Không commit 5 tệp đang dở: `CLAUDE.md`, `docs/meta-app-review.md`, `docs/postgres-chat-setup.md`, `docs/quy-trinh-multica.md`, `docs/yeu-cau-du-lieu-tu-co-quan.md`.

**Giả định đã chốt trong plan này (spec §10 chưa có câu trả lời):** chọn nhiều nhãn = **HOẶC** (khách mang *bất kỳ* nhãn nào). Đổi sang VÀ là đổi một mệnh đề SQL (ghi ở Task 1, bước 3).

---

## File Structure

| Tệp | Trách nhiệm trong đợt này |
|---|---|
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs` | `ListConversationsAsync`, `CountAsync` nhận `string[]? nhan` |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `GET /conversations` đọc `tag`; `Shape()` phát `accountLabel`; `GET /conversations/{id}` cũng phát |
| `TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs` | `LabelMapAsync` + hàm thuần `ChonTenTrang` |
| `wwwroot/pages/chat-inbox.jsx` | chip lọc nhãn; hiện tên Trang ở dòng và đầu khung chat |
| `wwwroot/styles.css` | `.ci-trang` |
| `TourkitAiProxy.Tests/Chat/ChatTagFilterGuardTests.cs` (mới) | chốt: lọc nhãn có mặt ở CẢ liệt kê lẫn đếm |
| `TourkitAiProxy.Tests/Chat/ChannelLabelMapTests.cs` (mới) | luật "chỉ đặt tên khi ≥ 2 tài khoản cùng kênh" |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `E — Lọc theo nhãn` |
| `CHANGELOG.md` | hai mục |

---

### Task 1: Câu SQL liệt kê và đếm nhận bộ lọc nhãn

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs:366-411` (`ListConversationsAsync`), và `CountAsync` (tìm bằng `grep -n "CountAsync(" ChatRepository.cs`)
- Test: `TourkitAiProxy.Tests/Chat/ChatTagFilterGuardTests.cs` (mới)

**Interfaces:**
- Produces: `ListConversationsAsync(..., int? nguoiDung = null, string[]? nhan = null, CancellationToken ct = default)` và `CountAsync(string tenant, int? chiCuaToi, NguoiXem xem, int? nguoiDung = null, short? kenh = null, string[]? nhan = null, CancellationToken ct = default)`. Task 2 gọi bằng **tham số có tên** `nhan:`.

- [ ] **Step 1: Chạy `codegraph impact ListConversationsAsync` và `codegraph impact CountAsync`** — ghi số caller vào commit message. Dự kiến: mỗi hàm một caller trong `ChatInboxEndpoints.cs`.

- [ ] **Step 2: Viết chốt canh đỏ trước**

```csharp
// TourkitAiProxy.Tests/Chat/ChatTagFilterGuardTests.cs
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Lọc theo nhãn phải có mặt ở CẢ HAI câu: liệt kê VÀ đếm.
///
/// <para>Chip trạng thái ("Mới 3 · Đang xử lý 7") đứng ngay trên danh sách và nói về ĐÚNG danh
/// sách đang hiện — đó là luật đã chốt khi thêm lọc theo kênh (xem chú thích ở lượt gọi
/// CountAsync trong ChatInboxEndpoints). Lọc nhãn mà chỉ sửa câu liệt kê thì chip đếm cả công ty
/// trong khi danh sách chỉ hiện khách mang nhãn: hai con số cạnh nhau mâu thuẫn nhau.</para>
/// </summary>
public class ChatTagFilterGuardTests
{
    private static string Kho()
        => BoChuThich(ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs"));

    private static string BoChuThich(string src) => string.Join("\n",
        src.Split('\n').Where(d => !d.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                && !d.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    private static string Than(string src, string chuKy)
    {
        var i = src.IndexOf(chuKy, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Không thấy “{chuKy}”");
        var sau = src.Substring(i);
        var het = sau.IndexOf("\n    public ", 10, StringComparison.Ordinal);
        return het > 0 ? sau.Substring(0, het) : sau;
    }

    [Theory]
    [InlineData("ListConversationsAsync(")]
    [InlineData("CountAsync(")]
    public void Ca_liet_ke_lan_dem_deu_loc_theo_nhan(string ham)
    {
        var than = Than(Kho(), ham);
        Assert.Contains("chat_contact_tags", than);
        // Đúng khuôn mảng của repo: ANY(@nhan) với ép kiểu text[] vì tham số có thể null.
        Assert.Matches(@"ANY\(@nhan::text\[\]\)", than);
        // Kẹp công ty bằng tham số, không bằng cột — chốt ChatTagCatalogGuardTests đòi vậy.
        Assert.Contains("t.tenant_id = @tenant", than);
    }
}
```

> Lưu ý cho người thực hiện: `Than()` cắt theo mốc cú pháp (chuỗi xuống dòng + `    public `), không đếm ký tự cố
> định; hàm đứng cuối file thì `het < 0` đã xử. Đừng "sửa cho đẹp" thành cửa sổ N ký tự.

- [ ] **Step 3: Chạy để thấy ĐỎ**

Run: `taskkill //IM TourkitAiProxy.exe //F; dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --nologo --filter "FullyQualifiedName~ChatTagFilterGuardTests"`
Expected: FAIL ×2 — `Assert.Contains("chat_contact_tags")` không thấy.

- [ ] **Step 4: Sửa `ListConversationsAsync`**

Chữ ký (dòng 366-369) thêm `string[]? nhan = null` **trước** `CancellationToken ct`:

```csharp
    public async Task<List<ChatConversation>> ListConversationsAsync(string tenant, NguoiXem xem, short? trangThai,
        int? chiCuaToi, string? timKiem, short? kenh = null, int? giaoCho = null,
        bool chiChuaDoc = false, bool chiTheoDoi = false, ConvCursor? sau = null, int limit = 60, int? nguoiDung = null,
        string[]? nhan = null, CancellationToken ct = default)
```

Trong câu SQL, chèn **ngay sau** mệnh đề `@tim` (dòng `AND (@tim IS NULL OR ct.display_name ILIKE @tim …)`) và **trước** mệnh đề `@sauLuc`:

```sql
              -- Lọc theo NHÃN. Nhãn nằm trên KHÁCH (chat_contact_tags), khoá (channel, external_id)
              -- — đúng cặp đang dùng để nối chat_contacts ngay trên. Chọn nhiều nhãn = HOẶC: khách
              -- mang bất kỳ nhãn nào trong số đó. (Muốn VÀ: thay EXISTS bằng
              -- (SELECT COUNT(DISTINCT t.tag) …) = cardinality(@nhan::text[]).)
              --
              -- t.tenant_id = @tenant chứ KHÔNG phải = v.tenant_id: hai cách cùng kết quả, nhưng
              -- chốt canh ChatTagCatalogGuardTests đòi đúng chữ này trên mọi câu chạm bảng nhãn.
              AND (@nhan::text[] IS NULL OR EXISTS (
                    SELECT 1 FROM chat_contact_tags t
                     WHERE t.tenant_id = @tenant AND t.channel = v.channel
                       AND t.external_id = v.contact_external_id
                       AND t.tag = ANY(@nhan::text[])))
```

Trong đối tượng tham số (`new { tenant, trangThai, … }`) thêm:

```csharp
                       nhan = nhan is { Length: > 0 } ? nhan : null,
```

- [ ] **Step 5: Sửa `CountAsync`**

Chữ ký thêm `string[]? nhan = null` trước `CancellationToken ct`:

```csharp
    public async Task<ChatInboxCounts> CountAsync(string tenant, int? chiCuaToi, NguoiXem xem,
        int? nguoiDung = null, short? kenh = null, string[]? nhan = null, CancellationToken ct = default)
```

Trong SQL của nó, chèn **ngay sau** dòng `AND (@chiCuaToi IS NULL OR …)`:

```sql
              -- Cùng bộ lọc nhãn với ListConversationsAsync — chip đếm phải nói về ĐÚNG danh sách
              -- đang hiện. Xem ChatTagFilterGuardTests.
              AND (@nhan::text[] IS NULL OR EXISTS (
                    SELECT 1 FROM chat_contact_tags t
                     WHERE t.tenant_id = @tenant AND t.channel = v.channel
                       AND t.external_id = v.contact_external_id
                       AND t.tag = ANY(@nhan::text[])))
```

Và tham số: `new { tenant, chiCuaToi, nguoiDung, xemTatCa = xem.XemTatCa, maNguoi = xem.CrmUserId, nhan = nhan is { Length: > 0 } ? nhan : null }`.

- [ ] **Step 6: Chạy toàn bộ test — XANH**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --nologo`
Expected: `Passed! … Failed: 0`. Đặc biệt `ChatTagCatalogGuardTests` vẫn xanh (hai câu mới có `tenant_id = @tenant`).

- [ ] **Step 7: Chứng minh chốt đỏ khi lỗi quay lại** — xoá tạm mệnh đề trong `CountAsync`, chạy filter test → phải ĐỎ ở `CountAsync(`; khôi phục từ bản sao lưu, `md5sum` khớp.

- [ ] **Step 8: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Tests/Chat/ChatTagFilterGuardTests.cs
git commit -m "feat(chat): câu liệt kê và đếm hội thoại nhận bộ lọc theo nhãn

Nhãn nằm trên khách, khoá (channel, external_id) — đúng cặp đang nối
chat_contacts, nên phép lọc là một EXISTS, không đổi con trỏ phân trang.
Đếm đi cùng: chip trạng thái phải nói về đúng danh sách đang hiện.

Chốt canh: lọc nhãn phải có ở CẢ hai câu (đã chứng minh đỏ khi bỏ khỏi CountAsync).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `GET /conversations` đọc tham số `tag`

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs:883-930`
- Test: e2e `e2e/tests/07-chat-phan-cong-api.spec.js` (nhóm mới, viết ở Task 4)

**Interfaces:**
- Consumes: `nhan:` từ Task 1.
- Produces: query `?tag=slug1,slug2` (tối đa 10, HOẶC).

- [ ] **Step 1: `codegraph impact` cho route** — route là lambda; kiểm `Shape(` có 2 caller (list + detail) để Task 5 dùng.

- [ ] **Step 2: Sửa chữ ký route** (dòng 885) thêm `string? tag` sau `bool? mine`:

```csharp
            short? status, string? search, short? channel, bool? unread, bool? followed, bool? mine,
            string? tag, string? cursor, CancellationToken ct) =>
```

- [ ] **Step 3: Chuẩn hoá tham số** — chèn ngay trước `const int soDong = 60;`:

```csharp
            // Nhãn lọc: danh sách slug cách nhau bằng dấu phẩy, tối đa 10 — giao diện chỉ gửi slug
            // lấy từ danh mục nên không chuẩn hoá lại ở đây; cắt trần để một URL bậy không kéo
            // theo một mảng vài nghìn phần tử xuống SQL.
            var nhanLoc = string.IsNullOrWhiteSpace(tag) ? null
                : tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Take(10).ToArray();
```

- [ ] **Step 4: Truyền vào hai lượt gọi** — thêm `nhan: nhanLoc,` vào cả `repo.ListConversationsAsync(...)` và `repo.CountAsync(...)` (đặt trước `ct: ct`).

- [ ] **Step 5: Build và gọi thật**

Run (máy chủ chạy với worker tắt, phiên admin staging trong `X-Session-Id`):
```
curl -s "http://localhost:5080/api/v1/chat/conversations?tag=khach-vip" -H "X-Session-Id: <sid>" | python -c "import sys,json;d=json.load(sys.stdin);print(len(d['items']), d['counts'])"
```
Expected: JSON, số dòng ≤ danh sách không lọc, `counts.tong` = số dòng trả về khi không phân trang.

- [ ] **Step 6: Commit**

```bash
git add TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs
git commit -m "feat(chat): GET /conversations nhận ?tag=slug,slug — lọc theo nhãn, HOẶC

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Chip lọc nhãn trên thanh lọc

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — state (dòng ~2303), `taiDsach` (2380-2411), effect đặt lại (2483), `coLoc` (2846), thanh chip (2945-2951)

**Interfaces:**
- Consumes: `?tag=` (Task 2), `GET /api/v1/chat/tags` → `{items:[{id, slug, name, usageCount}]}`.

- [ ] **Step 1: Thêm state** ngay sau dòng `const [tim, setTim] = useState('');`:

```jsx
    const [nhanLoc, setNhanLoc] = useState([]);      // slug[] đang lọc — rỗng = không lọc
    const [danhMucNhan, setDanhMucNhan] = useState([]); // {id, slug, name, usageCount}[]
```

- [ ] **Step 2: Nạp danh mục nhãn** — thêm effect ngay sau khai báo state (một lần, và nạp lại khi đổi hội thoại vì thanh nhãn trong khung chat có thể vừa tạo nhãn mới):

```jsx
    // Danh mục nhãn cho chip lọc. Nạp lại khi đổi hội thoại: ThanhNhan trong khung chat tạo
    // được nhãn mới, mà chip lọc bên trái không có cách nào khác để biết.
    useEffect(() => {
      let song = true;
      authedFetch('/api/v1/chat/tags')
        .then(r => (r.ok ? r.json() : { items: [] }))
        .then(j => { if (song) setDanhMucNhan(j.items || []); })
        .catch(() => {});
      return () => { song = false; };
    }, [chon]);
```

- [ ] **Step 3: Đưa vào query** — trong `taiDsach`, sau `if (tim.trim()) q.set('search', tim.trim());`:

```jsx
        if (nhanLoc.length > 0) q.set('tag', nhanLoc.join(','));
```
và thêm `nhanLoc` vào mảng phụ thuộc của `useCallback` (dòng 2411): `[loc, kenhLoc, nhom, tim, nhanLoc]`.

- [ ] **Step 4: Đặt lại danh sách khi đổi bộ lọc** — dòng 2483 thành:

```jsx
    useEffect(() => { setDsach([]); setConTro(null); }, [loc, kenhLoc, nhom, tim, nhanLoc]);
```
và `coLoc` (dòng 2846) thêm `|| nhanLoc.length > 0`.

- [ ] **Step 5: Vẽ chip** — ngay sau khối `<div className="ci-chip">…</div>` của trạng thái (đóng ở dòng ~2951), thêm:

```jsx
              {/* Chip nhãn — chỉ mọc khi công ty đã có nhãn. Chọn nhiều = HOẶC, bấm lại để bỏ. */}
              {danhMucNhan.length > 0 && (
                <div className="ci-chip ci-chip-nhan" aria-label="Lọc theo nhãn">
                  {danhMucNhan.map(n => (
                    <button key={n.slug} className={nhanLoc.includes(n.slug) ? 'on' : ''}
                            title={n.usageCount > 0 ? n.usageCount + ' khách' : 'chưa khách nào'}
                            onClick={() => setNhanLoc(ds => ds.includes(n.slug)
                              ? ds.filter(s => s !== n.slug) : [...ds, n.slug])}>
                      {n.name}
                    </button>
                  ))}
                  {nhanLoc.length > 0 && (
                    <button className="ci-chip-xoa" onClick={() => setNhanLoc([])} title="Bỏ lọc nhãn">×</button>
                  )}
                </div>
              )}
```

- [ ] **Step 6: CSS** — thêm sau `.ci-chip button b { … }` (styles.css:9053):

```css
/* Hàng chip nhãn đứng dưới hàng chip trạng thái: cùng khuôn, chỉ thêm khoảng cách trên. */
.ci-chip-nhan { margin-top: 4px; }
.ci-chip-xoa { width: 22px; padding: 0 !important; justify-content: center; }
```

- [ ] **Step 7: Dựng bundle, khởi động lại, kiểm tay** — `.\build-frontend.ps1`; mở `/chat-inbox` bằng phiên admin staging; bấm chip "Khách VIP" → danh sách còn đúng khách mang nhãn, chip trạng thái đổi số theo; bấm lại → về đủ.

- [ ] **Step 8: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx wwwroot/styles.css
git commit -m "feat(chat): chip lọc theo nhãn trên thanh lọc hộp thư

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: E2E — lọc nhãn bằng request thật

**Files:**
- Modify: `e2e/tests/07-chat-phan-cong-api.spec.js` — thêm `test.describe('E — Lọc theo nhãn', …)` **trước** `describe('Vòng đời cấu hình phân công')`

- [ ] **Step 1: Viết bài** (dùng đúng `api`, `GOC`, `nhu`, `doc`, `PHIEN_QUAN_TRI`, `maHoiThoai` của file):

```js
test.describe('E — Lọc theo nhãn', () => {
  const NHAN = 'e2e-loc-nhan';       // slug cố định, dọn ở afterAll
  let maNhan;                        // id dòng danh mục, để xoá

  test.beforeAll(async () => {
    // Gắn nhãn cho hội thoại thử — máy chủ tự đưa vào danh mục.
    const r = await doc(await api.post(`${GOC}/conversations/${maHoiThoai}/tags`, {
      headers: nhu(PHIEN_QUAN_TRI, { 'Content-Type': 'application/json' }), data: { tag: NHAN },
    }));
    expect(r.ma, 'gắn nhãn thử phải được').toBe(200);
    const dm = await doc(await api.get(`${GOC}/tags`, { headers: nhu(PHIEN_QUAN_TRI) }));
    maNhan = (dm.json.items || []).find(n => n.slug === NHAN)?.id;
    expect(maNhan, 'nhãn thử phải vào danh mục').toBeTruthy();
  });

  test.afterAll(async () => {
    await api.delete(`${GOC}/conversations/${maHoiThoai}/tags/${NHAN}`, { headers: nhu(PHIEN_QUAN_TRI) });
    if (maNhan) await api.delete(`${GOC}/tags/${maNhan}`, { headers: nhu(PHIEN_QUAN_TRI) });
  });

  test('E1 — lọc đúng nhãn thì thấy hội thoại, và chip đếm nói về đúng danh sách đó', async () => {
    const r = await doc(await api.get(`${GOC}/conversations?tag=${NHAN}`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.ma).toBe(200);
    const ids = (r.json.items || []).map(x => x.id);
    expect(ids, 'hội thoại vừa gắn nhãn phải nằm trong kết quả lọc').toContain(maHoiThoai);
    // Đếm phải đi theo bộ lọc: tổng == số dòng (danh sách thử nhỏ hơn một trang).
    expect(r.json.counts.tong, 'chip đếm không theo bộ lọc nhãn').toBe(ids.length);
  });

  test('E2 — lọc nhãn không tồn tại thì rỗng, KHÔNG phải "không lọc"', async () => {
    const r = await doc(await api.get(`${GOC}/conversations?tag=e2e-khong-co-nhan-nay`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect(r.ma).toBe(200);
    expect(r.json.items, 'nhãn lạ phải ra rỗng — ra đủ nghĩa là tham số bị bỏ qua').toHaveLength(0);
    expect(r.json.counts.tong).toBe(0);
  });

  test('E3 — nhiều nhãn là HOẶC: nhãn thật + nhãn lạ vẫn thấy hội thoại', async () => {
    const r = await doc(await api.get(`${GOC}/conversations?tag=${NHAN},e2e-khong-co-nhan-nay`, { headers: nhu(PHIEN_QUAN_TRI) }));
    expect((r.json.items || []).map(x => x.id)).toContain(maHoiThoai);
  });
});
```

- [ ] **Step 2: Chạy** — `scratchpad/chay-e2e-phancong.ps1` (máy chủ đã dựng lại từ Task 2–3).
Expected: `20 passed` (17 cũ + 3 mới).

- [ ] **Step 3: Commit**

```bash
git add e2e/tests/07-chat-phan-cong-api.spec.js
git commit -m "test(e2e): lọc theo nhãn — thấy đúng, rỗng đúng, và nhiều nhãn là HOẶC

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Tên Trang — tra `label` đã lưu và phát `accountLabel`

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs` (sau `ListAccountsAsync`, dòng ~62)
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` — `Shape()` và hai route gọi nó
- Test: `TourkitAiProxy.Tests/Chat/ChannelLabelMapTests.cs` (mới)

**Interfaces:**
- Produces: `ChannelCredentialStore.ChonTenTrang(IEnumerable<(short kenh, string accountId, string? label)>) → Dictionary<(short, string), string>` (thuần, test được) và `Task<Dictionary<(short, string), string>> LabelMapAsync(string tenantId, CancellationToken ct)`.
- Produces: trường JSON `accountLabel` (string | null) trên mỗi dòng của `GET /conversations` và `conversation` của `GET /conversations/{id}`.

- [ ] **Step 1: Test luật thuần — ĐỎ trước**

```csharp
// TourkitAiProxy.Tests/Chat/ChannelLabelMapTests.cs
using System.Collections.Generic;
using TourkitAiProxy.Infrastructure.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Tên Trang chỉ hiện khi nó PHÂN BIỆT được gì: công ty một Trang thì tên Trang là nhiễu trên
/// mọi dòng; hai Trang trở lên cùng kênh thì thiếu nó người trực không biết khách nhắn vào đâu.
/// </summary>
public class ChannelLabelMapTests
{
    [Fact]
    public void Mot_tai_khoan_tren_kenh_thi_KHONG_dat_ten()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[] { ((short)1, "pageA", (string?)"Trang A") });
        Assert.Empty(ra);
    }

    [Fact]
    public void Hai_tai_khoan_cung_kenh_thi_dat_ten_ca_hai()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)"Trang A"),
            ((short)1, "pageB", (string?)"Trang B"),
            ((short)4, "oa1",   (string?)"OA duy nhất"),   // kênh khác, một mình → không đặt
        });
        Assert.Equal(2, ra.Count);
        Assert.Equal("Trang A", ra[((short)1, "pageA")]);
        Assert.False(ra.ContainsKey(((short)4, "oa1")));
    }

    [Fact]
    public void Thieu_label_thi_lui_ve_ma_tai_khoan_chu_khong_bo_trong()
    {
        var ra = ChannelCredentialStore.ChonTenTrang(new[]
        {
            ((short)1, "pageA", (string?)null),
            ((short)1, "pageB", (string?)"Trang B"),
        });
        Assert.Equal("pageA", ra[((short)1, "pageA")]);
    }
}
```

- [ ] **Step 2: Chạy — ĐỎ** (`ChonTenTrang` chưa có → lỗi biên dịch, chấp nhận là "đỏ").

- [ ] **Step 3: Viết `ChonTenTrang` + `LabelMapAsync`** — thêm vào `ChannelCredentialStore` ngay sau `ListAccountsAsync`:

```csharp
    /// <summary>
    /// Tên hiển thị của từng tài khoản kênh, CHỈ cho những kênh có từ hai tài khoản trở lên.
    /// Một tài khoản thì tên nó là nhiễu trên mọi dòng hội thoại — xem ChannelLabelMapTests.
    /// </summary>
    public static Dictionary<(short Kenh, string AccountId), string> ChonTenTrang(
        IEnumerable<(short Kenh, string AccountId, string? Label)> ds)
    {
        var theoKenh = ds.GroupBy(x => x.Kenh);
        var ra = new Dictionary<(short, string), string>();
        foreach (var nhom in theoKenh)
        {
            var tk = nhom.ToList();
            if (tk.Count < 2) continue;
            foreach (var x in tk)
                ra[(x.Kenh, x.AccountId)] = string.IsNullOrWhiteSpace(x.Label) ? x.AccountId : x.Label!;
        }
        return ra;
    }

    /// <summary>
    /// Bản đồ (kênh, mã tài khoản) → tên Trang/OA của một công ty, đọc từ `label` đã ghi lúc
    /// nối kênh (Messenger ghi tên Trang, Zalo ghi tên OA). MỘT truy vấn cho mọi kênh.
    /// Lỗi đọc thì trả rỗng — tên Trang là tiện, không phải điều kiện để hộp thư chạy.
    /// </summary>
    public async Task<Dictionary<(short Kenh, string AccountId), string>> LabelMapAsync(
        string tenantId, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var hang = (await c.QueryAsync<(string Channel, string ConfigJson)>(
                "SELECT Channel, ConfigJson FROM dbo.TenantChannelSettings WHERE TenantId=@t",
                new { t = tenantId })).ToList();

            var ds = new List<(short, string, string?)>();
            foreach (var kenh in Enum.GetValues<ChatChannel>())
            {
                var tienTo = KeyOf(kenh) + ":";
                foreach (var h in hang.Where(h => h.Channel.StartsWith(tienTo, StringComparison.Ordinal)))
                    ds.Add(((short)kenh, h.Channel[tienTo.Length..],
                            Decode(h.ConfigJson).GetValueOrDefault("label")));
            }
            return ChonTenTrang(ds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/cred] đọc tên Trang hỏng, tenant={T} — bỏ trống", tenantId);
            return new();
        }
    }
```

> `KeyOf` và `Decode` là hai hàm private đã có trong lớp này (xem `ListAccountsAsync`). Nếu `Enum.GetValues<ChatChannel>()` không biên dịch được vì ChatChannel có giá trị "Web" chưa có KeyOf, `KeyOf` đã có nhánh `_ => "chat-" + …` nên vẫn an toàn.

- [ ] **Step 4: Chạy test — XANH.**

- [ ] **Step 5: `Shape()` phát `accountLabel`** — đổi chữ ký (giữ mọi caller cũ qua overload):

```csharp
    private static object Shape(ChatConversation v, string sessionId)
        => Shape(v, sessionId, null);

    private static object Shape(ChatConversation v, string sessionId,
        IReadOnlyDictionary<(short, string), string>? tenTrang)
    {
        var docToi = v.MyLastReadAt ?? v.AgentLastReadAt;
        return new
        {
            v.Id, v.Channel, v.ContactExternalId, v.AccountId, v.Status, v.AssignedUsername,
            v.AssignedUserId,
            v.Followed,
            blocked = v.BlockedUtc is not null,
            v.LastActivityAt, v.LastPreview, v.ContactRepliedAt,
            displayName = v.DisplayName,
            avatarUrl = ContactAvatarUrl(v.AvatarUrl, sessionId),
            referral = v.ReferralSource is null && v.ReferralRef is null && v.ReferralAdId is null
                ? null : new { source = v.ReferralSource, gtRef = v.ReferralRef, adId = v.ReferralAdId },
            botPaused = v.BotResumeAt is { } m && m > DateTime.UtcNow,
            unread = v.ContactRepliedAt is { } cr && (docToi is null || cr > docToi),
            // Tên Trang/OA — chỉ có giá trị khi công ty nối ≥ 2 tài khoản cùng kênh (xem
            // ChannelCredentialStore.ChonTenTrang). null = không cần hiện gì.
            accountLabel = tenTrang is not null && tenTrang.TryGetValue((v.Channel, v.AccountId), out var l) ? l : null,
        };
    }
```
(Giữ nguyên các chú thích hiện có của `Shape` khi chép — ở trên lược bớt cho ngắn.)

- [ ] **Step 6: Hai route truyền bản đồ** — `GET /conversations` (dòng ~883) thêm `ChannelCredentialStore cred` vào tham số lambda, và trước `return Results.Json(new { items = …` thêm:

```csharp
            var tenTrang = await cred.LabelMapAsync(a.TenantId, ct);
```
rồi `items = items.Select(x => Shape(x, a.SessionId, tenTrang)),`.
`GET /conversations/{id}` (khoảng dòng 960-990): thêm `ChannelCredentialStore cred` và `conversation = Shape(v, a.SessionId, await cred.LabelMapAsync(a.TenantId, ct)),`.

- [ ] **Step 7: Build + gọi thật** — `curl …/conversations` với phiên staging: mọi dòng có khoá `accountLabel` (null nếu staging chỉ có một tài khoản/kênh — đó là đúng).

- [ ] **Step 8: Toàn bộ test xanh. Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Channels/ChannelCredentialStore.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/ChannelLabelMapTests.cs
git commit -m "feat(chat): mỗi hội thoại mang tên Trang/OA khi công ty nối nhiều tài khoản cùng kênh

Tên đã có sẵn ở label lúc nối kênh — chỉ tra và phát ra, không bảng mới.
Đường /channels gác quyền quản trị nên nhân viên không tra được từ đó;
phát ngay trong dòng hội thoại là lối duy nhất mọi người đều đọc được.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Hiện tên Trang trên dòng và đầu khung chat

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx` — dòng hội thoại (khối `<span className="ci-xemtruoc">`, ~2990) và dòng `em` đầu khung chat (~3133)
- Modify: `wwwroot/styles.css`

- [ ] **Step 1: Dòng hội thoại** — thay

```jsx
                    <span className="ci-xemtruoc">{c.lastPreview || 'chưa có tin nào'}</span>
```
bằng
```jsx
                    {/* Tên Trang đi TRƯỚC dòng xem trước, cùng hàng — giữ luật hai hàng của mục.
                        Máy chủ chỉ gửi khi công ty có ≥ 2 tài khoản cùng kênh. */}
                    <span className="ci-xemtruoc">
                      {c.accountLabel && <i className="ci-trang">{c.accountLabel}</i>}
                      {c.lastPreview || 'chưa có tin nào'}
                    </span>
```

- [ ] **Step 2: Đầu khung chat** — mảng `join(' · ')` thêm `v.accountLabel` ngay sau `KENH[v.channel]?.ten` và lọc rỗng:

```jsx
                      <em>{[KENH[v.channel]?.ten, v.accountLabel, TEN_TRANG_THAI[v.status],
                           (phanCong.staffs || []).find(nv => nv.id === v.assignedUserId)?.name
                             || v.assignedUsername || 'chưa ai nhận',
                           v.botPaused ? 'bot tạm dừng' : 'bot đang trả lời'].filter(Boolean).join(' · ')}</em>
```

- [ ] **Step 3: CSS** — thêm cạnh `.ci-xemtruoc`:

```css
.ci-trang { margin-right: 5px; font-style: normal; font-weight: 600; color: var(--text-2, #475569); }
```

- [ ] **Step 4: Dựng bundle, khởi động lại, kiểm tay** trên một công ty có ≥ 2 Trang (nếu staging chỉ có một, tạm nối thêm một OA/Trang thử rồi gỡ; hoặc chấp nhận kiểm bằng `curl` ở Task 5 + đọc mã).

- [ ] **Step 5: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx wwwroot/styles.css
git commit -m "feat(chat): hiện tên Trang/OA ở dòng hội thoại và đầu khung chat

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: CHANGELOG + chạy toàn bộ lần cuối

**Files:**
- Modify: `CHANGELOG.md` — dưới `### ✨ Tính năng mới` của mục `## Phiên bản 09/09/2026 — Hộp thư chat đi theo phân quyền của CRM`

- [ ] **Step 1: Thêm hai mục**

```markdown
- **Lọc hội thoại theo nhãn.** Trên thanh lọc có thêm hàng chip nhãn của công ty: bấm một hay
  nhiều nhãn để chỉ thấy khách đang mang nhãn đó (chọn nhiều thì thấy khách mang bất kỳ nhãn
  nào trong số đã chọn). Số đếm ở chip trạng thái đi theo bộ lọc, không đếm cả công ty nữa.
- **Biết khách đang nhắn vào Trang nào.** Công ty nối từ hai Trang Facebook hay hai OA Zalo trở
  lên sẽ thấy tên Trang ngay đầu dòng xem trước và trên đầu khung chat. Nối một Trang thì không
  hiện gì thêm — không có gì để phân biệt.
```

- [ ] **Step 2: Chạy toàn bộ** — `dotnet test …` (không lọc) → xanh; e2e nhóm 07 → `20 passed`.

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): lọc theo nhãn và tên Trang trong hộp thư chat

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** §1 (lọc nhãn: SQL, tham số, chip, VÀ/HOẶC ghi rõ) → Task 1–4. §2 (tên Trang: tra `label`, bẫy `/channels` gác quyền, chỉ hiện khi ≥ 2, chỗ hiện) → Task 5–6. §3 (mục 8) → cố ý ngoài phạm vi, ghi ở đầu.
- **Placeholder:** không có TBD; mọi bước có mã.
- **Nhất quán tên:** `nhan` (tham số SQL) ↔ `nhanLoc` (endpoint) ↔ `?tag=` (HTTP) ↔ `nhanLoc` (state JSX); `ChonTenTrang` ↔ `LabelMapAsync` ↔ `accountLabel` dùng thống nhất ở Task 5–6.
