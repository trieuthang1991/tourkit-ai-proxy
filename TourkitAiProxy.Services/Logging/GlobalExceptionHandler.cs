using Microsoft.AspNetCore.Diagnostics;

namespace TourkitAiProxy.Services.Logging;

/// <summary>
/// Handler cuối cùng cho exception KHÔNG được endpoint bắt.
///  • Log ERROR có full stack + method/path + requestId → dễ tra từ file log.
///  • Trả JSON gọn cho client (không leak stack): <c>{ error, detail, requestId }</c>.
///  • KHÔNG double-response nếu status đã ghi 1 phần.
///
/// Wire ở Program.cs: <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c> + <c>UseExceptionHandler()</c>.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _log;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) => _log = log;

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path + ctx.Request.QueryString;
        var reqId = ctx.Items.TryGetValue("RequestId", out var r) ? r?.ToString() : null;

        // BadHttpRequestException MANG SẴN mã của nó (thường 400): thân JSON hỏng, thiếu tham số
        // bắt buộc, thân quá lớn. Ép hết thành 500 là nói sai hai lần cùng lúc — nói với người gọi
        // rằng máy chủ hỏng trong khi lỗi nằm ở yêu cầu của họ, và nhét lỗi của người gọi vào nhật
        // ký lỗi máy chủ nên cảnh báo thật bị chìm. Đã trả giá 09/09/2026: gửi {"userId":null} vào
        // đường giao việc nhận về "Internal server error", không ai đoán được mình gửi sai gì.
        var maLoi = ex is BadHttpRequestException loiGoi
            ? loiGoi.StatusCode
            : StatusCodes.Status500InternalServerError;
        var loiNguoiGoi = maLoi >= 400 && maLoi < 500;

        if (loiNguoiGoi)
            _log.LogWarning("YÊU CẦU HỎNG {Method} {Path} → {Ma}: {Type}: {Msg}",
                method, path, maLoi, ex.GetType().Name, ex.Message);
        else
            _log.LogError(ex, "UNHANDLED {Method} {Path} → {Ma}: {Type}: {Msg}",
                method, path, maLoi, ex.GetType().Name, ex.Message);

        if (ctx.Response.HasStarted) return false;

        ctx.Response.StatusCode = maLoi;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            // Giữ khoá `error` vì giao diện đang đọc đúng khoá này để hiện câu báo.
            error = loiNguoiGoi ? "Yêu cầu không hợp lệ" : "Internal server error",
            detail = ex.Message,
            type = ex.GetType().Name,
            requestId = reqId
        }, ct);
        return true;
    }
}
