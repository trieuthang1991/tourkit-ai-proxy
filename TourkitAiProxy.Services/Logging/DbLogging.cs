using System.Threading.Channels;
using System.Text.Json;

namespace TourkitAiProxy.Services.Logging;

/// <summary>1 dòng log để ghi xuống dbo.AppLogs. Thiết kế ĐỘNG: Kind phân loại + DataJson payload tùy ý.</summary>
public sealed record DbLogEntry(
    DateTime AtUtc,
    string Kind,
    string Level,
    string? Category,
    string? Message,
    string? Exception,
    string? DataJson,
    string? TenantId,
    string? Username,
    string? Instance);

/// <summary>
/// Hàng đợi log in-memory (bounded, non-blocking). Logger/sink chỉ enqueue (không chạm DB) →
/// không bao giờ block request. <see cref="DbLogWriter"/> drain + batch-insert nền.
/// Đầy → DropWrite (bỏ log mới) thay vì block — chấp nhận mất log khi quá tải.
/// </summary>
public sealed class DbLogQueue
{
    // Định danh instance phát log (phân biệt khi nhiều worker/site cùng ghi 1 bảng).
    public static readonly string InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly Channel<DbLogEntry> _ch = Channel.CreateBounded<DbLogEntry>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<DbLogEntry> Reader => _ch.Reader;

    /// Enqueue best-effort, KHÔNG throw, KHÔNG block. Trả false nếu bị drop.
    public bool TryEnqueue(DbLogEntry e) => _ch.Writer.TryWrite(e);
}

/// <summary>
/// Sink log CÓ CẤU TRÚC — điểm mở rộng để ghi loại log BẤT KỲ (kind tự đặt) + payload JSON tùy ý.
/// Vd: _sink.Write("mail-sync", LogLevel.Error, "RST giữa chừng", new { address, lastUid }, tenantId: t).
/// Mọi loại log mới sau này dùng API này → cùng đổ về dbo.AppLogs, query theo Kind.
/// </summary>
public interface ILogSink
{
    void Write(string kind, LogLevel level, string message,
        object? data = null, string? category = null,
        string? tenantId = null, string? username = null, Exception? ex = null);
}

public sealed class DbLogSink : ILogSink
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly DbLogQueue _queue;
    public DbLogSink(DbLogQueue queue) => _queue = queue;

    public void Write(string kind, LogLevel level, string message,
        object? data = null, string? category = null,
        string? tenantId = null, string? username = null, Exception? ex = null)
    {
        string? dataJson = null;
        if (data != null)
        {
            try { dataJson = JsonSerializer.Serialize(data, _json); } catch { /* payload lỗi → bỏ */ }
        }
        _queue.TryEnqueue(new DbLogEntry(
            DateTime.UtcNow, string.IsNullOrWhiteSpace(kind) ? "app" : kind,
            level.ToString(), category, message, ex?.ToString(), dataJson,
            tenantId, username, DbLogQueue.InstanceId));
    }
}

/// <summary>ILoggerProvider cầu nối ILogger chuẩn → dbo.AppLogs (Kind='app'). Lọc theo MinLevel.</summary>
public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly DbLogQueue _queue;
    private readonly LogLevel _min;
    public DbLoggerProvider(DbLogQueue queue, LogLevel min) { _queue = queue; _min = min; }
    public ILogger CreateLogger(string categoryName) => new DbLogger(categoryName, _queue, _min);
    public void Dispose() { }
}

internal sealed class DbLogger : ILogger
{
    private readonly string _category;
    private readonly DbLogQueue _queue;
    private readonly LogLevel _min;
    public DbLogger(string category, DbLogQueue queue, LogLevel min)
    { _category = category; _queue = queue; _min = min; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level)
    {
        if (level < _min || level == LogLevel.None) return false;
        // TRÁNH ĐỆ QUY: không ghi log của chính tầng logging vào DB (writer fail sẽ tự log → vòng lặp).
        return !_category.StartsWith("TourkitAiProxy.Services.Logging", StringComparison.Ordinal);
    }

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        string msg;
        try { msg = formatter(state, ex); } catch { return; }
        _queue.TryEnqueue(new DbLogEntry(
            DateTime.UtcNow, "app", level.ToString(), _category,
            msg, ex?.ToString(), null, null, null, DbLogQueue.InstanceId));
    }
}
