# Hợp đồng hàng đợi `dbo.CrmActionQueue` — trợ lý hành động → CRM

Trợ lý (`/assistant`, `/travai`) có 2 hành động ghi vào CRM: **giao việc** (`assign_task`) và
**tạo lịch hẹn CSKH** (`create_appointment`). Proxy (`tourkit-ai-proxy`) **KHÔNG POST thẳng vào
TourKit.Api** — sau khi user xác nhận trên thẻ, proxy chỉ **enqueue 1 dòng** vào
`dbo.CrmActionQueue` với `Status=0 (Pending)` rồi trả "✅ Đã đưa vào hàng đợi". Việc đọc hàng đợi,
gọi API CRM thật (`POST /api/tasks`, `POST /api/customer-care`) và cập nhật kết quả là trách nhiệm
của **worker phía `toutkit-app`** (viết sau, ngoài phạm vi proxy v1) — tài liệu này là hợp đồng để
worker đó implement đúng.

Nguồn thiết kế: `docs/superpowers/specs/2026-07-14-assistant-action-tools-design.md` §4.
Code enqueue: [`Services/Crm/CrmActionQueueRepository.cs`](../../Services/Crm/CrmActionQueueRepository.cs),
[`Services/Chat/ActionExecutor.cs`](../../Services/Chat/ActionExecutor.cs) (`ExecuteAssignTaskAsync` /
`ExecuteCreateAppointmentAsync` — nơi build `PayloadJson`).

## 1. Bảng `dbo.CrmActionQueue`

Schema idempotent trong [`Services/Db/TourkitAiDb.cs`](../../Services/Db/TourkitAiDb.cs) (`SchemaSql`).

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | `BIGINT IDENTITY(1,1)` PK | Id dòng, tăng dần. |
| `TenantId` | `NVARCHAR(64)` | Tenant sở hữu hành động (từ session TourKit). |
| `Username` | `NVARCHAR(128)` | Ai yêu cầu (từ session) — dùng để audit "ai giao việc". |
| `Kind` | `NVARCHAR(40)` | `'assign-task'` hoặc `'create-appointment'` (hằng số [`CrmActionKind`](../../Services/Crm/CrmActionQueueRepository.cs)). |
| `PayloadJson` | `NVARCHAR(MAX)` | JSON — khớp field cho field với DTO CRM đích (xem §2/§3 dưới). |
| `Status` | `TINYINT` | Vòng đời — xem §4. |
| `ResultJson` | `NVARCHAR(MAX)` NULL | Worker ghi sau khi POST thành công, vd `{"crmTaskId": 789}`. |
| `RetryCount` | `INT` | Worker tăng mỗi lần retry thất bại. |
| `ErrorMessage` | `NVARCHAR(1000)` NULL | Worker ghi lỗi lần POST cuối khi Failed. |
| `CreatedUtc` | `DATETIME2` | Lúc proxy enqueue (UTC). |
| `ProcessedUtc` | `DATETIME2` NULL | Lúc worker xử lý xong (Done hoặc Failed) — UTC. |

Index: `IX_CrmActionQueue_Poll (Status, CreatedUtc)` — worker poll theo index này (oldest Pending
trước). `IX_CrmActionQueue_Tenant (TenantId, Status, CreatedUtc)` — cho trang theo dõi per-tenant.

**Toàn bộ timestamp là UTC** (`SYSUTCDATETIME()` khi proxy insert). Worker đọc ra `Kind=Unspecified`
qua Dapper — tự coi là UTC, KHÔNG cộng/trừ múi giờ.

## 2. `assign-task` → `POST /api/tasks` (`CreateOrUpdateTaskingRequest`)

`PayloadJson` do [`ActionExecutor.BuildAssignTaskPayload`](../../Services/Chat/ActionExecutor.cs) sinh, ví dụ:

```json
{
  "id": 0,
  "workflowId": null,
  "workflowName": "Chăm sóc khách hàng",
  "name": "Gọi lại khách A",
  "content": "Khách hỏi giá tour Hàn Quốc, cần gọi lại xác nhận",
  "staffsInCharge": "15,18",
  "prioritized": 1,
  "status": 1,
  "startDate": "2026-07-15T02:00:00Z",
  "endDate": "2026-07-16T02:00:00Z",
  "appointmentReminder": 30,
  "bookingTicketId": 456
}
```

| Key trong `PayloadJson` | → Field `CreateOrUpdateTaskingRequest` | Ghi chú |
|---|---|---|
| `id` | `id` | Luôn `0` — proxy chỉ TẠO MỚI, không update. |
| `workflowId` | `workflowId` | **CÓ THỂ NULL.** Proxy KHÔNG resolve workflow (không có endpoint list ổn định để gọi lúc chat). Xem §2.1. |
| `workflowName` | *(không có field tương ứng trực tiếp)* | Chuỗi thô do AI/planner điền (tên workflow user nói ra, vd "Chăm sóc khách hàng"). **Worker PHẢI tự resolve `workflowName` → `workflowId`** — proxy không có cách tra cứu danh sách workflow ổn định tại thời điểm chat. |
| `name` | `name` | Tiêu đề việc. Default `"Việc mới"` nếu AI không điền. |
| `content` | `content` | Mô tả chi tiết, có thể `null`. |
| `staffsInCharge` | `staffsInCharge` | **CSV id nhân viên đã resolve** (vd `"15,18"`) — proxy đã resolve tên → id qua `ActionResolver.ResolveStaffAsync` trước khi enqueue, KHÔNG phải tên thô. Rỗng (`""`) nếu AI không nêu người phụ trách. |
| `prioritized` | `prioritized` | `0`=— `1`=Cao `2`=TB `3`=Thấp (map bởi `ActionExecutor.MapPriority` từ chuỗi tiếng Việt `"cao"/"tb"/"thap"`). |
| `status` | `status` | Luôn `1` (Chưa bắt đầu) — trạng thái mặc định khi tạo mới. |
| `startDate` | `startDate` | ISO UTC hoặc `null`. |
| `endDate` | `endDate` | ISO UTC hoặc `null` (hạn công việc). |
| `appointmentReminder` | `appointmentReminder` | Phút nhắc trước hạn, `0` = không nhắc. |
| `bookingTicketId` | `bookingTicketId` | Optional — liên kết cơ hội/lead, `null` nếu không nêu. |

### 2.1 Bắt buộc: worker phải tự resolve `workflowId`

`CreateOrUpdateTaskingRequest` **yêu cầu** `workflowId` để biết việc thuộc board nào, nhưng proxy
không có nguồn tra cứu workflow ổn định ngay trong lúc chat (không muốn phụ thuộc 1 endpoint chưa
xác nhận). Vì vậy proxy luôn gửi `workflowId: null` kèm `workflowName` là **chuỗi thô** người dùng
nói (không chuẩn hoá, có thể sai chính tả/viết tắt).

Worker khi xử lý dòng `Kind='assign-task'` **PHẢI**:
1. Nếu `workflowId` khác null → dùng thẳng (phòng khi tương lai proxy tự resolve được).
2. Nếu `workflowId == null` → thử match `workflowName` (case-insensitive, bỏ dấu) với danh sách
   workflow của tenant.
3. Không khớp / `workflowName` rỗng → áp dụng **workflow mặc định của tenant** (worker tự định
   nghĩa, vd workflow "Công việc chung" đầu tiên) — KHÔNG được để `workflowId` null khi POST
   `/api/tasks` (chắc chắn CRM từ chối).

## 3. `create-appointment` → `POST /api/customer-care` (`CreateCustomerCareRequest`)

`PayloadJson` do [`ActionExecutor.BuildAppointmentPayload`](../../Services/Chat/ActionExecutor.cs) sinh, ví dụ:

```json
{
  "customerId": 123,
  "careTitle": "Hẹn tư vấn tour Hàn",
  "careDetail": "Khách muốn tư vấn lịch trình 5N4Đ tháng 8",
  "careStartTime": "2026-07-16T02:00:00Z",
  "careEndTime": "2026-07-16T03:00:00Z",
  "status": 1,
  "appointmentReminder": 30,
  "bookingTicketId": null,
  "customerName": "Nguyễn Văn A",
  "customerPhone": "0901234567"
}
```

| Key trong `PayloadJson` | → Field `CreateCustomerCareRequest` | Ghi chú |
|---|---|---|
| `customerId` | `customerId` | Đã resolve — id trực tiếp hoặc qua `ActionResolver.ResolveCustomerAsync` (tên → id, chặn khi mơ hồ/không khớp trước khi enqueue). |
| `careTitle` | `careTitle` | Default `"Lịch hẹn"` nếu AI không điền. |
| `careDetail` | `careDetail` | Mô tả, có thể `null`. |
| `careStartTime` | `careStartTime` | ISO UTC hoặc `null`. |
| `careEndTime` | `careEndTime` | ISO UTC hoặc `null`. |
| `status` | `status` | Luôn `1` (mặc định khi tạo mới). |
| `appointmentReminder` | `appointmentReminder` | Phút nhắc trước, `0` = không nhắc. |
| `bookingTicketId` | `bookingTicketId` | Optional — liên kết cơ hội/lead. |
| `customerName` | `customerName` | Tên KH (resolved label, để worker/API không cần tra lại nếu chỉ cần hiển thị). |
| `customerPhone` | `customerPhone` | SĐT nếu AI có, có thể `null`. |

Payload KHÔNG có `typeSchedule`/`parentTaskId`/`tags` — nếu `CreateCustomerCareRequest`/
`CreateOrUpdateTaskingRequest` phía CRM có field bắt buộc khác không nằm trong payload trên, worker
tự set default hợp lý (proxy chỉ gửi field trợ lý có đủ ngữ cảnh để điền).

## 4. Vòng đời `Status` — trách nhiệm worker

```
0 Pending ──(worker pick up)──▶ 1 Processing ──(POST CRM OK)──▶ 2 Done
                                        │
                                        └──(POST CRM lỗi)──▶ 3 Failed
```

| Status | Ai set | Khi nào |
|---|---|---|
| `0 Pending` | Proxy (`CrmActionQueueRepository.EnqueueAsync`) | Ngay sau khi user bấm "Xác nhận" trên thẻ. |
| `1 Processing` | Worker | Ngay khi pick 1 dòng để xử lý (tránh worker khác/lần poll sau xử lý trùng). |
| `2 Done` | Worker | Sau khi `POST /api/tasks` hoặc `POST /api/customer-care` trả thành công — ghi kèm `ResultJson` (vd `{"crmTaskId": 789}` hoặc id CSKH trả về) + `ProcessedUtc=SYSUTCDATETIME()`. |
| `3 Failed` | Worker | POST lỗi (4xx/5xx/network) sau khi đã thử — ghi `ErrorMessage` + tăng `RetryCount` + `ProcessedUtc`. |

### Gợi ý luồng worker

1. **Poll**: `SELECT TOP N * FROM dbo.CrmActionQueue WHERE Status=0 ORDER BY CreatedUtc` (dùng
   `IX_CrmActionQueue_Poll`) — oldest trước.
2. **Claim**: `UPDATE ... SET Status=1 WHERE Id=@id AND Status=0` (điều kiện `Status=0` chống 2
   worker instance cùng lấy 1 dòng — giống pattern `dbo.OutboundMails`).
3. **Deserialize** `PayloadJson` theo `Kind` → map đúng DTO như §2/§3.
4. **Resolve bổ sung** (chỉ `assign-task`): `workflowName` → `workflowId` như §2.1.
5. **POST** tới TourKit.Api đúng endpoint, dùng JWT/quyền phù hợp cho `TenantId`+`Username` của
   dòng (worker tự quản lý auth phía app, không dùng session proxy).
6. **Cập nhật kết quả**: thành công → `Status=2` + `ResultJson` + `ProcessedUtc`; lỗi → `Status=3` +
   `ErrorMessage` + `RetryCount += 1` + `ProcessedUtc`.
7. **Retry policy đề xuất**: backoff (vd thử lại sau N phút, tối đa 3 lần) rồi để `Status=3` hẳn
   nếu vẫn lỗi — trang theo dõi (`GET /api/v1/workflows/crm-queue`) sẽ hiện "Failed" cho user biết
   tự xử lý tay.

## 5. Trang theo dõi (đã có sẵn ở proxy)

`GET /api/v1/workflows/crm-queue?kind=&status=&limit=` (require `X-Session-Id`) trả
`{ items: [...] }` — mỗi item là 1 dòng `CrmActionQueue` (camelCase), đặt trong `/workflows` cạnh
"Theo dõi hàng đợi mail" (`outbound-mails`). Proxy chỉ ĐỌC để hiển thị — không có endpoint sửa
`Status` từ phía proxy; mọi cập nhật `Status`/`ResultJson`/`ErrorMessage` đều do worker ghi thẳng
vào DB.

## 6. Không log / không lưu

- KHÔNG log JWT, password.
- `Username` trong bảng là người *yêu cầu* hành động (từ session chat), KHÔNG phải creds worker
  dùng để gọi CRM.
