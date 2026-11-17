using System.Collections.Concurrent;

namespace TourkitAiProxy.Services.Mcp;

/// <summary>
/// Tối đa <see cref="PerMinute"/> lời gọi tool mỗi phút cho MỘT khoá (khoá tay hoặc kết nối OAuth), cửa sổ TRƯỢT, trong bộ nhớ.
///
/// <para><b>Đếm lời gọi tool, không đếm request HTTP.</b> SDK cho móc đúng chỗ (<c>AddCallToolFilter</c>),
/// nên <c>initialize</c>/<c>tools/list</c>/ping không ăn vào hạn mức — thứ tốn tài nguyên CRM là
/// <c>tools/call</c>. Vượt thì trả <b>lỗi tool</b> (<c>isError</c>) "Bạn gọi quá nhanh…": AI phía người dùng
/// đọc được câu đó và tự chờ, thay vì một mã HTTP mà nhiều client chỉ hiện là "lỗi kết nối".</para>
///
/// <para><b>Vì sao trong bộ nhớ, chưa Redis.</b> Mục đích là chặn vòng lặp mất kiểm soát của một AI, không
/// phải hạn mức tính tiền. Nhiều instance thì trần thực tế là 60 × số instance — vẫn đủ chặn vòng lặp.
/// Cần chính xác tuyệt đối thì chuyển sang Redis (ChatCache) sau.</para>
/// </summary>
public sealed class McpRateLimiter
{
    public const int PerMinute = 60;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(McpCallerKind Kind, long Id), Queue<DateTimeOffset>> _hits = new();

    public McpRateLimiter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public static string Message(int retryAfterSeconds) => $"Bạn gọi quá nhanh, thử lại sau {retryAfterSeconds} giây";

    /// <summary>
    /// Ngăn đếm theo (loại, Id) của người gọi: khoá tay số 7 và kết nối OAuth số 7 là hai ngăn khác nhau —
    /// hai bảng đánh số độc lập (xem <see cref="McpCallerKind"/>).
    /// </summary>
    public bool TryAcquire(McpCaller caller, out int retryAfterSeconds) => TryAcquire(caller.Kind, caller.TokenId, out retryAfterSeconds);

    /// <summary>Đủ chỗ thì ghi nhận và trả true; hết chỗ thì trả false + số giây phải chờ (≥ 1).</summary>
    public bool TryAcquire(long tokenId, out int retryAfterSeconds) => TryAcquire(McpCallerKind.ManualKey, tokenId, out retryAfterSeconds);

    private bool TryAcquire(McpCallerKind kind, long id, out int retryAfterSeconds)
    {
        var now = _clock.GetUtcNow();
        var q = _hits.GetOrAdd((kind, id), _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && q.Peek() <= now - Window) q.Dequeue();
            if (q.Count >= PerMinute)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((q.Peek() + Window - now).TotalSeconds));
                return false;
            }
            q.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }
}
