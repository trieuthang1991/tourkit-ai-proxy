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

// IWebHostEnvironment shim — nhiều service (AiUsageLog, ReviewRepository, TenantStore,
// TenantQuotaStore, WorkflowTraceLog, CustomerRepository, VisaFileStore, UnresolvedQuestionsLog)
// nhận IWebHostEnvironment chỉ để lấy ContentRootPath cho thư mục `data/`.
// Generic Host KHÔNG có IWebHostEnvironment, nhưng có IHostEnvironment → adapt qua.
builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(sp =>
{
    var host = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
    return new WorkerWebHostEnvironment(host);
});

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

/// <summary>
/// Adapter cho phép Worker (generic host) inject <see cref="Microsoft.AspNetCore.Hosting.IWebHostEnvironment"/>
/// vào các service dùng chung với web. Chỉ ContentRootPath được dùng thực tế —
/// WebRootPath/FileProvider trỏ tạm về ContentRoot (không có wwwroot ở worker).
/// </summary>
internal sealed class WorkerWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment _host;
    public WorkerWebHostEnvironment(Microsoft.Extensions.Hosting.IHostEnvironment host)
    {
        _host = host;
        WebRootPath = host.ContentRootPath;
        WebRootFileProvider = host.ContentRootFileProvider;
    }
    public string EnvironmentName { get => _host.EnvironmentName; set => _host.EnvironmentName = value; }
    public string ApplicationName { get => _host.ApplicationName; set => _host.ApplicationName = value; }
    public string ContentRootPath { get => _host.ContentRootPath; set => _host.ContentRootPath = value; }
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
    {
        get => _host.ContentRootFileProvider;
        set => _host.ContentRootFileProvider = value;
    }
    public string WebRootPath { get; set; }
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
}
