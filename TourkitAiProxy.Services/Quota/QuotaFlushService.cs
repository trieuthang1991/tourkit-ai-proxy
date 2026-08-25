namespace TourkitAiProxy.Services.Quota;

/// <summary>
/// Background driver cho <see cref="TenantQuotaStore.FlushPendingAsync"/>.
///
/// Lý do tồn tại: Consume() trên hot path KHÔNG đụng SQL (chỉ in-mem + tích delta).
/// Service này tick mỗi 5s → 1 UPDATE per tenant với delta tổng. So với sync SQL
/// per AI call: nếu 100 call/giây cùng tenant → trước là 100 UPDATE/s, giờ là
/// 1 UPDATE / 5s = giảm 500× tải SQL row contention.
///
/// Trade-off: crash giữa 2 tick → mất tối đa delta của 5s gần nhất. User đã xác nhận
/// quota "tương đối" là acceptable (không phải billing nghiêm ngặt).
///
/// Shutdown: ExecuteAsync nhận stoppingToken → loop thoát → flush lần cuối với
/// CancellationToken.None (không cancel mid-flush) để không mất delta đã tích.
/// </summary>
public class QuotaFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly TenantQuotaStore _store;
    private readonly ILogger<QuotaFlushService> _log;

    public QuotaFlushService(TenantQuotaStore store, ILogger<QuotaFlushService> log)
    {
        _store = store; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[QuotaFlushService] Khởi động — flush mỗi {Sec}s", FlushInterval.TotalSeconds);

        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await _store.FlushPendingAsync(stoppingToken); }
                catch (Exception ex) { _log.LogWarning(ex, "[QuotaFlushService] Tick flush lỗi"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown bình thường */ }

        // Flush lần cuối khi shutdown — dùng None để không cancel mid-write.
        try
        {
            await _store.FlushPendingAsync(CancellationToken.None);
            _log.LogInformation("[QuotaFlushService] Final flush xong, dừng");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[QuotaFlushService] Final flush lỗi — có thể mất delta cuối");
        }
    }
}
