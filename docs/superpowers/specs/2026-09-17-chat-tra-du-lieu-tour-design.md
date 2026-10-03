# Hộp thư chat — trợ lý tra dữ liệu tour để tư vấn khách

**Ngày:** 17/09/2026 · **Trạng thái:** thiết kế, chưa code
**Nguồn yêu cầu:** "Phần cấu hình AI cho tôi thêm quyền lấy tour để tư vấn khách"
**Liên quan:** [chat-inbox-ky-thuat.md](../../features/chat-inbox-ky-thuat.md) ·
[assistant.md](../../features/assistant.md) ·
[CRM-Widget-Integration.md](../../CRM-Widget-Integration.md)

---

## 1. Vấn đề

Trợ lý hộp thư chat **không có cơ chế gọi dữ liệu nào cả**.
[`ChatReplyComposer`](../../../TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs) chỉ 166
dòng: dựng lời nhắc → gọi model → trả chữ. Không tool, không API, không kho.

Hệ quả là khung an toàn phải cấm bot nói **mọi** con số — `DefaultSystemPrompt` cấm báo giá, cấm
lịch khởi hành, cấm số chỗ còn. Đúng khi không có dữ liệu, nhưng nghĩa là khách hỏi "tour Đà Nẵng
tháng 11 bao nhiêu" thì bot chỉ biết hẹn nhân viên trả lời.

Trên giao diện, panel Cài đặt trợ lý đang hứa nguyên văn:
*"Trợ lý vẫn không bao giờ tự báo giá, lịch khởi hành hay số chỗ còn — nó chưa đọc dữ liệu."*

## 2. Phạm vi

**Làm trong đợt này** — bốn mức dữ liệu người dùng yêu cầu:

1. Tên tour, hành trình, điểm đến
2. Khoảng giá tham khảo
3. Giá chính xác theo ngày khởi hành
4. Lịch khởi hành + số chỗ còn

**KHÔNG làm trong đợt này** — đặt chỗ, giữ chỗ, tạo đơn, sửa bất kỳ thứ gì trên ERP. Đợt này
**chỉ đọc**. Cũng không đụng widget và không đụng Trợ lý số liệu.

---

## 3. Những gì ĐÃ CÓ — không dựng lại

Bốn thứ tưởng phải làm mới, thật ra đã chạy thật:

**Bản mẫu đúng bài toán này.**
[`WidgetChatCrmService`](../../../TourkitAiProxy.Services/Widget/WidgetChatCrmService.cs) (247 dòng)
đang làm: chat với khách lạ trên website → planner chọn tool → gọi `/api/ai/*` → bơm dữ liệu →
trả lời. Có sẵn whitelist tool, có sẵn fallback khi planner trượt. Thiết kế này là **dựng lại lối
đó bên trong `ChatReplyComposer`**, không phát minh gì.

**Tool-calling chạy được trên provider của hộp thư.**
[`JsonPlannerAgent`](../../../TourkitAiProxy.Services/Chat/JsonPlannerAgent.cs) ghi rõ ở đầu file:
*"Fallback cho mọi provider không hỗ trợ native function-calling (opencode-go, nine-routes...)"*.
Hộp thư chat chạy `ds/deepseek-v4-flash` qua nine-routes → **không phải đổi nhà cung cấp**.
Ghi lại vì trong lúc bàn đã có lúc kết luận ngược, và kết luận ngược đó suýt loại bỏ cả phương án.

**Danh mục tool đã có sẵn tour.** [`ChatTools`](../../../TourkitAiProxy.Domain/Chat/ChatTools.cs)
có `tours` (`/api/ai/tours`, lọc `tourType` · `tourName` · `status` · ngày · thị trường · chi
nhánh) và `departures` (`/api/ai/departures`). Hai cái này phủ đủ bốn mức trên.

**Tài khoản dịch vụ cho việc không có ai online.**
[`TenantServiceAccountStore`](../../../TourkitAiProxy.Infrastructure/TourKit/TenantServiceAccountStore.cs)
— mỗi công ty một tài khoản, mật khẩu mã hoá trong `dbo.TenantServiceAccounts`. **Bảy workflow**
đang dùng đúng lối này. Không cần đẻ mô hình quyền mới.

---

## 4. Quyết định đã chốt

| Câu hỏi | Chốt | Vì sao |
|---|---|---|
| Cơ chế lấy dữ liệu | Planner chọn tool → gọi API → bơm vào lời nhắc | Dựng lại lối widget; không đổi provider |
| Tool nào được phép | **Chỉ `tours` + `departures`** | §4.1 |
| Danh tính đường Gợi ý | Phiên TourKit của **chính nhân viên** | ERP chặn theo quyền người đó |
| Danh tính đường tự trả lời | **Tài khoản dịch vụ** của công ty | Đúng lối 7 workflow sẵn có |
| Chưa khai tài khoản dịch vụ | Đường tự trả lời **không tra tour**, vẫn trả lời như cũ | §4.2 |
| Cache | **Giữ nguyên `d\|` TTL 30 phút, không chỉnh** | §4.3 |
| Bật/tắt | CHỈ ô bật theo từng công ty (mặc định TẮT), nằm trong `chat_bot_settings.tour_lookup` | Chốt 18/09/2026: đi theo cờ `Features:Chat`, không cờ riêng — tra tour là một việc của hộp thư chat, không phải tính năng ra mắt riêng |

### 4.1 Vì sao chỉ hai tool

Widget mặc định cho ba tool: `tours`, `list_markets`, `booking_tickets`. Ở đây **bỏ
`booking_tickets`**.

Khác biệt nằm ở người đối diện. Widget nói chuyện với khách vãng lai hỏi về chính họ. Hộp thư chat
thì người nhắn **có danh tính và có hội thoại riêng** — để lọt `booking_tickets` là mở đường cho
khách A hỏi trúng cơ hội bán hàng của khách B. `list_markets` cũng bỏ: khách không cần biết công ty
chia thị trường thế nào.

Whitelist chặn **hai lớp**, giống widget: lọc danh mục trước khi gửi cho AI (AI không thấy tool
cấm), và chặn lại lần nữa ở bước dispatch (AI có bịa tên tool cũng không gọi được).

### 4.2 Khi công ty chưa khai tài khoản dịch vụ

Đường tự trả lời **không tra tour** — trả lời đúng như hôm nay. Không mượn phiên của nhân viên nào,
không có đường vòng.

Nhân viên bấm "Gợi ý" thì vẫn tra được, vì lúc đó có phiên thật của người đang ngồi đó.

Nghĩa là tính năng **hé mở dần**: khai tài khoản dịch vụ mới mở nốt đường tự động.

### 4.3 Cache — giữ nguyên, và vì sao thế là đủ

Dùng đúng cache dữ liệu sẵn có: khoá `d|{tenant}|{path}`, TTL 30 phút
([`JsonPlannerAgent.cs:274`](../../../TourkitAiProxy.Services/Chat/JsonPlannerAgent.cs#L274)).
**Không tách TTL riêng, không thêm tầng nào.**

Câu hỏi từng làm phân vân: *hỏi tour Đà Nẵng có ra nhầm tour Phú Quốc không?* Câu trả lời nằm ở
cách dựng khoá — `path` do
[`ChatTools.BuildPath`](../../../TourkitAiProxy.Domain/Chat/ChatTools.cs#L217) sinh ra **đã gồm cả
`tourName` và `startDate`**:

```
Đà Nẵng 15/12 → d|{tenant}|/api/ai/tours?pageIndex=1&pageSize=20&tourName=...&startDate=2026-12-15
Đà Nẵng 16/12 → d|{tenant}|/api/ai/tours?pageIndex=1&pageSize=20&tourName=...&startDate=2026-12-16
```

Hai câu hỏi khác nhau ⇒ hai khoá khác nhau. Và quan trọng hơn: `d|` chỉ lưu **gói JSON thô**, còn
**câu trả lời luôn sinh mới** — không có đường nào để đáp án cũ vọt ra.

Nỗi lo đó có căn cứ lịch sử thật: `e449b69` ghi đúng ca *"Chi phí tháng này trả NGUYÊN VĂN đáp án
Doanh thu tháng này"*. Nhưng thủ phạm là cache **câu trả lời** (`r1`/`r2`) và nó đã bị bỏ hẳn;
cache **dữ liệu** sống sót đợt đó có chủ đích, lý do ghi trong chính commit: *"key xác định TRỌN VẸN
kết quả"*.

**Rủi ro còn lại**, và nó không đến từ cache: số chỗ còn trong bản chụp tối đa 30 phút tuổi. Không
xử bằng cách đoán — bộ E2E `suite cache` (§9) là chỗ đo. Đau thật thì lúc đó mới tách TTL.

---

## 5. Luồng

```
Tin khách tới        ─┐
Nhân viên bấm Gợi ý  ─┴─►  ChatReplyComposer
                            │
                            ├─ (1) Cờ tắt / công ty tắt ───────────► đường trả lời hiện tại
                            │
                            ├─ (2) Planner: 1 lượt AI rẻ, chọn tool
                            │        không chọn được ──────────────► đường trả lời hiện tại
                            │        tool ngoài whitelist ─────────► đường trả lời hiện tại
                            │
                            ├─ (3) Lấy JWT
                            │        Gợi ý      → phiên nhân viên
                            │        Tự trả lời → tài khoản dịch vụ
                            │        không có   ──────────────────► đường trả lời hiện tại
                            │
                            ├─ (4) GET /api/ai/tours | /api/ai/departures
                            │        401 → re-login 1 lần rồi thử lại (lối widget)
                            │        lỗi ────────────────────────► đường trả lời hiện tại
                            │
                            └─ (5) Bơm bảng dữ liệu vào lời nhắc → sinh câu trả lời
```

**Mọi nhánh hỏng đều tụt về hành vi hôm nay.** Không có ca nào tệ hơn hiện trạng — đây là tính chất
phải giữ khi sửa, và là thứ bộ test phải khoá.

---

## 6. Phải sửa kèm — bỏ sót là hỏng

**Khung an toàn.** `DefaultSystemPrompt` đang cấm nói giá, lịch, số chỗ. Có dữ liệu thật thì luật
đổi: **được nói đúng số trong bảng vừa lấy về, vẫn cấm tuyệt đối mọi số không có trong bảng**.
Không sửa chỗ này thì bot đã cầm dữ liệu trong tay vẫn im — tính năng coi như không tồn tại.

**Dòng chữ trên giao diện.** Panel `CaiDatTroLy` đang hứa *"nó chưa đọc dữ liệu"*. Bật tính năng mà
không đổi dòng này là **nói dối người dùng**. Chữ phải đổi theo trạng thái bật/tắt, không phải xoá đi.

**Câu trả lời kèm mốc thời gian** khi có số chỗ hoặc giá — để khách biết số đó tính tại thời điểm nào.
Đây cũng là cái bù cho TTL 30 phút ở §4.3.

---

## 7. Bẫy đã biết

| Bẫy | Luật |
|---|---|
| Số chỗ còn | `Slots − (Booked + OnHold)`. CLAUDE.md: nhãn `OnHold`/`Booked` bị đảo **cố ý** ở giao diện mobile — **đừng "sửa cho đúng"** |
| `tourType` bỏ trống | API mặc định về **FIT**, không phải "tất cả" |
| `status` | **Động theo từng công ty**, đọc ở `/api/ai/reference` → `Lookups.TourStatuses`. Đừng đóng cứng |
| JWT hết hạn giữa chừng | Bắt 401 → `ForceReloginAsync` → thử lại **đúng một lần**, giống widget |
| `pageSize=20` | Danh sách bị cắt trang. Lời nhắc phân tích phải nói rõ bảng **có thể chưa đủ** — cấm kết luận "không có tour" từ một trang cắt. Xem case `feat-tour-08` |

---

## 8. Bán kính ảnh hưởng

`codegraph impact ChatReplyComposer` → **17 symbol, gọn trong 3 file**:

- [`ChatReplyComposer.cs`](../../../TourkitAiProxy.Services/Chat/Inbox/ChatReplyComposer.cs) — chỗ sửa chính
- [`ChatInboundService.cs`](../../../TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs) — đường tự trả lời
- [`ChatInboxEndpoints.cs`](../../../TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs) — đường `/suggest` + lưu cấu hình

**Không lan** sang widget, Trợ lý số liệu, hay `JsonPlannerAgent` — dùng lại `ChatTools` và
`TourKitApiClient` chứ không sửa chúng.

---

## 9. Kiểm thử

**Bộ câu hỏi E2E đã dựng:**
[`scripts/e2e/features/features-tour-lookup.cases.json`](../../../scripts/e2e/features/features-tour-lookup.cases.json)
— 11 case, 15 câu hỏi, ~30 lượt AI.

```powershell
.\scripts\e2e\run-e2e.ps1 -SidFile $env:TEMP\tk_sid.txt -Feature tour-lookup -ShowReply
.\scripts\e2e\run-e2e.ps1 -SidFile $env:TEMP\tk_sid.txt -Feature tour-lookup -Suite cache -ShowReply
```

| Suite | Khoá cái gì |
|---|---|
| `smoke` · `core` | Bốn mức dữ liệu ở §2 + bẫy phủ định sai |
| `cache` | Hai điểm đến ≠ nhau · hai ngày ≠ nhau · hỏi lại y hệt vẫn đúng |
| `security` | Không bịa số · không chạm dữ liệu ngoài phạm vi tour · không lộ tên tool |

Chạy **kèm `-ShowReply`** — assertion không bắt được chất lượng câu chữ, người đọc phải tự xem.

Hôm nay bộ này chạy qua `/api/v1/chat` (Trợ lý số liệu — nơi tool `tours` và cache `d|` **đã có
sẵn**), nên **đo được hành vi cache trước khi viết một dòng code nào**. Khi hộp thư chat có tính
năng, trỏ cùng bộ câu hỏi sang đường `/suggest`.

**Test thuần** (bộ `dotnet test`):

- Cờ tắt → không một lượt gọi API nào phát ra
- Planner trả tool ngoài whitelist (`booking_tickets`, `customers`, `financial_summary`) → **bị chặn ở dispatch**
- Không có tài khoản dịch vụ → đường tự trả lời không gọi API
- Số chỗ còn tính đúng `Slots − (Booked + OnHold)`, kể cả khi `OnHold` vắng mặt trong JSON
- Mọi nhánh hỏng đều trả về đúng chữ mà đường cũ trả

**Kiểm tay trên staging** — theo lệ "xong tính năng phải gọi API thật trước khi báo xong": hỏi thật
một câu về tour, đọc nguyên văn câu trả lời, đối chiếu số với ERP. **Chỉ staging, không đụng erp.**

---

## 10. Câu còn mở

- `/api/ai/tours` trả **giá gì** — giá gốc, giá bán, hay giá theo ngày khởi hành? Phải gọi thật một
  lần trên staging rồi mới chốt cách diễn đạt mức 2 và mức 3. Chưa biết thì đừng hứa với khách.
- Có giới hạn số lượt tra theo công ty không, hay để quota AI sẵn có tự chặn?
