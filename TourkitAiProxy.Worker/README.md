# TourkitAiProxy.Worker

Chạy scheduler workflow (`WorkflowSchedulerService` tick 60s) độc lập với web
`TourkitAiProxy`. Web restart / crash / IIS AppPool recycle → automation KHÔNG
rớt; ngược lại worker fail → UI + API vẫn sống.

Share TOÀN BỘ `Services/` qua `<ProjectReference>` sang `TourkitAiProxy.csproj`
(không copy code). DI wiring dùng chung extension `AddWorkflowStack()` trong
[`Services/Bootstrap/WorkflowStackRegistration.cs`](../Services/Bootstrap/WorkflowStackRegistration.cs).

## Cấu hình

1. Copy `appsettings.example.json` → `appsettings.json` (file này gitignored).
2. Điền các giá trị PHẢI TRÙNG với web:
   - `ConnectionStrings:PushDb` — share `dbo.UserWorkflows`, `dbo.WorkflowRuns`,
     `dbo.TkSessions`, `dbo.TenantServiceAccounts`, `dbo.MailAccounts`... (ENC:
     Crypton cũng hỗ trợ).
   - `Redis:ConnectionString` — quota + cache cross-process (ENC: cũng được).
   - `Providers:*:ApiKey` + `Models:*:ApiKey` — giống web (khỏi khác account →
     cùng quota + billing).
   - `TourKit:BaseUrl` — upstream API mà `deal-auto-review` /
     `customer-auto-review` gọi (thường `https://mobile-test-api-2.tourkit.vn`).
3. Web `appsettings.json` phải KHÔNG có `"Workflows": {"RunScheduler": true}`
   (mặc định code đã đổi thành `false`). Nếu có → xoá / set `false` để KHÔNG
   double-fire cùng workflow trên 2 process.

## Chạy dev (console)

```bash
dotnet run --project TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj
```

Log mong đợi trong 60s đầu:

```
[Worker] TourkitAiProxy.Worker khởi động — tick 60s
[Scheduler] Khởi động — tick mỗi 60s
[Scheduler] tick — N workflow due
```

`N=0` là bình thường nếu chưa tenant nào bật workflow.

## Deploy — Windows Service (chính)

```powershell
# 1) Publish tự-chứa runtime (khỏi cần cài .NET SDK trên server)
dotnet publish TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj `
    -c Release -r win-x64 --self-contained true `
    -o C:\Services\TourkitAiWorker

# 2) Copy appsettings.json vào cùng thư mục (KHÔNG commit git)
Copy-Item TourkitAiProxy.Worker/appsettings.json C:\Services\TourkitAiWorker\

# 3) Đăng ký Windows Service (chạy dưới NetworkService cho quyền DB)
sc.exe create TourkitAiProxyWorker `
    binPath= "C:\Services\TourkitAiWorker\TourkitAiProxy.Worker.exe" `
    start= auto `
    obj= "NT AUTHORITY\NetworkService" `
    DisplayName= "Tourkit AI Proxy Worker"

# 4) Restart policy — crash → SCM tự restart sau 60s (3 lần / 24h)
sc.exe failure TourkitAiProxyWorker reset= 86400 `
    actions= restart/60000/restart/60000/restart/60000

# 5) Start
sc.exe start TourkitAiProxyWorker
```

Xem log:
- **Event Viewer** → Windows Logs → Application → source `TourkitAiProxyWorker`
- Chi tiết theo thứ tự thời gian: `dbo.AppLogs` (bật `Logging:Database:Enabled=true`)

Uninstall:
```powershell
sc.exe stop TourkitAiProxyWorker
sc.exe delete TourkitAiProxyWorker
```

## Deploy — systemd (Linux, tương lai)

`/etc/systemd/system/tourkit-ai-worker.service`:

```ini
[Unit]
Description=Tourkit AI Proxy Worker
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/tourkit-ai-worker
ExecStart=/usr/bin/dotnet /opt/tourkit-ai-worker/TourkitAiProxy.Worker.dll
Restart=always
RestartSec=60
User=tourkit
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now tourkit-ai-worker.service
```

## Deploy — Docker (tương lai)

Dockerfile riêng cho worker (không cần Node/esbuild vì không có wwwroot):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj \
    -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "TourkitAiProxy.Worker.dll"]
```

Compose:
```yaml
services:
  tourkit-web:
    image: tourkit-ai-proxy
    environment:
      Workflows__RunScheduler: "false"
  tourkit-worker:
    build: { context: ., dockerfile: Dockerfile.worker }
    environment:
      ConnectionStrings__PushDb: "..."
      Redis__ConnectionString: "..."
```

## Health check

Không có HTTP endpoint. Verify liveness bằng 1 trong 3 cách:

1. **Event Viewer** (Windows) hoặc `journalctl -u tourkit-ai-worker` (Linux)
   → thấy log `[Scheduler] tick` mỗi 60s.
2. **SQL**: `SELECT MAX(StartedUtc) FROM dbo.WorkflowRuns` — timestamp update
   mỗi lần có workflow due.
3. **UI web**: trang `/workflows` "20 lần gần nhất" cho từng workflow.

## Troubleshooting

| Triệu chứng | Kiểm tra |
|---|---|
| Service không start | `sc.exe query TourkitAiProxyWorker`; Event Viewer → tìm exception CTOR |
| `[Scheduler] tick — 0 workflow due` mãi | Có workflow nào `Enabled=1 AND PausedReason IS NULL` không? `SELECT * FROM dbo.UserWorkflows` |
| Deal/customer báo "không kết nối được" | `TourKit:BaseUrl` reachable từ server worker chưa? `curl https://mobile-test-api-2.tourkit.vn/healthz` |
| Web + worker double-fire cùng 1 workflow | Web `Workflows:RunScheduler` phải = `false` (đó là default sau khi split). Xoá key khỏi web `appsettings.json` |
| Log DB trống dù bật | `Logging:Database:Enabled=true` VÀ schema `dbo.AppLogs` đã tồn tại (`SELECT * FROM sys.tables WHERE name='AppLogs'`) |
| `Unable to resolve service for type 'IWebHostEnvironment'` | Bug — shim `WorkerWebHostEnvironment` trong `Program.cs` chưa đăng ký. Xem [Program.cs](Program.cs) block "IWebHostEnvironment shim". |

## Ghi chú kiến trúc

- Worker dùng `Sdk="Microsoft.NET.Sdk.Worker"` (generic host, KHÔNG Kestrel/CORS).
  Vì `ProjectReference` sang main (dùng `Sdk.Web`), assembly worker vẫn kéo theo
  `Microsoft.AspNetCore.Http` — footprint chấp nhận được, worker vẫn chỉ chạy
  `BackgroundService`.
- Endpoint `/api/v1/workflows/*` GIỮ NGUYÊN trên web (worker không expose HTTP).
  "Chạy ngay" (`/run-now`) gọi `WorkflowSchedulerService.RunOneAsync` — Singleton
  đã đăng ký ở web nên vẫn chạy được dù web không có hosted tick.
- Khi thêm workflow mới: implement `IScheduledWorkflow` + `AddSingleton` trong
  `WorkflowStackRegistration.cs` (KHÔNG phải `Program.cs`). Worker + web đều tự
  pickup.
