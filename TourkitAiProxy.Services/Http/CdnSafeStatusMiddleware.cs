namespace TourkitAiProxy.Services.Http;

/// Đổi 502/504 của ỨNG DỤNG thành 422 trước khi ra khỏi tiến trình.
///
/// Lý do (đo thật trên travelai.vn ngày 06/11/2026): site chạy sau Cloudflare, và Cloudflare
/// THAY LUÔN thân phản hồi khi máy chủ gốc trả 502 hoặc 504 — nó coi đó là lỗi cổng nối nên
/// phục vụ trang lỗi của chính nó. Thân JSON mình viết bị vứt sạch:
///
///   app  → 502 {"error":"Tách bớt trang rồi bóc lại","detail":"…"}
///   user ← 502 text/html 6.390 byte "&lt;!DOCTYPE html&gt;… travelai.vn | 502: Bad gateway"
///
/// Giao diện gọi r.json() trên đống HTML đó chỉ ném «Unexpected token '&lt;', "&lt;!DOCTYPE "…» —
/// đúng lúc cần biết lý do thật thì lại mất sạch. Ở máy dev KHÔNG có Cloudflare nên JSON về
/// nguyên vẹn, thành ra lỗi này chỉ hiện trên bản chạy thật: sửa ở từng endpoint thì không ai
/// nhớ, nên chặn một lần tại đây.
///
/// 500 thì Cloudflare cho qua nguyên văn (đã đo). Chỉ 502/504 bị thay. Chọn 422 vì đúng nghĩa
/// "đã nhận yêu cầu nhưng không xử lý được nội dung" và 4xx thì CDN không đụng tới.
///
/// Status gốc giữ ở header <c>X-Upstream-Status</c> để tra log; thân phản hồi không đổi một byte.
public class CdnSafeStatusMiddleware
{
    /// Thay cho 502/504. 4xx không bị CDN nào dựng trang thay thế.
    public const int SafeStatus = StatusCodes.Status422UnprocessableEntity;

    private readonly RequestDelegate _next;

    public CdnSafeStatusMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        // OnStarting chạy NGAY TRƯỚC lúc đẩy header đi — là chỗ duy nhất còn sửa được status
        // sau khi endpoint đã gán nó, mà vẫn chưa gửi byte nào ra ngoài.
        ctx.Response.OnStarting(static state =>
        {
            var res = ((HttpContext)state).Response;
            if (CanThay(res.StatusCode))
            {
                res.Headers["X-Upstream-Status"] = res.StatusCode.ToString();
                res.StatusCode = SafeStatus;
            }
            return Task.CompletedTask;
        }, ctx);

        await _next(ctx);
    }

    /// Đúng hai status bị Cloudflare dựng trang thay thế. 500/503 cho qua — đừng nới thêm
    /// nếu chưa đo, vì mỗi status đổi đi là một ý nghĩa HTTP bị mất.
    public static bool CanThay(int status) => status is 502 or 504;
}
