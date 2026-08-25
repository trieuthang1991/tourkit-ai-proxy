namespace TourkitAiProxy.Services.Http;

/// <summary>
/// Gắn nguồn "ai" + IP người dùng cuối vào MỌI request gửi sang TourKit.Api.
///
/// VÌ SAO CẦN: ai-proxy không nối thẳng DB tenant — mọi thay đổi CRM đều đi qua TourKit.Api
/// (<c>POST /api/ai/tours</c>, <c>/api/ai/providers</c>...). Nhìn từ SQL Server thì app mobile và AI
/// dùng CHUNG tiến trình, CHUNG chuỗi kết nối, CHUNG IP máy chủ — không có cách nào phân biệt.
/// Không có header này thì mọi việc AI làm sẽ bị ghi nhầm thành nguồn "app" trong
/// <c>ActivityLogsNewVersion.source</c>.
///
/// Đặt ở DelegatingHandler (không phải trong TourKitApiClient) để không sót đường gọi nào.
///
/// <c>X-TK-Client-IP</c>: IP người dùng cuối đang chat, KHÔNG phải IP máy chủ ai-proxy —
/// TourKit.Api nhìn thấy ai-proxy chứ không nhìn thấy người dùng.
/// Chạy ở worker (không có HttpContext) thì chỉ gửi nguồn, bỏ IP.
/// </summary>
public sealed class TourKitSourceHandler : DelegatingHandler
{
    public const string SourceHeader = "X-TK-Source";
    public const string ClientIpHeader = "X-TK-Client-IP";
    private const string SourceValue = "ai";

    private readonly IHttpContextAccessor _accessor;

    public TourKitSourceHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            if (!request.Headers.Contains(SourceHeader))
                request.Headers.TryAddWithoutValidation(SourceHeader, SourceValue);

            if (!request.Headers.Contains(ClientIpHeader))
            {
                var ip = ResolveClientIp();
                if (!string.IsNullOrWhiteSpace(ip))
                    request.Headers.TryAddWithoutValidation(ClientIpHeader, ip);
            }
        }
        catch
        {
            // Nuốt lỗi tuyệt đối: thiếu header thì log mất ngữ cảnh, KHÔNG được làm hỏng lệnh gọi API.
        }

        return base.SendAsync(request, ct);
    }

    /// <summary>ai-proxy cũng đứng sau nginx nên ưu tiên phần tử ĐẦU của X-Forwarded-For.</summary>
    private string? ResolveClientIp()
    {
        var ctx = _accessor.HttpContext;
        if (ctx is null) return null;

        var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first)) return first;
        }

        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
