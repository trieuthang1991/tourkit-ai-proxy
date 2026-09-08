using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TourkitAiProxy.Infrastructure.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Gác một NHÓM endpoint theo mã quyền TourKit — đủ MỘT trong các mã là qua.
///
/// <para>Vì sao là bộ lọc nhóm chứ không kiểm trong từng handler: nhóm Visa có 9 đường, nhóm Khách
/// hàng có 10; nhét một dòng kiểm vào từng chỗ thì chỉ cần thêm đường mới mà quên là thủng, và cái
/// thủng đó KHÔNG có triệu chứng nào cả — nó chạy đúng, chỉ là chạy cho người không được phép.
/// Gắn ở nhóm thì đường thêm sau tự được gác (sheet bug dòng 105: đã thủng đúng kiểu này ở Visa và
/// Khách hàng sau khi chỉ vá riêng màn Tính giá tour).</para>
///
/// <para>Phiên không hợp lệ thì ĐỂ NGUYÊN cho handler tự trả 401 — bộ lọc chỉ lo chuyện quyền, không
/// giành việc xác thực, tránh hai chỗ cùng định nghĩa "thế nào là chưa đăng nhập".</para>
/// </summary>
public sealed class RequirePermissionFilter : IEndpointFilter
{
    private readonly string _what;
    private readonly string[] _codes;

    /// <param name="what">Tên tính năng hiển thị trong thông báo lỗi, vd "Visa".</param>
    /// <param name="codes">Các mã quyền chấp nhận (OR).</param>
    public RequirePermissionFilter(string what, params string[] codes)
    {
        _what = what;
        _codes = codes;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var sessions = http.RequestServices.GetRequiredService<TkSessionStore>();

        var sid = http.Request.Headers["X-Session-Id"].FirstOrDefault()
                  ?? http.Request.Query["sessionId"].FirstOrDefault();
        if (string.IsNullOrEmpty(sid) || sessions.Get(sid) == null)
            return await next(ctx);   // chưa đăng nhập → handler trả 401 như cũ

        await sessions.EnsurePermissionsAsync(sid, http.RequestAborted);
        foreach (var code in _codes)
            if (sessions.HasPermission(sid, code))
                return await next(ctx);

        return Results.Json(
            new { error = $"Bạn không có quyền {_what} ({string.Join(" / ", _codes)})." },
            statusCode: 403);
    }
}
