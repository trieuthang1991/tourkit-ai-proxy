using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Workflows;

// Worker Service host: KHÔNG Kestrel, KHÔNG CORS, KHÔNG endpoint HTTP.
// Chỉ chạy WorkflowSchedulerService (tick 60s) + hỗ trợ deploy Windows Service / systemd.
var builder = Host.CreateApplicationBuilder(args);

// TLS ép TLS 1.2/1.3 — set sớm TRƯỚC khi HttpClient nào được tạo (giống web).
// Windows Server 2012 R2/2016 default TLS 1.0 → upstream AI/TourKit reject.
System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

// Windows Service integration — nếu tiến trình khởi động qua sc.exe, tự nối vào SCM.
// No-op ở dev (chạy console).
builder.Services.AddWindowsService(o => o.ServiceName = "TourkitAiProxyWorker");

// JSON UTC converter — giữ convention với web (mọi DateTime kèm 'Z').
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new TourkitAiProxy.Services.Json.UtcDateTimeConverter()));

// DB logging động (khuyến nghị BẬT cho worker → log gom về dbo.AppLogs, xem chung với web).
var dbLogQueue = new TourkitAiProxy.Services.Logging.DbLogQueue();
builder.Services.AddSingleton(dbLogQueue);
builder.Services.AddSingleton<TourkitAiProxy.Services.Logging.ILogSink,
                              TourkitAiProxy.Services.Logging.DbLogSink>();
if (builder.Configuration.GetValue("Logging:Database:Enabled", false))
{
    var dbLogMin = Enum.TryParse<LogLevel>(
        builder.Configuration["Logging:Database:MinLevel"], out var lv) ? lv : LogLevel.Information;
    builder.Logging.AddProvider(new TourkitAiProxy.Services.Logging.DbLoggerProvider(dbLogQueue, dbLogMin));
    builder.Services.AddHostedService<TourkitAiProxy.Services.Logging.DbLogWriter>();
}

// Share DI stack với web.
builder.Services.AddWorkflowStack(builder.Configuration);

// Đăng ký scheduler làm hosted service — worker LUÔN chạy scheduler.
// Bỏ qua config Workflows:RunScheduler (đó là knob riêng của web).
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowSchedulerService>());

var host = builder.Build();

// Schema init đồng bộ (giống web) — tránh race với TkSessionStore CTOR.
try
{
    await host.Services.GetRequiredService<TourkitAiProxy.Services.Db.TourkitAiDb>().InitAsync();
}
catch (Exception ex)
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogWarning(ex, "[Worker] Schema init lỗi");
}

// Force-resolve AiUsageLog (giống web) → CTOR tự kick off migrate jsonl→SQL.
_ = host.Services.GetRequiredService<TourkitAiProxy.Services.AiUsageLog>();

host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("[Worker] TourkitAiProxy.Worker khởi động — tick {Sec}s",
        builder.Configuration.GetValue("Workflows:TickSeconds", 60));

await host.RunAsync();
