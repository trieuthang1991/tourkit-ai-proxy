# Hộp thư chat — Đợt 4: Tạo Cơ hội bán hàng từ hội thoại (mục 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Bản 2 — 11/09/2026.** Bản 1 cho proxy gọi thẳng `POST /api/booking-tickets` của CRM. Bỏ hướng đó theo quyết định "mọi thứ ghi sang hệ ngoài đều để lại". Nay proxy **thả một dòng vào `dbo.CrmActionQueue`**; worker bên `toutkit-app` gọi CRM — và **handler cho loại việc này chưa tồn tại**, chủ dự án sẽ tự viết. Đợt này giao đúng nửa phần proxy, kèm hợp đồng để bên kia viết đối ứng.

**Goal:** Từ một hội thoại đã nối khách CRM, nhân viên bấm một nút để **xếp hàng** tạo Cơ hội bán hàng (= BookingTicket), mang theo tóm tắt đoạn chat; màn hình nói thật là việc đang chờ đồng bộ chứ không giả vờ đã xong.

**Spec:** [../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md](../specs/2026-09-09-chat-cac-cum-hoan-phan-tich.md) §6.
**Phụ thuộc cứng:** [Đợt 3](2026-09-09-chat-dot3-noi-khach-crm.md) — cần hai cột `Action`/`ReferId` và cần hội thoại nối được khách CRM.

---

## 0. Điều phải nói thẳng trước khi viết dòng nào

Hàng đợi đang có đúng hai loại việc worker biết xử lý: `assign-task` và `create-appointment`. **Cơ hội bán hàng là loại thứ ba, chưa có ai nhặt.** Nghĩa là ngay sau đợt này, bấm nút xong thì dòng nằm ở *đang chờ* **mãi mãi** cho tới khi handler bên `toutkit-app` được viết.

Hệ quả bắt buộc, không phải tuỳ chọn:

1. ~~**Có cờ tính năng riêng** `Features:ChatCoHoi`.~~ **ĐÃ BỎ khi thực hiện (11/09/2026).** Lý do bỏ: điều kiện cần canh — worker chưa có nhánh xử lý — là TẠM THỜI, mà cờ thì vĩnh viễn; và chính repo này đã bỏ `Features:ChatAssign` vì cùng lẽ đó. Tạo Cơ hội là một việc của hộp thư chat, không phải tính năng tách rời để ra mắt riêng. Ai được làm thì do quyền `CH_TAO_MOI` quyết.
2. **Giao diện nói đúng sự thật**: "đã xếp hàng, chờ đồng bộ" — không phải "đã tạo Cơ hội". Một nút báo thành công trong khi bên kia chưa có gì là kiểu hỏng tệ nhất: người dùng tin là xong và không kiểm lại.
3. **Hợp đồng phải viết trước khi viết mã**, vào `docs/crm-action-contract/README.md`. Đó là thứ chủ dự án đọc để viết handler; thiếu nó thì hai bên đoán nhau.

---

## Global Constraints

- Chữ hiển thị, log, chú thích: tiếng Việt. Ngày giờ UTC kèm `Z`. `CHANGELOG.md` bắt buộc.
- **Cơ hội bán hàng = BookingTicket** (`toutkit-app/docs/module-mapping.md`). Không nhầm sang Lead/Prospect.
- Route **có thân** (tiêu đề + ghi chú tuỳ chọn) → giao diện **phải** gửi `Content-Type: application/json`; thân là record **không nullable** như `AssignReq`.
- **CRM `BookingTicketService.CreateAsync` không kiểm quyền** (chỉ `CH_XEM*` khi xem, `CH_SUA` khi sửa — đã soát). Proxy **tự kiểm `CH_TAO_MOI`** — hằng có thật ở `toutkit-app/TourKit.Shared/PermissionCodes.cs:247`. Kiểm ở lúc **xếp hàng**, không đợi worker: worker chạy bằng quyền khác và không biết ai bấm nút.
- CRM bắt buộc `IdKhachHang > 0` và `TenKH` → hội thoại **phải** đã nối khách. Chưa nối thì 400, không xếp hàng.
- `NguonPhieu` là mã số, web cũ dùng `3` cho đại lý, **chưa có mã cho "từ chat"** → đọc từ cấu hình `Chat:NguonPhieuCoHoi` (mặc định `1`), đổi khi bên CRM cấp mã. Đây là thứ duy nhất còn cần bên CRM gật đầu, và nó **không chặn** đợt này.
- Hành động nhật ký `tao-co-hoi` phải có nhãn trong `TEN_HANH_DONG`.
- **Không gọi CRM.** Có chốt canh (Task 3).
- Máy chủ khoá DLL → dừng trước build/test. Toàn bộ `dotnet test` cuối mỗi task, không lọc.
- E2E: worker chat tắt, cấm `/send`. Cấm chạy trên erp.
- Chốt canh mới phải chứng minh ĐỎ rồi khôi phục (so mã băm).
- Commit trailer: `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`. Không commit năm tệp đang dở.

---

## File Structure

| Tệp | Trách nhiệm |
|---|---|
| `docs/crm-action-contract/README.md` | §4 mới: `create-booking-ticket` — hợp đồng cho handler bên app |
| `TourkitAiProxy.Domain/Chat/ChatRules.cs` | `TomTatChoCoHoi` — thuần: N tin cuối + đường dẫn về hội thoại |
| `TourkitAiProxy.Infrastructure/Crm/CrmActionQueueRepository.cs` | `CrmActionKind.CreateBookingTicket` |
| `TourkitAiProxy.Infrastructure/TourKit/TkPermissionCodes.cs` | `TaoCoHoi = "CH_TAO_MOI"` |
| `TourkitAiProxy.Endpoints/SessionAuth.cs` | `ForbiddenTaoCoHoi()` |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | `POST /conversations/{id:long}/co-hoi` + `record CoHoiReq` |
| `wwwroot/pages/chat-inbox.jsx` | khối **Cơ hội bán hàng** trong tab khách hàng; nhãn nhật ký |
| `TourkitAiProxy.Tests/Chat/ChatRulesTests.cs` | test `TomTatChoCoHoi` |
| `TourkitAiProxy.Tests/Chat/ChatHangDoiCrmGuardTests.cs` | chốt: kiểm quyền TRƯỚC khi thả dòng · đòi nối khách · xếp hàng chứ không gọi CRM · KHÔNG mọc lại cờ riêng |
| `e2e/tests/07-chat-phan-cong-api.spec.js` | nhóm `H — Xếp hàng Cơ hội` |
| `CHANGELOG.md` | một mục |

---

### Task 1: Viết hợp đồng TRƯỚC

**Files:** `docs/crm-action-contract/README.md`

- [ ] **Step 1:** Thêm §4 `create-booking-ticket` → `POST /api/booking-tickets` (`CreateBookingTicketRequest`), theo đúng khuôn §2/§3 đang có: một khối JSON ví dụ, rồi bảng ánh xạ từng khoá sang field của CRM kèm ghi chú.

Payload dự kiến:

```json
{
  "idKhachHang": 123,
  "tenKH": "Nguyễn Văn A",
  "soDienThoaiKH": "0901234567",
  "emailKH": null,
  "tenPhieu": "Tư vấn tour Nhật tháng 10",
  "noiDungPhieu": "Trích từ hộp thư chat:\nKhách: …\nNhân viên: …\n\nXem hội thoại: https://…",
  "nguonPhieu": 1,
  "nguoiPhuTrachs": [45]
}
```

- [ ] **Step 2:** Ghi rõ ba điều handler **phải** làm, vì proxy không làm hộ được:
  - `nguoiPhuTrachs` là mã người trong CRM, proxy lấy từ người đang phụ trách hội thoại; rỗng thì handler để CRM tự xử theo mặc định;
  - `nguonPhieu` còn là số tạm — khi CRM cấp mã cho "từ chat" thì đổi ở **cấu hình proxy**, handler không phải sửa;
  - `ResultJson` khi xong nên ghi `{"bookingTicketId": <id>}`, để sau này giao diện chat dẫn thẳng sang phiếu.
- [ ] **Step 3:** Commit — hợp đồng đi riêng một commit để chủ dự án đọc được ngay, không phải chờ hết đợt.

---

### Task 2: Hàm thuần tóm tắt đoạn chat

**Files:** `TourkitAiProxy.Domain/Chat/ChatRules.cs`, test `ChatRulesTests.cs`

Giữ nguyên như bản 1 — nó không dính gì tới chuyện gọi CRM hay xếp hàng.

- [ ] **Step 1: Test ĐỎ trước**

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
    => Assert.Contains("https://x/y",
        ChatRules.TomTatChoCoHoi(System.Array.Empty<ChatMessage>(), 5, "https://x/y"));
```

- [ ] **Step 2: ĐỎ. Step 3: Viết hàm** cạnh `TachCauHoiCuoi`:

```csharp
/// <summary>
/// Tóm tắt đoạn chat để ghi vào <c>NoiDungPhieu</c> của Cơ hội bán hàng: N tin có chữ gần nhất,
/// mỗi dòng ghi rõ ai nói, và đường dẫn quay lại hội thoại ở cuối. KHÔNG gọi AI — đây là TRÍCH,
/// không phải diễn giải; người đọc phiếu cần đúng lời khách, không cần lời máy.
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

- [ ] **Step 4: XANH. Commit.**

---

### Task 3: Mã quyền, cờ tính năng, câu từ chối

**Files:** `TkPermissionCodes.cs`, `SessionAuth.cs`, `appsettings.example.json`

- [ ] **Step 1:** `TkPermissionCodes.TaoCoHoi = "CH_TAO_MOI"` kèm chú thích nói rõ **vì sao proxy phải tự kiểm**: CRM không kiểm ở `CreateAsync`, web cũ kiểm ở tầng màn hình — nên ai vào được hộp thư chat cũng xếp hàng tạo Cơ hội được nếu proxy không chặn.
- [ ] **Step 2:** `SessionAuth.ForbiddenTaoCoHoi()` — 403 kèm câu người đọc hiểu, không phải mã lỗi trần.
- [x] **Step 3:** ~~Cờ tính năng.~~ Bỏ — xem §0. Thay vào đó thêm `Chat:NguonPhieuCoHoi` (mặc định `1`) vào `appsettings.example.json`: mã nguồn phiếu đọc từ cấu hình để khi CRM cấp mã cho "từ chat" thì đổi cấu hình, không sửa mã.
- [ ] **Step 4:** Commit.

---

### Task 4: `POST /conversations/{id}/co-hoi` — xếp hàng

**Files:** `ChatInboxEndpoints.cs`, `CrmActionQueueRepository.cs`, test `ChatHangDoiCrmGuardTests.cs`

- [ ] **Step 1: Chốt canh ĐỎ trước** — năm điều:

```csharp
[Fact] public void Xep_hang_chu_KHONG_goi_CRM()        // Contains EnqueueAsync, DoesNotContain api.PostAsync
[Fact] public void Kiem_quyen_CH_TAO_MOI_truoc_khi_xep_hang()
[Fact] public void Doi_hoi_thoai_da_noi_khach_CRM()
[Fact] public void KHONG_mọc_lại_cờ_riêng()            // DoesNotContain "ChatCoHoi"
[Fact] public void Ghi_nhat_ky_tao_co_hoi()
```

> Điều thứ hai đáng nói riêng: kiểm quyền phải nằm **trước** lượt `EnqueueAsync`, không phải sau. Xếp hàng rồi mới từ chối thì dòng đã nằm đó và worker vẫn nhặt.

- [ ] **Step 2: ĐỎ.**

- [ ] **Step 3:** `CrmActionKind.CreateBookingTicket = "create-booking-ticket"`.

- [ ] **Step 4:** Viết route:
  - cờ tắt → 404 (không phải 403: tính năng chưa bật thì nó không tồn tại);
  - phiên + tầm xem như mọi route;
  - `EnsurePermissionsAsync` rồi `HasPermission(CH_TAO_MOI)` → thiếu thì `ForbiddenTaoCoHoi()`;
  - chưa nối khách CRM → 400 nói rõ phải nối trước;
  - `TenPhieu` lấy từ thân, rỗng thì dựng `"Chat: " + tên khách`;
  - `NoiDungPhieu` = `TomTatChoCoHoi(tin, 20, duongDanHoiThoai)`;
  - `EnqueueAsync` với `Kind = CreateBookingTicket`, `Action = "chat-co-hoi"`, `ReferId = id.ToString()`;
  - nhật ký `tao-co-hoi`; trả `{ id, trangThai: "dang-cho" }`.

- [ ] **Step 5:** Chốt canh xanh, toàn bộ test xanh, chứng minh đỏ rồi khôi phục (md5).

- [ ] **Step 6:** Commit.

---

### Task 5: Khối **Cơ hội bán hàng** + E2E + CHANGELOG

- [ ] **Step 1:** Khối trong tab khách hàng: ô tiêu đề, nút *Xếp hàng tạo Cơ hội*, và danh sách các lượt đã xếp của hội thoại này (dùng chung `GET …/cham-soc` của Đợt 3 nếu đã đổi thành `GET …/hang-doi` trả mọi `Action`; nếu chưa thì thêm đường tương tự). Khối **không hiện** khi cờ tắt.
- [ ] **Step 2:** Chữ trên nút và trên dòng trạng thái nói **"đã xếp hàng, chờ đồng bộ"**, không nói "đã tạo".
- [ ] **Step 3:** Nhãn `tao-co-hoi` vào `TEN_HANH_DONG`.
- [ ] **Step 4:** E2E nhóm `H`: cờ tắt → 404; cờ bật + chưa nối khách → 400; cờ bật + đã nối → 200 và thấy dòng `chat-co-hoi` trong hàng đợi. Không cần biến môi trường riêng như bản 1 (`E2E_TAO_CO_HOI`) vì **không còn ghi vào CRM thật** — đó là cái lợi thấy được ngay của hướng xếp hàng.
- [ ] **Step 5:** CHANGELOG một mục, nói rõ đang ở giai đoạn chờ đồng bộ.
- [ ] **Step 6:** Chạy toàn bộ. Commit.

---

## Bàn giao cho chủ dự án

Sau đợt này, phần còn thiếu nằm hết ở `toutkit-app`:

1. `CrmActionSyncWorker` thêm nhánh `create-booking-ticket` → `POST /api/booking-tickets`, đọc payload theo §4 hợp đồng.
2. Ghi `ResultJson = {"bookingTicketId": …}` khi xong.
3. Khi CRM cấp mã `NguonPhieu` cho "từ chat": sửa **cấu hình proxy** `Chat:NguonPhieuCoHoi`, không phải sửa mã.
4. Không phải bật cờ nào — tính năng đã chạy sẵn; handler xong là các dòng đang chờ tự được xử lý.

## Self-review

- **Spec coverage:** §6 ba ràng buộc — đòi nối khách (Task 4), proxy tự kiểm quyền (Task 3–4), `NguonPhieu` qua cấu hình (Task 3). Kéo nội dung chat sang phiếu → Task 2.
- **Không giả vờ đã xong:** cờ mặc định tắt + chữ "chờ đồng bộ" + §0 nói thẳng handler chưa có.
- **Rẻ hơn bản 1 ở E2E:** không ghi CRM thật nên không cần cửa an toàn riêng, không để lại phiếu rác trên staging.
