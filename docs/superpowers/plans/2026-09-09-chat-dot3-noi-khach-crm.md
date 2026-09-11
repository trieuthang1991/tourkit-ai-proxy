# Hộp thư chat — Đợt 3: Nối khách CRM + ghi nhận chăm sóc vào hàng đợi (mục 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Bản 2 — 11/09/2026.** Bản 1 cho proxy gọi thẳng `POST /api/customers` của CRM. Bỏ hẳn hướng đó: chủ dự án chốt **mọi thứ ghi sang hệ ngoài đều để lại**, phần cần bắn sang CRM thì lưu trên hệ chat trước để xem và chuẩn hoá, rồi tự viết đường đồng bộ sau. Chỗ lưu đó **đã có sẵn** — xem §0.

**Goal:** Khi khách chat đã cho số điện thoại, hộp thư **gợi ý** đúng khách CRM để nhân viên xác nhận nối. Khi nhân viên chốt xong một lượt chăm sóc, hộp thư **ghi một dòng vào hàng đợi hành động CRM** kèm đủ ngữ cảnh — không gọi CRM, không chờ CRM.

**Spec:** [../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §5.

---

## 0. Vì sao không cần dựng gì mới

`dbo.CrmActionQueue` (CSDL `TourKit_Push`) **đã là hộp thư đi**, và **proxy sở hữu schema** của nó:

| Mảnh | Ở đâu | Trạng thái |
|---|---|---|
| Bảng + hai chỉ mục | `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs` (`SchemaSql`, ~dòng 585) | có, tạo idempotent mỗi lần khởi động |
| Thả dòng + đọc để theo dõi | `TourkitAiProxy.Infrastructure/Crm/CrmActionQueueRepository.cs` | có |
| Worker lấy ra rồi gọi CRM | `toutkit-app/PushNotification.Worker/CrmActionSyncWorker.cs` | có, claim `READPAST/UPDLOCK` |
| Hợp đồng payload | `docs/crm-action-contract/README.md` | có |
| Loại việc `create-appointment` → `POST /api/customer-care` | cùng file trên, §3 | **có** — đúng thứ mục 4 việc 3 cần |

Nghĩa là việc 3 ("đẩy nhật ký chăm sóc") **không cần API mới, không cần bảng mới, không cần worker mới**. Chỉ cần thả đúng một dòng.

**Hai cột thêm vào (chủ dự án chốt 11/09):**
- `Action` — nghiệp vụ phía chat sinh ra dòng này (`chat-cham-soc`, sau này `chat-co-hoi`…). Khác `Kind`: `Kind` trả lời "gọi API CRM nào" và worker phân việc theo nó; `Action` trả lời "việc này từ nghiệp vụ nào ra".
- `ReferId` — mã hội thoại, để sau truy ngược ra đoạn chat.

Cả hai **NULL được**, mặc định rỗng. Dòng cũ không đổi, worker đang chạy không đọc hai cột này nên cũng không đổi.

**Việc 2 (tạo khách mới trên CRM) ra khỏi đợt này.** Lý do thật, không phải xếp lịch cho gọn: nối khách cần *mã khách* ngay lúc bấm (`chat_contacts.crm_customer_id`), mà thả vào hàng đợi thì mã chỉ có sau khi worker chạy xong. Làm nửa vời sẽ đẻ ra trạng thái "đang chờ nối" mà mọi chỗ đọc `crmCustomerId` đều phải biết — bốn màn hình, một luật mới. Để riêng một đợt, sau khi có đường worker ghi ngược `ResultJson`.

---

## Global Constraints

- Chữ hiển thị, log, chú thích: tiếng Việt. Ngày giờ UTC kèm `Z`. `CHANGELOG.md` bắt buộc, viết cho người dùng cuối.
- `codegraph impact` trước khi sửa: `EnqueueAsync`, `ListForMonitorAsync`, `LinkCrmAsync`, `NoiCrm`.
- **Sửa schema `dbo.CrmActionQueue` là đụng DB_Push** — chủ dự án đã cho phép đúng hai cột `Action` + `ReferId`, mặc định NULL. Thêm gì khác phải xin lại.
- `ALTER TABLE` phải **idempotent** như mọi thứ trong `SchemaSql`: bọc `IF COL_LENGTH('dbo.CrmActionQueue','Action') IS NULL`. Schema chạy lại mỗi lần khởi động.
- Route mới **không có thân** → không khai tham số thân (bẫy Content-Type ở tầng định tuyến). Cần dữ liệu thì lấy từ hội thoại.
- `ChatCrmLinkGuardTests` đang giữ: `crm-search`, `link-crm`, `LinkCrmAsync` kẹp `tenant_id = @tenant`, tìm bằng `a.SessionId`. Không phá.
- Hành động nhật ký mới phải có nhãn trong `TEN_HANH_DONG` (chat-inbox.jsx) — `ChatAuditGuardTests` đỏ nếu thiếu.
- **Không gọi CRM** trong đợt này. Có chốt canh (Task 4).
- Máy chủ khoá DLL → `taskkill //IM TourkitAiProxy.exe //F` trước build/test. Toàn bộ `dotnet test` cuối mỗi task, không lọc.
- E2E: `E2E_TARGET=local`, worker chat tắt, cấm `/send`, `/send-template`.
- Chốt canh mới phải **chứng minh ĐỎ** rồi khôi phục (so mã băm).
- Commit trailer: `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`. Không commit năm tệp đang dở: `CLAUDE.md`, `docs/meta-app-review.md`, `docs/postgres-chat-setup.md`, `docs/quy-trinh-multica.md`, `docs/yeu-cau-du-lieu-tu-co-quan.md`.

---

## File Structure

| Tệp | Trách nhiệm |
|---|---|
| `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs` | hai cột mới, `ALTER` idempotent + chỉ mục tra theo hội thoại |
| `TourkitAiProxy.Infrastructure/Crm/CrmActionQueueRepository.cs` | `CrmActionInput` mang `Action`/`ReferId`; `ListByReferAsync` |
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | `TomTatChamSoc` — hàm thuần dựng `careDetail` từ N tin cuối |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `POST /conversations/{id:long}/cham-soc`, `GET /conversations/{id:long}/cham-soc` |
| `wwwroot/pages/chat-inbox.jsx` | `NoiCrm`: gợi ý theo số ĐT; khối **Chăm sóc** trong tab khách hàng |
| `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs` | test `TomTatChamSoc` |
| `TourkitAiProxy.Tests/Chat/ChatChamSocGuardTests.cs` (mới) | chốt: đòi đã nối khách · thả hàng đợi chứ KHÔNG gọi CRM · ghi nhật ký |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `G — Ghi nhận chăm sóc` |
| `CHANGELOG.md` | hai mục |

---

### Task 1: Hai cột `Action` + `ReferId` trên hàng đợi

**Files:** `TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs`, `TourkitAiProxy.Infrastructure/Crm/CrmActionQueueRepository.cs`

- [ ] **Step 1:** `codegraph impact EnqueueAsync` — ghi số chỗ gọi vào commit. Dự kiến: `ActionExecutor` (trợ lý số liệu) và `WorkflowEndpoints`. **Cả hai phải tiếp tục chạy nguyên trạng** — đó là lý do hai cột mặc định NULL và tham số mới có giá trị mặc định.

- [ ] **Step 2:** `SchemaSql` — thêm ngay sau khối `CREATE TABLE dbo.CrmActionQueue`:

```sql
-- Hai cột BỔ SUNG, thêm 11/09/2026 cho nghiệp vụ chat. Cả hai NULL được và mặc định rỗng:
-- dòng cũ giữ nguyên, worker app-side không đọc chúng nên không phải deploy lại cùng lúc.
--   Action  — nghiệp vụ phía chat sinh ra dòng này. KHÁC Kind: Kind nói "gọi API CRM nào" và
--             worker phân việc theo nó; Action nói "từ nghiệp vụ nào ra", chỉ để tra cứu.
--   ReferId — mã hội thoại, để truy ngược ra đoạn chat đã đẻ ra việc này.
IF COL_LENGTH('dbo.CrmActionQueue', 'Action') IS NULL
    ALTER TABLE dbo.CrmActionQueue ADD Action NVARCHAR(60) NULL;
IF COL_LENGTH('dbo.CrmActionQueue', 'ReferId') IS NULL
    ALTER TABLE dbo.CrmActionQueue ADD ReferId NVARCHAR(64) NULL;
-- Tra "hội thoại này đã đẻ ra những việc gì" — chỗ duy nhất giao diện hỏi tới hai cột này.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmActionQueue_Refer')
    CREATE INDEX IX_CrmActionQueue_Refer ON dbo.CrmActionQueue(TenantId, ReferId, Id DESC);
```

> ⚠️ SQL Server **không** có `ALTER TABLE … ADD IF NOT EXISTS` — phải dùng `COL_LENGTH` như trên.
> `ALTER` đứng sau `CREATE TABLE` trong cùng đợt chạy là đủ, vì `SchemaSql` chạy tuần tự.

- [ ] **Step 3:** `CrmActionInput` thêm hai trường **có mặc định** để mọi chỗ gọi cũ biên dịch nguyên trạng:

```csharp
/// <param name="Action">Nghiệp vụ phía chat, vd "chat-cham-soc". Null với hành động do trợ lý
/// số liệu sinh ra — chúng không thuộc nghiệp vụ chat nào.</param>
/// <param name="ReferId">Mã hội thoại đã đẻ ra việc này. Null khi không đến từ hội thoại.</param>
public record CrmActionInput(string TenantId, string Username, string Kind, string PayloadJson,
    string? Action = null, string? ReferId = null);
```

`EnqueueAsync` thêm hai cột vào câu `INSERT` và vào đối tượng tham số. `CrmActionRow` cũng thêm hai trường cuối (có mặc định null) và `ListForMonitorAsync` `SELECT` thêm chúng.

- [ ] **Step 4:** `ListByReferAsync(tenantId, referId, take, ct)` — mới: `WHERE TenantId=@t AND ReferId=@r ORDER BY Id DESC`.

- [ ] **Step 5:** Build + `dotnet test` toàn bộ → xanh. Khởi động máy chủ, xem log `TourkitAiDb schema OK` — đó là bằng chứng `ALTER` chạy thật được trên bảng đang có dữ liệu, thứ không test nào thay thế.

- [ ] **Step 6:** Commit.

---

### Task 2: Gợi ý nối khách theo số điện thoại (thuần giao diện)

**Files:** `wwwroot/pages/chat-inbox.jsx` — `NoiCrm`

Không API mới: `GET …/crm-search?q=` đã có và tìm bằng phiên của chính nhân viên; CRM khớp `filter=` trên cả `phone`.

- [ ] **Step 1:** Khi mở khối nối CRM mà `chiTiet.contact.phone` có giá trị và chưa nối, tự gọi `crm-search` với số đó **một lần**.
- [ ] **Step 2:** Đúng **một** kết quả → thẻ "Có thể là **{tên}** · {số} — nối?" với hai nút *Nối* / *Không phải*. Nhiều hơn một → hiện danh sách, không chọn hộ.
- [ ] **Step 3:** **Không tự nối trong mọi trường hợp.** Lý do đã ghi sẵn trong mã `LinkCrmAsync`: nối nhầm là bot đọc lịch sử mua của người khác rồi nói với khách này. Trùng số điện thoại trong CRM là chuyện có thật (số công ty, số người nhà).
- [ ] **Step 4:** Kiểm tay trên staging với một hội thoại có số ĐT. Commit.

---

### Task 3: Hàm thuần dựng nội dung chăm sóc

**Files:** `TourkitAiProxy.Domain/Chat/ChatRules.cs`, test `ChatRulesTests.cs`

- [ ] **Step 1:** Viết test TRƯỚC, chạy thấy đỏ:

```csharp
// Lấy N tin cuối, ghi "Khách: …" / "Nhân viên: …", cắt theo GIỚI HẠN KÝ TỰ chứ không theo số tin:
// careDetail bên CRM là cột có trần, một hội thoại dài đủ sức vượt.
[Fact] public void Tom_tat_ghi_ro_ai_noi_cau_nao() { … }
[Fact] public void Tom_tat_cat_theo_tran_ky_tu_va_bao_da_cat() { … }
[Fact] public void Hoi_thoai_rong_tra_chuoi_rong_chu_khong_nem() { … }
```

- [ ] **Step 2:** Viết `TomTatChamSoc(IEnumerable<ChatMessage> tin, int tranKyTu = 1800)`.
- [ ] **Step 3:** Test xanh. Commit.

---

### Task 4: `POST /conversations/{id}/cham-soc` — thả một dòng vào hàng đợi

**Files:** `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs`, test `ChatChamSocGuardTests.cs` (mới)

**Interfaces:** route **không thân**. Mọi thứ lấy từ hội thoại + phiên.

- [ ] **Step 1: Chốt canh ĐỎ trước.** Ba điều, và điều thứ nhất là điều quan trọng nhất của cả đợt:

```csharp
/// <summary>
/// Đường ghi nhận chăm sóc THẢ VÀO HÀNG ĐỢI, tuyệt đối không gọi CRM.
///
/// <para>Chủ dự án chốt 11/09/2026: mọi thứ bắn sang hệ ngoài để lại, lưu trên hệ chat trước để
/// xem và chuẩn hoá. Gọi thẳng CRM ở đây là đi ngược quyết định đó — mà lại là kiểu đi ngược
/// KHÔNG lộ ra: nó chạy được, chỉ là dữ liệu chưa chuẩn đã nằm trong CRM thật rồi.</para>
/// </summary>
[Fact] public void Duong_cham_soc_KHONG_goi_CRM()
{
    var than = ChatSchemaGuardTests.ThanThanhVien(src, "g.MapPost(\"/conversations/{id:long}/cham-soc\"");
    Assert.Contains("EnqueueAsync", than);
    Assert.DoesNotContain("api.PostAsync", than);
    Assert.DoesNotContain("api.PutAsync", than);
}
[Fact] public void Doi_hoi_thoai_da_noi_khach_CRM() { /* Contains("CrmCustomerId") + nhánh trả 400 */ }
[Fact] public void Ghi_nhat_ky() { /* lượt ghi nhật ký mang hành động "cham-soc" */ }
```

- [ ] **Step 2:** Chạy → ĐỎ (chưa có route).

- [ ] **Step 3:** Viết route:
  - đọc phiên; hội thoại ngoài tầm xem → 404, đi qua `GetConversationAsync` như mọi route khác;
  - `GetContactAsync` → **chưa nối khách CRM thì 400** kèm câu nói rõ phải nối trước. Không im lặng bỏ qua: `CreateCustomerCareRequest.CustomerId` là bắt buộc, thả dòng thiếu mã là đẩy một việc chắc chắn hỏng cho worker;
  - dựng `PayloadJson` **đúng hợp đồng** `docs/crm-action-contract/README.md` §3: `customerId`, `careTitle` = `"Chat: " + tên khách`, `careDetail` = `TomTatChamSoc`, `careStartTime`/`careEndTime` = `null`, `status` = 1, `appointmentReminder` = 0, `customerName`, `customerPhone`;
  - `EnqueueAsync` với `Kind = CrmActionKind.CreateAppointment`, `Action = "chat-cham-soc"`, `ReferId = id.ToString()`;
  - ghi nhật ký hội thoại, hành động `cham-soc`;
  - trả `{ id, trangThai: "dang-cho" }`.

- [ ] **Step 4:** `GET /conversations/{id}/cham-soc` → `ListByReferAsync`, trả `{items:[{id, action, status, createdUtc, processedUtc, errorMessage}]}`. **Không trả `PayloadJson`** ra giao diện: nó chứa tên và số điện thoại khách, mà khối này hiện cho mọi người trực đọc được.

- [ ] **Step 5:** Chốt canh xanh; toàn bộ test xanh; chứng minh đỏ bằng cách đổi `EnqueueAsync` thành một lượt gọi CRM rồi khôi phục (so mã băm).

- [ ] **Step 6:** Commit.

---

### Task 5: Khối **Chăm sóc** trong tab khách hàng

**Files:** `wwwroot/pages/chat-inbox.jsx`, `wwwroot/styles.css`

- [ ] **Step 1:** Nút *Ghi nhận chăm sóc*, chỉ bật khi đã nối khách CRM; chưa nối thì hiện câu nhắc nối trước **ngay tại chỗ đó**, không phải toast — toast biến mất trước khi người ta đọc xong.
- [ ] **Step 2:** Dưới nút, danh sách các lượt đã ghi của **chính hội thoại này**, trạng thái bằng chữ người đọc được: *đang chờ đồng bộ · đang xử lý · đã sang CRM · lỗi*. Lỗi thì hiện `errorMessage`.
- [ ] **Step 3:** Nhãn `cham-soc` vào `TEN_HANH_DONG`.
- [ ] **Step 4:** Dựng bundle, khởi động lại, kiểm tay. Commit.

---

### Task 6: E2E + CHANGELOG

- [ ] **Step 1:** Nhóm `G — Ghi nhận chăm sóc`:
  - G1 hội thoại **chưa nối khách** → 400, và **không** đẻ dòng nào trong hàng đợi;
  - G2 hội thoại đã nối → 200, `GET …/cham-soc` thấy đúng dòng vừa tạo, trạng thái *đang chờ*;
  - G3 dòng tạo ra mang đúng `action` và `referId`.
  - E2E ghi vào hàng đợi staging là chấp nhận được (worker staging nhặt lên rồi tự chuyển trạng thái); **cấm chạy trên erp**.
- [ ] **Step 2:** CHANGELOG — hai mục viết theo trải nghiệm: "gợi ý khách trùng số điện thoại" và "ghi nhận chăm sóc, chờ đồng bộ sang CRM".
- [ ] **Step 3:** Chạy toàn bộ. Commit.

---

## Việc còn để lại, và vì sao

| Việc | Vì sao chưa làm |
|---|---|
| Tạo khách mới trên CRM từ chat | Cần mã khách ngay lúc bấm để nối; hàng đợi chỉ có mã sau khi worker chạy. Chờ đường worker ghi ngược `ResultJson`. |
| Worker xử lý `Action` mới | Nằm ở `toutkit-app`, chủ dự án tự viết. Đợt này chỉ thả dòng đúng hợp đồng `create-appointment` — loại việc worker **đã** xử lý được, nên không chờ ai. |
| Ghi CSKH theo *lượt* hay theo *hội thoại* | Câu hỏi này tự tan: nay là quyết định của người dùng chứ không phải của hệ — mỗi lần bấm nút là một dòng, bấm mấy lần thì mấy dòng. |

## Self-review

- **Spec coverage:** §5 việc 1 → Task 2; việc 3 → Task 3–5; việc 2 → ghi rõ để lại, kèm lý do kỹ thuật chứ không phải lý do xếp lịch.
- **Không ghi hệ ngoài:** có chốt canh mã nguồn (Task 4 Step 1), không chỉ là lời hứa trong chú thích.
- **Không phá cái đang chạy:** hai cột mặc định NULL, `CrmActionInput` thêm tham số có mặc định — `ActionExecutor` và `WorkflowEndpoints` biên dịch và chạy nguyên trạng.
