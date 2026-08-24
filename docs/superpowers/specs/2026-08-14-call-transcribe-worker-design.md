# Thiết kế: Worker bóc tách ghi âm cuộc gọi → text (Action `CallTranscribe`)

- **Ngày:** 2026-08-14 · **Trạng thái:** DRAFT v2 (đã chỉnh theo yêu cầu) · **Chờ duyệt**
- **Nơi làm:** `toutkit-app-master/PushNotification.Worker` (thêm 1 `ITaskHandler`), KHÔNG phải tourkit-ai-proxy.
- **Thay đổi nhỏ bên `tourkit` (web):** đẩy cấu hình tổng đài sang bảng job (append-only qua linked server).
- **Mục tiêu:** Tự lấy cuộc gọi TRONG NGÀY của từng site → tải ghi âm → Grok STT → lưu text vào `Tourkit_Push`. Chống bóc trùng, cô lập lỗi, chạy tốt ~1000 site cấu hình khác nhau.

> **Đổi so với v1:** (1) worker chuyển vào `PushNotification.Worker` (app), (2) dùng bảng **`ConfigTask`** copy từ `ScheduledTask` thay cho registry riêng, (3) nhà cung cấp call + engine STT thiết kế **mở rộng** (thêm provider sau khỏi sửa khung).

---

## 1. Tận dụng framework job nền SẴN CÓ của app

`PushNotification.Worker` đã có bộ điều phối job generic — cắm thẳng vào, không dựng mới:

- **Bảng** `ScheduledTask` (7 cột): `Id, TaskName, Action, Data(JSON config+state), NextRun, TenantId, Status(1=active,2=done,3=superseded)`. Append-only: web INSERT dòng mới, worker lấy `MAX(Id)` theo `(TenantId, Action)`, đánh dấu dòng cũ `Status=3`.
- **Worker** [`ScheduledTaskWorker`](../../../../toutkit-app-master/PushNotification.Worker/ScheduledTaskWorker.cs): mỗi poll (60s) dispatch task active theo cột `Action` tới `ITaskHandler` khớp; handler trả `NextRunInMinutes` → worker dời `NextRun`.
- **Handler** [`ITaskHandler`](../../../../toutkit-app-master/PushNotification.Worker/Handlers/ITaskHandler.cs): `Action` + `RunAsync(task, ct) → TaskRunResult(Summary, NextRunInMinutes)`. Mẫu: `AssignBookingTicketHandler`.

→ **Việc cần làm = thêm 1 handler `Action = "CallTranscribe"`.** Config mỗi tenant nằm trong cột `Data` (JSON) như mọi action khác. Worker KHÔNG cần đụng 1000 connection string tenant: mọi thứ (config call + kết quả) đều ở `Tourkit_Push` + gọi HTTP ra ngoài.

---

## 2. Yêu cầu 1: bảng `ConfigTask` (copy `ScheduledTask`, đổi tên cho chuẩn)

Tạo bảng mới **`ConfigTask`** với **đúng toàn bộ cột** của `ScheduledTask`, rồi chuyển code + web sang dùng `ConfigTask`; sau khi ổn định thì DROP `ScheduledTask`.

```sql
-- Database/migration-create-configtask.sql  (chạy trên TourKit_Push)
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='ConfigTask')
CREATE TABLE [dbo].[ConfigTask] (
    [Id]       BIGINT        IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [TaskName] NVARCHAR(255) NOT NULL,          -- 'Bóc tách ghi âm' | 'Phân seller tự động' | …
    [Action]   NVARCHAR(100) NOT NULL,          -- 'CallTranscribe' | 'AssignBookingTicket' | …
    [Data]     NVARCHAR(MAX) NULL,              -- JSON config + state riêng từng Action
    [NextRun]  DATETIME      NOT NULL,
    [TenantId] NVARCHAR(100) NOT NULL,
    [Status]   TINYINT       NOT NULL DEFAULT 1 -- 1=active, 2=done, 3=superseded
);
CREATE NONCLUSTERED INDEX IX_ConfigTask_Due ON dbo.ConfigTask([Status],[NextRun]);
CREATE NONCLUSTERED INDEX IX_ConfigTask_Tenant_Action ON dbo.ConfigTask([TenantId],[Action]);
-- (migrate dữ liệu ScheduledTask → ConfigTask nếu cần, rồi DROP ScheduledTask ở migration sau)
```

**Đổi tên trong code (kèm theo, để dọn ScheduledTask):**
- `PushNotification.Shared/Models/ScheduledTask.cs` → `ConfigTask.cs` (`[Table("ConfigTask")]`), giữ nguyên 7 property.
- `PushDbContext.ScheduledTasks` → `ConfigTasks`.
- `ScheduledTaskWorker` đọc `db.ConfigTasks` (giữ nguyên logic; đổi tên biến/log).
- `AssignBookingTicketHandler` không đổi (dispatch theo `Action`, không phụ thuộc tên bảng).
- **Web**: chỗ INSERT job (linked server) đổi target `ScheduledTask` → `ConfigTask`.
- Sau khi verify chạy ổn → `DROP TABLE ScheduledTask`.

> ConfigTask **CHÍNH LÀ** registry cấu hình call — không cần bảng `CallCenterConfigs` riêng như v1.

---

## 3. Yêu cầu 2: nhà cung cấp call & engine STT — thiết kế MỞ RỘNG

### 3a. Call provider (thêm NCC sau khỏi sửa khung)
Interface + registry, mỗi NCC 1 class:

```csharp
interface ICallProvider {                     // Services/CallCenter/ICallProvider.cs
    string Id { get; }                         // "omicall" | "ccall" | "stringee" | …
    Task<IReadOnlyList<CallItem>> ListTodayAsync(CallProviderConfig cfg, CancellationToken ct);
}
record CallItem(string RecordingId, string? PhoneCustomer, string RecordingUrl, DateTime CallDate);
record CallProviderConfig(string Provider, string ApiKey, string? ApiSecret, string? BaseUrl);
```
- `OmicallCallProvider`, `CcallCallProvider` hiện tại; thêm NCC mới = 1 class + đăng ký DI, **registry tự pickup** (giống pattern `IReviewAgent`/`IAiProvider`).
- **Base URL không hardcode 2 dòng nữa** → catalog trong config, override được per-tenant:
```jsonc
"CallProviders": {
  "omicall": { "BaseUrl": "https://public-v1-stg.omicall.com" },
  "ccall":   { "BaseUrl": "https://api22.ccall.vn" }
  // thêm NCC mới ở đây, hoặc để trống rồi truyền BaseUrl trong Data của tenant
}
```
Thứ tự lấy BaseUrl: `Data.baseUrl` (tenant) → `CallProviders:{id}:BaseUrl` (config) → default hằng số.

### 3b. STT engine (Grok chính, mở rộng engine khác)
```csharp
interface ISttEngine { string Id { get; }
    Task<SttResult> TranscribeAsync(byte[] audio, string fileName, string lang, CancellationToken ct); }
record SttResult(string Text, string Engine);
```
- `GrokSttEngine` — `POST https://api.x.ai/v1/stt`, `Authorization: Bearer {key}`, multipart `format=true&language=vi&file=@...`. Key từ `CallTranscribe:Stt:Grok:ApiKey` (ENC).
- Chọn engine qua `CallTranscribe:Stt:Provider` (default `grok`). Thêm engine sau (Gemini/Whisper/Deepgram) = 1 class + registry; bật fallback qua `CallTranscribe:Stt:Fallback`.
- **DeepSeek KHÔNG dùng giai đoạn 1** (chỉ text). Để dành phase 2 (tóm tắt/trích ý cuộc gọi).

> ⚠️ Verify khi code: shape response thật `/v1/stt` (đoán `{text,...}`), giới hạn dung lượng/định dạng, rate-limit — test 1 call trước khi chốt parser.

---

## 4. Bảng kết quả `dbo.CallTranscripts` (Tourkit_Push)

Đúng yêu cầu (ngày bóc tách, phone, file ghi âm, text bóc tách, lỗi, tenantId) + cột kiểm soát:

```sql
IF OBJECT_ID('dbo.CallTranscripts','U') IS NULL
CREATE TABLE dbo.CallTranscripts (
    Id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    TenantId       NVARCHAR(100)  NOT NULL,
    RecordingId    NVARCHAR(256)  NOT NULL,      -- id cuộc gọi từ NCC (chống bóc 2 lần)
    PhoneCustomer  NVARCHAR(32)   NULL,
    RecordingUrl   NVARCHAR(1024) NULL,          -- link ghi âm
    TranscriptText NVARCHAR(MAX)  NULL,           -- "file bóc tách" (text)
    Status         VARCHAR(12)    NOT NULL DEFAULT 'pending',  -- pending|done|error
    ErrorMessage   NVARCHAR(1024) NULL,           -- lỗi khi bóc tách
    Engine         VARCHAR(24)    NULL,           -- grok|…
    RetryCount     INT            NOT NULL DEFAULT 0,
    CallDate       DATETIME2      NULL,           -- thời điểm gọi (từ NCC)
    TranscribedDate DATETIME2     NULL,           -- ngày bóc tách
    CreatedUtc     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_CallTranscripts UNIQUE (TenantId, RecordingId)
);
```
- **Dedup** = `UNIQUE(TenantId, RecordingId)` → chạy lại / nhiều worker cũng không bóc trùng.
- Text lưu thẳng DB (giai đoạn 1). Cần lưu file vật lý (R2) sau → thêm cột `TranscriptPath`, không phá schema.

---

## 5. Cấu hình call trong `Data` của ConfigTask (mỗi tenant 1 dòng)

Web đẩy sang khi lưu "Cài đặt tổng đài" (append-only INSERT qua linked server `PUSHDB_REMOTE`, y hệt `AssignBookingTicket`):

```json
{ "provider": "omicall",
  "apiKey":  "ENC:...",         // Omicall ApiKey / Ccall api_key
  "apiSecret": "ENC:...",       // Ccall api_secret (Omicall bỏ trống)
  "baseUrl": "",                 // rỗng = dùng default theo provider
  "intervalMinutes": 30 }
```
Nguồn giá trị: web đọc từ `config_company` (`PickCall`→provider, `ApiKey`/`Domain` hoặc `ApikeyCcall`/`ApiSecertCcall`/`DomainCcall`) → serialize vào `Data`, secret mã hoá `Mi.Common.Crypton`. Tắt tính năng → INSERT dòng `Status=3` hoặc không đẩy.

---

## 6. Luồng handler `CallTranscribeHandler.RunAsync(task)`

```
cfg = parse task.Data (provider, apiKey, apiSecret, baseUrl, intervalMinutes)
provider = registry[cfg.Provider]              // Omicall | Ccall | …
calls = provider.ListTodayAsync(cfg)           // auth + list, from/to = HÔM NAY
foreach c in calls where có RecordingUrl:
   if Repo.Exists(task.TenantId, c.RecordingId): continue     // DEDUP
   row = InsertPending(tenant, c.phone, c.url, c.callDate)
   try:
       bytes = Download(c.RecordingUrl) NGAY   // link expiring
       text  = Stt.Transcribe(bytes, "vi")     // Grok (+fallback nếu bật)
       MarkDone(row, text, engine)
   catch e: MarkError(row, e); RetryCount++
return TaskRunResult($"tenant={tenant} new={n} done={d} err={e}", cfg.intervalMinutes)
```
- **Chỉ ngày hiện tại.** Chạy nhiều lần/ngày an toàn nhờ dedup.
- **Retry**: chu kỳ sau quét lại `Status='error' AND RetryCount < MaxRetry` (vd 3).
- Handler ném exception ở tầng tenant → worker backoff 1' (đã có sẵn), KHÔNG chết tenant khác.

---

## 7. Kiểm soát scale (1000 site)

- **Cô lập lỗi**: framework đã try/catch từng task; 1 tenant lỗi không ảnh hưởng tenant khác.
- **Throughput**: 1000 dòng active, worker `BatchSize=50`/poll 60s. → **tăng `BatchSize`** hoặc thêm filter `NextRun <= now` vào truy vấn due (nhỏ, cân nhắc vì AssignBookingTicket muốn chạy mỗi poll — sẽ để CallTranscribe tự `return Wait()` khi chưa tới hạn). Đặt `intervalMinutes` hợp lý (vd 30–60') để giãn tải.
- **Rate-limit STT**: `SemaphoreSlim` giới hạn số call `/v1/stt` đồng thời + backoff 429 → (tuỳ chọn) fallback engine.
- **Idempotent**: dedup ở DB (unique) → restart/deploy/2 instance đều đúng.
- **Secrets**: key Grok trong `PushNotification.Worker/appsettings.json` dạng `ENC:` (gitignore); config call từng tenant mã hoá trong `Data`. Template ở `appsettings.Template.json`.
- **Log** (log4net sẵn có): START, per-tenant new/done/err/skip, FINISH. KHÔNG log key/full SĐT.

---

## 8. Các bước triển khai (khi duyệt)

1. **Rename bảng**: `ConfigTask` (SQL migration) + `ConfigTask.cs` + `PushDbContext.ConfigTasks` + `ScheduledTaskWorker` trỏ `ConfigTasks` + web INSERT đổi target; migrate data; DROP `ScheduledTask` (bước cuối).
2. **Bảng** `CallTranscripts` (migration) + model + repo (`Exists/InsertPending/MarkDone/MarkError`).
3. **Call providers**: `ICallProvider` + `OmicallCallProvider` + `CcallCallProvider` + registry + config catalog base URL.
4. **STT**: `ISttEngine` + `GrokSttEngine` (verify `/v1/stt` bằng 1 call thử).
5. **Handler** `CallTranscribeHandler : ITaskHandler` (Action="CallTranscribe") + đăng ký DI trong `Program.cs`.
6. **Web** (`tourkit`): `SaveSettingCall` đẩy 1 dòng `ConfigTask` (Action="CallTranscribe", Data=config call, secret ENC) qua linked server.
7. Cập nhật doc DB + **CHANGELOG** khi release; build verify từng repo.

---

## 9. Câu hỏi mở / rủi ro

1. **Rename ScheduledTask→ConfigTask đụng AssignBookingTicket + web insert** → làm cẩn thận, migrate data, giữ chạy song song tới khi verify rồi mới DROP. Cần xác nhận không còn nơi nào khác tham chiếu `ScheduledTask`.
2. **Shape `/v1/stt`** + rate-limit + dung lượng/định dạng file → verify bằng call thử.
3. **RecordingId ổn định** — cần id duy nhất/cuộc gọi từ NCC (nếu thiếu → hash `recording_file`).
4. **Backfill 1000 site** — web chỉ đẩy khi lưu MỚI → cần 1 lượt đồng bộ config đang có (hoặc yêu cầu site lưu lại "Cài đặt tổng đài").
5. **Omicall staging→prod**: BaseUrl lấy theo `Domain` tenant / config catalog.
