using Dapper;
using TourkitAiProxy.Services.Db;

namespace TourkitAiProxy.Services.Logging;

/// <summary>
/// Drain <see cref="DbLogQueue"/> nền → batch INSERT vào dbo.AppLogs. SingleReader.
/// TỰ NUỐT LỖI (ghi Console.Error, KHÔNG dùng ILogger) để không đệ quy ghi-log-khi-ghi-log-lỗi.
/// </summary>
public sealed class DbLogWriter : BackgroundService
{
    private const int MaxBatch = 500;
    private const string InsertSql = @"
INSERT INTO dbo.AppLogs (AtUtc, Kind, Level, Category, Message, Exception, DataJson, TenantId, Username, Instance)
VALUES (@AtUtc, @Kind, @Level, @Category, @Message, @Exception, @DataJson, @TenantId, @Username, @Instance);";

    private readonly DbLogQueue _queue;
    private readonly TourkitAiDb _db;

    public DbLogWriter(DbLogQueue queue, TourkitAiDb db) { _queue = queue; _db = db; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        var batch = new List<DbLogEntry>(MaxBatch);
        try
        {
            await foreach (var first in reader.ReadAllAsync(stoppingToken))
            {
                batch.Add(first);
                while (batch.Count < MaxBatch && reader.TryRead(out var more))
                    batch.Add(more);
                await FlushAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }

        // Cố flush nốt phần còn lại khi shutdown (best-effort).
        if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<DbLogEntry> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(InsertSql, batch);   // Dapper chạy INSERT cho từng entry
        }
        catch (Exception ex)
        {
            // KHÔNG dùng ILogger ở đây (sẽ enqueue lại → vòng lặp). Ghi thẳng stderr.
            Console.Error.WriteLine($"[DbLogWriter] flush {batch.Count} dòng lỗi: {ex.Message}");
        }
    }
}
