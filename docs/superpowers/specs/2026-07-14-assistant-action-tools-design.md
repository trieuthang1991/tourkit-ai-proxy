# Thiết kế: Trợ lý hành động cho /assistant & /travai (JARVIS)

**Ngày:** 2026-07-14
**Trạng thái:** Đã chốt thiết kế (user duyệt 2026-07-14) — sẵn sàng viết plan triển khai
**Phạm vi:** Mở rộng Chat-Analytics (đọc số liệu) thành trợ lý **có quyền hành động**: kiểm tra/trả lời mail, review khách hàng, chấm deal, giao việc / tạo lịch hẹn — với cổng xác nhận an toàn.

---

## 1. Mục tiêu & phi mục tiêu

**Mục tiêu (v1):**
- Cho `/assistant` (text) và `/travai` (JARVIS voice) — dùng chung `ChatAgentService` — thực hiện **4 nhóm hành động**:
  1. Kiểm tra & tóm tắt mail mới (đọc).
  2. Soạn & gửi trả lời mail (ghi, có xác nhận).
  3. Review khách hàng / chấm deal (chạy thẳng, hiện kết quả).
  4. Giao việc / tạo lịch hẹn cho nhân viên (ghi qua **hàng đợi CRM**, có xác nhận).
- Giữ nguyên luồng **đọc số liệu** hiện có, không phá vỡ.
- **Confirm-first** cho hành động hướng ra ngoài / khó undo; **run-through** cho hành động nội bộ non-destructive.

**Phi mục tiêu (v1 — để phase 2):**
- Chuỗi nhiều hành động trong 1 câu ("đánh giá A **rồi** giao việc").
- Xác nhận bằng giọng nói (nói "xác nhận") cho hành động ghi trên /travai.
- Sửa/xóa việc/lịch hẹn đã tạo; đính kèm file vào task; đính kèm mail; OAuth mail.

**Ranh giới đồng bộ CRM (user chốt 2026-07-14):** proxy **chỉ enqueue** vào bảng tạm với payload đủ field. Phần drain/POST vào CRM (`/api/tasks`, `/api/customer-care`) **user tự xử lý sau** — ngoài phạm vi v1 (mục 4.1). Đã soi DTO thật nên payload đủ field cho worker map (mục 4).

---

## 2. Quyết định thiết kế (3 điểm user chưa xác nhận — mặc định đã chọn, có thể override)

| # | Quyết định | Phương án chọn | Lý do |
|---|---|---|---|
| D1 | Xác nhận trên /travai (voice) | **Đọc-actions nói, ghi-actions bấm** | Review/deal hands-free bằng giọng; gửi mail & giao việc BẮT BUỘC chạm thẻ → chống nghe nhầm "xác nhận" cho hành động khó undo |
| D2 | CRM worker chưa có | **Proxy chỉ enqueue bảng tạm** | User chốt: proxy đẩy vào `CrmActionQueue` (payload đủ field theo DTO thật), phần đồng bộ vào CRM user tự xử lý sau — ngoài phạm vi v1 (mục 4.1) |
| D3 | Nhiều action / 1 câu | **Mỗi lượt 1 hành động** | YAGNI, dễ đoán; gộp nhiều → làm cái đầu + nhắc cái sau |

Quyết định đã chốt trước đó (user duyệt): **confirm-first** cho hành động ghi; **review/deal đơn lẻ chạy thẳng hiện kết quả**, **batch review nhiều KH cần xác nhận** (tốn quota).

---

## 3. Kiến trúc tổng thể

### 3.1 Hai loại tool

Song song `ChatTools.All` (read) là **`ActionTools.All`** (action) — 1 nguồn cho cả prompt planner lẫn dispatch.

```csharp
public record ActionTool(
    string Name,           // "send_mail_reply" | "review_customer" | "assign_task" ...
    string Description,    // để planner hiểu khi nào chọn
    string[] Params,       // key AI được điền (đã resolve tên→id ở backend)
    ActionKind Kind,       // Mail | Internal | CrmQueue
    bool NeedsConfirm,     // true = phải qua thẻ xác nhận
    string Title);         // nhãn thẻ

public enum ActionKind { Mail, Internal, CrmQueue }
```

Catalog v1:

| Name | Kind | NeedsConfirm | Thực thi |
|---|---|---|---|
| `check_mail` | Mail | false | sync IMAP + list chưa đọc (đọc) |
| `send_mail_reply` | Mail | **true** | draft theo tone → confirm → SMTP send |
| `compose_mail` | Mail | **true** | draft mới → confirm → SMTP send |
| `review_customer` | Internal | false* | ReviewService → thẻ hạng A–D |
| `score_deal` | Internal | false* | fetch deal → build profile → DealScoringService → thẻ điểm |
| `assign_task` | CrmQueue | **true** | enqueue `dbo.CrmActionQueue` (Kind=assign-task) |
| `create_appointment` | CrmQueue | **true** | enqueue `dbo.CrmActionQueue` (Kind=create-appointment) |

`*` review/deal: `NeedsConfirm=false` cho **1 thực thể**; batch **nhiều** thực thể → backend nâng thành proposal xác nhận (ước tính số lượt AI).

### 3.2 Planner mở rộng

`JsonPlannerAgent` (default) / `NativeToolUseAgent` (Anthropic) hiện trả `{tool, params}` cho đọc. Mở rộng để trả thêm:

```json
{ "action": "assign_task",
  "params": { "staffName": "Minh", "customerName": "A",
              "title": "Gọi lại khách", "dueDate": "2026-07-16", "note": "..." } }
```

- Prompt planner nhận **cả 2 catalog** (`ChatTools.CatalogForPrompt()` + `ActionTools.CatalogForPrompt()`), nêu rõ: câu hỏi số liệu → `tool`; yêu cầu làm việc gì đó → `action`.
- `HeuristicRoute` (fallback khi reasoning model trả non-JSON) mở rộng thêm từ khóa action ("giao việc", "trả lời mail", "đánh giá khách"...).
- Ngữ cảnh: planner đọc `messages` history (đã có) → điền id thực thể nhắc trước ("khách này" → customerId từ lượt trước). Backend re-resolve để chắc chắn.

### 3.3 Resolver tên → id (tái dùng pattern có sẵn)

Theo `ChatAgentService.ResolveMarketAsync` + `employeeName→id`:
- `staffName → staffId`: qua `employee_performance` / `top_sellers` list, normalize bỏ dấu.
- `customerName → customerId`: qua `/api/ai/customers?filter={name}` (search server-side, không phân trang mù).
- deal name / khách của deal → dealId: qua `/api/ai/booking-tickets?keyword=`.
- **Mơ hồ (nhiều khớp)** → KHÔNG đoán: trả `action-clarify` với danh sách để user chọn.
- **Không khớp** → trả câu "không tìm thấy …".
- Cache resolver per-tenant TTL ngắn (giống `_markets` 6h) khi phù hợp.

### 3.4 Ba dạng phản hồi (SSE / JSON)

| Dạng event | Khi nào | Frontend |
|---|---|---|
| `action-proposal` | Hành động `NeedsConfirm` (gửi mail, giao việc, batch review) | Thẻ xác nhận: tóm tắt + field sửa được + "Xác nhận"/"Hủy" |
| `action-clarify` | Resolve mơ hồ | Danh sách chọn thực thể |
| `action-result` (+ `data?`) | Sau execute | Câu trả lời + panel data (thẻ review / "✅ đã gửi" / "✅ vào hàng đợi") |
| `{stage}/{delta}/{done}` | Câu hỏi đọc (như cũ) | Bảng + chart |

`action-proposal` mang `actionId` (idempotency key server-sinh), `action`, `resolved` (id đã resolve, để hiển thị tên), `fields[]` (field sửa được), `estimate` (vd "3 lượt AI").

### 3.5 Endpoint execute

```
POST /api/v1/assistant/action/execute   (require X-Session-Id, tenant-scope)
body: { actionId, action, params, edited? }
```

- **Re-validate + re-resolve + re-check tenant** server-side (KHÔNG tin payload client mù → chống vượt tenant / giả id).
- `AiCallContext.Push("assistant-action", tenantId, sessionId)` bao quanh mọi AI call (quota + log đúng feature).
- Idempotent theo `actionId` (execute lần 2 → trả kết quả cũ, không gửi/enqueue trùng).
- Định tuyến theo `Kind`:
  - **Mail** → tái dùng logic `MailReplyService` + `IMailSender` (đã có). Draft là SSE; send buffered.
  - **Internal** → `ReviewService.ReviewAsync` / build-profile + `DealScoringService.ScoreAsync`.
  - **CrmQueue** → `CrmActionQueueRepository.Enqueue(...)`.

---

## 4. Hàng đợi CRM (`dbo.CrmActionQueue`) — outbox pattern

Clone convention `dbo.OutboundMails` / `MailQueueRepository`. Proxy **không ghi thẳng CRM** — enqueue, worker (viết sau, `toutkit-app`) đồng bộ.

```sql
CREATE TABLE dbo.CrmActionQueue (
  Id            BIGINT IDENTITY(1,1) PRIMARY KEY,
  TenantId      NVARCHAR(64)  NOT NULL,
  Username      NVARCHAR(128) NOT NULL,     -- ai yêu cầu (từ session)
  Kind          NVARCHAR(40)  NOT NULL,     -- 'assign-task' | 'create-appointment'
  PayloadJson   NVARCHAR(MAX) NOT NULL,     -- {staffId, customerId, title, dueDateUtc, note, ...}
  Status        INT           NOT NULL DEFAULT 0,  -- 0=Pending 1=Processing 2=Done 3=Failed
  ResultJson    NVARCHAR(MAX) NULL,         -- {crmTaskId, ...} sau khi worker tạo
  RetryCount    INT           NOT NULL DEFAULT 0,
  ErrorMessage  NVARCHAR(1000) NULL,
  CreatedUtc    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
  ProcessedUtc  DATETIME2     NULL
);
CREATE INDEX IX_CrmActionQueue_Tenant_Status ON dbo.CrmActionQueue(TenantId, Status, CreatedUtc);
```

- Schema idempotent (`IF OBJECT_ID(...) IS NULL`) trong `Services/Db/TourkitAiDb.cs` (`SchemaSql`). Cập nhật `docs/database-schema.md`.
- DateTime UTC kèm `Z` (convention).
- **PayloadJson contract — KHỚP 1:1 DTO thật của TourKit.Api** (đã soi `TourKit.Shared/DTOs/TaskingDtos.cs` + `CustomerCareDtos.cs`). Doc mới `docs/crm-action-contract/`:

  **`assign-task`** → `POST /api/tasks` (`CreateOrUpdateTaskingRequest`):
  ```json
  { "workflowId": 12,          // BẮT BUỘC — việc thuộc board/workflow nào
    "name": "Gọi lại khách A",
    "content": "…mô tả…",
    "staffsInCharge": "15,18",  // CSV id nhân viên (nhiều người) — resolver name→id
    "prioritized": 1,           // 0=— 1=Cao 2=TB 3=Thấp
    "status": 1,                // mặc định 1=Chưa bắt đầu
    "startDate": "2026-07-15T…Z",
    "endDate": "2026-07-16T…Z", // "hạn ngày mai"
    "appointmentReminder": 30,  // phút nhắc trước hạn (optional, 0=không)
    "bookingTicketId": 456,     // optional — liên kết cơ hội/lead
    "parentTaskId": null, "tags": [] }
  ```
  **`create-appointment`** → `POST /api/customer-care` (`CreateCustomerCareRequest`):
  ```json
  { "customerId": 123,
    "careTitle": "Hẹn tư vấn tour Hàn",
    "careDetail": "…",
    "careStartTime": "2026-07-16T02:00:00Z",
    "careEndTime":   "2026-07-16T03:00:00Z",
    "status": 1,
    "typeSchedule": null, "appointmentReminder": 30,
    "bookingTicketId": null,
    "customerName": "Nguyễn Văn A", "customerPhone": "09…" }
  ```

- **Resolver bổ sung cho task:** `workflowName → workflowId` (task PHẢI thuộc 1 workflow). Nếu user không nêu workflow → hỏi/chọn từ list `GET /api/tasks`-workflow (hoặc default workflow tenant). `staffsInCharge` hỗ trợ **nhiều nhân viên** (CSV).

### 4.1 Ranh giới: proxy CHỈ enqueue — đồng bộ do worker (user xử lý sau)

Quyết định user (2026-07-14): **proxy chỉ đẩy vào bảng tạm**, phần "đồng bộ vào CRM" (worker) **user tự làm sau**. Proxy KHÔNG tự POST `/api/tasks`.

- **Execute (CrmQueue action):** enqueue `CrmActionQueue` với `Status=Pending` + PayloadJson **đủ field** (mục 4) → trả "✅ Đã đưa vào hàng đợi". Hết trách nhiệm proxy.
- **Đồng bộ:** **worker bên app `toutkit-app` (user quản lý)** đọc `Pending` → map PayloadJson → `POST /api/tasks` · `/api/customer-care` → cập nhật `Status=Done` + `ResultJson`. **Ngoài phạm vi proxy v1** — proxy chỉ cung cấp bảng tạm + contract.
- Vì payload đã khớp DTO thật (mục 4), worker chỉ cần deserialize + POST — không phải đoán field.
- Trang xem hàng đợi: `GET /api/v1/workflows/crm-queue?status=&limit=` (đặt trong `/workflows`, cạnh outbound-mails) → item "Pending ⏳ / Done ✅ / Failed ❌" (Done/Failed do worker cập nhật sau).

---

## 5. Kịch bản hội thoại (đã duyệt)

**KB-A · Xem thông tin → nhờ đánh giá (nhớ ngữ cảnh)**
```
User: "Cho tôi thông tin khách hàng Nguyễn Văn A"
JARVIS: [read customers filter=A] → hồ sơ + doanh số. (nhớ customerId=123)
User: "Đánh giá khách này giúp tôi"
JARVIS: [action review_customer, customerId=123 từ ngữ cảnh] → chạy thẳng
        → THẺ REVIEW hạng A–D + điểm mạnh/lo ngại + gợi ý. (lưu dbo.Reviews + sync hạng CRM)
```

**KB-B · Khách mới hôm nay → đánh giá**
```
User: "Hôm nay có khách hàng mới nào không?"   → [read customers startDate=hôm nay] → 3 KH.
User: "Đánh giá giúp tôi"
  • 1 KH → review thẳng.
  • Nhiều KH → THẺ XÁC NHẬN "Đánh giá 3 khách? (~3 lượt AI)" → bấm → batch → 3 thẻ.
```

**KB-C · Deal**
```
User: "Có cơ hội bán hàng nào mới chờ xử lý không?"   → [read booking_tickets trangThai=2].
User: "Chấm điểm deal của khách B"
JARVIS: [resolve B→dealId] [fetch deal → profile] [score_deal] → thẻ điểm + gợi ý.
```

**KB-D · Kiểm tra & trả lời mail (CÓ xác nhận)**
```
User: "Kiểm tra mail mới"   → [check_mail] → "3 mail mới: 1 hỏi giá, 1 khiếu nại, 1 xác nhận".
User: "Trả lời khách khiếu nại, xin lỗi và hẹn xử lý hôm nay"
JARVIS: [send_mail_reply tone=xin_loi] → draft → THẺ XÁC NHẬN (sửa được) → "Gửi" → SMTP → "✅ Đã gửi."
```

**KB-E · Giao việc (CÓ xác nhận → hàng đợi)**
```
User: "Giao việc gọi lại khách A cho nhân viên Minh, hạn ngày mai"
JARVIS: [resolve Minh→staffId, A→customerId] → THẺ XÁC NHẬN {việc, giao cho, hạn}
        → "Xác nhận" → enqueue CrmActionQueue → "✅ Đã đưa vào hàng đợi — worker sẽ tạo trong CRM."
```

---

## 6. Xử lý edge case

| Case | Xử lý |
|---|---|
| Tên không khớp | "Không tìm thấy KH tên Z" — không đoán |
| Tên trùng nhiều | `action-clarify` danh sách chọn |
| "khách này" chưa có ngữ cảnh | Hỏi lại "khách nào ạ?" |
| Ngữ cảnh nhiều thực thể | Ưu tiên thực thể nhắc gần nhất; mơ hồ → hỏi |
| Bấm Xác nhận 2 lần / gửi trùng | Idempotent theo `actionId` |
| Client sửa payload rồi confirm | Backend re-resolve + re-check tenant (không tin payload) |
| Review đã fresh (fingerprint trùng) | Báo "đã có đánh giá gần đây" → xem lại / chấm mới |
| "đánh giá lại" | forceFresh=true |
| SMTP gửi lỗi | Báo lỗi + cho gửi lại, KHÔNG lật status |
| Mail chưa sync / hộp thư chưa cấu hình | Nhắc "kiểm tra mail mới trước" / "chưa cấu hình hộp thư" |
| Batch review lỗi 1 phần | Báo per-KH (2/3 xong, 1 lỗi) |
| Hết quota giữa batch | Dừng, báo đã làm tới đâu |
| Prompt injection trong body mail | Confirm-first chặn (user thấy nháp trước khi gửi) |
| Field bắt buộc thiếu (task không hạn) | Hỏi bổ sung / default hợp lý (vd không hạn → null) |
| Worker chưa chạy | Item Pending hiện "⏳ chờ đồng bộ" ở trang queue |

---

## 7. Bảo mật · quota · log

- **Tenant scope:** mọi resolve + action scope theo session tenant (`ITenantContext`). Không cross-tenant.
- **Thực thi as user:** dùng JWT của session (TkSessionStore) → write CRM attribute đúng người.
- **Quota:** mỗi AI call (planner + draft + review) tính vào quota tenant qua `AiCallContext.Push`. Batch → ước tính + xác nhận trước.
- **Log:** feature `assistant-action` trong `dbo.AiUsageHistory` (tách chi phí action vs đọc). WorkflowTrace `?debug=1`. Action fail resolve → có thể log vào "AI bí câu hỏi".
- **Không log:** JWT, password, body mail đầy đủ.

---

## 8. Frontend

- Component tái dùng (cả /assistant + /travai): `ActionConfirmCard` (thẻ xác nhận, field sửa được), `ActionClarifyList` (chọn thực thể), render `action-result.data` qua thẻ review có sẵn (`customer-review-card.jsx`) + trạng thái "✅ đã gửi / vào hàng đợi".
- SSE reader tái dùng `window.tourkitUtil.readSSE`; thêm nhánh xử lý event `action-proposal` / `action-clarify` / `action-result`.
- **/travai (D1):** review/deal chạy bằng giọng; gửi mail/giao việc → JARVIS đọc "Tôi đã soạn xong, hãy kiểm tra và bấm Xác nhận" + hiện thẻ (bắt buộc chạm). Không bắt từ khóa giọng cho hành động ghi (tránh nghe nhầm).
- **Trang xem hàng đợi CRM đặt trong `/workflows`** (cạnh mục "Theo dõi hàng đợi mail" `outbound-mails` sẵn có — cùng bản chất queue chờ worker, tái dùng UI/pattern). KHÔNG thêm UI queue vào trang chat.

---

## 9. Thay đổi dữ liệu & file

**Mới:**
- `Services/Chat/ActionTools.cs` — catalog action.
- `Services/Chat/ActionExecutor.cs` — định tuyến execute theo Kind + re-validate.
- `Services/Crm/CrmActionQueueRepository.cs` — Dapper CRUD enqueue/list (clone MailQueueRepository). CHỈ enqueue + đọc; KHÔNG drain (user làm sau).
- `Endpoints/AssistantActionEndpoints.cs` — `/action/execute`.
- Thêm vào `Endpoints/WorkflowEndpoints.cs`: `GET /api/v1/workflows/crm-queue`.
- `docs/crm-action-contract/*.md` — payload contract (khớp DTO thật) cho worker user viết sau.
- Frontend: `components/action-confirm-card.jsx` (+ clarify list).

**Sửa:**
- `Services/Db/TourkitAiDb.cs` — thêm `dbo.CrmActionQueue`.
- `Services/Chat/JsonPlannerAgent.cs` + `NativeToolUseAgent.cs` — nhận `{action, params}`, nhúng ActionTools catalog, HeuristicRoute action.
- `Services/Chat/ChatAgentService.cs` — resolver tên→id cho staff/customer/deal; phát event action-proposal/clarify.
- `Program.cs` — DI cho ActionExecutor + CrmActionQueueRepository.
- `wwwroot/pages/assistant.jsx` + `jarvis.jsx` + `bundle-entry.js` — xử lý event mới + thẻ.
- `docs/database-schema.md`, `CLAUDE.md` — cập nhật.

---

## 10. Chốt các điểm mở (user đã xác nhận 2026-07-14)

1. **D1/D2/D3 (mục 2): đồng ý** giữ mặc định (voice ghi-actions bấm thẻ · ship queue + contract · 1 action/lượt).
2. **Trang xem hàng đợi CRM: đặt trong `/workflows`** (cạnh "Theo dõi hàng đợi mail").
3. **Làm cả `assign_task` + `create_appointment` trong v1** (không hoãn lịch hẹn).
